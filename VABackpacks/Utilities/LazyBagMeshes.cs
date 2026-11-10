using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;

namespace VerdantsAscent.Utilities
{
    /// <summary>
    /// 1.0.134 ([ghost]'s round51 [Mem] find, [lead] 10-28): on a CLIENT, the bags' heavy worn meshes (the Explorer Bag's
    /// BackpackROtateTest2_103424 is 26 MB) were loaded at game start and stayed in memory before any bag was ever worn. After the load,
    /// every mesh of our bag / piece prefabs above HeavyBytes is unloaded with Resources.UnloadAsset. Every reference stays as it is:
    /// Unity reloads an unloaded asset from its still-open bundle the moment something uses it (a bag worn, dropped or previewed), so
    /// the look never changes; the memory comes back only when a bag is shown. A headless server is handled by DedicatedStrip instead.
    /// Proof lines: one at start (what was freed), one the first time each freed mesh is drawn again ("reloaded on first use").
    /// </summary>
    [HarmonyPatch]
    internal static class LazyBagMeshes
    {
        private const long HeavyBytes = 2L * 1024 * 1024;   // only meshes worth freeing (the 26 / 4.6 / 2.3 MB ones), not small props
        private static readonly HashSet<Mesh> s_freed = new HashSet<Mesh>();
        private static readonly HashSet<Mesh> s_reported = new HashSet<Mesh>();
        private static readonly HashSet<string> s_prefabs = new HashSet<string>();   // our prefabs that carry a freed mesh (the cheap name gate)
        private static readonly BepInEx.Logging.ManualLogSource s_log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks.LazyMeshes");   // proof lines at Debug

        internal static void Run(IEnumerable<GameObject> prefabs, string plugin)
        {
            if (DedicatedStrip.Headless) return;
            var heavy = new Dictionary<Mesh, long>();
            foreach (GameObject p in prefabs.Where(x => x != null))
            {
                var colliderMeshes = new HashSet<Mesh>(p.GetComponentsInChildren<MeshCollider>(true).Select(c => c.sharedMesh).Where(m => m != null));
                IEnumerable<Mesh> meshes = p.GetComponentsInChildren<MeshFilter>(true).Select(f => f.sharedMesh)
                    .Concat(p.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(s => s.sharedMesh));
                foreach (var m in meshes)
                {
                    if (m == null || heavy.ContainsKey(m) || colliderMeshes.Contains(m)) continue;   // physics needs collider meshes resident
                    long b = Profiler.GetRuntimeMemorySizeLong(m);
                    if (b >= HeavyBytes) { heavy[m] = b; s_prefabs.Add(p.name); }
                }
            }
            long total = heavy.Values.Sum();
            string list = string.Join(", ", heavy.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key.name} {kv.Value / 1048576f:0.0} MB").ToArray());
            foreach (var m in heavy.Keys) { s_freed.Add(m); Resources.UnloadAsset(m); }
            s_log.LogDebug($"[{plugin}] lazy bag meshes: {heavy.Count} heavy mesh(es) freed until a bag is shown ({total / 1048576f:0.0} MB: {list}); they reload from the bundle on first use");
        }

        // proof: the first time a freed mesh is drawn again (a bag worn / dropped), one line with its vertex count = it really came back
        [HarmonyPatch(typeof(VisEquipment), "AttachItem"), HarmonyPostfix]   // once per attached item, never per frame
        private static void Worn(GameObject __result) => Check(__result, "worn", false);   // the attach child: no name gate (equip changes are rare)

        [HarmonyPatch(typeof(ItemDrop), "Awake"), HarmonyPostfix]
        private static void Dropped(ItemDrop __instance) => Check(__instance != null ? __instance.gameObject : null, "item in the world", true);

        private static void Check(GameObject go, string where, bool nameGate)
        {
            if (go == null || s_freed.Count == 0 || s_reported.Count == s_freed.Count) return;
            string n = go.name; int k = n.IndexOf("(Clone)", System.StringComparison.Ordinal); if (k >= 0) n = n.Substring(0, k).TrimEnd();
            if (nameGate && !s_prefabs.Contains(n) && !s_prefabs.Any(p => n.StartsWith(p, System.StringComparison.Ordinal))) return;   // not one of our heavy bags
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                var m = mf.sharedMesh;
                if (m == null || !s_freed.Contains(m) || !s_reported.Add(m)) continue;
                s_log.LogDebug($"[VABackpacks] lazy bag meshes: '{m.name}' reloaded on first use ({where}): {m.vertexCount} vertices, {Profiler.GetRuntimeMemorySizeLong(m) / 1048576f:0.0} MB");
            }
        }
    }
}
