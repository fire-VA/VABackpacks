// BackpackApi — public surface for other mods (notably VAInventory) to
// query and react to VABackpacks state without poking at internals or
// taking a hard assembly dependency on this mod.
//
// All members are static. VAInventory consumes this via reflection in
// its BackpackBridge.cs so it doesn't take a hard load-order dep.
//
// Architecture context: see Refs/BACKPACK_INVENTORY_REFACTOR_PLAN.md.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public static class BackpackApi
    {
        // ── Identification ─────────────────────────────────────────────

        public static bool IsBackpack(ItemDrop.ItemData item)
        {
            if (item?.m_dropPrefab == null) return false;
            return BackpackManager.BackpackPrefabNames.Contains(item.m_dropPrefab.name);
        }

        public static bool IsBackpack(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            return BackpackManager.BackpackPrefabNames.Contains(prefabName);
        }

        // ── Per-prefab config lookup ──────────────────────────────────

        /// <summary>Returns null if the prefab isn't a registered backpack.</summary>
        public static BackpackConfig GetConfig(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;
            BackpackManager.Configs.TryGetValue(prefabName, out var cfg);
            return cfg;
        }

        public static BackpackConfig GetConfig(ItemDrop.ItemData item)
        {
            return item?.m_dropPrefab == null
                ? null
                : GetConfig(item.m_dropPrefab.name);
        }

        // ── Equipped state ────────────────────────────────────────────

        public static ItemDrop.ItemData GetEquippedBackpack(Player player)
        {
            if (player == null) return null;
            return BackpackManager.FindEquippedBackpackPublic(player);
        }

        /// <summary>
        /// All currently-equipped registered backpacks on this player, in
        /// stable order: coexistence dict first, then armor slots
        /// (chest/legs/helmet/shoulder/utility/trinket), then weapon slots
        /// (right/left/ammo). Empty list if none.
        ///
        /// VAInventory uses this to render one detached storage panel per
        /// equipped bag (e.g. shoulder bag + HoboStick = two panels).
        /// </summary>
        public static IReadOnlyList<ItemDrop.ItemData> GetEquippedBackpacks(Player player)
        {
            if (player == null) return System.Array.Empty<ItemDrop.ItemData>();
            return BackpackManager.FindAllEquippedBackpacksPublic(player);
        }

        public static bool TryGetEquippedConfig(Player player, out BackpackConfig cfg)
        {
            cfg = null;
            var item = GetEquippedBackpack(player);
            if (item == null) return false;
            cfg = GetConfig(item);
            return cfg != null;
        }

        public static int GetEquippedRows(Player player)
            => TryGetEquippedConfig(player, out var c) ? c.Rows : 0;

        public static int GetEquippedColumns(Player player)
            => TryGetEquippedConfig(player, out var c) ? c.Columns : 0;

        public static int GetEquippedSlotCount(Player player)
            => TryGetEquippedConfig(player, out var c) ? c.Rows * c.Columns : 0;

        // ── Storage round-trip (raw, used by VAInventory) ─────────────
        // VAInventory writes the equipment-panel expansion contents into
        // this same customData field on equip/unequip, so the backpack
        // item carries its inventory wherever it goes (chest, ground, etc).

        public const string CUSTOM_DATA_KEY = "BackpackInventory";

        /// <summary>
        /// Returns the base64-encoded ZPackage of the backpack's stored
        /// items, or empty string if none. Never returns null.
        /// </summary>
        public static string ReadStorage(ItemDrop.ItemData backpackItem)
        {
            if (backpackItem == null) return string.Empty;
            return backpackItem.m_customData != null
                && backpackItem.m_customData.TryGetValue(CUSTOM_DATA_KEY, out var s)
                && s != null
                ? s
                : string.Empty;
        }

        /// <summary>
        /// Writes the base64-encoded ZPackage onto the backpack item's
        /// custom data. Pass an empty / null string to clear.
        /// </summary>
        public static void WriteStorage(ItemDrop.ItemData backpackItem, string base64)
        {
            if (backpackItem == null) return;
            if (backpackItem.m_customData == null) return;

            if (string.IsNullOrEmpty(base64))
            {
                backpackItem.m_customData.Remove(CUSTOM_DATA_KEY);
                return;
            }
            backpackItem.m_customData[CUSTOM_DATA_KEY] = base64;
        }

        // ── Registration (open up the prefab list to other mods) ─────

        public static void RegisterBackpack(string prefabName, BackpackConfig cfg)
        {
            if (string.IsNullOrEmpty(prefabName) || cfg == null) return;
            BackpackManager.BackpackPrefabNames.Add(prefabName);
            BackpackManager.Configs[prefabName] = cfg;
            OnRegistered?.Invoke(prefabName, cfg);
        }

        public static bool UnregisterBackpack(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            bool a = BackpackManager.BackpackPrefabNames.Remove(prefabName);
            bool b = BackpackManager.Configs.Remove(prefabName);
            return a || b;
        }

        public static IReadOnlyCollection<string> GetRegisteredPrefabs()
            => BackpackManager.BackpackPrefabNames;

        // ── Events (fired by BackpackManager equip/unequip patches) ──

        /// <summary>Fired when a backpack is equipped on a Player.
        /// Args: player, item, config.</summary>
        public static event Action<Player, ItemDrop.ItemData, BackpackConfig> OnEquipped;

        /// <summary>Fired when a backpack is unequipped from a Player.
        /// Args: player, item.</summary>
        public static event Action<Player, ItemDrop.ItemData> OnUnequipped;

        /// <summary>Fired when a new backpack prefab is registered (built-in or third-party).
        /// Args: prefab name, config.</summary>
        public static event Action<string, BackpackConfig> OnRegistered;

        // ── Internal-ish helpers (called from BackpackManager) ───────
        // These are public so the dispatch in BackpackManager can stay in
        // its own file, but external consumers should subscribe to the
        // events above rather than calling these directly.

        internal static void FireEquipped(Player p, ItemDrop.ItemData item, BackpackConfig cfg)
            => OnEquipped?.Invoke(p, item, cfg);

        internal static void FireUnequipped(Player p, ItemDrop.ItemData item)
            => OnUnequipped?.Invoke(p, item);

        // ── Capability flags ─────────────────────────────────────────

        /// <summary>
        /// True when VAInventory is loaded and the slot-based storage path is
        /// active. False means VABackpacks is in legacy Container mode
        /// (standalone install). External mods can use this to decide UI text
        /// or behavior gates.
        /// </summary>
        public static bool IsSlotBasedStorageActive => !BackpackManager.LegacyMode;

        // ── Special slots (1.0.20) ───────────────────────────────────
        // Upgraded bags (quality 2+) carry a rail of special slots right of the grid. Each slot holds one stack outside
        // the grid and outside the player inventory; ReadSpecialSlot returns the stored ItemData itself (never a copy).

        public const string SlotTool = BackpackSpecialSlots.SlotTool;
        public const string SlotResources = BackpackSpecialSlots.SlotResources;
        public const string SlotBypass = BackpackSpecialSlots.SlotBypass;

        /// <summary>Every slot kind, in rail order (top-down).</summary>
        public static readonly string[] SpecialSlotKinds = { SlotTool, SlotResources, SlotBypass };

        /// <summary>The bag's special slots in rail order; empty below quality 2.</summary>
        public static string[] GetSpecialSlots(ItemDrop.ItemData bag) => BackpackSpecialSlots.GetSpecialSlots(bag);

        /// <summary>The item in that slot, or null.</summary>
        public static ItemDrop.ItemData ReadSpecialSlot(ItemDrop.ItemData bag, string kind) => BackpackSpecialSlots.Read(bag, kind);

        /// <summary>Puts <paramref name="item"/> in the slot (null clears it). False when the slot doesn't take it.</summary>
        public static bool WriteSpecialSlot(ItemDrop.ItemData bag, string kind, ItemDrop.ItemData item) => BackpackSpecialSlots.Write(bag, kind, item);

        public static bool SpecialSlotAccepts(ItemDrop.ItemData bag, string kind, ItemDrop.ItemData item) => BackpackSpecialSlots.Accepts(bag, kind, item);

        /// <summary>Bumped on every special-slot change.</summary>
        public static int SpecialSlotsVersion => BackpackSpecialSlots.Version;

        /// <summary>The slot's white-on-transparent icon (tint it with the slot colour), or null for an unknown kind.</summary>
        public static Sprite GetSlotIcon(string kind) => BackpackSpecialSlots.GetIcon(kind);
    }
}
