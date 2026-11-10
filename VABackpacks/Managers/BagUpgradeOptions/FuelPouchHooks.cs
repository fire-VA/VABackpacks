// The fuel pouches (Smelter's, Kiln's) on the framework's hooks: the fuel goes back to the bag (or at the feet) before the upgrade is
// destroyed, and the confirm popup says so; and fuel dropped straight onto the slotted upgrade's rail cell fills its pouch (no window).

using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    internal static class FuelPouchHooks
    {
        private static bool s_registered;

        private static readonly AccessTools.FieldRef<InventoryGui, int> DragAmount = AccessTools.FieldRefAccess<InventoryGui, int>("m_dragAmount");
        private static readonly System.Reflection.MethodInfo SetupDragItem = AccessTools.Method(typeof(InventoryGui), "SetupDragItem");

        /// <summary>Once, from Smelter's and Kiln's Bind.</summary>
        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            BagUpgrades.UpgradeRemoving += (player, bag, kind, upgrade) =>
            {
                FuelPouch pouch = FuelPouch.For(upgrade);
                if (pouch == null || player == null) return;
                string what = pouch.ReturnToBag(BagUpgrades.BagInventory(player, bag), player);
                FuelPouchLog.Debug($"[FuelPouch] {upgrade?.m_shared.m_name} removed: {what}");
            };
            BagUpgrades.UpgradeRemoveWarning += upgrade => FuelPouch.For(upgrade)?.SwapWarning();
        }

        /// <summary>
        /// A stack dropped onto the slotted upgrade's rail cell, by vanilla's drag rules: only the dragged amount (a shift-split carries
        /// part of the stack) goes in, up to the room left. All of the drag in -> the drag ends (SetupDragItem(null)); part -> the
        /// drag amount drops to what's left. False (refused) leaves the stack on the cursor, and the rail does its usual refuse.
        /// </summary>
        internal static bool FillFromSlotDrop(FuelPouch pouch, ItemDrop.ItemData dropped, Inventory from)
        {
            string prefab = dropped?.m_dropPrefab != null ? dropped.m_dropPrefab.name : null;
            if (prefab == null || !pouch.Accepts.Contains(prefab))
                return Refuse($"{prefab ?? "?"} is not {pouch.UpgradeId} fuel (takes {string.Join(", ", pouch.Accepts)})");
            int room = pouch.Capacity - pouch.Count;
            if (room <= 0) return Refuse($"the {pouch.UpgradeId} pouch is full ({pouch.Count} / {pouch.Capacity})");
            if (from == null || !from.ContainsItem(dropped)) return Refuse($"{prefab} is no longer in the inventory it was dragged from");

            InventoryGui gui = InventoryGui.instance;
            int dragged = gui != null ? DragAmount(gui) : dropped.m_stack;
            dragged = Mathf.Clamp(dragged, 1, dropped.m_stack);
            int moved;
            if (dragged >= dropped.m_stack) moved = pouch.TryFill(dropped, from);   // the whole stack: takes what fits and removes it
            else
            {
                // a split drag: only the dragged part goes in; removed first, then filled, never more than fits, so nothing is lost
                int take = Mathf.Min(dragged, room);
                ItemDrop.ItemData part = dropped.Clone();
                part.m_stack = take;
                if (!from.RemoveItem(dropped, take)) return Refuse($"could not take {take} {prefab} from the dragged stack");
                moved = pouch.TryFill(part);
                if (moved < take && dropped.m_dropPrefab != null) from.AddItem(dropped.m_dropPrefab, take - moved);
            }
            if (moved <= 0) return Refuse($"the {pouch.UpgradeId} pouch took none of {dragged} {prefab}");

            int left = dragged - moved;
            if (gui != null)
            {
                if (left <= 0 || !from.ContainsItem(dropped) || dropped.m_stack <= 0)
                {
                    try { SetupDragItem?.Invoke(gui, new object[] { null, null, 1 }); }
                    catch (Exception e) { FuelPouchLog.Debug($"[FuelPouch] SetupDragItem threw {(e.InnerException ?? e).Message}"); }
                }
                else DragAmount(gui) = Mathf.Min(left, dropped.m_stack);
            }
            FuelPouchLog.Debug($"[FuelPouch] drop on slot: {moved} {prefab} added (pouch now {pouch.Count} / {pouch.Capacity})"
                + (left > 0 ? $"; {left} stay on the cursor" : ""));
            return true;
        }

        private static bool Refuse(string why)
        {
            FuelPouchLog.Debug($"[FuelPouch] drop on slot refused: {why}");
            return false;
        }
    }
}
