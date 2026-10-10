using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Managers
{
    public static class BackpackManager
    {
        // List of registered backpack prefab names. Mutable so BackpackApi
        // can register/unregister at runtime. Built-in prefabs are seeded
        // here; additional ones can be added at startup by other mods or
        // content packs via BackpackApi.RegisterBackpack.
        public static readonly HashSet<string> BackpackPrefabNames = new HashSet<string>
        {
            "SmallBag_va",
            "DayBag_va",
            "ExplorerBag_va",
            "HoboStick",
            "StaffFiresWizard",
        };

        // Backpacks that are also weapons (TwoHandedWeapon) and use the
        // MMB sledgehammer-slam → camp-deploy gesture instead of being
        // treated like a Shoulder-slot bag. To make a new prefab behave
        // like the HoboStick, add it here AND to BackpackPrefabNames.
        // To make it behave like a normal Shoulder backpack, leave it
        // out of this set.
        public static readonly HashSet<string> SlamDeployBackpacks = new HashSet<string>
        {
            "HoboStick",
            "StaffFiresWizard",
        };

        // Per-prefab gameplay/storage config. Public so BackpackApi can
        // mutate it during registration. External code should go through
        // BackpackApi.GetConfig / RegisterBackpack rather than touching
        // this dictionary directly.
        public static readonly Dictionary<string, BackpackConfig> Configs = new();

        // Back-compat alias — older code in this file references _configs
        // directly. Kept for minimal-diff refactor; new code uses Configs.
        private static Dictionary<string, BackpackConfig> _configs => Configs;

        private static FieldInfo _containerInventoryField;
        private static FieldInfo _inventoryGuiCurrentContainerField;
        private static bool _inShowPatch = false; // Flag to prevent recursion in Show patch

        // ── Storage-mode toggle ────────────────────────────────────────
        // LegacyMode = true  → VAInventory is NOT loaded; VABackpacks owns
        //                      a Container on the Player and uses the
        //                      vanilla container-window flow (the old code).
        // LegacyMode = false → VAInventory IS loaded; storage lives in the
        //                      equipment panel's expansion slots, and most
        //                      Container-flow patches early-return. The
        //                      backpack item still carries its data via
        //                      customData["BackpackInventory"], so both
        //                      modes round-trip the same on-disk format.
        //
        // Auto-detected at Initialize; can be force-overridden via config
        // (UseLegacyContainer) for debugging.
        public static bool LegacyMode { get; private set; } = true;

        public const string VAINVENTORY_PLUGIN_GUID = "com.Fire.VAInventory";

        // ── Cape + Backpack coexistence ────────────────────────────────
        // Backpacks have m_itemType = Shoulder, the same slot capes use.
        // Vanilla EquipItem assigns Shoulder items to m_shoulderItem,
        // kicking out whatever was there before — so equipping a backpack
        // would unequip the player's cape and vice versa.
        //
        // To let both coexist we keep backpacks OUT of m_shoulderItem
        // entirely. Instead we Harmony-prefix EquipItem/UnequipItem on
        // backpacks and route them into _equippedBackpacks (keyed by
        // Humanoid). Vanilla never sees the backpack as "equipped via
        // m_shoulderItem", so capes get the slot to themselves.
        //
        // We also patch IsItemEquiped postfix to OR-in our dict, so any
        // code (vanilla or modded) that asks "is this backpack equipped"
        // gets a truthful yes. The backpack's ItemData.m_equipped flag
        // is also set so Inventory.Save round-trips the equipped state
        // correctly across logout.
        //
        // While a backpack is in our dict (not m_shoulderItem), vanilla's
        // VisEquipment doesn't draw it, so BackpackWornVisual (1.0.24)
        // draws the bag's worn model on the player's back itself, next to
        // any cape.
        private static readonly Dictionary<Humanoid, ItemDrop.ItemData> _equippedBackpacks = new();

        /// <summary>True if <paramref name="humanoid"/> currently has a
        /// backpack tracked in our coexistence dict. Used by IsItemEquiped
        /// postfix and other code that wants to know about backpacks
        /// without going through vanilla equipment fields.</summary>
        public static bool TryGetEquippedBackpack(Humanoid humanoid, out ItemDrop.ItemData item)
            => _equippedBackpacks.TryGetValue(humanoid, out item);

        /// <summary>Public accessor used by BackpackApi to find the
        /// currently-equipped backpack on a Player.</summary>
        /// <summary>1.0.20: the live bag inventory on the player's Container in legacy mode (null otherwise), and the bag that owns it.</summary>
        internal static Inventory LegacyLiveInventory(Player player, out ItemDrop.ItemData owner)
        {
            owner = _activeContainerOwner;
            if (!LegacyMode || player == null || owner == null) return null;
            Container c = player.GetComponent<Container>();
            return c != null ? (Inventory)_containerInventoryField?.GetValue(c) : null;
        }

        public static ItemDrop.ItemData FindEquippedBackpackPublic(Player player)
            => FindEquippedBackpack(player);

        /// <summary>Public accessor used by BackpackApi to enumerate all
        /// currently-equipped registered backpacks on a Player. See
        /// FindAllEquippedBackpacks for ordering details.</summary>
        public static System.Collections.Generic.IReadOnlyList<ItemDrop.ItemData>
            FindAllEquippedBackpacksPublic(Player player)
            => FindAllEquippedBackpacks(player);

        // BackpackConfig moved to its own file (BackpackConfig.cs) so
        // VAInventory and other consumers can reference the type without
        // poking at internals. Same namespace, so callers in this file
        // keep working unchanged.

        public static void Initialize()
        {
            _containerInventoryField = typeof(Container).GetField("m_inventory", BindingFlags.Instance | BindingFlags.NonPublic);
            _inventoryGuiCurrentContainerField = typeof(InventoryGui).GetField("m_currentContainer", BindingFlags.Instance | BindingFlags.NonPublic);

            if (_containerInventoryField == null || _inventoryGuiCurrentContainerField == null)
            {
                Debug.LogError($"{MyMod.PluginName}: Reflection failed - backpack UI will not work properly");
            }

            // Populate configs from ConfigManager dictionaries
            foreach (string prefabName in BackpackPrefabNames)
            {
                _configs[prefabName] = new BackpackConfig
                {
                    Rows = ConfigManager.Instance.configBackpackRows[prefabName].Value,
                    Columns = ConfigManager.Instance.configBackpackColumns[prefabName].Value,
                    WeightReduction = ConfigManager.Instance.configBackpackWeightReduction[prefabName].Value,
                    ColdResistance = ConfigManager.Instance.configBackpackColdResistance[prefabName].Value,
                    WetResistance = ConfigManager.Instance.configBackpackWetResistance[prefabName].Value,
                    Teleportable = ConfigManager.Instance.configBackpackTeleportable[prefabName].Value,
                    FeatherFall = ConfigManager.Instance.configBackpackFeatherFall[prefabName].Value,
                    SpeedModifier = ConfigManager.Instance.configBackpackSpeedModifier[prefabName].Value,
                    StrengthBonus = ConfigManager.Instance.configBackpackStrengthBonus[prefabName].Value
                };
            }

            // Auto-detect VAInventory. When loaded, hand off storage display
            // to the equipment-panel slots; we just publish events and keep
            // status-effect application. When absent, fall back to the old
            // Container-on-Player path so VABackpacks remains usable solo.
            DetectStorageMode();

            Debug.Log($"{MyMod.PluginName}: BackpackManager initialized with {BackpackPrefabNames.Count} backpacks. " +
                      $"Storage mode: {(LegacyMode ? "Legacy Container" : "VAInventory slots")}");
        }

        // Whether the storage-mode check has run since the last
        // assembly-load event. We run it once at Initialize (which may
        // be too early — see comment below) and again at first
        // Player.OnSpawned, which is guaranteed late enough that all
        // BepInEx plugins have loaded.
        private static bool _storageModeFinalized;

        /// <summary>
        /// Decide between legacy-Container and VAInventory-slots storage by
        /// looking at the loaded assembly list for VAInventory.
        ///
        /// IMPORTANT: BepInEx plugin load order isn't guaranteed. If
        /// VABackpacks runs Initialize() BEFORE VAInventory's assembly
        /// is loaded into the AppDomain, this check sees no VAInventory
        /// and falls back to legacy mode — even though VAInventory will
        /// load moments later. Symptom in logs: lots of "[VAInventory]"
        /// tag lines despite VABackpacks logging "[legacy mode]".
        ///
        /// To avoid that race we run detection a second time on the
        /// first Player.OnSpawned (see Player_OnSpawned_Patch below),
        /// which fires well after every plugin's Awake. The first call
        /// from Initialize() still sets a useful default for any code
        /// that runs pre-spawn. Calls after _storageModeFinalized are
        /// no-ops so the mode can't oscillate after a player has
        /// already spawned with a Container set up.
        /// </summary>
        public static void DetectStorageMode()
        {
            if (_storageModeFinalized) return;
            try
            {
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm == null) continue;
                    string n = asm.GetName().Name;
                    if (n == "VAInventory" || n == "com.Fire.VAInventory")
                    {
                        LegacyMode = false;
                        Debug.Log($"{MyMod.PluginName}: VAInventory detected - using slot-based storage");
                        return;
                    }
                }
                LegacyMode = true;
                Debug.Log($"{MyMod.PluginName}: VAInventory not detected (yet) - defaulting to legacy Container storage; will re-check on player spawn.");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"{MyMod.PluginName}: Storage-mode detection failed; defaulting to legacy. {ex.Message}");
                LegacyMode = true;
            }
        }

        public static void Cleanup()
        {
            _configs.Clear();
        }

        // Add Container to player on spawn (legacy storage path only) +
        // re-fire equip event for whatever's already in an armor slot.
        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        public static class Player_OnSpawned_Patch
        {
            public static void Postfix(Player __instance)
            {
                // Re-run storage-mode detection now that the AppDomain
                // is fully loaded. Initialize() may have run before
                // VAInventory's assembly was loaded (BepInEx plugin
                // order isn't guaranteed) — by OnSpawned every plugin's
                // assemblies are guaranteed to be present.
                if (!_storageModeFinalized)
                {
                    DetectStorageMode();
                    _storageModeFinalized = true;
                    Debug.Log($"{MyMod.PluginName}: Storage mode finalized on first spawn: " +
                              $"{(LegacyMode ? "Legacy Container" : "VAInventory slots")}");
                }

                // Container is only needed when we own storage. In slots
                // mode VAInventory handles persistence directly via the
                // backpack item's customData, so no Container is required.
                if (LegacyMode)
                {
                    if (__instance.gameObject.GetComponent<Container>() == null)
                    {
                        __instance.gameObject.AddComponent<Container>();
                        Debug.Log($"{MyMod.PluginName}: Added Container to player {__instance.GetPlayerName()} on spawn (legacy mode)");
                    }
                }

                // Re-fire equip in both modes — slots mode needs the event
                // to populate the equipment panel; legacy mode needs the
                // Container hookup to refresh.
                ItemDrop.ItemData equippedBackpack = FindEquippedBackpack(__instance);
                if (equippedBackpack != null)
                {
                    // Coexistence migration: if the backpack is sitting in
                    // m_shoulderItem from a pre-coexistence save, move it
                    // into our dict and free the vanilla slot so a cape
                    // can occupy it.
                    MigrateBackpackFromVanillaSlots(__instance, equippedBackpack);

                    OnEquipItem(__instance, equippedBackpack);
                    Debug.Log($"{MyMod.PluginName}: Refreshed equipped backpack on spawn/login");
                }
            }
        }

        /// <summary>
        /// Move a backpack out of whichever vanilla equipment field
        /// currently holds it, into our coexistence dict. Used at spawn
        /// time to migrate saves that pre-date the cape coexistence patch
        /// — those saves have the backpack in m_shoulderItem, blocking
        /// capes from equipping. After this runs the vanilla slot is
        /// empty and the cape can take it.
        /// </summary>
        private static void MigrateBackpackFromVanillaSlots(Humanoid humanoid, ItemDrop.ItemData backpack)
        {
            if (humanoid == null || backpack == null) return;

            // Slam-deploy weapon-bags (HoboStick, StaffFiresWizard, …)
            // are TwoHandedWeapons with a backpack config grafted on
            // top — vanilla puts them in m_rightItem and the player
            // wields them. Migrating into the coexistence dict would
            // silently un-wield the weapon, so leave them alone.
            if (IsSlamDeployBackpack(backpack)) return;

            if (_equippedBackpacks.TryGetValue(humanoid, out var existing) && existing == backpack)
                return;     // already migrated

            // Walk every equipment field; whichever one holds the
            // backpack reference gets nulled. We check all nine because
            // some mods set the backpack as Chest or Utility.
            string[] fieldNames =
            {
                "m_shoulderItem", "m_chestItem", "m_legItem", "m_helmetItem",
                "m_utilityItem",  "m_trinketItem",
                "m_rightItem",    "m_leftItem",  "m_ammoItem",
            };
            foreach (var fieldName in fieldNames)
            {
                var field = typeof(Humanoid).GetField(fieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null) continue;
                if (!ReferenceEquals(field.GetValue(humanoid), backpack)) continue;

                field.SetValue(humanoid, null);
                Debug.Log($"{MyMod.PluginName}: Migrated backpack '{backpack.m_dropPrefab?.name}' " +
                          $"out of vanilla {fieldName} — cape slot is now free.");
                break;
            }

            _equippedBackpacks[humanoid] = backpack;
            backpack.m_equipped = true;

            // Refresh vis equipment so any equipped cape becomes visible
            // now that m_shoulderItem isn't occupied by the backpack.
            try { humanoid.SetupEquipment(); } catch { /* defensive */ }
        }

        // Cleanup: when a Player GameObject is destroyed (logout, scene
        // change, character switch), drop its entry from the coexistence
        // dict. Without this the dict accumulates stale Humanoid keys
        // that point at destroyed GameObjects, leaking memory and
        // breaking subsequent equip checks.
        [HarmonyPatch(typeof(Player), nameof(Player.OnDestroy))]
        public static class Player_OnDestroy_Coexistence_Patch
        {
            public static void Prefix(Player __instance)
            {
                if (__instance == null) return;
                _equippedBackpacks.Remove(__instance);
            }
        }

        /// <summary>
        /// Returns every currently-equipped registered backpack on the
        /// humanoid. Multi-bag equippers (e.g. shoulder bag + HoboStick
        /// in m_rightItem) appear in stable order:
        ///   1. Coexistence dict (Shoulder-type bags hidden from m_shoulderItem)
        ///   2. Armor slots (chest, legs, helmet, shoulder, utility, trinket)
        ///   3. Weapon slots (right, left, ammo)
        /// Each bag appears at most once. Order is stable across calls so
        /// VAInventory can keep a deterministic panel ordering.
        /// </summary>
        private static System.Collections.Generic.List<ItemDrop.ItemData>
            FindAllEquippedBackpacks(Humanoid humanoid)
        {
            var result = new System.Collections.Generic.List<ItemDrop.ItemData>();
            if (humanoid == null) return result;

            // 1. Coexistence dict
            if (_equippedBackpacks.TryGetValue(humanoid, out var tracked) && tracked != null)
                result.Add(tracked);

            // Helper: read a private equipment field via reflection,
            // accept the item if it's a backpack and isn't already in the
            // result list (the dict could in principle hold the same
            // reference, though our hijack is supposed to prevent that).
            void TryAdd(string fieldName)
            {
                var field = typeof(Humanoid).GetField(fieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null) return;
                if (field.GetValue(humanoid) is ItemDrop.ItemData item
                    && IsBackpack(item)
                    && !result.Contains(item))
                {
                    result.Add(item);
                }
            }

            // 2. Armor slots
            TryAdd("m_chestItem");
            TryAdd("m_legItem");
            TryAdd("m_helmetItem");
            TryAdd("m_shoulderItem");
            TryAdd("m_utilityItem");
            TryAdd("m_trinketItem");

            // 3. Weapon slots — HoboStick lands here (m_rightItem)
            TryAdd("m_rightItem");
            TryAdd("m_leftItem");
            TryAdd("m_ammoItem");

            return result;
        }

        // Find equipped backpack — ONLY in equipped armor/weapon slots (not loose in inventory)
        private static ItemDrop.ItemData FindEquippedBackpack(Humanoid humanoid)
        {
            // Weapon slots FIRST — specifically to catch the HoboStick
            // hybrid before falling back to the coexistence dict. Without
            // this, a player wearing a Shoulder backpack (tracked in our
            // dict) who then equips a HoboStick would have FindEquippedBackpack
            // keep reporting the Shoulder bag, while every FireEquipped /
            // FireUnequipped event was being fired for the HoboStick. That
            // mismatch left VAInventory's storage block bound to the wrong
            // item: the bag-inventory grid wouldn't render for the stick,
            // the player couldn't place items into it, and on deploy the
            // chest spawned with an empty BackpackInventory blob.
            //
            // Short-circuit for any slam-deploy weapon-bag (HoboStick,
            // StaffFiresWizard, …) so the bag-inventory grid binds to the
            // copy that's actually wielded. Adding a new prefab to
            // SlamDeployBackpacks automatically extends this behavior.
            FieldInfo rightField = typeof(Humanoid).GetField("m_rightItem", BindingFlags.Instance | BindingFlags.NonPublic);
            if (rightField != null && rightField.GetValue(humanoid) is ItemDrop.ItemData rightItem
                && IsSlamDeployBackpack(rightItem))
                return rightItem;

            // Coexistence dict — once a Shoulder backpack has been routed
            // here by our EquipItem prefix, vanilla equipment fields no
            // longer hold it (so the cape can take the shoulder slot).
            // Checking the dict before the legacy field scan keeps every
            // existing caller of FindEquippedBackpack working.
            if (_equippedBackpacks.TryGetValue(humanoid, out var tracked) && tracked != null)
                return tracked;

            // Armor slots (private fields — use reflection)
            FieldInfo chestField = typeof(Humanoid).GetField("m_chestItem", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo legField = typeof(Humanoid).GetField("m_legItem", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo helmetField = typeof(Humanoid).GetField("m_helmetItem", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo shoulderField = typeof(Humanoid).GetField("m_shoulderItem", BindingFlags.Instance | BindingFlags.NonPublic);

            if (chestField != null && chestField.GetValue(humanoid) is ItemDrop.ItemData chestItem && IsBackpack(chestItem)) return chestItem;
            if (legField != null && legField.GetValue(humanoid) is ItemDrop.ItemData legItem && IsBackpack(legItem)) return legItem;
            if (helmetField != null && helmetField.GetValue(humanoid) is ItemDrop.ItemData helmetItem && IsBackpack(helmetItem)) return helmetItem;
            if (shoulderField != null && shoulderField.GetValue(humanoid) is ItemDrop.ItemData shoulderItem && IsBackpack(shoulderItem)) return shoulderItem;

            // Other weapon slots (private fields) — m_rightItem already
            // partially handled above for the HoboStick case; recheck it
            // here for any non-HoboStick weapon-slot backpack.
            FieldInfo leftField = typeof(Humanoid).GetField("m_leftItem", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo ammoField = typeof(Humanoid).GetField("m_ammoItem", BindingFlags.Instance | BindingFlags.NonPublic);

            if (rightField != null && rightField.GetValue(humanoid) is ItemDrop.ItemData rightItem2 && IsBackpack(rightItem2)) return rightItem2;
            if (leftField != null && leftField.GetValue(humanoid) is ItemDrop.ItemData leftItem && IsBackpack(leftItem)) return leftItem;
            if (ammoField != null && ammoField.GetValue(humanoid) is ItemDrop.ItemData ammoItem && IsBackpack(ammoItem)) return ammoItem;

            // Utility and Trinket (Ashlands+ — private, reflection fallback)
            FieldInfo utilityField = typeof(Humanoid).GetField("m_utilityItem", BindingFlags.Instance | BindingFlags.NonPublic);
            if (utilityField != null && utilityField.GetValue(humanoid) is ItemDrop.ItemData utilityItem && IsBackpack(utilityItem)) return utilityItem;

            FieldInfo trinketField = typeof(Humanoid).GetField("m_trinketItem", BindingFlags.Instance | BindingFlags.NonPublic);
            if (trinketField != null && trinketField.GetValue(humanoid) is ItemDrop.ItemData trinketItem && IsBackpack(trinketItem)) return trinketItem;

            return null;
        }

        // A backpack the player equips opens its storage.
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
        public static class Humanoid_EquipItem_Debug_Patch
        {
            public static void Postfix(Humanoid __instance, ItemDrop.ItemData item, bool __result)
            {
                if (__result && __instance is Player player)
                {
                    if (IsBackpack(item))
                    {
                        OnEquipItem(player, item);
                        if (InventoryGui.instance != null && InventoryGui.IsVisible())
                        {
                            ShowBackpackContainer();
                        }
                    }
                }
            }
        }

        // Refresh backpack container when inventory opens (if equipped).
        // LEGACY-ONLY: in slots mode the backpack contents are part of the
        // equipment panel itself, so there's no separate container window
        // to auto-show.
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show))]
        public static class InventoryGui_Show_Patch
        {
            public static void Postfix(InventoryGui __instance)
            {
                if (!LegacyMode) return;       // slots mode — VAInventory owns the panel
                if (_inShowPatch) return;
                _inShowPatch = true;

                Player player = Player.m_localPlayer;
                if (player == null)
                {
                    _inShowPatch = false;
                    return;
                }

                ItemDrop.ItemData equippedBackpack = FindEquippedBackpack(player);
                if (equippedBackpack == null)
                {
                    _inShowPatch = false;
                    return;
                }

                Container container = player.gameObject.GetComponent<Container>();
                if (container == null)
                {
                    _inShowPatch = false;
                    return;
                }

                // Refresh with current config
                OnEquipItem(player, equippedBackpack);

                // Show the container
                __instance.Show(container);

                _inShowPatch = false;
            }
        }

        // ── COEXISTENCE HIJACK ───────────────────────────────────────
        // Backpacks have m_itemType = Shoulder, the same slot capes use.
        // Vanilla EquipItem would route the backpack to m_shoulderItem,
        // booting the cape. We Prefix EquipItem with priority First and
        // — when the item is a backpack — handle equip ourselves:
        //   1. Set item.m_equipped = true (so Inventory.Save persists it).
        //   2. Add to _equippedBackpacks dict (keyed by Humanoid).
        //   3. Set __result = true and return false to skip vanilla,
        //      keeping the backpack OUT of m_shoulderItem.
        // The existing Postfix logging patch + OnEquipItem flow still
        // fires (the Humanoid_EquipItem_Debug_Patch postfix runs after
        // a successful __result=true), so storage hookup & status
        // effects are unchanged.
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
        [HarmonyPriority(Priority.First)]
        public static class Humanoid_EquipItem_BackpackHijack_Patch
        {
            public static bool Prefix(Humanoid __instance, ItemDrop.ItemData item, ref bool __result)
            {
                if (item == null) return true;                 // let vanilla decide
                if (!IsHijackedBackpack(item)) return true;    // HoboStick + non-backpacks → vanilla path

                // Already equipped on THIS humanoid? Re-equip is a no-op
                // success — vanilla returns true in that case too.
                if (_equippedBackpacks.TryGetValue(__instance, out var existing) && existing == item)
                {
                    __result = true;
                    return false;
                }

                // If a DIFFERENT backpack is currently in the dict for
                // this humanoid, swap-out cleanly: fire OnUnequip on the
                // old one (saves customData → bag's customData), then
                // equip the new one. Mirrors vanilla's "Shoulder slot
                // replaces previous occupant" semantics.
                if (existing != null && existing != item)
                {
                    try
                    {
                        existing.m_equipped = false;
                        _equippedBackpacks.Remove(__instance);
                        if (__instance is Player oldOwner)
                            OnUnequipItem(oldOwner, existing);
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"{MyMod.PluginName}: Coexistence swap-out failed: {ex.Message}");
                    }
                }

                // Hijack the equip: track it ourselves, keep it OUT of
                // m_shoulderItem so capes can occupy that slot freely.
                _equippedBackpacks[__instance] = item;
                item.m_equipped = true;
                __result = true;

                Debug.Log($"{MyMod.PluginName}: Coexistence equip — '{item.m_dropPrefab?.name}' " +
                          $"tracked outside m_shoulderItem (cape can coexist).");
                return false;          // SKIP vanilla EquipItem entirely
            }
        }

        // Mirror hijack for unequip: when vanilla can't find the
        // backpack in any equipment field (because we hid it in our
        // dict), it bails on UnequipItem. Catch the call here, drop
        // the dict entry, set m_equipped=false, and skip vanilla.
        // The OnUnequipItem flow is then driven by the existing
        // Humanoid_UnequipItem_Patch Postfix.
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UnequipItem))]
        [HarmonyPriority(Priority.First)]
        public static class Humanoid_UnequipItem_BackpackHijack_Patch
        {
            public static bool Prefix(Humanoid __instance, ItemDrop.ItemData item)
            {
                if (item == null) return true;
                if (!IsHijackedBackpack(item)) return true;    // HoboStick stays on vanilla weapon path

                if (!_equippedBackpacks.TryGetValue(__instance, out var tracked) || tracked != item)
                    return true;       // not the one we're tracking — let vanilla try

                _equippedBackpacks.Remove(__instance);
                item.m_equipped = false;
                Debug.Log($"{MyMod.PluginName}: Coexistence unequip — '{item.m_dropPrefab?.name}' " +
                          $"removed from coexistence dict.");

                // Postfix patch (Humanoid_UnequipItem_Patch) handles
                // OnUnequipItem + container teardown; we just need to
                // stop vanilla from rummaging through m_shoulderItem
                // looking for an item that was never there.
                return false;
            }
        }

        // IsItemEquiped is what every other system asks to know if a
        // given ItemData is "live" on the humanoid. Vanilla checks the
        // nine equipment fields — none of which hold our coexistence
        // backpack. Postfix the result so backpacks in our dict report
        // as equipped. Keeps Player.UnequipItem's "if not equipped,
        // bail" guard from skipping our backpack on Drop / death.
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.IsItemEquiped))]
        public static class Humanoid_IsItemEquiped_Coexistence_Patch
        {
            public static void Postfix(Humanoid __instance, ItemDrop.ItemData item, ref bool __result)
            {
                if (__result) return;
                // HoboStick is a real weapon in m_rightItem — vanilla
                // already returns true for it; we only need this fallback
                // for hijacked Shoulder-type backpacks.
                if (item == null || !IsHijackedBackpack(item)) return;
                if (_equippedBackpacks.TryGetValue(__instance, out var tracked) && tracked == item)
                    __result = true;
            }
        }

        // Patch for unequip to close backpack container if open
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UnequipItem))]
        public static class Humanoid_UnequipItem_Patch
        {
            public static void Postfix(Humanoid __instance, ItemDrop.ItemData item)
            {
                if (__instance is Player player && IsBackpack(item))
                {
                    Debug.Log($"{MyMod.PluginName}: Backpack unequipped - closing container if open");
                    // The container-window only exists in legacy mode; in
                    // slots mode there's no separate window to close.
                    if (LegacyMode
                        && InventoryGui.instance != null
                        && InventoryGui.instance.IsContainerOpen())
                    {
                        CloseBackpackContainer();
                    }
                    OnUnequipItem(player, item);
                }
            }
        }

        // SINGLE PATCH: Handles both close on backpack select AND quick-transfer block
        [HarmonyPatch(typeof(InventoryGui), "OnSelectedItem")]
        public static class OnSelectedItem_Patch
        {
            public static bool Prefix(InventoryGui __instance, InventoryGrid grid, ItemDrop.ItemData item, Vector2i pos, InventoryGrid.Modifier mod)
            {
                if (item == null) return true;

                bool isPlayerGrid = grid == __instance.m_playerGrid;
                bool containerOpen = __instance.IsContainerOpen();

                // LEGACY-ONLY: close-on-select-backpack only matters when
                // we own a Container window. In slots mode the backpack
                // selection is just a normal grid-item interaction.
                if (LegacyMode && isPlayerGrid && IsBackpack(item) && containerOpen)
                {
                    Debug.Log($"{MyMod.PluginName}: Selected backpack item while container open - closing container");
                    CloseBackpackContainer();
                    return false; // Prevent normal selection behavior (no drag start)
                }

                // LEGACY-ONLY: same — quick-transfer-into-own-backpack is
                // a Container concept that doesn't exist in slots mode.
                if (LegacyMode && mod == InventoryGrid.Modifier.Move)
                {
                    Container currentContainer = (Container)_inventoryGuiCurrentContainerField?.GetValue(__instance);
                    if (IsBackpack(item) && currentContainer != null && currentContainer.GetInventory().GetName() == "Backpack")
                    {
                        Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Cannot quick-transfer backpack into itself!");
                        return false;
                    }
                }

                return true;
            }
        }

        private static void ShowBackpackContainer()
        {
            Player player = Player.m_localPlayer;
            if (player == null) return;

            ItemDrop.ItemData equippedBackpack = FindEquippedBackpack(player);
            if (equippedBackpack == null) return;

            Container container = player.gameObject.GetComponent<Container>();
            if (container == null) return;

            InventoryGui.instance.Show(container);
            Debug.Log($"{MyMod.PluginName}: Backpack container shown");
        }

        private static void CloseBackpackContainer()
        {
            _inventoryGuiCurrentContainerField?.SetValue(InventoryGui.instance, null);
            InventoryGui.instance.m_container.gameObject.SetActive(false);
            FieldInfo firstUpdateField = typeof(InventoryGui).GetField("m_firstContainerUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
            firstUpdateField?.SetValue(InventoryGui.instance, true);
            Debug.Log($"{MyMod.PluginName}: Backpack container closed");
        }

        // LEGACY-MODE ONLY: tracks which backpack ItemData currently
        // owns the player's Container's inventory. Used as the SAFETY
        // INTERLOCK for "click another backpack while wearing one" —
        // before we replace the container's inventory for a new
        // backpack, we save the existing container's contents into
        // _activeContainerOwner.m_customData so its items survive the
        // swap. Cleared on unequip and on player-spawned re-init.
        // null in slots mode (VAInventory owns storage there).
        private static ItemDrop.ItemData _activeContainerOwner;

        public static void OnEquipItem(Player player, ItemDrop.ItemData item)
        {
            Debug.Log($"{MyMod.PluginName}: OnEquipItem called for {item?.m_shared.m_name} (prefab: {item?.m_dropPrefab.name})");
            if (player == null || item == null || !IsBackpack(item)) return;

            if (!_configs.TryGetValue(item.m_dropPrefab.name, out var config))
            {
                Debug.LogWarning($"{MyMod.PluginName}: No config for {item.m_dropPrefab.name}");
                return;
            }

            // Status effects always apply, regardless of storage mode — the
            // cold/wet/feather-fall buffs are gameplay, not inventory layout.
            ApplyEffects(player, config, true);

            // Storage layout split: legacy mode owns a Container on the
            // Player; slots mode lets VAInventory handle the storage panel
            // and just listens for the OnEquipped event.
            if (LegacyMode)
            {
                Container container = player.gameObject.GetComponent<Container>();
                if (container == null)
                {
                    // 1.0.33: at character load the worn bags are equipped again before VAInventory switches VABackpacks out of
                    // legacy mode, so there is no legacy Container yet; VAInventory takes the storage over right after. Harmless.
                    if (ConfigManager.Instance?.configVerboseLogging?.Value == true) Debug.Log($"{MyMod.PluginName}: no legacy Container on the player during equip (load-time, before VAInventory takes over storage)");
                }
                else
                {
                    // ── SAFETY INTERLOCK ────────────────────────────
                    // Before we replace the container's inventory for
                    // the new backpack, save the existing inventory's
                    // contents to whichever backpack currently owns
                    // them. Without this, clicking a second backpack
                    // (which goes into the same vanilla shoulder slot
                    // as the first, OR which equips alongside without
                    // unequipping the first) would discard the first
                    // backpack's items entirely.
                    //
                    // We only do this if the previous owner is a
                    // DIFFERENT item — re-equipping the same backpack
                    // (e.g. on spawn) just reloads from its customData,
                    // which is identical to the container state.
                    if (_activeContainerOwner != null && _activeContainerOwner != item)
                    {
                        try
                        {
                            var existingInv = (Inventory)_containerInventoryField?.GetValue(container);
                            if (existingInv != null && existingInv.GetAllItems().Count > 0)
                            {
                                var savePkg = new ZPackage();
                                existingInv.Save(savePkg);
                                _activeContainerOwner.m_customData["BackpackInventory"] = savePkg.GetBase64();
                                Debug.Log($"{MyMod.PluginName}: Saved displaced backpack '{_activeContainerOwner.m_shared?.m_name}' " +
                                          $"({existingInv.GetAllItems().Count} items) before equipping '{item.m_shared.m_name}'");
                            }
                        }
                        catch (System.Exception ex)
                        {
                            Debug.LogError($"{MyMod.PluginName}: SAFETY INTERLOCK failed to save displaced backpack: {ex.Message}");
                        }
                    }

                    string backpackName = item.m_shared.m_name;

                    Inventory backpackInv = new Inventory(backpackName, null, config.Columns, config.Rows);
                    _containerInventoryField?.SetValue(container, backpackInv);
                    container.m_width = config.Columns;
                    container.m_height = config.Rows;
                    container.m_name = backpackName;

                    if (item.m_customData.TryGetValue("BackpackInventory", out string customData)
                        && !string.IsNullOrEmpty(customData))
                    {
                        ZPackage pkg = new ZPackage(customData);
                        backpackInv.Load(pkg);
                        Debug.Log($"{MyMod.PluginName}: Loaded backpack inventory from custom data");
                    }

                    // Track the new owner so the next equip can save
                    // these items if the player swaps to yet another bag.
                    _activeContainerOwner = item;
                }
            }

            // Notify subscribers (VAInventory in slots mode) — happens in
            // both modes so any other consumer can react too.
            BackpackApi.FireEquipped(player, item, config);

            Debug.Log($"{MyMod.PluginName}: SUCCESS - Equipped {item.m_dropPrefab.name} ({config.Columns}x{config.Rows}) " +
                      $"as '{item.m_shared.m_name}' [{(LegacyMode ? "legacy" : "slots")} mode]");
        }

        public static void OnUnequipItem(Player player, ItemDrop.ItemData item)
        {
            Debug.Log($"{MyMod.PluginName}: OnUnequipItem called for {item?.m_shared.m_name} (prefab: {item?.m_dropPrefab.name})");
            if (player == null || item == null || !IsBackpack(item)) return;

            // Storage drain: only in legacy mode does this manager own the
            // storage. In slots mode VAInventory has already serialized
            // the equipment-panel block back into the item's customData
            // before this method runs.
            if (LegacyMode)
            {
                Container container = player.gameObject.GetComponent<Container>();
                if (container != null)
                {
                    Inventory inv = (Inventory)_containerInventoryField?.GetValue(container);

                    if (inv != null)
                    {
                        ZPackage pkg = new ZPackage();
                        inv.Save(pkg);
                        item.m_customData["BackpackInventory"] = pkg.GetBase64();
                        Debug.Log($"{MyMod.PluginName}: Saved backpack inventory to custom data");

                        inv.GetAllItems().Clear();
                    }

                    _containerInventoryField?.SetValue(container, new Inventory("Backpack", null, 1, 1));
                    container.m_width = 1;
                    container.m_height = 1;
                    container.m_name = "Backpack";
                }

                // Container is empty now; clear the active-owner pointer
                // so the next equip's safety-interlock doesn't try to
                // re-save the just-unequipped bag's stale data.
                if (_activeContainerOwner == item) _activeContainerOwner = null;
            }

            if (_configs.TryGetValue(item.m_dropPrefab.name, out var config))
                ApplyEffects(player, config, false);

            // Notify subscribers (in both modes, after we've finished any
            // legacy bookkeeping above).
            BackpackApi.FireUnequipped(player, item);

            Debug.Log($"{MyMod.PluginName}: Unequipped {item.m_dropPrefab.name} [{(LegacyMode ? "legacy" : "slots")} mode]");
        }

        private static void ApplyEffects(Player player, BackpackConfig config, bool add)
        {
            var seMan = player.GetSEMan();
            if (config.ColdResistance)
            {
                int hash = "ColdResistance".GetStableHashCode();
                if (add) seMan.AddStatusEffect(hash, true);
                else seMan.RemoveStatusEffect(hash, true);
            }
            if (config.WetResistance)
            {
                int hash = "Wet".GetStableHashCode();
                if (add) seMan.AddStatusEffect(hash, true);
                else seMan.RemoveStatusEffect(hash, true);
            }
            if (config.FeatherFall)
            {
                int hash = "FeatherFall".GetStableHashCode();
                if (add) seMan.AddStatusEffect(hash, true);
                else seMan.RemoveStatusEffect(hash, true);
            }
            if (config.StrengthBonus)
            {
                int hash = "Strength".GetStableHashCode();
                if (add) seMan.AddStatusEffect(hash, true);
                else seMan.RemoveStatusEffect(hash, true);
            }
            if (config.SpeedModifier != 0f)
            {
                // Speed modifier handled in SE_Stats via ModifySpeed
                // We use a custom SE_Stats instance if needed, but for now assume it's covered by existing systems
            }
        }

        public static bool IsBackpack(ItemDrop.ItemData item)
        {
            return item != null && item.m_dropPrefab != null && BackpackPrefabNames.Contains(item.m_dropPrefab.name);
        }

        // ── HoboStick: special "backpack-weapon" hybrid ────────────────
        // HoboStick is registered in BackpackPrefabNames so it gets a
        // storage config (rows/cols, status effects), BUT its m_itemType
        // is TwoHandedWeapon — equipping it should put it in m_rightItem
        // and let the player attack with it, NOT hide it in the
        // coexistence dict like a Shoulder-type backpack.
        //
        // IsHijackedBackpack is what the EquipItem / UnequipItem /
        // IsItemEquiped hijack patches use to decide whether to take
        // over the equip flow. HoboStick is excluded — vanilla handles
        // it as a normal weapon, and we just hook OnEquipItem afterwards
        // (via the Postfix on Humanoid.EquipItem) to wire up storage.
        public const string HOBOSTICK_PREFAB = "HoboStick";

        public static bool IsHoboStick(ItemDrop.ItemData item)
            => item != null && item.m_dropPrefab != null
               && item.m_dropPrefab.name == HOBOSTICK_PREFAB;

        /// <summary>
        /// True for any backpack that's also a weapon and uses the
        /// MMB sledgehammer-slam camp-deploy gesture. Membership is
        /// driven by SlamDeployBackpacks — add a prefab name there to
        /// opt it into this behavior, remove it to treat the prefab
        /// as an ordinary Shoulder-slot bag.
        /// </summary>
        public static bool IsSlamDeployBackpack(ItemDrop.ItemData item)
            => item != null && item.m_dropPrefab != null
               && SlamDeployBackpacks.Contains(item.m_dropPrefab.name);

        public static bool IsSlamDeployBackpack(string prefabName)
            => !string.IsNullOrEmpty(prefabName) && SlamDeployBackpacks.Contains(prefabName);

        /// <summary>
        /// True for backpacks that should use the cape-coexistence
        /// hijack — i.e. Shoulder-type backpacks that would otherwise
        /// fight a cape for m_shoulderItem. Slam-deploy weapon-bags
        /// (HoboStick, StaffFiresWizard, …) are excluded because
        /// they're TwoHandedWeapons and vanilla equips them fine
        /// without us getting in the way.
        /// </summary>
        public static bool IsHijackedBackpack(ItemDrop.ItemData item)
            => IsBackpack(item) && !IsSlamDeployBackpack(item);

        // LEGACY-ONLY: prevent putting a backpack into its own container.
        // In slots mode, backpacks live in the dedicated Backpack equipment
        // slot — VAInventory enforces "only backpack-items here" via slot
        // validators, so this guard is unnecessary.
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemDrop.ItemData) })]
        public static class AddItem_Inception_Patch
        {
            public static bool Prefix(Inventory __instance, ItemDrop.ItemData item)
            {
                if (!LegacyMode) return true;
                if (IsBackpack(item) && __instance.GetName() == "Backpack")
                {
                    Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "No backpack in backpack!");
                    return false;
                }
                return true;
            }
        }

        // LEGACY-ONLY: backpack-weight calculation that adds the contained
        // items' weight (with reduction) to the backpack item itself. In
        // slots mode the contents live in the player's main Inventory and
        // are weighed individually, so this addition would double-count.
        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetWeight))]
        public static class GetWeight_Patch
        {
            public static void Postfix(ItemDrop.ItemData __instance, ref float __result)
            {
                if (!LegacyMode) return;
                if (!IsBackpack(__instance)) return;

                Player player = Player.m_localPlayer;
                if (player == null) return;

                ItemDrop.ItemData equippedBackpack = FindEquippedBackpack(player);
                if (equippedBackpack != __instance) return;

                Container container = player.gameObject.GetComponent<Container>();
                if (container == null) return;

                Inventory inv = (Inventory)_containerInventoryField?.GetValue(container);
                if (inv == null || !_configs.TryGetValue(__instance.m_dropPrefab.name, out var config)) return;

                float reduced = inv.GetTotalWeight() * config.WeightReduction;
                __result += reduced;
            }
        }

        private static int s_updateFrame = -1;

        public static void Update()
        {
            // 1.0.22: MyMod.Update calls this AND a Harmony postfix on MyMod.Update calls it again, so a single B press
            // toggled twice in one frame (with VAInventory: Show, then Hide). Once per frame.
            if (Time.frameCount == s_updateFrame) return;
            s_updateFrame = Time.frameCount;
            if (Player.m_localPlayer == null || InventoryGui.instance == null) return;

            // 1.0.43 (Fire: no keybinds while typing, e.g. in the hammer filter): raw hotkeys wait while a text field has input.
            if (!IsFiresInputCaptured() && Input.GetKeyDown(ConfigManager.Instance.configBackpackHotkey.Value))
            {
                // Never fire the hotkey while another UI owns input. Opening/closing the
                // backpack (InventoryGui.Show/Hide) under an open chat box, escape menu,
                // console, text field, trader, or any Fires* mod panel splits input capture
                // between two windows and soft-locks the client — this is the "pressed B in
                // a menu and the screen froze" report. We deliberately do NOT block on the
                // inventory being visible: the hotkey's whole job is to toggle the backpack
                // while the inventory is open or closed.
                if (IsBlockingUiOpen())
                    return;

                Debug.Log($"{MyMod.PluginName}: Backpack hotkey pressed");
                // LEGACY-ONLY: hotkey opens the backpack-as-container window.
                // In slots mode the backpack contents are visible in the
                // equipment panel whenever inventory is open, so a separate
                // hotkey toggle isn't meaningful — toggle the inventory
                // window instead.
                if (!LegacyMode)
                {
                    if (InventoryGui.IsVisible())
                        InventoryGui.instance.Hide();
                    else
                        InventoryGui.instance.Show(null);
                    return;
                }

                Player player = Player.m_localPlayer;
                ItemDrop.ItemData equippedBackpack = FindEquippedBackpack(player);
                if (equippedBackpack != null)
                {
                    Debug.Log($"{MyMod.PluginName}: Backpack equipped - opening via reflection");
                    Container container = player.gameObject.GetComponent<Container>();
                    if (container != null)
                    {
                        InventoryGui.instance.Show(container);
                        Debug.Log($"{MyMod.PluginName}: Backpack opened via hotkey (reflection)");
                    }
                }
                else
                {
                    Debug.Log($"{MyMod.PluginName}: No backpack equipped on hotkey press");
                }
            }
        }

        // True when a UI other than the plain inventory/backpack owns input, so the
        // backpack hotkey must not fire. Covers the vanilla modals directly and every
        // Fires* mod UI (NPC dialogue, trader, books, config panels) via a reflective
        // read of FiresCore.Input.FiresInputBlock.IsCapturing — there is no hard
        // dependency on FiresUnifiedCore, so the Fires check is simply skipped when it
        // isn't loaded. InventoryGui is intentionally NOT treated as blocking.
        private static bool IsBlockingUiOpen()
        {
            if (global::Console.IsVisible()) return true;
            if (TextInput.IsVisible()) return true;
            if (Menu.IsVisible()) return true;
            if (Chat.instance != null && Chat.instance.HasFocus()) return true;
            if (StoreGui.IsVisible()) return true;
            if (IsFiresInputCaptured()) return true;
            return false;
        }

        private static bool _firesGateResolved;
        private static PropertyInfo _firesIsCapturingProp;
        private static MethodInfo _firesIsTyping;

        // 1.0.47 (Fire, team-wide: typing in any input field blocks every mod hotkey): Core 0.2.376's
        // FiresInputBlock.IsTyping() is the one shared check; older Core falls back to IsCapturing.
        internal static bool IsFiresInputCaptured()
        {
            if (!_firesGateResolved)
            {
                _firesGateResolved = true;
                var t = AccessTools.TypeByName("FiresCore.Input.FiresInputBlock");
                _firesIsTyping = t?.GetMethod("IsTyping", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                _firesIsCapturingProp = t?.GetProperty("IsCapturing", BindingFlags.Public | BindingFlags.Static);
                Debug.Log($"[VABackpacks] typing gate: {(_firesIsTyping != null ? "Core FiresInputBlock.IsTyping()" : _firesIsCapturingProp != null ? "Core FiresInputBlock.IsCapturing (older Core)" : "none (no Core)")}");
            }
            try
            {
                if (_firesIsTyping != null) return (bool)_firesIsTyping.Invoke(null, null);
                if (_firesIsCapturingProp != null) return (bool)_firesIsCapturingProp.GetValue(null);
            }
            catch { }
            return false;
        }

        public static void OnConfigReloaded()
        {
            foreach (string prefabName in BackpackPrefabNames)
            {
                if (_configs.ContainsKey(prefabName))
                {
                    var cfg = _configs[prefabName];
                    cfg.Rows = ConfigManager.Instance.configBackpackRows[prefabName].Value;
                    cfg.Columns = ConfigManager.Instance.configBackpackColumns[prefabName].Value;
                    cfg.WeightReduction = ConfigManager.Instance.configBackpackWeightReduction[prefabName].Value;
                    cfg.ColdResistance = ConfigManager.Instance.configBackpackColdResistance[prefabName].Value;
                    cfg.WetResistance = ConfigManager.Instance.configBackpackWetResistance[prefabName].Value;
                    cfg.Teleportable = ConfigManager.Instance.configBackpackTeleportable[prefabName].Value;
                    cfg.FeatherFall = ConfigManager.Instance.configBackpackFeatherFall[prefabName].Value;
                    cfg.SpeedModifier = ConfigManager.Instance.configBackpackSpeedModifier[prefabName].Value;
                    cfg.StrengthBonus = ConfigManager.Instance.configBackpackStrengthBonus[prefabName].Value;
                }
            }

            Player player = Player.m_localPlayer;
            if (player != null)
            {
                ItemDrop.ItemData equippedBackpack = FindEquippedBackpack(player);
                if (equippedBackpack != null)
                {
                    var seMan = player.GetSEMan();
                    seMan.RemoveStatusEffect("ColdResistance".GetStableHashCode(), true);
                    seMan.RemoveStatusEffect("Wet".GetStableHashCode(), true);
                    seMan.RemoveStatusEffect("FeatherFall".GetStableHashCode(), true);
                    seMan.RemoveStatusEffect("Strength".GetStableHashCode(), true);

                    OnEquipItem(player, equippedBackpack);

                    // LEGACY-ONLY: re-show the container window if it was
                    // open. In slots mode the panel is always visible
                    // when inventory is open, so VAInventory's panel
                    // rebuild on the OnEquipped event handles the refresh.
                    if (LegacyMode
                        && InventoryGui.instance != null
                        && InventoryGui.instance.m_container.gameObject.activeSelf)
                    {
                        InventoryGui.instance.Show(player.gameObject.GetComponent<Container>());
                    }
                }
            }
        }

        [HarmonyPatch(typeof(MyMod), nameof(MyMod.Update))]
        public static class MyMod_Update_Patch
        {
            public static void Postfix()
            {
                BackpackManager.Update();
            }
        }
    }
}