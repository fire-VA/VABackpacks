// FuelPouchWindow: the Smelter's / Kiln's fuel pouch window (a Core KitWindow, uGUI), opened by a right-click on the slotted upgrade
// (FuelPouch.OpenRequested). One window; a reopen replaces the pouch shown. Rows burn top to bottom; Take back empties one fuel into
// the bag; clicking a dragged fuel stack onto the window fills the pouch. Being uGUI on Core's window canvas, the cursor item and
// tooltips draw over it. Client only. Core (FiresUnifiedCore) is a SOFT dependency of VABackpacks: without it, or with an older Core
// that has no KitWindow, the window never subscribes and the framework's quick-fill keeps working. Every Core type lives in
// FuelPouchView / BagKitUi, which only run once the window exists (Core present), never from the patches or the test hook.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;
using FiresCore.UI.Kit;
using FiresTokens = FiresCore.UI.Shell.Kit.FiresTokens;
using KitWindow = FiresCore.UI.Kit.KitWindow;

namespace VerdantsAscent.Managers
{
    /// <summary>Subscribes the window to FuelPouch.OpenRequested once ZNet is up. Idempotent: ZNet.Awake runs per world load.</summary>
    [HarmonyPatch(typeof(ZNet), "Awake")]
    internal static class FuelPouchWindowInit
    {
        [HarmonyPostfix]
        private static void Postfix() => FuelPouchWindow.Install();
    }

    internal enum BagTone { Muted, Ok, Warn, Danger }

    public sealed class FuelPouchWindow : MonoBehaviour
    {
        private const string BurnsFirst = "burns first", NoneLeft = "none";

        // The Smelter's WORKING line names the bar ("Copper from Copper ore"); the Smelter's own table is private.
        private static readonly Dictionary<string, string> SmelterBars = new Dictionary<string, string>
        {
            { "CopperOre", "Copper" }, { "TinOre", "Tin" }, { "IronScrap", "Iron" }, { "SilverOre", "Silver" },
            { "BlackMetalScrap", "BlackMetal" }, { "FlametalOreNew", "FlametalNew" },
        };
        private const string KilnOutput = "Coal";

        /// <summary>One fuel row's data (the view shows it).</summary>
        internal struct RowData
        {
            internal string Prefab, Name, Sub;
            internal int Count;
            internal float Fill;
        }

        // ── vanilla drag (InventoryGui private fields / methods; names as in the game) ──
        private static readonly AccessTools.FieldRef<InventoryGui, ItemDrop.ItemData> DragItem = AccessTools.FieldRefAccess<InventoryGui, ItemDrop.ItemData>("m_dragItem");
        private static readonly AccessTools.FieldRef<InventoryGui, Inventory> DragInventory = AccessTools.FieldRefAccess<InventoryGui, Inventory>("m_dragInventory");
        private static readonly AccessTools.FieldRef<InventoryGui, int> DragAmount = AccessTools.FieldRefAccess<InventoryGui, int>("m_dragAmount");
        private static readonly AccessTools.FieldRef<InventoryGui, GameObject> DragGo = AccessTools.FieldRefAccess<InventoryGui, GameObject>("m_dragGo");
        private static readonly System.Reflection.MethodInfo SetupDragItem = AccessTools.Method(typeof(InventoryGui), "SetupDragItem");
        private static readonly System.Reflection.MethodInfo UpdateCraftingPanel = AccessTools.Method(typeof(InventoryGui), "UpdateCraftingPanel");

        private static FuelPouchWindow s_instance;
        private static bool s_installed, s_noCoreLogged;

        internal static FuelPouchWindow Instance => s_instance;

        // ── the pouch and its view ──
        private FuelPouch _pouch;
        private bool _dirty = true;
        private int _rowCount;
        private FuelPouchView _view;
        private string _status = "";
        private BagTone _statusTone;
        private bool _layoutLogged;
        private int _shownFrame;
        internal string LastLayoutLine { get; private set; }

        // ── wiring ───────────────────────────────────────────────────────────────────────────────────────────────────

