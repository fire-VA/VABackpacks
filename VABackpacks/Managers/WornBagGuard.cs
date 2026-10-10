// WornBagGuard - 1.0.33 (Fire 10-08, round 33: "If you removed everything from the player and then that backpack showed up,
// that's an actual problem."). A worn bag is tracked outside the inventory (the coexistence dict, so a cape can share the
// shoulder) and a weapon-slot bag sits in m_rightItem. When the bag item leaves the player's inventory by a path that never
// unequips it (Inventory.RemoveItem / RemoveAll by a mod or a test, a destroy), the tracking, the "va_bp_worn" ZDO key and the
// worn visual stayed: a ghost bag. After every change of a player's inventory, any equipped bag that is no longer in it is
// unequipped the normal way (Humanoid.UnequipItem), which runs the existing unequip path: the dict entry goes, m_equipped is
// cleared, OnUnequipItem/FireUnequipped tear down the storage binding, and the visual's next update clears va_bp_worn.
// Contents stay with the ItemData that left (they live in its customData), so nothing is duplicated or added back.

using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    internal static class WornBagGuard
    {
        static bool _busy;

        [HarmonyPatch(typeof(Inventory), "Changed")]
        public static class Inventory_Changed_Patch
        {
            public static void Postfix(Inventory __instance)
            {
                if (_busy || __instance == null) return;
                Player owner = null;
                foreach (Player p in Player.GetAllPlayers())
                    if (p != null && p.GetInventory() == __instance) { owner = p; break; }
                if (owner == null) return;

                var worn = BackpackManager.FindAllEquippedBackpacksPublic(owner);
                if (worn == null || worn.Count == 0) return;
                var gone = worn.Where(b => b != null && !__instance.ContainsItem(b)).ToList();
                if (gone.Count == 0) return;

                _busy = true;
                try
                {
                    foreach (var bag in gone)
                    {
                        Debug.Log($"[VABackpacks] worn bag left the inventory without an unequip: {bag.m_dropPrefab?.name ?? bag.m_shared.m_name}; unequipping it (no ghost bag)");
                        try { owner.UnequipItem(bag, false); }
                        catch (Exception ex) { Debug.LogWarning($"[VABackpacks] worn bag guard: unequip failed: {ex.Message}"); }
                        bag.m_equipped = false;
                    }
                }
                finally { _busy = false; }
            }
        }
    }
}
