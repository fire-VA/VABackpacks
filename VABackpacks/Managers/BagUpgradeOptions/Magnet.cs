// Magnet (bypass slot): loose loot within the tier's radius is pulled to the wearer and kept in the bag (vacuum only; nothing is sent
// home, no Remote Access needed). It widens vanilla's own auto-pickup (Player.AutoPickup pulls items within m_autoPickupRange to the
// player and picks them up), then moves each NEW stack that pickup made into a free cell of the worn bag. A pickup that merged into a
// stack already carried stays where that stack is. Local player only; guarded by WornTier.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public sealed class MagnetOption : IBagUpgradeOption
    {
        public const string UpgradeId = "Magnet";
        private static readonly float[] RadiusDefaults = { 4f, 6f, 8f };
        private static readonly ConfigEntry<float>[] Radius = new ConfigEntry<float>[BagUpgrades.TierCount];
        internal static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks.Magnet");

        /// <summary>
        /// Set by VAInventory (like BagUpgrades.BagCellsProvider): the FREE cell positions of one worn bag in the player inventory.
        /// Without it (no VAInventory / not wired yet) the pulled loot stays where vanilla put it, and that is logged.
        /// </summary>
        public static Func<Player, ItemDrop.ItemData, List<Vector2i>> FreeBagCells;

        public string Id => UpgradeId;
        public string Name => "Magnet";
        public string Slot => BackpackSpecialSlots.SlotBypass;
        public string Donor => "IronNails";
        public string[] Recipes => new[]
        {
            "forge,1,Bronze:5,LeatherScraps:10,IronNails:10",
            "forge,3,Iron:10,WolfPelt:5,IronNails:20",
            "forge,5,BlackMetal:10,LinenThread:5,IronNails:30",
        };

        public string Describe(int tier) => $"Loose loot within {RadiusFor(tier):0.#} m is pulled to you and kept in this bag (vacuum only: nothing is sent home).";

        public void Bind(ConfigFile cfg, ConfigSync sync)
        {
            for (int t = 0; t < BagUpgrades.TierCount; t++)
            {
                Radius[t] = cfg.Bind(BagUpgrades.Section, $"Magnet {BagUpgrades.TierNumerals[t]} radius", RadiusDefaults[t],
                    new ConfigDescription($"Loose loot within this many metres is pulled to the wearer of a bag with Magnet {BagUpgrades.TierNumerals[t]}. Server-synced.",
                        new AcceptableValueRange<float>(1f, 30f)));
                sync?.AddConfigEntry(Radius[t]);
            }
        }

        internal static float RadiusFor(int tier) => tier < 1 ? 0f : Radius[tier - 1]?.Value ?? RadiusDefaults[tier - 1];

        // ── the vacuum: vanilla auto-pickup with the Magnet's radius, for the frame it runs ─────────────────────────────────────
        private static float s_vanillaRange = -1f;
        private static int s_tier;

        [HarmonyPatch(typeof(Player), "AutoPickup")]
        private static class AutoPickupRange
        {
            private static void Prefix(Player __instance)
            {
                s_tier = 0;
                if (__instance == null || __instance != Player.m_localPlayer) return;
                s_tier = BagUpgrades.WornTier(__instance, UpgradeId);
                if (s_tier < 1) return;
                float r = RadiusFor(s_tier);
                if (__instance.m_autoPickupRange < r) { s_vanillaRange = __instance.m_autoPickupRange; __instance.m_autoPickupRange = r; }
            }

            private static void Finalizer(Player __instance)
            {
                if (s_vanillaRange >= 0f && __instance != null) __instance.m_autoPickupRange = s_vanillaRange;
                s_vanillaRange = -1f;
                s_tier = 0;
            }
        }

        // ── into the bag: the stack a magnet pickup made goes to a free bag cell ───────────────────────────────────────────────

        private struct PickupState { public HashSet<ItemDrop.ItemData> Before; public string Name; public int Stack; public float Distance; }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.Pickup))]
        private static class IntoTheBag
        {
            private static void Prefix(Humanoid __instance, GameObject go, out PickupState __state)
            {
                __state = default;
                if (s_tier < 1 || !(__instance is Player p) || p != Player.m_localPlayer || go == null) return;
                var drop = go.GetComponent<ItemDrop>();
                if (drop == null || drop.m_itemData == null) return;
                __state.Before = new HashSet<ItemDrop.ItemData>(p.GetInventory().GetAllItems());
                __state.Name = drop.m_itemData.m_dropPrefab != null ? drop.m_itemData.m_dropPrefab.name : drop.m_itemData.m_shared.m_name;
                __state.Stack = drop.m_itemData.m_stack;
                __state.Distance = Vector3.Distance(go.transform.position, p.transform.position);
            }

            private static void Postfix(Humanoid __instance, bool __result, PickupState __state)
            {
                if (!__result || __state.Before == null || !(__instance is Player p)) return;
                try
                {
                    ItemDrop.ItemData bag = BagUpgrades.WornBagWith(p, UpgradeId);
                    Inventory inv = p.GetInventory();
                    var fresh = inv.GetAllItems().Where(i => i != null && !__state.Before.Contains(i)).ToList();
                    if (fresh.Count == 0) { Log.LogDebug($"[Magnet] pulled {__state.Name} x{__state.Stack} from {__state.Distance:0.0} m: stacked onto one already carried"); return; }
                    List<ItemDrop.ItemData> inBag = BagUpgrades.ItemsInBag(p, bag);
                    foreach (ItemDrop.ItemData item in fresh)
                    {
                        if (inBag.Contains(item)) { Log.LogDebug($"[Magnet] pulled {__state.Name} x{item.m_stack} from {__state.Distance:0.0} m into the bag"); continue; }
                        List<Vector2i> free = FreeBagCells?.Invoke(p, bag);
                        if (free == null) { Log.LogDebug($"[Magnet] pulled {__state.Name} x{item.m_stack} from {__state.Distance:0.0} m; no bag-cell map (VAInventory not wired), left at {item.m_gridPos}"); continue; }
                        Vector2i cell = free.FirstOrDefault(c => inv.GetItemAt(c.x, c.y) == null);
                        if (free.Count == 0 || inv.GetItemAt(cell.x, cell.y) != null) { Log.LogDebug($"[Magnet] pulled {__state.Name} x{item.m_stack} from {__state.Distance:0.0} m; the bag is full, left at {item.m_gridPos}"); continue; }
                        Vector2i from = item.m_gridPos;
                        item.m_gridPos = cell;   // one step, in the wearer's own inventory: nothing is created or removed
                        inv.Changed();   // direct: Valheim 1.0.21 made it Changed(bool, bool) and Invoke(inv, null) threw
                        Log.LogDebug($"[Magnet] pulled {__state.Name} x{item.m_stack} from {__state.Distance:0.0} m into the bag (cell {from} -> {cell})");
                    }
                }
                catch (Exception e) { Log.LogWarning($"[Magnet] into the bag failed: {e.Message}"); }
            }
        }
    }
}
