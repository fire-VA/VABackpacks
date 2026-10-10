// CampRing - 1.0.41 (Fire: "the camp level radius needs to ACCURATELY show; we were within our circle when we built our last
// camp"). ONE definition of the camp's save ring, used by the white ring around the chest (CampChestCircle) AND by pack-up
// (pieces, plants, crops, terrain), so the drawn circle and the capture disc are the same circle: centre = the camp chest,
// radius = the camp's save radius. A piece counts when any part of it reaches inside the ring (its colliders' closest
// point, plus a small margin), not only its pivot.

using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    internal static class CampRing
    {
        /// <summary>How far past the ring a piece's closest point may be and still count as inside (the ring line's width).</summary>
        internal const float EdgeMargin = 0.25f;

        /// <summary>The ring's centre: the camp chest.</summary>
        internal static Vector3 Centre(BackpackCampMarker marker) => marker.transform.position;

        /// <summary>The ring's radius: the save radius set at deploy (the camp level's), or the camp radius for older camps.</summary>
        internal static float Radius(BackpackCampMarker marker) => marker.SaveRadius > 0f ? marker.SaveRadius : marker.CampRadius;

        /// <summary>True when any part of <paramref name="go"/> is within the ring (flat XZ distance).</summary>
        internal static bool Reaches(GameObject go, Vector3 centre, float radius)
        {
            if (go == null) return false;
            float limit = radius + EdgeMargin;
            if (FlatSq(go.transform.position, centre) <= limit * limit) return true;
            foreach (Collider c in go.GetComponentsInChildren<Collider>())
            {
                if (c == null || !c.enabled || c.isTrigger) continue;
                Vector3 probe = new Vector3(centre.x, c.bounds.center.y, centre.z);
                Vector3 closest = c is MeshCollider mc && !mc.convex ? c.bounds.ClosestPoint(probe) : c.ClosestPoint(probe);
                if (FlatSq(closest, centre) <= limit * limit) return true;
            }
            return false;
        }

        static float FlatSq(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }
    }
}
