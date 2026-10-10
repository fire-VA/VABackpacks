// CampTerrain — direct terrain manipulation + undo, ported from
// WorldEditCommands' Terrain.cs (Jere Kuusela). The same pattern that
// the `terrain` and `terrain_reset` server-dev-commands use.
//
// COEXISTENCE WITH WORLDEDITCOMMANDS:
// Both this file and WEC operate on the same vanilla TerrainComp arrays
// (m_levelDelta, m_smoothDelta, m_modifiedHeight, m_paintMask,
// m_modifiedPaint). They CAN coexist because:
//
//   1. Different namespaces: CampTerrain lives in
//      VerdantsAscent.Managers.Camp; WEC's helpers are in
//      WorldEditCommands. No type-name collisions at compile time.
//   2. No Harmony patches on terrain methods. Neither side patches
//      Heightmap / TerrainComp / TerrainModifier — both just call public
//      vanilla APIs and write to public arrays. No patch order to fight.
//   3. Both use `compiler.transform.position` as the per-compiler key
//      (matches WEC's GetData line 231) so a snapshot taken by one
//      tool indexes against the same position the other expects.
//   4. ClutterSystem.ResetGrass is idempotent.
//      compiler.m_operations++ is idempotent.
//      compiler.Save() + Heightmap.Poke(false) are idempotent.
//      Both sides call all three.
//
// Independence guarantees we maintain:
//   * We never spawn a persistent TerrainModifier piece from this file.
//     The legacy mud_road path (still configurable) does, but the
//     direct-ops path here writes purely to the compiler arrays so
//     there's no rogue ZDO that could survive a pack-up failure.
//   * Our snapshot serialization is namespaced (ZPackage layout
//     specific to this file) and stored on the camp chest's ZDO under
//     `va_camp_terrain_snap`. WEC stores nothing on ZDOs.
//   * If a user runs WEC's `terrain_reset` over a deployed camp, our
//     terrain mods are wiped — but the camp pieces stay, and pack-up
//     still restores from our snapshot (returning the heightmap to
//     pre-deploy, including any prior non-camp modifications).
//
// PATTERN:
//
//   1. Build a list of TerrainComps that overlap the target circle.
//   2. From those, build a list of (compiler, vertex-index) "nodes"
//      that fall inside the circle.
//   3. Snapshot the existing m_levelDelta / m_smoothDelta /
//      m_modifiedHeight / m_paintMask / m_modifiedPaint values for each
//      node BEFORE we touch them — this snapshot IS the undo data.
//   4. Apply the operation by writing directly to those compiler arrays
//      (no TerrainModifier piece spawn, no rogue ZDOs to destroy later).
//   5. On every TerrainComp touched: claim ZNetView ownership, bump
//      m_operations, call Save(), and Poke the heightmap to rebuild.
//   6. ClutterSystem.ResetGrass over the radius so grass blades match
//      the new terrain state.
//
// The snapshot we build at step 3 is what we round-trip through the
// camp marker's ZDO so pack-up restores the exact pre-deploy state.
//
// Reference: WorldEditCommands/Terrain.cs (LevelTerrain / PaintTerrain
// / GetData / ApplyData / Save). We don't depend on WEC at runtime —
// this is a pure port of the relevant pieces.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    public static class CampTerrain
    {
        // We link against a publicized assembly_valheim (libs/assembly_valheim_publicized.dll),
        // which exposes TerrainComp.m_levelDelta / m_smoothDelta / m_modifiedHeight /
        // m_paintMask / m_modifiedPaint / m_width / m_hmap / m_operations / m_lastOpPoint
        // / m_lastOpRadius and TerrainComp.Save() and Heightmap.m_heights as public.
        // Same approach the rest of the modpack (FiresNPCs, BalrondArsenalReborn) uses.

        // ── Per-vertex snapshot records (mirror WEC) ──────────────────

        public class HeightUndo
        {
            public int   Index;
            public bool  HeightModified;
            public float Level;
            public float Smooth;
        }

        public class PaintUndo
        {
            public int   Index;
            public bool  PaintModified;
            public Color Paint;
        }

        public class CompUndo
        {
            public List<HeightUndo> Heights = new();
            public List<PaintUndo>  Paints  = new();
        }

        // Keyed by TerrainComp.transform.position. Same scheme WEC uses
        // so multiple compiler tiles overlapping our radius each get
        // their own bucket.
        public class TerrainSnapshot
        {
            public Dictionary<Vector3, CompUndo> Comps = new();
            public Vector3 Center;
            public float   Radius;
        }

        // ── Compiler / node enumeration ───────────────────────────────

        /// <summary>
        /// Find every TerrainComp whose tile overlaps a sphere — plus
        /// every immediate-neighbor compiler within one heightmap tile,
        /// so seam vertices on a zone boundary are always patched in
        /// BOTH compilers that own a copy of them.
        ///
        /// Why the wide radius: a zone seam vertex is duplicated in the
        /// vertex arrays of the two TerrainComps it borders. If we only
        /// modify one compiler's copy, the neighbor's mesh keeps the
        /// original height at that vertex → visible mesh tear at the
        /// seam. By including neighbor compilers in `comps`, the existing
        /// CollectHeightNodes / falloff math runs for both, modifies the
        /// shared seam vertex consistently, and the mesh stays continuous.
        ///
        /// Vertices in the neighbor compiler that are outside the actual
        /// patch radius are filtered out by CollectHeightNodes' distance
        /// check, so no extra terrain gets disturbed.
        /// </summary>
        public static TerrainComp[] GetCompilers(Vector3 pos, float radius)
        {
            var hms = new List<Heightmap>();
            // Heightmap tile width is 64. We search a 3×3 zone area
            // around the patch — that's the maximum extent any reasonable
            // patch radius could touch. CollectHeightNodes filters
            // per-vertex by actual radius so non-overlapping vertices in
            // the captured neighbor compilers are skipped.
            //
            // Why this wide: a seam vertex on a zone boundary is owned by
            // BOTH adjacent compilers. If only one gets the modification,
            // the other's mesh stays at the original height → visible
            // mesh tear at the seam (worst when a blueprint piece spans
            // the boundary). Capturing 3×3 zones guarantees every neighbor
            // sharing a seam with the patch is included.
            const float HEIGHTMAP_TILE_SIZE = 64f;
            float searchRadius = radius + HEIGHTMAP_TILE_SIZE * 1.5f;
            Heightmap.FindHeightmap(pos, searchRadius, hms);

            var result = new List<TerrainComp>(hms.Count);
            foreach (var hmap in hms)
            {
                if (hmap == null) continue;
                // NOTE: we deliberately do NOT filter by ZNetScene.InActiveArea
                // here. FindHeightmap already only returns currently-loaded
                // heightmaps — if Unity has the GameObject, we can write to
                // its compiler. The active-area gate was over-restrictive
                // and could exclude a neighbor zone that's loaded but just
                // outside a peer's tight active radius, leaving its seam
                // vertices unmodified → mesh tear at the boundary.
                var c = hmap.GetAndCreateTerrainCompiler();
                if (c != null) result.Add(c);
            }

            // One-line diagnostic so the next test confirms how many
            // compilers actually got modified per op. If this prints "1"
            // when the patch is on a zone seam, the seam-tear regressed.
            if (result.Count > 0)
            {
                Debug.Log($"[VABackpacks] CampTerrain.GetCompilers: pos={pos:F1} r={radius:F1} " +
                          $"→ {result.Count} compilers (search={searchRadius:F0}m)");
            }
            return result.ToArray();
        }

        /// <summary>
        /// After a height/paint operation that may have affected a zone
        /// boundary, force every nearby active heightmap to rebuild its
        /// mesh. Belt-and-suspenders to GetCompilers' wide radius — Poke
        /// flushes any cached mesh data on neighbors that didn't have a
        /// vertex actually modified but whose mesh might still display
        /// stale values.
        /// </summary>
        public static void RefreshNeighborHeightmaps(Vector3 pos, float radius)
        {
            var hms = new List<Heightmap>();
            const float HEIGHTMAP_TILE_SIZE = 64f;
            // Match GetCompilers' search radius so any compiler we *might*
            // have written to also gets its mesh poked.
            Heightmap.FindHeightmap(pos, radius + HEIGHTMAP_TILE_SIZE * 1.5f, hms);
            int poked = 0;
            foreach (var hmap in hms)
            {
                if (hmap == null) continue;
                // No InActiveArea filter — same reasoning as GetCompilers.
                try { hmap.Poke(); poked++; }
                catch { /* defensive — Poke is well-behaved but this runs on a hot path */ }
            }
            if (poked > 0)
            {
                Debug.Log($"[VABackpacks] RefreshNeighborHeightmaps: poked {poked} heightmaps near {pos:F1} r={radius:F1}");
            }
        }

        /// <summary>Collect every height vertex of a compiler that lies
        /// inside <paramref name="radius"/> of <paramref name="centerPos"/>.</summary>
        public static void CollectHeightNodes(List<HeightNode> nodes, TerrainComp compiler,
                                              Vector3 centerPos, float radius)
        {
            int n = compiler.m_width + 1;
            var hmap = compiler.m_hmap;
            for (int x = 0; x < n; x++)
            {
                for (int z = 0; z < n; z++)
                {
                    Vector3 world = VertexToWorld(hmap, x, z);
                    float dx = world.x - centerPos.x;
                    float dz = world.z - centerPos.z;
                    float dist = Mathf.Sqrt(dx * dx + dz * dz);
                    if (dist > radius) continue;
                    nodes.Add(new HeightNode
                    {
                        Compiler = compiler,
                        Index    = z * n + x,
                        Distance = radius > 0f ? dist / radius : 0f
                    });
                }
            }
        }

        public static void CollectPaintNodes(List<PaintNode> nodes, TerrainComp compiler,
                                             Vector3 centerPos, float radius)
        {
            int n = compiler.m_width + 1;
            var hmap = compiler.m_hmap;
            for (int x = 0; x < n; x++)
            {
                for (int z = 0; z < n; z++)
                {
                    Vector3 world = VertexToWorld(hmap, x, z);
                    float dx = world.x - centerPos.x;
                    float dz = world.z - centerPos.z;
                    float dist = Mathf.Sqrt(dx * dx + dz * dz);
                    if (dist > radius) continue;
                    nodes.Add(new PaintNode
                    {
                        Compiler = compiler,
                        Index    = z * n + x,
                        Distance = radius > 0f ? dist / radius : 0f
                    });
                }
            }
        }

        private static Vector3 VertexToWorld(Heightmap hmap, int x, int z)
        {
            Vector3 p = hmap.transform.position;
            p.x += (x - hmap.m_width / 2) * hmap.m_scale;
            p.z += (z - hmap.m_width / 2) * hmap.m_scale;
            return p;
        }

        public class HeightNode { public TerrainComp Compiler; public int Index; public float Distance; }
        public class PaintNode  { public TerrainComp Compiler; public int Index; public float Distance; }

        // ── Snapshot (the undo data) ──────────────────────────────────

        public static TerrainSnapshot Snapshot(Vector3 pos, float radius)
        {
            var snap = new TerrainSnapshot { Center = pos, Radius = radius };
            var comps = GetCompilers(pos, radius);
            foreach (var c in comps)
            {
                var hb = new List<HeightNode>();
                var pb = new List<PaintNode>();
                CollectHeightNodes(hb, c, pos, radius);
                CollectPaintNodes(pb, c, pos, radius);

                var bucket = new CompUndo();
                foreach (var h in hb)
                {
                    bucket.Heights.Add(new HeightUndo
                    {
                        Index          = h.Index,
                        HeightModified = c.m_modifiedHeight[h.Index],
                        Level          = c.m_levelDelta[h.Index],
                        Smooth         = c.m_smoothDelta[h.Index],
                    });
                }
                foreach (var p in pb)
                {
                    bucket.Paints.Add(new PaintUndo
                    {
                        Index         = p.Index,
                        PaintModified = c.m_modifiedPaint[p.Index],
                        Paint         = c.m_paintMask[p.Index],
                    });
                }
                snap.Comps[c.transform.position] = bucket;
            }
            return snap;
        }

        // ── Operations ────────────────────────────────────────────────
        // Mirror WEC's LevelTerrain + PaintTerrain. They write directly
        // to the compiler's arrays rather than spawning a TerrainModifier.

        /// <summary>Set every node in radius to a target altitude with a
        /// smoothing falloff toward the edge.</summary>
        public static void Level(Vector3 pos, float radius, float smooth, float altitude)
        {
            var comps = GetCompilers(pos, radius);
            var nodes = new List<HeightNode>();
            foreach (var c in comps) CollectHeightNodes(nodes, c, pos, radius);

            foreach (var node in nodes)
            {
                var c = node.Compiler;
                float falloff = SmoothFalloff(smooth, node.Distance);
                c.m_levelDelta[node.Index] += falloff * (altitude - c.m_hmap.m_heights[node.Index]);
                c.m_smoothDelta[node.Index] = 0f;
                c.m_modifiedHeight[node.Index] = c.m_levelDelta[node.Index] != 0f;
            }

            foreach (var c in comps) Save(c);
            ClutterSystem.instance?.ResetGrass(pos, radius);
        }

        /// <summary>Paint the radius with a target color, smoothing toward the edge.</summary>
        public static void Paint(Vector3 pos, float radius, float smooth, Color color)
        {
            var comps = GetCompilers(pos, radius);
            var nodes = new List<PaintNode>();
            foreach (var c in comps) CollectPaintNodes(nodes, c, pos, radius);

            foreach (var node in nodes)
            {
                var c = node.Compiler;
                float falloff = SmoothFalloff(smooth, node.Distance);
                Color blended = Color.Lerp(c.m_paintMask[node.Index], color, falloff);
                blended.a = color.a;
                c.m_paintMask[node.Index] = blended;
                c.m_modifiedPaint[node.Index] = true;
            }

            foreach (var c in comps) Save(c);
            ClutterSystem.instance?.ResetGrass(pos, radius);
        }

        /// <summary>Restore the heightmap + paint mask to the snapshot's
        /// state at every node it covers. Mirrors WEC.ApplyData.</summary>
        public static void Restore(TerrainSnapshot snap)
        {
            if (snap == null || snap.Comps.Count == 0) return;
            var touched = new HashSet<TerrainComp>();
            foreach (var kv in snap.Comps)
            {
                var c = TerrainComp.FindTerrainCompiler(kv.Key);
                if (c == null) continue;
                touched.Add(c);
                foreach (var h in kv.Value.Heights)
                {
                    if (h.Index < 0 || h.Index >= c.m_levelDelta.Length) continue;
                    c.m_levelDelta[h.Index]     = h.Level;
                    c.m_smoothDelta[h.Index]    = h.Smooth;
                    c.m_modifiedHeight[h.Index] = h.HeightModified;
                }
                foreach (var p in kv.Value.Paints)
                {
                    if (p.Index < 0 || p.Index >= c.m_paintMask.Length) continue;
                    c.m_paintMask[p.Index]     = p.Paint;
                    c.m_modifiedPaint[p.Index] = p.PaintModified;
                }
            }
            foreach (var c in touched) Save(c);
            ClutterSystem.instance?.ResetGrass(snap.Center, snap.Radius);
        }

        /// <summary>Claim ownership and persist a TerrainComp's state.</summary>
        public static void Save(TerrainComp compiler)
        {
            if (compiler == null) return;
            compiler.GetComponent<ZNetView>()?.ClaimOwnership();
            compiler.m_operations++;
            compiler.m_lastOpPoint  = Vector3.zero;
            compiler.m_lastOpRadius = 0f;
            compiler.Save();
            compiler.m_hmap?.Poke();
        }

        private static float SmoothFalloff(float smooth, float dist)
        {
            // Matches WEC.CalculateSmooth: full effect inside (1-smooth)*r,
            // linear falloff to zero at the edge.
            return 1f - dist < smooth ? (1f - dist) / smooth : 1f;
        }

        // ── Snapshot serialization (for ZDO storage) ──────────────────
        // ZPackage layout:
        //   int  compsCount
        //   for each comp:
        //     float kx, ky, kz                         (compiler position key)
        //     int   heightsCount
        //     for each:
        //       int   index
        //       byte  modified (0/1)
        //       float level
        //       float smooth
        //     int   paintsCount
        //     for each:
        //       int   index
        //       byte  modified (0/1)
        //       float r, g, b, a
        //   float centerX, centerY, centerZ
        //   float radius

        public static string Serialize(TerrainSnapshot snap)
        {
            if (snap == null) return string.Empty;
            var pkg = new ZPackage();
            pkg.Write(snap.Comps.Count);
            foreach (var kv in snap.Comps)
            {
                pkg.Write(kv.Key.x); pkg.Write(kv.Key.y); pkg.Write(kv.Key.z);
                pkg.Write(kv.Value.Heights.Count);
                foreach (var h in kv.Value.Heights)
                {
                    pkg.Write(h.Index);
                    pkg.Write((byte)(h.HeightModified ? 1 : 0));
                    pkg.Write(h.Level);
                    pkg.Write(h.Smooth);
                }
                pkg.Write(kv.Value.Paints.Count);
                foreach (var p in kv.Value.Paints)
                {
                    pkg.Write(p.Index);
                    pkg.Write((byte)(p.PaintModified ? 1 : 0));
                    pkg.Write(p.Paint.r);
                    pkg.Write(p.Paint.g);
                    pkg.Write(p.Paint.b);
                    pkg.Write(p.Paint.a);
                }
            }
            pkg.Write(snap.Center.x); pkg.Write(snap.Center.y); pkg.Write(snap.Center.z);
            pkg.Write(snap.Radius);
            return pkg.GetBase64();
        }

        public static TerrainSnapshot Deserialize(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return null;
            try
            {
                var pkg = new ZPackage(base64);
                var snap = new TerrainSnapshot();
                int comps = pkg.ReadInt();
                for (int i = 0; i < comps; i++)
                {
                    var key = new Vector3(pkg.ReadSingle(), pkg.ReadSingle(), pkg.ReadSingle());
                    var bucket = new CompUndo();
                    int hc = pkg.ReadInt();
                    for (int j = 0; j < hc; j++)
                    {
                        bucket.Heights.Add(new HeightUndo
                        {
                            Index          = pkg.ReadInt(),
                            HeightModified = pkg.ReadByte() != 0,
                            Level          = pkg.ReadSingle(),
                            Smooth         = pkg.ReadSingle(),
                        });
                    }
                    int pc = pkg.ReadInt();
                    for (int j = 0; j < pc; j++)
                    {
                        bucket.Paints.Add(new PaintUndo
                        {
                            Index         = pkg.ReadInt(),
                            PaintModified = pkg.ReadByte() != 0,
                            Paint         = new Color(pkg.ReadSingle(), pkg.ReadSingle(),
                                                       pkg.ReadSingle(), pkg.ReadSingle()),
                        });
                    }
                    snap.Comps[key] = bucket;
                }
                snap.Center = new Vector3(pkg.ReadSingle(), pkg.ReadSingle(), pkg.ReadSingle());
                snap.Radius = pkg.ReadSingle();
                return snap;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] Failed to deserialize terrain snapshot: {ex.Message}");
                return null;
            }
        }
    }
}
