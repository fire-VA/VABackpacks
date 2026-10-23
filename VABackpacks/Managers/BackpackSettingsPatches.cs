using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // The per-bag SpeedModifier and Teleportable settings were bound
    // but never applied (BackpackManager's SpeedModifier branch was an empty placeholder; nothing read Teleportable).
    //   SpeedModifier: added to vanilla's equipment movement modifier for every equipped bag, the same total armour and
    //   weapons feed (Player.GetEquipmentMovementModifier), so it shows and stacks exactly like gear speed.
    //   Teleportable = false: a bag holding anything blocks portals while carried (worn or in the bag grid), unless the
    //   world allows teleporting everything. Reuses BackpackSpecialSlots' saved-grid parse (one parse per distinct grid).
    internal static class BackpackSettingsPatches
    {
        private static ConfigManager Cfg => ConfigManager.Instance;

        internal static float SpeedFor(ItemDrop.ItemData bag)
        {
            if (bag?.m_dropPrefab == null || Cfg == null) return 0f;
            return Cfg.configBackpackSpeedModifier.TryGetValue(bag.m_dropPrefab.name, out var e) && e != null ? e.Value : 0f;
        }

        internal static bool Teleportable(ItemDrop.ItemData bag)
        {
            if (bag?.m_dropPrefab == null || Cfg == null) return true;
            return !Cfg.configBackpackTeleportable.TryGetValue(bag.m_dropPrefab.name, out var e) || e == null || e.Value;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.GetEquipmentMovementModifier))]
        private static class Speed_Patch
        {
            private static void Postfix(Player __instance, ref float __result)
            {
                var worn = BackpackApi.GetEquippedBackpacks(__instance);
                if (worn == null) return;
                foreach (ItemDrop.ItemData bag in worn) __result += SpeedFor(bag);
            }
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.IsTeleportable))]
        private static class Teleport_Patch
        {
            private static void Postfix(Inventory __instance, bool allowAllItems, ref bool __result)
            {
                if (!__result) return;
                Player p = Player.m_localPlayer;
                if (p == null || __instance != p.GetInventory()) return;
                if (allowAllItems || (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.TeleportAll))) return;

                Inventory live = BackpackManager.LegacyLiveInventory(p, out ItemDrop.ItemData liveOwner);
                foreach (ItemDrop.ItemData bag in __instance.GetAllItems())
                {
                    if (!BackpackManager.IsBackpack(bag) || Teleportable(bag)) continue;
                    bool holds = (bag == liveOwner && live != null && live.GetAllItems().Count > 0)
                                 || (bag.m_customData != null && bag.m_customData.TryGetValue("BackpackInventory", out string saved) && BackpackSpecialSlots.SavedHasItems(saved))
                                 || BackpackSpecialSlots.AnyStoredSpecial(bag);
                    if (holds) { __result = false; return; }
                }
            }
        }
    }
}