        internal static void Install()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;   // dedicated server: no window
            string missing = BagWindowKit.Missing();
            if (missing != null)
            {
                if (!s_noCoreLogged) { s_noCoreLogged = true; FuelPouchLog.Debug($"[FuelPouch] window not subscribed: {missing} (right-click keeps the quick-fill)"); }
                return;
            }
            Subscribe();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Subscribe()
        {
            FuelPouch.OpenRequested -= OnOpenRequested;   // ZNet.Awake runs per world load
            FuelPouch.OpenRequested += OnOpenRequested;
            FuelPouch.AnyChanged -= OnAnyChanged;
            FuelPouch.AnyChanged += OnAnyChanged;
            if (!s_installed) { s_installed = true; FuelPouchLog.Debug("[FuelPouch] window subscribed to OpenRequested (the quick-fill fallback is off while subscribed)"); }
        }

        private static void OnOpenRequested(FuelPouch pouch)
        {
            if (pouch == null) return;
            if (s_instance == null)
            {
                var go = new GameObject("VABackpacksFuelPouchWindow");
                DontDestroyOnLoad(go);
                s_instance = go.AddComponent<FuelPouchWindow>();
            }
            s_instance.Show(pouch);
        }

        private static void OnAnyChanged(FuelPouch pouch)
        {
            if (s_instance != null && pouch != null && pouch == s_instance._pouch) s_instance._dirty = true;
        }

        private void OnPouchChanged() => _dirty = true;

        // ── open / close ─────────────────────────────────────────────────────────────────────────────────────────────

        internal bool IsShowing => _pouch != null;
        internal FuelPouch Pouch => _pouch;
        internal int RowCount => _pouch != null ? _rowCount : 0;
        internal string Status => _status;

        private void Show(FuelPouch pouch)
        {
            bool fresh = _pouch == null;
            if (_pouch != null) _pouch.Changed -= OnPouchChanged;
            _pouch = pouch;
            _pouch.Changed += OnPouchChanged;
            _layoutLogged = false;
            _status = ""; _statusTone = BagTone.Muted;
            if (_view == null || !_view.Alive) _view = new FuelPouchView(this);
            Refresh();
            _view.Open();
            _shownFrame = Time.frameCount;
            FuelPouchLog.Debug($"[FuelPouch] window {(fresh ? "open" : "now shows")}: {pouch.UpgradeId} {Numeral(pouch.Tier)} ({PrefabOf(pouch.Upgrade)}), {DescribeCounts(pouch)}");
        }

        internal void Close(string why)
        {
            if (_pouch == null) return;
            FuelPouchLog.Debug($"[FuelPouch] window closed ({why})");
            _pouch.Changed -= OnPouchChanged;
            _pouch = null;
            _rowCount = 0;
            _view?.Hide();   // its OnClosed sees no pouch and does nothing more
        }

        /// <summary>The KitWindow hid itself (its close button or Core's Esc).</summary>
        internal void OnWindowClosed()
        {
            if (_pouch == null) return;   // closed from here
            bool esc = Input.GetKeyDown(KeyCode.Escape);
            Close(esc ? "Esc" : "close button");
        }

        private void OnDestroy()
        {
            Close("destroyed");
            if (s_instance == this) s_instance = null;
        }

        // ── frame ────────────────────────────────────────────────────────────────────────────────────────────────────

        private void Update()
        {
            if (_pouch == null) return;
            string why = CloseReason();
            if (why != null) { Close(why); return; }
            if (_view == null || !_view.Alive) { Close("window gone"); return; }

            if (_dirty) Refresh();
            RefreshWorking();
            _view.SetHighlight(DraggedFuelFits() && _view.MouseOver());
            _view.TrackPlacement();
            if (!_layoutLogged && Time.frameCount > _shownFrame + 1)
            {
                _layoutLogged = true;
                LastLayoutLine = "[FuelPouch] window layout: " + _view.LayoutReport();
                FuelPouchLog.Debug(LastLayoutLine);
            }
        }

        private string CloseReason()
        {
            Player player = Player.m_localPlayer;
            if (player == null || player.IsDead()) return "no player";
            if (!InventoryGui.IsVisible()) return "inventory closed";
            if (FuelPouch.For(_pouch.Upgrade) != _pouch) return "upgrade left its slot";
            if (HoldingBag(player) == null) return BagUpgrades.WornBagWith(player, _pouch.UpgradeId) == null ? "bag no longer worn" : "upgrade left its slot";
            return null;
        }

