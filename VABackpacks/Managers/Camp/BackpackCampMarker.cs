// BackpackCampMarker — MonoBehaviour attached at runtime to a deployed
// camp container (a vanilla piece_chest_wood by default). It does NOT
// alter the chest's vanilla open-on-E behavior — that stays intact.
// Its job is to:
//
//   1. Carry the camp's metadata (owner, linked campfire/workbench
//      ZDOIDs, original backpack prefab name, the backpack's config
//      dimensions) so the pack-up flow can find and remove every
//      piece it spawned.
//   2. Round-trip those fields through the chest's ZDO so the data
//      survives logout / world reload / chunk unload.
//   3. Apply the backpack's storage dimensions to the chest at runtime
//      (m_width / m_height + the underlying Inventory) so the deployed
//      chest looks and behaves like the backpack's grid.
//
// The hover-text addition and the Shift+E pack-up gesture are handled
// in CampInteractPatches via Harmony patches on Container, because a
// MonoBehaviour can't safely hijack Container's own Hoverable methods.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    public class BackpackCampMarker : MonoBehaviour
    {
        // ── Runtime fields, hydrated from ZDO on first frame ──────────
        public long  OwnerPlayerId;
        public ZDOID CampfireZdoId;
        public ZDOID WorkbenchZdoId;
        public ZDOID BedZdoId;            // sleeping spot
        public ZDOID WoodpileZdoId;       // decorative wood stack
        public ZDOID ChairZdoId;          // sitting spot
        public ZDOID CookingStationZdoId; // cooking grill over the fire
        public ZDOID TerrainModZdoId;     // mud_road (level + dirt paint) — legacy path only
        public List<ZDOID> BlueprintPieceZdoIds = new(); // pieces spawned from a PlanBuild blueprint, if any
        // Pre-deploy heightmap + paint mask snapshot, base64-encoded.
        // Restored on pack-up to return the camp area to its exact prior
        // state. See CampTerrain.Snapshot / Restore.
        public string TerrainSnapshotBase64 = "";
        public string BackpackPrefabName; // e.g. "DayBag_va"
        public int    BackpackRows;
        public int    BackpackColumns;
        public Vector3 CampCenter;        // world position of the campfire
        public float   CampRadius;        // for cover/bed bypass queries
        public float   CampYaw;           // 1.0.35: the camp facing (degrees) its saved layout is relative to
        public string  GroundBefore = "";  // 1.0.36: FAT voxel snapshot of the site before the camp (restored at pack-up)
        public List<ZDOID> DeployedPieceIds = new(); // 1.0.44: pieces this camp's deploy placed (packed first when over the cap)
        public float   SaveRadius;        // 1.0.40: the ring the camp saves (pieces, plants, terrain); the camp workbench shows it
        // 1.0.22: the deployed bag's own item state (quality, variant, durability, crafter, world level and every
        // customData key except the contents, which go into the chest), so pack-up gives back the same bag.
        // Base64 ZPackage from BackpackCampManager.CaptureItemState; empty for camps from before 1.0.22.
        public string BackpackItemState = "";

        // ── ZDO key constants ─────────────────────────────────────────
        // Stored on the chest's ZDO so the marker can rehydrate after
        // world reload and so the pack-up flow can find the linked
        // pieces via ZDOMan.GetZDO.
        public const string KEY_OWNER       = "va_camp_owner";
        public const string KEY_CAMPFIRE    = "va_camp_fire_zdo";
        public const string KEY_WORKBENCH   = "va_camp_wb_zdo";
        public const string KEY_BED         = "va_camp_bed_zdo";
        public const string KEY_WOODPILE    = "va_camp_wp_zdo";
        public const string KEY_CHAIR       = "va_camp_chair_zdo";
        public const string KEY_COOKSTATION = "va_camp_cook_zdo";
        public const string KEY_TERRAIN_MOD = "va_camp_terrain_zdo";
        public const string KEY_BLUEPRINT_PIECES = "va_camp_bp_pieces"; // CSV "uid:id,uid:id,…"
        public const string KEY_TERRAIN_SNAP     = "va_camp_terrain_snap"; // base64 ZPackage from CampTerrain.Serialize
        public const string KEY_BACKPACK    = "va_camp_bp_prefab";
        public const string KEY_ROWS        = "va_camp_rows";
        public const string KEY_COLS        = "va_camp_cols";
        public const string KEY_CENTER_X    = "va_camp_center_x";
        public const string KEY_CENTER_Y    = "va_camp_center_y";
        public const string KEY_CENTER_Z    = "va_camp_center_z";
        public const string KEY_RADIUS      = "va_camp_radius";
        public const string KEY_ITEM_STATE  = "va_camp_bp_item"; // 1.0.22: base64 ZPackage of the bag's item state
        public const string KEY_YAW         = "va_camp_yaw";     // 1.0.35
        public const string KEY_GROUND_BEFORE = "va_camp_ground0"; // 1.0.36
        public const string KEY_SAVE_RADIUS = "va_camp_save_r"; // 1.0.40
        public const string KEY_DEPLOYED    = "va_camp_deployed"; // 1.0.44

        private Container _container;
        private ZNetView  _nview;
        private bool      _hydrated;

        private void Awake()
        {
            _container = GetComponent<Container>();
            _nview = GetComponent<ZNetView>();
        }

        private void OnEnable()
        {
            // Defer to first Update — Container's own Awake may not have
            // run yet, and the ZDO might not be fully populated until a
            // frame after construction.
            _hydrated = false;
        }

        private void Update()
        {
            if (_hydrated) return;
            if (_nview == null || !_nview.IsValid()) return;

            HydrateFromZdo();
            ApplyContainerDimensions();
            // 1.0.35: the camp chest IS the camp (it holds the bag's state and the piece list), so the hammer cannot remove
            // it; packing up is how it goes. Its contents are never at risk from a hammer remove.
            var piece = GetComponent<Piece>();
            if (piece != null) piece.m_canBeRemoved = false;
            _hydrated = true;
            // 1.0.25: the bag leaning on the chest is local decoration, so every client adds its own when the chest loads
            // (before, only the deployer saw it, and only until the chest unloaded).
            try { BackpackCampManager.AttachBackpackVisualToChest(gameObject, BackpackPrefabName); }
            catch (System.Exception ex) { Debug.LogWarning($"[VABackpacks] Camp chest visual on load failed: {ex.Message}"); }
        }

        /// <summary>
        /// Stamp the runtime fields onto the chest's ZDO. Called by the
        /// camp manager right after spawning, so the data is on disk
        /// before a save tick.
        /// </summary>
        public void WriteToZdo()
        {
            if (_nview == null) _nview = GetComponent<ZNetView>();
            var zdo = _nview?.GetZDO();
            if (zdo == null) return;

            zdo.Set(KEY_OWNER, OwnerPlayerId);
            zdo.Set(KEY_CAMPFIRE,    CampfireZdoId);
            zdo.Set(KEY_WORKBENCH,   WorkbenchZdoId);
            zdo.Set(KEY_BED,         BedZdoId);
            zdo.Set(KEY_WOODPILE,    WoodpileZdoId);
            zdo.Set(KEY_CHAIR,       ChairZdoId);
            zdo.Set(KEY_COOKSTATION, CookingStationZdoId);
            zdo.Set(KEY_TERRAIN_MOD, TerrainModZdoId);
            zdo.Set(KEY_BLUEPRINT_PIECES, SerializeZdoIdList(BlueprintPieceZdoIds));
            zdo.Set(KEY_TERRAIN_SNAP,     TerrainSnapshotBase64 ?? string.Empty);
            zdo.Set(KEY_BACKPACK, BackpackPrefabName ?? string.Empty);
            zdo.Set(KEY_ROWS, BackpackRows);
            zdo.Set(KEY_COLS, BackpackColumns);
            zdo.Set(KEY_CENTER_X, CampCenter.x);
            zdo.Set(KEY_CENTER_Y, CampCenter.y);
            zdo.Set(KEY_CENTER_Z, CampCenter.z);
            zdo.Set(KEY_RADIUS,   CampRadius);
            zdo.Set(KEY_ITEM_STATE, BackpackItemState ?? string.Empty);
            zdo.Set(KEY_YAW, CampYaw);
            zdo.Set(KEY_GROUND_BEFORE, GroundBefore ?? string.Empty);
            zdo.Set(KEY_SAVE_RADIUS, SaveRadius);
            zdo.Set(KEY_DEPLOYED, SerializeZdoIdList(DeployedPieceIds));
        }

        private void HydrateFromZdo()
        {
            var zdo = _nview.GetZDO();
            if (zdo == null) return;

            OwnerPlayerId       = zdo.GetLong(KEY_OWNER, 0L);
            CampfireZdoId       = zdo.GetZDOID(KEY_CAMPFIRE);
            WorkbenchZdoId      = zdo.GetZDOID(KEY_WORKBENCH);
            BedZdoId            = zdo.GetZDOID(KEY_BED);
            WoodpileZdoId       = zdo.GetZDOID(KEY_WOODPILE);
            ChairZdoId          = zdo.GetZDOID(KEY_CHAIR);
            CookingStationZdoId = zdo.GetZDOID(KEY_COOKSTATION);
            TerrainModZdoId     = zdo.GetZDOID(KEY_TERRAIN_MOD);
            BlueprintPieceZdoIds = ParseZdoIdList(zdo.GetString(KEY_BLUEPRINT_PIECES, string.Empty));
            TerrainSnapshotBase64 = zdo.GetString(KEY_TERRAIN_SNAP, string.Empty);
            BackpackPrefabName  = zdo.GetString(KEY_BACKPACK, string.Empty);
            BackpackRows        = zdo.GetInt(KEY_ROWS, 0);
            BackpackColumns     = zdo.GetInt(KEY_COLS, 0);
            CampCenter          = new Vector3(
                zdo.GetFloat(KEY_CENTER_X, transform.position.x),
                zdo.GetFloat(KEY_CENTER_Y, transform.position.y),
                zdo.GetFloat(KEY_CENTER_Z, transform.position.z));
            CampRadius          = zdo.GetFloat(KEY_RADIUS, 6f);
            BackpackItemState   = zdo.GetString(KEY_ITEM_STATE, string.Empty);
            CampYaw             = zdo.GetFloat(KEY_YAW, 0f);
            GroundBefore        = zdo.GetString(KEY_GROUND_BEFORE, string.Empty);
            SaveRadius          = zdo.GetFloat(KEY_SAVE_RADIUS, 0f);
            DeployedPieceIds    = ParseZdoIdList(zdo.GetString(KEY_DEPLOYED, string.Empty));

            // Register with the camp registry for cover-bypass queries.
            BackpackCampRegistry.Register(this);
            if (GetComponent<CampChestCircle>() == null) gameObject.AddComponent<CampChestCircle>();   // 1.0.41: the save ring around the chest
            CampPieceCounter.Ensure();   // 1.0.44: the camp piece counter
            // And re-register the vegetation suppression so chunk reload
            // restores the "no new trees in here" behavior.
            BackpackCampManager.RegisterVegetationClearArea(CampCenter, CampRadius);

            // Orphan cleanup intentionally removed: the original duplicate-
            // spawn it defended against (chest visual ItemDrop registering
            // a ZDO in Awake) is now prevented at source by
            // AttachBackpackVisualToChest's SetActive(false)+StripGameLogic
            // sequence. The sweep was destroying player-dropped backpacks
            // that happened to land inside the camp radius — straight-up
            // data loss. If a legacy save still has duplicates from the
            // old bug, the player can pick them up manually.
        }

        private void OnDisable()
        {
            // Keep the registry tidy when chunks unload.
            BackpackCampRegistry.Unregister(this);
        }

        /// <summary>
        /// Apply the backpack's grid dimensions to the chest's Container.
        /// Called once at hydrate time so the deployed chest displays the
        /// correct number of slots. Vanilla piece_chest_wood is 4×2; a
        /// DayBag is typically 4×6 — without this the chest would lose
        /// rows on first display.
        /// </summary>
        private void ApplyContainerDimensions()
        {
            if (_container == null) _container = GetComponent<Container>();
            if (_container == null) return;
            if (BackpackRows <= 0 || BackpackColumns <= 0) return;

            // Only mutate when the dimensions actually differ — avoids
            // wiping the inventory unnecessarily.
            if (_container.m_width == BackpackColumns
             && _container.m_height == BackpackRows) return;

            _container.m_width  = BackpackColumns;
            _container.m_height = BackpackRows;

            // Resize the underlying Inventory. We can't replace it
            // wholesale without losing items, so use Inventory.Resize via
            // reflection if available; otherwise build a new one and copy.
            // Container exposes GetInventory() but not a public resize,
            // so we go through the Inventory's m_width/m_height fields.
            try
            {
                var inv = _container.GetInventory();
                if (inv == null) return;

                // Inventory's own width/height are the source of truth for
                // grid display. They're public on this Valheim build.
                var wField = typeof(Inventory).GetField("m_width",
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                var hField = typeof(Inventory).GetField("m_height",
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                wField?.SetValue(inv, BackpackColumns);
                hField?.SetValue(inv, BackpackRows);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] BackpackCampMarker.ApplyContainerDimensions: {ex.Message}");
            }
        }

        /// <summary>
        /// True if a marker's data is consistent enough to be packed up.
        /// Used by the manager before kicking off the pack-up coroutine.
        /// </summary>
        public bool IsValid()
            => _hydrated
            && OwnerPlayerId != 0L
            && !string.IsNullOrEmpty(BackpackPrefabName);

        // ── ZDOID list (de)serialization ──────────────────────────────
        // ZDO doesn't have a native "list of ZDOID" slot, so we pack a
        // List<ZDOID> into a comma-separated string of "userId:id"
        // pairs. Used for blueprint pieces, where the count varies per
        // deploy.

        private static string SerializeZdoIdList(List<ZDOID> ids)
        {
            if (ids == null || ids.Count == 0) return string.Empty;
            var sb = new System.Text.StringBuilder(ids.Count * 24);
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var id = ids[i];
                sb.Append(id.UserID).Append(':').Append(id.ID);
            }
            return sb.ToString();
        }

        private static List<ZDOID> ParseZdoIdList(string raw)
        {
            var list = new List<ZDOID>();
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (var part in raw.Split(','))
            {
                int colon = part.IndexOf(':');
                if (colon < 1 || colon == part.Length - 1) continue;
                if (!long.TryParse(part.Substring(0, colon), out long userId)) continue;
                if (!uint.TryParse(part.Substring(colon + 1), out uint id)) continue;
                list.Add(new ZDOID(userId, id));
            }
            return list;
        }
    }
}
