// CampBuildProbe - 1.0.35 (Fire: near the camp nothing could be built without devcommands "nobuild"). Read only: while
// the player holds a build ghost inside a camp, every change of vanilla's placement status is logged with the reason
// and the nearest workbench's distance and range, so the next round names what blocks building.

using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    [HarmonyPatch]
    internal static class CampBuildProbe
    {
        static string _last;
        static readonly AccessTools.FieldRef<Player, GameObject> Ghost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
        static readonly System.Reflection.FieldInfo Status = AccessTools.Field(typeof(Player), "m_placementStatus");

        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        [HarmonyPostfix]
        private static void Player_UpdatePlacementGhost_Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            GameObject ghost = Ghost(__instance);
            if (ghost == null || !ghost.activeSelf) { _last = null; return; }
            Vector3 pos = ghost.transform.position;
            if (!BackpackCampRegistry.IsPointInAnyCamp(pos)) { _last = null; return; }

            Piece piece = ghost.GetComponent<Piece>();
            string need = piece != null && piece.m_craftingStation != null ? piece.m_craftingStation.m_name : "none";
            bool stationOk = need == "none" || CraftingStation.HaveBuildStationInRange(need, pos);
            string status = Status?.GetValue(__instance)?.ToString() ?? "?";
            string key = $"{status}|{need}|{stationOk}";
            if (key == _last) return;
            _last = key;
            Debug.Log($"[VABackpacks] camp build probe: {Utils.GetPrefabName(ghost)} placement {status}; needs station {need}, in range {stationOk}");
        }
    }
}
