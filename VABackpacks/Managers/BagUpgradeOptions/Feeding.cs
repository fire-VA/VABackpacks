// Feeding (resources slot): when one of the wearer's food slots is empty, the bag feeds them the next food they picked for it (1, 2 or
// 3 foods by tier), taken from the bag's own cells. Never in combat (attacking, blocking, or hurt a moment ago), never while dead,
// sleeping or teleporting. The picked foods are saved on the upgrade item ("BagUp.Feeding.foods" = prefab names, '|'), so they
// travel with the bag. Pick them with the picker (right-click the slotted upgrade) or the console: bagup_feeding <food> [food] [food].

using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public sealed class FeedingOption : IBagUpgradeOption
    {
        public const string UpgradeId = "Feeding";
        public const string FoodsKey = "BagUp.Feeding.foods";
        private const char FoodSeparator = '|';
        private const int MaxFoodSlots = 3;                 // Valheim lets a player have three foods at once
        private static readonly int[] DefaultChoices = { 1, 2, 3 };
        private const float DefaultCheckInterval = 1f;
        private const float DefaultCombatDelay = 5f;

        private static readonly ConfigEntry<int>[] Choices = new ConfigEntry<int>[BagUpgrades.TierCount];
        private static ConfigEntry<float> s_checkInterval, s_combatDelay;
        private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks." + UpgradeId);
        private static float s_nextCheck, s_lastHurt = -999f;
        private static bool s_hooked;

        public FeedingOption()
        {
            if (s_hooked) return;
            s_hooked = true;
            BagUpgrades.Tick += OnTick;
            BagUpgrades.UpgradeBadge += Badge;
            BagUpgrades.UpgradeRightClick += OnRightClick;
            new Terminal.ConsoleCommand("bagup_feeding", "[food] [food] [food] - the foods your worn bag's Feedbag upgrade feeds you (no foods = clear)", args => SetFromConsole(args));
        }

        public string Id => UpgradeId;
        public string Name => "Feedbag";   // display name only (Fire 10-23); the id, item prefab, config keys and save key stay "Feeding"
        public string Slot => BackpackSpecialSlots.SlotResources;
        public string Donor => "Honey";
        public string[] Recipes => new[]
        {
            "forge,1,Bronze:5,LeatherScraps:10,Honey:5",
            "forge,3,Iron:10,WolfPelt:5,Honey:10",
            "forge,5,BlackMetal:10,LinenThread:5,Honey:15",
        };

        public string Describe(int tier)
        {
            int n = ChoicesFor(tier);
            return $"When a food slot is empty, the bag feeds you {(n == 1 ? "the food" : $"the next of up to {n} foods")} you picked for it, from the bag, never in combat. Right-click it in the bag to pick.";
        }

        public void Bind(ConfigFile cfg, ConfigSync sync)
        {
            for (int t = 0; t < BagUpgrades.TierCount; t++)
            {
                Choices[t] = cfg.Bind(BagUpgrades.Section, $"Feeding {BagUpgrades.TierNumerals[t]} food choices", DefaultChoices[t],
                    new ConfigDescription($"How many foods a bag with Feedbag {BagUpgrades.TierNumerals[t]} can be set to feed. Server-synced.", new AcceptableValueRange<int>(1, MaxFoodSlots)));
                sync?.AddConfigEntry(Choices[t]);
            }
            s_checkInterval = cfg.Bind(BagUpgrades.Section, "Feeding check interval", DefaultCheckInterval,
                new ConfigDescription("Seconds between checks for an empty food slot. Server-synced.", new AcceptableValueRange<float>(0.25f, 10f)));
            sync?.AddConfigEntry(s_checkInterval);
            s_combatDelay = cfg.Bind(BagUpgrades.Section, "Feeding combat delay", DefaultCombatDelay,
                new ConfigDescription("Seconds after taking damage before the bag feeds you again (it never feeds in combat). Server-synced.", new AcceptableValueRange<float>(0f, 60f)));
            sync?.AddConfigEntry(s_combatDelay);
        }

        internal static int ChoicesFor(int tier) => tier < 1 ? 0 : Mathf.Clamp(Choices[tier - 1]?.Value ?? DefaultChoices[tier - 1], 1, MaxFoodSlots);

        // ── the picked foods (saved on the upgrade item) ─────────────────────────────────────────────────────────────

        /// <summary>The food prefab names picked on this upgrade, in order (the picker window reads and writes these).</summary>
        public static List<string> GetFoods(ItemDrop.ItemData upgrade)
        {
            if (upgrade?.m_customData == null || !upgrade.m_customData.TryGetValue(FoodsKey, out string raw) || string.IsNullOrEmpty(raw)) return new List<string>();
            return raw.Split(FoodSeparator).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        }

        /// <summary>Sets the picked foods (cut to the tier's number of choices) and saves the bag's slot.</summary>
        public static void SetFoods(ItemDrop.ItemData bag, ItemDrop.ItemData upgrade, IEnumerable<string> foods, int tier)
        {
            if (upgrade == null) return;
            var list = (foods ?? Enumerable.Empty<string>()).Where(IsFood).Distinct().Take(ChoicesFor(tier)).ToList();
            if (upgrade.m_customData == null) upgrade.m_customData = new Dictionary<string, string>();
            if (list.Count == 0) upgrade.m_customData.Remove(FoodsKey);
            else upgrade.m_customData[FoodsKey] = string.Join(FoodSeparator.ToString(), list.ToArray());
            BagUpgrades.SaveUpgradeState(bag, BackpackSpecialSlots.SlotResources);
            Log.LogDebug($"[{UpgradeId}] foods set: {(list.Count == 0 ? "none" : string.Join(", ", list.ToArray()))}");
        }

        /// <summary>True for a prefab whose item is something a player can eat (it fills a food slot).</summary>
        public static bool IsFood(string prefab)
        {
            GameObject go = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null;
            ItemDrop.ItemData.SharedData s = go != null ? go.GetComponent<ItemDrop>()?.m_itemData?.m_shared : null;
            return s != null && s.m_itemType == ItemDrop.ItemData.ItemType.Consumable && (s.m_food > 0f || s.m_foodStamina > 0f || s.m_foodEitr > 0f);
        }

        private static ItemDrop.ItemData MyUpgrade(Player me, out ItemDrop.ItemData bag)
        {
            bag = BagUpgrades.WornBagWith(me, UpgradeId);
            return bag != null ? BackpackSpecialSlots.Read(bag, BackpackSpecialSlots.SlotResources) : null;
        }

        // ── feeding ─────────────────────────────────────────────────────────────────────────────────────────────────

        private static void OnTick(Player me, float dt)
        {
            if (me == null || me != Player.m_localPlayer || Time.time < s_nextCheck) return;
            s_nextCheck = Time.time + (s_checkInterval?.Value ?? DefaultCheckInterval);
            int tier = BagUpgrades.WornTier(me, UpgradeId);
            if (tier == 0 || me.IsDead() || me.InAttack() || me.IsBlocking() || me.IsSleeping() || me.IsTeleporting()) return;
            if (Time.time - s_lastHurt < (s_combatDelay?.Value ?? DefaultCombatDelay)) return;
            List<Player.Food> eaten = me.GetFoods();
            if (eaten.Count >= MaxFoodSlots) return;
            ItemDrop.ItemData upgrade = MyUpgrade(me, out ItemDrop.ItemData bag);
            List<string> foods = GetFoods(upgrade).Take(ChoicesFor(tier)).ToList();
            if (foods.Count == 0) return;
            Inventory inv = BagUpgrades.BagInventory(me, bag);
            if (inv == null) return;
            List<ItemDrop.ItemData> inBag = BagUpgrades.ItemsInBag(me, bag);
            foreach (string food in foods)
            {
                ItemDrop.ItemData item = inBag.FirstOrDefault(i => i?.m_dropPrefab != null && i.m_dropPrefab.name == food);
                if (item == null) continue;
                if (eaten.Any(f => f.m_item != null && f.m_item.m_shared.m_name == item.m_shared.m_name)) continue;   // already in a slot
                if (!me.CanEat(item, false)) continue;
                int before = inBag.Where(i => i?.m_dropPrefab != null && i.m_dropPrefab.name == food).Sum(i => i.m_stack);
                if (!me.ConsumeItem(inv, item)) continue;
                Log.LogDebug($"[{UpgradeId}] fed {food} (food slots {eaten.Count}/{MaxFoodSlots} before; {before - 1} left in the bag)");
                return;   // one food per check: the next empty slot fills on the next check
            }
        }

        // the last time the local player took damage: no feeding in combat
        [HarmonyPatch(typeof(Character), "ApplyDamage")]
        private static class Hurt_Patch
        {
            private static void Postfix(Character __instance)
            {
                if (__instance != null && __instance == Player.m_localPlayer) s_lastHurt = Time.time;
            }
        }

        // ── the rail: badge and right-click ───────────────────────────────────────────────────────────────────────

        private static (string text, bool warn)? Badge(ItemDrop.ItemData upgrade)
        {
            if (upgrade?.m_dropPrefab == null || !upgrade.m_dropPrefab.name.StartsWith(BagUpgrades.PrefabPrefix + UpgradeId + "_", StringComparison.Ordinal)) return null;
            Player me = Player.m_localPlayer;
            List<string> foods = GetFoods(upgrade);
            if (foods.Count == 0) return ("pick food", true);
            ItemDrop.ItemData bag = me != null ? BagUpgrades.WornBagWith(me, UpgradeId) : null;
            int meals = bag == null ? 0 : BagUpgrades.ItemsInBag(me, bag).Where(i => i?.m_dropPrefab != null && foods.Contains(i.m_dropPrefab.name)).Sum(i => i.m_stack);
            return (meals.ToString(), meals == 0);
        }

        /// <summary>Set by the picker window when it exists ([classes-ui]); until then the right-click explains the console command.</summary>
        public static Action<ItemDrop.ItemData, ItemDrop.ItemData, int> OpenPicker;

        private static bool OnRightClick(ItemDrop.ItemData bag, string kind, ItemDrop.ItemData upgrade)
        {
            if (kind != BackpackSpecialSlots.SlotResources || upgrade?.m_dropPrefab == null || !upgrade.m_dropPrefab.name.StartsWith(BagUpgrades.PrefabPrefix + UpgradeId + "_", StringComparison.Ordinal)) return false;
            int tier = BagUpgrades.SlottedTier(bag, UpgradeId);
            if (OpenPicker != null) { OpenPicker(bag, upgrade, tier); return true; }
            List<string> foods = GetFoods(upgrade);
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                $"Feedbag: {(foods.Count == 0 ? "no food picked" : string.Join(", ", foods.ToArray()))}. Type bagup_feeding <food> (up to {ChoicesFor(tier)}) in the console to pick.");
            return true;
        }

        private static void SetFromConsole(Terminal.ConsoleEventArgs args)
        {
            Player me = Player.m_localPlayer;
            ItemDrop.ItemData upgrade = MyUpgrade(me, out ItemDrop.ItemData bag);
            if (upgrade == null) { args.Context?.AddString("No worn bag has a Feedbag upgrade."); return; }
            var asked = args.Args.Skip(1).ToList();
            var bad = asked.Where(a => !IsFood(a)).ToList();
            int tier = BagUpgrades.SlottedTier(bag, UpgradeId);
            SetFoods(bag, upgrade, asked, tier);
            List<string> now = GetFoods(upgrade);
            args.Context?.AddString($"Feedbag {BagUpgrades.TierNumerals[Mathf.Clamp(tier, 1, BagUpgrades.TierCount) - 1]}: {(now.Count == 0 ? "no food" : string.Join(", ", now.ToArray()))}"
                + (bad.Count > 0 ? $" (not a food: {string.Join(", ", bad.ToArray())})" : "")
                + (asked.Count - bad.Count > now.Count ? $" (this tier takes {ChoicesFor(tier)})" : ""));
        }
    }
}
