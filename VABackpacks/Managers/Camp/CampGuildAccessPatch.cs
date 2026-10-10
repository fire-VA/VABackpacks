// CampGuildAccessPatch — extend a camp chest's owner-only access rule
// to also grant access to the owner's guildmates.
//
// Vanilla flow (Container.CheckAccess, private):
//   switch (m_privacy) {
//     case Private: return m_piece.GetCreator() == playerID;
//     case Group:   return ...
//     default:      return true;
//   }
//
// We deploy camp chests as Private + creator = deployer, so vanilla
// denies everyone except the deployer. Postfix this method: when
// vanilla returns false on a camp chest, ask the GuildBridge whether
// the requesting player and the camp owner are in the same guild;
// if so, flip the result to true.
//
// Camp chests carry the BackpackCampMarker (added at deploy + re-
// attached on chunk load via the Container.Awake postfix). Other
// Private containers in the world (someone else's regular chest with
// guard-stone access etc.) lack the marker and don't get the bypass —
// we only widen access for our own chests.

using HarmonyLib;

namespace VerdantsAscent.Managers.Camp
{
    [HarmonyPatch]
    public static class CampGuildAccessPatch
    {
        [HarmonyPatch(typeof(Container), "CheckAccess")]
        [HarmonyPostfix]
        private static void Container_CheckAccess_Postfix(Container __instance,
                                                          long playerID,
                                                          ref bool __result)
        {
            // Already allowed — nothing to do (covers the owner's own access).
            if (__result) return;
            if (__instance == null) return;

            // Only widen for camp chests.
            var marker = __instance.GetComponent<BackpackCampMarker>();
            if (marker == null) return;
            if (marker.OwnerPlayerId == 0L) return;       // not yet hydrated

            // Guildmate check. Returns false cleanly when FiresNPCs
            // isn't loaded or either party has no guild — leaving the
            // original deny in place.
            if (GuildBridge.AreGuildmates(marker.OwnerPlayerId, playerID))
            {
                __result = true;
            }
        }
    }
}
