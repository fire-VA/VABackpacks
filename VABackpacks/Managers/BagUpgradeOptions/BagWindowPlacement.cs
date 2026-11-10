// BagWindowPlacement: the one placement rule for the bag windows (fuel pouch, pickers). By default a window opens directly beside
// the bag/character panel: its left edge a small gap right of the panel's right edge, top-aligned with it; when there's no room on
// the right it goes on the panel's left; always clamped on screen. The panel is VAInventory's CharacterPanel (the panel that holds
// the bag and its upgrade rail) when present, else vanilla's InventoryGui.m_player.
// Works in SCREEN pixels (bottom-left origin, as Input.mousePosition); the windows pass PlaceScreen as their KitWindow.DefaultPlacement.
// Call it from Update or a show (it reads the live UI).

using UnityEngine;

namespace VerdantsAscent.Managers
{
    internal static class BagWindowPlacement
    {
        private const float GapPx = 8f;                                  // the window's edge sits this far from the panel's edge (screen px)
        private const string CharacterPanelName = "CharacterPanel";      // VAInventory's bag/character panel (Unified and Panels layouts)
        private const string RailName = "VAInventoryBagSpecialRail";     // VAInventory's upgrade rail; its CharacterPanel ancestor wins
        private const float RefindSeconds = 1f;                          // the hierarchy search is throttled; a cached panel is reused

        private static RectTransform s_panel;
        private static float s_nextFind;
        private static readonly Vector3[] s_corners = new Vector3[4];

        /// <summary>The bag/character panel's rect in screen pixels (bottom-left origin); false when no inventory panel is up.</summary>
        internal static bool TryPanelScreenRect(out Rect screen)
        {
            screen = default;
            RectTransform panel = Panel();
            if (panel == null) return false;
            panel.GetWorldCorners(s_corners);
            Canvas canvas = panel.GetComponentInParent<Canvas>();
            Camera cam = canvas != null && canvas.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.rootCanvas.worldCamera : null;
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, s_corners[0]);   // bottom-left
            Vector2 b = RectTransformUtility.WorldToScreenPoint(cam, s_corners[2]);   // top-right
            screen = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            return screen.width > 0f && screen.height > 0f;
        }

        /// <summary>
        /// Where a window of <paramref name="sizePx"/> (screen px) goes, in screen pixels with a bottom-left origin (rect.y = its bottom).
        /// Without a panel: centred on screen.
        /// </summary>
        internal static Rect PlaceScreen(Vector2 sizePx)
        {
            float sw = Screen.width, sh = Screen.height;
            float w = Mathf.Min(sizePx.x, sw), h = Mathf.Min(sizePx.y, sh);
            float x, top;
            if (TryPanelScreenRect(out Rect p))
            {
                x = p.xMax + GapPx;
                if (x + w > sw && p.xMin - GapPx - w >= 0f) x = p.xMin - GapPx - w;   // no room on the right: the panel's left
                top = p.yMax;
            }
            else { x = (sw - w) * 0.5f; top = (sh + h) * 0.5f; }
            x = Mathf.Clamp(x, 0f, sw - w);
            float y = Mathf.Clamp(top - h, 0f, sh - h);
            return new Rect(x, y, w, h);
        }

        private static RectTransform Panel()
        {
            if (s_panel != null && s_panel.gameObject.activeInHierarchy) return s_panel;
            if (Time.unscaledTime < s_nextFind) return VanillaPanel();   // the cached panel is gone or hidden: vanilla until the next search
            s_nextFind = Time.unscaledTime + RefindSeconds;
            s_panel = FindCharacterPanel();
            return s_panel != null ? s_panel : VanillaPanel();
        }

        private static RectTransform VanillaPanel()
        {
            RectTransform p = InventoryGui.instance != null ? InventoryGui.instance.m_player : null;
            return p != null && p.gameObject.activeInHierarchy ? p : null;
        }

        private static RectTransform FindCharacterPanel()
        {
            Transform root = InventoryGui.instance != null ? InventoryGui.instance.m_inventoryRoot : null;
            if (root == null) return null;
            RectTransform any = null;
            foreach (RectTransform t in root.GetComponentsInChildren<RectTransform>(false))
            {
                if (t.name == RailName)
                    for (Transform up = t.parent; up != null && up != root; up = up.parent)
                        if (up.name == CharacterPanelName) return (RectTransform)up;
                if (any == null && t.name == CharacterPanelName) any = t;
            }
            return any;
        }
    }
}