        // the worn bag whose slot holds this very upgrade item
        private ItemDrop.ItemData HoldingBag(Player player)
        {
            string slot = BagUpgrades.SlotOf(_pouch.Upgrade) ?? BackpackSpecialSlots.SlotTool;
            foreach (ItemDrop.ItemData bag in BackpackApi.GetEquippedBackpacks(player))
                if (bag != null && BackpackSpecialSlots.Read(bag, slot) == _pouch.Upgrade) return bag;
            return null;
        }

        private bool DraggedFuelFits()
        {
            InventoryGui gui = InventoryGui.instance;
            if (gui == null || DragGo(gui) == null) return false;
            ItemDrop.ItemData item = DragItem(gui);
            string prefab = item?.m_dropPrefab != null ? item.m_dropPrefab.name : null;
            return prefab != null && Contains(_pouch.Accepts, prefab) && _pouch.Count < _pouch.Capacity;
        }

        // ── data (read on a change only; the view updates in place) ──────────────────────────────────────────────────

        private void Refresh()
        {
            _dirty = false;
            var rows = new List<RowData>();
            bool firstFound = false;
            int cap = Mathf.Max(1, _pouch.Capacity);
            foreach (string prefab in _pouch.Accepts)
            {
                int n = _pouch.CountOf(prefab);
                var row = new RowData { Prefab = prefab, Count = n, Fill = Mathf.Clamp01(n / (float)cap), Name = NameOf(prefab) };
                if (n > 0 && !firstFound) { firstFound = true; row.Sub = BurnsFirst; }
                else if (n <= 0) row.Sub = NoneLeft;
                rows.Add(row);
            }
            _rowCount = rows.Count;
            string id = _pouch.UpgradeId;
            string fuelWord = id == SmelterOption.UpgradeId ? "Coal" : "wood";
            BagTone countTone = _pouch.IsEmpty ? BagTone.Danger : _pouch.IsLow ? BagTone.Warn : BagTone.Muted;
            _view.SetData(id + " fuel", $"{id} Bag Upgrade {Numeral(_pouch.Tier)} · burns top to bottom",
                $"{_pouch.Count} / {_pouch.Capacity}", countTone, rows,
                $"Drop {fuelWord} on this window or on the {id}'s slot to add it. Take back = to your bag (what doesn't fit stays in the pouch).");
            _view.SetStatus(_status, _statusTone);
        }

        // The options set the work state without raising Changed, so the WORKING line is re-read every Update (the labels change only on a new text).
        private void RefreshWorking()
        {
            if (_pouch.IsWorking && !string.IsNullOrEmpty(_pouch.WorkingOn))
            {
                string output = _pouch.UpgradeId == SmelterOption.UpgradeId
                    ? (SmelterBars.TryGetValue(_pouch.WorkingOn, out string bar) ? NameOf(bar) : "Bars")
                    : NameOf(KilnOutput);
                float p = Mathf.Clamp01(_pouch.Progress01);
                _view.SetWorking($"{output} from {NameOf(_pouch.WorkingOn)}", Mathf.RoundToInt(p * 100f) + "%", true, p, BagTone.Muted);
            }
            else if (_pouch.IsWaitingForFuel) _view.SetWorking("Waiting for fuel", "", false, 0f, BagTone.Danger);
            else _view.SetWorking("Idle", "", false, 0f, BagTone.Muted);
        }

        // ── actions (window buttons, the dropped stack, the test hook) ───────────────────────────────────────────────

        internal int TakeBack(string prefab)
        {
            Player player = Player.m_localPlayer;
            if (_pouch == null || player == null) return 0;
            int n = _pouch.CountOf(prefab);
            if (n <= 0) return 0;
            ItemDrop.ItemData bag = HoldingBag(player);
            Inventory to = BagUpgrades.BagInventory(player, bag) ?? player.GetInventory();
            int moved = _pouch.TryTakeOut(prefab, n, to);
            string name = NameOf(prefab);
            if (moved <= 0) SetStatus($"No room in your bag for {name}", BagTone.Danger);
            else if (moved < n) SetStatus($"Took back {moved} {name}; {n - moved} didn't fit", BagTone.Warn);
            else SetStatus($"Took back {moved} {name}", BagTone.Ok);
            FuelPouchLog.Debug($"[FuelPouch] take back via the window: {moved} of {n} {prefab} to the bag; pouch now {DescribeCounts(_pouch)}");
            _dirty = true;
            return moved;
        }

