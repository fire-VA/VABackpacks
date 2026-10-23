// BagBonusPatches - bag affixes from another mod through BackpackApi.BonusProvider.
// Without a provider (FRC not installed) every patch returns at once and nothing changes.
//   carry weight    : Player.GetMaxCarryWeight, both modes
//   loaded speed    : Player.GetEquipmentMovementModifier, while the bag HAS contents - legacy mode here; with VAInventory the
//                     contents are player-grid cells, so VAInventory adds it (it knows the cells)
//   contents weight : legacy mode in BackpackManager.GetWeight_Patch; with VAInventory, VAInventory applies it to the bag's cells

using HarmonyLib;

namespace VerdantsAscent.Managers
{
    [HarmonyPatch(typeof(Player), nameof(Player.GetMaxCarryWeight))]
    internal static class BagBonusCarryPatch
    {
        private static void Postfix(Player __instance, ref float __result)
        {
            if (BackpackApi.BonusProvider == null || __instance == null) return;
            __result += BackpackApi.GetEquippedBonus(__instance)[BackpackApi.BonusCarryWeight];
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.GetEquipmentMovementModifier))]
    internal static class BagBonusSpeedPatch
    {
        private static void Postfix(Player __instance, ref float __result)
        {
            if (BackpackApi.BonusProvider == null || __instance == null || !BackpackManager.LegacyMode) return;
            Inventory inv = BackpackManager.LegacyLiveInventory(__instance, out ItemDrop.ItemData owner);
            if (inv == null || owner == null || inv.NrOfItems() == 0) return;
            float[] b = BackpackApi.GetBonus(owner);
            if (b != null && b.Length > BackpackApi.BonusLoadedSpeed) __result += b[BackpackApi.BonusLoadedSpeed];
        }
    }
}
