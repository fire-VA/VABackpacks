// CampCoverBypassPatch — make the camp's radius count as "covered" so
// the bed inside it doesn't refuse to set spawn ("Bed is too exposed").
//
// Vanilla Bed.CheckExposure (Bed.cs:95) calls Cover.GetCoverForPoint at
// the bed's spawn point. If the cover percentage is below 0.8 OR the
// point is not under a roof, the bed refuses. We override that for
// beds inside any camp radius — a tarp's worth of "yes, this is sheltered"
// for the player without spawning an actual roof piece.
//
// We patch Bed.CheckExposure (private method, Harmony handles those by
// name) rather than Cover.GetCoverForPoint so we don't accidentally
// flip cover state for unrelated systems (cozy/rested timers, weather
// damage, etc.).

using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    [HarmonyPatch]
    public static class CampCoverBypassPatch
    {
        [HarmonyPatch(typeof(Bed), "CheckExposure")]
        [HarmonyPrefix]
        private static bool Bed_CheckExposure_Prefix(Bed __instance, ref bool __result)
        {
            if (__instance == null) return true;
            if (!ConfigManager.Instance.configCampEnabled.Value) return true;

            // Bed.GetSpawnPoint is what vanilla checks; we use the same
            // point so our radius math matches vanilla's expectation.
            Vector3 spawn = __instance.transform.position;
            try { spawn = __instance.GetSpawnPoint(); } catch { /* fall back to transform */ }

            if (BackpackCampRegistry.IsPointInAnyCamp(spawn))
            {
                __result = true;
                return false;       // skip vanilla — claim "covered + under roof"
            }
            return true;
        }

        /// <summary>
        /// Camp fires ignore weather. Vanilla `Fireplace.CheckWet` reads
        /// `EnvMan.IsWet()` + `Cover.GetCoverForPoint` to decide whether
        /// rain/wind has snuffed the fire. Inside any camp radius we
        /// short-circuit the whole check, force `m_wet = false`, and
        /// skip the vanilla method — so a camp fire stays lit through
        /// rain and high wind regardless of actual cover.
        ///
        /// (We patch CheckWet rather than Cover.GetCoverForPoint because
        /// flipping cover globally would also alter the player's
        /// rested/cozy state and bed-exposure logic — too broad. The
        /// bed bypass above is independently scoped to Bed.CheckExposure.)
        /// </summary>
        [HarmonyPatch(typeof(Fireplace), "CheckWet")]
        [HarmonyPrefix]
        private static bool Fireplace_CheckWet_Prefix(Fireplace __instance)
        {
            if (__instance == null) return true;
            if (!ConfigManager.Instance.configCampEnabled.Value) return true;

            if (BackpackCampRegistry.IsPointInAnyCamp(__instance.transform.position))
            {
                __instance.m_wet = false;
                return false;       // skip vanilla
            }
            return true;
        }

        /// <summary>
        /// Fire (2026-09-29): "the bed and work bench should bypass roof checks for camps"; last night the camp's workbench
        /// refused with "needs a roof". Vanilla `CraftingStation.CheckUsable` wants the roof check point under a roof with
        /// ≥ 70 % cover. Inside a camp radius the roof and cover part is skipped; a station that also needs a fire still
        /// needs one (vanilla's own second half, kept as is).
        /// </summary>
        [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.CheckUsable))]
        [HarmonyPrefix]
        private static bool CraftingStation_CheckUsable_Prefix(CraftingStation __instance, Player player, bool showMessage, ref bool __result)
        {
            if (__instance == null || player == null) return true;
            if (!ConfigManager.Instance.configCampEnabled.Value) return true;
            if (!__instance.m_craftRequireRoof) return true;
            Vector3 at = __instance.m_roofCheckPoint != null ? __instance.m_roofCheckPoint.position : __instance.transform.position;
            if (!BackpackCampRegistry.IsPointInAnyCamp(at)) return true;
            if (!__instance.m_craftRequireFire || player.NoCostCheat() || __instance.m_haveFire)
            {
                __result = true;
                return false;
            }
            if (showMessage) player.Message(MessageHud.MessageType.Center, "$msg_needfire");
            __result = false;
            return false;
        }

        /// <summary>
        /// Fire (2026-09-29): "You can sleep in the bed when it's raining". Vanilla `Bed.CheckWet` refuses while the player
        /// has the Wet status effect ("$msg_bedwet"). A bed inside a camp radius skips it; the camp is the shelter.
        /// </summary>
        [HarmonyPatch(typeof(Bed), "CheckWet")]
        [HarmonyPrefix]
        private static bool Bed_CheckWet_Prefix(Bed __instance, ref bool __result)
        {
            if (__instance == null) return true;
            if (!ConfigManager.Instance.configCampEnabled.Value) return true;
            if (!BackpackCampRegistry.IsPointInAnyCamp(__instance.transform.position)) return true;
            __result = true;
            return false;
        }
    }
}