        /// <summary>A left click on the window while vanilla drags a stack (the window's canvas is above vanilla's drop-outside button,
        /// so that button never sees this click): fill or refuse here; the stack never lands on the ground.</summary>
        internal void OnWindowClicked()
        {
            InventoryGui gui = InventoryGui.instance;
            if (_pouch == null || gui == null || DragGo(gui) == null) return;
            ItemDrop.ItemData item = DragItem(gui);
            Inventory from = DragInventory(gui);
            if (item == null || from == null) return;
            if (!from.ContainsItem(item)) { InvokeSetupDrag(gui, null, null, 1); return; }
            FillFromDrag(gui, item, from, DragAmount(gui));
        }

        private void FillFromDrag(InventoryGui gui, ItemDrop.ItemData item, Inventory from, int dragAmount)
        {
            string id = _pouch.UpgradeId;
            string prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : null;
            string label = item.m_shared != null ? Localize(item.m_shared.m_name) : prefab;
            if (prefab == null || !Contains(_pouch.Accepts, prefab))
            {
                SetStatus($"Not fuel for this {id}", BagTone.Danger);
                FuelPouchLog.Debug($"[FuelPouch] refused drop on the window: {prefab ?? "?"} is not {id} fuel (takes {string.Join(", ", _pouch.Accepts)})");
                return;
            }
            int room = _pouch.Capacity - _pouch.Count;
            if (room <= 0)
            {
                SetStatus($"The {id}'s pouch is full", BagTone.Warn);
                FuelPouchLog.Debug($"[FuelPouch] refused drop on the window: {prefab} x{dragAmount}, the pouch is full ({_pouch.Count} / {_pouch.Capacity})");
                return;
            }
            int amount = Mathf.Clamp(dragAmount, 1, item.m_stack);
            int moved;
            if (amount >= item.m_stack) moved = _pouch.TryFill(item, from);   // the whole stack: the API takes what fits and removes it
            else
            {
                // a split drag: only the dragged part goes in. Removed first, then filled; never more than fits, so nothing is lost
                int take = Mathf.Min(amount, room);
                ItemDrop.ItemData part = item.Clone();
                part.m_stack = take;
                if (!from.RemoveItem(item, take)) { SetStatus($"Could not take {label}", BagTone.Danger); FuelPouchLog.Debug($"[FuelPouch] refused drop on the window: could not remove {take} {prefab} from the dragged stack"); return; }
                moved = _pouch.TryFill(part);
                if (moved < take && item.m_dropPrefab != null) from.AddItem(item.m_dropPrefab, take - moved);
            }
            if (moved <= 0)
            {
                SetStatus($"Not fuel for this {id}", BagTone.Danger);
                FuelPouchLog.Debug($"[FuelPouch] refused drop on the window: {prefab} x{amount}, the pouch took none");
                return;
            }
            // vanilla after a move: the whole drag moved -> SetupDragItem(null); a partial move -> the drag amount follows the stack
            int left = amount - moved;
            if (left <= 0 || !from.ContainsItem(item) || item.m_stack <= 0) InvokeSetupDrag(gui, null, null, 1);
            else DragAmount(gui) = Mathf.Min(left, item.m_stack);
            try { UpdateCraftingPanel?.Invoke(gui, null); } catch (Exception e) { FuelPouchLog.Debug($"[FuelPouch] UpdateCraftingPanel threw {(e.InnerException ?? e).Message}"); }
            SetStatus($"Added {moved} {label}" + (left > 0 ? $"; {left} didn't fit" : ""), BagTone.Ok);
            FuelPouchLog.Debug($"[FuelPouch] fill via the window: {moved} {prefab} of {amount} dragged{(left > 0 ? $" ({left} stay on the cursor)" : "")}; pouch now {DescribeCounts(_pouch)}");
            _dirty = true;
        }

        private static void InvokeSetupDrag(InventoryGui gui, ItemDrop.ItemData item, Inventory inv, int amount)
        {
            try { SetupDragItem?.Invoke(gui, new object[] { item, inv, amount }); }
            catch (Exception e) { FuelPouchLog.Debug($"[FuelPouch] SetupDragItem threw {(e.InnerException ?? e).Message}"); }
        }

