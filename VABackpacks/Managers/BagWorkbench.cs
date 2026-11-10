// BagWorkbench - the Workbench bag upgrade (tool slot): while no real station is in use, the wearer's bag counts as the crafting
// stations of its tier, for crafting from the inventory tab and for building pieces that need a station in range.
//   crafting : Player.RequiredCraftingStation - a recipe that needs a station passes when the bag provides that station at the level
//              the recipe asks for, and only while no real station is open (at a real station, vanilla rules apply unchanged)
//   building : CraftingStation.HaveBuildStationInRange - a piece's "needs a <station> in range" passes for the wearer
// Only the local player's own worn bags count. The stations and levels per tier are synced config ("prefab:level, ...").

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    internal static class BagWorkbench
    {

        internal const string UpgradeId = "Workbench";
        private const float BuildCheckRadius = 2f;   // HaveBuildStationInRange is asked at the builder's own position

        private static readonly string[] TierDefaults =
        {
            "piece_workbench:5",
            "piece_workbench:5, forge:7, piece_stonecutter:1",
            "piece_workbench:5, forge:7, piece_stonecutter:1, piece_artisanstation:1, blackforge:2, piece_magetable:2",
        };
        private static readonly string[] TierNumerals = { "I", "II", "III" };
        private static readonly ConfigEntry<string>[] TierStations = new ConfigEntry<string>[BagUpgrades.TierCount];

        private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks.Workbench");
        private static string s_lastLine;


        internal static void Bind(ConfigFile cfg, ConfigSync sync)
        {
            for (int t = 0; t < BagUpgrades.TierCount; t++)
            {
                TierStations[t] = cfg.Bind(BagUpgrades.Section, $"Workbench {TierNumerals[t]} stations", TierDefaults[t],
                    $"The crafting stations (piece prefab name : station level) a bag with Workbench {TierNumerals[t]} counts as. Server-synced.");
                sync?.AddConfigEntry(TierStations[t]);
            }
        }

        // ── which stations a tier gives ────────────────────────────────────────────────────────────────

        private static Dictionary<string, int> PrefabLevels(int tier)
        {
            var map = new Dictionary<string, int>();
            if (tier < 1 || tier > BagUpgrades.TierCount) return map;
            string text = TierStations[tier - 1]?.Value ?? TierDefaults[tier - 1];
            foreach (string part in text.Split(','))
            {
                string[] kv = part.Trim().Split(':');
                if (kv.Length == 2 && int.TryParse(kv[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int level) && level > 0)
                    map[kv[0].Trim()] = level;
            }
            return map;
        }

        private static CraftingStation StationPrefab(string prefab)
        {
            GameObject go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            return go != null ? go.GetComponent<CraftingStation>() : null;
        }

        /// <summary>The station m_name (e.g. "$piece_workbench") -> level the local player's worn bag provides; empty without the upgrade.</summary>
        private static Dictionary<string, int> Provided(Player player)
        {
            var byName = new Dictionary<string, int>();
            if (player == null || player != Player.m_localPlayer) return byName;
            int tier = 0;
            foreach (ItemDrop.ItemData bag in BackpackApi.GetEquippedBackpacks(player))
                tier = Math.Max(tier, BagUpgrades.SlottedTier(bag, UpgradeId));
            foreach (KeyValuePair<string, int> kv in PrefabLevels(tier))
            {
                CraftingStation st = StationPrefab(kv.Key);
                if (st != null) byName[st.m_name] = Math.Max(kv.Value, byName.TryGetValue(st.m_name, out int l) ? l : 0);
            }
            string line = tier == 0 ? "none" : $"Workbench {TierNumerals[tier - 1]}: " + string.Join(", ", byName.Select(kv => $"{kv.Key} {kv.Value}").ToArray());
            if (line != s_lastLine) { s_lastLine = line; Log.LogDebug($"{MyMod.PluginName}: bag workbench now provides {line}"); }
            return byName;
        }

        internal static string DescribeTier(int tier)
        {
            var names = PrefabLevels(tier).Select(kv =>
            {
                CraftingStation st = StationPrefab(kv.Key);
                string label = st != null && Localization.instance != null ? Localization.instance.Localize(st.m_name) : kv.Key;
                return $"{label} {kv.Value}";
            });
            return string.Join(", ", names.ToArray());
        }

        // ── crafting ───────────────────────────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(Player), "RequiredCraftingStation")]
        private static class Craft_Patch
        {
            private static void Postfix(Player __instance, Recipe recipe, int qualityLevel, bool checkLevel, ref bool __result)
            {
                if (__result || recipe == null || __instance != Player.m_localPlayer || __instance.GetCurrentCraftingStation() != null) return;
                CraftingStation required = recipe.GetRequiredStation(qualityLevel);
                if (required == null) return;
                if (!Provided(__instance).TryGetValue(required.m_name, out int level)) return;
                if (!checkLevel || level >= recipe.GetRequiredStationLevel(qualityLevel)) __result = true;
            }
        }

        // ── building ───────────────────────────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.HaveBuildStationInRange))]
        private static class Build_Patch
        {
            private static void Postfix(string name, Vector3 point, ref CraftingStation __result)
            {
                Player me = Player.m_localPlayer;
                if (__result != null || me == null || string.IsNullOrEmpty(name)) return;
                if (Vector3.Distance(point, me.transform.position) > BuildCheckRadius) return;
                if (!Provided(me).ContainsKey(name)) return;
                foreach (GameObject go in ZNetScene.instance != null ? ZNetScene.instance.m_prefabs : new List<GameObject>())
                {
                    CraftingStation st = go != null ? go.GetComponent<CraftingStation>() : null;
                    if (st != null && st.m_name == name) { __result = st; return; }
                }
            }
        }
    }
}
