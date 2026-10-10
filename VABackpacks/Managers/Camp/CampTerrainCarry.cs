// CampTerrainCarry - 1.0.36 (Fire's hand test 10-08: the camp footprint must keep the player's terrain edits, level,
// raise, dig and paint, and bring them back on the next deploy). The ground inside the camp is captured at pack-up as a
// height + paint snapshot in the camp's frame and stamped again around the new camp, turned to its facing. The work is
// FAT's (VerdantsAscent.Terrain.Voxel.FatTerrainSnapshot, voxel-only per our terrain rule), reached by reflection: without
// FAT the camp keeps its own level-and-paint and nothing is carried.

using System;
using System.Reflection;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    internal static class CampTerrainCarry
    {
        internal const string CustomDataKey = "VA_CampTerrain";
        const string TypeName = "VerdantsAscent.Terrain.Voxel.FatTerrainSnapshot";

        static bool _resolved;
        static MethodInfo _capture, _apply, _level;

        static bool Resolve()
        {
            if (_resolved) return _capture != null && _apply != null;
            _resolved = true;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType(TypeName, false); } catch { continue; }
                if (t == null) continue;
                _capture = t.GetMethod("Capture", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vector3), typeof(float), typeof(float) }, null);
                _apply = t.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(byte[]), typeof(Vector3), typeof(float) }, null);
                // 1.0.38: FAT 0.2.862+
                _level = t.GetMethod("Level", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vector3), typeof(float), typeof(float), typeof(int) }, null);
                break;
            }
            Debug.Log(_capture != null && _apply != null
                ? "[VABackpacks] camp terrain: FatTerrainSnapshot found; camps carry their ground"
                : "[VABackpacks] camp terrain: FatTerrainSnapshot not found (FAT missing or older than 0.2.860); camps keep their own level and paint");
            return _capture != null && _apply != null;
        }

        /// <summary>1.0.38: FAT can level voxel ground (0.2.862+).</summary>
        internal static bool CanLevel => Resolve() && _level != null;

        /// <summary>Fills below and carves above y in a disc, then paints (0 none, 1 dirt, 2 cultivated, 3 paved). why = null: no line.</summary>
        internal static void Level(Vector3 center, float radius, float y, int paint, string why)
        {
            if (!CanLevel) return;
            try
            {
                string line = _level.Invoke(null, new object[] { center, radius, y, paint }) as string;
                if (why != null) Debug.Log($"[VABackpacks] {why} on voxel ground: {line}");
            }
            catch (Exception ex) { Debug.LogWarning($"[VABackpacks] voxel level ({why ?? "pad"}) failed: {(ex.InnerException ?? ex).Message}"); }
        }

        /// <summary>Pack-up: the ground in the camp, as base64, or null.</summary>
        internal static string Capture(Vector3 center, float yaw, float radius)
        {
            if (!Resolve()) return null;
            try
            {
                var bytes = _capture.Invoke(null, new object[] { center, yaw, radius }) as byte[];
                Debug.Log($"[VABackpacks] camp terrain captured: {(bytes == null ? "nothing (no voxel ground loaded)" : bytes.Length + " bytes")}, radius {radius:0.#} m, facing {yaw:0}");
                return bytes == null ? null : Convert.ToBase64String(bytes);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] camp terrain capture failed: {(ex.InnerException ?? ex).Message}");
                return null;
            }
        }

        /// <summary>Deploy: stamps the bag's carried ground around the new camp.</summary>
        internal static void Apply(ItemDrop.ItemData bag, Vector3 center, float yaw)
        {
            if (bag?.m_customData == null || !bag.m_customData.TryGetValue(CustomDataKey, out var b64) || string.IsNullOrEmpty(b64)) return;
            if (!Resolve()) { Debug.Log("[VABackpacks] camp terrain: the bag carries ground but FAT is missing; not applied (kept in the bag)"); return; }
            try
            {
                string line = _apply.Invoke(null, new object[] { Convert.FromBase64String(b64), center, yaw }) as string;
                Debug.Log($"[VABackpacks] camp terrain applied at facing {yaw:0}: {line}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] camp terrain apply failed: {(ex.InnerException ?? ex).Message}");
            }
        }

        /// <summary>Pack-up / rollback: puts the site's ground from before the camp back (voxel worlds; the heightmap restore
        /// cannot undo voxel stamps). A heightfield: an overhang or cave mouth comes back solid at its top height.</summary>
        internal static void RestoreGround(string b64, Vector3 center, float yaw, string why)
        {
            if (string.IsNullOrEmpty(b64) || !Resolve()) return;
            try
            {
                string line = _apply.Invoke(null, new object[] { Convert.FromBase64String(b64), center, yaw }) as string;
                Debug.Log($"[VABackpacks] camp ground put back ({why}): {line}");
            }
            catch (Exception ex) { Debug.LogWarning($"[VABackpacks] camp ground restore ({why}) failed: {(ex.InnerException ?? ex).Message}"); }
        }

        internal static void Write(ItemDrop.ItemData bag, string b64)
        {
            if (bag == null) return;
            if (bag.m_customData == null) bag.m_customData = new System.Collections.Generic.Dictionary<string, string>();
            if (string.IsNullOrEmpty(b64)) bag.m_customData.Remove(CustomDataKey);
            else bag.m_customData[CustomDataKey] = b64;
        }
    }
}