        private void SetStatus(string text, BagTone tone)
        {
            _status = text ?? ""; _statusTone = tone;
            if (_view != null && _view.Alive) _view.SetStatus(_status, tone);
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

        private static bool Contains(IReadOnlyList<string> list, string s)
        {
            for (int i = 0; i < list.Count; i++) if (list[i] == s) return true;
            return false;
        }

        internal static ItemDrop.ItemData ItemOf(string prefab)
        {
            GameObject go = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null;
            return go != null ? go.GetComponent<ItemDrop>()?.m_itemData : null;
        }

        internal static Sprite IconOf(string prefab) => prefab != null ? ItemOf(prefab)?.GetIcon() : null;

        internal static string NameOf(string prefab)
        {
            ItemDrop.ItemData d = ItemOf(prefab);
            return d?.m_shared != null ? Localize(d.m_shared.m_name) : prefab;
        }

        internal static string Localize(string token) => Localization.instance != null ? Localization.instance.Localize(token) : token;

        internal static string Numeral(int tier) => tier >= 1 && tier <= BagUpgrades.TierNumerals.Length ? BagUpgrades.TierNumerals[tier - 1] : tier.ToString();

        private static string PrefabOf(ItemDrop.ItemData item) => item?.m_dropPrefab != null ? item.m_dropPrefab.name : "?";

        private static string DescribeCounts(FuelPouch p)
        {
            var parts = new List<string>();
            foreach (string prefab in p.Accepts) parts.Add($"{prefab} {p.CountOf(prefab)}");
            return string.Join(", ", parts) + $" = {p.Count} / {p.Capacity}" + (p.IsEmpty ? " (empty)" : p.IsLow ? " (low)" : "");
        }
    }

    /// <summary>Forwards left clicks on the fuel pouch window (and its children without a click handler of their own) to the window.</summary>
    internal sealed class FuelPouchDropTarget : MonoBehaviour, IPointerClickHandler
    {
        internal FuelPouchWindow Owner;
        public void OnPointerClick(PointerEventData e) { if (e.button == PointerEventData.InputButton.Left && Owner != null) Owner.OnWindowClicked(); }
    }

    /// <summary>The fuel pouch window's uGUI (Core KitWindow + KitUI). Built once per pouch layout; counts and texts update in place.</summary>
    internal sealed class FuelPouchView
    {
        private const string WindowKey = "VABackpacks.FuelPouch";
        private const float DefaultW = 400f, MinW = 360f, MinH = 220f;
        private const float RowPadY = 6f, ColGap = 12f, NameH = 18f, SmallH = 14f, ColGapY = 1f, BarH = 6f, BarTop = 3f;
        private const float WorkLineH = 20f, WorkGap = 4f, PercentW = 48f, StatusH = 18f, HighlightBorder = 2f;
        private const string FuelHelp = "Fuel burns top to bottom. Click a dragged stack onto this window to add it; what doesn't fit stays on the cursor.";

        private sealed class RowView
        {
            internal string Prefab;
            internal KitIconCell Cell;
            internal TextMeshProUGUI Name, Sub;
            internal Image BarBack, BarFill;
            internal RectTransform Fill;
            internal KitButton TakeBack;
        }

        private readonly FuelPouchWindow _owner;
        private readonly KitWindow _win;
        private readonly Image _plate;
        private readonly Sprite _plateNormal, _plateHighlight;
        private readonly TextMeshProUGUI _count, _note, _working, _percent, _status;
        private readonly RectTransform _rowsRoot;
        private readonly Image _workBack, _workFill;
        private readonly RectTransform _workFillRt;
        private readonly List<RowView> _rows = new List<RowView>();
        private bool _highlight;
        private Vector2 _placedPos, _placedSize;
        private static bool s_userPlaced;   // the player dragged or resized it this session: Core keeps that rect; otherwise BagWindowPlacement's rule

        internal bool Alive => _win != null && _win.Root != null;

