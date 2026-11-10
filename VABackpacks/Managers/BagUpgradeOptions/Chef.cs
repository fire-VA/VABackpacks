// Chef's (tool slot): the bag cooks for its wearer, from the inventory (bag cells included), while no real station is open.
//   - Cooking stations and the oven cook by placing food on them, so each of their conversions (raw -> cooked) becomes a
//     "cook from bag" recipe in the inventory crafting tab, shown only while the worn tier covers that station.
//   - The cauldron and the food preparation table are real crafting stations: their recipes pass the station check, as the
//     Workbench upgrade does for its stations.
// The stations per tier are synced config ("prefab:level, ..."). Building is not affected. Local player only.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public sealed class ChefOption : IBagUpgradeOption
    {
        public const string UpgradeId = "Chef";
        private const string RecipePrefix = "Recipe_FiresBagChef_";
        private const int DefaultStationLevel = 1;

        private static readonly string[] TierDefaults =
        {
            "piece_cookingstation:1, piece_cookingstation_iron:1",
            "piece_cookingstation:1, piece_cookingstation_iron:1, piece_cauldron:4",
            "piece_cookingstation:1, piece_cookingstation_iron:1, piece_cauldron:6, piece_oven:1, piece_preptable:1",
        };
        private static readonly ConfigEntry<string>[] TierStations = new ConfigEntry<string>[BagUpgrades.TierCount];
        private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks." + UpgradeId);

        // cook-from-bag recipes, per cooking station prefab (made once the game's prefabs exist)
        private static readonly Dictionary<string, List<Recipe>> s_cookRecipes = new Dictionary<string, List<Recipe>>();
        private static string s_lastLine;

        public string Id => UpgradeId;
        public string Name => "Chef's";
        public string Slot => BackpackSpecialSlots.SlotTool;
        public string Donor => "CookedMeat";
        public string[] Recipes => new[]
        {
            "forge,1,Bronze:3,FineWood:10,RawMeat:5",
            "forge,3,Iron:5,ElderBark:10,Bloodbag:5",
            "forge,5,BlackMetal:5,Sap:10,RoyalJelly:5",
        };

        public string Describe(int tier) => $"The bag cooks from your inventory when no station is open: {DescribeTier(tier)}.";

        public void Bind(ConfigFile cfg, ConfigSync sync)
        {
            for (int t = 0; t < BagUpgrades.TierCount; t++)
            {
                // 1.0.128: the key had an apostrophe ("Chef's I stations"), which BepInEx refuses, so Chef never bound (Fire 10-24). The
                // key is "Chef I stations"; the name with the apostrophe stays in the description.
                TierStations[t] = cfg.Bind(BagUpgrades.Section, $"Chef {BagUpgrades.TierNumerals[t]} stations", TierDefaults[t],
                    $"The cooking stations (piece prefab name : station level) a bag with Chef's {BagUpgrades.TierNumerals[t]} counts as. Server-synced.");
                MigrateOldKey(cfg, TierStations[t], $"Chef's {BagUpgrades.TierNumerals[t]} stations");
                sync?.AddConfigEntry(TierStations[t]);
            }
        }

        // One-time: an old "Chef's …" line left in the .cfg (an orphaned entry; its key can't even be built as a ConfigDefinition) moves
        // its value to the new key, then goes. Only while the new key still holds its default, so a newer edit is never overwritten.
        private static void MigrateOldKey(ConfigFile cfg, ConfigEntry<string> entry, string oldKey)
        {
            try
            {
                if (!(AccessTools.Property(typeof(ConfigFile), "OrphanedEntries")?.GetValue(cfg) is Dictionary<ConfigDefinition, string> orphans)) return;
                ConfigDefinition hit = null;
                foreach (var d in orphans.Keys) if (d.Section == entry.Definition.Section && d.Key == oldKey) { hit = d; break; }
                if (hit == null) return;
                string old = orphans[hit];
                orphans.Remove(hit);
                if (entry.Value == (string)entry.DefaultValue && !string.IsNullOrEmpty(old)) entry.Value = old;
                cfg.Save();
                BepInEx.Logging.Logger.CreateLogSource("VABackpacks.Upgrades").LogDebug($"Chef: config key '{oldKey}' moved to '{entry.Definition.Key}' (value '{entry.Value}')");
            }
            catch (Exception e) { BepInEx.Logging.Logger.CreateLogSource("VABackpacks.Upgrades").LogWarning($"Chef: migrating '{oldKey}' failed: {e.Message}"); }
        }

        // ── which stations a tier gives ──────────────────────────────────────────────────────────────────────────

        private static Dictionary<string, int> PrefabLevels(int tier)
        {
            var map = new Dictionary<string, int>();
            if (tier < 1 || tier > BagUpgrades.TierCount) return map;
            foreach (string part in (TierStations[tier - 1]?.Value ?? TierDefaults[tier - 1]).Split(','))
            {
                string[] kv = part.Trim().Split(':');
                if (kv[0].Trim().Length == 0) continue;
                int level = DefaultStationLevel;
                if (kv.Length == 2 && !int.TryParse(kv[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out level)) continue;
                if (level > 0) map[kv[0].Trim()] = level;
            }
            return map;
        }

        private static GameObject Prefab(string name) => ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;

        private static string DescribeTier(int tier)
        {
            var parts = PrefabLevels(tier).Keys.Select(p =>
            {
                GameObject go = Prefab(p);
                string label = go?.GetComponent<CraftingStation>()?.m_name ?? go?.GetComponent<CookingStation>()?.m_name ?? go?.GetComponent<Piece>()?.m_name ?? p;
                return Localization.instance != null ? Localization.instance.Localize(label) : label;
            });
            return string.Join(", ", parts.Distinct().ToArray());
        }

        private static int Tier(Player p) => p != null && p == Player.m_localPlayer ? BagUpgrades.WornTier(p, UpgradeId) : 0;

        /// <summary>Crafting station m_name -> level the worn bag provides (cauldron, prep table...).</summary>
        private static Dictionary<string, int> CraftingProvided(int tier)
        {
            var byName = new Dictionary<string, int>();
            foreach (KeyValuePair<string, int> kv in PrefabLevels(tier))
            {
                CraftingStation st = Prefab(kv.Key)?.GetComponent<CraftingStation>();
                if (st != null) byName[st.m_name] = Math.Max(kv.Value, byName.TryGetValue(st.m_name, out int l) ? l : 0);
            }
            return byName;
        }

        /// <summary>The cook-from-bag recipes the tier unlocks (its cooking stations' conversions).</summary>
        private static List<Recipe> CookProvided(int tier)
        {
            EnsureCookRecipes();
            return PrefabLevels(tier).Keys.Where(s_cookRecipes.ContainsKey).SelectMany(p => s_cookRecipes[p]).Distinct().ToList();
        }

        private static void LogProvides(int tier)
        {
            string line = tier == 0 ? "none" : $"Chef's {BagUpgrades.TierNumerals[tier - 1]}: "
                + string.Join(", ", CraftingProvided(tier).Select(kv => $"{kv.Key} {kv.Value}").ToArray()) + $"; cook from bag: {CookProvided(tier).Count} recipes";
            if (line == s_lastLine) return;
            s_lastLine = line;
            Log.LogDebug($"[{UpgradeId}] now provides {line}");
        }

        // ── cook-from-bag recipes ───────────────────────────────────────────────────────────────────────────────

        // One recipe per conversion of every cooking station named in any tier. Disabled (m_enabled = false), so the game and other
        // mods never list them; the crafting tab gets them from the GetAvailableRecipes patch below, only while the tier covers them.
        private static void EnsureCookRecipes()
        {
            if (s_cookRecipes.Count > 0 || ObjectDB.instance == null || ZNetScene.instance == null) return;
            var stations = Enumerable.Range(1, BagUpgrades.TierCount).SelectMany(t => PrefabLevels(t).Keys).Distinct();
            int made = 0;
            foreach (string p in stations)
            {
                CookingStation cs = Prefab(p)?.GetComponent<CookingStation>();
                if (cs == null) continue;
                var list = new List<Recipe>();
                foreach (CookingStation.ItemConversion c in cs.m_conversion)
                {
                    if (c?.m_from == null || c.m_to == null) continue;
                    string name = RecipePrefix + c.m_from.name;
                    Recipe r = ObjectDB.instance.m_recipes.FirstOrDefault(x => x != null && x.name == name);
                    if (r == null)
                    {
                        r = ScriptableObject.CreateInstance<Recipe>();
                        r.name = name;
                        r.m_item = c.m_to;
                        r.m_amount = 1;
                        r.m_enabled = false;
                        r.m_craftingStation = null;
                        r.m_resources = new[] { new Piece.Requirement { m_resItem = c.m_from, m_amount = 1, m_recover = false } };
                        ObjectDB.instance.m_recipes.Add(r);
                        made++;
                    }
                    list.Add(r);
                }
                s_cookRecipes[p] = list;
            }
            Log.LogDebug($"[{UpgradeId}] cook-from-bag recipes: {made} made ({string.Join(", ", s_cookRecipes.Select(kv => $"{kv.Key} {kv.Value.Count}").ToArray())})");
        }

        [HarmonyPatch(typeof(ObjectDB), "Awake")]
        private static class ObjectDB_Patch
        {
            [HarmonyPriority(Priority.VeryLow)]
            private static void Postfix() => s_cookRecipes.Clear();   // a new world: made again on first use, against this ObjectDB
        }

        // the crafting tab: the tier's cook-from-bag recipes, while no real station is open
        [HarmonyPatch(typeof(Player), nameof(Player.GetAvailableRecipes))]
        private static class Available_Patch
        {
            private static void Postfix(Player __instance, ref List<Recipe> available)
            {
                int tier = Tier(__instance);
                if (tier == 0 || __instance.GetCurrentCraftingStation() != null || available == null) return;
                LogProvides(tier);
                foreach (Recipe r in CookProvided(tier))
                {
                    if (available.Contains(r)) continue;
                    ItemDrop raw = r.m_resources.Length > 0 ? r.m_resources[0].m_resItem : null;
                    if (raw != null && !__instance.IsMaterialKnown(raw.m_itemData.m_shared.m_name)) continue;   // only food the player has seen
                    available.Add(r);
                }
            }
        }

        // the cauldron / prep table recipes pass the station check (as the Workbench upgrade does)
        [HarmonyPatch(typeof(Player), "RequiredCraftingStation")]
        private static class Station_Patch
        {
            private static void Postfix(Player __instance, Recipe recipe, int qualityLevel, bool checkLevel, ref bool __result)
            {
                if (__result || recipe == null) return;
                int tier = Tier(__instance);
                if (tier == 0 || __instance.GetCurrentCraftingStation() != null) return;
                CraftingStation required = recipe.GetRequiredStation(qualityLevel);
                if (required == null || !CraftingProvided(tier).TryGetValue(required.m_name, out int level)) return;
                if (!checkLevel || level >= recipe.GetRequiredStationLevel(qualityLevel))
                {
                    __result = true;
                    LogProvides(tier);
                }
            }
        }

        // a crafted cook-from-bag recipe: one Debug line (what, from what)
        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        private static class Craft_Patch
        {
            private static void Prefix(InventoryGui __instance, out string __state)
            {
                Recipe r = __instance.m_craftRecipe;
                __state = r != null && r.name.StartsWith(RecipePrefix, StringComparison.Ordinal) ? r.m_resources[0].m_resItem.name + " -> " + r.m_item.name : null;
            }

            private static void Postfix(string __state)
            {
                if (__state != null) Log.LogDebug($"[{UpgradeId}] cooked from the bag: {__state}");
            }
        }
    }
}
