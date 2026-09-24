using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public static class VAItemManager
    {
        // Backpacks - loaded from "backpacks/" folder
        private static readonly List<string> BackpackNameList = new List<string>
        {
            "SmallBag_va",
            "DayBag_va",
            "ExplorerBag_va",
            "HoboStick",
            "StaffFiresWizard",

            // Add more backpacks here
        };

        // Regular items - loaded from "items/" folder
        private static readonly List<string> ItemNameList = new List<string>
        {
            "FiresUtilityBelt",

            // Add more regular items here
            // (ArmorMageChestGreen_vad removed — standalone armor, not part of the backpack set;
            //  drop the asset from the bundle on the next Unity rebuild.)
        };

        public static void LoadItems(string mainPath, MyMod modInstance)
        {
            if (MyMod.assetBundle == null)
            {
                Debug.LogError($"{MyMod.PluginName}: AssetBundle is null.");
                return;
            }

            MyMod.itemPrefabs.Clear();

            var allAssets = MyMod.assetBundle.GetAllAssetNames();
            Debug.Log($"{MyMod.PluginName}: AssetBundle contains {allAssets.Length} assets");
            if (ConfigManager.Instance?.configVerboseLogging?.Value ?? false)
                Debug.Log($"{MyMod.PluginName}: bundle assets: {string.Join(", ", allAssets)}");

            int failed = 0;

            // Load backpacks from "backpacks/" folder
            foreach (var itemName in BackpackNameList)
            {
                string prefabPath = mainPath + "backpacks/" + itemName + ".prefab";
                LoadSingleItem(prefabPath, itemName, ref failed);
            }

            // Load regular items from "items/" folder
            foreach (var itemName in ItemNameList)
            {
                string prefabPath = mainPath + "items/" + itemName + ".prefab";
                LoadSingleItem(prefabPath, itemName, ref failed);
            }

            Debug.Log($"{MyMod.PluginName}: Loaded {MyMod.itemPrefabs.Count} items. Failed: {failed}");
        }

        private static void LoadSingleItem(string prefabPath, string itemName, ref int failed)
        {
            string normalizedPath = prefabPath.Replace('\\', '/').ToLowerInvariant();

            GameObject itemPrefab = MyMod.assetBundle.LoadAsset<GameObject>(normalizedPath);
            if (itemPrefab == null)
            {
                Debug.LogError($"{MyMod.PluginName}: Failed to load item prefab: {normalizedPath}");
                failed++;
                return;
            }

            var itemDrop = itemPrefab.GetComponent<ItemDrop>();
            if (itemDrop == null)
            {
                Debug.LogError($"{MyMod.PluginName}: {itemName} - ItemDrop component missing!");
                UnityEngine.Object.Destroy(itemPrefab);
                failed++;
                return;
            }

            itemPrefab.SetActive(true);

            // Per-prefab post-load setup. HoboStick has a swinging-bag
            // pendulum (Stick + Potli AttachPoints with Rigidbody/Joint
            // components) that needs runtime configuration — we reparent
            // the bag mesh under its physics body and tune the joint so
            // the bag swings like a lantern under gravity.
            //
            // Temporarily disabled while diagnosing a bag-visibility
            // regression. Re-enable once the root cause (bag mesh missing
            // after Configure runs) is identified.
            // if (itemName == "HoboStick")
            //     HoboStickPhysicsSetup.Configure(itemPrefab);

            MyMod.itemPrefabs.Add(itemPrefab);

            Debug.Log($"{MyMod.PluginName}: Loaded item prefab: {itemName}, ItemDrop present: {itemDrop != null}, m_dropPrefab: {(itemDrop.m_itemData.m_dropPrefab != null ? itemDrop.m_itemData.m_dropPrefab.name : "null")}");
        }
    }
}