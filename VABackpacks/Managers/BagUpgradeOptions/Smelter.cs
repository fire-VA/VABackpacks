// Smelter (tool slot): ore that enters the bag (picked up or vacuumed) is smelted into bars, one bar at a time in pickup order,
// burning Coal ONLY from the upgrade's own fuel pouch (FuelPouch.cs). Each conversion is atomic: one ore and one Coal leave and one
// bar arrives in the same frame, and only when the bar fits. Progress (the ore being smelted + elapsed seconds) is saved on the
// upgrade item, so a relog resumes the bar; it never restarts or doubles it.

using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public sealed class SmelterOption : IBagUpgradeOption
    {
        public const string UpgradeId = "Smelter";
        private const string FuelPrefab = "Coal";
        private const string ProgressKey = "BagUp.Smelter.progress";
        private const char ProgressSeparator = ':';
        private const int FuelPerBar = 1;

        private const float DefaultSecondsPerBar = 30f;   // the vanilla smelter's rate
        private const int DefaultPouchCapacity = 50;
        private const int DefaultLowFuelAt = 5;
        private const float DefaultTickInterval = 0.5f;
        private const float SaveEverySeconds = 2f;

        // ore -> bar, unlocked by tier (T3 adds what vanilla needs the Blast Furnace for)
        private static readonly (string ore, string bar, int tier)[] Ores =
        {
            ("CopperOre", "Copper", 1), ("TinOre", "Tin", 1),
            ("IronScrap", "Iron", 2), ("SilverOre", "Silver", 2),
            ("BlackMetalScrap", "BlackMetal", 3), ("FlametalOreNew", "FlametalNew", 3),
        };
        private static readonly IReadOnlyList<string> Fuel = new[] { FuelPrefab };

        private static ConfigEntry<float> _secondsPerBar, _tickInterval;
        private static ConfigEntry<int> _pouchCapacity, _lowFuelAt;
        private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks.Smelter");

        private static bool _hooked;
        private static float _sinceTick, _sinceSave;
        private static string _lastWait;
        private static readonly HashSet<string> MissingPrefabsLogged = new HashSet<string>();

        public SmelterOption()
        {
            if (_hooked) return;
            _hooked = true;
            FuelPouch.Register(UpgradeId, _ => Fuel, _ => _pouchCapacity?.Value ?? DefaultPouchCapacity, () => _lowFuelAt?.Value ?? DefaultLowFuelAt);
            BagUpgrades.Tick += OnTick;
            BagUpgrades.UpgradeRightClick += OnRightClick;
            BagUpgrades.UpgradeDropOnto += OnDropOnto;
            BagUpgrades.UpgradeBadge += OnBadge;
        }

        public string Id => UpgradeId;
        public string Name => "Smelter";
        public string Slot => BackpackSpecialSlots.SlotTool;
        public string Donor => "SurtlingCore";
        public string[] Recipes => new[]
        {
            "forge,1,Bronze:5,SurtlingCore:4,Stone:20",
            "forge,3,Iron:10,SurtlingCore:6,ElderBark:10",
            "forge,5,BlackMetal:10,SurtlingCore:10,Eitr:5",
        };

        public string Describe(int tier) =>
            $"Smelts {string.Join(", ", Ores.Where(o => o.tier <= tier).Select(o => o.ore))} that enter this bag, burning Coal from its own fuel pouch "
            + $"(1 per bar, {SecondsPerBar:0} s each). Right-click it to open the pouch, or drop Coal on it to fill.";

        public void Bind(ConfigFile cfg, ConfigSync sync)
        {
            FuelPouchHooks.Register();   // pouch fuel goes back to the bag before the upgrade is destroyed
            _secondsPerBar = cfg.Bind(BagUpgrades.Section, "Smelter seconds per bar", DefaultSecondsPerBar,
                new ConfigDescription("Seconds to smelt one bar, at every tier. Server-synced.", new AcceptableValueRange<float>(1f, 600f)));
            _pouchCapacity = cfg.Bind(BagUpgrades.Section, "Smelter fuel pouch capacity", DefaultPouchCapacity,
                new ConfigDescription("How much Coal the Smelter's own fuel pouch holds. Server-synced.", new AcceptableValueRange<int>(1, 1000)));
            _lowFuelAt = cfg.Bind(BagUpgrades.Section, "Smelter low fuel at", DefaultLowFuelAt,
                new ConfigDescription("The slot shows the amber low-fuel state at or below this much Coal. Server-synced.", new AcceptableValueRange<int>(0, 1000)));
            _tickInterval = cfg.Bind(BagUpgrades.Section, "Smelter check interval", DefaultTickInterval,
                new ConfigDescription("Seconds between Smelter checks (lower is smoother, higher is cheaper). Server-synced.", new AcceptableValueRange<float>(0.1f, 5f)));
            sync?.AddConfigEntry(_secondsPerBar); sync?.AddConfigEntry(_pouchCapacity); sync?.AddConfigEntry(_lowFuelAt); sync?.AddConfigEntry(_tickInterval);
        }

        private static float SecondsPerBar => Mathf.Max(1f, _secondsPerBar?.Value ?? DefaultSecondsPerBar);

        private static void OnTick(Player player, float dt)
        {
            _sinceTick += dt; _sinceSave += dt;
            float interval = _tickInterval?.Value ?? DefaultTickInterval;
            if (_sinceTick < interval) return;
            float elapsedNow = _sinceTick;
            _sinceTick = 0f;
            if (player == null || player != Player.m_localPlayer) return;
            int tier = BagUpgrades.WornTier(player, UpgradeId);
            if (tier <= 0) return;
            ItemDrop.ItemData bag = BagUpgrades.WornBagWith(player, UpgradeId);
            ItemDrop.ItemData upgrade = bag != null ? BackpackSpecialSlots.Read(bag, BackpackSpecialSlots.SlotTool) : null;
            FuelPouch pouch = FuelPouch.For(upgrade);
            if (pouch == null) return;

            ReadProgress(upgrade, out string ore, out float elapsed);
            List<ItemDrop.ItemData> items = BagUpgrades.ItemsInBag(player, bag);
            ItemDrop.ItemData oreItem = FindOre(items, ore, tier);
            if (oreItem == null) { Idle(pouch, upgrade, bag, ore); return; }
            string orePrefab = oreItem.m_dropPrefab.name;
            if (orePrefab != ore) elapsed = 0f;   // a new ore starts from zero; the same ore resumes (relog-safe)
            string barPrefab = Ores.First(o => o.ore == orePrefab).bar;

            pouch.WorkingOn = orePrefab;
            if (pouch.CountOf(FuelPrefab) < FuelPerBar)
            {
                pouch.IsWorking = false; pouch.IsWaitingForFuel = true;
                WaitOnce($"waiting for Coal: {orePrefab} in the bag, the pouch is empty");
                WriteProgress(upgrade, bag, orePrefab, elapsed, force: false);
                return;
            }
            pouch.IsWaitingForFuel = false; pouch.IsWorking = true;
            elapsed += elapsedNow;
            pouch.Progress01 = Mathf.Clamp01(elapsed / SecondsPerBar);
            if (elapsed < SecondsPerBar) { WriteProgress(upgrade, bag, orePrefab, elapsed, force: false); return; }

            Inventory inv = BagUpgrades.BagInventory(player, bag);
            GameObject bar = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(barPrefab) : null;
            if (inv == null || bar == null) { LogMissing(barPrefab); return; }
            if (!inv.CanAddItem(bar, 1))
            {
                WaitOnce($"waiting for room: {barPrefab} does not fit in the bag");
                WriteProgress(upgrade, bag, orePrefab, SecondsPerBar, force: false);
                return;
            }
            // atomic: one ore + one Coal out, one bar in, in this frame
            if (!inv.RemoveItem(oreItem, 1)) return;
            if (pouch.TryBurn(FuelPerBar) == null) { inv.AddItem(oreItem.m_dropPrefab, 1); return; }
            inv.AddItem(bar, 1);
            int left = BagUpgrades.ItemsInBag(player, bag).Where(i => i.m_dropPrefab != null && i.m_dropPrefab.name == orePrefab).Sum(i => i.m_stack);
            Log.LogDebug($"[{UpgradeId}] smelted 1 {orePrefab} + {FuelPerBar} Coal -> 1 {barPrefab} (tier {tier}; {left} {orePrefab} left, Coal {pouch.CountOf(FuelPrefab)})");
            _lastWait = null;
            pouch.Progress01 = 0f;
            WriteProgress(upgrade, bag, left > 0 ? orePrefab : null, 0f, force: true);
        }

        // the ore already in progress first (relog), else the first smeltable ore in the bag, in pickup (list) order
        private static ItemDrop.ItemData FindOre(List<ItemDrop.ItemData> items, string current, int tier)
        {
            var smeltable = items.Where(i => i?.m_dropPrefab != null && i.m_stack > 0 && Ores.Any(o => o.ore == i.m_dropPrefab.name && o.tier <= tier)).ToList();
            return smeltable.FirstOrDefault(i => i.m_dropPrefab.name == current) ?? smeltable.FirstOrDefault();
        }

        private static void Idle(FuelPouch pouch, ItemDrop.ItemData upgrade, ItemDrop.ItemData bag, string had)
        {
            pouch.IsWorking = false; pouch.IsWaitingForFuel = false; pouch.WorkingOn = null; pouch.Progress01 = 0f;
            if (had != null) { Log.LogDebug($"[{UpgradeId}] idle: no smeltable ore left in the bag"); WriteProgress(upgrade, bag, null, 0f, force: true); }
        }

        private static void WaitOnce(string why)
        {
            if (why == _lastWait) return;
            _lastWait = why;
            Log.LogDebug($"[{UpgradeId}] {why}");
        }

        private static void ReadProgress(ItemDrop.ItemData upgrade, out string ore, out float elapsed)
        {
            ore = null; elapsed = 0f;
            if (upgrade?.m_customData == null || !upgrade.m_customData.TryGetValue(ProgressKey, out string raw) || string.IsNullOrEmpty(raw)) return;
            int cut = raw.LastIndexOf(ProgressSeparator);
            if (cut <= 0) return;
            ore = raw.Substring(0, cut);
            float.TryParse(raw.Substring(cut + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out elapsed);
        }

        private static void WriteProgress(ItemDrop.ItemData upgrade, ItemDrop.ItemData bag, string ore, float elapsed, bool force)
        {
            if (upgrade == null) return;
            if (upgrade.m_customData == null) upgrade.m_customData = new Dictionary<string, string>();
            string value = ore == null ? "" : ore + ProgressSeparator + elapsed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            upgrade.m_customData.TryGetValue(ProgressKey, out string old);
            if (value == (old ?? "")) return;
            upgrade.m_customData[ProgressKey] = value;
            if (!force && _sinceSave < SaveEverySeconds) return;   // elapsed seconds are saved every few seconds, state changes at once
            _sinceSave = 0f;
            BagUpgrades.SaveUpgradeState(bag, BackpackSpecialSlots.SlotTool);
        }

        private static void LogMissing(string prefab)
        {
            if (MissingPrefabsLogged.Add(prefab)) Log.LogWarning($"[{UpgradeId}] no item prefab '{prefab}' in ObjectDB; that ore is skipped");
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
            int coal = pouch.CountOf(FuelPrefab);
            if (pouch.WorkingOn != null) return ($"{pouch.WorkingOn} · Coal {coal}", coal == 0);
            return ($"Coal {coal}", coal == 0);
        }
    }
}
