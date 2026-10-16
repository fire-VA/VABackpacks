// CampPieceCounter - 1.0.44 (Fire: show how full a camp is while building in it, warn near the cap, and refuse a piece at the
// cap). While the local player stands inside a camp's ring with the hammer (or cultivator) out, a small parchment box shows
// "Camp pieces: N / CAP (L#)". N is CampCapture.Collect over CampRing, the exact count the pack-up cap check uses, so the
// counter and the pack-up never disagree. At 90% it turns amber with a one-time centre message; at the cap, placing one more
// piece inside the ring is refused (Player.TryPlacePiece). When the packed-size limit (256 KB) would be reached first, the
// counter shows that limit instead. Counter changes and refusals log at Debug.

using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    internal sealed class CampPieceCounter : MonoBehaviour
    {
        static CampPieceCounter _instance;
        static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks.Camp");

        // 1.0.45 (Fire: fps dips at his camp): the full ring scan is costly, so it runs once a second, and the packed-size
        // estimate (re-encodes every piece) only when the count changed and at most every 10 s, and only past half the cap.
        const float CountEvery = 5f, WarnAt = 0.9f;   // [lead]: recount on a placed/removed piece, 5 s fallback
        static bool _dirty = true;
        internal static void MarkDirty() => _dirty = true;
        static readonly System.Diagnostics.Stopwatch Watch = new System.Diagnostics.Stopwatch();
        float _nextTimingLog;

        BackpackCampMarker _camp;
        int _count = -1, _cap, _level, _sizePct;
        float _nextCount, _nextSize;
        bool _warned, _full;
        string _lastLogged;
        static Texture2D _bg;

        internal static void Ensure()
        {
            if (_instance != null || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            var go = new GameObject("VACampPieceCounter");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<CampPieceCounter>();
        }

        /// <summary>True (with the camp) when the point is inside a ring whose camp is at its cap or size limit.</summary>
        internal static bool IsFullAt(Vector3 point, out string message)
        {
            message = null;
            if (_instance == null || _instance._camp == null || !_instance._full) return false;
            var c = _instance._camp;
            Vector3 d = point - CampRing.Centre(c);
            float r = CampRing.Radius(c);
            if (d.x * d.x + d.z * d.z > r * r) return false;
            message = _instance._sizePct >= 100
                ? $"Camp full: the bag's data limit is reached ({_instance._count} pieces, level {_instance._level} bag)"
                : $"Camp full: {_instance._count} / {_instance._cap} for a level {_instance._level} bag";
            return true;
        }

        static bool Building(Player p)
        {
            var right = p.RightItem;
            return right != null && right.m_shared.m_buildPieces != null;
        }

        void Update()
        {
            var player = Player.m_localPlayer;
            BackpackCampMarker camp = null;
            if (player != null && Building(player))
            {
                Vector3 pos = player.transform.position;
                foreach (var m in BackpackCampRegistry.All)
                {
                    if (m == null || m.OwnerPlayerId != player.GetPlayerID()) continue;
                    Vector3 d = pos - CampRing.Centre(m);
                    float r = CampRing.Radius(m);
                    if (d.x * d.x + d.z * d.z <= r * r) { camp = m; break; }
                }
            }
            if (camp != _camp) { _camp = camp; _count = -1; _warned = false; _nextCount = 0f; _nextSize = 0f; }
            if (_camp == null) { _full = false; return; }

            if (_dirty || Time.time >= _nextCount)
            {
                _dirty = false;
                _nextCount = Time.time + CountEvery;
                _level = BackpackCampManager.LevelOf(_camp);
                _cap = CampLevels.PieceCap(_level);
                Watch.Restart();
                var found = CampCapture.Collect(player, CampRing.Centre(_camp), CampRing.Radius(_camp), new CampCapture.CollectStats());
                double scanMs = Watch.Elapsed.TotalMilliseconds;
                bool changed = found.Count != _count;
                _count = found.Count;
                double sizeMs = 0;
                if (changed)
                {
                    Watch.Restart();
                    int chars = found.Count * 2 >= _cap ? CampCapture.EstimateBytes(found) : 0;
                    sizeMs = Watch.Elapsed.TotalMilliseconds;
                    _sizePct = Mathf.Clamp(Mathf.RoundToInt(100f * chars / CampCapture.MaxEncodedChars), 0, 999);
                }
                _full = _count >= _cap || _sizePct >= 100;
                if (Time.time >= _nextTimingLog || scanMs + sizeMs > 8.0)
                {
                    _nextTimingLog = Time.time + 30f;
                    Log.LogDebug($"camp counter timing: ring scan {scanMs:0.0} ms for {found.Count} piece(s), size estimate {sizeMs:0.0} ms");
                }
                float fill = Mathf.Max(_cap > 0 ? (float)_count / _cap : 0f, _sizePct / 100f);
                if (fill >= WarnAt && !_warned)
                {
                    _warned = true;
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                        $"Camp nearly full: {_count} / {_cap} pieces{(_sizePct >= 90 ? $", {_sizePct}% of the bag's data" : "")}");
                }
                string state = $"{_count}/{_cap} L{_level} size {_sizePct}%";
                if (state != _lastLogged)
                {
                    _lastLogged = state;
                    Log.LogDebug($"camp counter: {_count} / {_cap} pieces (level {_level}), packed size {_sizePct}% of the limit{(_full ? ", FULL" : fill >= WarnAt ? ", amber" : "")}");
                }
            }
        }

        void OnGUI()
        {
            if (_camp == null || _count < 0) return;
            if (_bg == null)
            {
                _bg = new Texture2D(1, 1);
                _bg.SetPixel(0, 0, new Color(0.84f, 0.76f, 0.58f, 0.94f));   // the VA player panel's parchment
                _bg.Apply();
            }
            float fill = Mathf.Max(_cap > 0 ? (float)_count / _cap : 0f, _sizePct / 100f);
            Color ink = _full ? new Color(0.65f, 0.08f, 0.05f) : fill >= WarnAt ? new Color(0.78f, 0.45f, 0.02f) : new Color(0.22f, 0.15f, 0.08f);
            string text = _sizePct > 0 && _sizePct >= Mathf.RoundToInt(100f * _count / Mathf.Max(1, _cap))
                ? $"Camp pieces: {_count} / {_cap} (L{_level})   data {_sizePct}%"
                : $"Camp pieces: {_count} / {_cap} (L{_level})";
            var style = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            style.normal.textColor = ink;
            var size = style.CalcSize(new GUIContent(text));
            var rect = new Rect((Screen.width - size.x - 28f) / 2f, 92f, size.x + 28f, size.y + 12f);
            GUI.DrawTexture(rect, _bg);
            GUI.Label(rect, text, style);
        }

        // 1.0.45: a piece placed or removed anywhere marks the count stale; the next Update recounts (no per-frame scans).
        [HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
        static class Piece_SetCreator_Dirty { static void Postfix() => MarkDirty(); }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Remove))]
        static class WearNTear_Remove_Dirty { static void Prefix() => MarkDirty(); }

        // At the cap, one more piece inside the ring is refused before vanilla places it.
        [HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
        static class Player_TryPlacePiece_CampCap
        {
            static readonly AccessTools.FieldRef<Player, GameObject> Ghost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");

            static bool Prefix(Player __instance, ref bool __result)
            {
                if (__instance != Player.m_localPlayer) return true;
                GameObject ghost = Ghost(__instance);
                if (ghost == null || !IsFullAt(ghost.transform.position, out string message)) return true;
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, message);
                Log.LogDebug($"camp counter: placement of {Utils.GetPrefabName(ghost)} refused ({message})");
                __result = false;
                return false;
            }
        }
    }
}
