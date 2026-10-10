using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// 1.0.18 (Fire 10-07): every backpack has a crafting recipe of its biome tier, and the three shoulder bags can turn up in
    /// the vanilla location chests of their biome at a low chance.
    /// Recipes: one config string per bag, "station,level,Item:amount,Item:amount,..." (station is a CraftingStation prefab such
    /// as piece_workbench or forge). [General] EnableRecipes switches them all; an empty string leaves that bag uncraftable.
    /// Loot: the bag joins the chest prefab's own DropTable (Container.m_defaultItems) with a weight worked out from that table,
    /// so it shows up in about one chest in N of that biome whatever the chest's other loot; vanilla rolls it, nothing is saved,
    /// and Core's location loot refresh refills it like any other drop. [Loot] One chest in = 0 turns it off. The camp bags
    /// (HoboStick, StaffFiresWizard) are craftable but never chest loot.
    /// </summary>
    internal static class BackpackCraftingAndLoot
    {
        private sealed class BagDefaults
        {
            internal string Prefab, Recipe;
            internal string[] ChestKeys;   // chest prefab name fragments for its biome; null = never chest loot
        }

        private static readonly BagDefaults[] Bags =
        {
            new BagDefaults { Prefab = "SmallBag_va", Recipe = "piece_workbench,1,LeatherScraps:6,DeerHide:4,Resin:2",
                ChestKeys = new[] { "meadows" } },
            new BagDefaults { Prefab = "DayBag_va", Recipe = "piece_workbench,2,TrollHide:4,LeatherScraps:10,BronzeNails:10",
                ChestKeys = new[] { "blackforest", "forestcrypt", "trollcave" } },
            new BagDefaults { Prefab = "ExplorerBag_va", Recipe = "piece_workbench,3,WolfPelt:4,Iron:4,TrollHide:4,Silver:2",
                ChestKeys = new[] { "swamp", "sunkencrypt", "mountain" } },
            new BagDefaults { Prefab = "HoboStick", Recipe = "piece_workbench,2,Wood:10,LeatherScraps:8,DeerHide:4,Resin:6" },
            new BagDefaults { Prefab = "StaffFiresWizard", Recipe = "piece_workbench,3,FineWood:10,Bronze:4,TrollHide:3,GreydwarfEye:8" },
        };

        private const string ChestPrefix = "TreasureChest_";
        private static readonly Dictionary<string, ConfigEntry<string>> RecipeConfig = new Dictionary<string, ConfigEntry<string>>();
        private static readonly Dictionary<string, ConfigEntry<string>> UpgradeConfig = new Dictionary<string, ConfigEntry<string>>();
        private static ConfigEntry<int> _oneChestIn;

        internal static void Bind(ConfigFile cfg, ConfigSync sync)
        {
            foreach (BagDefaults b in Bags)
            {
                RecipeConfig[b.Prefab] = cfg.Bind($"Recipes.{b.Prefab}", "Recipe", b.Recipe,
                    $"How {b.Prefab} is crafted: station prefab, station level, then Item:amount pairs (e.g. piece_workbench,1,LeatherScraps:6). Empty = not craftable.");
                sync?.AddConfigEntry(RecipeConfig[b.Prefab]);
                // 1.0.20: quality 2 at the workbench (one level higher than the craft) adds the bag's special slots.
                UpgradeConfig[b.Prefab] = cfg.Bind($"Recipes.{b.Prefab}", "Upgrade", "",
                    $"What upgrading {b.Prefab} to quality 2 costs, as Item:amount pairs (e.g. TrollHide:2,Bronze:1). Empty = half of each crafting ingredient, rounded up.");
                sync?.AddConfigEntry(UpgradeConfig[b.Prefab]);
            }
            _oneChestIn = cfg.Bind("Loot", "One chest in", 40,
                new ConfigDescription("About one location chest in this many, in a bag's own biome, holds that bag (Small = Meadows, Day = Black Forest, Explorer = Swamp and Mountains). 0 = never.",
                    new AcceptableValueRange<int>(0, 1000)));
            sync?.AddConfigEntry(_oneChestIn);
        }

        // ── recipes ─────────────────────────────────────────────────────────────────────────────────────────────────────

        internal static void AddRecipes(ObjectDB db)
        {
            if (db == null || db.m_items == null || db.m_items.Count == 0) return;
            SetDisplayNames(db);
            if (ConfigManager.Instance?.configEnableRecipes != null && !ConfigManager.Instance.configEnableRecipes.Value) return;
            int made = 0;
            foreach (BagDefaults b in Bags)
            {
                GameObject itemGo = db.GetItemPrefab(b.Prefab);
                ItemDrop item = itemGo != null ? itemGo.GetComponent<ItemDrop>() : null;
                if (item == null) continue;
                if (db.m_recipes.Exists(r => r != null && r.m_item == item)) continue;   // already added for this ObjectDB
                if (!RecipeConfig.TryGetValue(b.Prefab, out ConfigEntry<string> entry) || string.IsNullOrWhiteSpace(entry.Value)) continue;
                Recipe recipe = Parse(db, item, entry.Value, out string why);
                if (recipe == null) { Debug.LogWarning($"{MyMod.PluginName}: no recipe for {b.Prefab}: {why}"); continue; }
                UpgradeConfig.TryGetValue(b.Prefab, out ConfigEntry<string> up);
                if (!ApplyUpgrade(db, recipe, up?.Value, out why)) Debug.LogWarning($"{MyMod.PluginName}: {b.Prefab} upgrade cost: {why}; using half the recipe");
                VerdantsAscent.Managers.Camp.CampLevels.ApplyToRecipe(db, recipe, item);   // 1.0.29: quality = camp level
                db.m_recipes.Add(recipe);
                made++;
            }
            if (made > 0) Debug.Log($"{MyMod.PluginName}: added {made} backpack recipe(s).");
        }

        // 1.0.21 (Fire 10-07): readable names; the bundle's m_name strings are leftovers (ExplorerBag_va read "FiresFirstBackpack").
        // Display only: prefab names, saves and customData are untouched, and every ItemData shares the prefab's m_shared.
        private static readonly Dictionary<string, string> DisplayNames = new Dictionary<string, string>
        {
            { "SmallBag_va", "Small Bag" },
            { "DayBag_va", "Day Bag" },
            { "ExplorerBag_va", "Explorer Bag" },
            { "HoboStick", "Hobo Stick" },
            { "StaffFiresWizard", "Wizard Staff" },
        };

        private static void SetDisplayNames(ObjectDB db)
        {
            foreach (KeyValuePair<string, string> kv in DisplayNames)
            {
                GameObject go = db.GetItemPrefab(kv.Key);
                ItemDrop drop = go != null ? go.GetComponent<ItemDrop>() : null;
                if (drop != null && drop.m_itemData?.m_shared != null) drop.m_itemData.m_shared.m_name = kv.Value;
            }
        }

        private static Recipe Parse(ObjectDB db, ItemDrop item, string text, out string why)
        {
            why = null;
            string[] parts = text.Split(',');
            if (parts.Length < 3) { why = $"'{text}' needs a station, a level and at least one Item:amount"; return null; }
            CraftingStation station = Station(db, parts[0].Trim());
            if (station == null) { why = $"no crafting station '{parts[0].Trim()}'"; return null; }
            if (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int level)) { why = $"'{parts[1]}' is not a station level"; return null; }
            var needs = new List<Piece.Requirement>();
            for (int i = 2; i < parts.Length; i++)
            {
                string[] pair = parts[i].Trim().Split(':');
                if (pair.Length != 2 || !int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount) || amount <= 0)
                { why = $"'{parts[i].Trim()}' is not Item:amount"; return null; }
                GameObject res = db.GetItemPrefab(pair[0].Trim());
                ItemDrop resDrop = res != null ? res.GetComponent<ItemDrop>() : null;
                if (resDrop == null) { why = $"no item '{pair[0].Trim()}'"; return null; }
                needs.Add(new Piece.Requirement { m_resItem = resDrop, m_amount = amount, m_amountPerLevel = 0, m_recover = true });
            }
            Recipe recipe = ScriptableObject.CreateInstance<Recipe>();
            recipe.name = "Recipe_" + item.name;
            recipe.m_item = item;
            recipe.m_amount = 1;
            recipe.m_enabled = true;
            recipe.m_craftingStation = station;
            recipe.m_repairStation = station;
            recipe.m_minStationLevel = Mathf.Max(1, level);
            recipe.m_resources = needs.ToArray();
            return recipe;
        }

        /// <summary>Sets m_amountPerLevel (the quality 1 -> 2 cost): from "Item:amount,..." or, when empty or bad, half of each craft amount.</summary>
        private static bool ApplyUpgrade(ObjectDB db, Recipe recipe, string text, out string why)
        {
            why = null;
            var needs = new List<Piece.Requirement>(recipe.m_resources);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var parsed = new List<KeyValuePair<ItemDrop, int>>();
                foreach (string part in text.Split(','))
                {
                    string[] pair = part.Trim().Split(':');
                    GameObject res = pair.Length == 2 ? db.GetItemPrefab(pair[0].Trim()) : null;
                    ItemDrop resDrop = res != null ? res.GetComponent<ItemDrop>() : null;
                    if (resDrop == null || !int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount) || amount <= 0)
                    { why = $"'{part.Trim()}' is not Item:amount"; break; }
                    parsed.Add(new KeyValuePair<ItemDrop, int>(resDrop, amount));
                }
                if (why == null)
                {
                    foreach (Piece.Requirement r in needs) r.m_amountPerLevel = 0;
                    foreach (KeyValuePair<ItemDrop, int> kv in parsed)
                    {
                        Piece.Requirement r = needs.Find(n => n.m_resItem == kv.Key);
                        if (r == null) needs.Add(r = new Piece.Requirement { m_resItem = kv.Key, m_amount = 0, m_recover = true });
                        r.m_amountPerLevel = kv.Value;
                    }
                    recipe.m_resources = needs.ToArray();
                    return true;
                }
            }
            foreach (Piece.Requirement r in recipe.m_resources) r.m_amountPerLevel = Mathf.Max(1, Mathf.CeilToInt(r.m_amount / 2f));
            return why == null;
        }

        /// <summary>A station by its prefab name: from ZNetScene, else from any vanilla recipe already using it.</summary>
        private static CraftingStation Station(ObjectDB db, string prefabName)
        {
            GameObject go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
            CraftingStation cs = go != null ? go.GetComponent<CraftingStation>() : null;
            if (cs != null) return cs;
            foreach (Recipe r in db.m_recipes)
                if (r != null && r.m_craftingStation != null && r.m_craftingStation.name == prefabName) return r.m_craftingStation;
            return null;
        }

        // ── chest loot ─────────────────────────────────────────────────────────────────────────────────────────────────

        internal static void AddChestLoot(ZNetScene scene)
        {
            if (scene == null || _oneChestIn == null || _oneChestIn.Value <= 0) return;
            float target = 1f / _oneChestIn.Value;
            var touched = new List<string>();
            foreach (GameObject prefab in scene.m_prefabs)
            {
                if (prefab == null || !prefab.name.StartsWith(ChestPrefix, StringComparison.Ordinal)) continue;
                Container chest = prefab.GetComponent<Container>();
                if (chest == null || chest.m_defaultItems == null || chest.m_defaultItems.m_drops == null) continue;
                string lower = prefab.name.ToLowerInvariant();
                foreach (BagDefaults b in Bags)
                {
                    if (b.ChestKeys == null || !Matches(lower, b.ChestKeys)) continue;
                    GameObject bag = scene.GetPrefab(b.Prefab);
                    if (bag == null) continue;
                    DropTable table = chest.m_defaultItems;
                    if (table.m_drops.Exists(d => d.m_item == bag)) continue;   // already in (a second ZNetScene)
                    float weight = WeightFor(table, target);
                    table.m_drops.Add(new DropTable.DropData { m_item = bag, m_stackMin = 1, m_stackMax = 1, m_weight = weight });
                    touched.Add($"{prefab.name}<-{b.Prefab}({weight:0.###})");
                }
            }
            Debug.Log($"{MyMod.PluginName}: backpack chest loot (about 1 in {_oneChestIn.Value}): {(touched.Count == 0 ? "no matching chest prefabs" : string.Join(", ", touched.ToArray()))}");
        }

        private static bool Matches(string lowerName, string[] keys)
        {
            foreach (string k in keys) if (lowerName.Contains(k)) return true;
            return false;
        }

        /// <summary>
        /// The weight that gives the bag about `target` chance of being in a filled chest: a chest draws n items (the middle of
        /// its min..max) by weight from total W, so P(at least one bag) = 1 - (1 - w/(W+w))^n; solved for w.
        /// </summary>
        private static float WeightFor(DropTable table, float target)
        {
            float total = 0f;
            foreach (DropTable.DropData d in table.m_drops) total += Mathf.Max(0f, d.m_weight);
            if (total <= 0f) total = 1f;
            float picks = Mathf.Max(1f, (table.m_dropMin + table.m_dropMax) * 0.5f);
            float chance = Mathf.Clamp01(table.m_dropChance <= 0f ? 1f : table.m_dropChance);
            float perChest = Mathf.Clamp(target / chance, 0f, 0.99f);   // the table only rolls on m_dropChance of chests
            float perPick = 1f - Mathf.Pow(1f - perChest, 1f / picks);
            return total * perPick / Mathf.Max(0.0001f, 1f - perPick);
        }

        [HarmonyPatch(typeof(ObjectDB), "Awake")]
        private static class ObjectDB_Awake
        {
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(ObjectDB __instance)
            {
                MyMod.AddItemsToObjectDB();   // the bags themselves may not be in this ObjectDB yet (idempotent)
                AddRecipes(__instance);
            }
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class ZNetScene_Awake
        {
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(ZNetScene __instance) => AddChestLoot(__instance);
        }
    }
}
