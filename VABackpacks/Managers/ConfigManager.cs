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

namespace VerdantsAscent.Managers
{
    public class ConfigManager
    {
        private static ConfigManager _instance;
        public static ConfigManager Instance => _instance ?? (_instance = new ConfigManager());
        public bool _isProcessingSync = false;
        private FileSystemWatcher configWatcher;
        // Stamped on every ConfigEntry write we make (SettingChanged fires on .Value sets and
        // SaveOnConfigSet rewrites the .cfg on the same set) - lets the watcher tell our own
        // file writes from an external/admin edit, so interactive UI writes can't loop us into
        // endless self-reloads (+ synced-entry rebroadcasts).
        private DateTime _lastSelfWriteUtc = DateTime.MinValue;
        private const double SelfWriteSuppressSeconds = 3.0;

        private void SelfWriteStamp(object sender, BepInEx.Configuration.SettingChangedEventArgs e)
            => _lastSelfWriteUtc = DateTime.UtcNow;

        private bool IsRecentSelfWrite()
            => (DateTime.UtcNow - _lastSelfWriteUtc).TotalSeconds < SelfWriteSuppressSeconds;
        private bool _isShuttingDown = false;
        private DateTime _lastConfigUpdate = DateTime.MinValue;
        private Coroutine _reloadCoroutine;

        // ────── General Settings ──────
        public ConfigEntry<bool> configVerboseLogging;
        public ConfigEntry<bool> configEnableRecipes;

        // ────── Backpack Settings (Global) ──────
        public ConfigEntry<KeyCode> configBackpackHotkey;

        // ────── Backpack Camp Settings ──────
        public ConfigEntry<bool>    configCampEnabled;
        public ConfigEntry<KeyCode> configCampGestureKey;
        public ConfigEntry<float>   configCampHoldDuration;
        public ConfigEntry<float>   configCampRadius;
        public ConfigEntry<float>   configCampMinSeparation;
        public ConfigEntry<string> configCampCampfirePrefab;
        public ConfigEntry<string> configCampWorkbenchPrefab;
        public ConfigEntry<string> configCampContainerPrefab;
        public ConfigEntry<string> configCampBedPrefab;
        public ConfigEntry<string> configCampWoodpilePrefab;
        public ConfigEntry<string> configCampChairPrefab;
        public ConfigEntry<string> configCampCookingStationPrefab;
        public ConfigEntry<string> configCampTerrainModPrefab;
        public ConfigEntry<float>  configCampFireOffsetForward;
        public ConfigEntry<float>  configCampHoboStickDeployDelay;
        public ConfigEntry<float>  configCampDeployRadius;

        // Per-element toggles — when a blueprint is in use, these decide
        // which procedural pieces still spawn alongside the blueprint.
        // The chest is always required (it holds the bag's contents) so
        // it is NOT toggleable. Without a blueprint, all toggles are
        // ignored and the procedural camp uses its defaults.
        public ConfigEntry<bool>   configCampBlueprintSpawnFire;
        public ConfigEntry<bool>   configCampBlueprintSpawnWorkbench;
        public ConfigEntry<bool>   configCampBlueprintSpawnBed;
        public ConfigEntry<bool>   configCampBlueprintSpawnWoodpile;
        public ConfigEntry<bool>   configCampBlueprintSpawnChair;
        public ConfigEntry<bool>   configCampBlueprintSpawnCookingStation;
        public ConfigEntry<bool>   configCampBlueprintSpawnTerrainPatch;

