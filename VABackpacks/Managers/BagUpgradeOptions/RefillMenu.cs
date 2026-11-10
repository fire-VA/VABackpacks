// RefillMenu: the "Mark for autofill" cell menu and the autofill badge, the UI over RefillOption's marks. The inventory integrator calls
// it: Open on a right-click on a bag cell, StateOf + EnsureBadge (uGUI) or DrawBadge (IMGUI) per cell, BadgeTooltip on hover.
// Core (FiresUnifiedCore) is a SOFT dependency: every Core type is touched only from NoInlining methods that run after the Core check,
// so VABackpacks still loads (and Open just returns false) without it.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using FiresCore.UI.ContextMenu;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    public static class RefillMenu
    {
        public enum AutofillState { None, On, OutOfRange }

        private const string CoreGuid = "com.Fire.FiresUnifiedCore";
        private const string BadgeName = "AutofillBadge";
        private const string MenuGlyph = "↻";          // the circular arrow of the mockup; only shown when the menu font has it
        private const float BadgeSize = 16f;
        private const float BadgeInset = 2f;                // from the cell's top-right corner
        private const float OutOfRangeAlpha = 0.4f;
        private const float ReachCacheSeconds = 1f;         // the home distance is a reflection call; the badge asks per cell per frame
        private const int BadgeTexSize = 32;                // drawn at 2x for a clean edge on high-DPI HUD scales
        private static readonly Color BadgeFill = new Color(0.08f, 0.06f, 0.04f, 0.92f);
        private static readonly Color OkFallback = new Color(0.435f, 0.816f, 0.478f, 1f);   // #6FD07A when Core's token isn't there

        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks.Refill");
        private static bool? s_coreLoaded, s_glyphInFont;
        private static Color? s_ok;
        private static Texture2D s_badgeTex;
        private static Sprite s_badgeSprite;

        // per-frame / per-second caches for StateOf
        private static int s_marksFrame = -1;
        private static Player s_marksPlayer;
        private static Dictionary<Vector2i, int> s_marks = new Dictionary<Vector2i, int>();
        private static float s_reachUntil;
        private static Player s_reachPlayer;
        private static bool s_inReach;

        // ---- the menu ----

        /// <summary>True when the worn bag has a Refill upgrade and the cell holds an item in that bag (not equipped, not the hotbar row).</summary>
        public static bool HasRows(Player p, ItemDrop.ItemData cell)
        {
            if (p == null || cell == null || cell.m_equipped || cell.m_dropPrefab == null) return false;
            ItemDrop.ItemData bag = BagUpgrades.WornBagWith(p, RefillOption.UpgradeId);
            if (bag == null) return false;
            // With VAInventory the bag's cells live in the player inventory, whose row 0 is the hotbar.
            if (BagUpgrades.BagCellsProvider != null && cell.m_gridPos.y == 0) return false;
            foreach (var it in BagUpgrades.ItemsInBag(p, bag)) if (ReferenceEquals(it, cell)) return true;
            return false;
        }

        /// <summary>Opens Core's context menu for the cell. False when Core isn't loaded or the cell has no rows (show nothing then).</summary>
        public static bool Open(Player p, ItemDrop.ItemData cell, Vector2 screenPos)
        {
            if (!CoreLoaded() || !HasRows(p, cell)) return false;
            try { return OpenCore(p, cell, screenPos); }
            catch (Exception e) { Log.LogWarning($"[Refill] menu failed to open: {(e.InnerException ?? e).Message}"); return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool OpenCore(Player p, ItemDrop.ItemData cell, Vector2 screenPos)
        {
            string glyph = GlyphPrefix();
            var rows = new List<ContextMenuItem>();
            if (RefillOption.IsMarked(p, cell))
            {
                rows.Add(ContextMenuItem.Row(glyph + "Stop autofill", () =>
                {
                    RefillOption.UnmarkCell(p, cell);
                    InvalidateMarks();
                    Log.LogDebug($"[Refill] menu: unmarked {cell.m_dropPrefab?.name} at ({cell.m_gridPos.x}, {cell.m_gridPos.y})");
                }));
            }
            else
            {
                int count = RefillOption.MarkCount(p), max = RefillOption.WornMaxMarks(p);
                bool full = count >= max;
                rows.Add(ContextMenuItem.Row($"{glyph}Mark for autofill (keeps {KeepCount(cell)})", () =>
                {
                    if (RefillOption.MarkCell(p, cell))
                    {
                        InvalidateMarks();
                        Log.LogDebug($"[Refill] menu: marked {cell.m_dropPrefab?.name} at ({cell.m_gridPos.x}, {cell.m_gridPos.y})");
                    }
                }, !full));
                if (full) rows.Add(ContextMenuItem.Row($"Refill full: {count} / {max} cells marked", null, false));
            }
            string reach = OutOfReachLine(p);
            if (reach != null) { rows.Add(ContextMenuItem.Sep()); rows.Add(ContextMenuItem.Row(reach, null, false)); }
            FiresContextMenu.OpenCustom(ItemName(cell), rows, screenPos);
            return true;
        }

        // ---- the badge ----

        /// <summary>The badge state of a cell; cheap (marks cached per frame, the home reach per second).</summary>
        public static AutofillState StateOf(Player p, ItemDrop.ItemData cell)
        {
            if (p == null || cell == null) return AutofillState.None;
            if (s_marksFrame != Time.frameCount || !ReferenceEquals(s_marksPlayer, p))
            {
                s_marksFrame = Time.frameCount; s_marksPlayer = p;
                s_marks = BagUpgrades.WornTier(p, RefillOption.UpgradeId) > 0 ? RefillOption.MarkedCells(p) : new Dictionary<Vector2i, int>();
            }
            if (s_marks.Count == 0 || !s_marks.ContainsKey(cell.m_gridPos)) return AutofillState.None;
            return InReachCached(p) ? AutofillState.On : AutofillState.OutOfRange;
        }

        /// <summary>The hover text of a marked cell, null when the cell isn't marked.</summary>
        public static string BadgeTooltip(Player p, ItemDrop.ItemData cell)
        {
            if (p == null || cell == null || !RefillOption.TryGetTarget(p, cell, out int target)) return null;
            string text = $"Autofill: keeps {target} {ItemName(cell)} here, from your home chests.";
            string reach = OutOfReachLine(p);
            return reach != null ? text + "\n" + reach + "." : text;
        }

        /// <summary>IMGUI badge at the top-right of a cell rect (GUI coordinates). Call from OnGUI.</summary>
        public static void DrawBadge(Rect cellScreenRect, AutofillState state)
        {
            if (state == AutofillState.None || Event.current == null || Event.current.type != EventType.Repaint) return;
            var r = new Rect(cellScreenRect.xMax - BadgeSize - BadgeInset, cellScreenRect.y + BadgeInset, BadgeSize, BadgeSize);
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, state == AutofillState.OutOfRange ? OutOfRangeAlpha : 1f);
            GUI.DrawTexture(r, BadgeTexture(), ScaleMode.StretchToFill, true);
            GUI.color = old;
        }

        /// <summary>uGUI badge: creates or updates the "AutofillBadge" child at the cell's top-right; hidden for None. Returns it (null if never needed).</summary>
        public static GameObject EnsureBadge(RectTransform cell, AutofillState state)
        {
            if (cell == null) return null;
            Transform found = cell.Find(BadgeName);
            if (state == AutofillState.None) { if (found != null) found.gameObject.SetActive(false); return found != null ? found.gameObject : null; }
            GameObject go;
            Image img;
            if (found == null)
            {
                go = new GameObject(BadgeName, typeof(RectTransform), typeof(Image));
                var rt = (RectTransform)go.transform;
                rt.SetParent(cell, false);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
                rt.anchoredPosition = new Vector2(-BadgeInset, -BadgeInset);
                rt.sizeDelta = new Vector2(BadgeSize, BadgeSize);
                img = go.GetComponent<Image>();
                img.sprite = BadgeSprite();
                img.raycastTarget = false;   // the cell keeps its clicks, drags and hover
            }
            else { go = found.gameObject; img = go.GetComponent<Image>(); }
            go.transform.SetAsLastSibling();   // above the icon and the stack count
            if (img != null) img.color = new Color(1f, 1f, 1f, state == AutofillState.OutOfRange ? OutOfRangeAlpha : 1f);
            if (!go.activeSelf) go.SetActive(true);
            return go;
        }

        // ---- helpers ----

        // Same rule as RefillOption.MarkCell: a full stack keeps the max stack, otherwise the current count.
        private static int KeepCount(ItemDrop.ItemData cell) =>
            Mathf.Max(1, cell.m_stack >= cell.m_shared.m_maxStackSize ? cell.m_shared.m_maxStackSize : cell.m_stack);

        private static string ItemName(ItemDrop.ItemData cell) =>
            Localization.instance != null ? Localization.instance.Localize(cell.m_shared.m_name) : cell.m_shared.m_name;

        /// <summary>The "can't reach home" line, or null when home is in reach.</summary>
        private static string OutOfReachLine(Player p)
        {
            float range = RefillOption.Reach(p);
            if (!RemoteAccessBridge.Installed || !RemoteAccessBridge.HasHome(p)) return "Needs a home terminal in reach";
            float d = RemoteAccessBridge.DistanceToHome(p);
            if (d < 0f) return "Needs a home terminal in reach";
            return d > range ? string.Format(CultureInfo.InvariantCulture, "Out of range: {0:N0} m from home (this tier reaches {1:N0} m)", d, range) : null;
        }

        private static bool InReachCached(Player p)
        {
            if (Time.unscaledTime < s_reachUntil && ReferenceEquals(s_reachPlayer, p)) return s_inReach;
            s_reachUntil = Time.unscaledTime + ReachCacheSeconds; s_reachPlayer = p;
            try { s_inReach = RemoteAccessBridge.Refusal(p, RefillOption.Reach(p)) == null; } catch { s_inReach = false; }
            return s_inReach;
        }

        private static void InvalidateMarks() { s_marksFrame = -1; s_reachUntil = 0f; }

        private static bool CoreLoaded()
        {
            if (s_coreLoaded == null) s_coreLoaded = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(CoreGuid);
            return s_coreLoaded.Value;
        }

        // Core's menu draws with TMP's default font; Valheim's fonts may lack U+21BB, so ask TMP (with its fallbacks) once, by reflection
        // because VABackpacks has no TextMeshPro reference. No glyph beats a missing-glyph box.
        private static string GlyphPrefix()
        {
            if (s_glyphInFont == null)
            {
                s_glyphInFont = false;
                try
                {
                    Type settings = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { settings = asm.GetType("TMPro.TMP_Settings", false); if (settings != null) break; }
                    object font = settings?.GetProperty("defaultFontAsset", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
                    MethodInfo has = font?.GetType().GetMethod("HasCharacter", new[] { typeof(char), typeof(bool), typeof(bool) });
                    if (has != null) s_glyphInFont = (bool)has.Invoke(font, new object[] { MenuGlyph[0], true, false });
                }
                catch (Exception e) { Log.LogDebug($"[Refill] glyph probe failed: {e.Message}"); }
                Log.LogDebug($"[Refill] menu glyph {(s_glyphInFont.Value ? "shown" : "left out (the menu font lacks it)")}");
            }
            return s_glyphInFont.Value ? MenuGlyph + " " : "";
        }

        private static Color OkColor()
        {
            if (s_ok == null)
            {
                s_ok = OkFallback;
                if (CoreLoaded()) { try { s_ok = CoreOk(); } catch { } }
            }
            return s_ok.Value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Color CoreOk() => FiresCore.UI.Shell.Kit.FiresTokens.Ok;

        private static Sprite BadgeSprite()
        {
            if (s_badgeSprite == null) s_badgeSprite = Sprite.Create(BadgeTexture(), new Rect(0, 0, BadgeTexSize, BadgeTexSize), new Vector2(0.5f, 0.5f), 100f);
            return s_badgeSprite;
        }

        // The badge is drawn, not typed: a dark disc, a green rim, and inside a green arc (~300 degrees) with an arrowhead at its open
        // end, i.e. the circular-arrow glyph without depending on any font having it.
        private static Texture2D BadgeTexture()
        {
            if (s_badgeTex != null) return s_badgeTex;
            const float Rim = 1.6f, ArcR = 7.2f, ArcW = 1.6f, GapStart = 50f, GapEnd = 110f, Head = 3.4f;
            int n = BadgeTexSize; float c = (n - 1) * 0.5f, outer = n * 0.5f - 0.5f;
            Color ok = OkColor();
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "VABackpacks.AutofillBadge", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            // the arrowhead sits at the arc's end that meets the gap, pointing clockwise into it
            float headAng = GapStart * Mathf.Deg2Rad;
            Vector2 tip = new Vector2(c + Mathf.Cos(headAng) * ArcR, c + Mathf.Sin(headAng) * ArcR);
            Vector2 tangent = new Vector2(Mathf.Sin(headAng), -Mathf.Cos(headAng));   // clockwise direction
            Vector2 radial = new Vector2(Mathf.Cos(headAng), Mathf.Sin(headAng));
            Vector2 a = tip + tangent * Head, b = tip - tangent * Head * 0.4f + radial * Head, d = tip - tangent * Head * 0.4f - radial * Head;
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x - c, dy = y - c, r = Mathf.Sqrt(dx * dx + dy * dy);
                    Color col = Color.clear;
                    float disc = Mathf.Clamp01(outer - r + 0.5f);
                    if (disc > 0f)
                    {
                        col = BadgeFill; col.a *= disc;
                        if (r > outer - Rim) col = Blend(col, ok, Mathf.Clamp01(r - (outer - Rim) + 0.5f) * disc);
                        float ang = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg; if (ang < 0f) ang += 360f;
                        bool inGap = ang > GapStart && ang < GapEnd;
                        float arc = Mathf.Clamp01(ArcW * 0.5f - Mathf.Abs(r - ArcR) + 0.5f);
                        if (!inGap && arc > 0f) col = Blend(col, ok, arc);
                        if (InTriangle(new Vector2(x, y), a, b, d)) col = Blend(col, ok, 1f);
                    }
                    px[y * n + x] = col;
                }
            tex.SetPixels(px); tex.Apply(false, false);
            s_badgeTex = tex;
            return tex;
        }

        private static Color Blend(Color under, Color over, float t) { Color o = Color.Lerp(under, over, t); o.a = Mathf.Max(under.a, over.a * t); return o; }

        private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(p, a, b), d2 = Cross(p, b, c), d3 = Cross(p, c, a);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        private static float Cross(Vector2 p, Vector2 a, Vector2 b) => (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
    }
}
