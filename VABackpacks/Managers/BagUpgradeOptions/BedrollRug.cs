// BedrollRug - the object a Bedroll lays down (Fire 10-23: "the bedroll should lay down a deerskin rug, and that would be interactable
// as a bed"). A runtime prefab, FiresBagUp_BedrollRug: a clone of vanilla's deer-hide rug (rug_deer: its model, material and collider)
// with a Bed component added, so it LOOKS like the rug and BEHAVES as a bed (Use = sleep / set spawn). The real rug_deer prefab is never
// changed, so players' own rugs stay rugs. The Bed's settings are copied from vanilla's bed; its spawn point is the rug's own centre.
// Registered in ZNetScene on every peer (same name hash everywhere), in an inactive holder like the bag upgrade items. Hover text:
// "Bedroll" in place of the bed's name. Bedroll.cs lays it down and keeps the roof / fire exemption for it.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    internal static class BedrollRug
    {
        internal const string PrefabName = "FiresBagUp_BedrollRug";
        private const string RugPrefab = "rug_deer", BedPrefab = "bed", ShownName = "Bedroll";
        private static readonly Vector3 SpawnPointOffset = new Vector3(0f, 0.1f, 0f);
        private static GameObject s_holder, s_prefab;

        /// <summary>The bedroll prefab (built on first use; null when rug_deer or bed is missing).</summary>
        internal static GameObject Prefab(ZNetScene zs)
        {
            if (s_prefab != null || zs == null) return s_prefab;
            GameObject rug = zs.GetPrefab(RugPrefab), bed = zs.GetPrefab(BedPrefab);
            Bed vanillaBed = bed != null ? bed.GetComponent<Bed>() : null;
            if (rug == null || vanillaBed == null) { BedrollOption.Log.LogWarning($"[Bedroll] can't build {PrefabName}: '{RugPrefab}' {(rug != null ? "found" : "MISSING")}, '{BedPrefab}' {(vanillaBed != null ? "found" : "MISSING")}"); return null; }
            if (s_holder == null) { s_holder = new GameObject("VABackpacks_BedrollRug"); s_holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(s_holder); }

            GameObject clone = UnityEngine.Object.Instantiate(rug, s_holder.transform, false);
            clone.name = PrefabName;
            Bed b = clone.AddComponent<Bed>();
            // vanilla bed's settings (effects, texts, ranges); references into the bed prefab's own children are replaced below
            foreach (FieldInfo f in typeof(Bed).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (typeof(Component).IsAssignableFrom(f.FieldType) || f.FieldType == typeof(GameObject)) continue;
                try { f.SetValue(b, f.GetValue(vanillaBed)); } catch { }
            }
            var spawn = new GameObject("spawnpoint");
            spawn.transform.SetParent(clone.transform, false);
            spawn.transform.localPosition = SpawnPointOffset;
            b.m_spawnPoint = spawn.transform;
            Piece piece = clone.GetComponent<Piece>();
            if (piece != null) piece.m_name = ShownName;
            s_prefab = clone;
            BedrollOption.Log.LogDebug($"[Bedroll] built {PrefabName}: '{RugPrefab}' look + a Bed (settings from '{BedPrefab}')");
            return s_prefab;
        }

        private static void Register(ZNetScene zs)
        {
            GameObject prefab = Prefab(zs);
            if (prefab == null || zs.GetPrefab(PrefabName) != null) return;
            var named = (Dictionary<int, GameObject>)AccessTools.Field(typeof(ZNetScene), "m_namedPrefabs").GetValue(zs);
            zs.m_prefabs.Add(prefab);
            named[PrefabName.GetStableHashCode()] = prefab;
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class ZNetScene_Awake
        {
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(ZNetScene __instance)
            {
                try { Register(__instance); } catch (Exception e) { BedrollOption.Log.LogWarning($"[Bedroll] register {PrefabName} failed: {e.Message}"); }
            }
        }

        internal static bool IsRug(Component c) => c != null && Utils.GetPrefabName(c.gameObject) == PrefabName;

        // the hover reads "Bedroll" in place of the bed's own name
        [HarmonyPatch(typeof(Bed), nameof(Bed.GetHoverText))]
        private static class HoverText
        {
            private static void Postfix(Bed __instance, ref string __result)
            {
                if (!IsRug(__instance) || string.IsNullOrEmpty(__result)) return;
                int nl = __result.IndexOf('\n');
                __result = ShownName + (nl >= 0 ? __result.Substring(nl) : "");
            }
        }

        [HarmonyPatch(typeof(Bed), nameof(Bed.GetHoverName))]
        private static class HoverName
        {
            private static void Postfix(Bed __instance, ref string __result) { if (IsRug(__instance)) __result = ShownName; }
        }
    }
}