        // ────── Per-Backpack Settings (auto-generated) ──────
        public readonly Dictionary<string, ConfigEntry<int>> configBackpackRows = new();
        public readonly Dictionary<string, ConfigEntry<int>> configBackpackColumns = new();
        public readonly Dictionary<string, ConfigEntry<float>> configBackpackWeightReduction = new();
        public readonly Dictionary<string, ConfigEntry<bool>> configBackpackColdResistance = new();
        public readonly Dictionary<string, ConfigEntry<bool>> configBackpackWetResistance = new();
        public readonly Dictionary<string, ConfigEntry<bool>> configBackpackTeleportable = new();
        public readonly Dictionary<string, ConfigEntry<bool>> configBackpackFeatherFall = new();
        public readonly Dictionary<string, ConfigEntry<float>> configBackpackSpeedModifier = new();
        public readonly Dictionary<string, ConfigEntry<bool>> configBackpackStrengthBonus = new();
        // PlanBuild blueprint to spawn as the camp layout for this backpack.
        // Empty = use the default procedural layout (campfire + chest + ...).
        // Non-empty = look up the blueprint by name in PlanBuild's blueprint
        // folder and spawn its pieces around the campfire as the camp.
        public readonly Dictionary<string, ConfigEntry<string>> configBackpackCampBlueprint = new();
        // Per-backpack vertical offset applied to every blueprint piece's
        // y-position at deploy time. Useful for sinking buildings into
        // the ground (negative values) or raising elevated blueprints
        // (positive values). Has no effect when the blueprint is empty.
        public readonly Dictionary<string, ConfigEntry<float>> configBackpackCampBlueprintYOffset = new();

        // ────── Piece Recipes (Synced) ──────
        public readonly Dictionary<string, ConfigEntry<string>> configPerPieceRecipes = new();
        public readonly Dictionary<string, ConfigEntry<bool>> configPerPieceEnabled = new();

