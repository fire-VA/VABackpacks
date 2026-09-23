// BackpackCampRegistry — process-wide list of every loaded camp marker
// so the bed-cover-bypass patch can answer "is this point in any camp's
// radius?" without walking ZDOMan every frame.
//
// Markers register on hydrate (BackpackCampMarker.HydrateFromZdo) and
// unregister on disable (chunk unload, destruction). The list is small
// — typically zero to a handful of camps in range at once — so a flat
// linear scan is fine.

using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    public static class BackpackCampRegistry
    {
        private static readonly List<BackpackCampMarker> _markers = new();

        public static void Register(BackpackCampMarker marker)
        {
            if (marker == null) return;
            if (!_markers.Contains(marker)) _markers.Add(marker);
        }

        public static void Unregister(BackpackCampMarker marker)
        {
            if (marker == null) return;
            _markers.Remove(marker);
        }

        /// <summary>True if <paramref name="point"/> is inside any camp's
        /// radius (in XZ plane — Y is ignored so beds in the camp area
        /// pass the cover check regardless of slope).</summary>
        public static bool IsPointInAnyCamp(Vector3 point)
        {
            // Sweep flat — beds and pieces sit on the ground, the camp
            // center is the campfire's ground position, so XZ distance is
            // the right metric. Compare squared distances to avoid sqrt.
            for (int i = 0; i < _markers.Count; i++)
            {
                var m = _markers[i];
                if (m == null) continue;
                float dx = point.x - m.CampCenter.x;
                float dz = point.z - m.CampCenter.z;
                float r = m.CampRadius;
                if (dx * dx + dz * dz <= r * r) return true;
            }
            return false;
        }

        public static IReadOnlyList<BackpackCampMarker> All => _markers;
    }
}
