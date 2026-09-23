// BlueprintLoader — load and parse PlanBuild .blueprint and BuildShare
// .vbuild files using the same format InfinityHammer uses, so any
// blueprint authored with PlanBuild or InfinityHammer round-trips cleanly.
//
// Schema reference: InfinityHammer/HammerBlueprintCommand.cs:
//   GetPlanBuild + GetPlanBuildObject + GetPlanBuildSnapPoint
//   GetBuildShare + GetBuildShareObject
//
// Format (.blueprint):
//   #name:<name>
//   #description:<text>
//   #center:<prefabName>
//   #coordinates:x,z,y          (XZY order, hammer convention)
//   #rotation:x,z,y
//   #height:fnp;fnr;dist        (terrain section header)
//     ...rows of semicolon-separated heights...
//   #paint
//     ...rows of "r:g:b:a;r:g:b:a;..." paint cells...
//   #snappoints
//     x;y;z   per row
//   #pieces                     (default mode at file start)
//     name;id;x;y;z;qx;qy;qz;qw;extraInfo;sx;sy;sz;data;chance
//
// Each piece line: position is fields 2-4, rotation 5-8 (xyzw), scale
// 10-12 (default 1), data blob 13 (base64 ZPackage with custom data),
// chance 14 (default 1). Field 9 is the "extra info" string used for
// some non-data-blob storage.
//
// Format (.vbuild — older BuildShare format):
//   space-separated: name qx qy qz qw px py pz [data] [chance]
//
// All numeric fields are parsed in invariant culture; rows containing
// commas have them swapped to dots before parsing (pre-en_US fallback).
//
// File search order:
//   1. Dedicated server PlanBuild dir (admin override)
//   2. Local Steam Valheim PlanBuild dir
//   3. The active process's BepInEx config / PlanBuild dir
// Subdirs probed in each: blueprints/, server/, .

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    public static class BlueprintLoader
    {
        // ── Data classes (match InfinityHammer.Blueprint /
        //    InfinityHammer.BlueprintObject so the field shape is the
        //    same and the centering logic ports cleanly). ───────────

        public class Piece
        {
            public string Prefab = "";
            public Vector3 Pos;
            public Quaternion Rot = Quaternion.identity;
            public Vector3 Scale = Vector3.one;
            public string ExtraInfo = "";
            public string Data = "";
            public float Chance = 1f;
        }

        public class Blueprint
        {
            public string Name = "";
            public string Description = "";
            public string Category = "";
            public string CenterPiece = "";
            public Vector3 Coordinates;
            public Vector3 Rotation;
            public List<Piece> Objects = new();
            public List<Vector3> SnapPoints = new();
            public string SourcePath = "";
            public float Radius;

            /// <summary>
            /// Re-center every object and snap point so the blueprint's
            /// origin is either the named center piece (rotated so its
            /// forward becomes +Z) or, lacking that, the bounding-box
            /// center with Y at the lowest object. Mirrors
            /// InfinityHammer.Blueprint.Center().
            /// </summary>
            public Vector3 Center(string centerPieceOverride)
            {
                if (!string.IsNullOrEmpty(centerPieceOverride))
                    CenterPiece = centerPieceOverride;

                if (Objects.Count == 0) return Vector3.zero;

                var bounds = new Bounds(Objects[0].Pos, Vector3.zero);
                float minY = float.MaxValue;
                Quaternion rot = Quaternion.identity;
                foreach (var p in Objects)
                {
                    minY = Mathf.Min(minY, p.Pos.y);
                    bounds.Encapsulate(p.Pos);
                }
                Vector3 center = new Vector3(bounds.center.x, minY, bounds.center.z);

                // If a center piece is named, use its position + inverse
                // rotation so the blueprint snaps to "center piece is the
                // origin, facing +Z".
                if (!string.IsNullOrEmpty(CenterPiece))
                {
                    foreach (var p in Objects)
                    {
                        if (p.Prefab == CenterPiece)
                        {
                            center = p.Pos;
                            rot = Quaternion.Inverse(p.Rot);
                            break;
                        }
                    }
                }

                Radius = Mathf.Sqrt(bounds.extents.x * bounds.extents.x
                                    + bounds.extents.z * bounds.extents.z);

                foreach (var p in Objects) p.Pos -= center;
                for (int i = 0; i < SnapPoints.Count; i++) SnapPoints[i] -= center;

                if (rot != Quaternion.identity)
                {
                    foreach (var p in Objects)
                    {
                        p.Pos = rot * p.Pos;
                        p.Rot = rot * p.Rot;
                    }
                    for (int i = 0; i < SnapPoints.Count; i++)
                        SnapPoints[i] = rot * SnapPoints[i];
                }
                return center;
            }
        }

        // ── File lookup ───────────────────────────────────────────────

        public static Blueprint LoadByName(string blueprintName)
        {
            if (string.IsNullOrEmpty(blueprintName)) return null;
            if (blueprintName.EndsWith(".blueprint", StringComparison.OrdinalIgnoreCase))
                blueprintName = blueprintName.Substring(0, blueprintName.Length - ".blueprint".Length);
            else if (blueprintName.EndsWith(".vbuild", StringComparison.OrdinalIgnoreCase))
                blueprintName = blueprintName.Substring(0, blueprintName.Length - ".vbuild".Length);

            var probed = new List<string>();
            foreach (var path in EnumerateCandidatePaths(blueprintName))
            {
                probed.Add(path);
                if (!File.Exists(path)) continue;

                try
                {
                    var bp = ParseFile(path);
                    bp.Name = string.IsNullOrEmpty(bp.Name) ? blueprintName : bp.Name;
                    bp.SourcePath = path;
                    Debug.Log($"[VABackpacks] Loaded blueprint '{bp.Name}' from {path} " +
                              $"({bp.Objects.Count} pieces, {bp.SnapPoints.Count} snap points)");
                    return bp;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[VABackpacks] Failed to parse blueprint at {path}: {ex.Message}");
                }
            }

            Debug.LogWarning($"[VABackpacks] Blueprint '{blueprintName}' not found. Probed:\n  " +
                             string.Join("\n  ", probed));
            return null;
        }

        private static IEnumerable<string> EnumerateCandidatePaths(string blueprintName)
        {
            foreach (var ext in new[] { ".blueprint", ".vbuild" })
            {
                string fileName = blueprintName + ext;
                foreach (var root in EnumerateRoots())
                {
                    yield return Path.Combine(root, "blueprints", fileName);
                    yield return Path.Combine(root, "server", fileName);
                    yield return Path.Combine(root, fileName);
                }
            }
        }

        private static IEnumerable<string> EnumerateRoots()
        {
            string steamCommon = GuessSteamCommonRoot();
            string ded   = Path.Combine(steamCommon, "Valheim dedicated server", "BepInEx", "config", "PlanBuild");
            string local = Path.Combine(steamCommon, "Valheim",                  "BepInEx", "config", "PlanBuild");
            string here  = Path.Combine(BepInEx.Paths.ConfigPath, "PlanBuild");

            if (Directory.Exists(ded))   yield return ded;
            if (Directory.Exists(local)) yield return local;
            if (Directory.Exists(here))  yield return here;
        }

        private static string GuessSteamCommonRoot()
        {
            try
            {
                string here = AppDomain.CurrentDomain.BaseDirectory;
                int idx = here.IndexOf(Path.DirectorySeparatorChar + "common" + Path.DirectorySeparatorChar,
                                       StringComparison.OrdinalIgnoreCase);
                if (idx > 0)
                    return here.Substring(0, idx + ("\\common\\").Length);
            }
            catch { }
            return @"C:\Program Files (x86)\Steam\steamapps\common\";
        }

        // ── Top-level dispatch ────────────────────────────────────────

        public static Blueprint ParseFile(string path)
        {
            string[] rows = File.ReadAllLines(path);
            string ext = Path.GetExtension(path);
            var bp = new Blueprint
            {
                Name = Path.GetFileNameWithoutExtension(path).Replace(' ', '_'),
                SourcePath = path,
            };
            switch (ext.ToLowerInvariant())
            {
                case ".blueprint": return ParsePlanBuild(bp, rows);
                case ".vbuild":    return ParseBuildShare(bp, rows);
                default: throw new InvalidOperationException($"Unknown blueprint extension: {ext}");
            }
        }

        // ── PlanBuild .blueprint state-machine parser ────────────────
        // Mirrors InfinityHammer.HammerBlueprintCommand.GetPlanBuild.

        private enum Section { Pieces, TerrainHeight, TerrainPaint, SnapPoints }

        private static Blueprint ParsePlanBuild(Blueprint bp, string[] rows)
        {
            Section section = Section.Pieces;

            foreach (var raw in rows)
            {
                if (raw == null) continue;
                var row = raw;     // don't trim — leading whitespace can be meaningful in some sections
                if (row.Length == 0) continue;

                // Section toggles + metadata headers all start with '#'.
                if (row.StartsWith("#", StringComparison.Ordinal))
                {
                    if (HeaderEquals(row, "#name:", out var name))                 bp.Name = name;
                    else if (HeaderEquals(row, "#description:", out var desc))     bp.Description = desc;
                    else if (HeaderEquals(row, "#category:", out var cat))         bp.Category = cat;
                    else if (HeaderEquals(row, "#center:", out var c))             bp.CenterPiece = c;
                    else if (HeaderEquals(row, "#coordinates:", out var coord))    bp.Coordinates = ParseVectorXZY(coord);
                    else if (HeaderEquals(row, "#rotation:", out var rotS))        bp.Rotation = ParseVectorXZY(rotS);
                    else if (row.StartsWith("#height:", StringComparison.OrdinalIgnoreCase))
                    {
                        // We don't apply terrain heights yet — mud_road
                        // already covers the leveling case for the camp.
                        // Switch to a swallow-mode section so subsequent
                        // height rows are ignored without parse errors.
                        section = Section.TerrainHeight;
                    }
                    else if (row.StartsWith("#paint", StringComparison.OrdinalIgnoreCase))
                        section = Section.TerrainPaint;
                    else if (row.StartsWith("#snappoints", StringComparison.OrdinalIgnoreCase))
                        section = Section.SnapPoints;
                    else if (row.StartsWith("#terrain", StringComparison.OrdinalIgnoreCase))
                        section = Section.TerrainHeight;
                    else if (row.StartsWith("#pieces", StringComparison.OrdinalIgnoreCase))
                        section = Section.Pieces;
                    // Anything else starting with # is a comment — ignore.
                    continue;
                }

                switch (section)
                {
                    case Section.Pieces:
                        var piece = ParsePlanBuildPiece(row);
                        if (piece != null) bp.Objects.Add(piece);
                        break;
                    case Section.SnapPoints:
                        bp.SnapPoints.Add(ParsePlanBuildSnapPoint(row));
                        break;
                    case Section.TerrainHeight:
                    case Section.TerrainPaint:
                        // Parsed but not applied — TODO when the camp grows
                        // a "level the ground from a recorded heightmap"
                        // feature beyond what mud_road provides.
                        break;
                }
            }
            return bp;
        }

        // Header utilities ---------------------------------------------

        /// <summary>
        /// Case-insensitive prefix match, returning the trailing payload.
        /// Mirrors how InfinityHammer reads "#name:foo".
        /// </summary>
        private static bool HeaderEquals(string row, string prefix, out string payload)
        {
            if (row.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                int colon = row.IndexOf(':');
                payload = colon >= 0 && colon + 1 < row.Length
                    ? row.Substring(colon + 1).Trim()
                    : string.Empty;
                return true;
            }
            payload = null;
            return false;
        }

        // ── Piece line parser ─────────────────────────────────────────
        // Format: name;id;x;y;z;qx;qy;qz;qw;info;sx;sy;sz;data;chance
        // Indexes:  0   1 2 3 4  5  6  7  8   9  10 11 12  13   14

        private static Piece ParsePlanBuildPiece(string row)
        {
            // Decimal-comma → decimal-dot tolerance (pre-en_US PlanBuild).
            if (row.IndexOf(',') > -1) row = row.Replace(',', '.');
            string[] cols = row.Split(';');
            if (cols.Length < 9) return null;

            string name = cols[0];
            if (string.IsNullOrEmpty(name)) return null;

            float x  = InvariantFloat(cols, 2);
            float y  = InvariantFloat(cols, 3);
            float z  = InvariantFloat(cols, 4);
            float qx = InvariantFloat(cols, 5);
            float qy = InvariantFloat(cols, 6);
            float qz = InvariantFloat(cols, 7);
            float qw = InvariantFloat(cols, 8);
            string info = cols.Length > 9 ? cols[9] : "";
            float sx = InvariantFloat(cols, 10, 1f);
            float sy = InvariantFloat(cols, 11, 1f);
            float sz = InvariantFloat(cols, 12, 1f);
            string data = cols.Length > 13 ? cols[13] : "";
            float chance = InvariantFloat(cols, 14, 1f);

            return new Piece
            {
                Prefab    = name,
                Pos       = new Vector3(x, y, z),
                Rot       = new Quaternion(qx, qy, qz, qw),
                Scale     = new Vector3(sx, sy, sz),
                ExtraInfo = info,
                Data      = data,
                Chance    = chance,
            };
        }

        private static Vector3 ParsePlanBuildSnapPoint(string row)
        {
            if (row.IndexOf(',') > -1) row = row.Replace(',', '.');
            string[] cols = row.Split(';');
            return new Vector3(
                InvariantFloat(cols, 0),
                InvariantFloat(cols, 1),
                InvariantFloat(cols, 2));
        }

        // ── BuildShare .vbuild parser ─────────────────────────────────
        // Format: name qx qy qz qw px py pz [data] [chance]
        //         space-separated, one piece per row.

        private static Blueprint ParseBuildShare(Blueprint bp, string[] rows)
        {
            foreach (var raw in rows)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                if (raw.StartsWith("#")) continue;
                var piece = ParseBuildSharePiece(raw);
                if (piece != null) bp.Objects.Add(piece);
            }
            return bp;
        }

        private static Piece ParseBuildSharePiece(string row)
        {
            if (row.IndexOf(',') > -1) row = row.Replace(',', '.');
            string[] cols = row.Split(' ');
            if (cols.Length < 8) return null;
            string name = cols[0];
            if (string.IsNullOrEmpty(name)) return null;

            float qx = InvariantFloat(cols, 1);
            float qy = InvariantFloat(cols, 2);
            float qz = InvariantFloat(cols, 3);
            float qw = InvariantFloat(cols, 4);
            float px = InvariantFloat(cols, 5);
            float py = InvariantFloat(cols, 6);
            float pz = InvariantFloat(cols, 7);
            string data = cols.Length > 8 ? cols[8] : "";
            float chance = InvariantFloat(cols, 9, 1f);

            return new Piece
            {
                Prefab = name,
                Pos    = new Vector3(px, py, pz),
                Rot    = new Quaternion(qx, qy, qz, qw),
                Scale  = Vector3.one,
                Data   = data,
                Chance = chance,
            };
        }

        // ── Numeric utilities ─────────────────────────────────────────

        private static float InvariantFloat(string[] row, int index, float def = 0f)
        {
            if (row == null || index >= row.Length) return def;
            return float.TryParse(row[index]?.Trim(), NumberStyles.Float,
                                  CultureInfo.InvariantCulture, out var v)
                ? v : def;
        }

        /// <summary>
        /// Hammer-style XZY vector — "x,z,y" per InfinityHammer's Parse.VectorXZY.
        /// PlanBuild stores coordinates with Y as the up axis sandwiched
        /// between X and Z so saving via the in-game console is friendlier.
        /// </summary>
        private static Vector3 ParseVectorXZY(string s)
        {
            if (string.IsNullOrEmpty(s)) return Vector3.zero;
            if (s.IndexOf(',') > -1 && s.IndexOf('.') < 0)
            {
                // ambiguous: comma may be field separator OR decimal mark.
                // PlanBuild uses commas as separators here, so treat them as such.
            }
            string[] parts = s.Split(',', ';', ' ');
            float x = parts.Length > 0 ? ParseInv(parts[0]) : 0f;
            float z = parts.Length > 1 ? ParseInv(parts[1]) : 0f;
            float y = parts.Length > 2 ? ParseInv(parts[2]) : 0f;
            return new Vector3(x, y, z);
        }

        private static float ParseInv(string s)
            => float.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
    }
}
