// Compactor (resources slot): items in this bag's cells stack higher (x1.5 / x2 / x3 by tier, synced config). Only items that stack
// at all (vanilla max > 1); tools and armour are untouched. VAInventory's BagStackCap applies the cap in the bag cells and keeps
// every item safe when it leaves the bag or the cap goes away: it asks BackpackApi.BagCellStackCap, which uses BagCellStackCap here.

using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public sealed class CompactorOption : IBagUpgradeOption
    {
        public const string UpgradeId = "Compactor";
        private static readonly float[] Defaults = { 1.5f, 2f, 3f };
        private const float MaxFactor = 10f;
        private static readonly ConfigEntry<float>[] Factor = new ConfigEntry<float>[BagUpgrades.TierCount];
        private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks." + UpgradeId);
        private static int s_lastTier = -1;

        public string Id => UpgradeId;
        public string Name => "Compactor";
        public string Slot => BackpackSpecialSlots.SlotResources;
        public string Donor => "Resin";
        public string[] Recipes => new[]
        {
            "forge,1,Bronze:5,LeatherScraps:10,Resin:10",
            "forge,3,Iron:10,WolfPelt:5,Resin:20",
            "forge,5,BlackMetal:10,LinenThread:5,Resin:30",
        };

        public string Describe(int tier) => $"Items in this bag's cells stack {FactorFor(tier):0.#}x as high (stacking items only).";

        public void Bind(ConfigFile cfg, ConfigSync sync)
        {
            for (int t = 0; t < BagUpgrades.TierCount; t++)
            {
                Factor[t] = cfg.Bind(BagUpgrades.Section, $"Compactor {BagUpgrades.TierNumerals[t]} stack factor", Defaults[t],
                    new ConfigDescription($"Stacks in a bag with Compactor {BagUpgrades.TierNumerals[t]} hold this many times their usual size. Server-synced.", new AcceptableValueRange<float>(1f, MaxFactor)));
                sync?.AddConfigEntry(Factor[t]);
            }
        }

        private static float FactorFor(int tier) => tier < 1 ? 1f : Mathf.Clamp(Factor[tier - 1]?.Value ?? Defaults[tier - 1], 1f, MaxFactor);

        /// <summary>The stack cap for an item in this bag's cells (0 = no Compactor, vanilla rules). BackpackApi.BagCellStackCap uses it.</summary>
        public static int BagCellStackCap(Player p, ItemDrop.ItemData bag, ItemDrop.ItemData item)
        {
            if (bag == null || item == null || item.m_shared.m_maxStackSize <= 1) return 0;
            int tier = BagUpgrades.SlottedTier(bag, UpgradeId);
            if (p == Player.m_localPlayer && tier != s_lastTier)
            {
                s_lastTier = tier;
                Log.LogDebug($"[{UpgradeId}] bag {(bag.m_dropPrefab != null ? bag.m_dropPrefab.name : "?")}: {(tier == 0 ? "no Compactor" : $"Compactor {BagUpgrades.TierNumerals[tier - 1]}, stacks x{FactorFor(tier):0.#}")}");
            }
            return tier == 0 ? 0 : Mathf.RoundToInt(item.m_shared.m_maxStackSize * FactorFor(tier));
        }
    }
}
