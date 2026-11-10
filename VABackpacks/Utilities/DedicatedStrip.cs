using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Profiling;

namespace VerdantsAscent.Utilities
{
    /// <summary>
    /// On a HEADLESS server (batch mode, no graphics
    /// device) the bags' bundle still has to load, because ItemDrop / ObjectDB / ZNetScene need the prefabs, but nothing there is ever
    /// drawn. After the load, this drops the render-only data from VABackpacks' own item and piece prefabs: MeshFilter and skinned meshes
    /// (BackpackROtateTest2 26 MB, Rope, ...) and the renderers' materials (the 2048^2 albedo / normal maps). Meshes a MeshCollider also
    /// uses are kept (physics needs them). Then the now-unreferenced assets are unloaded. Clients and listen hosts: untouched (a no-op).
    /// One line logs what was dropped and the measured sizes.
    /// </summary>
    internal static class DedicatedStrip
    {
        /// <summary>True on a dedicated / batch-mode server with no graphics device: render data is never used.</summary>
        internal static bool Headless =>
            Application.isBatchMode || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

        internal static void Run(IEnumerable<GameObject> prefabs, string plugin)
        {
            if (!Headless) return;
            var meshes = new HashSet<Mesh>();
            var textures = new HashSet<Texture>();
            int filters = 0, skinned = 0, renderers = 0, keptForColliders = 0;
            foreach (GameObject p in prefabs.Where(x => x != null))
            {
                var colliderMeshes = new HashSet<Mesh>(p.GetComponentsInChildren<MeshCollider>(true).Select(c => c.sharedMesh).Where(m => m != null));
                foreach (var mf in p.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.sharedMesh == null) continue;
                    if (colliderMeshes.Contains(mf.sharedMesh)) { keptForColliders++; continue; }
                    meshes.Add(mf.sharedMesh); mf.sharedMesh = null; filters++;
                }
                foreach (var sk in p.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (sk.sharedMesh != null && !colliderMeshes.Contains(sk.sharedMesh)) { meshes.Add(sk.sharedMesh); sk.sharedMesh = null; skinned++; }
                }
                foreach (var r in p.GetComponentsInChildren<Renderer>(true))
                {
                    var mats = r.sharedMaterials;
                    if (mats == null || mats.Length == 0) continue;
                    foreach (var m in mats.Where(m => m != null))
                        foreach (int prop in m.GetTexturePropertyNameIDs())
                        { var t = m.GetTexture(prop); if (t != null) textures.Add(t); }
                    r.sharedMaterials = new Material[0];
                    renderers++;
                }
            }
            long meshBytes = meshes.Sum(m => Profiler.GetRuntimeMemorySizeLong(m));
            long texBytes = textures.Sum(t => Profiler.GetRuntimeMemorySizeLong(t));
            string biggest = string.Join(", ", meshes.OrderByDescending(m => Profiler.GetRuntimeMemorySizeLong(m)).Take(3)
                .Select(m => $"{m.name} {Profiler.GetRuntimeMemorySizeLong(m) / 1048576f:0.0} MB").ToArray());
            Resources.UnloadUnusedAssets();
            VabLog.Line($"[{plugin}] dedicated strip: headless server, render-only data dropped from the bag prefabs: {filters} mesh filter(s) + {skinned} skinned mesh(es) " +
                      $"({meshes.Count} meshes, {meshBytes / 1048576f:0.0} MB; biggest {biggest}), {renderers} renderer(s)' materials ({textures.Count} textures, {texBytes / 1048576f:0.0} MB); " +
                      $"{keptForColliders} mesh(es) kept for colliders; unused assets unloaded");
        }
    }
}