        internal FuelPouchView(FuelPouchWindow owner)
        {
            _owner = owner;
            _win = KitWindow.Create(WindowKey, "", "", new Vector2(DefaultW, MinH), new Vector2(MinW, MinH));
            _win.DefaultPlacement = BagWindowPlacement.PlaceScreen;
            _win.RememberRect = s_userPlaced;
            _win.OnClosed = () => { if (_owner != null) _owner.OnWindowClosed(); };
            _plate = _win.Root.GetComponent<Image>();
            _plateNormal = _plate != null ? _plate.sprite : null;
            _plateHighlight = FiresCore.UI.FiresRoundedSkin.RoundedSprite(Mathf.RoundToInt(FiresTokens.Radius), FiresTokens.Panel, FiresTokens.GoldBright, (int)HighlightBorder);
            var drop = _win.Root.gameObject.GetComponent<FuelPouchDropTarget>() ?? _win.Root.gameObject.AddComponent<FuelPouchDropTarget>();
            drop.Owner = owner;

            Transform body = _win.Body;
            for (int i = body.childCount - 1; i >= 0; i--) BagKitUi.Drop(body.GetChild(i).gameObject);   // a window left by an earlier view

            RectTransform fuel = KitUI.Section(body, "FUEL", FuelHelp);
            _count = BagKitUi.HeaderRight(fuel, "0 / 0");
            _rowsRoot = BagKitUi.Column(fuel, "Rows", 0f);
            _note = KitUI.Note(fuel, "");
            _note.alignment = TextAlignmentOptions.Top;

            RectTransform work = KitUI.Section(body, "WORKING");
            RectTransform line = BagKitUi.HRow(work, "Line", 8f, TextAnchor.MiddleLeft, 0f);
            _working = BagKitUi.Text(line, "Working", "", KitUI.BodySize, FiresTokens.TextMuted, TextAlignmentOptions.MidlineLeft, -1f, WorkLineH);
            _percent = BagKitUi.Text(line, "Percent", "", KitUI.BodySize, FiresTokens.TextMuted, TextAlignmentOptions.MidlineRight, PercentW, WorkLineH);
            BagKitUi.Gap(work, WorkGap);
            _workBack = BagKitUi.Bar(work, BarH, out _workFillRt, out _workFill);

            _status = BagKitUi.Text(body, "Status", "", KitUI.SmallSize, FiresTokens.Ok, TextAlignmentOptions.Center, -1f, StatusH);
        }

        internal void SetData(string title, string subtitle, string count, BagTone countTone, List<FuelPouchWindow.RowData> rows, string note)
        {
            _win.SetTitle(title);
            _win.SetSubtitle(subtitle);
            _count.text = count;
            _count.color = BagKitUi.ToneColour(countTone);
            _note.text = note;

            bool same = rows.Count == _rows.Count;
            for (int i = 0; same && i < rows.Count; i++) same = rows[i].Prefab == _rows[i].Prefab;
            if (!same) BuildRows(rows);
            for (int i = 0; i < rows.Count; i++) UpdateRow(_rows[i], rows[i]);
            if (_win.IsShowing) Fit();
        }

        private void BuildRows(List<FuelPouchWindow.RowData> rows)
        {
            foreach (RowView r in _rows) if (r.Cell?.Root != null) BagKitUi.Drop(r.Cell.Root.parent.gameObject);
            _rows.Clear();
            foreach (FuelPouchWindow.RowData d in rows)
            {
                var v = new RowView { Prefab = d.Prefab };
                RectTransform row = BagKitUi.HRow(_rowsRoot, "Row_" + d.Prefab, ColGap, TextAnchor.MiddleLeft, RowPadY);
                v.Cell = KitUI.IconCell(row, FuelPouchWindow.IconOf(d.Prefab), "", false);
                RectTransform col = BagKitUi.Column(row, "Text", ColGapY);
                BagKitUi.Flexible(col.gameObject);
                v.Name = BagKitUi.Text(col, "Name", "", KitUI.BodySize, FiresTokens.Text, TextAlignmentOptions.MidlineLeft, -1f, NameH);
                v.Sub = BagKitUi.Text(col, "Sub", "", KitUI.NoteSize, FiresTokens.TextFaint, TextAlignmentOptions.MidlineLeft, -1f, SmallH);
                BagKitUi.Gap(col, BarTop);
                v.BarBack = BagKitUi.Bar(col, BarH, out v.Fill, out v.BarFill);
                string prefab = d.Prefab;
                v.TakeBack = KitUI.Button(row, "Take back", false, () => _owner.TakeBack(prefab));
                _rows.Add(v);
            }
        }

        private static void UpdateRow(RowView v, FuelPouchWindow.RowData d)
        {
            bool has = d.Count > 0;
            v.Cell.SetCount(has ? d.Count.ToString() : "");
            v.Cell.SetDim(!has);
            if (v.Cell.Icon != null && v.Cell.Icon.sprite == null) v.Cell.SetIcon(FuelPouchWindow.IconOf(d.Prefab));   // ObjectDB was not up at build
            v.Name.text = d.Name ?? d.Prefab;
            v.Name.color = has ? FiresTokens.Text : FiresTokens.TextFaint;
            v.Sub.text = d.Sub ?? "";
            v.Sub.gameObject.SetActive(!string.IsNullOrEmpty(d.Sub));
            v.BarBack.gameObject.SetActive(has);
            BagKitUi.SetFill(v.Fill, d.Fill);
            v.TakeBack.SetEnabled(has);
        }

