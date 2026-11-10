// Refill (resources slot): bag cells marked "for autofill" are topped back up from home storage through the storage network. A mark is a
// cell (its position in the bag), the item in it and a target count (the stack size when marked). The reach is Remote Access's range when
// it is in the same bag, otherwise only near home. Marks are saved on the Refill upgrade, so they travel with the bag.

using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public sealed class RefillOption : IBagUpgradeOption
    {
        public const string UpgradeId = "Refill";
        private const string MarksKey = "BagUp.Refill.marks";   // "x,y,prefab,target|..."
        private static readonly int[] DefaultMarks = { 4, 8, 16 };
        private static readonly ConfigEntry<int>[] MaxMarks = new ConfigEntry<int>[BagUpgrades.TierCount];
        private static ConfigEntry<float> s_interval, s_nearHomeRange;
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks.Refill");
        private static float s_next;
        private static bool s_busy;

        private struct Mark { public int X, Y, Target; public string Prefab; }

        public string Id => UpgradeId;
        public string Name => "Refill";
        public string Slot => BackpackSpecialSlots.SlotResources;
        public string Donor => "Resin";
        public string[] Recipes => new[]
        {
            "forge,2,Bronze:4,Resin:10,LeatherScraps:5",
            "forge,4,Iron:6,Resin:15,Guck:2",
            "blackforge,1,BlackMetal:5,Resin:20,Sap:5",
        };

        public string Describe(int tier) => $"Keeps up to {MaxMarksFor(tier)} marked bag cells topped up from your home storage.";

        public void Bind(ConfigFile cfg, ConfigSync sync)
        {
            for (int t = 0; t < BagUpgrades.TierCount; t++)
            {
                MaxMarks[t] = cfg.Bind(BagUpgrades.Section, $"Refill {BagUpgrades.TierNumerals[t]} marked cells", DefaultMarks[t],
                    new ConfigDescription($"Refill {BagUpgrades.TierNumerals[t]}: how many bag cells can be marked for autofill. Server-synced.", new AcceptableValueRange<int>(1, 64)));
                sync?.AddConfigEntry(MaxMarks[t]);
            }
            s_interval = cfg.Bind(BagUpgrades.Section, "Refill check seconds", 5f, new ConfigDescription("How often marked cells are checked and topped up.", new AcceptableValueRange<float>(1f, 60f)));
            s_nearHomeRange = cfg.Bind(BagUpgrades.Section, "Refill near home range", 50f, new ConfigDescription("Without Remote Access, Refill only works this close (horizontal m) to the home terminal. Server-synced.", new AcceptableValueRange<float>(0f, 500f)));
            sync?.AddConfigEntry(s_nearHomeRange);
            BagUpgrades.Tick += OnTick;
        }

        private static int MaxMarksFor(int tier) => tier < 1 ? 0 : MaxMarks[tier - 1]?.Value ?? DefaultMarks[tier - 1];

        // ---- the marks (the cell menu calls these) ----

        public static bool IsMarked(Player p, ItemDrop.ItemData cell) => Find(Read(p), cell) >= 0;

        /// <summary>Marks the cell for autofill at its current count (a full stack when it is full). False when the limit is reached.</summary>
        public static bool MarkCell(Player p, ItemDrop.ItemData cell)
        {
            var upgrade = Upgrade(p, out ItemDrop.ItemData bag, out int tier);
            if (upgrade == null || cell?.m_dropPrefab == null) return false;
            var marks = Read(p);
            if (Find(marks, cell) >= 0) return true;
            if (marks.Count >= MaxMarksFor(tier)) { p.Message(MessageHud.MessageType.Center, $"Refill: {marks.Count} cells marked already (this tier allows {MaxMarksFor(tier)})"); return false; }
            int target = cell.m_stack >= cell.m_shared.m_maxStackSize ? cell.m_shared.m_maxStackSize : cell.m_stack;
            marks.Add(new Mark { X = cell.m_gridPos.x, Y = cell.m_gridPos.y, Prefab = cell.m_dropPrefab.name, Target = Mathf.Max(1, target) });
            Write(upgrade, bag, marks);
            Log.LogDebug($"[Refill] marked cell ({cell.m_gridPos.x}, {cell.m_gridPos.y}) {cell.m_dropPrefab.name} to {target}");
            return true;
        }

        public static void UnmarkCell(Player p, ItemDrop.ItemData cell)
        {
            var upgrade = Upgrade(p, out ItemDrop.ItemData bag, out _);
            if (upgrade == null) return;
            var marks = Read(p); int i = Find(marks, cell);
            if (i < 0) return;
            marks.RemoveAt(i); Write(upgrade, bag, marks);
            Log.LogDebug($"[Refill] unmarked cell ({cell.m_gridPos.x}, {cell.m_gridPos.y})");
        }

        // ---- read-only views for the cell menu and the badge (RefillMenu) ----

        /// <summary>The marked target count of this cell; false when it isn't marked.</summary>
        internal static bool TryGetTarget(Player p, ItemDrop.ItemData cell, out int target)
        {
            var marks = Read(p); int i = Find(marks, cell);
            target = i >= 0 ? marks[i].Target : 0;
            return i >= 0;
        }

        /// <summary>Every marked cell position of the worn Refill bag, with its target.</summary>
        internal static Dictionary<Vector2i, int> MarkedCells(Player p)
        {
            var map = new Dictionary<Vector2i, int>();
            foreach (var m in Read(p)) map[new Vector2i(m.X, m.Y)] = m.Target;
            return map;
        }

        internal static int MarkCount(Player p) => Read(p).Count;

        internal static int WornMaxMarks(Player p) => MaxMarksFor(p != null ? BagUpgrades.WornTier(p, UpgradeId) : 0);

        /// <summary>How far from home Refill works: Remote Access's range when it is worn, else the near-home range.</summary>
        internal static float Reach(Player p)
        {
            float range = RemoteAccessOption.WornRange(p);
            return range > 0f ? range : s_nearHomeRange?.Value ?? 50f;
        }

        // ---- the top-up ----

        private static void OnTick(Player p, float dt)
        {
            if (p != Player.m_localPlayer || s_busy || Time.time < s_next) return;
            s_next = Time.time + (s_interval?.Value ?? 5f);
            var upgrade = Upgrade(p, out ItemDrop.ItemData bag, out _);
            if (upgrade == null) return;
            var marks = Read(p);
            if (marks.Count == 0) return;
            float range = Reach(p);
            if (RemoteAccessBridge.Refusal(p, range) != null) return;   // out of reach: try again next check
            Inventory inv = BagUpgrades.BagInventory(p, bag);
            if (inv == null) return;
            foreach (var m in marks)
            {
                ItemDrop.ItemData cell = inv.GetItemAt(m.X, m.Y);
                if (cell != null && (cell.m_dropPrefab == null || cell.m_dropPrefab.name != m.Prefab)) continue;   // the cell holds something else now
                int missing = m.Target - (cell != null ? cell.m_stack : 0);
                if (missing <= 0) continue;
                s_busy = true;
                var mark = m;
                RemoteAccessBridge.Withdraw(p, m.Prefab, missing, range, got => { s_busy = false; MoveIntoCell(p, inv, mark, got); });
                return;   // one cell per check keeps the server traffic small
            }
        }

        // The ledger puts withdrawn items into the player's inventory; move them into the marked cell.
        private static void MoveIntoCell(Player p, Inventory bagInv, Mark m, int got)
        {
            if (got <= 0 || p == null) return;
            Inventory main = p.GetInventory();
            int moved = 0;
            foreach (var it in new List<ItemDrop.ItemData>(main.GetAllItems()))
            {
                if (moved >= got) break;
                if (it?.m_dropPrefab == null || it.m_dropPrefab.name != m.Prefab) continue;
                int n = Mathf.Min(it.m_stack, got - moved);
                ItemDrop.ItemData cell = bagInv.GetItemAt(m.X, m.Y);
                if (cell == null)
                {
                    var clone = it.Clone(); clone.m_stack = n;
                    if (!bagInv.AddItem(clone, new Vector2i(m.X, m.Y))) break;
                }
                else { n = Mathf.Min(n, cell.m_shared.m_maxStackSize - cell.m_stack); if (n <= 0) break; cell.m_stack += n; bagInv.Changed(); }
                main.RemoveItem(it, n); moved += n;
            }
            Log.LogDebug($"[Refill] topped up cell ({m.X}, {m.Y}) {m.Prefab}: {got} from home, {moved} into the cell");
        }

        // ---- storage ----

        private static ItemDrop.ItemData Upgrade(Player p, out ItemDrop.ItemData bag, out int tier)
        {
            tier = p != null ? BagUpgrades.WornTier(p, UpgradeId) : 0;
            bag = tier > 0 ? BagUpgrades.WornBagWith(p, UpgradeId) : null;
            return bag != null ? BackpackSpecialSlots.Read(bag, BackpackSpecialSlots.SlotResources) : null;
        }

        private static List<Mark> Read(Player p)
        {
            var list = new List<Mark>();
            var upgrade = Upgrade(p, out _, out _);
            if (upgrade == null || !upgrade.m_customData.TryGetValue(MarksKey, out string raw) || string.IsNullOrEmpty(raw)) return list;
            foreach (string entry in raw.Split('|'))
            {
                string[] f = entry.Split(',');
                if (f.Length != 4 || !int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) || !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
                    || !int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int target)) continue;
                list.Add(new Mark { X = x, Y = y, Prefab = f[2], Target = target });
            }
            return list;
        }

        private static void Write(ItemDrop.ItemData upgrade, ItemDrop.ItemData bag, List<Mark> marks)
        {
            var parts = new List<string>();
            foreach (var m in marks) parts.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", m.X, m.Y, m.Prefab, m.Target));
            upgrade.m_customData[MarksKey] = string.Join("|", parts.ToArray());
            BagUpgrades.SaveUpgradeState(bag, BackpackSpecialSlots.SlotResources);
        }

        private static int Find(List<Mark> marks, ItemDrop.ItemData cell)
        {
            if (cell == null) return -1;
            for (int i = 0; i < marks.Count; i++) if (marks[i].X == cell.m_gridPos.x && marks[i].Y == cell.m_gridPos.y) return i;
            return -1;
        }
    }
}
