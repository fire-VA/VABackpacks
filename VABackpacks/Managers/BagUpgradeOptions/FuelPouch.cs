// FuelPouch: the private fuel reserve of a slotted Smelter's or Kiln's upgrade. The upgrade burns ONLY what is in its own pouch,
// never loose fuel in the bag. Counts live on the slotted upgrade item's m_customData, so the pouch travels with the bag (trade,
// drop, relog) and is saved with it.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public sealed class FuelPouch
    {
        private const string KeyPrefix = "BagUp.";
        private const string FuelKey = ".fuel";
        private const char EntrySeparator = '|';
        private const char CountSeparator = ':';

        private static readonly Dictionary<ItemDrop.ItemData, FuelPouch> ByItem = new Dictionary<ItemDrop.ItemData, FuelPouch>();
        private static readonly Dictionary<string, PouchKind> Kinds = new Dictionary<string, PouchKind>();

        /// <summary>Raised on every pouch after any fill, burn, take-out or return, so a bag view redraws once.</summary>
        public static event Action<FuelPouch> AnyChanged;
        /// <summary>A right-click on the slotted upgrade asks for the pouch window; the UI subscribes and opens it.</summary>
        public static event Action<FuelPouch> OpenRequested;

        /// <summary>Raised after any change to this pouch.</summary>
        public event Action Changed;

        private readonly PouchKind _kind;
        private readonly Dictionary<string, int> _counts = new Dictionary<string, int>();

        /// <summary>The slotted upgrade item this pouch belongs to.</summary>
        public ItemDrop.ItemData Upgrade { get; }
        /// <summary>The upgrade id ("Smelter" / "Kiln").</summary>
        public string UpgradeId => _kind.Id;
        /// <summary>Fuel prefabs this pouch takes at the upgrade's tier, in display AND burn order.</summary>
        public IReadOnlyList<string> Accepts => _kind.Accepts(Tier);
        public int Capacity => _kind.Capacity(Tier);
        public int LowAt => _kind.LowAt();
        public int Count => _counts.Values.Sum();
        public bool IsEmpty => Count == 0;
        public bool IsLow => Count > 0 && Count <= LowAt;
        public int Tier => TierOf(Upgrade);

        // Smelter / Kiln work state for the UI (optional to show)
        public bool IsWorking { get; internal set; }
        public bool IsWaitingForFuel { get; internal set; }
        public string WorkingOn { get; internal set; }
        public float Progress01 { get; internal set; }

        private FuelPouch(ItemDrop.ItemData upgrade, PouchKind kind)
        {
            Upgrade = upgrade;
            _kind = kind;
            Load();
        }

        internal sealed class PouchKind
        {
            internal string Id;
            internal Func<int, IReadOnlyList<string>> Accepts;
            internal Func<int, int> Capacity;
            internal Func<int> LowAt;
        }

        /// <summary>Each fuel upgrade registers what its pouch takes, once at startup.</summary>
        internal static void Register(string upgradeId, Func<int, IReadOnlyList<string>> accepts, Func<int, int> capacity, Func<int> lowAt)
            => Kinds[upgradeId] = new PouchKind { Id = upgradeId, Accepts = accepts, Capacity = capacity, LowAt = lowAt };

        /// <summary>The pouch of a fuel upgrade item, or null when the item is not a Smelter's / Kiln's upgrade.</summary>
        public static FuelPouch For(ItemDrop.ItemData upgradeItem)
        {
            if (upgradeItem == null) return null;
            if (ByItem.TryGetValue(upgradeItem, out FuelPouch cached)) return cached;
            string id = IdOf(upgradeItem);
            if (id == null || !Kinds.TryGetValue(id, out PouchKind kind)) return null;
            var pouch = new FuelPouch(upgradeItem, kind);
            ByItem[upgradeItem] = pouch;
            return pouch;
        }

        public int CountOf(string prefab) => prefab != null && _counts.TryGetValue(prefab, out int n) ? n : 0;

        /// <summary>One display stack per fuel type with a count, for the pouch window's cells.</summary>
        public IEnumerable<ItemDrop.ItemData> Contents
        {
            get
            {
                foreach (string prefab in Accepts.Concat(_counts.Keys).Distinct())
                {
                    int n = CountOf(prefab);
                    if (n <= 0) continue;
                    ItemDrop.ItemData shown = PrefabItem(prefab)?.Clone();
                    if (shown == null) continue;
                    shown.m_stack = n;
                    yield return shown;
                }
            }
        }

        /// <summary>
        /// Moves as much of <paramref name="stack"/> into the pouch as fits and returns how many were taken (0 = refused: not this
        /// pouch's fuel, or full). When <paramref name="from"/> is given, the taken amount is removed from it here; otherwise the
        /// caller removes it.
        /// </summary>
        public int TryFill(ItemDrop.ItemData stack, Inventory from = null)
        {
            string prefab = stack?.m_dropPrefab != null ? stack.m_dropPrefab.name : null;
            if (prefab == null || !Accepts.Contains(prefab)) return 0;
            int take = Mathf.Min(stack.m_stack, Capacity - Count);
            if (take <= 0) return 0;
            if (from != null && !from.RemoveItem(stack, take)) return 0;
            _counts[prefab] = CountOf(prefab) + take;
            Commit($"filled {take} {prefab}");
            return take;
        }

        /// <summary>Burns <paramref name="amount"/> of the first accepted fuel that has enough, in burn order; the name of what burned, or null.</summary>
        internal string TryBurn(int amount)
        {
            foreach (string prefab in Accepts)
            {
                if (CountOf(prefab) < amount) continue;
                _counts[prefab] = CountOf(prefab) - amount;
                Commit($"burned {amount} {prefab}");
                return prefab;
            }
            return null;
        }

        /// <summary>The fuel that would burn next (burn order), or null when none is left at this tier.</summary>
        internal string NextFuel() => Accepts.FirstOrDefault(p => CountOf(p) > 0);

        /// <summary>Takes up to <paramref name="amount"/> of <paramref name="prefab"/> out of the pouch into <paramref name="to"/>; how many moved.</summary>
        public int TryTakeOut(string prefab, int amount, Inventory to)
        {
            GameObject item = PrefabObject(prefab);
            int move = Mathf.Min(amount, CountOf(prefab));
            if (item == null || to == null || move <= 0) return 0;
            while (move > 0 && !to.CanAddItem(item, move)) move--;
            if (move <= 0 || !to.AddItem(item, move)) return 0;
            _counts[prefab] = CountOf(prefab) - move;
            Commit($"took out {move} {prefab}");
            return move;
        }

        /// <summary>
        /// Before the upgrade is swapped or removed: every fuel goes back into <paramref name="bagInventory"/>, and what doesn't fit
        /// drops at the player's feet. Never destroyed. Returns a short "Coal 12" summary for the log / popup.
        /// </summary>
        public string ReturnToBag(Inventory bagInventory, Player player)
        {
            var parts = new List<string>();
            foreach (string prefab in _counts.Keys.ToList())
            {
                int n = CountOf(prefab);
                if (n <= 0) continue;
                GameObject item = PrefabObject(prefab);
                if (item == null) continue;
                int kept = 0;
                if (bagInventory != null) { kept = n; while (kept > 0 && !bagInventory.CanAddItem(item, kept)) kept--; if (kept > 0) bagInventory.AddItem(item, kept); }
                for (int dropped = kept; dropped < n; dropped++) DropAtFeet(item, player);
                parts.Add($"{prefab} {n}{(kept < n ? $" ({n - kept} dropped)" : "")}");
                _counts[prefab] = 0;
            }
            Commit("returned to the bag: " + (parts.Count > 0 ? string.Join(", ", parts) : "nothing"));
            ByItem.Remove(Upgrade);
            return string.Join(", ", parts);
        }

        /// <summary>Text for the swap/remove popup: what goes back to the bag.</summary>
        public string SwapWarning() => IsEmpty ? null
            : $"The fuel in this upgrade's pouch ({string.Join(", ", _counts.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key} {kv.Value}"))}) goes back to your bag, or drops at your feet if the bag is full.";

        internal void RequestOpen()
        {
            if (OpenRequested != null) { OpenRequested(this); return; }
            QuickFill();
        }

        /// <summary>
        /// Until the pouch window exists: a right-click on the slotted upgrade fills the pouch with every accepted fuel from the
        /// bag; shift + right-click empties the pouch back into the bag. A centre message says what moved.
        /// </summary>
        private void QuickFill()
        {
            Player p = Player.m_localPlayer;
            ItemDrop.ItemData bag = p != null ? BagUpgrades.WornBagWith(p, UpgradeId) : null;
            Inventory inv = BagUpgrades.BagInventory(p, bag);
            if (p == null || bag == null || inv == null) { FuelPouchLog.Debug($"[{UpgradeId}] pouch: {Describe()} (no worn bag to fill from)"); return; }
            bool empty = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            int moved = 0;
            if (empty)
            {
                foreach (string prefab in _counts.Keys.ToList())
                    moved += TryTakeOut(prefab, CountOf(prefab), inv);
            }
            else
            {
                foreach (ItemDrop.ItemData stack in BagUpgrades.ItemsInBag(p, bag).ToList())
                {
                    if (stack?.m_dropPrefab == null || !Accepts.Contains(stack.m_dropPrefab.name)) continue;
                    moved += TryFill(stack, inv);
                    if (Count >= Capacity) break;
                }
            }
            string what = empty ? $"{moved} fuel back into the bag" : moved > 0 ? $"{moved} fuel from the bag" : $"no {string.Join(" / ", Accepts)} in the bag";
            p.Message(MessageHud.MessageType.Center, $"{Upgrade.m_shared.m_name}: {what} (pouch {Describe()})");
            FuelPouchLog.Debug($"[{UpgradeId}] pouch quick-{(empty ? "empty" : "fill")}: {what}; pouch now {Describe()}");
        }

        internal string Describe() => IsEmpty ? "empty" : string.Join(", ", _counts.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key} {kv.Value}")) + $" / {Capacity}";

        private void Load()
        {
            _counts.Clear();
            if (Upgrade?.m_customData == null || !Upgrade.m_customData.TryGetValue(KeyPrefix + _kind.Id + FuelKey, out string raw) || string.IsNullOrEmpty(raw)) return;
            foreach (string entry in raw.Split(EntrySeparator))
            {
                int cut = entry.LastIndexOf(CountSeparator);
                if (cut <= 0 || !int.TryParse(entry.Substring(cut + 1), out int n) || n <= 0) continue;
                _counts[entry.Substring(0, cut)] = n;
            }
        }

        private void Commit(string what)
        {
            if (Upgrade != null)
            {
                if (Upgrade.m_customData == null) Upgrade.m_customData = new Dictionary<string, string>();
                Upgrade.m_customData[KeyPrefix + _kind.Id + FuelKey] = string.Join(EntrySeparator.ToString(),
                    _counts.Where(kv => kv.Value > 0).Select(kv => kv.Key + CountSeparator + kv.Value));
                ItemDrop.ItemData bag = BagUpgrades.WornBagWith(Player.m_localPlayer, _kind.Id);
                if (bag != null) BagUpgrades.SaveUpgradeState(bag, BackpackSpecialSlots.SlotTool);
            }
            FuelPouchLog.Debug($"[{_kind.Id}] pouch {what}; now {Describe()}");
            Changed?.Invoke();
            AnyChanged?.Invoke(this);
        }

        // The framework's own parser is private; upgrade items are named FiresBagUp_<Id>_<tier>.
        internal static string IdOf(ItemDrop.ItemData item) => ParseName(item, out string id, out _) ? id : null;
        internal static int TierOf(ItemDrop.ItemData item) => ParseName(item, out _, out int tier) ? tier : 0;

        private static bool ParseName(ItemDrop.ItemData item, out string id, out int tier)
        {
            id = null; tier = 0;
            string name = item?.m_dropPrefab != null ? item.m_dropPrefab.name : null;
            if (name == null || !name.StartsWith(BagUpgrades.PrefabPrefix, StringComparison.Ordinal)) return false;
            int cut = name.LastIndexOf('_');
            if (cut <= BagUpgrades.PrefabPrefix.Length || !int.TryParse(name.Substring(cut + 1), out tier) || tier < 1 || tier > BagUpgrades.TierCount) return false;
            id = name.Substring(BagUpgrades.PrefabPrefix.Length, cut - BagUpgrades.PrefabPrefix.Length);
            return true;
        }

        private static GameObject PrefabObject(string prefab) => ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null;
        private static ItemDrop.ItemData PrefabItem(string prefab) => PrefabObject(prefab)?.GetComponent<ItemDrop>()?.m_itemData;

        private static void DropAtFeet(GameObject item, Player player)
        {
            if (player == null) return;
            Vector3 fuelUp = Camp.CampFrame.InField(player.transform.position) ? Camp.CampFrame.UpAt(player.transform.position) : Vector3.up;   // 1.0.129: along the ring's up on a halo ring
                Vector3 at = player.transform.position + fuelUp;
            UnityEngine.Object.Instantiate(item, at, Quaternion.FromToRotation(Vector3.up, fuelUp));
        }
    }

    internal static class FuelPouchLog
    {
        private static readonly BepInEx.Logging.ManualLogSource Source = BepInEx.Logging.Logger.CreateLogSource("VABackpacks.FuelPouch");
        internal static void Debug(string line) => Source.LogDebug(line);
    }
}
