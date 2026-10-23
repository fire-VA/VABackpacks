using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // Chest bags come from the chest's vanilla DropTable
    // (Container.AddDefaultItems), so neither this mod nor FLO touched the item and it never got a rarity. When the key is on and
    // Fires Loot Overhaul is loaded, each of our bags in a freshly filled chest goes through FloApi.Roll(item, -1 = random, tier,
    // biome, 1) by reflection. Tier: 1 Meadows/BlackForest, 2 Swamp/Mountain/Plains, 3 later. Default OFF (new behaviour).
    // Chests filled before the key was on keep their bags as they are (vanilla fills a chest once).
    internal static class ChestBagRarity
    {
        internal static ConfigEntry<bool> Enabled;
        private static MethodInfo s_roll, s_rollsOn;
        private static bool s_resolved;
        private static HashSet<string> s_bags;

        internal static void Bind(ConfigFile cfg, ConfigSync sync)
        {
            Enabled = cfg.Bind("Loot", "Roll FLO rarity on chest bags", false,
                "When true and Fires Loot Overhaul is loaded, a backpack that a chest fills with gets a FLO rarity roll (random, tier by the chest's biome), like crafted gear and monster drops. Off = as before 1.0.63. Server-synced.");
            sync?.AddConfigEntry(Enabled);
        }

        // once, logged: FiresLootOverhaul.FloApi.Roll(ItemData, int, int, Heightmap.Biome, float), the 5-argument one by its types
        private static void Resolve()
        {
            if (s_resolved) return;
            s_resolved = true;
            var api = AccessTools.TypeByName("FiresLootOverhaul.FloApi");
            s_roll = api?.GetMethod("Roll", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(Heightmap.Biome), typeof(float) }, null);
            s_rollsOn = api?.GetProperty("RollsOn", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();   // add-only, may come later
            s_bags = new HashSet<string>(BackpackCraftingAndLoot.BagPrefabs);
            Debug.Log($"{MyMod.PluginName}: chest bag FLO rarity: FloApi {(api != null ? "found" : "NOT loaded")}, Roll(ItemData,int,int,Biome,float) {(s_roll != null ? "resolved" : "NOT resolved")}, RollsOn {(s_rollsOn != null ? "resolved" : "absent")}; key '{Enabled?.Definition.Key}' = {Enabled?.Value}");
        }

        internal static void LogAtLoad() => Resolve();

        private static int TierOf(Heightmap.Biome b)
        {
            switch (b)
            {
                case Heightmap.Biome.Meadows: case Heightmap.Biome.BlackForest: return 1;
                case Heightmap.Biome.Swamp: case Heightmap.Biome.Mountain: case Heightmap.Biome.Plains: return 2;
                default: return 3;
            }
        }

        [HarmonyPatch(typeof(Container), "AddDefaultItems")]
        private static class Container_AddDefaultItems
        {
            [HarmonyPostfix]
            private static void Post(Container __instance)
            {
                if (Enabled == null || !Enabled.Value || __instance == null) return;
                Resolve();
                if (s_roll == null) return;
                try
                {
                    if (s_rollsOn != null && s_rollsOn.Invoke(null, null) is bool on && !on) return;
                    var inv = __instance.GetInventory();
                    if (inv == null) return;
                    Heightmap.Biome biome = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(__instance.transform.position) : Heightmap.Biome.Meadows;
                    int tier = TierOf(biome);
                    foreach (var item in inv.GetAllItems())
                    {
                        string prefab = item?.m_dropPrefab?.name;
                        if (prefab == null || !s_bags.Contains(prefab)) continue;
                        bool rolled = (bool)s_roll.Invoke(null, new object[] { item, -1, tier, biome, 1f });
                        Debug.Log($"[VABackpacks] chest bag {prefab} rolled via FLO: {rolled} (tier {tier}, {biome}, chest {Utils.GetPrefabName(__instance.gameObject)})");
                    }
                }
                catch (Exception e) { Debug.LogWarning($"{MyMod.PluginName}: chest bag FLO roll failed: {(e.InnerException ?? e).Message}"); }
            }
        }
    }
}
