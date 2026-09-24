using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using VerdantsAscent.Managers;

#nullable disable

namespace VerdantsAscent
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class MyMod : BaseUnityPlugin
    {
        public static MyMod Instance;
        public static readonly Harmony harmony = new Harmony("com.Fire.VA_Backpacks");
        public const string PluginGUID = "com.Fire.VA_Backpacks";
        public const string PluginName = "VABackpacks";
        public const string PluginVersion = "1.0.14";

        public static AssetBundle assetBundle;
        public static readonly List<GameObject> piecePrefabs = new List<GameObject>();
        public static readonly List<GameObject> itemPrefabs = new List<GameObject>();

        public static GameObject RootObject;
        public ConfigSync configSync;
        public static GameObject PrefabContainer { get; private set; }

        private void Awake()
        {
            Instance = this;

            RootObject = new GameObject("_VABackpacksRoot");
            UnityEngine.Object.DontDestroyOnLoad(RootObject);

            PrefabContainer = new GameObject("VA_Backpacks_Prefabs");
            PrefabContainer.transform.parent = RootObject.transform;
            PrefabContainer.SetActive(false);

            this.configSync = new ConfigSync(PluginGUID)
            {
                DisplayName = PluginName,
                CurrentVersion = PluginVersion,
                MinimumRequiredVersion = PluginVersion
            };

            this.configSync.lockedConfigChanged += ConfigManager.Instance.OnConfigLockChanged;

            ConfigManager.Instance.Initialize(Config);

            BindConfigsToSync();

            // Shared help panel (FiresCore) — SOFT dependency; no-ops when Core is absent.
            VerdantsAscent.Utilities.VaBackpacksHelpContent.TryRegister();

            LoadAssets();

            if (ZNet.instance && ZNet.instance.IsDedicated())
                StartCoroutine(DelayedDedicatedServerInit());
            else
                FinishInit();
        }

        private IEnumerator DelayedDedicatedServerInit()
        {
            while (ZNetScene.instance == null || ObjectDB.instance == null)
                yield return null;

            yield return new WaitForEndOfFrame();

            FinishInit();
        }

        private void FinishInit()
        {
            harmony.PatchAll();

            // Initialize the backpack system after all patches are applied
            BackpackManager.Initialize();

            // Camp manager polls Input.GetKey for the Shift+C gesture, so it
            // must exist as a live MonoBehaviour from now on rather than
            // being lazy-created on first interact.
            VerdantsAscent.Managers.Camp.BackpackCampManager.EnsureExists();

            Debug.Log($"{PluginName}: Init complete (dedicated: {ZNet.instance?.IsDedicated()})");

            StartCoroutine(WaitForZNetReady());
        }

        private void BindConfigsToSync()
        {
            var instance = ConfigManager.Instance;

            configSync.AddLockingConfigEntry(instance.configEnableRecipes);
            configSync.AddConfigEntry(instance.configVerboseLogging);

            // Backpack-specific configs can be added here later
        }

        private void OnDestroy()
        {
            BackpackManager.Cleanup();

            if (assetBundle != null)
            {
                try
                {
                    assetBundle.Unload(true);
                    Debug.Log($"{PluginName}: AssetBundle unloaded.");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"{PluginName}: AssetBundle unload error: {ex}");
                }
                finally
                {
                    assetBundle = null;
                }
            }

            if (RootObject != null)
            {
                UnityEngine.Object.Destroy(RootObject);
                RootObject = null;
            }

            if (configSync != null)
            {
                configSync.lockedConfigChanged -= ConfigManager.Instance.OnConfigLockChanged;
                configSync = null;
            }

            Instance = null;
        }

        public static bool IsObjectDBValid() => ObjectDB.instance != null;

        private void LoadAssets()
        {
            if (assetBundle != null)
            {
                Debug.Log($"{PluginName}: AssetBundle already loaded.");
                return;
            }

            try
            {
                assetBundle = GetAssetBundleFromResources("vabackpacks");
                if (assetBundle == null) throw new Exception("Bundle not found");

                VAItemManager.LoadItems("Assets/Custom/VAitems/", this);

                VAPieceManager.LoadPieces("Assets/Custom/VAitems/", PluginName);

                Debug.Log($"{PluginName}: Loaded {itemPrefabs.Count} items, {piecePrefabs.Count} pieces");
            }
            catch (Exception ex)
            {
                Debug.LogError($"{PluginName}: Asset load failed: {ex}");
            }
        }

        private AssetBundle GetAssetBundleFromResources(string name)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                Debug.LogError($"{PluginName}: No embedded resource ending with '{name}'. Available: {string.Join(", ", assembly.GetManifestResourceNames())}");
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            return AssetBundle.LoadFromStream(stream);
        }

        public static void AddPrefabsToZNetScene(ZNetScene zNetScene)
        {
            if (zNetScene == null) return;

            int added = 0;
            foreach (var prefab in piecePrefabs.Concat(itemPrefabs))
            {
                if (prefab != null && zNetScene.GetPrefab(prefab.name) == null)
                {
                    zNetScene.m_prefabs.Add(prefab);
                    added++;
                }
            }

            zNetScene.m_prefabs.RemoveAll(p => p == null);
            if (added > 0) Debug.Log($"{PluginName}: added {added} prefab(s) to ZNetScene");
        }

        public static void AddItemsToObjectDB()
        {
            if (!IsObjectDBValid()) return;

            var registered = new List<string>();
            foreach (var item in itemPrefabs)
            {
                if (item == null) continue;

                if (ObjectDB.instance.GetItemPrefab(item.name) != null) continue;

                ObjectDB.instance.m_items.Add(item);

                var hashDict = (Dictionary<int, GameObject>)typeof(ObjectDB)
                    .GetField("m_itemByHash", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(ObjectDB.instance);

                if (hashDict != null)
                {
                    int hash = item.name.GetStableHashCode();
                    if (!hashDict.ContainsKey(hash))
                        hashDict[hash] = item;
                }

                registered.Add(item.name);
            }
            if (registered.Count > 0) Debug.Log($"{PluginName}: registered {registered.Count} item(s) in ObjectDB: {string.Join(", ", registered)}");
        }

        private IEnumerator WaitForZNetReady()
        {
            Debug.Log($"{PluginName}: Waiting for ZNetScene & ObjectDB...");

            while (ZNetScene.instance == null || ObjectDB.instance == null)
                yield return null;

            yield return new WaitForEndOfFrame();

            AddItemsToObjectDB();

            AddPrefabsToZNetScene(ZNetScene.instance);

            VAPieceManager.AddPiecesToHammer();
            VARecipeManager.UpdatePieceRequirements();

            Debug.Log($"{PluginName}: All items & pieces fully registered — ready!");
        }

        public enum CraftingStationType
        {
            Workbench,
            Forge,
            ArtisanStation,
            Stonecutter,
            Cauldron,
            BlackForge,
            GaldrTable,
        }

        // OBJECTDB COPY PATCH
        [HarmonyPatch(typeof(ObjectDB), "CopyOtherDB")]
        public static class ObjectDB_CopyOtherDB_Patch
        {
            public static void Postfix()
            {
                if (!IsObjectDBValid()) return;

                AddItemsToObjectDB();
                FiresCore.Materials.VanillaShaderRebind.RebindPrefabs(itemPrefabs.Concat(piecePrefabs), PluginName);
            }
        }

        // ZNETSCENE AWAKE PATCH
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        public static class ZNetScene_Awake_Patch
        {
            public static void Prefix(ZNetScene __instance)
            {
                if (__instance == null) return;
                AddPrefabsToZNetScene(__instance);
                FiresCore.Materials.VanillaShaderRebind.RebindPrefabs(itemPrefabs.Concat(piecePrefabs), PluginName);
            }
        }

        // =============================================================================
        // BACKPACK EQUIP / UNEQUIP PATCHES
        // =============================================================================
        // NOTE: The Humanoid.EquipItem / UnequipItem postfix patches that
        // used to live here have been REMOVED. They duplicated the gated
        // versions already present in BackpackManager
        // (Humanoid_EquipItem_Debug_Patch, Humanoid_UnequipItem_Patch),
        // which correctly fire OnEquipItem / OnUnequipItem only for
        // backpack items. With both sets of patches active, every equip
        // or unequip caused HandleEquipped / HandleUnequipped to run
        // twice — and on the second HandleEquipped the just-equipped
        // bag's items were skipped in capture but re-deserialized in
        // restore, overflowing duplicates into the player's main
        // inventory (see BackpackStorageSync.cs comment around _deserializedBags).
        // BackpackManager's patches are the authoritative ones and
        // remain in place.

        // =============================================================================
        // UPDATE METHOD FOR HOTKEY SUPPORT
        // =============================================================================
        public void Update()
        {
            BackpackManager.Update();
        }
    }
}