        internal void SetWorking(string text, string percent, bool progress, float p, BagTone tone)
        {
            if (_working.text != text) _working.text = text;
            Color c = tone == BagTone.Danger ? FiresTokens.Danger : FiresTokens.TextMuted;
            if (_working.color != c) _working.color = c;
            if (_percent.text != percent) _percent.text = percent;
            if (_workBack.enabled != progress) { _workBack.enabled = progress; _workFill.enabled = progress; }   // the bar keeps its place
            if (progress) BagKitUi.SetFill(_workFillRt, p);
        }

        internal void SetStatus(string text, BagTone tone)
        {
            _status.text = text ?? "";
            _status.color = BagKitUi.ToneColour(tone);
        }

        internal void Open()
        {
            _win.RememberRect = s_userPlaced;
            _win.Show();
            if (!s_userPlaced) { _win.SetDefaultSize(new Vector2(DefaultW, BagKitUi.FitHeight(_win))); _win.Show(); }   // placed at its real size
            Fit();
            _placedPos = _win.Root.anchoredPosition; _placedSize = _win.Root.sizeDelta;
        }

        internal void Hide() { if (Alive) _win.Hide(); }

        // grows to fit the rows (never shrinks a size the player chose); Core keeps it on screen
        private void Fit()
        {
            float need = BagKitUi.FitHeight(_win);
            Vector2 s = _win.Root.sizeDelta;
            if (s.y < need - 0.5f) { _win.Root.sizeDelta = new Vector2(s.x, need); _win.Clamp(); }
            if (!s_userPlaced) { _placedPos = _win.Root.anchoredPosition; _placedSize = _win.Root.sizeDelta; }
        }

        // a title drag or a grip resize: the session keeps that rect from now on
        internal void TrackPlacement()
        {
            if (s_userPlaced || !Input.GetMouseButton(0)) return;
            if (_win.Root.anchoredPosition != _placedPos || _win.Root.sizeDelta != _placedSize) { s_userPlaced = true; _win.RememberRect = true; }
        }

        internal bool MouseOver() => _win.IsShowing && RectTransformUtility.RectangleContainsScreenPoint(_win.Root, Input.mousePosition, null);

        internal void SetHighlight(bool on)
        {
            if (on == _highlight || _plate == null) return;
            _highlight = on;
            _plate.sprite = on ? _plateHighlight : _plateNormal;
        }

        internal string LayoutReport() => _win.LayoutReport();
    }

    /// <summary>Small layout helpers the bag windows share on top of KitUI (Core types; only run once a window exists).</summary>
    internal static class BagKitUi
    {
        internal static RectTransform NewRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        internal static RectTransform Column(Transform parent, string name, float spacing)
        {
            var c = NewRect(parent, name);
            var v = c.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = spacing; v.childAlignment = TextAnchor.UpperLeft;
            v.childControlWidth = true; v.childControlHeight = true; v.childForceExpandWidth = true; v.childForceExpandHeight = false;
            return c;
        }

        internal static RectTransform HRow(Transform parent, string name, float spacing, TextAnchor align, float padY)
        {
            var r = NewRect(parent, name);
            var h = r.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = spacing; h.childAlignment = align;
            h.padding = new RectOffset(0, 0, Mathf.RoundToInt(padY), Mathf.RoundToInt(padY));
            h.childControlWidth = true; h.childControlHeight = true; h.childForceExpandWidth = false; h.childForceExpandHeight = false;
            return r;
        }

        internal static LayoutElement Size(GameObject go, float w, float h)
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            if (w >= 0f) { le.preferredWidth = w; le.minWidth = w; }
            if (h >= 0f) { le.preferredHeight = h; le.minHeight = h; }
            return le;
        }

        internal static void Flexible(GameObject go)
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f; le.minWidth = 0f;
        }

        /// <summary>Destroys an element; hidden first so this frame's layout already leaves it out.</summary>
        internal static void Drop(GameObject go)
        {
            if (go == null) return;
            go.SetActive(false);
            UnityEngine.Object.Destroy(go);
        }

