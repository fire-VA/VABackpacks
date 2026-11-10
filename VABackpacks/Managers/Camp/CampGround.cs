using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    /// <summary>
    /// Camp deployment on already edited terrain.
    /// Older versions snapped every piece with ZoneSystem.GetGroundHeight, the generated heightmap, which never sees a voxel edit (FAT
    /// levels, the carried player ground, a dug edge). Saved-layout pieces weren't snapped at all (centre y + the saved offset). Then
    /// each piece's pad levelled the ground to that wrong height, which raised lumps. Nothing kept the pieces apart
    /// either (the chest sits at the centre, so a saved woodpile could sit in it).
    /// Always on: every procedural piece is sampled on the real ground (FAT's
    /// FatGroundProbe.VoxelSurfaceY, the topmost voxel floor; without FAT a ray onto terrain + pieces, never GetGroundHeight), kept clear of the pieces placed before it
    /// (footprint circles from the prefab's colliders), and moved when the ground under it spreads too much; a piece with no good spot
    /// within reach is left out with a message. One line per piece: prefab, sampled y, source, spread, nudge.
    /// </summary>
    internal static class CampGround
    {
        // GroundTruePlacement is gone; the placement always runs
        internal static ConfigEntry<float> MaxSpread;
        internal static bool On => true;

        private const float Gap = 0.25f;           // clearance between two footprints
        private const float Step = 0.5f;           // nudge step
        private const float MaxNudge = 4f;         // furthest a piece moves from its spot
        private const float Reach = 6f;            // voxel window round the hint

        internal static void Bind(ConfigFile cfg)
        {
            MaxSpread = cfg.Bind("Backpacks.Camp", "GroundTrueMaxSpread", 0.6f,
                "The most the ground may rise or fall under one camp piece (metres) before the piece is moved.");
        }

        // ── ground ──
        private static bool _looked;
        private static MethodInfo _voxelY;
        private static int _groundMask;

        private static void Look()
        {
            if (_looked) return;
            _looked = true;
            var t = Type.GetType("VerdantsAscent.Terrain.Voxel.FatGroundProbe, FiresAdminTerrain", false);
            if (t == null) foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { t = a.GetType("VerdantsAscent.Terrain.Voxel.FatGroundProbe", false); if (t != null) break; }
            _voxelY = t?.GetMethod("VoxelSurfaceY", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(float), typeof(float), typeof(float), typeof(float), typeof(float).MakeByRefType() }, null);
            VabLog.Line($"[VABackpacks] camp ground: {(_voxelY != null ? "FAT FatGroundProbe.VoxelSurfaceY (voxel edits seen)" : "no FAT probe: a ray onto the drawn ground (terrain + pieces)")}");
        }

        /// <summary>The ground under (x, z) near `hint` y. Source "voxel" / "raycast &lt;layer&gt;" / "none".</summary>
        internal static bool Sample(float x, float z, float hint, out float y, out string source)
        {
            Look();
            if (_groundMask == 0) _groundMask = LayerMask.GetMask("terrain", "Default", "static_solid", "piece");
            // 1.0.129: in the halo-ring frame a ray along the ring's down (FAT's voxel probe and the heightmap read world y)
            if (CampFrame.Active)
            {
                if (CampFrame.RayDown(x, z, hint + Reach, Reach * 2f, _groundMask, out y, out var col)) { source = "ring ray " + LayerMask.LayerToName(col.gameObject.layer); return true; }
                y = hint; source = "none";
                return false;
            }
            if (_voxelY != null)
            {
                try
                {
                    var args = new object[] { x, z, hint, Reach, 0f };
                    if (_voxelY.Invoke(null, args) is bool ok && ok) { y = (float)args[4]; source = "voxel"; return true; }
                }
                catch { }
            }
            // no FAT : a ray down onto the drawn ground (terrain + building pieces), never GetGroundHeight (it ignores edits)
            if (_groundMask == 0) _groundMask = LayerMask.GetMask("terrain", "Default", "static_solid", "piece");
            if (Physics.Raycast(new Vector3(x, hint + Reach, z), Vector3.down, out RaycastHit hit, Reach * 2f, _groundMask, QueryTriggerInteraction.Ignore))
            { y = hit.point.y; source = "raycast " + LayerMask.LayerToName(hit.collider.gameObject.layer); return true; }
            y = hint; source = "none";
            return false;
        }

        /// <summary>The XZ radius of a prefab's footprint (its solid colliders, around its own origin). Min 0.4 m.</summary>
        internal static float FootprintRadius(GameObject prefab)
        {
            if (prefab == null) return 0.5f;
            // Collider.bounds is EMPTY
            // on an inactive prefab, so every piece fell to the minimum. The shapes themselves are read instead (box size/centre,
            // sphere, capsule, mesh bounds), through each collider's own transform, in the prefab root's space (rotation + scale).
            float r = 0.4f;
            Transform root = prefab.transform;
            foreach (var c in prefab.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || c.isTrigger) continue;
                Vector3 centre, half;
                switch (c)
                {
                    case BoxCollider b: centre = b.center; half = b.size * 0.5f; break;
                    case SphereCollider s: centre = s.center; half = Vector3.one * s.radius; break;
                    case CapsuleCollider cap:
                        centre = cap.center;
                        half = Vector3.one * cap.radius;
                        half[cap.direction] = Mathf.Max(cap.radius, cap.height * 0.5f);
                        break;
                    case MeshCollider m when m.sharedMesh != null: centre = m.sharedMesh.bounds.center; half = m.sharedMesh.bounds.extents; break;
                    default: continue;
                }
                for (int i = 0; i < 8; i++)
                {
                    var corner = centre + new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z);
                    Vector3 local = root.InverseTransformPoint(c.transform.TransformPoint(corner));
                    r = Mathf.Max(r, new Vector2(local.x, local.z).magnitude * 0.85f);   // a box corner overstates a round footprint
                }
            }
            return Mathf.Min(r, 3f);
        }

        internal sealed class Placed { internal string Role; internal Vector3 Pos; internal float Radius; }

        /// <summary>
        /// Puts `pos` on the ground, clear of `placed`, on ground flat enough. `pinned` = keep x/z (the chest is the camp centre).
        /// False = no good spot within MaxNudge: the caller leaves the piece out. Adds the piece to `placed` on success.
        /// </summary>
        internal static bool Place(string role, GameObject prefab, ref Vector3 pos, Vector3 campCenter, List<Placed> placed, bool pinned, out string line)
        {
            float r = FootprintRadius(prefab);
            Vector3 want = pos;
            Vector3 away = new Vector3(want.x - campCenter.x, 0f, want.z - campCenter.z);
            if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
            away.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, away);
            string why = null;
            // candidates: the spot itself, then rings of nudges (out, round each side, in), nearest first
            var tries = new List<Vector3> { want };
            if (!pinned)
                for (float d = Step; d <= MaxNudge + 0.01f; d += Step)
                    foreach (var dir in new[] { away, side, -side, (away + side).normalized, (away - side).normalized, -away })
                        tries.Add(want + dir * d);
            foreach (var at in tries)
            {
                if (!Clear(at, r, placed, out string hit)) { why = why ?? $"overlaps {hit}"; continue; }
                // The probe's window centres on the generated ground at this x/z (only a HINT: the voxel read
                // inside +-6 m is the answer, so edits still win); a saved spot's centre y + offset can be more than 6 m off on a steep site
                float hint = want.y; string hintSrc = "spot y";
                if (!CampFrame.Active && ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(new Vector3(at.x, want.y, at.z), out float gh)) { hint = gh; hintSrc = "generated ground"; }
                if (!Spread(at, r, hint, out float y, out float spread, out string src)) { why = why ?? $"no ground read (hint {hint:0.00} from {hintSrc})"; continue; }
                src += $", hint {hintSrc} {hint:0.00}";
                if (spread > MaxSpread.Value && !pinned) { why = why ?? $"ground spread {spread:0.00} m"; continue; }
                float nudge = Vector3.Distance(new Vector3(at.x, 0f, at.z), new Vector3(want.x, 0f, want.z));
                pos = new Vector3(at.x, y, at.z);
                placed.Add(new Placed { Role = role, Pos = pos, Radius = r });
                line = $"[VABackpacks] camp ground: {role} {prefab?.name} at ({pos.x:0.0},{pos.z:0.0}) y {y:0.00} ({src}; was {want.y:0.00}), footprint {r:0.0} m, spread {spread:0.00} m, "
                     + (nudge > 0.01f ? $"nudged {nudge:0.0} m ({why})" : "no nudge");
                return true;
            }
            line = $"[VABackpacks] camp ground: {role} {prefab?.name} LEFT OUT: no spot within {MaxNudge:0} m of ({want.x:0.0},{want.z:0.0}) is clear and level ({why ?? "?"})";
            return false;
        }

        private static bool Clear(Vector3 at, float r, List<Placed> placed, out string hit)
        {
            foreach (var p in placed)
            {
                float d = Vector2.Distance(new Vector2(at.x, at.z), new Vector2(p.Pos.x, p.Pos.z));
                if (d < r + p.Radius + Gap) { hit = $"{p.Role} ({d:0.0} m, needs {r + p.Radius + Gap:0.0})"; return false; }
            }
            hit = null;
            return true;
        }

        // The ground at the centre and 8 points round the footprint edge: y = the median (flush on a slope, never floating on a
        // lip), spread = max - min.
        private static bool Spread(Vector3 at, float r, float hint, out float y, out float spread, out string src)
        {
            var ys = new List<float>(9);
            src = "none";
            for (int i = -1; i < 8; i++)
            {
                Vector3 p = i < 0 ? at : at + Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward * (r * 0.9f);
                if (Sample(p.x, p.z, hint, out float gy, out string s)) { ys.Add(gy); if (i < 0) src = s; }
            }
            if (ys.Count < 5) { y = hint; spread = 0f; return false; }
            ys.Sort();
            y = ys[ys.Count / 2];
            spread = ys[ys.Count - 1] - ys[0];
            return true;
        }
    }
}
