// BackpackSlotRail — 1.0.20 (Fire 10-07, [classes-ui] layout A): the special-slot rail in the vanilla container view
// (legacy storage mode; with VAInventory loaded its own bag view draws the rail through BackpackApi).
// The bag grid never moves. When the open container is the worn bag and the bag has special slots, a narrow column
// appears right of the grid behind a thin line: tool, resources, bypass, top-down. Each slot is a clone of the
// container grid showing that slot's 1x1 inventory, so vanilla drag / drop / split / drop-on-ground all work; the
// fit rule is enforced in the OnSelectedItem prefix below.

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    public static class BackpackSlotRail
    {
        private sealed class Slot
        {
            public InventoryGrid Grid;
            public string Kind;
            public ItemDrop.ItemData Bag;
        }

        private static readonly List<Slot> Slots = new List<Slot>();
        private static Image _line;
        private static InventoryGui _builtFor;

        private static readonly AccessTools.FieldRef<InventoryGui, Container> CurrentContainer = AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer");
        private static readonly AccessTools.FieldRef<InventoryGui, InventoryGrid> ContainerGrid = AccessTools.FieldRefAccess<InventoryGui, InventoryGrid>("m_containerGrid");
        private static readonly AccessTools.FieldRef<InventoryGui, ItemDrop.ItemData> DragItem = AccessTools.FieldRefAccess<InventoryGui, ItemDrop.ItemData>("m_dragItem");
        private static readonly AccessTools.FieldRef<InventoryGui, Inventory> DragInventory = AccessTools.FieldRefAccess<InventoryGui, Inventory>("m_dragInventory");
        private static readonly AccessTools.FieldRef<InventoryGui, GameObject> DragGo = AccessTools.FieldRefAccess<InventoryGui, GameObject>("m_dragGo");
        private static readonly AccessTools.FieldRef<InventoryGrid, List<InventoryElement>> Elements = AccessTools.FieldRefAccess<InventoryGrid, List<InventoryElement>>("m_elements");

        private static Slot SlotFor(InventoryGrid grid)
        {
            foreach (Slot s in Slots) if (s.Grid == grid) return s;
            return null;
        }

        private static Slot SlotForInventory(Inventory inv)
        {
            if (inv == null) return null;
            foreach (Slot s in Slots) if (s.Grid != null && s.Grid.gameObject.activeSelf && s.Grid.GetInventory() == inv) return s;
            return null;
        }

        // ── build ──────────────────────────────────────────────────────────────────────────────────

        private static void Build(InventoryGui gui)
        {
            Clear();
            InventoryGrid src = ContainerGrid(gui);
            if (src == null) return;
            var onSelected = (Action<InventoryGrid, ItemDrop.ItemData, Vector2i, InventoryGrid.Modifier>)Delegate.CreateDelegate(
                typeof(Action<InventoryGrid, ItemDrop.ItemData, Vector2i, InventoryGrid.Modifier>), gui, AccessTools.Method(typeof(InventoryGui), "OnSelectedItem"));
            var onRight = (Action<InventoryGrid, ItemDrop.ItemData, Vector2i>)Delegate.CreateDelegate(
                typeof(Action<InventoryGrid, ItemDrop.ItemData, Vector2i>), gui, AccessTools.Method(typeof(InventoryGui), "OnRightClickItem"));

            for (int i = 0; i < BackpackSpecialSlots.Kinds.Length; i++)
            {
                GameObject go = UnityEngine.Object.Instantiate(src.gameObject, gui.m_container, false);
                go.name = "VABSlot_" + BackpackSpecialSlots.Kinds[i];
                InventoryGrid grid = go.GetComponent<InventoryGrid>();
                for (int c = grid.m_gridRoot.childCount - 1; c >= 0; c--) UnityEngine.Object.Destroy(grid.m_gridRoot.GetChild(c).gameObject);
                foreach (ScrollRect sr in go.GetComponentsInChildren<ScrollRect>(true)) sr.enabled = false;
                if (grid.m_scrollbar != null) grid.m_scrollbar.gameObject.SetActive(false);
                foreach (Image img in go.GetComponents<Image>()) img.enabled = false;   // the grid panel's own background
                var rt = (RectTransform)go.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(src.m_elementSpace, src.m_elementSpace);
                grid.m_onSelected = onSelected;
                grid.m_onRightClick = onRight;
                grid.m_onReleased = null;
                grid.m_onEnter = null;
                grid.m_uiGroup = src.m_uiGroup;
                go.SetActive(false);
                Slots.Add(new Slot { Grid = grid, Kind = BackpackSpecialSlots.Kinds[i] });
            }

            var lineGo = new GameObject("VABSlotRailLine", typeof(RectTransform), typeof(Image));
            lineGo.transform.SetParent(gui.m_container, false);
            _line = lineGo.GetComponent<Image>();
            _line.color = new Color(1f, 1f, 1f, 0.25f);
            _line.raycastTarget = false;
            lineGo.SetActive(false);
            _builtFor = gui;
        }

        private static void Clear()
        {
            foreach (Slot s in Slots) if (s.Grid != null) UnityEngine.Object.Destroy(s.Grid.gameObject);
            Slots.Clear();
            if (_line != null) UnityEngine.Object.Destroy(_line.gameObject);
            _line = null;
            _builtFor = null;
        }

        private static void Hide()
        {
            foreach (Slot s in Slots) { if (s.Grid != null && s.Grid.gameObject.activeSelf) s.Grid.gameObject.SetActive(false); s.Bag = null; }
            if (_line != null && _line.gameObject.activeSelf) _line.gameObject.SetActive(false);
        }

        // ── per frame ──────────────────────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(InventoryGui), "UpdateContainer")]
        public static class UpdateContainer_Patch
        {
            public static void Postfix(InventoryGui __instance, Player player)
            {
                try { Update(__instance, player); }
                catch (Exception ex) { Hide(); Debug.LogWarning($"{MyMod.PluginName}: slot rail: {ex.Message}"); }
            }
        }

        private static void Update(InventoryGui gui, Player player)
        {
            Container open = CurrentContainer(gui);
            ItemDrop.ItemData bag = null;
            if (BackpackManager.LegacyMode && player != null && open != null && gui.m_container.gameObject.activeInHierarchy
                && open == player.GetComponent<Container>())
                bag = BackpackManager.FindEquippedBackpackPublic(player);
            string[] kinds = BackpackSpecialSlots.GetSpecialSlots(bag);
            if (kinds.Length == 0) { Hide(); return; }
            if (_builtFor != gui || Slots.Count == 0 || Slots[0].Grid == null) Build(gui);
            if (Slots.Count == 0) return;

            InventoryGrid main = ContainerGrid(gui);
            List<InventoryElement> mainEls = Elements(main);
            Inventory mainInv = main.GetInventory();
            if (mainEls == null || mainEls.Count == 0 || mainInv == null) { Hide(); return; }
            int width = mainInv.GetWidth();
            RectTransform topRight = (RectTransform)mainEls[Mathf.Clamp(width - 1, 0, mainEls.Count - 1)].transform;
            float step = main.m_elementSpace * main.m_gridRoot.lossyScale.x;
            Vector3 right = main.m_gridRoot.right, down = -main.m_gridRoot.up;
            Vector3 railTop = topRight.position + right * (step * 1.25f);

            ItemDrop.ItemData drag = DragItem(gui);
            for (int i = 0; i < Slots.Count; i++)
            {
                Slot s = Slots[i];
                if (i >= kinds.Length) { if (s.Grid.gameObject.activeSelf) s.Grid.gameObject.SetActive(false); s.Bag = null; continue; }
                s.Kind = kinds[i];
                s.Bag = bag;
                if (!s.Grid.gameObject.activeSelf) s.Grid.gameObject.SetActive(true);
                s.Grid.UpdateInventory(BackpackSpecialSlots.GetInventory(bag, s.Kind), null, drag);
                List<InventoryElement> els = Elements(s.Grid);
                if (els == null || els.Count == 0) continue;
                InventoryElement el = els[0];
                Vector3 want = railTop + down * (step * i);
                s.Grid.transform.position += want - el.transform.position;
                Decorate(el, s.Kind);
            }

            RectTransform line = (RectTransform)_line.transform;
            if (!_line.gameObject.activeSelf) _line.gameObject.SetActive(true);
            line.sizeDelta = new Vector2(2f, main.m_elementSpace * kinds.Length);
            line.pivot = new Vector2(0.5f, 1f);
            line.position = topRight.position + right * (step * 0.625f) - down * (step * 0.5f);
            line.SetAsLastSibling();
        }

        private static void Decorate(InventoryElement el, string kind)
        {
            Color c = BackpackSpecialSlots.Colors[kind];
            Image frame = el.GetComponent<Image>();
            if (frame != null) frame.color = new Color(c.r, c.g, c.b, 0.85f);
            Transform icon = el.transform.Find("vab_slot_icon");
            if (icon == null)
            {
                var go = new GameObject("vab_slot_icon", typeof(RectTransform), typeof(Image));
                go.transform.SetParent(el.transform, false);
                go.transform.SetSiblingIndex(0);
                var rt = (RectTransform)go.transform;
                rt.anchorMin = new Vector2(0.15f, 0.15f);
                rt.anchorMax = new Vector2(0.85f, 0.85f);
                rt.offsetMin = rt.offsetMax = Vector2.zero;
                var img = go.GetComponent<Image>();
                img.raycastTarget = false;
                img.preserveAspect = true;
                icon = go.transform;
            }
            var iconImg = icon.GetComponent<Image>();
            iconImg.sprite = BackpackSpecialSlots.GetIcon(kind);
            iconImg.color = new Color(c.r, c.g, c.b, 0.45f);
            bool empty = !el.m_used;
            iconImg.enabled = empty && iconImg.sprite != null;
            if (empty && el.m_tooltip != null)
            {
                el.m_tooltip.m_topic = "";
                el.m_tooltip.m_text = BackpackSpecialSlots.Tooltips[kind];
            }
        }

        // ── input rules ────────────────────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(InventoryGui), "OnSelectedItem")]
        [HarmonyPriority(Priority.High)]
        public static class OnSelectedItem_Patch
        {
            public static bool Prefix(InventoryGui __instance, InventoryGrid grid, ItemDrop.ItemData item, Vector2i pos, InventoryGrid.Modifier mod)
            {
                if (Slots.Count == 0) return true;
                Player player = Player.m_localPlayer;
                if (player == null) return true;
                Slot target = SlotFor(grid);
                bool dragging = DragGo(__instance) != null;
                ItemDrop.ItemData drag = DragItem(__instance);

                if (dragging && drag != null)
                {
                    // Into a rail slot: the dragged item must fit.
                    if (target != null && target.Bag != null && !BackpackSpecialSlots.Accepts(target.Bag, target.Kind, drag))
                    {
                        player.Message(MessageHud.MessageType.Center, "That doesn't go in the " + target.Kind + " slot");
                        return false;
                    }
                    // Out of a rail slot onto an occupied cell: the item swapped back must fit the slot too.
                    Slot from = SlotForInventory(DragInventory(__instance));
                    if (from != null && from.Bag != null && target == null)
                    {
                        ItemDrop.ItemData there = grid.GetInventory()?.GetItemAt(pos.x, pos.y);
                        if (there != null && there != drag && !BackpackSpecialSlots.Accepts(from.Bag, from.Kind, there))
                        {
                            player.Message(MessageHud.MessageType.Center, "That doesn't go in the " + from.Kind + " slot");
                            return false;
                        }
                    }
                    return true;
                }

                // Shift-click on a rail item moves it to the player inventory.
                if (target != null && item != null && mod == InventoryGrid.Modifier.Move)
                {
                    player.GetInventory().MoveItemToThis(grid.GetInventory(), item);
                    return false;
                }
                return true;
            }
        }

        // Rail items are outside the player inventory, so they can't be used / equipped from there.
        [HarmonyPatch(typeof(InventoryGui), "OnRightClickItem")]
        public static class OnRightClickItem_Patch
        {
            public static bool Prefix(InventoryGrid grid) => Slots.Count == 0 || SlotFor(grid) == null;
        }
    }
}