        internal static void Gap(Transform parent, float h) => Size(NewRect(parent, "Gap").gameObject, -1f, h);

        /// <summary>A kit label with a fixed height; width -1 = flexible (fills the row).</summary>
        internal static TextMeshProUGUI Text(Transform parent, string name, string text, float size, Color colour, TextAlignmentOptions align, float width, float height)
        {
            var t = KitUI.Label(parent, name, text, size, colour, align);
            var le = Size(t.gameObject, width, height);
            if (width < 0f) { le.flexibleWidth = 1f; le.minWidth = 0f; }
            KitUI.MarkLeaf(t.rectTransform);
            return t;
        }

        /// <summary>A label at the right of a section's header (e.g. the fuel count).</summary>
        internal static TextMeshProUGUI HeaderRight(RectTransform sectionContent, string text)
        {
            Transform head = sectionContent.parent != null ? sectionContent.parent.Find("Head") : null;
            var t = KitUI.Label(head != null ? head : sectionContent, "HeaderRight", text, KitUI.SmallSize, FiresTokens.TextMuted, TextAlignmentOptions.MidlineRight);
            Transform help = head != null ? head.Find("Help") : null;
            if (help != null) t.transform.SetSiblingIndex(help.GetSiblingIndex());   // the count, then the "?"
            KitUI.MarkLeaf(t.rectTransform);
            return t;
        }

        /// <summary>A thin rounded bar (Well, Edge border) with a gold fill; returns the back image.</summary>
        internal static Image Bar(Transform parent, float height, out RectTransform fill, out Image fillImg)
        {
            var back = NewRect(parent, "Bar");
            Size(back.gameObject, -1f, height);
            var bi = back.gameObject.AddComponent<Image>();
            KitUI.SetPlate(bi, KitUI.Plate(height * 0.5f, FiresTokens.Well, FiresTokens.Edge));
            bi.raycastTarget = false;
            fill = NewRect(back, "Fill");
            fill.anchorMin = Vector2.zero; fill.anchorMax = new Vector2(0f, 1f); fill.offsetMin = Vector2.zero; fill.offsetMax = Vector2.zero;
            fillImg = fill.gameObject.AddComponent<Image>();
            KitUI.SetPlate(fillImg, KitUI.Plate(height * 0.5f, FiresTokens.Gold, null));
            fillImg.raycastTarget = false;
            KitUI.MarkLeaf(back);
            return bi;
        }

        internal static void SetFill(RectTransform fill, float f)
        {
            var a = new Vector2(Mathf.Clamp01(f), 1f);
            if (fill.anchorMax != a) fill.anchorMax = a;
        }

        /// <summary>The window height that fits its Body (title bar + the Body's preferred height). The window must be showing.</summary>
        internal static float FitHeight(KitWindow w)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(w.Body);
            float chrome = w.Root.rect.height - w.Body.rect.height;
            return Mathf.Ceil(chrome + LayoutUtility.GetPreferredHeight(w.Body));
        }

        internal static Color ToneColour(BagTone t) =>
            t == BagTone.Danger ? FiresTokens.Danger : t == BagTone.Warn ? FiresTokens.Warn : t == BagTone.Ok ? FiresTokens.Ok : FiresTokens.TextMuted;
    }

    /// <summary>Drives the fuel pouch window without OS input. All members are safe when the window is closed.</summary>
    internal static class FuelPouchWindowTest
    {
        private static FuelPouchWindow W => FuelPouchWindow.Instance;

        internal static bool IsShowing => W != null && W.IsShowing;
        /// <summary>"Kiln 3" / "Smelter 1": the upgrade id and tier of the pouch shown, or null.</summary>
        internal static string PouchId => W?.Pouch != null ? W.Pouch.UpgradeId + " " + W.Pouch.Tier : null;
        internal static int RowCount => W != null ? W.RowCount : 0;
        internal static string Status => W?.Status;
        internal static string LastLayoutLine => W?.LastLayoutLine;

        /// <summary>Presses that row's Take back; how many went to the bag (0 when the row is empty or the window is closed).</summary>
        internal static int ClickTakeBack(string prefab) => W != null && W.IsShowing ? W.TakeBack(prefab) : 0;

        internal static void Close() => W?.Close("test");
    }
}
