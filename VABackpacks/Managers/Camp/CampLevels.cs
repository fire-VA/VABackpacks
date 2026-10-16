// CampLevels — 1.0.29 (Fire 10-08, camp upgrades plan step 1): a bag's quality is its camp level. Each level has a radius and a
// piece cap ([Camp] Radius / PieceCap, server-synced). Max quality per bag: Small 2, Day 3, Explorer / Hobo Stick / Staff 4.
// Upgrades use the bag's recipe (half the craft cost per level); level 3 and 4 add [Camp] Level3Extra / Level4Extra on top.
// The workbench tops out at level 5, so a bag's required station level is capped there.

using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    internal static class CampLevels
    {
        internal const int MaxLevel = 4;
        private const int WorkbenchMaxLevel = 5;

        private static readonly Dictionary<string, int> MaxQualityByBag = new Dictionary<string, int>
        {
            { "SmallBag_va", 2 }, { "DayBag_va", 3 }, { "ExplorerBag_va", 4 }, { "HoboStick", 4 }, { "StaffFiresWizard", 4 },
        };

        private static ConfigEntry<string> _radius, _pieceCap, _level3Extra, _level4Extra;
        private static ConfigEntry<bool> _customCamps;

        internal static bool CustomCamps => _customCamps?.Value ?? true;

        // Requirements added for one level only (level 3 / level 4 extras): the amount at that quality, 0 at any other.
        private static readonly Dictionary<Piece.Requirement, KeyValuePair<int, int>> LevelOnly = new Dictionary<Piece.Requirement, KeyValuePair<int, int>>();
        private static readonly HashSet<Recipe> BagRecipes = new HashSet<Recipe>();

        internal static void Bind(ConfigFile cfg, ConfigSync sync)
        {
            _radius = cfg.Bind("Camp", "Radius", "10,20,30,40", "Camp radius in metres for camp level (bag quality) 1,2,3,4.");
            _pieceCap = cfg.Bind("Camp", "PieceCap", "500,1000,1500,2000", "Most pieces a camp can pack, for camp level 1,2,3,4.");
            // 1.0.44 (Fire chose a level-1 cap of 500, the higher levels scaled from it): a config still on the old default moves
            // to the new one; a value the player set by hand is kept.
            if (_pieceCap.Value.Replace(" ", "") == "100,200,300,500")
            {
                _pieceCap.Value = "500,1000,1500,2000";
                Debug.Log("[VABackpacks] camp piece caps moved from the old default 100,200,300,500 to 500,1000,1500,2000");
            }
            _level3Extra = cfg.Bind("Camp", "Level3Extra", "Iron:5", "Extra cost to upgrade a bag to quality 3, as Item:amount pairs. Empty = none.");
            _level4Extra = cfg.Bind("Camp", "Level4Extra", "Silver:5", "Extra cost to upgrade a bag to quality 4, as Item:amount pairs. Empty = none.");
            foreach (var e in new[] { _radius, _pieceCap, _level3Extra, _level4Extra }) sync?.AddConfigEntry(e);
            _customCamps = cfg.Bind("Camp", "CustomCamps", true, "Pack-up takes your own buildings inside the camp radius into the bag, and the next deploy places them again. Off = only the camp itself packs.");
            sync?.AddConfigEntry(_customCamps);
        }

        internal static int MaxQuality(string prefab) => prefab != null && MaxQualityByBag.TryGetValue(prefab, out int q) ? q : 1;

        internal static int Level(ItemDrop.ItemData bag) => bag == null ? 1 : Mathf.Clamp(bag.m_quality, 1, MaxLevel);

        internal static float Radius(int level) => PerLevel(_radius?.Value, level, new[] { 10f, 20f, 30f, 40f });

        internal static int PieceCap(int level) => Mathf.RoundToInt(PerLevel(_pieceCap?.Value, level, new[] { 500f, 1000f, 1500f, 2000f }));

        private static float PerLevel(string text, int level, float[] fallback)
        {
            int i = Mathf.Clamp(level, 1, MaxLevel) - 1;
            string[] parts = (text ?? "").Split(',');
            if (i < parts.Length && float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && v > 0f) return v;
            return fallback[i];
        }

        /// <summary>Called by AddRecipes for each bag recipe: max quality and the level 3 / 4 extras.</summary>
        internal static void ApplyToRecipe(ObjectDB db, Recipe recipe, ItemDrop item)
        {
            item.m_itemData.m_shared.m_maxQuality = Mathf.Max(item.m_itemData.m_shared.m_maxQuality, MaxQuality(item.name));
            BagRecipes.Add(recipe);
            int max = item.m_itemData.m_shared.m_maxQuality;
            var needs = new List<Piece.Requirement>(recipe.m_resources);
            if (max >= 3) AddExtras(db, needs, _level3Extra?.Value, 3, item.name);
            if (max >= 4) AddExtras(db, needs, _level4Extra?.Value, 4, item.name);
            recipe.m_resources = needs.ToArray();
        }

        private static void AddExtras(ObjectDB db, List<Piece.Requirement> needs, string text, int level, string bag)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (string part in text.Split(','))
            {
                string[] pair = part.Trim().Split(':');
                GameObject res = pair.Length == 2 ? db.GetItemPrefab(pair[0].Trim()) : null;
                ItemDrop resDrop = res != null ? res.GetComponent<ItemDrop>() : null;
                if (resDrop == null || !int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount) || amount <= 0)
                {
                    Debug.LogWarning($"{MyMod.PluginName}: {bag} level {level} extra '{part.Trim()}' is not Item:amount; skipped");
                    continue;
                }
                var r = new Piece.Requirement { m_resItem = resDrop, m_amount = 0, m_amountPerLevel = 0, m_recover = true };
                LevelOnly[r] = new KeyValuePair<int, int>(level, amount);
                needs.Add(r);
            }
        }

        [HarmonyPatch(typeof(Piece.Requirement), nameof(Piece.Requirement.GetAmount))]
        public static class RequirementAmount_Patch
        {
            public static void Postfix(Piece.Requirement __instance, int qualityLevel, ref int __result)
            {
                if (LevelOnly.TryGetValue(__instance, out KeyValuePair<int, int> only))
                    __result = qualityLevel == only.Key ? only.Value : 0;
            }
        }

        [HarmonyPatch(typeof(Recipe), nameof(Recipe.GetRequiredStationLevel))]
        public static class StationLevel_Patch
        {
            public static void Postfix(Recipe __instance, ref int __result)
            {
                if (BagRecipes.Contains(__instance)) __result = Mathf.Min(__result, WorkbenchMaxLevel);
            }
        }

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip),
            new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool) })]
        public static class Tooltip_Patch
        {
            public static void Postfix(ItemDrop.ItemData item, int qualityLevel, ref string __result)
            {
                if (item?.m_dropPrefab == null || !MaxQualityByBag.ContainsKey(item.m_dropPrefab.name)) return;
                int level = Mathf.Clamp(qualityLevel, 1, MaxLevel);
                __result += $"\nCamp: level {level}, {Radius(level):0} m, {PieceCap(level)} pieces";
                int stored = CampStorage.ItemsOf(item);
                if (stored > 0) __result += $"\nCamp storage: {stored} items, {CampStorage.WeightOf(item):0.#} weight";
            }
        }
    }
}
