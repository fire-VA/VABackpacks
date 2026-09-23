// CampVegetationSuppressPatch — make ZoneSystem treat the camp's
// footprint as a "clear area" so trees / grass / mushrooms don't
// pop in over a deployed camp when the host zone refreshes.
//
// Vanilla flow (ZoneSystem.cs:758):
//   PlaceVegetation iterates random veg-spawn points in a zone. For
//   each point it asks `InsideClearArea(clearAreas, p)`. If the point
//   sits inside any registered ClearArea (Vector3 center, float radius)
//   the placement is skipped.
//
// Locations build that list at zone-population time from their own
// `Location.m_clearArea + m_exteriorRadius`. We can't inject into
// that list directly without a transpiler, so we postfix the private
// `InsideClearArea` helper and OR our own camp footprints in.
//
// Conflict-free with WorldEditCommands: WEC doesn't patch this method
// either; the postfix just adds an extra "OR true" branch when the
// query point is inside a camp.

using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    [HarmonyPatch]
    public static class CampVegetationSuppressPatch
    {
        [HarmonyPatch(typeof(ZoneSystem), "InsideClearArea")]
        [HarmonyPostfix]
        private static void ZoneSystem_InsideClearArea_Postfix(Vector3 point, ref bool __result)
        {
            if (__result) return;        // already inside a vanilla clear area
            if (BackpackCampManager.IsPointInVegetationClearArea(point))
                __result = true;
        }
    }
}
