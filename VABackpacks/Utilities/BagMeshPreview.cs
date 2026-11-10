using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    /// <summary>
    /// 1.0.135 ([lead] 10-28, the Explorer Bag decimation review): a CHEAT-only console command that shows a decimated mesh on the
    /// local player's worn bag WITHOUT touching the bag's bundle, so one shot round can show the current bag and a candidate side by
    /// side. `vab_preview_mesh &lt;obj path&gt;` loads a Wavefront OBJ (positions, UVs, normals; triangles or quads) and puts it on every
    /// worn-bag renderer that currently draws the heaviest mesh; `vab_preview_mesh off` puts the original back. Nothing is saved: a
    /// relog or re-equip shows the real bag again. Every step logs at Debug with the vertex / triangle counts.
    /// </summary>
    [HarmonyPatch]
    internal static class BagMeshPreview
    {
        private static readonly BepInEx.Logging.ManualLogSource s_log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks.MeshPreview");
        private static readonly Dictionary<MeshFilter, Mesh> s_original = new Dictionary<MeshFilter, Mesh>();

        [HarmonyPatch(typeof(Terminal), "InitTerminal"), HarmonyPostfix]
        private static void Commands()
        {
            new Terminal.ConsoleCommand("vab_preview_mesh", "<obj path> | off - show an OBJ on your worn bag in place of its heaviest mesh (review only, not saved)", args =>
            {
                try
                {
                    if (args.Length < 2) { Say(args, "usage: vab_preview_mesh <obj path> | off"); return; }
                    string a = args.FullLine.Substring(args.FullLine.IndexOf(' ') + 1).Trim().Trim('"');
                    if (a.Equals("off", StringComparison.OrdinalIgnoreCase)) { Restore(args); return; }
                    Apply(args, a);
                }
                catch (Exception e) { Say(args, "failed: " + e.Message); }
            }, isCheat: true);
        }

        private static void Say(Terminal.ConsoleEventArgs args, string s) { args.Context?.AddString("[vab_preview_mesh] " + s); s_log.LogDebug("[VABackpacks] mesh preview: " + s); }

        private static void Apply(Terminal.ConsoleEventArgs args, string path)
        {
            var p = Player.m_localPlayer; if (p == null) { Say(args, "no local player"); return; }
            if (!File.Exists(path)) { Say(args, "no file " + path); return; }
            var vis = p.GetComponent<VisEquipment>(); if (vis == null) { Say(args, "no VisEquipment"); return; }
            // the worn bag's heaviest mesh filter(s): the biggest mesh under the player's attached equipment
            MeshFilter best = null; int bestV = 0;
            foreach (var mf in p.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null || s_original.ContainsKey(mf)) continue;
                int v = mf.sharedMesh.vertexCount; if (v > bestV) { bestV = v; best = mf; }
            }
            if (best == null || bestV < 20000) { Say(args, $"no heavy worn mesh on the player (largest {bestV} vertices): equip the Explorer Bag first"); return; }
            var mesh = LoadObj(path);
            string fit = FitTo(mesh, best.sharedMesh.bounds);
            Say(args, "fit to the original's bounds: " + fit);
            s_original[best] = best.sharedMesh;
            string was = best.sharedMesh.name; int wasV = bestV, wasT = best.sharedMesh.triangles.Length / 3;
            best.sharedMesh = mesh;
            Say(args, $"'{best.name}' now shows {Path.GetFileName(path)}: {mesh.vertexCount} vertices / {mesh.triangles.Length / 3} triangles (was '{was}' {wasV} / {wasT})");
        }

        private static void Restore(Terminal.ConsoleEventArgs args)
        {
            int n = 0;
            foreach (var kv in s_original) if (kv.Key != null) { kv.Key.sharedMesh = kv.Value; n++; }
            s_original.Clear();
            Say(args, $"original bag mesh back on {n} renderer(s)");
        }

        /// <summary>
        /// Blender's FBX import + OBJ export and Unity's FBX import convert axes and units differently, so the OBJ is fitted onto the
        /// original mesh's local bounds: the axis order (of 6) whose extents match best, then one uniform scale and an offset onto its
        /// centre. Returns what was done, for the log (a large residual = the candidate needs a manual fix).
        /// </summary>
        private static string FitTo(Mesh m, Bounds target)
        {
            int[][] perms = { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } };
            var src = m.bounds.size; var dst = target.size;
            int[] bp = perms[0]; float bestErr = float.MaxValue, bestS = 1f;
            foreach (var p in perms)
            {
                var s = new Vector3(src[p[0]], src[p[1]], src[p[2]]);
                float k = (dst.x + dst.y + dst.z) / Mathf.Max(1e-5f, s.x + s.y + s.z);
                float err = Mathf.Abs(s.x * k - dst.x) + Mathf.Abs(s.y * k - dst.y) + Mathf.Abs(s.z * k - dst.z);
                if (err < bestErr) { bestErr = err; bp = p; bestS = k; }
            }
            var v = new List<Vector3>(); m.GetVertices(v); var n = new List<Vector3>(); m.GetNormals(n);
            for (int i = 0; i < v.Count; i++) { var a = v[i]; v[i] = new Vector3(a[bp[0]], a[bp[1]], a[bp[2]]) * bestS; }
            for (int i = 0; i < n.Count; i++) { var a = n[i]; n[i] = new Vector3(a[bp[0]], a[bp[1]], a[bp[2]]); }
            m.SetVertices(v); m.SetNormals(n); m.RecalculateBounds();
            var off = target.center - m.bounds.center;
            for (int i = 0; i < v.Count; i++) v[i] += off;
            m.SetVertices(v); m.RecalculateBounds();
            return $"axes ({bp[0]},{bp[1]},{bp[2]}), scale {bestS:0.####}, offset {off}, size {m.bounds.size} vs original {target.size} (residual {bestErr:0.###} m)";
        }

        /// <summary>A small OBJ reader: v / vt / vn / f (triangles and quads; v/vt/vn, v//vn, v/vt and v forms). Faces are split per corner.</summary>
        private static Mesh LoadObj(string path)
        {
            var pos = new List<Vector3>(); var uv = new List<Vector2>(); var nor = new List<Vector3>();
            var outP = new List<Vector3>(); var outU = new List<Vector2>(); var outN = new List<Vector3>(); var tri = new List<int>();
            var map = new Dictionary<string, int>();
            var ci = CultureInfo.InvariantCulture;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim(); if (line.Length < 2 || line[0] == '#') continue;
                var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                switch (t[0])
                {
                    // OBJ is right-handed (Blender export), Unity left-handed: x is mirrored and the winding flipped below
                    case "v": pos.Add(new Vector3(-float.Parse(t[1], ci), float.Parse(t[2], ci), float.Parse(t[3], ci))); break;
                    case "vt": uv.Add(new Vector2(float.Parse(t[1], ci), float.Parse(t[2], ci))); break;
                    case "vn": nor.Add(new Vector3(-float.Parse(t[1], ci), float.Parse(t[2], ci), float.Parse(t[3], ci))); break;
                    case "f":
                        var idx = new List<int>();
                        for (int i = 1; i < t.Length; i++)
                        {
                            if (!map.TryGetValue(t[i], out int k))
                            {
                                var s = t[i].Split('/');
                                int vi = int.Parse(s[0]) - 1; outP.Add(pos[vi < 0 ? pos.Count + vi + 1 : vi]);
                                outU.Add(s.Length > 1 && s[1].Length > 0 ? uv[int.Parse(s[1]) - 1] : Vector2.zero);
                                outN.Add(s.Length > 2 && s[2].Length > 0 ? nor[int.Parse(s[2]) - 1] : Vector3.up);
                                k = outP.Count - 1; map[t[i]] = k;
                            }
                            idx.Add(k);
                        }
                        for (int i = 1; i + 1 < idx.Count; i++) { tri.Add(idx[0]); tri.Add(idx[i + 1]); tri.Add(idx[i]); }
                        break;
                }
            }
            var m = new Mesh { name = Path.GetFileNameWithoutExtension(path) + "_preview", indexFormat = outP.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            m.SetVertices(outP); m.SetUVs(0, outU); m.SetNormals(outN); m.SetTriangles(tri, 0); m.RecalculateBounds();
            return m;
        }
    }
}
