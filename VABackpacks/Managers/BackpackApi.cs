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

        // -- Bag bonuses (1.0.49, [fgn] FRC section 15) --
        // A CONTRACT with FiresRPGCrafting (it sets the field by reflection, no reference to this DLL) and VAInventory.
        // BonusProvider(bag) returns float[] { carryWeight, contentsWeightFactor, loadedSpeed, extraSlots } - index order
        // fixed, append-only; null = no bonus. contentsWeightFactor 1 = none, 0.8 = contents weigh 20 % less; loadedSpeed is
        // added to the movement modifier while the bag HAS contents; extraSlots is reserved (not applied, Fire 10-13).
        // Without a provider nothing changes.

        public const int BonusCarryWeight = 0, BonusContentsWeightFactor = 1, BonusLoadedSpeed = 2, BonusExtraSlots = 3;
        private const int BonusLength = 4;

        public static Func<ItemDrop.ItemData, float[]> BonusProvider;

        /// <summary>Raised by RefreshBonus: a worn bag's bonus changed (re-roll, enchant, socket).</summary>
        public static event Action<Player> BonusChanged;

        private static readonly BepInEx.Logging.ManualLogSource BonusLog = BepInEx.Logging.Logger.CreateLogSource("VABackpacks.Bonus");
        private static bool s_bonusFailLogged;
        private static Player s_bonusPlayer;
        private static int s_bonusFrame = -1;
        private static float[] s_bonusSum = { 0f, 1f, 0f, 0f };

        /// <summary>One bag's bonus from the provider, always BonusLength long; null when there is none.</summary>
        public static float[] GetBonus(ItemDrop.ItemData bag)
        {
            var provider = BonusProvider;
            if (provider == null || bag == null || !IsBackpack(bag)) return null;
            float[] raw;
            try { raw = provider(bag); }
            catch (Exception ex)
            {
                if (!s_bonusFailLogged) { s_bonusFailLogged = true; BonusLog.LogWarning($"BonusProvider threw {ex.GetType().Name}: {ex.Message} (logged once)"); }
                return null;
            }
            if (raw == null) return null;
            var b = new float[BonusLength];
            b[BonusContentsWeightFactor] = 1f;
            for (int i = 0; i < BonusLength && i < raw.Length; i++) b[i] = raw[i];
            b[BonusContentsWeightFactor] = Mathf.Clamp01(b[BonusContentsWeightFactor]);
            return b;
        }

        /// <summary>The worn bags' bonuses together: carry and speed add, the weight factors multiply. Cached for the frame.</summary>
        public static float[] GetEquippedBonus(Player player)
        {
            if (player == null || BonusProvider == null) return new float[] { 0f, 1f, 0f, 0f };
            if (player == s_bonusPlayer && Time.frameCount == s_bonusFrame) return s_bonusSum;
            var sum = new float[] { 0f, 1f, 0f, 0f };
            foreach (var bag in GetEquippedBackpacks(player))
            {
                var b = GetBonus(bag);
                if (b == null) continue;
                sum[BonusCarryWeight] += b[BonusCarryWeight];
                sum[BonusContentsWeightFactor] *= b[BonusContentsWeightFactor];
                sum[BonusLoadedSpeed] += b[BonusLoadedSpeed];
                sum[BonusExtraSlots] += b[BonusExtraSlots];
            }
            s_bonusPlayer = player; s_bonusFrame = Time.frameCount; s_bonusSum = sum;
            return sum;
        }

        /// <summary>FRC calls this (by reflection) after a roll / enchant / socket on a worn bag.</summary>
        public static void RefreshBonus(Player player)
        {
            s_bonusFrame = -1;
            var b = GetEquippedBonus(player);
            BonusLog.LogDebug($"bonus refreshed for {player?.GetPlayerName() ?? "?"}: carry +{b[BonusCarryWeight]:0.#}, contents x{b[BonusContentsWeightFactor]:0.##}, loaded speed {b[BonusLoadedSpeed]:+0.###;-0.###;0}");
            try { BonusChanged?.Invoke(player); } catch (Exception ex) { BonusLog.LogWarning($"BonusChanged handler threw {ex.Message}"); }
        }

        // -- Worn bag visual (1.0.50, [fgn] FRC rarity aura) --
        // The bag on a player's back is VABackpacks' own attachment (BackpackWornVisual), not a VisEquipment item, so another mod
        // that dresses worn gear (FRC's rarity aura) hooks it here. Raised when the visual is built or replaced (the item is null
        // for ANOTHER player's bag: only its owner knows the item) and on removal (GameObject null).
        public static event Action<Player, ItemDrop.ItemData, GameObject> WornVisualChanged;

        /// <summary>The worn bag's drawn GameObject on this player, or null. For a refresh after a roll / enchant.</summary>
        public static GameObject GetWornVisual(Player player) => BackpackWornVisual.WornVisualOf(player);

        internal static void RaiseWornVisualChanged(Player player, ItemDrop.ItemData item, GameObject visual)
        {
            var handler = WornVisualChanged;
            if (handler == null) return;
            try { handler(player, item, visual); }
            catch (Exception ex) { BonusLog.LogWarning($"WornVisualChanged handler threw {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>
        /// 1.0.78, add-only (Core's companions): the shoulder bag a NON-player Humanoid wears, by item prefab name (null or "" = none).
        /// Writes the humanoid's ZDO "va_bp_worn" (the prefab name's stable hash) on its owner, so every client draws it on the body
        /// the same way as a player's bag. Players keep their own path (their equipped bag); this call is ignored for a Player.
        /// Returns false when the humanoid has no valid ZNetView or this client isn't its owner.
        /// </summary>
        public static bool SetWornBag(Humanoid humanoid, string bagPrefabName)
        {
            if (humanoid == null || humanoid is Player) return false;
            ZNetView nv = humanoid.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid() || !nv.IsOwner()) return false;
            int hash = string.IsNullOrEmpty(bagPrefabName) ? 0 : bagPrefabName.GetStableHashCode();
            if (nv.GetZDO().GetInt("va_bp_worn".GetStableHashCode()) != hash) nv.GetZDO().Set("va_bp_worn".GetStableHashCode(), hash);
            return true;
        }
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