        public void Initialize(ConfigFile config)
        {
            // ────── General Settings ──────
            // Off by default (1.0.17; Fire, 2026-10-01: cut verbose logging; every other Fires mod defaults it off).
            configVerboseLogging = config.Bind("General", "VerboseLogging", false,
                "Enable detailed debug logs.");
            configEnableRecipes = config.Bind("General", "EnableRecipes", true,
                "Master switch for all custom recipes.");

            // ────── Backpack Settings (Global) ──────
            configBackpackHotkey = config.Bind(
                "Backpacks", "OpenHotkey", KeyCode.B,
                "Key to open the backpack inventory window.");

            // ────── Backpack Camp Settings ──────
            // The camp system turns a dropped backpack into a temporary base:
            // hold Shift+E for N seconds on a backpack ItemDrop to deploy a
            // campfire + workbench + the backpack as a chest. Hold Shift+E
            // for the same duration on the deployed chest to pack it back up.
            // One camp per character per server.
            configCampEnabled = config.Bind(
                "Backpacks.Camp", "Enabled", true,
                "Enable the deploy-as-camp feature for backpacks dropped on the ground.");

            configCampGestureKey = config.Bind(
                "Backpacks.Camp", "GestureKey", KeyCode.B,
                "Second key of the camp gesture. Left-Shift is always required (hardcoded). " +
                "Default 'B' so the gesture is Shift+B. Avoid keys vanilla binds (E, F, R, Q, etc.) " +
                "or you'll fight the pickup/use logic.");

            configCampHoldDuration = config.Bind(
                "Backpacks.Camp", "HoldDurationSeconds", 3f,
                new ConfigDescription("How long the player must hold Shift + GestureKey to deploy or pack a camp.",
                    new AcceptableValueRange<float>(0.5f, 10f)));

            configCampRadius = config.Bind(
                "Backpacks.Camp", "PieceSpawnRadius", 5f,
                new ConfigDescription("Distance from the campfire (anchor) at which the chest and workbench spawn. Each gets a random direction.",
                    new AcceptableValueRange<float>(2f, 15f)));

            configCampMinSeparation = config.Bind(
                "Backpacks.Camp", "PieceMinSeparation", 2f,
                new ConfigDescription("Minimum distance between any two camp pieces (chest, workbench, fire). Prevents overlap.",
                    new AcceptableValueRange<float>(1f, 10f)));

            configCampCampfirePrefab = config.Bind(
                "Backpacks.Camp", "CampfirePrefab", "fire_pit",
                "ZNetScene prefab name for the camp's campfire. Default 'fire_pit' is the basic Valheim campfire.");

            configCampWorkbenchPrefab = config.Bind(
                "Backpacks.Camp", "WorkbenchPrefab", "piece_workbench",
                "ZNetScene prefab name for the camp's workbench.");

            configCampContainerPrefab = config.Bind(
                "Backpacks.Camp", "ContainerPrefab", "piece_chest_wood",
                "ZNetScene prefab name used as the deployed-backpack container. Inventory dimensions are taken from the backpack's config, not the prefab's.");

            configCampBedPrefab = config.Bind(
                "Backpacks.Camp", "BedPrefab", "rawood_bed_bal,bed",
                "Comma-separated fallback chain of bed prefab names. Tries each in order, uses the first that's registered with ZNetScene. Default prefers Balrond's Amazing Nature 'rawood_bed_bal' when that mod is loaded, falls back to vanilla 'bed'. Roof check is bypassed inside the camp radius.");

            configCampWoodpilePrefab = config.Bind(
                "Backpacks.Camp", "WoodpilePrefab", "wood_stack",
                "ZNetScene prefab for the decorative wood pile. Set to empty string to skip.");

            configCampChairPrefab = config.Bind(
                "Backpacks.Camp", "ChairPrefab", "piece_logbench01,piece_chair",
                "Comma-separated fallback chain of chair prefab names. Defaults to the log bench (piece_logbench01) which seats nicely near a fire; falls back to the basic chair if missing.");

            configCampCookingStationPrefab = config.Bind(
                "Backpacks.Camp", "CookingStationPrefab", "piece_cookingstation",
                "ZNetScene prefab for the cooking grill placed over the campfire. Set to empty string to skip.");

            configCampTerrainModPrefab = config.Bind(
                "Backpacks.Camp", "TerrainModifierPrefab", "mud_road",
                "Terrain-modifier prefab spawned at the camp center to level + paint dirt. Removed (and terrain restored) on pack-up. Set to empty to skip.");

            configCampFireOffsetForward = config.Bind(
                "Backpacks.Camp", "FireOffsetForward", 1.5f,
                new ConfigDescription("Distance to push the campfire away from the player along the player's facing direction. Prevents spawning the fire directly under the player and burning them.",
                    new AcceptableValueRange<float>(0f, 5f)));

            configCampHoboStickDeployDelay = config.Bind(
                "Backpacks.Camp", "HoboStickDeployDelaySeconds", 2f,
                new ConfigDescription("Seconds to wait after the HoboStick MMB swing animation before the camp deploys. The vanilla sledge animation slam-frame lands around 1.5-1.8s in, so 2s aligns the deploy with the visible impact. The runtime enforces a minimum of 2s to keep the swing from being cut short before the slam plays.",
                    new AcceptableValueRange<float>(2f, 10f)));

            configCampDeployRadius = config.Bind(
                "Backpacks.Camp", "DeployInteractRadius", 3f,
                new ConfigDescription("How close (meters) the player must be to a dropped backpack — or to a deployed camp chest — for the Shift+B hold gesture to count. Hover-text still requires looking at the object, but the gesture itself doesn't.",
                    new AcceptableValueRange<float>(1f, 10f)));

            // Per-element blueprint toggles. When a blueprint IS configured
            // for the backpack being deployed, these decide which procedural
            // pieces are also spawned. Defaults: all true, so a blueprint
            // adds itself to the existing camp rather than replacing the
            // procedural pieces. Set any to false to strip that element
            // (e.g. SpawnBed=false if your blueprint includes a bed).
            // The chest is always required and is NOT a toggle.
            configCampBlueprintSpawnFire = config.Bind(
                "Backpacks.Camp.Blueprint", "SpawnFire", true,
                "When a blueprint is in use: also spawn the procedural campfire. Disable if your blueprint includes one.");
            configCampBlueprintSpawnWorkbench = config.Bind(
                "Backpacks.Camp.Blueprint", "SpawnWorkbench", true,
                "When a blueprint is in use: also spawn the procedural workbench. Disable if your blueprint includes one.");
            configCampBlueprintSpawnBed = config.Bind(
                "Backpacks.Camp.Blueprint", "SpawnBed", true,
                "When a blueprint is in use: also spawn the procedural bed. Disable if your blueprint includes one.");
            configCampBlueprintSpawnWoodpile = config.Bind(
                "Backpacks.Camp.Blueprint", "SpawnWoodpile", true,
                "When a blueprint is in use: also spawn the procedural wood pile (the daily-quota wood source).");
            configCampBlueprintSpawnChair = config.Bind(
                "Backpacks.Camp.Blueprint", "SpawnChair", true,
                "When a blueprint is in use: also spawn the procedural chair / log bench by the fire.");
            configCampBlueprintSpawnCookingStation = config.Bind(
                "Backpacks.Camp.Blueprint", "SpawnCookingStation", true,
                "When a blueprint is in use: also spawn the cooking grill on top of the campfire.");
            configCampBlueprintSpawnTerrainPatch = config.Bind(
                "Backpacks.Camp.Blueprint", "SpawnTerrainPatch", true,
                "When a blueprint is in use: also flatten + paint a dirt patch under the camp. Disable if your blueprint includes its own terrain heights.");

            // ────── Per-Backpack Settings (auto-generated from BackpackPrefabNames) ──────
            foreach (var prefabName in BackpackManager.BackpackPrefabNames)
            {
                string section = $"Backpack.{prefabName}";

                // 1.0.19 (Fire 10-07): a grid per tier (columns x rows) - Small 5x2, Day 6x3, Explorer 7x4, the camp bags 5x3;
                // an upgrade (quality 2+) adds special slots, never grid. Others' bags keep the old 6x4.
                (int cols, int rows) grid = DefaultGrid(prefabName);
                configBackpackRows[prefabName] = config.Bind(section, "Rows", grid.rows,
                    new ConfigDescription($"Number of rows in {prefabName} inventory grid.", new AcceptableValueRange<int>(1, 20)));

                configBackpackColumns[prefabName] = config.Bind(section, "Columns", grid.cols,
                    new ConfigDescription($"Number of columns in {prefabName} inventory grid.", new AcceptableValueRange<int>(1, 20)));

                configBackpackWeightReduction[prefabName] = config.Bind(section, "WeightReductionMultiplier", 0.5f,
                    new ConfigDescription($"Multiplier for {prefabName} contents weight (0.5 = 50% reduction).", new AcceptableValueRange<float>(0f, 1f)));

                configBackpackColdResistance[prefabName] = config.Bind(section, "ColdResistance", false,
                    $"Grants cold resistance while {prefabName} is equipped.");

                configBackpackWetResistance[prefabName] = config.Bind(section, "WetResistance", false,
                    $"Grants wet resistance while {prefabName} is equipped.");

                configBackpackTeleportable[prefabName] = config.Bind(section, "Teleportable", true,
                    $"Allow teleporting with {prefabName} contents.");

                configBackpackFeatherFall[prefabName] = config.Bind(section, "FeatherFall", false,
                    $"Grants feather fall (no fall damage) while {prefabName} is equipped.");

                configBackpackSpeedModifier[prefabName] = config.Bind(section, "SpeedModifier", 0f,
                    new ConfigDescription($"Movement speed modifier while {prefabName} is equipped (positive = faster, negative = slower).", new AcceptableValueRange<float>(-0.5f, 0.5f)));

                configBackpackCampBlueprint[prefabName] = config.Bind(section, "CampBlueprint", "",
                    $"PlanBuild blueprint name (without .blueprint extension) to use as the camp layout when this backpack is deployed. " +
                    $"Searches the dedicated-server PlanBuild folder first, then the local Steam install. " +
                    $"Empty = use the default procedural layout (campfire + chest + workbench + bed + chair + woodpile + cooking station).");

                configBackpackCampBlueprintYOffset[prefabName] = config.Bind(section, "CampBlueprintYOffset", 0f,
                    new ConfigDescription(
                        $"Vertical offset (meters) applied to every blueprint piece for this backpack's camp. " +
                        $"Negative values sink the building into the ground (e.g. -2 for a half-buried foundation), " +
                        $"positive values raise it. No effect when CampBlueprint is empty.",
                        new AcceptableValueRange<float>(-10f, 10f)));

                configBackpackStrengthBonus[prefabName] = config.Bind(section, "StrengthBonus", false,
                    $"Grants strength bonus (increased carry weight) while {prefabName} is equipped.");
            }

            // ────── Piece Recipes ──────
            foreach (var pieceName in VAPieceManager.PieceNames)
            {
                string section = $"Pieces.{pieceName}";
                configPerPieceRecipes[pieceName] = config.Bind(section, "Recipe",
                    "Wood,5",
                    $"Recipe string for {pieceName} (name,amount,perLevel,…). Use format like 'Wood,5' for simple, or 'Wood,5,2,Stone,3,1' for leveled.");
                configPerPieceEnabled[pieceName] = config.Bind(section, "Enabled", true,
                    $"Enable piece {pieceName}.");
            }

            ValidateConfigs();
            SetupConfigWatcher(config);
        }

