// Trophy Pouch (bypass slot): trophies and boss items stack to the tier's size and weigh nothing in this bag's cells.
//   stacking : ONLY in the worn bag's cells. The shared m_maxStackSize is never touched, so nothing stacks bigger anywhere else
//              ([lead] 10-23). VAInventory asks BagCellStackCap for a bag cell, the same per-cell way its coin purse stacks past the
//              vanilla cap (CoinPurseStackPatch: AddItem/CanAddItem merge, Save/Load persistence since vanilla Load clamps a stack to
//              its max, resync after RemoveItem). Until VAInventory calls it, the pouch only does the weight.
//   weight   : an item of those kinds that is in the worn bag's cells weighs 0 (ItemData.GetWeight), so carrying them costs nothing.
// Local player only; refreshed on a throttled Tick.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public sealed class TrophyPouchOption : IBagUpgradeOption
    {
        public const string UpgradeId = "TrophyPouch";
        private static readonly int[] StackDefaults = { 10, 25, 50 };
        private const string BossItemsDefault = "HardAntler,CryptKey,Wishbone,DragonTear,YagluthDrop,QueenDrop,FaderDrop";
        private const float RefreshSeconds = 0.5f;
        private static readonly ConfigEntry<int>[] StackSize = new ConfigEntry<int>[BagUpgrades.TierCount];
        private static ConfigEntry<string> s_bossItems;
        internal static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks.TrophyPouch");

        public string Id => UpgradeId;
        public string Name => "Trophy Pouch";
        public string Slot => BackpackSpecialSlots.SlotBypass;
        public string Donor => "TrophyDeer";
        public string[] Recipes => new[]
        {
            "forge,1,Bronze:5,LeatherScraps:10,TrophyDeer:3",
            "forge,3,Iron:10,WolfPelt:5,TrophyWolf:2",
            "forge,5,BlackMetal:10,LinenThread:5,TrophyLox:1",
        };

        public string Describe(int tier) => $"Trophies and boss items stack to {SizeFor(tier)} and weigh nothing in this bag.";

        public void Bind(ConfigFile cfg, ConfigSync sync)
        {
            for (int t = 0; t < BagUpgrades.TierCount; t++)
            {
                StackSize[t] = cfg.Bind(BagUpgrades.Section, $"Trophy Pouch {BagUpgrades.TierNumerals[t]} stack size", StackDefaults[t],
                    new ConfigDescription($"Trophies and boss items stack to this size in the cells of a bag with Trophy Pouch {BagUpgrades.TierNumerals[t]}. Server-synced.",
                        new AcceptableValueRange<int>(1, 999)));
                sync?.AddConfigEntry(StackSize[t]);
            }
            s_bossItems = cfg.Bind(BagUpgrades.Section, "Trophy Pouch boss items", BossItemsDefault,
                "Item prefabs (comma-separated) the Trophy Pouch treats like trophies, besides every item of type Trophy. Server-synced.");
            sync?.AddConfigEntry(s_bossItems);
            BagUpgrades.Tick += OnTick;
        }

        private static int SizeFor(int tier) => tier < 1 ? 0 : Mathf.Max(1, StackSize[tier - 1]?.Value ?? StackDefaults[tier - 1]);

        private static HashSet<string> BossItems() => new HashSet<string>((s_bossItems?.Value ?? BossItemsDefault)
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0), StringComparer.Ordinal);

        internal static bool IsPouchItem(ItemDrop.ItemData item, HashSet<string> boss) =>
            item?.m_shared != null && (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Trophy
                                       || (item.m_dropPrefab != null && boss.Contains(item.m_dropPrefab.name)));

        private static int s_wornTier;
        private static float s_nextRefresh;
        private static readonly HashSet<ItemDrop.ItemData> s_inBag = new HashSet<ItemDrop.ItemData>();
        private static HashSet<string> s_boss = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// For VAInventory: the stack cap an item may reach in a cell of <paramref name="bag"/> worn by the local player, or 0 when the
        /// Trophy Pouch does not apply (not slotted in that bag, or not a trophy / boss item); then the vanilla cap stands. Never lower
        /// than the item's own cap.
        /// </summary>
        public static int BagCellStackCap(Player player, ItemDrop.ItemData bag, ItemDrop.ItemData item)
        {
            if (player == null || player != Player.m_localPlayer || bag == null || item?.m_shared == null) return 0;
            int tier = BagUpgrades.SlottedTier(bag, UpgradeId);
            if (tier < 1 || !IsPouchItem(item, s_boss.Count > 0 ? s_boss : BossItems())) return 0;
            return Mathf.Max(item.m_shared.m_maxStackSize, SizeFor(tier));
        }

        private static void OnTick(Player player, float dt)
        {
            if (Time.time < s_nextRefresh) return;
            s_nextRefresh = Time.time + RefreshSeconds;
            int tier = BagUpgrades.WornTier(player, UpgradeId);
            s_boss = BossItems();
            if (tier != s_wornTier)
            {
                Log.LogDebug(tier > 0
                    ? $"[TrophyPouch] tier {BagUpgrades.TierNumerals[tier - 1]} worn: trophies / boss items in the bag's cells stack to {SizeFor(tier)} and weigh nothing"
                    : "[TrophyPouch] off: trophies / boss items stack and weigh as normal");
                s_wornTier = tier;
            }
            s_inBag.Clear();
            if (tier > 0)
            {
                ItemDrop.ItemData bag = BagUpgrades.WornBagWith(player, UpgradeId);
                foreach (ItemDrop.ItemData it in BagUpgrades.ItemsInBag(player, bag)) if (IsPouchItem(it, s_boss)) s_inBag.Add(it);
            }
        }

        // weight: nothing, in the worn bag's cells
        [HarmonyPatch]
        private static class Weightless
        {
            private static IEnumerable<MethodBase> TargetMethods() => AccessTools.GetDeclaredMethods(typeof(ItemDrop.ItemData)).Where(m => m.Name == "GetWeight").Cast<MethodBase>();
            private static void Postfix(ItemDrop.ItemData __instance, ref float __result)
            {
                if (s_wornTier > 0 && __result > 0f && s_inBag.Contains(__instance)) __result = 0f;
            }
        }
    }
}
