// Remote Access (resources slot): opens your home storage from far away and lets the Hopper and Refill reach it. The tier is the reach:
// the horizontal distance to the home Storage Terminal (the last one with the Remote Rune you opened). The server checks it again.

using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public sealed class RemoteAccessOption : IBagUpgradeOption
    {
        public const string UpgradeId = "RemoteAccess";
        private static readonly float[] DefaultRange = { 1000f, 5000f, 7500f };
        private static readonly ConfigEntry<float>[] Range = new ConfigEntry<float>[BagUpgrades.TierCount];
        private static ConfigEntry<KeyboardShortcut> s_openKey;

        public string Id => UpgradeId;
        public string Name => "Remote Access";
        public string Slot => BackpackSpecialSlots.SlotResources;
        public string Donor => "SurtlingCore";
        public string[] Recipes => new[]
        {
            "forge,2,Bronze:5,SurtlingCore:1,FineWood:5",
            "forge,4,Silver:5,SurtlingCore:2,Crystal:2",
            "blackforge,1,BlackMetal:5,Eitr:2,SurtlingCore:3",
        };

        public string Describe(int tier) => $"Reach your home storage from up to {RangeFor(tier):0} m away (right-click it in the bag's slot to open, or press {KeyText()}).";

        public void Bind(ConfigFile cfg, ConfigSync sync)
        {
            for (int t = 0; t < BagUpgrades.TierCount; t++)
            {
                Range[t] = cfg.Bind(BagUpgrades.Section, $"Remote Access {BagUpgrades.TierNumerals[t]} range", DefaultRange[t],
                    new ConfigDescription($"How far (horizontal metres) Remote Access {BagUpgrades.TierNumerals[t]} reaches your home Storage Terminal. Server-synced.", new AcceptableValueRange<float>(0f, 20000f)));
                sync?.AddConfigEntry(Range[t]);
            }
            s_openKey = cfg.Bind(BagUpgrades.Section, "Remote Access open key", new KeyboardShortcut(KeyCode.Home), "Opens your home storage when a bag with Remote Access is worn.");
            BagUpgrades.Tick += OnTick;
            // Fire: open it by right-clicking the slotted upgrade (the key stays as a shortcut)
            BagUpgrades.UpgradeRightClick += (bag, kind, upgrade) =>
            {
                if (BagUpgrades.SlotOf(upgrade) != Slot || !upgrade.m_dropPrefab.name.StartsWith(BagUpgrades.PrefabPrefix + UpgradeId + "_", System.StringComparison.Ordinal)) return false;
                Open(Player.m_localPlayer);
                return true;
            };
        }

        internal static float RangeFor(int tier) => tier < 1 ? 0f : Range[tier - 1]?.Value ?? DefaultRange[tier - 1];

        /// <summary>The wearer's Remote Access reach in metres, 0 without one.</summary>
        internal static float WornRange(Player p) => RangeFor(BagUpgrades.WornTier(p, UpgradeId));

        private static string KeyText() => s_openKey != null ? s_openKey.Value.ToString() : "Home";

        private static void OnTick(Player p, float dt)
        {
            if (p != Player.m_localPlayer || s_openKey == null || !s_openKey.Value.IsDown()) return;
            if (Chat.instance != null && Chat.instance.HasFocus() || Console.IsVisible() || Menu.IsVisible() || InventoryGui.IsVisible()) return;
            Open(p);
        }

        /// <summary>Opens the home storage window if this bag reaches it (the Send home / panel buttons can call this too).</summary>
        public static void Open(Player p)
        {
            float range = WornRange(p);
            if (range <= 0f) return;
            string why = RemoteAccessBridge.Refusal(p, range);
            if (why != null) { p.Message(MessageHud.MessageType.Center, "Remote Access: " + why); return; }
            RemoteAccessBridge.OpenTerminal(p, range);
        }
    }
}
