// CampCapture — 1.0.29 (Fire 10-08, camp upgrades plan step 2): pack-up takes the player's OWN pieces inside the camp
// level's radius into the bag (m_customData["VA_CampBlueprint"]), and the next deploy places them again around the camp.
//
// Rules (Tools\VABACKPACKS_CAMP_UPGRADES_PLAN.md sections 3, 6):
// - Only pieces this player built (Piece.GetCreator), never the camp's own pieces (immortal tag), ships, carts,
//   tombstones, plants (step 4), or pieces in a ward that doesn't permit the player.
// - Read only first: over the level's piece cap, or a container in use, nothing is taken.
// - A container that still holds items stays in the world for now (step 3 adds stored-item weight), and is reported.
// - Each piece: its ZDO data is read, it is destroyed, and only a piece whose destroy is committed (gone or queued,
//   BackpackCampManager.DestroyedOrQueued) goes into the blueprint. A piece that stays is not packed.
// - Blueprint over MaxBytes (compressed): nothing is taken.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    internal static class CampCapture
    {
        internal const string CustomDataKey = "VA_CampBlueprint";
        private const byte FormatVersion = 3;   // 1.0.39: + plant growth (elapsed ticks); 1.0.35 v2: camp frame; v1: world axes
        private const int MaxBytes = 256 * 1024;

        private sealed class Packed
        {
            internal string Prefab;
            internal Vector3 Offset;
            internal Quaternion Rotation;
            internal string Data;
            internal long Elapsed;     // 1.0.39: a plant's growing time so far (ticks); 0 for anything else
        }

        // ── 1.0.44: the one piece count ─────────────────────────────────────────────────────────────────────────────────

        internal sealed class CollectStats
        {
            internal int Foreign, Unowned, Warded, FullChests, Crops, SkippedTotal;
            internal readonly List<string> Skipped = new List<string>();
            internal ZDOID OpenChest = ZDOID.None;
        }

        /// <summary>
        /// The player's own pieces (plants included) and grown crops inside the ring: what pack-up takes and what counts
        /// against the bag's cap. Used by pack-up and by the camp HUD counter, so the two never disagree.
        /// </summary>
        internal static List<Component> Collect(Player player, Vector3 center, float radius, CollectStats st)
        {
            var found = new List<Component>();
            if (player == null) return found;
            long me = player.GetPlayerID();
            var seen = new HashSet<ZDOID>();
            void Skip(Component c, string why) { st.SkippedTotal++; if (st.Skipped.Count < 10) st.Skipped.Add($"{Utils.GetPrefabName(c.gameObject)} ({why})"); }
            var inRadius = new List<Piece>();
            // Searched wider, then kept when any part of the piece reaches inside the ring (a long wall straddling the edge counts).
            Piece.GetAllPiecesInRadius(center, radius + 16f, inRadius);
            foreach (Piece piece in inRadius)
            {
                if (piece == null) continue;
                if (!CampRing.Reaches(piece.gameObject, center, radius)) continue;
                Vector3 p = piece.transform.position;
                ZNetView nview = piece.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid() || !seen.Add(nview.GetZDO().m_uid)) continue;
                if (nview.GetZDO().GetInt(CampImmortalPatch.KEY_IMMORTAL, 0) == 1) continue;   // the camp's own pieces travel in its layout
                if (piece.GetCreator() != me) { if (piece.GetCreator() != 0) { st.Foreign++; Skip(piece, "another player built it"); } else { st.Unowned++; Skip(piece, "no builder"); } continue; }
                // 1.0.39: own plants (cultivator-placed) are packed with their growth
                if (piece.GetComponent<Ship>() || piece.GetComponent<Vagon>() || piece.GetComponent<TombStone>()) { Skip(piece, "ship, cart or tombstone"); continue; }
                if (!PrivateArea.CheckAccess(p, 0f, false, false)) { st.Warded++; Skip(piece, "inside a ward you cannot use"); continue; }
                Container chest = piece.GetComponentInChildren<Container>();
                if (chest != null)
                {
                    if (chest.IsInUse() && st.OpenChest == ZDOID.None) st.OpenChest = nview.GetZDO().m_uid;
                    if (chest.GetInventory() != null && chest.GetInventory().NrOfItems() > 0) st.FullChests++;   // 1.0.32: packed, contents weigh (CampStorage)
                }
                found.Add(piece);
            }
            // 1.0.39 (plan step 4): grown crops standing on cultivated soil in the radius, ready to harvest. They carry no builder
            // (vanilla grows them from the player's plant), so the soil and the ward decide.
            // 1.0.45 (Fire: fps dips at his camp): crops are found by a physics query around the ring, not a scan of every
            // Pickable in the scene.
            var picks = new HashSet<Pickable>();
            foreach (Collider col in Physics.OverlapSphere(center, radius + 2f, ~0, QueryTriggerInteraction.Collide))
            {
                var pk = col != null ? col.GetComponentInParent<Pickable>() : null;
                if (pk != null) picks.Add(pk);
            }
            foreach (Pickable pick in picks)
            {
                if (pick == null || pick.GetComponent<Piece>() != null) continue;
                Vector3 p = pick.transform.position;
                if (!CampRing.Reaches(pick.gameObject, center, radius)) continue;
                ZNetView nview = pick.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid() || !seen.Add(nview.GetZDO().m_uid)) continue;
                Heightmap hm = Heightmap.FindHeightmap(p);
                if (hm == null || !hm.IsCultivated(p)) continue;
                if (!PrivateArea.CheckAccess(p, 0f, false, false)) { st.Warded++; continue; }
                found.Add(pick);
                st.Crops++;
            }
            return found;
        }

        /// <summary>The packed size estimate in bytes of these pieces (base64 of the compressed blueprint), for the HUD.</summary>
        internal static int EstimateBytes(List<Component> found)
        {
            var packed = new List<Packed>(found.Count);
            foreach (Component c in found)
            {
                var nv = c != null ? c.GetComponent<ZNetView>() : null;
                if (nv == null || !nv.IsValid()) continue;
                packed.Add(new Packed { Prefab = Utils.GetPrefabName(c.gameObject), Offset = Vector3.zero, Rotation = Quaternion.identity, Data = ReadData(nv.GetZDO().m_uid) });
            }
            string s = Encode(packed);
            return s?.Length ?? int.MaxValue;
        }

        internal static int MaxEncodedChars => MaxBytes * 4 / 3 + 4;

        /// <summary>1.0.44: from the last capture, the pieces in the ring and how many a partial pack left standing.</summary>
        internal static int LastInRing, LastLeftBehind;
        static readonly BepInEx.Logging.ManualLogSource CampLog = BepInEx.Logging.Logger.CreateLogSource("VABackpacks.CampCapture");

        // ── pack-up ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Takes the player's own pieces within the level's radius. Returns the blueprint (null when nothing was taken)
        /// and the evidence line.
        /// </summary>
        internal static string Capture(Player player, Vector3 center, float radius, float campYaw, int level, out string line, out CampStorage.Totals storage, bool checkOnly = false,
            bool partial = false, ICollection<ZDOID> first = null)
        {
            line = null;
            storage = new CampStorage.Totals();
            if (player == null || ZNetScene.instance == null || !CampLevels.CustomCamps) return null;
            // 1.0.41: centre and radius come from CampRing (the same circle the chest ring draws).
            int cap = CampLevels.PieceCap(level);
            long me = player.GetPlayerID();

            // 1. Read only (1.0.44: the shared Collect, the same count the camp HUD counter shows).
            var st = new CollectStats();
            var found = Collect(player, center, radius, st);
            int foreign = st.Foreign, unowned = st.Unowned, warded = st.Warded, fullChests = st.FullChests, crops = st.Crops;
            int skippedTotal = st.SkippedTotal;
            var skipped = st.Skipped;
            if (st.OpenChest != ZDOID.None)
            {
                line = $"camp pack-up: refused, a chest is open ({st.OpenChest}); no pieces taken";
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "Camp NOT packed: a chest in the camp is open. Close every chest and try again.");
                return null;
            }
            if (found.Count == 0)
            {
                line = $"camp pack-up: 0 pieces, level {level}, {radius:0} m ({foreign} foreign, {unowned} with no builder, {warded} warded)" + (skippedTotal > 0 ? $"; skipped {skippedTotal}: {string.Join(", ", skipped)}{(skippedTotal > skipped.Count ? ", ..." : "")}" : "");
                return null;
            }
            // 1.0.44 (Fire: over the cap, ask, and on Yes pack up to the cap and leave the rest standing): the camp's own
            // deploy pieces first, then the nearest to the chest, so what stays behind is the outer part.
            LastInRing = found.Count;
            LastLeftBehind = 0;
            var leftBehind = new List<Component>();
            if (partial)
            {
                found = found.OrderBy(c => first != null && c.GetComponent<ZNetView>() is ZNetView fv && fv.IsValid() && first.Contains(fv.GetZDO().m_uid) ? 0 : 1)
                             .ThenBy(c => (c.transform.position - center).sqrMagnitude).ToList();
                if (found.Count > cap) { leftBehind.AddRange(found.Skip(cap)); found = found.Take(cap).ToList(); }
            }
            if (found.Count > cap)
            {
                line = $"camp pack-up: refused, {found.Count} pieces over the level {level} cap of {cap}; no pieces taken";
                if (!checkOnly) MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"Camp NOT packed: {found.Count} building pieces in the ring, a level {level} bag holds {cap}. Remove {found.Count - cap} or use a higher-level bag.");
                return null;
            }

            // Size check before anything is removed.
            long now = ZNet.instance != null ? ZNet.instance.GetTime().Ticks : 0L;
            int plants = 0;
            var packed = new List<Packed>(found.Count);
            foreach (Component piece in found)
            {
                ZDO zdo = piece.GetComponent<ZNetView>().GetZDO();
                packed.Add(new Packed
                {
                    Prefab = Utils.GetPrefabName(piece.gameObject),
                    // 1.0.35: in the camp's frame, so the next deploy turns the whole layout to its own facing.
                    Offset = Quaternion.Inverse(CampLayout.Facing(campYaw)) * (piece.transform.position - center),
                    Rotation = Quaternion.Inverse(CampLayout.Facing(campYaw)) * piece.transform.rotation,
                    Data = ReadData(zdo.m_uid),
                    // 1.0.39: growth is kept as the time grown so far, so the plant does not age while it is packed.
                    Elapsed = piece.GetComponent<Plant>() != null ? Math.Max(0L, now - zdo.GetLong(ZDOVars.s_plantTime, now)) : 0L,
                });
                if (piece.GetComponent<Plant>() != null) plants++;
            }
            string trial = Encode(packed);
            // 1.0.44: a partial pack drops the farthest pieces until the blueprint fits.
            while (partial && trial != null && trial.Length > MaxBytes * 4 / 3 + 4 && packed.Count > 1)
            {
                int drop = Math.Max(1, packed.Count / 10);
                leftBehind.AddRange(found.Skip(found.Count - drop));
                found = found.Take(found.Count - drop).ToList();
                packed = packed.Take(packed.Count - drop).ToList();
                trial = Encode(packed);
            }
            if (trial == null || trial.Length > MaxBytes * 4 / 3 + 4)
            {
                line = $"camp pack-up: refused, the blueprint is {(trial?.Length ?? -1)} chars, over {MaxBytes / 1024} KB; no pieces taken";
                if (!checkOnly) MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "Camp NOT packed: the buildings in the ring are too much data for one bag. Remove some and try again.");
                return null;
            }

            // 1.0.44 (Fire: a refused pack-up still took the camp down and left his building standing): a check-only call stops here,
            // before anything is removed, so pack-up can refuse with the camp untouched.
            if (checkOnly) { line = $"camp pack-up check: {found.Count} pieces, cap {cap}: ok"; return string.Empty; }
            LastLeftBehind = leftBehind.Count;
            if (leftBehind.Count > 0)
                CampLog.LogDebug($"camp pack-up: {leftBehind.Count} piece(s) left standing (over the cap / data limit, farthest from the chest): " +
                    string.Join(", ", leftBehind.Take(30).Select(c => $"{Utils.GetPrefabName(c.gameObject)} at {Vector3.Distance(c.transform.position, center):0.0} m")) +
                    (leftBehind.Count > 30 ? ", ..." : ""));

            // 2-3. Own, remove, verify, per piece.
            var taken = new List<Packed>(packed.Count);
            int stayed = 0;
            for (int i = 0; i < found.Count; i++)
            {
                Component piece = found[i];
                ZNetView nview = piece != null ? piece.GetComponent<ZNetView>() : null;
                if (nview == null || !nview.IsValid()) { stayed++; continue; }
                ZDOID id = nview.GetZDO().m_uid;
                // The chest's items are counted before the destroy; its contents travel in the packed ZDO data.
                Inventory chestInv = piece.GetComponentInChildren<Container>()?.GetInventory();
                CampStorage.Totals before = null;
                if (chestInv != null) { before = new CampStorage.Totals(); before.Add(chestInv); }
                try
                {
                    if (!nview.IsOwner()) nview.ClaimOwnership();
                    ZNetScene.instance.Destroy(piece.gameObject);
                }
                catch (Exception ex) { Debug.LogWarning($"[VABackpacks] camp pack-up: could not remove {id}: {ex.Message}"); }
                if (BackpackCampManager.DestroyedOrQueued(id))
                {
                    taken.Add(packed[i]);
                    if (before != null) { storage.Items += before.Items; storage.Weight += before.Weight; storage.NoTeleport |= before.NoTeleport; }
                }
                else stayed++;
            }
            string blueprint = taken.Count > 0 ? Encode(taken) : null;
            line = $"camp pack-up: {taken.Count} pieces ({plants} growing plants, {crops} grown crops), {stayed} refused, level {level}, {radius:0} m, {blueprint?.Length ?? 0} chars, " +
                   $"{fullChests} chests holding {storage.Items} items weigh {storage.Weight:0.##}{(storage.NoTeleport ? ", blocks portals" : "")} " +
                   $"({foreign} foreign, {unowned} with no builder, {warded} warded)" + (skippedTotal > 0 ? $"; skipped {skippedTotal}: {string.Join(", ", skipped)}{(skippedTotal > skipped.Count ? ", ..." : "")}" : "");
            if (taken.Count == 0) storage = new CampStorage.Totals();
            return blueprint;
        }

        // The piece's ZDO data in the blob format BackpackCampManager.TryApplyPieceData reads (InfinityHammer/WEC style).
        // Connections (portal pairs) are not carried: their ZDOIDs mean nothing at a new spot.
        private static string ReadData(ZDOID id)
        {
            List<KeyValuePair<int, float>> floats = null;
            List<KeyValuePair<int, Vector3>> vecs = null;
            List<KeyValuePair<int, Quaternion>> quats = null;
            List<KeyValuePair<int, int>> ints = null;
            List<KeyValuePair<int, long>> longs = null;
            List<KeyValuePair<int, string>> strings = null;
            List<KeyValuePair<int, byte[]>> bytes = null;
            ZDOConnection connection = null;
            ZDOExtraData.GetData(id, out floats, out vecs, out quats, out ints, out longs, out strings, out bytes, out connection);

            int flags = 0;
            if (Any(floats)) flags |= 0x001;
            if (Any(vecs)) flags |= 0x002;
            if (Any(quats)) flags |= 0x004;
            if (Any(ints)) flags |= 0x008;
            if (Any(strings)) flags |= 0x010;
            if (Any(longs)) flags |= 0x040;
            if (Any(bytes)) flags |= 0x080;
            if (flags == 0) return "";
            var pkg = new ZPackage();
            pkg.Write(flags);
            if (Any(floats)) Write(pkg, floats, (k, v) => k.Write(v));
            if (Any(vecs)) Write(pkg, vecs, (k, v) => k.Write(v));
            if (Any(quats)) Write(pkg, quats, (k, v) => k.Write(v));
            if (Any(ints)) Write(pkg, ints, (k, v) => k.Write(v));
            if (Any(longs)) Write(pkg, longs, (k, v) => k.Write(v));      // longs before strings: the reader's order
            if (Any(strings)) Write(pkg, strings, (k, v) => k.Write(v));
            if (Any(bytes)) Write(pkg, bytes, (k, v) => k.Write(v));
            return pkg.GetBase64();
        }

        private static bool Any<T>(List<T> list) => list != null && list.Count > 0;

        private static void Write<T>(ZPackage pkg, List<KeyValuePair<int, T>> list, Action<ZPackage, T> write)
        {
            int n = Math.Min(list.Count, 255);
            pkg.Write((byte)n);
            for (int i = 0; i < n; i++)
            {
                pkg.Write(list[i].Key);
                write(pkg, list[i].Value);
            }
        }

        private static string Encode(List<Packed> pieces)
        {
            try
            {
                var pkg = new ZPackage();
                pkg.Write(FormatVersion);
                pkg.Write(pieces.Count);
                foreach (Packed p in pieces)
                {
                    pkg.Write(p.Prefab);
                    pkg.Write(p.Offset);
                    pkg.Write(p.Rotation);
                    pkg.Write(p.Data ?? "");
                    pkg.Write(p.Elapsed);
                }
                byte[] raw = pkg.GetArray();
                using (var ms = new MemoryStream())
                {
                    using (var gz = new GZipStream(ms, CompressionMode.Compress)) gz.Write(raw, 0, raw.Length);
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] camp blueprint encode failed: {ex.Message}");
                return null;
            }
        }

        private static List<Packed> Decode(string blueprint) => Decode(blueprint, out _);

        private static List<Packed> Decode(string blueprint, out byte version)
        {
            var list = new List<Packed>();
            byte[] raw;
            using (var input = new MemoryStream(Convert.FromBase64String(blueprint)))
            using (var gz = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gz.CopyTo(output);
                raw = output.ToArray();
            }
            var pkg = new ZPackage(raw);
            version = pkg.ReadByte();
            if (version < 1 || version > FormatVersion) throw new InvalidDataException($"blueprint version {version}");
            int count = pkg.ReadInt();
            for (int i = 0; i < count; i++)
                {
                var p = new Packed { Prefab = pkg.ReadString(), Offset = pkg.ReadVector3(), Rotation = pkg.ReadQuaternion(), Data = pkg.ReadString() };
                if (version >= 3) p.Elapsed = pkg.ReadLong();
                list.Add(p);
            }
            return list;
        }

        // ── deploy ──────────────────────────────────────────────────────────────────────────────────────────────────

        internal static bool HasBlueprint(ItemDrop.ItemData bag) =>
            bag?.m_customData != null && bag.m_customData.TryGetValue(CustomDataKey, out string bp) && !string.IsNullOrEmpty(bp);

        /// <summary>
        /// Places the bag's packed pieces around <paramref name="center"/>. Every spawned object goes into
        /// <paramref name="spawned"/> first, so the deploy's rollback removes them on a failure. Throws on a bad blueprint
        /// (the deploy rolls back and the bag keeps it).
        /// </summary>
        internal static int Place(Player player, ItemDrop.ItemData bag, Vector3 center, float campYaw, List<GameObject> spawned,
            Func<GameObject, string, bool> applyData)
        {
            if (!HasBlueprint(bag)) return 0;
            List<Packed> pieces = Decode(bag.m_customData[CustomDataKey], out byte version);
            Quaternion facing = version >= 2 ? CampLayout.Facing(campYaw) : Quaternion.identity;   // v1 blueprints were world-axis
            int placed = 0, missing = 0, dataFailed = 0, grown = 0;
            foreach (Packed p in pieces)
            {
                GameObject prefab = ZNetScene.instance.GetPrefab(p.Prefab);
                if (prefab == null) { missing++; continue; }
                GameObject go = UnityEngine.Object.Instantiate(prefab, center + facing * p.Offset, facing * p.Rotation);
                spawned.Add(go);
                if (!string.IsNullOrEmpty(p.Data) && !applyData(go, p.Data)) dataFailed++;
                // 1.0.39: a plant carries on from the growth it had when packed.
                if (p.Elapsed > 0 && ZNet.instance != null)
                {
                    var nv = go.GetComponent<ZNetView>();
                    if (nv != null && nv.IsValid()) { nv.GetZDO().Set(ZDOVars.s_plantTime, ZNet.instance.GetTime().Ticks - p.Elapsed); grown++; }
                }
                Piece piece = go.GetComponent<Piece>();
                if (piece != null && piece.GetCreator() == 0)
                    piece.SetCreator(player.GetPlayerID(), Splatform.PlatformManager.DistributionPlatform.LocalUser.PlatformUserID);
                placed++;
            }
            Debug.Log($"[VABackpacks] camp deploy: {placed} packed pieces placed (blueprint v{version}, facing {campYaw:0}, {grown} plants carry on growing), {missing} unknown prefab(s), {dataFailed} data blob(s) not applied");
            return placed;
        }

        /// <summary>The blueprint is consumed by a successful deploy (the pieces are in the world again).</summary>
        internal static void Clear(ItemDrop.ItemData bag) => bag?.m_customData?.Remove(CustomDataKey);
    }
}
