// CampStorage — 1.0.32 (Fire 10-08, camp upgrades plan step 3, section 4): chests with items are packed with the camp, and
// their items weigh on the bag at a share of their normal weight by category ([CampStorage], server-synced). Anything
// non-teleportable in a packed camp blocks portals, like ore in a bag cell. Worked out once at pack-up and kept on the bag
// (m_customData VA_CampWeight / VA_CampItems / VA_CampNoTeleport), so neither the weight nor the portal check unpacks the
// blueprint.

using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    internal static class CampStorage
    {
        internal const string KeyWeight = "VA_CampWeight";
        internal const string KeyItems = "VA_CampItems";
        internal const string KeyNoTeleport = "VA_CampNoTeleport";

        private static ConfigEntry<float> _food, _gear, _oreBars, _materials, _other;
        private static ConfigEntry<string> _oreBarList;
        private static HashSet<string> _oreBarSet;
        private static string _oreBarParsed;

        internal static void Bind(ConfigFile cfg, ConfigSync sync)
        {
            const string S = "CampStorage";
            _food = cfg.Bind(S, "Food", 0f, "Share of normal weight for food in a packed camp's chests (0-1).");
            _gear = cfg.Bind(S, "Gear", 0f, "Share for armour, weapons, shields, tools, torches and ammo.");
            _oreBars = cfg.Bind(S, "OreBars", 0.05f, "Share for ores, scrap and metal bars (the OreBarList, plus any non-teleportable material).");
            _materials = cfg.Bind(S, "Materials", 0.01f, "Share for other crafting materials.");
            _other = cfg.Bind(S, "Other", 0.01f, "Share for trophies, valuables and everything else.");
            _oreBarList = cfg.Bind(S, "OreBarList",
                "CopperOre,TinOre,IronOre,IronScrap,SilverOre,BlackMetalScrap,FlametalOre,FlametalOreNew,CopperScrap,Bronze,Copper,Tin,Iron,Silver,BlackMetal,Flametal,FlametalNew",
                "Prefab names weighed with the OreBars share.");
            foreach (ConfigEntry<float> e in new[] { _food, _gear, _oreBars, _materials, _other }) sync?.AddConfigEntry(e);
            sync?.AddConfigEntry(_oreBarList);
        }

        private static bool IsOreBar(ItemDrop.ItemData it)
        {
            string raw = _oreBarList?.Value ?? "";
            if (_oreBarSet == null || _oreBarParsed != raw)
            {
                _oreBarSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string s in raw.Split(',')) if (s.Trim().Length > 0) _oreBarSet.Add(s.Trim());
                _oreBarParsed = raw;
            }
            return it.m_dropPrefab != null && _oreBarSet.Contains(it.m_dropPrefab.name);
        }

        internal static float Share(ItemDrop.ItemData it)
        {
            switch (it.m_shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.Consumable:
                    return _food?.Value ?? 0f;
                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Shoulder:
                case ItemDrop.ItemData.ItemType.Utility:
                case ItemDrop.ItemData.ItemType.Trinket:
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.Shield:
                case ItemDrop.ItemData.ItemType.Tool:
                case ItemDrop.ItemData.ItemType.Torch:
                case ItemDrop.ItemData.ItemType.Ammo:
                case ItemDrop.ItemData.ItemType.AmmoNonEquipable:
                    return _gear?.Value ?? 0f;
                case ItemDrop.ItemData.ItemType.Material:
                    return IsOreBar(it) || !it.m_shared.m_teleportable ? _oreBars?.Value ?? 0.05f : _materials?.Value ?? 0.01f;
                default:
                    return _other?.Value ?? 0.01f;
            }
        }

        /// <summary>Totals for the chests being packed: items, weight at the camp shares, and whether any blocks portals.</summary>
        internal sealed class Totals
        {
            internal int Items;
            internal float Weight;
            internal bool NoTeleport;

            internal void Add(Inventory inv)
            {
                if (inv == null) return;
                foreach (ItemDrop.ItemData it in inv.GetAllItems())
                {
                    if (it?.m_shared == null) continue;
                    Items += it.m_stack;
                    Weight += it.GetWeight() * Mathf.Clamp01(Share(it));
                    if (!it.m_shared.m_teleportable || it.m_shared.m_toolTier >= 1000) NoTeleport = true;
                }
            }
        }

        internal static void Write(ItemDrop.ItemData bag, Totals t)
        {
            if (bag == null) return;
            if (bag.m_customData == null) bag.m_customData = new Dictionary<string, string>();
            Clear(bag);
            if (t == null || t.Items <= 0) return;
            bag.m_customData[KeyItems] = t.Items.ToString(CultureInfo.InvariantCulture);
            bag.m_customData[KeyWeight] = t.Weight.ToString("0.###", CultureInfo.InvariantCulture);
            if (t.NoTeleport) bag.m_customData[KeyNoTeleport] = "1";
        }

        internal static void Clear(ItemDrop.ItemData bag)
        {
            if (bag?.m_customData == null) return;
            bag.m_customData.Remove(KeyWeight);
            bag.m_customData.Remove(KeyItems);
            bag.m_customData.Remove(KeyNoTeleport);
        }

        internal static float WeightOf(ItemDrop.ItemData bag) =>
            bag?.m_customData != null && bag.m_customData.TryGetValue(KeyWeight, out string w)
            && float.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;

        internal static int ItemsOf(ItemDrop.ItemData bag) =>
            bag?.m_customData != null && bag.m_customData.TryGetValue(KeyItems, out string n)
            && int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : 0;

        private static bool BlocksPortals(ItemDrop.ItemData bag) =>
            bag?.m_customData != null && bag.m_customData.ContainsKey(KeyNoTeleport);

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetWeight))]
        public static class Weight_Patch
        {
            public static void Postfix(ItemDrop.ItemData __instance, ref float __result)
            {
                if (__instance?.m_customData == null || __instance.m_customData.Count == 0) return;
                __result += WeightOf(__instance);
            }
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.IsTeleportable))]
        public static class Teleport_Patch
        {
            public static void Postfix(Inventory __instance, bool allowAllItems, ref bool __result)
            {
                if (!__result || allowAllItems) return;
                if (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.TeleportAll)) return;
                foreach (ItemDrop.ItemData bag in __instance.GetAllItems())
                    if (BlocksPortals(bag)) { __result = false; return; }
            }
        }
    }
}
