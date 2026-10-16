// CampLayout - 1.0.35 (Fire's hand test 10-08: a re-deploy put the chest on the log pile and the bedroll over the fire).
// The camp's own pieces (fire, workbench, chest, bed, wood pile, chair, cooking station) were placed fresh every deploy
// by random offsets, while the player's captured pieces came back at fixed offsets, so they collided. Pack-up now saves
// where each camp piece stood, as an offset and a yaw in the CAMP's frame (centre + facing), and the next deploy puts
// it back at the same spot turned to the new facing. A role with no saved spot (first deploy, or a piece the player
// removed) is placed the old way.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    internal static class CampLayout
    {
        internal const string CustomDataKey = "VA_CampLayout";

        internal struct Spot
        {
            internal Vector3 Offset;   // in the camp frame
            internal float Yaw;        // relative to the camp facing
        }

        internal static Quaternion Facing(float yaw) => Quaternion.Euler(0f, yaw, 0f);

        internal static float YawOf(Vector3 forward)
        {
            forward.y = 0f;
            // 1.0.41 (Fire): the camp chest always faces north, so a camp is never turned: every offset is world-axis from
            // the chest. Kept as a function so the callers stay one place to change.
            return 0f;
        }

        /// <summary>World pose -> camp-frame spot.</summary>
        internal static Spot ToSpot(Vector3 center, float campYaw, Vector3 pos, Quaternion rot)
        {
            Quaternion inv = Quaternion.Inverse(Facing(campYaw));
            return new Spot { Offset = inv * (pos - center), Yaw = Mathf.DeltaAngle(campYaw, rot.eulerAngles.y) };
        }

        internal static Vector3 WorldPos(Vector3 center, float campYaw, Spot s) => center + Facing(campYaw) * s.Offset;
        internal static Quaternion WorldRot(float campYaw, Spot s) => Facing(campYaw + s.Yaw);

        /// <summary>Reads the layout from the camp marker's piece ZDOs. Missing pieces are left out.</summary>
        internal static Dictionary<string, Spot> Capture(BackpackCampMarker marker)
        {
            var spots = new Dictionary<string, Spot>();
            if (marker == null || ZDOMan.instance == null) return spots;
            void Add(string role, ZDOID id)
            {
                if (id == ZDOID.None) return;
                ZDO zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null) return;
                spots[role] = ToSpot(marker.CampCenter, marker.CampYaw, zdo.GetPosition(), zdo.GetRotation());
            }
            Add("fire", marker.CampfireZdoId);
            Add("workbench", marker.WorkbenchZdoId);
            Add("bed", marker.BedZdoId);
            Add("woodpile", marker.WoodpileZdoId);
            Add("chair", marker.ChairZdoId);
            Add("cook", marker.CookingStationZdoId);
            spots["chest"] = ToSpot(marker.CampCenter, marker.CampYaw, marker.transform.position, marker.transform.rotation);
            return spots;
        }

        internal static string Encode(Dictionary<string, Spot> spots)
        {
            if (spots == null || spots.Count == 0) return null;
            var sb = new StringBuilder("1");
            foreach (var kv in spots)
                sb.Append(';').Append(kv.Key).Append(',')
                  .Append(F(kv.Value.Offset.x)).Append(',').Append(F(kv.Value.Offset.y)).Append(',').Append(F(kv.Value.Offset.z)).Append(',')
                  .Append(F(kv.Value.Yaw));
            return sb.ToString();
        }

        internal static Dictionary<string, Spot> Decode(string s)
        {
            var spots = new Dictionary<string, Spot>();
            if (string.IsNullOrEmpty(s)) return spots;
            string[] parts = s.Split(';');
            if (parts.Length == 0 || parts[0] != "1") return spots;
            for (int i = 1; i < parts.Length; i++)
            {
                string[] f = parts[i].Split(',');
                if (f.Length != 5) continue;
                try
                {
                    spots[f[0]] = new Spot
                    {
                        Offset = new Vector3(P(f[1]), P(f[2]), P(f[3])),
                        Yaw = P(f[4]),
                    };
                }
                catch (FormatException) { }
            }
            return spots;
        }

        internal static Dictionary<string, Spot> Read(ItemDrop.ItemData bag) =>
            bag?.m_customData != null && bag.m_customData.TryGetValue(CustomDataKey, out var s) ? Decode(s) : new Dictionary<string, Spot>();

        internal static void Write(ItemDrop.ItemData bag, string encoded)
        {
            if (bag == null) return;
            if (bag.m_customData == null) bag.m_customData = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(encoded)) bag.m_customData.Remove(CustomDataKey);
            else bag.m_customData[CustomDataKey] = encoded;
        }

        static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        static float P(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
