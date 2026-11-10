// Featherweight (resources slot): everything carried in this bag's cells weighs less. It feeds BackpackApi.GetBonus' contents-weight
// factor, which VAInventory applies to the bag's cells (and its tooltips show the reduced weight).

using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public sealed class FeatherweightOption : IBagUpgradeOption
    {
        public const string UpgradeId = "Featherweight";
        private static readonly float[] Defaults = { 0.75f, 0.5f, 0.25f };
        private static readonly ConfigEntry<float>[] Factor = new ConfigEntry<float>[BagUpgrades.TierCount];

        public string Id => UpgradeId;
        public string Name => "Featherweight";
        public string Slot => BackpackSpecialSlots.SlotResources;
        public string Donor => "Feathers";
        public string[] Recipes => new[]
        {
            "forge,1,Bronze:5,LeatherScraps:10,Feathers:5",
            "forge,3,Iron:10,WolfPelt:5,Feathers:10",
            "forge,5,BlackMetal:10,LinenThread:5,Feathers:15",
        };

        public string Describe(int tier) => $"Everything carried in this bag's cells weighs {Mathf.RoundToInt((1f - FactorFor(tier)) * 100f)}% less.";

        public void Bind(ConfigFile cfg, ConfigSync sync)
        {
            for (int t = 0; t < BagUpgrades.TierCount; t++)
            {
                Factor[t] = cfg.Bind(BagUpgrades.Section, $"Featherweight {BagUpgrades.TierNumerals[t]} weight factor", Defaults[t],
                    new ConfigDescription($"Items in a bag with Featherweight {BagUpgrades.TierNumerals[t]} weigh this share of their weight. Server-synced.", new AcceptableValueRange<float>(0f, 1f)));
                sync?.AddConfigEntry(Factor[t]);
            }
        }

        private static float FactorFor(int tier) => tier < 1 ? 1f : Mathf.Clamp01(Factor[tier - 1]?.Value ?? Defaults[tier - 1]);

        /// <summary>The share of their weight the items in this bag's cells keep (1 = no Featherweight slotted).</summary>
        internal static float ContentsWeightFactor(ItemDrop.ItemData bag) => FactorFor(BagUpgrades.SlottedTier(bag, UpgradeId));
    }
}
