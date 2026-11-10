using System;
using System.Reflection;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    /// <summary>
    /// 1.0.129 (halo ring, Fire 10-24): the camp's local frame. On a halo ring (Core GravityUp.InField) "up" is the ring's local up,
    /// so a camp is laid out in a flat frame at the drop point: the camp code computes in that frame as on a flat world, ground
    /// probes cast along the ring's down, and every spawn is mapped back by <see cref="Pos"/> / <see cref="Rot"/>. The ring is
    /// 4.7 km across and a camp is a few metres, so the flat frame stays within millimetres of the floor.
    /// Off a ring, or on a Core without GravityUp, nothing is active and every call returns its input unchanged.
    /// </summary>
    internal static class CampFrame
    {
        private static bool s_looked, s_logged;
        private static Func<Vector3, Vector3> s_at;
        private static Func<Vector3, bool> s_inField; private static Func<bool> s_active;   // bound once (no Invoke / object[] per call)
        private static Vector3 s_origin;
        private static Quaternion s_rot = Quaternion.identity, s_inv = Quaternion.identity;

        /// <summary>A camp is being built in the ring frame right now.</summary>
        internal static bool Active { get; private set; }

        private static void Bind()
        {
            if (s_looked) return;
            s_looked = true;
            var t = Type.GetType("FiresCore.World.GravityUp, FiresUnifiedCore", false);
            var at = t?.GetMethod("At", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vector3) }, null);
            var inf = t?.GetMethod("InField", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vector3) }, null);
            var act = t?.GetProperty("Active", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
            try { if (inf != null && act != null && inf.ReturnType == typeof(bool)) { s_inField = (Func<Vector3, bool>)Delegate.CreateDelegate(typeof(Func<Vector3, bool>), inf); s_active = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), act); } } catch { s_inField = null; s_active = null; }
            if (at != null) s_at = (Func<Vector3, Vector3>)Delegate.CreateDelegate(typeof(Func<Vector3, Vector3>), at);
        }

        /// <summary>The point is on / in a halo ring.</summary>
        internal static bool InField(Vector3 p)
        {
            Bind();
            if (s_inField == null || s_at == null || s_active == null) return false;
            // Fire 10-26 (no ring logic on a non-ring world): Core's GravityUp.Active (the one ring authority) first
            try { return s_active() && s_inField(p); } catch { return false; }
        }

        /// <summary>The local up at the point (world up outside every field).</summary>
        internal static Vector3 UpAt(Vector3 p)
        {
            Bind();
            if (s_at == null) return Vector3.up;
            try { var u = s_at(p); return u.sqrMagnitude > 0.5f ? u.normalized : Vector3.up; } catch { return Vector3.up; }
        }

        /// <summary>Starts the ring frame at the drop point when it is on a ring; returns whether it did.</summary>
        internal static bool Begin(Vector3 origin)
        {
            Active = false;
            if (!InField(origin)) return false;
            s_origin = origin;
            s_rot = Quaternion.FromToRotation(Vector3.up, UpAt(origin));
            s_inv = Quaternion.Inverse(s_rot);
            Active = true;
            if (!s_logged)
            {
                s_logged = true;
                Vector3 u = s_rot * Vector3.up;
                VabLog.Line($"[VABackpacks] halo ring: camp built in the ring's frame at {origin.x:F0},{origin.y:F0},{origin.z:F0} (up {u.x:F2},{u.y:F2},{u.z:F2}); pieces stand on the ring floor, no terrain level / paint on the ring");
            }
            return true;
        }

        internal static void End() { Active = false; }

        /// <summary>Camp-frame point -> world.</summary>
        internal static Vector3 Pos(Vector3 local) => Active ? s_origin + s_rot * (local - s_origin) : local;
        /// <summary>Camp-frame rotation -> world.</summary>
        internal static Quaternion Rot(Quaternion local) => Active ? s_rot * local : local;
        /// <summary>World direction -> camp frame.</summary>
        internal static Vector3 DirToLocal(Vector3 world) => Active ? s_inv * world : world;

        /// <summary>
        /// In the frame: a ray straight down from (x, fromY, z) for `dist` m. The hit's height in the frame, or false.
        /// </summary>
        internal static bool RayDown(float x, float z, float fromY, float dist, int mask, out float y, out Collider hitCollider)
        {
            y = 0f; hitCollider = null;
            if (!Active) return false;
            Vector3 from = Pos(new Vector3(x, fromY, z));
            Vector3 down = s_rot * Vector3.down;
            if (!Physics.Raycast(from, down, out RaycastHit hit, dist, mask, QueryTriggerInteraction.Ignore)) return false;
            y = (s_origin + s_inv * (hit.point - s_origin)).y;
            hitCollider = hit.collider;
            return true;
        }
    }
}
