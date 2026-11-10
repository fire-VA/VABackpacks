// Kiln (tool slot): turns the wood in the upgrade's own fuel pouch (FuelPouch.cs) into Coal, which goes into the bag, at the vanilla
// charcoal kiln's rate (T3 at double speed). It uses ONLY its pouch's wood, never loose wood in the bag. Each conversion is atomic:
// one wood leaves the pouch and one Coal enters the bag in the same frame, only when the Coal fits. Progress is saved on the upgrade
// item, so a relog resumes it.

using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public sealed class KilnOption : IBagUpgradeOption
    {
        public const string UpgradeId = "Kiln";
        private const string OutputPrefab = "Coal";
        private const string ProgressKey = "BagUp.Kiln.progress";
        private const int WoodPerCoal = 1;

        private const float DefaultSecondsPerCoal = 15f;   // the vanilla charcoal kiln's rate
        private const float DefaultTopTierSpeed = 2f;      // T3 burns at double speed
        private const int DefaultPouchCapacity = 100;
        private const int DefaultLowFuelAt = 10;
        private const float DefaultTickInterval = 0.5f;
        private const float SaveEverySeconds = 2f;

        // the woods each tier takes, in display and burn order
        private static readonly string[][] WoodsByTier =
        {
            new[] { "Wood" },
            new[] { "Wood", "FineWood", "RoundLog" },
            new[] { "Wood", "FineWood", "RoundLog", "YggdrasilWood" },
        };

        private static ConfigEntry<float> _secondsPerCoal, _topTierSpeed, _tickInterval;
        private static ConfigEntry<int> _pouchCapacity, _lowFuelAt;
        private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks.Kiln");

        private static bool _hooked;
        private static float _sinceTick, _sinceSave;
        private static string _lastWait;

        public KilnOption()
        {
            if (_hooked) return;
            _hooked = true;
            FuelPouch.Register(UpgradeId, WoodsFor, _ => _pouchCapacity?.Value ?? DefaultPouchCapacity, () => _lowFuelAt?.Value ?? DefaultLowFuelAt);
            BagUpgrades.Tick += OnTick;
            BagUpgrades.UpgradeRightClick += OnRightClick;
            BagUpgrades.UpgradeDropOnto += OnDropOnto;
            BagUpgrades.UpgradeBadge += OnBadge;
        }

        public string Id => UpgradeId;
        public string Name => "Kiln";
        public string Slot => BackpackSpecialSlots.SlotTool;
        public string Donor => "Coal";
        public string[] Recipes => new[]
        {
            "forge,1,Bronze:5,SurtlingCore:2,Stone:20",
            "forge,3,Iron:5,SurtlingCore:4,ElderBark:10",
            "forge,5,BlackMetal:5,SurtlingCore:6,YggdrasilWood:10",
        };

        public string Describe(int tier) =>
            $"Turns {string.Join(", ", WoodsFor(tier))} in its own fuel pouch into Coal in this bag ({SecondsPerCoal(tier):0.#} s each)."
            + " Right-click it to open the pouch, or drop wood on it to fill.";

        public void Bind(ConfigFile cfg, ConfigSync sync)
        {
            FuelPouchHooks.Register();   // pouch fuel goes back to the bag before the upgrade is destroyed
            _secondsPerCoal = cfg.Bind(BagUpgrades.Section, "Kiln seconds per coal", DefaultSecondsPerCoal,
                new ConfigDescription("Seconds to turn one wood into one Coal (tiers I and II). Server-synced.", new AcceptableValueRange<float>(1f, 600f)));
            _topTierSpeed = cfg.Bind(BagUpgrades.Section, "Kiln III speed", DefaultTopTierSpeed,
                new ConfigDescription("How much faster tier III works (2 = double speed). Server-synced.", new AcceptableValueRange<float>(1f, 10f)));
            _pouchCapacity = cfg.Bind(BagUpgrades.Section, "Kiln fuel pouch capacity", DefaultPouchCapacity,
                new ConfigDescription("How much wood the Kiln's own pouch holds, all wood types together. Server-synced.", new AcceptableValueRange<int>(1, 1000)));
            _lowFuelAt = cfg.Bind(BagUpgrades.Section, "Kiln low fuel at", DefaultLowFuelAt,
                new ConfigDescription("The slot shows the amber low-fuel state at or below this much wood. Server-synced.", new AcceptableValueRange<int>(0, 1000)));
            _tickInterval = cfg.Bind(BagUpgrades.Section, "Kiln check interval", DefaultTickInterval,
                new ConfigDescription("Seconds between Kiln checks. Server-synced.", new AcceptableValueRange<float>(0.1f, 5f)));
            sync?.AddConfigEntry(_secondsPerCoal); sync?.AddConfigEntry(_topTierSpeed); sync?.AddConfigEntry(_pouchCapacity);
            sync?.AddConfigEntry(_lowFuelAt); sync?.AddConfigEntry(_tickInterval);
        }

        private static IReadOnlyList<string> WoodsFor(int tier) => WoodsByTier[Mathf.Clamp(tier, 1, BagUpgrades.TierCount) - 1];

        private static float SecondsPerCoal(int tier)
        {
            float seconds = Mathf.Max(1f, _secondsPerCoal?.Value ?? DefaultSecondsPerCoal);
            return tier >= BagUpgrades.TierCount ? seconds / Mathf.Max(1f, _topTierSpeed?.Value ?? DefaultTopTierSpeed) : seconds;
        }

        private static void OnTick(Player player, float dt)
        {
            _sinceTick += dt; _sinceSave += dt;
            if (_sinceTick < (_tickInterval?.Value ?? DefaultTickInterval)) return;
            float elapsedNow = _sinceTick;
            _sinceTick = 0f;
            if (player == null || player != Player.m_localPlayer) return;
            int tier = BagUpgrades.WornTier(player, UpgradeId);
            if (tier <= 0) return;
            ItemDrop.ItemData bag = BagUpgrades.WornBagWith(player, UpgradeId);
            ItemDrop.ItemData upgrade = bag != null ? BackpackSpecialSlots.Read(bag, BackpackSpecialSlots.SlotTool) : null;
            FuelPouch pouch = FuelPouch.For(upgrade);
            if (pouch == null) return;

            string wood = pouch.NextFuel();
            if (wood == null)
            {
                pouch.IsWorking = false; pouch.IsWaitingForFuel = true; pouch.WorkingOn = null; pouch.Progress01 = 0f;
                WaitOnce("waiting for wood: the pouch is empty");
                return;
            }
            pouch.IsWaitingForFuel = false; pouch.IsWorking = true; pouch.WorkingOn = wood;
            float elapsed = ReadProgress(upgrade) + elapsedNow;
            float needed = SecondsPerCoal(tier);
            pouch.Progress01 = Mathf.Clamp01(elapsed / needed);
            if (elapsed < needed) { WriteProgress(upgrade, bag, elapsed, force: false); return; }

            Inventory inv = BagUpgrades.BagInventory(player, bag);
            GameObject coal = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(OutputPrefab) : null;
            if (inv == null || coal == null) return;
            if (!inv.CanAddItem(coal, 1))
            {
                WaitOnce("waiting for room: Coal does not fit in the bag");
                WriteProgress(upgrade, bag, needed, force: false);
                return;
            }
            // atomic: one wood out of the pouch, one Coal into the bag, in this frame
            string burned = pouch.TryBurn(WoodPerCoal);
            if (burned == null) return;
            inv.AddItem(coal, 1);
            Log.LogDebug($"[{UpgradeId}] burned {WoodPerCoal} {burned} -> 1 Coal (tier {tier}, {needed:0.#} s; pouch {pouch.Count} wood left)");
            _lastWait = null;
            pouch.Progress01 = 0f;
            WriteProgress(upgrade, bag, 0f, force: true);
        }

        private static void WaitOnce(string why)
        {
            if (why == _lastWait) return;
            _lastWait = why;
            Log.LogDebug($"[{UpgradeId}] {why}");
        }

        private static float ReadProgress(ItemDrop.ItemData upgrade)
        {
            if (upgrade?.m_customData == null || !upgrade.m_customData.TryGetValue(ProgressKey, out string raw)) return 0f;
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float s) ? s : 0f;
        }

        private static void WriteProgress(ItemDrop.ItemData upgrade, ItemDrop.ItemData bag, float elapsed, bool force)
        {
            if (upgrade == null) return;
            if (upgrade.m_customData == null) upgrade.m_customData = new Dictionary<string, string>();
            upgrade.m_customData[ProgressKey] = elapsed.ToString("0.0", CultureInfo.InvariantCulture);
            if (!force && _sinceSave < SaveEverySeconds) return;   // elapsed seconds every few seconds, a finished coal at once
            _sinceSave = 0f;
            BagUpgrades.SaveUpgradeState(bag, BackpackSpecialSlots.SlotTool);
        }

        private static bool OnRightClick(ItemDrop.ItemData bag, string kind, ItemDrop.ItemData upgrade)
        {
            FuelPouch pouch = FuelPouch.For(upgrade);
            if (pouch == null || pouch.UpgradeId != UpgradeId) return false;
            pouch.RequestOpen();
            return true;
        }

        // the framework passes (bag, kind, dropped, from); the upgrade is the one slotted in that bag's slot
        private static bool OnDropOnto(ItemDrop.ItemData bag, string kind, ItemDrop.ItemData dropped, Inventory from)
        {
            FuelPouch pouch = FuelPouch.For(BackpackSpecialSlots.Read(bag, kind));
            if (pouch == null || pouch.UpgradeId != UpgradeId) return false;
            return FuelPouchHooks.FillFromSlotDrop(pouch, dropped, from);   // wrong item or full = refused the vanilla way (never a swap)
        }

        private static (string text, bool warn)? OnBadge(ItemDrop.ItemData upgrade)
        {
            FuelPouch pouch = FuelPouch.For(upgrade);
            if (pouch == null || pouch.UpgradeId != UpgradeId) return null;
            return ($"Wood {pouch.Count}", pouch.IsEmpty);
        }
    }
}
