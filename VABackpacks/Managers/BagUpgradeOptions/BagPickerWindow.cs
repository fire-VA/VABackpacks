// BagPickerWindow: the shared picker window (a Core KitWindow, uGUI) behind the Feeding and Void upgrades. A right-click on the slotted
// upgrade calls the option's OpenPicker; the window shows the upgrade's numbered slots (the tier's count), and a click on a slot opens a
// list beside it (a second, chrome-less KitWindow): what is in this bag first, then what the player knows. Every change is saved
// through the option's own setter. One picker at a time; a reopen replaces what it shows. Client only. Core (FiresUnifiedCore) is a
// SOFT dependency: without it, or with an older Core that has no KitWindow, the pickers never set OpenPicker and the options keep their
// stopgaps. Core types live in BagPickerView / BagPickerListView only, which run once the window exists, never from the patches or the
// test hook. Helpers shared with the fuel pouch window live there (FuelPouchWindow.*, BagKitUi).

using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <summary>What one picker shows and how it reads and saves its list (Feeding, Void).</summary>
    internal sealed class BagPickerKind
    {
        internal int Index;                 // which picker window (and session rect) this is
        internal string Id, Title, Section, KnownHeader, EmptyText, FilledSub, Note, Warning, Noun, SlotKind, NoneKnown;
        internal bool ShowBagCount;
        internal Func<string, bool> Accepts;
        internal Func<ItemDrop.ItemData, List<string>> Get;
        internal Action<ItemDrop.ItemData, ItemDrop.ItemData, List<string>, int> Set;
        internal Func<int, int> CountFor;
        internal Func<Player, List<string>> Known;
        internal BepInEx.Logging.ManualLogSource Log;
    }

    internal sealed class BagPickerWindow : MonoBehaviour
    {
        internal const int KindCount = 2;
        private const int SearchThreshold = 12;
        private const float RefreshInterval = 0.5f;

        internal enum EntryType { Header, Separator, Item, Empty }

        /// <summary>One line of the list (the list view shows it).</summary>
        internal struct Entry
        {
            internal EntryType Type;
            internal string Prefab, Name, Right;
            internal bool Current;
        }

        /// <summary>One slot row's data (the picker view shows it).</summary>
        internal struct SlotData
        {
            internal string Prefab, Name, Sub;
        }

        private static BagPickerWindow s_instance;

        internal static BagPickerWindow Instance => s_instance;

        // ── what is shown ──
        private BagPickerKind _kind;
        private ItemDrop.ItemData _bag, _upgrade;
        private int _tier;
        private string[] _slots = new string[0];
        private readonly Dictionary<string, int> _bagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private bool _dirty = true;
        private float _nextRefresh;
        private string _shownKey;
        private string _status = "";
        private BagTone _statusTone;
        private bool _layoutLogged;
        private int _shownFrame;
        private readonly BagPickerView[] _views = new BagPickerView[KindCount];
        private BagPickerListView _list;
        internal string LastLayoutLine { get; private set; }

        // ── the list ──
        private int _popSlot = -1;
        private string _search = "";
        private List<string> _known;

        // ── wiring ───────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>True on a client with a Core that has KitWindow (the picker may set OpenPicker); otherwise logs once why not.</summary>
        internal static bool CanInstall(BagPickerKind kind, ref bool noCoreLogged)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return false;   // dedicated server: no window
            string missing = BagWindowKit.Missing();
            if (missing == null) return true;
            if (!noCoreLogged) { noCoreLogged = true; kind.Log.LogDebug($"[{kind.Id}] picker not subscribed: {missing} (the right-click keeps its stopgap)"); }
            return false;
        }

        internal static void Open(BagPickerKind kind, ItemDrop.ItemData bag, ItemDrop.ItemData upgrade, int tier)
        {
            if (kind == null || bag == null || upgrade == null) return;
            if (s_instance == null)
            {
                var go = new GameObject("VABackpacksBagPickerWindow");
                DontDestroyOnLoad(go);
                s_instance = go.AddComponent<BagPickerWindow>();
            }
            s_instance.Show(kind, bag, upgrade, tier);
        }

        // ── open / close ─────────────────────────────────────────────────────────────────────────────────────────────

        internal bool IsShowing => _kind != null;
        internal string KindId => _kind?.Id;
        internal string[] SlotsCopy => _kind != null ? (string[])_slots.Clone() : new string[0];
        internal string Status => _status;

        private BagPickerView View => _kind != null ? _views[Mathf.Clamp(_kind.Index, 0, KindCount - 1)] : null;

        private void Show(BagPickerKind kind, ItemDrop.ItemData bag, ItemDrop.ItemData upgrade, int tier)
        {
            if (_kind != null) Close(kind == _kind && upgrade == _upgrade ? "reopened" : "replaced by " + kind.Id);
            _kind = kind; _bag = bag; _upgrade = upgrade; _tier = tier;
            int i = Mathf.Clamp(kind.Index, 0, KindCount - 1);
            if (_views[i] == null || !_views[i].Alive) _views[i] = new BagPickerView(this, kind);
            if (_list == null || !_list.Alive) _list = new BagPickerListView(this);
            _layoutLogged = false;
            _status = ""; _statusTone = BagTone.Muted;
            _shownKey = null;
            Snapshot(true);
            _views[i].Open();
            _shownFrame = Time.frameCount;
            kind.Log.LogDebug($"[{kind.Id}] picker open: tier {tier}, {kind.Noun} [{Join(_slots)}]");
        }

        internal void Close(string why)
        {
            if (_kind == null) return;
            _kind.Log.LogDebug($"[{_kind.Id}] picker closed ({why})");
            ClosePopover();
            BagPickerView v = View;
            _kind = null; _bag = null; _upgrade = null;
            if (v != null && v.Alive) v.Hide();   // its OnClosed sees no kind and does nothing more
        }

        /// <summary>A picker KitWindow hid itself (its close button or Core's Esc).</summary>
        internal void OnWindowClosed(BagPickerView view)
        {
            if (_kind == null || view != View) return;   // closed from here
            bool esc = Input.GetKeyDown(KeyCode.Escape);
            Close(esc ? "Esc" : "close button");
        }

        /// <summary>The list KitWindow hid itself (Core's Esc closes it first, as it is the top window).</summary>
        internal void OnListClosed()
        {
            if (_popSlot < 0) return;   // closed from here
            bool esc = Input.GetKeyDown(KeyCode.Escape);
            if (esc) { _kind?.Log.LogDebug($"[{_kind.Id}] picker list closed (Esc)"); }
            _popSlot = -1;
            View?.SetOpenSlot(-1);
        }

        private void OnDestroy()
        {
            Close("destroyed");
            if (s_instance == this) s_instance = null;
        }

        private void ReadSlots()
        {
            int n = Mathf.Max(1, _kind.CountFor(_tier));
            var got = _kind.Get(_upgrade);
            if (_slots.Length != n) _slots = new string[n];
            for (int i = 0; i < n; i++) _slots[i] = i < got.Count ? got[i] : null;
        }

        // ── frame ────────────────────────────────────────────────────────────────────────────────────────────────────

        private void Update()
        {
            if (_kind == null) return;
            string why = CloseReason();
            if (why != null) { Close(why); return; }
            BagPickerView view = View;
            if (view == null || !view.Alive || _list == null || !_list.Alive) { Close("window gone"); return; }

            if (_dirty || Time.unscaledTime >= _nextRefresh) Snapshot(false);
            if (_popSlot >= _slots.Length) ClosePopover();
            if (_popSlot >= 0)
            {
                // a click outside both windows closes the list; the list stays above its picker (so Esc closes the list first)
                if ((Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)) && !_list.MouseOver() && !view.MouseOver()) ClosePopover();
                else _list.KeepAbove(view);
            }
            view.TrackPlacement();
            if (!_layoutLogged && Time.frameCount > _shownFrame + 1)
            {
                _layoutLogged = true;
                LastLayoutLine = $"[{_kind.Id}] picker layout: " + view.LayoutReport();
                _kind.Log.LogDebug(LastLayoutLine);
            }
        }

        private string CloseReason()
        {
            Player player = Player.m_localPlayer;
            if (player == null || player.IsDead()) return "no player";
            if (!InventoryGui.IsVisible()) return "inventory closed";
            bool worn = false;
            foreach (ItemDrop.ItemData b in BackpackApi.GetEquippedBackpacks(player)) if (b == _bag) { worn = true; break; }
            if (!worn) return "bag no longer worn";
            if (BackpackSpecialSlots.Read(_bag, _kind.SlotKind) != _upgrade) return "upgrade left its slot";
            return null;
        }

        // ── snapshot (polled; the view changes only when the slots or the bag counts did) ────────────────────────────

        private void Snapshot(bool force)
        {
            _dirty = false;
            _nextRefresh = Time.unscaledTime + RefreshInterval;
            ReadSlots();
            _bagCounts.Clear();
            Player player = Player.m_localPlayer;
            foreach (ItemDrop.ItemData it in BagUpgrades.ItemsInBag(player, _bag))
            {
                string prefab = it?.m_dropPrefab != null ? it.m_dropPrefab.name : null;
                if (prefab == null || !_kind.Accepts(prefab)) continue;
                _bagCounts.TryGetValue(prefab, out int c);
                _bagCounts[prefab] = c + it.m_stack;
            }
            string key = string.Join("|", _slots.Select(s => s ?? "").ToArray()) + "#" + string.Join(",", _bagCounts.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value).ToArray());
            if (!force && key == _shownKey) return;
            _shownKey = key;

            var rows = new List<SlotData>();
            for (int i = 0; i < _slots.Length; i++)
            {
                string prefab = _slots[i];
                var d = new SlotData { Prefab = prefab };
                if (prefab != null)
                {
                    d.Name = FuelPouchWindow.NameOf(prefab);
                    if (_kind.ShowBagCount)
                    {
                        _bagCounts.TryGetValue(prefab, out int c);
                        d.Sub = c > 0 ? $"{c} in this bag" : "none in this bag";
                    }
                    else d.Sub = _kind.FilledSub;
                }
                else d.Name = _kind.EmptyText;
                rows.Add(d);
            }
            View.SetData(_kind.Title, $"{_kind.Id} Bag Upgrade {FuelPouchWindow.Numeral(_tier)}", _kind.Warning, rows, _kind.Note);
            View.SetStatus(_status, _statusTone);
            View.SetOpenSlot(_popSlot);
            if (_popSlot >= 0) BuildPopover(false);   // the counts in the list follow the bag
        }

        // ── the list ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A click on a slot row: opens its list, or closes it when it is that slot's list.</summary>
        internal void OnSlotClicked(int slot)
        {
            if (_kind == null) return;
            if (slot == _popSlot) ClosePopover();
            else OpenPopover(slot);
        }

        private void OpenPopover(int slot)
        {
            if (slot < 0 || slot >= _slots.Length) return;
            _popSlot = slot;
            _search = "";
            _known = null;
            View.SetOpenSlot(slot);
            BuildPopover(true);
            _list.ShowBeside(View, slot);
        }

        private void ClosePopover()
        {
            if (_popSlot < 0) return;
            _popSlot = -1;
            View?.SetOpenSlot(-1);
            if (_list != null && _list.Alive) _list.Hide();   // its OnClosed sees no slot and does nothing more
        }

        internal void OnSearch(string text)
        {
            if (_popSlot < 0 || text == _search) return;
            _search = text ?? "";
            BuildPopover(false);
        }

        private void BuildPopover(bool first)
        {
            if (_known == null) _known = _kind.Known(Player.m_localPlayer) ?? new List<string>();
            var inBag = _bagCounts.Keys.Select(p => new KeyValuePair<string, string>(p, FuelPouchWindow.NameOf(p)))
                .OrderBy(kv => kv.Value, StringComparer.CurrentCultureIgnoreCase).ToList();
            var rest = _known.Where(p => !_bagCounts.ContainsKey(p)).Select(p => new KeyValuePair<string, string>(p, FuelPouchWindow.NameOf(p)))
                .OrderBy(kv => kv.Value, StringComparer.CurrentCultureIgnoreCase).ToList();
            bool showSearch = inBag.Count + rest.Count > SearchThreshold;
            string q = showSearch ? (_search ?? "").Trim() : "";
            bool Match(KeyValuePair<string, string> kv) => q.Length == 0 || kv.Value.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 || kv.Key.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            var bagShown = inBag.Where(Match).ToList();
            var restShown = rest.Where(Match).ToList();

            var entries = new List<Entry>();
            Entry Item(KeyValuePair<string, string> kv, bool fromBag)
            {
                string right = fromBag ? _bagCounts[kv.Key].ToString() : "";
                int other = Array.FindIndex(_slots, s => string.Equals(s, kv.Key, StringComparison.OrdinalIgnoreCase));
                if (other >= 0 && other != _popSlot) right += (right.Length > 0 ? " · " : "") + $"slot {other + 1}";
                return new Entry { Type = EntryType.Item, Prefab = kv.Key, Name = kv.Value, Right = right, Current = string.Equals(_slots[_popSlot], kv.Key, StringComparison.OrdinalIgnoreCase) };
            }
            if (bagShown.Count > 0)
            {
                entries.Add(new Entry { Type = EntryType.Header, Name = "IN THIS BAG" });
                foreach (var kv in bagShown) entries.Add(Item(kv, true));
            }
            if (restShown.Count > 0)
            {
                if (bagShown.Count > 0) entries.Add(new Entry { Type = EntryType.Separator });
                entries.Add(new Entry { Type = EntryType.Header, Name = _kind.KnownHeader });
                foreach (var kv in restShown) entries.Add(Item(kv, false));
            }
            if (entries.Count == 0)
                entries.Add(new Entry { Type = EntryType.Empty, Name = q.Length > 0 ? $"Nothing matches \"{q}\"" : _kind.NoneKnown });
            _list.SetEntries(entries, showSearch, first);
            if (first) _kind.Log.LogDebug($"[{_kind.Id}] picker list: slot {_popSlot + 1}, {inBag.Count} in this bag, {rest.Count} more known{(showSearch ? ", search shown" : "")}");
        }

        internal void OnEntryPicked(string prefab) { if (_popSlot >= 0) Pick(_popSlot, prefab); }

        // ── actions (window clicks and the test hook) ────────────────────────────────────────────────────────────────

        /// <summary>Puts the prefab in that slot (moving it when it is in another slot) and saves. False when not allowed.</summary>
        internal bool Pick(int slot, string prefab)
        {
            if (_kind == null || slot < 0 || slot >= _slots.Length || string.IsNullOrEmpty(prefab)) return false;
            if (!_kind.Accepts(prefab)) { SetStatus($"{prefab} can't go on this list", BagTone.Danger); return false; }
            var arr = (string[])_slots.Clone();
            int old = Array.FindIndex(arr, s => string.Equals(s, prefab, StringComparison.OrdinalIgnoreCase));
            string name = FuelPouchWindow.NameOf(prefab);
            if (old == slot) { ClosePopover(); return true; }
            if (old >= 0) arr[old] = null;
            arr[slot] = prefab;
            Commit(arr);
            int now = Array.FindIndex(_slots, s => string.Equals(s, prefab, StringComparison.OrdinalIgnoreCase));
            if (now < 0) { SetStatus($"{name} was not saved", BagTone.Danger); return false; }
            SetStatus(old >= 0 ? $"Moved {name} to slot {now + 1}" : $"{name} in slot {now + 1}", BagTone.Ok);
            ClosePopover();
            return true;
        }

        /// <summary>Empties that slot and saves. False when it was empty.</summary>
        internal bool Clear(int slot)
        {
            if (_kind == null || slot < 0 || slot >= _slots.Length || _slots[slot] == null) return false;
            var arr = (string[])_slots.Clone();
            string name = FuelPouchWindow.NameOf(arr[slot]);
            arr[slot] = null;
            Commit(arr);
            SetStatus($"Cleared {name}", BagTone.Muted);
            return true;
        }

        // saved in slot order; empty slots close up (the option stores an ordered list)
        private void Commit(string[] arr)
        {
            var list = arr.Where(s => s != null).ToList();
            _kind.Set(_bag, _upgrade, list, _tier);
            ReadSlots();
            _kind.Log.LogDebug($"[{_kind.Id}] picker set: [{Join(_slots)}]");
            _dirty = true;
            Snapshot(true);   // the rows (and an open list's "slot N" marks) follow at once
        }

        private void SetStatus(string text, BagTone tone)
        {
            _status = text ?? ""; _statusTone = tone;
            BagPickerView v = View;
            if (v != null && v.Alive) v.SetStatus(_status, tone);
        }

        private static string Join(string[] slots) => string.Join(", ", slots.Where(s => s != null).ToArray());
    }

    /// <summary>A row's hover / open tint (the colours are set by the view).</summary>
    internal sealed class BagRowHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        internal Image Back;
        internal Color Normal, Hover, Open;
        private bool _open, _in;
        public void OnPointerEnter(PointerEventData e) { _in = true; Apply(); }
        public void OnPointerExit(PointerEventData e) { _in = false; Apply(); }
        private void OnDisable() { _in = false; Apply(); }
        internal void SetOpen(bool open) { _open = open; Apply(); }
        internal void Apply() { if (Back != null) Back.color = _open ? Open : _in ? Hover : Normal; }
    }

    /// <summary>One picker's uGUI (Core KitWindow + KitUI): the numbered slot rows, the note and the status line.</summary>
    internal sealed class BagPickerView
    {
        private const float DefaultW = 340f, MinW = 320f, MinH = 200f;
        private const float RowH = 56f, RowPadY = 6f, ColGap = 12f, NameH = 18f, SmallH = 14f, ClearSize = 28f, StatusH = 18f, WarnH = 18f, HoverAlpha = 0.5f;
        private const string Help = "Click a slot to pick from the list beside it (what is in this bag first). × empties a slot.";

        private sealed class RowView
        {
            internal RectTransform Root;
            internal KitIconCell Cell;
            internal TextMeshProUGUI Name, Sub;
            internal KitButton Clear;
            internal BagRowHover Hover;
            internal string Prefab;
        }

        private readonly BagPickerWindow _owner;
        private readonly KitWindow _win;
        private readonly TextMeshProUGUI _warn, _note, _status;
        private readonly RectTransform _rowsRoot;
        private readonly LayoutElement _rowsSize;
        private readonly List<RowView> _rows = new List<RowView>();
        private Vector2 _placedPos, _placedSize;
        private bool _userPlaced;   // the player dragged or resized it this session: Core keeps that rect; otherwise BagWindowPlacement's rule

        internal bool Alive => _win != null && _win.Root != null;
        internal KitWindow Window => _win;

        internal BagPickerView(BagPickerWindow owner, BagPickerKind kind)
        {
            _owner = owner;
            _win = KitWindow.Create("VABackpacks.Picker." + kind.Id, kind.Title, "", new Vector2(DefaultW, MinH), new Vector2(MinW, MinH));
            _win.DefaultPlacement = BagWindowPlacement.PlaceScreen;
            _win.RememberRect = false;
            _win.OnClosed = () => _owner.OnWindowClosed(this);
            Transform body = _win.Body;
            for (int i = body.childCount - 1; i >= 0; i--) BagKitUi.Drop(body.GetChild(i).gameObject);   // a window left by an earlier view

            _warn = BagKitUi.Text(body, "Warning", "", KitUI.SmallSize, FiresTokens.Danger, TextAlignmentOptions.Center, -1f, WarnH);
            RectTransform section = KitUI.Section(body, kind.Section, Help);
            _rowsRoot = KitUI.ScrollView(section, RowH);
            ScrollRect sr = _rowsRoot.GetComponentInParent<ScrollRect>();
            _rowsSize = sr != null ? sr.GetComponent<LayoutElement>() : null;
            _note = KitUI.Note(section, "");
            _note.alignment = TextAlignmentOptions.Top;
            _status = BagKitUi.Text(body, "Status", "", KitUI.SmallSize, FiresTokens.Ok, TextAlignmentOptions.Center, -1f, StatusH);
        }

        internal void SetData(string title, string subtitle, string warning, List<BagPickerWindow.SlotData> rows, string note)
        {
            _win.SetTitle(title);
            _win.SetSubtitle(subtitle);
            _warn.text = warning ?? "";
            _warn.gameObject.SetActive(!string.IsNullOrEmpty(warning));
            _note.text = note ?? "";
            if (rows.Count != _rows.Count) BuildRows(rows.Count);
            for (int i = 0; i < rows.Count; i++) UpdateRow(_rows[i], rows[i]);
            if (_rowsSize != null) _rowsSize.preferredHeight = rows.Count * RowH;   // grows to fit; scrolls only when the screen is too short
            if (_win.IsShowing) Fit();
        }

        private void BuildRows(int count)
        {
            foreach (RowView r in _rows) BagKitUi.Drop(r.Root != null ? r.Root.gameObject : null);
            _rows.Clear();
            for (int i = 0; i < count; i++)
            {
                int slot = i;
                var v = new RowView();
                v.Root = BagKitUi.HRow(_rowsRoot, "Slot_" + (i + 1), ColGap, TextAnchor.MiddleLeft, RowPadY);
                BagKitUi.Size(v.Root.gameObject, -1f, RowH);
                var back = v.Root.gameObject.AddComponent<Image>();
                KitUI.SetPlate(back, KitUI.Plate(FiresTokens.RadiusSmall, Color.white, null));
                var b = v.Root.gameObject.AddComponent<UnityEngine.UI.Button>();
                b.targetGraphic = back; b.transition = Selectable.Transition.None;
                b.onClick.AddListener(() => _owner.OnSlotClicked(slot));
                v.Hover = v.Root.gameObject.AddComponent<BagRowHover>();
                v.Hover.Back = back;
                v.Hover.Normal = Color.clear;   // clear keeps the row clickable
                v.Hover.Hover = FiresTokens.WithAlpha(FiresTokens.Selected, HoverAlpha);
                v.Hover.Open = FiresTokens.Selected;
                v.Hover.Apply();

                v.Cell = KitUI.IconCell(v.Root, null, (i + 1).ToString(), true);
                RectTransform col = BagKitUi.Column(v.Root, "Text", 1f);
                BagKitUi.Flexible(col.gameObject);
                col.GetComponent<VerticalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
                v.Name = BagKitUi.Text(col, "Name", "", KitUI.BodySize, FiresTokens.Text, TextAlignmentOptions.MidlineLeft, -1f, NameH);
                v.Sub = BagKitUi.Text(col, "Sub", "", KitUI.NoteSize, FiresTokens.TextFaint, TextAlignmentOptions.MidlineLeft, -1f, SmallH);
                v.Clear = KitUI.Button(v.Root, "×", false, () => _owner.Clear(slot));
                BagKitUi.Size(v.Clear.Button.gameObject, ClearSize, ClearSize);
                _rows.Add(v);
            }
        }

        private static void UpdateRow(RowView v, BagPickerWindow.SlotData d)
        {
            bool filled = d.Prefab != null;
            if (v.Prefab != d.Prefab || (filled && v.Cell.Icon != null && v.Cell.Icon.sprite == null))
            {
                v.Prefab = d.Prefab;
                v.Cell.SetIcon(filled ? FuelPouchWindow.IconOf(d.Prefab) : null);
                v.Cell.SetDashed(!filled);   // an empty slot is dashed
            }
            v.Name.text = d.Name ?? "";
            v.Name.color = filled ? FiresTokens.Text : FiresTokens.TextFaint;
            v.Sub.text = d.Sub ?? "";
            v.Sub.gameObject.SetActive(!string.IsNullOrEmpty(d.Sub));
            v.Clear.Button.gameObject.SetActive(filled);
        }

        internal void SetOpenSlot(int slot)
        {
            for (int i = 0; i < _rows.Count; i++) _rows[i].Hover.SetOpen(i == slot);
        }

        internal RectTransform RowRect(int slot) => slot >= 0 && slot < _rows.Count ? _rows[slot].Root : null;

        internal void SetStatus(string text, BagTone tone)
        {
            _status.text = text ?? "";
            _status.color = BagKitUi.ToneColour(tone);
        }

        internal void Open()
        {
            _win.RememberRect = _userPlaced;
            _win.Show();
            if (!_userPlaced) { _win.SetDefaultSize(new Vector2(DefaultW, BagKitUi.FitHeight(_win))); _win.Show(); }   // placed at its real size
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
            if (!_userPlaced) { _placedPos = _win.Root.anchoredPosition; _placedSize = _win.Root.sizeDelta; }
        }

        internal void TrackPlacement()
        {
            if (_userPlaced || !Input.GetMouseButton(0)) return;
            if (_win.Root.anchoredPosition != _placedPos || _win.Root.sizeDelta != _placedSize) { _userPlaced = true; _win.RememberRect = true; }
        }

        internal bool MouseOver() => _win.IsShowing && RectTransformUtility.RectangleContainsScreenPoint(_win.Root, Input.mousePosition, null);

        internal string LayoutReport() => _win.LayoutReport();
    }

    /// <summary>The list beside a picker slot: a chrome-less KitWindow (no title, close or grip; Esc and the top-window order are Core's).</summary>
    internal sealed class BagPickerListView
    {
        private const string WindowKey = "VABackpacks.PickerList";
        private const float PopW = 240f, PopPad = 6f, PopGap = 8f, MinH = 80f;
        private const float EntryH = 26f, HeaderH = 22f, SepH = 9f, EntryIcon = 20f, EntryInsetX = 8f, RightW = 70f, HoverAlpha = 0.5f;
        private const int MaxVisibleEntries = 12;
        private const float RefH = 1080f;

        private readonly BagPickerWindow _owner;
        private readonly KitWindow _win;
        private readonly KitTextField _field;
        private readonly RectTransform _content;
        private readonly LayoutElement _listSize;
        private BagPickerView _beside;
        private int _besideSlot = -1;
        private bool _setting;

        internal bool Alive => _win != null && _win.Root != null;

        internal BagPickerListView(BagPickerWindow owner)
        {
            _owner = owner;
            _win = KitWindow.Create(WindowKey, "", "", new Vector2(PopW, MinH), new Vector2(PopW, MinH));
            _win.RememberRect = false;
            _win.DefaultPlacement = Place;
            _win.OnClosed = () => _owner.OnListClosed();

            // the menu look: no title bar, no grip, the Well plate, 6 px padding
            Transform title = _win.Root.Find("TitleBar"), grip = _win.Root.Find("ResizeGrip");
            if (title != null) title.gameObject.SetActive(false);
            if (grip != null) grip.gameObject.SetActive(false);
            _win.Body.offsetMin = Vector2.zero; _win.Body.offsetMax = Vector2.zero;
            var vlg = _win.Body.GetComponent<VerticalLayoutGroup>();
            if (vlg != null) { int p = Mathf.RoundToInt(PopPad); vlg.padding = new RectOffset(p, p, p, p); vlg.spacing = PopPad; }
            var plate = _win.Root.GetComponent<Image>();
            if (plate != null) KitUI.SetPlate(plate, KitUI.Plate(FiresTokens.Radius, FiresTokens.Well, FiresTokens.Edge));
            Transform body = _win.Body;
            for (int i = body.childCount - 1; i >= 0; i--) BagKitUi.Drop(body.GetChild(i).gameObject);

            _field = KitUI.TextField(body, "Search…", null);
            _field.Input.onValueChanged.AddListener(s => { if (!_setting) _owner.OnSearch(s); });
            _content = KitUI.ScrollView(body, EntryH);
            ScrollRect sr = _content.GetComponentInParent<ScrollRect>();
            _listSize = sr != null ? sr.GetComponent<LayoutElement>() : null;
        }

        internal void SetEntries(List<BagPickerWindow.Entry> entries, bool showSearch, bool first)
        {
            if (first)
            {
                _setting = true;
                try { _field.SetText(""); } finally { _setting = false; }
            }
            _field.Input.gameObject.SetActive(showSearch);
            for (int i = _content.childCount - 1; i >= 0; i--) BagKitUi.Drop(_content.GetChild(i).gameObject);
            float h = 0f;
            foreach (BagPickerWindow.Entry e in entries) h += AddEntry(e);
            float cap = MaxVisibleEntries * EntryH + HeaderH * 2f + SepH;
            if (_listSize != null) { _listSize.preferredHeight = Mathf.Min(h, cap); _listSize.minHeight = Mathf.Min(h, EntryH); }
            if (_win.IsShowing) Resize();
        }

        private float AddEntry(BagPickerWindow.Entry e)
        {
            switch (e.Type)
            {
                case BagPickerWindow.EntryType.Separator:
                {
                    RectTransform sep = BagKitUi.NewRect(_content, "Separator");
                    BagKitUi.Size(sep.gameObject, -1f, SepH);
                    RectTransform line = BagKitUi.NewRect(sep, "Line");
                    line.anchorMin = new Vector2(0f, 0.5f); line.anchorMax = new Vector2(1f, 0.5f);
                    line.offsetMin = new Vector2(2f, -0.5f); line.offsetMax = new Vector2(-2f, 0.5f);
                    var li = line.gameObject.AddComponent<Image>();
                    li.color = FiresTokens.Hairline; li.raycastTarget = false;
                    return SepH;
                }
                case BagPickerWindow.EntryType.Header:
                case BagPickerWindow.EntryType.Empty:
                {
                    float h = e.Type == BagPickerWindow.EntryType.Header ? HeaderH : EntryH;
                    RectTransform row = BagKitUi.HRow(_content, e.Type.ToString(), 0f, TextAnchor.MiddleLeft, 0f);
                    row.GetComponent<HorizontalLayoutGroup>().padding = new RectOffset(Mathf.RoundToInt(EntryInsetX), 0, 0, 0);
                    BagKitUi.Text(row, "Text", e.Name ?? "", KitUI.NoteSize, FiresTokens.TextFaint, TextAlignmentOptions.MidlineLeft, -1f, h);
                    return h;
                }
                default:
                {
                    RectTransform row = BagKitUi.HRow(_content, "Item_" + e.Prefab, 8f, TextAnchor.MiddleLeft, 0f);
                    row.GetComponent<HorizontalLayoutGroup>().padding = new RectOffset(Mathf.RoundToInt(EntryInsetX), Mathf.RoundToInt(EntryInsetX), 0, 0);
                    BagKitUi.Size(row.gameObject, -1f, EntryH);
                    var back = row.gameObject.AddComponent<Image>();
                    KitUI.SetPlate(back, KitUI.Plate(FiresTokens.RadiusSmall, Color.white, null));
                    var b = row.gameObject.AddComponent<UnityEngine.UI.Button>();
                    b.targetGraphic = back; b.transition = Selectable.Transition.None;
                    string prefab = e.Prefab;
                    b.onClick.AddListener(() => _owner.OnEntryPicked(prefab));
                    var hover = row.gameObject.AddComponent<BagRowHover>();
                    hover.Back = back;
                    hover.Normal = Color.clear;
                    hover.Hover = FiresTokens.WithAlpha(FiresTokens.Selected, HoverAlpha);
                    hover.Open = FiresTokens.Selected;
                    hover.SetOpen(e.Current);

                    RectTransform ic = BagKitUi.NewRect(row, "Icon");
                    BagKitUi.Size(ic.gameObject, EntryIcon, EntryIcon);
                    var ii = ic.gameObject.AddComponent<Image>();
                    ii.preserveAspect = true; ii.raycastTarget = false;
                    ii.sprite = FuelPouchWindow.IconOf(e.Prefab);
                    ii.enabled = ii.sprite != null;
                    BagKitUi.Text(row, "Name", e.Name ?? "", KitUI.BodySize, e.Current ? FiresTokens.Gold : FiresTokens.Text, TextAlignmentOptions.MidlineLeft, -1f, EntryH);
                    if (!string.IsNullOrEmpty(e.Right))
                        BagKitUi.Text(row, "Right", e.Right, KitUI.SmallSize, FiresTokens.TextFaint, TextAlignmentOptions.MidlineRight, RightW, EntryH);
                    return EntryH;
                }
            }
        }

        internal void ShowBeside(BagPickerView picker, int slot)
        {
            _beside = picker; _besideSlot = slot;
            bool focus = _field.Input.gameObject.activeSelf;
            _win.Show();   // not remembered: placed beside the slot every time
            Resize();
            _win.Show();   // again at its real size, so the placement fits it
            if (focus) { _field.Input.Select(); _field.Input.ActivateInputField(); }
        }

        internal void Hide() { if (Alive) _win.Hide(); }

        // keeps its top edge; Core keeps it on screen
        private void Resize()
        {
            float need = Mathf.Max(MinH, BagKitUi.FitHeight(_win));
            _win.SetDefaultSize(new Vector2(PopW, need));
            Vector2 s = _win.Root.sizeDelta;
            if (Mathf.Abs(s.y - need) > 0.5f) { _win.Root.sizeDelta = new Vector2(PopW, need); _win.Clamp(); }
        }

        internal void KeepAbove(BagPickerView picker)
        {
            if (_win.IsShowing && KitWindow.Top == picker.Window) _win.BringToFront();
        }

        internal bool MouseOver() => _win.IsShowing && RectTransformUtility.RectangleContainsScreenPoint(_win.Root, Input.mousePosition, null);

        // beside the picker (its right, else its left), the top level with the slot; always on screen. Screen px, bottom-left origin.
        private Rect Place(Vector2 sizePx)
        {
            float s = Mathf.Max(0.01f, Screen.height / RefH);
            float sw = Screen.width, sh = Screen.height;
            float w = Mathf.Min(sizePx.x, sw), h = Mathf.Min(sizePx.y, sh);
            if (_beside == null || !_beside.Alive) return new Rect((sw - w) * 0.5f, (sh - h) * 0.5f, w, h);
            Rect win = KitUI.DrawnRect(_beside.Window.Root);
            RectTransform row = _beside.RowRect(_besideSlot);
            float top = row != null ? KitUI.DrawnRect(row).yMax + PopPad * s : win.yMax;
            float x = win.xMax + PopGap * s;
            if (x + w > sw) x = win.xMin - PopGap * s - w;
            x = Mathf.Clamp(x, 0f, Mathf.Max(0f, sw - w));
            float y = Mathf.Clamp(top - h, 0f, Mathf.Max(0f, sh - h));
            return new Rect(x, y, w, h);
        }
    }

    /// <summary>Drives the picker window without OS input. All members are safe when the window is closed.</summary>
    internal static class BagPickerWindowTest
    {
        private static BagPickerWindow W => BagPickerWindow.Instance;

        internal static bool IsShowing => W != null && W.IsShowing;
        /// <summary>"Feeding" / "Void": which picker is showing, or null.</summary>
        internal static string Kind => W != null && W.IsShowing ? W.KindId : null;
        /// <summary>The slots in order (null = empty), one per the tier's count; empty when closed.</summary>
        internal static string[] Slots => W != null ? W.SlotsCopy : new string[0];
        internal static string Status => W?.Status;
        internal static string LastLayoutLine => W?.LastLayoutLine;

        /// <summary>Puts the prefab in that slot (0-based) as a click in the list would. False when closed, out of range, or not allowed.</summary>
        internal static bool Pick(int slot, string prefab) => W != null && W.IsShowing && W.Pick(slot, prefab);

        /// <summary>Presses that slot's ×. False when closed or the slot is empty.</summary>
        internal static bool Clear(int slot) => W != null && W.IsShowing && W.Clear(slot);

        internal static void Close() => W?.Close("test");
    }
}
