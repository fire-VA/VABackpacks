// CampInteractPatches — three small Harmony patches:
//   1. ItemDrop.GetHoverText  → append "Hold Shift+C to deploy as camp"
//      to dropped backpack items.
//   2. Container.GetHoverText → append "Hold Shift+C to pack camp" to
//      chests that carry a BackpackCampMarker.
//   3. Container.Awake        → re-attach the BackpackCampMarker when a
//      camp chest loads from a saved ZDO (chunk-reload path).
//
// The actual gesture detection lives in BackpackCampManager.Update()
// which polls Input.GetKey(KeyCode.C) + Shift and uses a camera raycast
// to resolve the hovered target. That avoids fighting vanilla's
// pickup-on-E and open-chest-on-E logic — Shift+C has no vanilla
// binding so we can claim it cleanly.

using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    [HarmonyPatch]
    public static class CampInteractPatches
    {
        // ── ItemDrop hover-text injection ─────────────────────────────

        /// <summary>
        /// Append the deploy-as-camp prompt to the hover text of dropped
        /// backpack items. Postfix because we want vanilla's pickup line
        /// to remain visible — both gestures are valid on a dropped
        /// backpack (E to pick up, Shift+C-hold to deploy).
        /// </summary>
        [HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.GetHoverText))]
        [HarmonyPostfix]
        private static void ItemDrop_GetHoverText_Postfix(ItemDrop __instance, ref string __result)
        {
            if (!ConfigManager.Instance.configCampEnabled.Value) return;
            if (__instance == null || __instance.m_itemData == null) return;
            if (!BackpackApi.IsBackpack(__instance.m_itemData)) return;

            float dur = ConfigManager.Instance.configCampHoldDuration.Value;
            __result += $"\n[<color=yellow><b>Hold $KEY_Use</b></color>] Deploy camp ({dur:F0}s)";   // 1.0.19 (Fire 10-07)
            __result = Localization.instance != null ? Localization.instance.Localize(__result) : __result;
        }

        /// <summary>
        /// 1.0.19 (Fire 10-07: hold E deploys the camp, tap E picks up): the first Use press on a dropped backpack is held back
        /// and handed to BackpackCampManager, which picks it up on release or deploys the camp after the hold time.
        /// </summary>
        [HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.Interact))]
        [HarmonyPrefix]
        private static bool ItemDrop_Interact_Prefix(ItemDrop __instance, Humanoid character, bool repeat, ref bool __result)
        {
            if (BackpackCampManager.AllowPickup || repeat) return true;
            if (!ConfigManager.Instance.configCampEnabled.Value) return true;
            if (__instance == null || __instance.m_itemData == null || character != Player.m_localPlayer) return true;
            if (!BackpackApi.IsBackpack(__instance.m_itemData)) return true;
            BackpackCampManager.BeginHoldUse(__instance);
            __result = true;
            return false;
        }

        // ── Container hover-text injection ────────────────────────────

        /// <summary>
        /// Append the pack-up prompt to the hover text of a deployed
        /// camp container. Only shows on chests that actually carry a
        /// BackpackCampMarker — vanilla chests are unaffected.
        /// </summary>
        [HarmonyPatch(typeof(Container), nameof(Container.GetHoverText))]
        [HarmonyPostfix]
        private static void Container_GetHoverText_Postfix(Container __instance, ref string __result)
        {
            if (!ConfigManager.Instance.configCampEnabled.Value) return;
            if (__instance == null) return;
            var marker = __instance.GetComponent<BackpackCampMarker>();
            if (marker == null) return;

            float dur = ConfigManager.Instance.configCampHoldDuration.Value;
            string key = FormatGestureKey();
            __result += $"\n[<color=yellow><b>Hold L.Shift+{key}</b></color>] " +
                        $"Pack camp ({dur:F0}s)";
        }

        /// <summary>Display-friendly form of the configured second key.</summary>
        private static string FormatGestureKey()
        {
            var k = ConfigManager.Instance.configCampGestureKey.Value;
            // Single-letter KeyCodes show as "A", "B" etc. in their ToString;
            // numbered/special ones get their full name. Both are fine.
            return k.ToString();
        }

        // ── Marker rehydration ────────────────────────────────────────
        //
        // When a chunk loads, ZNetScene re-instantiates piece_chest_wood
        // GameObjects from saved ZDOs. The instantiated chest doesn't
        // carry our BackpackCampMarker MonoBehaviour by default — the
        // marker is added at deploy time and isn't part of the prefab.
        // We re-attach it here when we detect the camp ZDO keys.

        [HarmonyPatch(typeof(Container), "Awake")]
        [HarmonyPostfix]
        private static void Container_Awake_Postfix(Container __instance)
        {
            if (__instance == null) return;
            var nview = __instance.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            var zdo = nview.GetZDO();
            if (zdo == null) return;

            // The owner field is the cheapest tell — non-zero means this
            // chest was deployed by the camp system.
            long owner = zdo.GetLong(BackpackCampMarker.KEY_OWNER, 0L);
            if (owner == 0L) return;

            // Already has a marker? Nothing to do.
            if (__instance.GetComponent<BackpackCampMarker>() != null) return;

            __instance.gameObject.AddComponent<BackpackCampMarker>();
        }
    }
}
