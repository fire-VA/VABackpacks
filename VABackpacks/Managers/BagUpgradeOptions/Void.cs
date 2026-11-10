// Void (bypass slot): items on the bag's filter list are deleted as they are picked up, so the bag never fills with junk (Stone, Resin,
// ...). The filter holds 3 / 6 / 10 items by tier and is saved on the upgrade item ("BagUp.Void.filter" = prefab names, '|'), so it
// travels with the bag. Fill it by dropping an item onto the slotted upgrade, with the picker window (right-click the upgrade), or the
// console: bagup_void <item> [item] ... Vanilla's pickup runs as usual; the amount it added is then removed, so a voided pickup leaves the
// inventory exactly as it was. Local player only; guarded by WornTier. Nothing already carried is ever deleted.

using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public sealed class VoidOption : IBagUpgradeOption
    {
        public const string UpgradeId = "Void";
        public const string FilterKey = "BagUp.Void.filter";
        private const char FilterSeparator = '|';
        private const int MaxFilterEntries = 20;
        private static readonly int[] DefaultEntries = { 3, 6, 10 };

        private static readonly ConfigEntry<int>[] Entries = new ConfigEntry<int>[BagUpgrades.TierCount];
        private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks." + UpgradeId);
        private static bool s_hooked;

        public VoidOption()
        {
            if (s_hooked) return;
            s_hooked = true;
            BagUpgrades.UpgradeBadge += Badge;
            BagUpgrades.UpgradeRightClick += OnRightClick;
            BagUpgrades.UpgradeDropOnto += OnDropOnto;
            new Terminal.ConsoleCommand("bagup_void", "[item] [item] ... - the items your worn bag's Void upgrade deletes on pickup (no items = clear)", args => SetFromConsole(args));
        }

        public string Id => UpgradeId;
        public string Name => "Void";
        public string Slot => BackpackSpecialSlots.SlotBypass;
        public string Donor => "SurtlingCore";
        public string[] Recipes => new[]
        {
            "forge,1,Bronze:5,LeatherScraps:10,SurtlingCore:2",
            "forge,3,Iron:10,WolfPelt:5,SurtlingCore:4",
            "forge,5,BlackMetal:10,LinenThread:5,SurtlingCore:6",
        };

        public string Describe(int tier)
            => $"Deletes up to {EntriesFor(tier)} kinds of junk you pick as you pick them up, so the bag never fills with them. Drop an item onto it, or right-click it, to pick.";

        public void Bind(ConfigFile cfg, ConfigSync sync)
        {
            for (int t = 0; t < BagUpgrades.TierCount; t++)
            {
                Entries[t] = cfg.Bind(BagUpgrades.Section, $"Void {BagUpgrades.TierNumerals[t]} filter entries", DefaultEntries[t],
                    new ConfigDescription($"How many kinds of items a bag with Void {BagUpgrades.TierNumerals[t]} can delete on pickup. Server-synced.",
                        new AcceptableValueRange<int>(1, MaxFilterEntries)));
                sync?.AddConfigEntry(Entries[t]);
            }
        }

        internal static int EntriesFor(int tier) => tier < 1 ? 0 : Mathf.Clamp(Entries[tier - 1]?.Value ?? DefaultEntries[tier - 1], 1, MaxFilterEntries);

        private static bool IsMine(ItemDrop.ItemData upgrade)
            => upgrade?.m_dropPrefab != null && upgrade.m_dropPrefab.name.StartsWith(BagUpgrades.PrefabPrefix + UpgradeId + "_", StringComparison.Ordinal);

        // ── the filter (saved on the upgrade item) ────────────────────────────────────────────────────────────────────

        /// <summary>The item prefab names on this upgrade's filter, in order (the picker window reads and writes these).</summary>
        public static List<string> GetFilter(ItemDrop.ItemData upgrade)
        {
            if (upgrade?.m_customData == null || !upgrade.m_customData.TryGetValue(FilterKey, out string raw) || string.IsNullOrEmpty(raw)) return new List<string>();
            return raw.Split(FilterSeparator).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        }

        /// <summary>Sets the filter (known item prefabs only, cut to the tier's entries) and saves the bag's slot.</summary>
        public static void SetFilter(ItemDrop.ItemData bag, ItemDrop.ItemData upgrade, IEnumerable<string> items, int tier)
        {
            if (upgrade == null) return;
            var list = (items ?? Enumerable.Empty<string>()).Where(IsItem).Distinct(StringComparer.OrdinalIgnoreCase).Take(EntriesFor(tier)).ToList();
            if (upgrade.m_customData == null) upgrade.m_customData = new Dictionary<string, string>();
            if (list.Count == 0) upgrade.m_customData.Remove(FilterKey);
            else upgrade.m_customData[FilterKey] = string.Join(FilterSeparator.ToString(), list.ToArray());
            BagUpgrades.SaveUpgradeState(bag, BackpackSpecialSlots.SlotBypass);
            Log.LogDebug($"[{UpgradeId}] filter set: {(list.Count == 0 ? "none" : string.Join(", ", list.ToArray()))}");
        }

        /// <summary>True for a prefab that is an item a player can carry.</summary>
        public static bool IsItem(string prefab)
        {
            GameObject go = ObjectDB.instance != null && !string.IsNullOrEmpty(prefab) ? ObjectDB.instance.GetItemPrefab(prefab) : null;
            return go != null && go.GetComponent<ItemDrop>() != null;
        }

        private static ItemDrop.ItemData MyUpgrade(Player me, out ItemDrop.ItemData bag)
        {
            bag = me != null ? BagUpgrades.WornBagWith(me, UpgradeId) : null;
            return bag != null ? BackpackSpecialSlots.Read(bag, BackpackSpecialSlots.SlotBypass) : null;
        }

        // ── voiding: vanilla picks the item up, then exactly what it added is removed ────────────────────────────────

        private struct PickupState { public string Prefab, SharedName; public int Before; }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.Pickup))]
        private static class VoidOnPickup
        {
            private static void Prefix(Humanoid __instance, GameObject go, out PickupState __state)
            {
                __state = default;
                if (!(__instance is Player p) || p != Player.m_localPlayer || go == null) return;
                if (BagUpgrades.WornTier(p, UpgradeId) < 1) return;
                var drop = go.GetComponent<ItemDrop>();
                string prefab = drop?.m_itemData?.m_dropPrefab != null ? drop.m_itemData.m_dropPrefab.name : (drop != null ? Utils.GetPrefabName(drop.gameObject) : null);
                if (string.IsNullOrEmpty(prefab)) return;
                ItemDrop.ItemData upgrade = MyUpgrade(p, out _);
                if (!GetFilter(upgrade).Contains(prefab, StringComparer.OrdinalIgnoreCase)) return;
                __state.Prefab = prefab;
                __state.SharedName = drop.m_itemData.m_shared.m_name;
                __state.Before = p.GetInventory().CountItems(__state.SharedName);
            }

            private static void Postfix(Humanoid __instance, bool __result, PickupState __state)
            {
                if (__state.Prefab == null || !(__instance is Player p)) return;
                if (!__result) { Log.LogDebug($"[{UpgradeId}] {__state.Prefab} is on the filter but the pickup did not happen (inventory full or not yours)"); return; }
                try
                {
                    Inventory inv = p.GetInventory();
                    int added = inv.CountItems(__state.SharedName) - __state.Before;
                    if (added <= 0) { Log.LogDebug($"[{UpgradeId}] {__state.Prefab} picked up but nothing was added (count unchanged)"); return; }
                    inv.RemoveItem(__state.SharedName, added);
                    Log.LogDebug($"[{UpgradeId}] voided {__state.Prefab} x{added} on pickup (on the filter); carried before and after: {__state.Before}");
                }
                catch (Exception e) { Log.LogWarning($"[{UpgradeId}] void failed for {__state.Prefab}: {e.Message}"); }
            }
        }

        // ── the rail: badge, drop onto, right-click ──────────────────────────────────────────────────────────────────

        private static (string text, bool warn)? Badge(ItemDrop.ItemData upgrade)
        {
            if (!IsMine(upgrade)) return null;
            Player me = Player.m_localPlayer;
            ItemDrop.ItemData bag = me != null ? BagUpgrades.WornBagWith(me, UpgradeId) : null;
            int tier = bag != null ? BagUpgrades.SlottedTier(bag, UpgradeId) : 0;
            int used = GetFilter(upgrade).Count;
            return used == 0 ? ("pick junk", true) : ($"{used}/{EntriesFor(tier)}", false);
        }

        // An item dropped onto the slotted Void joins its filter; the item itself stays on the cursor.
        private static bool OnDropOnto(ItemDrop.ItemData bag, string kind, ItemDrop.ItemData dropped, Inventory fromInventory)
        {
            if (kind != BackpackSpecialSlots.SlotBypass || dropped?.m_dropPrefab == null) return false;
            ItemDrop.ItemData upgrade = BackpackSpecialSlots.Read(bag, kind);
            if (!IsMine(upgrade) || BagUpgrades.SlotOf(dropped) != null) return false;   // another upgrade: the rail asks to swap
            int tier = BagUpgrades.SlottedTier(bag, UpgradeId);
            List<string> filter = GetFilter(upgrade);
            string prefab = dropped.m_dropPrefab.name;
            string msg;
            if (filter.Contains(prefab, StringComparer.OrdinalIgnoreCase)) msg = $"{dropped.m_shared.m_name} is already voided";
            else if (filter.Count >= EntriesFor(tier)) msg = $"The Void filter is full ({filter.Count}/{EntriesFor(tier)}): right-click it to change";
            else { filter.Add(prefab); SetFilter(bag, upgrade, filter, tier); msg = $"Void: {dropped.m_shared.m_name} will be deleted on pickup ({filter.Count}/{EntriesFor(tier)})"; }
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, Localization.instance != null ? Localization.instance.Localize(msg) : msg);
            return true;
        }

        /// <summary>Set by the picker window when it exists ([classes-ui]); until then the right-click offers to clear the filter.</summary>
        public static Action<ItemDrop.ItemData, ItemDrop.ItemData, int> OpenPicker;

        private static bool OnRightClick(ItemDrop.ItemData bag, string kind, ItemDrop.ItemData upgrade)
        {
            if (kind != BackpackSpecialSlots.SlotBypass || !IsMine(upgrade)) return false;
            int tier = BagUpgrades.SlottedTier(bag, UpgradeId);
            if (OpenPicker != null) { OpenPicker(bag, upgrade, tier); return true; }
            List<string> filter = GetFilter(upgrade);
            if (filter.Count == 0)
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, $"Void: nothing picked. Drop an item onto it, or type bagup_void <item> (up to {EntriesFor(tier)}).");
                return true;
            }
            UnifiedPopup.Push(new YesNoPopup("Void filter", $"Deleted on pickup: {string.Join(", ", filter.ToArray())} ({filter.Count}/{EntriesFor(tier)}).\nClear the list?",
                () => { UnifiedPopup.Pop(); SetFilter(bag, upgrade, null, tier); },
                () => UnifiedPopup.Pop()));
            return true;
        }

        private static void SetFromConsole(Terminal.ConsoleEventArgs args)
        {
            Player me = Player.m_localPlayer;
            ItemDrop.ItemData upgrade = MyUpgrade(me, out ItemDrop.ItemData bag);
            if (upgrade == null) { args.Context?.AddString("No worn bag has a Void upgrade."); return; }
            var asked = args.Args.Skip(1).ToList();
            var bad = asked.Where(a => !IsItem(a)).ToList();
            int tier = BagUpgrades.SlottedTier(bag, UpgradeId);
            SetFilter(bag, upgrade, asked, tier);
            List<string> now = GetFilter(upgrade);
            args.Context?.AddString($"Void {BagUpgrades.TierNumerals[Mathf.Clamp(tier, 1, BagUpgrades.TierCount) - 1]}: {(now.Count == 0 ? "nothing" : string.Join(", ", now.ToArray()))}"
                + (bad.Count > 0 ? $" (not an item: {string.Join(", ", bad.ToArray())})" : "")
                + (asked.Count - bad.Count > now.Count ? $" (this tier takes {EntriesFor(tier)})" : ""));
        }
    }
}