        private static (int cols, int rows) DefaultGrid(string prefabName)
        {
            switch (prefabName)
            {
                case "SmallBag_va": return (5, 2);
                case "DayBag_va": return (6, 3);
                case "ExplorerBag_va": return (7, 4);
                case "HoboStick":
                case "StaffFiresWizard": return (5, 3);
                default: return (6, 4);
            }
        }

        private void SetupConfigWatcher(ConfigFile config)
        {
            if (configWatcher != null) return;
            string configPath = config.ConfigFilePath;
            if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath)) return;
            string directory = Path.GetDirectoryName(configPath);
            string fileName = Path.GetFileName(configPath);
            config.SettingChanged -= SelfWriteStamp;
            config.SettingChanged += SelfWriteStamp;

            configWatcher = new FileSystemWatcher
            {
                Path = directory,
                Filter = fileName,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true
            };
            configWatcher.Changed += (sender, e) =>
            {
                if (_isShuttingDown || _isProcessingSync) return;
                if (IsRecentSelfWrite()) return;
                if (_reloadCoroutine != null) return;
                _reloadCoroutine = MyMod.Instance.StartCoroutine(ReloadConfigAfterDelay());
            };
            configWatcher.Renamed += (sender, e) =>
            {
                if (_isShuttingDown || _isProcessingSync) return;
                if (IsRecentSelfWrite()) return;
                if (_reloadCoroutine != null) return;
                _reloadCoroutine = MyMod.Instance.StartCoroutine(ReloadConfigAfterDelay());
            };
        }

        private IEnumerator ReloadConfigAfterDelay()
        {
            yield return new WaitForSeconds(2f);
            if (_isShuttingDown || _isProcessingSync) { _reloadCoroutine = null; yield break; }
            if ((DateTime.Now - _lastConfigUpdate).TotalMilliseconds < 2000) { _reloadCoroutine = null; yield break; }
            if (IsRecentSelfWrite()) { _reloadCoroutine = null; yield break; }
            _lastConfigUpdate = DateTime.Now;
            Debug.Log($"{MyMod.PluginName}: Config file changed, reloading...");
            var configFile = MyMod.Instance.Config;
            bool saveOnSet = configFile.SaveOnConfigSet;
            configFile.SaveOnConfigSet = false;
            MyMod.Instance.Config.Reload();
            ValidateConfigs();
            configFile.SaveOnConfigSet = saveOnSet;
            VARecipeManager.UpdatePieceRequirements();
            BackpackManager.OnConfigReloaded();
            if (configVerboseLogging.Value)
                Debug.Log($"{MyMod.PluginName}: Config reloaded and synced due to file change.");
            _reloadCoroutine = null;
        }

        public void ValidateConfigs()
        {
            foreach (var kv in configPerPieceRecipes)
            {
                var parts = kv.Value.Value.Split(',').Select(s => s.Trim()).ToArray();
                if (parts.Length % 3 != 0 || parts.Any(string.IsNullOrEmpty))
                    kv.Value.Value = (string)kv.Value.DefaultValue;
            }
        }

        public void OnConfigLockChanged()
        {
            ValidateConfigs();
            VARecipeManager.UpdatePieceRequirements();
            if (configVerboseLogging.Value)
                Debug.Log($"{MyMod.PluginName}: Config lock changed.");
        }

        public void Dispose()
        {
            _isShuttingDown = true;
            configWatcher?.Dispose();
        }
    }
}