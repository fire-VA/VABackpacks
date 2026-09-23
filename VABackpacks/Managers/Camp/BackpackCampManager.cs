// BackpackCampManager — coordinates the deploy / pack-up flow for the
// "drop a backpack, hold Shift+E to make it a camp" feature.
//
// Lifecycle:
//   1. CampInteractPatches catches Shift+E on a backpack ItemDrop or a
//      deployed camp container and forwards to NotifyHoldFrame() each
//      frame the gesture is held.
//   2. After the configured hold duration elapses, the manager fires
//      DeployFromItemDrop() or PackUpCamp() once.
//   3. The MonoBehaviour Update() polls for "the player let go" so the
//      timer resets if the gesture breaks.
//
// One-camp-per-player tracking lives in player.m_customData under
// KEY_PLAYER_CAMP — the value is the camp container's ZDOID encoded as
// "userId:id". On a fresh deploy we check that key, fail if a valid
// camp ZDO is still alive, otherwise clear it (stale leftover) and
// proceed.
//
// Server-authoritative limit enforcement is deferred — for v1 the
// client honors its own customData. A future RPC can do a server-side
// scan of camp ZDOs by owner before allowing a second deploy.

using System;
using System.Collections;
using System.Linq;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    public class BackpackCampManager : MonoBehaviour
    {
        // ── Singleton plumbing ────────────────────────────────────────

        private static BackpackCampManager _instance;
        public static BackpackCampManager Instance => _instance;

        public static void EnsureExists()
        {
            if (_instance != null) return;
            var go = new GameObject("VA_BackpackCampManager");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<BackpackCampManager>();
        }

        private void Awake() { _instance = this; }

        // ── Player-state key ──────────────────────────────────────────

        public const string KEY_PLAYER_CAMP = "VA_BackpackCampZdoId";

        // ── Hold-gesture state ────────────────────────────────────────
        //
        // Implementation note: an earlier draft hooked into Hoverable.Interact
        // and used Shift+E as the gesture. That fights vanilla — the moment
        // E is pressed on a backpack ItemDrop, the pickup fires before our
        // patch can intercept, and the item is gone. Instead we now do
        // independent input polling (Shift + KeyCode.C) and resolve the
        // hovered target via a camera raycast each frame, so we never
        // touch the vanilla pickup or open-chest paths.

        private ItemDrop            _deployTarget;
        private BackpackCampMarker  _packTarget;
        private float               _holdStart;
        private bool                _fired;     // dedupe per-gesture

        // Raycast budget — we only need to find the hovered camp-eligible
        // object at human-perception speeds, not every frame.
        private float _nextRaycastAt;
        private const float RAYCAST_INTERVAL = 0.05f;
        private const float RAYCAST_MAX_DIST = 6f;   // ItemDrop / chest interact range

        // ── Update loop ───────────────────────────────────────────────

        private void Update()
        {
            if (Player.m_localPlayer == null) return;
            if (!ConfigManager.Instance.configCampEnabled.Value) return;

            // Don't run the gesture while menus / chat / other UI are eating input.
            // InventoryGui is included because the player may be typing B in a
            // search box (or just clicking around) — without this guard, B
            // ticks the deploy/pack timer and the progress HUD spams while
            // the cursor is unlocked, which the user perceives as a "camera
            // lockup" once the timer fires.
            if (Console.IsVisible() || TextInput.IsVisible() || Menu.IsVisible()
                || (Chat.instance != null && Chat.instance.HasFocus())
                || (InventoryGui.instance != null && InventoryGui.IsVisible()))
            {
                ResetGesture();
                return;
            }

            // HoboStick MMB special case — runs before the hold-gesture
            // logic so the MMB-press path can short-circuit to an
            // immediate deploy without needing the 3-second hold.
            HoboStickAttackPatch.TryHandleHoboStickMmb();

            // ── Refresh hovered camp target on a throttled cadence ────
            if (Time.unscaledTime >= _nextRaycastAt)
            {
                _nextRaycastAt = Time.unscaledTime + RAYCAST_INTERVAL;
                RefreshHoveredTarget();
            }

            // ── Detect the gesture ───────────────────────────────────
            // Shift is hardcoded as the modifier; the second key is
            // configurable (default B). Avoids fighting vanilla pickup
            // and use bindings on E.
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            KeyCode gestureKey = ConfigManager.Instance.configCampGestureKey.Value;
            bool secondKey = gestureKey != KeyCode.None && Input.GetKey(gestureKey);
            bool gestureHeld = shift && secondKey;

            if (!gestureHeld)
            {
                ResetGesture();
                return;
            }

            // No camp-eligible target → don't accumulate.
            if (_deployTarget == null && _packTarget == null)
            {
                _holdStart = 0f;
                _fired = false;
                return;
            }

            // First frame of the hold → start the timer.
            if (_holdStart <= 0f) _holdStart = Time.unscaledTime;

            float duration = ConfigManager.Instance.configCampHoldDuration.Value;
            float elapsed = Time.unscaledTime - _holdStart;
            float remaining = Mathf.Max(0f, duration - elapsed);

            // Progress feedback (top-left so it doesn't clobber the gesture target).
            if (_deployTarget != null)
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft,
                    $"Deploying camp... {remaining:F1}s");
            }
            else if (_packTarget != null)
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft,
                    $"Packing camp... {remaining:F1}s");
            }

            if (!_fired && elapsed >= duration)
            {
                _fired = true;
                try
                {
                    if (_deployTarget != null) DeployFromItemDrop(_deployTarget);
                    else if (_packTarget != null) PackUpCamp(_packTarget);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[VABackpacks] Camp action failed: {ex}");
                }
                ResetGesture();
            }
        }

        /// <summary>
        /// Find the nearest camp-eligible target the player is positioned
        /// to interact with. Two paths run, in priority order:
        ///
        ///   1. Crosshair raycast — if the player is LOOKING at a backpack
        ///      ItemDrop or a camp chest within RAYCAST_MAX_DIST, that
        ///      target wins. This is what drives the hover-text "[Shift+B]
        ///      Deploy/Pack" prompts.
        ///
        ///   2. Proximity scan — if no raycast hit, but the player is
        ///      WITHIN configCampDeployRadius (default 3m) of a backpack
        ///      ItemDrop or camp chest, that becomes the target. The
        ///      player no longer needs to be standing in just-the-right
        ///      spot looking at a tiny chest collider to pack a camp.
        ///
        /// Switching targets resets the timer so the player can't
        /// accidentally "hold through" between two camps.
        /// </summary>
        private void RefreshHoveredTarget()
        {
            ItemDrop newDeploy = null;
            BackpackCampMarker newPack = null;

            // ── Path 1: crosshair raycast ────────────────────────────
            var cam = Camera.main;
            if (cam != null)
            {
                Vector3 origin = cam.transform.position;
                Vector3 dir = cam.transform.forward;
                if (Physics.Raycast(origin, dir, out RaycastHit hit, RAYCAST_MAX_DIST,
                                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
                {
                    var hitObj = hit.collider != null ? hit.collider.gameObject : null;
                    if (hitObj != null)
                    {
                        // Backpack-as-ItemDrop on the ground
                        var drop = hitObj.GetComponent<ItemDrop>() ?? hitObj.GetComponentInParent<ItemDrop>();
                        if (drop != null && drop.m_itemData != null
                            && BackpackApi.IsBackpack(drop.m_itemData))
                        {
                            newDeploy = drop;
                        }

                        // Already-deployed camp chest (carries our marker)
                        if (newDeploy == null)
                        {
                            var marker = hitObj.GetComponent<BackpackCampMarker>()
                                      ?? hitObj.GetComponentInParent<BackpackCampMarker>();
                            if (marker != null) newPack = marker;
                        }
                    }
                }
            }

            // ── Path 2: proximity scan (if raycast didn't find one) ──
            if (newDeploy == null && newPack == null)
            {
                var player = Player.m_localPlayer;
                if (player != null)
                {
                    float r = ConfigManager.Instance.configCampDeployRadius.Value;
                    Vector3 pp = player.transform.position;
                    var hits = Physics.OverlapSphere(pp, r,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);

                    float bestDeployDistSq = float.MaxValue;
                    float bestPackDistSq   = float.MaxValue;
                    foreach (var col in hits)
                    {
                        if (col == null) continue;
                        var go = col.gameObject;
                        if (go == null) continue;

                        // Backpack ItemDrop?
                        var drop = go.GetComponent<ItemDrop>() ?? go.GetComponentInParent<ItemDrop>();
                        if (drop != null && drop.m_itemData != null
                            && BackpackApi.IsBackpack(drop.m_itemData))
                        {
                            float d = (drop.transform.position - pp).sqrMagnitude;
                            if (d < bestDeployDistSq)
                            {
                                bestDeployDistSq = d;
                                newDeploy = drop;
                            }
                            continue;
                        }

                        // Camp chest with marker? (Pack target.)
                        var marker = go.GetComponent<BackpackCampMarker>()
                                  ?? go.GetComponentInParent<BackpackCampMarker>();
                        if (marker != null)
                        {
                            float d = (marker.transform.position - pp).sqrMagnitude;
                            if (d < bestPackDistSq)
                            {
                                bestPackDistSq = d;
                                newPack = marker;
                            }
                        }
                    }

                    // If both kinds of targets are in range, prefer the
                    // closer one — keeps the gesture deterministic when
                    // a player drops a fresh bag right next to a deployed
                    // camp.
                    if (newDeploy != null && newPack != null)
                    {
                        if (bestDeployDistSq <= bestPackDistSq) newPack = null;
                        else newDeploy = null;
                    }
                }
            }

            // Target switched → reset the timer.
            if (newDeploy != _deployTarget || newPack != _packTarget)
            {
                _holdStart = 0f;
                _fired = false;
            }
            _deployTarget = newDeploy;
            _packTarget = newPack;
        }

        private void ResetGesture()
        {
            _holdStart = 0f;
            _fired = false;
        }

        // ── Deploy ────────────────────────────────────────────────────

        private void DeployFromItemDrop(ItemDrop drop)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            if (drop == null || drop.m_itemData == null) return;

            // One-camp-per-player check
            if (HasActiveCamp(player))
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                    "You already have a camp deployed. Pack it up first.");
                return;
            }

            string backpackPrefab = drop.m_itemData.m_dropPrefab?.name;
            if (string.IsNullOrEmpty(backpackPrefab) || !BackpackApi.IsBackpack(backpackPrefab))
            {
                Debug.LogWarning($"[VABackpacks] DeployFromItemDrop called on non-backpack: {backpackPrefab}");
                return;
            }

            var cfg = BackpackApi.GetConfig(backpackPrefab);
            if (cfg == null)
            {
                Debug.LogWarning($"[VABackpacks] Backpack {backpackPrefab} has no config registered.");
                return;
            }

            // Compute the camp anchor:
            //   - The CAMPFIRE is offset away from the player along the
            //     player's facing direction, so we don't spawn it under
            //     their feet and burn them.
            //   - All other pieces orbit the campfire at PieceSpawnRadius
            //     in random directions, with PieceMinSeparation enforced.
            var fcfg = ConfigManager.Instance;
            float radius = fcfg.configCampRadius.Value;
            float minSep = fcfg.configCampMinSeparation.Value;
            float fireOffset = fcfg.configCampFireOffsetForward.Value;

            Vector3 dropPos = drop.transform.position;
            Vector3 forward = player.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            else forward.Normalize();

            // Resolve blueprint FIRST so we know its footprint radius
            // before we pick the anchor. With a blueprint, the player is
            // standing right where the structure would otherwise spawn
            // on top of them — we need to push the anchor far enough
            // forward to clear the building's outer edge.
            BlueprintLoader.Blueprint blueprintEarly = null;
            if (ConfigManager.Instance.configBackpackCampBlueprint.TryGetValue(backpackPrefab, out var bpCfgEarly)
                && !string.IsNullOrEmpty(bpCfgEarly.Value))
            {
                blueprintEarly = BlueprintLoader.LoadByName(bpCfgEarly.Value);
                blueprintEarly?.Center("");      // normalize + populate Radius
            }

            // Anchor = camp center.
            //   No blueprint:  push by FireOffsetForward (default 1.5m)
            //                  so the fire isn't under the player.
            //   With blueprint: push by FireOffsetForward + blueprint.Radius
            //                  + 1m so the WHOLE building lands forward of
            //                  the player. Without this, the player ends
            //                  up inside (or under!) the structure.
            float forwardPush = fireOffset;
            if (blueprintEarly != null && blueprintEarly.Objects.Count > 0)
                forwardPush += blueprintEarly.Radius + 1f;
            Vector3 anchor = dropPos + forward * forwardPush;

            // Resolve mandatory prefabs.
            var firePrefab  = ZNetScene.instance?.GetPrefab(fcfg.configCampCampfirePrefab.Value);
            var wbPrefab    = ZNetScene.instance?.GetPrefab(fcfg.configCampWorkbenchPrefab.Value);
            var chestPrefab = ZNetScene.instance?.GetPrefab(fcfg.configCampContainerPrefab.Value);

            if (firePrefab == null || wbPrefab == null || chestPrefab == null)
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                    "Camp deploy failed: prefabs missing (check config).");
                Debug.LogError($"[VABackpacks] Missing camp prefabs " +
                               $"fire={firePrefab != null} wb={wbPrefab != null} chest={chestPrefab != null}");
                return;
            }

            // Reuse the blueprint we already loaded above when computing
            // the anchor — no need to load again.
            BlueprintLoader.Blueprint blueprint = blueprintEarly;
            bool useBlueprint = blueprint != null && blueprint.Objects.Count > 0;

            // Effective placement radius for chest/workbench.
            //   - No blueprint: regular PieceSpawnRadius from config.
            //   - With blueprint: max(config radius, blueprint XZ radius
            //     + 1m clearance) so the chest sits clear of any wall/roof.
            float chestRadius = useBlueprint
                ? Mathf.Max(radius, blueprint.Radius + 1f)
                : radius;

            // Layout strategy depends on whether a blueprint is in use:
            //
            //   No blueprint:  anchor IS the fire. Pieces orbit fire at
            //                  radius. Wood pile sits closer (radius*0.7).
            //                  This is the "open clearing" procedural camp.
            //
            //   With blueprint: anchor is the BLUEPRINT CENTER (just a
            //                  spatial reference, no piece spawns there —
            //                  the blueprint pieces fill that space). The
            //                  fire, chest, workbench, AND wood pile all
            //                  orbit the anchor at chestRadius so they
            //                  sit OUTSIDE the building's footprint and
            //                  remain accessible. Cooking station follows
            //                  the fire (it sits on top of it). Terrain
            //                  leveling stays centered on the anchor so
            //                  the dirt patch covers the entire building.
            //
            // PickOffsetPosition tracks `occupied` to keep pieces from
            // overlapping each other.
            var occupied = new System.Collections.Generic.List<Vector3> { anchor };
            Vector3 firePos;
            Vector3 woodpilePos;
            if (useBlueprint)
            {
                // Fire and woodpile share the chest's keep-out radius so
                // they don't end up inside the structure.
                firePos     = PickOffsetPosition(anchor, chestRadius, minSep, occupied.ToArray()); occupied.Add(firePos);
                woodpilePos = PickOffsetPosition(anchor, chestRadius, minSep, occupied.ToArray()); occupied.Add(woodpilePos);
            }
            else
            {
                firePos     = anchor;       // procedural: fire at center
                // woodpile placeholder — picked below at radius*0.7.
                woodpilePos = anchor;
            }
            Vector3 chestPos     = PickOffsetPosition(anchor, chestRadius, minSep, occupied.ToArray()); occupied.Add(chestPos);
            Vector3 workbenchPos = PickOffsetPosition(anchor, chestRadius, minSep, occupied.ToArray()); occupied.Add(workbenchPos);
            Vector3 bedPos       = PickOffsetPosition(anchor, radius, minSep, occupied.ToArray()); occupied.Add(bedPos);
            if (!useBlueprint)
            {
                // Procedural: pull the woodpile inward a bit so it reads
                // as part of the camp clearing.
                woodpilePos = PickOffsetPosition(anchor, radius * 0.7f, minSep, occupied.ToArray());
                occupied.Add(woodpilePos);
            }

            // Chair is pinned to exactly CHAIR_FIRE_DISTANCE meters from the
            // fire so the player can comfortably sit and look at the flames.
            // PickOffsetPosition would put it at a variable radius; we want
            // it close and consistent.
            const float CHAIR_FIRE_DISTANCE = 2f;
            Vector3 chairPos = PickFixedRadiusPosition(firePos, CHAIR_FIRE_DISTANCE,
                                                       minSep, occupied.ToArray());
            occupied.Add(chairPos);

            // Camp center first — its ground height is the altitude we'll
            // level the whole footprint to, and it's what SnapToGround
            // will return for every piece position once leveling is done.
            Vector3 campCenter = SnapToGround(anchor);

            // Resolve optional prefabs — null on miss, we just skip.
            //
            // When a blueprint is in use, each piece is gated by its
            // per-element toggle (Backpacks.Camp.Blueprint section in
            // the config). Defaults are all true, so a blueprint deploy
            // also gets the procedural bed / chair / wood pile / etc by
            // default — set the toggle to false if your blueprint
            // already includes that piece.
            //
            // Without a blueprint, every prefab is resolved unconditionally
            // (the toggles only apply to the blueprint path).
            bool spawnBed       = !useBlueprint || fcfg.configCampBlueprintSpawnBed.Value;
            bool spawnWood      = !useBlueprint || fcfg.configCampBlueprintSpawnWoodpile.Value;
            bool spawnChair     = !useBlueprint || fcfg.configCampBlueprintSpawnChair.Value;
            bool spawnCook      = !useBlueprint || fcfg.configCampBlueprintSpawnCookingStation.Value;
            bool spawnFire      = !useBlueprint || fcfg.configCampBlueprintSpawnFire.Value;
            bool spawnWorkbench = !useBlueprint || fcfg.configCampBlueprintSpawnWorkbench.Value;
            bool spawnTerrain   = !useBlueprint || fcfg.configCampBlueprintSpawnTerrainPatch.Value;

            var bedPrefab     = spawnBed   ? SafeGetPrefab(fcfg.configCampBedPrefab.Value)            : null;
            var woodPrefab    = spawnWood  ? SafeGetPrefab(fcfg.configCampWoodpilePrefab.Value)       : null;
            var chairPrefab   = spawnChair ? SafeGetPrefab(fcfg.configCampChairPrefab.Value)          : null;
            var cookPrefab    = spawnCook  ? SafeGetPrefab(fcfg.configCampCookingStationPrefab.Value) : null;
            var terrainPrefab = SafeGetPrefab(fcfg.configCampTerrainModPrefab.Value);

            // ── Pre-level the terrain BEFORE snapping piece positions ──
            // This is the fix for "pieces end up below ground on uneven
            // terrain". The OLD ordering was: snap each piece to its
            // local ground height (varies across hills), THEN level the
            // ground flat to campCenter.y. The result was pieces above
            // campCenter.y floated, pieces below sank.
            //
            // New ordering: snapshot original terrain → level globally →
            // snap pieces to (now-flat) ground → spawn → per-piece pad.
            // SnapToGround queries the heightmap, which CampTerrain.Level
            // modifies directly, so subsequent snaps see the new flat
            // altitude and pieces sit flush regardless of where the
            // original hill was.
            //
            // Radius math: pieces are placed at chestRadius from the
            // anchor (which is max(config radius, blueprint footprint
            // + 1m)). Add minSep for the inner pad and an extra 4m for
            // a clean dirt buffer past the outermost piece. With a
            // default 5m PieceSpawnRadius and 2m minSep this ends at
            // 11m of dirt.
            float terrainRadius = chestRadius + minSep + 4f;
            string terrainSnapshotB64 = string.Empty;
            GameObject terrainGo = null;     // legacy fallback only
            if (spawnTerrain)
            {
                try
                {
                    var snap = CampTerrain.Snapshot(campCenter, terrainRadius);
                    terrainSnapshotB64 = CampTerrain.Serialize(snap);

                    // Level the entire camp footprint to the camp center's
                    // altitude. Smooth = 0.5 → linear falloff over the
                    // outer half of the radius so the edges aren't a
                    // hard cliff.
                    CampTerrain.Level(campCenter, terrainRadius, 0.5f, campCenter.y);

                    // Paint dirt across the area. Vanilla paint mask
                    // channels: R=dirt, G=paved, B=cultivated, A=blend.
                    // Full-dirt R=1 with 0.85 alpha lets the original
                    // texture break through at the edges for a softer
                    // transition.
                    CampTerrain.Paint(campCenter, terrainRadius, 0.5f, new Color(1f, 0f, 0f, 0.85f));

                    // Force-refresh any neighboring zone heightmap that
                    // shares a seam with the patched area. Without this,
                    // a camp placed near a zone boundary leaves a visible
                    // mesh gap where the patched compiler's seam vertex
                    // doesn't match the un-Poke'd neighbor's mesh.
                    CampTerrain.RefreshNeighborHeightmaps(campCenter, terrainRadius);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[VABackpacks] Direct terrain ops failed: {ex.Message}. Falling back to mud_road if configured.");
                    if (terrainPrefab != null)
                    {
                        terrainGo = UnityEngine.Object.Instantiate(terrainPrefab, campCenter, Quaternion.identity);
                        ApplyCampRadiusToTerrainModifier(terrainGo, terrainRadius);
                    }
                }

                // Vegetation suppression travels with the terrain patch —
                // no point suppressing veg if we didn't flatten the area.
                RegisterVegetationClearArea(campCenter, terrainRadius);
            }

            // Now snap each piece's y to ground. After the level pass
            // above, the entire footprint is flat at campCenter.y, so
            // every piece lands at the same altitude flush with the
            // dirt patch. When spawnTerrain is false the snap returns
            // each piece's local ground height (uneven), which is the
            // expected behavior with the dirt patch disabled.
            chestPos     = SnapToGround(chestPos);
            workbenchPos = SnapToGround(workbenchPos);
            bedPos       = SnapToGround(bedPos);
            woodpilePos  = SnapToGround(woodpilePos);
            chairPos     = SnapToGround(chairPos);
            firePos      = SnapToGround(firePos);

            // Spawn the chest (always). Fire and workbench respect their
            // blueprint toggles — without a blueprint both spawn; with a
            // blueprint each only spawns when its toggle is true.
            GameObject fireGo = spawnFire
                ? UnityEngine.Object.Instantiate(firePrefab, firePos, Quaternion.identity)
                : null;
            GameObject wbGo = spawnWorkbench
                ? UnityEngine.Object.Instantiate(wbPrefab, workbenchPos, Quaternion.Euler(0, UnityEngine.Random.Range(0f, 360f), 0))
                : null;
            var chestGo = UnityEngine.Object.Instantiate(chestPrefab, chestPos, Quaternion.Euler(0, UnityEngine.Random.Range(0f, 360f), 0));

            // Spawn optional decoration / utility pieces. Each get-or-skip
            // is tolerant of missing prefabs so a server can disable any
            // of them by emptying the config string.
            //
            // Bed orientation: face away from the campfire so the player
            // wakes up looking at it.
            GameObject bedGo = null;
            if (bedPrefab != null)
            {
                Quaternion bedRot = Quaternion.LookRotation(bedPos - firePos);
                bedGo = UnityEngine.Object.Instantiate(bedPrefab, bedPos, bedRot);
            }

            GameObject woodGo = woodPrefab != null
                ? UnityEngine.Object.Instantiate(woodPrefab, woodpilePos, Quaternion.Euler(0, UnityEngine.Random.Range(0f, 360f), 0))
                : null;

            // Tag the wood pile's ZDO with our marker so:
            //   1. The BackpackCampWoodPile component (added directly
            //      below) recognises it as a camp pile.
            //   2. The WearNTear.Awake re-attach patch fires when the
            //      wood pile re-spawns from a chunk reload.
            // Daily quota state (today's day + count) is initialized
            // lazily on the first Interact via TakenToday's rollover
            // path, so we don't need to seed it here.
            if (woodGo != null)
            {
                var woodNview = woodGo.GetComponent<ZNetView>();
                if (woodNview != null && woodNview.IsValid())
                {
                    woodNview.GetZDO()?.Set(BackpackCampWoodPile.KEY_MARKER, 1);
                }
                if (woodGo.GetComponent<BackpackCampWoodPile>() == null)
                    woodGo.AddComponent<BackpackCampWoodPile>();
            }

            // Chair faces the fire so sitting → looking at the flames.
            // The log bench's seat-forward axis (+Z) is the back of the
            // bench (where you'd lean a backrest), not the front. Pointing
            // +Z toward the fire would put the player's back to it; we
            // want +Z AWAY from the fire so the player sits facing in.
            GameObject chairGo = null;
            if (chairPrefab != null)
            {
                Quaternion chairRot = Quaternion.LookRotation(chairPos - firePos);
                chairGo = UnityEngine.Object.Instantiate(chairPrefab, chairPos, chairRot);
            }

            // Cooking station goes ON the fire (small Y offset) so it
            // gets heat for cooking. Random Y rotation just for variety.
            // Earlier 0.55m offset left the grill visibly floating
            // above the campfire — sitting just barely above ground
            // (0.05m) reads as "resting on the stones".
            GameObject cookGo = cookPrefab != null
                ? UnityEngine.Object.Instantiate(cookPrefab, firePos + Vector3.up * 0.05f, Quaternion.Euler(0, UnityEngine.Random.Range(0f, 360f), 0))
                : null;

            // ── Per-piece terrain pad (defensive layer) ──
            // The global level above flattens the whole camp footprint to
            // campCenter.y, so under normal conditions every spawned
            // piece is already flush. But edge cases — pieces near the
            // smooth-falloff zone, blueprint footprints that push the
            // chest unusually far out, weird heightmap states — can
            // leave a piece slightly below the leveled altitude.
            //
            // For each procedural piece we drop a small (1.5m radius)
            // local level pass at the piece's spawn altitude. Sharp
            // smooth (0.3) keeps the pad tight under the piece without
            // bleeding into neighbors. The original snapshot taken
            // above already covers these positions (they're well within
            // terrainRadius), so pack-up's Restore reverts the pads
            // along with the global level — no extra bookkeeping needed.
            //
            // Skipped entirely when spawnTerrain is false: with the dirt
            // patch disabled, the player explicitly opted out of any
            // ground reshaping.
            if (spawnTerrain)
            {
                const float PIECE_PAD_RADIUS = 1.5f;
                const float PIECE_PAD_SMOOTH = 0.3f;
                try
                {
                    if (fireGo  != null) CampTerrain.Level(firePos,      PIECE_PAD_RADIUS, PIECE_PAD_SMOOTH, firePos.y);
                    if (wbGo    != null) CampTerrain.Level(workbenchPos, PIECE_PAD_RADIUS, PIECE_PAD_SMOOTH, workbenchPos.y);
                    if (chestGo != null) CampTerrain.Level(chestPos,     PIECE_PAD_RADIUS, PIECE_PAD_SMOOTH, chestPos.y);
                    if (bedGo   != null) CampTerrain.Level(bedPos,       PIECE_PAD_RADIUS, PIECE_PAD_SMOOTH, bedPos.y);
                    if (woodGo  != null) CampTerrain.Level(woodpilePos,  PIECE_PAD_RADIUS, PIECE_PAD_SMOOTH, woodpilePos.y);
                    if (chairGo != null) CampTerrain.Level(chairPos,     PIECE_PAD_RADIUS, PIECE_PAD_SMOOTH, chairPos.y);
                    // Cooking station sits on top of the fire — no pad
                    // needed since the fire's pad already flattened the
                    // ground beneath both.

                    // Per-piece pads can also straddle a zone seam — refresh
                    // the wider area once at the end (single Poke per tile,
                    // cheaper than one per pad).
                    CampTerrain.RefreshNeighborHeightmaps(campCenter, terrainRadius);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[VABackpacks] Per-piece terrain pad failed: {ex.Message}");
                }
            }

            // Spawn blueprint pieces (if any). InfinityHammer's flow:
            //   1. Re-center the blueprint around its named center piece
            //      (or bbox center). Already done above when we resolved
            //      the blueprint, which also gave us bp.Radius for the
            //      chest keep-out distance.
            //   2. The campfire (firePos) becomes the placement origin in
            //      the world; we add the player's facing-yaw so the
            //      blueprint rotates with the player on deploy.
            //   3. For each piece: compute world pos/rot, instantiate,
            //      apply saved scale, apply data blob.
            //
            // CRITICAL: pieces are placed at their authored relative
            // positions (campYaw * piece.Pos + firePos) WITHOUT a
            // per-piece SnapToGround. Snapping each piece individually
            // would collapse the entire vertical structure to ground
            // height — walls, beams, and roofs all stacked at y=0
            // instead of preserving their authored heights. This is what
            // produced the "tower of crashed pieces" visual when the
            // blueprint was a real building.
            //
            // The blueprint root (anchor / firePos) IS snapped to ground
            // earlier in this method, so the whole structure rests on
            // the terrain at the campfire's altitude. Combined with the
            // dirt-leveling pass above (which already flattened the area
            // to firePos.y), pieces sit on flat terrain at their authored
            // y-offsets relative to the anchor.
            var blueprintZdoIds = new System.Collections.Generic.List<ZDOID>();
            if (useBlueprint)
            {
                // Arm the one-shot diagnostic dump for this deploy. The
                // first piece whose data blob fails to parse will log
                // its flags + failing section + raw byte preview, so we
                // can identify any format we don't yet handle without
                // spamming the log with hundreds of identical messages.
                EnableDataBlobFailureDump();

                Quaternion campYaw = Quaternion.LookRotation(forward);
                int spawned = 0, missing = 0, dataApplied = 0;

                // Per-backpack vertical offset — sinks the blueprint
                // into the ground (negative) or raises it (positive).
                // Useful when a blueprint was authored on flat terrain
                // but is being deployed on uneven ground, or to bury a
                // foundation slightly so its bottom edge is below the
                // grass line.
                float blueprintYOffset = 0f;
                if (ConfigManager.Instance.configBackpackCampBlueprintYOffset.TryGetValue(backpackPrefab, out var yCfg))
                    blueprintYOffset = yCfg.Value;

                foreach (var piece in blueprint.Objects)
                {
                    // Chance filter (PlanBuild blueprints can have probabilistic pieces).
                    if (piece.Chance < 1f && UnityEngine.Random.value > piece.Chance) continue;

                    var prefab = SafeGetPrefab(piece.Prefab);
                    if (prefab == null) { missing++; continue; }

                    // Authored offset — preserve y so vertical relationships
                    // between floors, walls, and roofs hold.
                    // Blueprint pieces center on the CAMP CENTER (the
                    // pushed-forward anchor), NOT firePos. With a blueprint
                    // in use firePos orbits the camp at chestRadius — using
                    // it as the blueprint origin is what was producing
                    // "fire under the blueprint" no matter how far we
                    // pushed the anchor forward.
                    Vector3 worldPos = campCenter + (campYaw * piece.Pos);
                    worldPos.y += blueprintYOffset;     // per-backpack vertical adjust
                    Quaternion worldRot = campYaw * piece.Rot;

                    var pieceGo = UnityEngine.Object.Instantiate(prefab, worldPos, worldRot);

                    // Apply blueprint-saved scale. Vanilla Valheim mostly
                    // ignores transform.localScale on pieces, but the scaler
                    // is what InfinityHammer uses; we mirror it for parity.
                    if (piece.Scale != Vector3.one && piece.Scale != Vector3.zero)
                    {
                        pieceGo.transform.localScale = piece.Scale;
                    }

                    // Try to apply the per-piece data blob (signs, container
                    // contents, fermenter state, etc.) to the piece's ZDO.
                    if (!string.IsNullOrEmpty(piece.Data))
                    {
                        if (TryApplyPieceData(pieceGo, piece.Data))
                            dataApplied++;
                    }

                    var pieceZ = GetZdoId(pieceGo);
                    if (pieceZ != ZDOID.None) blueprintZdoIds.Add(pieceZ);
                    spawned++;
                }
                Debug.Log($"[VABackpacks] Blueprint '{blueprint.Name}' spawned {spawned} pieces " +
                          $"({missing} missing prefabs, {dataApplied} data blobs applied, " +
                          $"{blueprint.SnapPoints.Count} snap points carried).");
            }

            // Capture ZDOIDs for everything we spawned (we'll need them on
            // pack-up).
            ZDOID fireZ    = GetZdoId(fireGo);
            ZDOID wbZ      = GetZdoId(wbGo);
            ZDOID chestZ   = GetZdoId(chestGo);
            ZDOID bedZ     = GetZdoId(bedGo);
            ZDOID woodZ    = GetZdoId(woodGo);
            ZDOID chairZ   = GetZdoId(chairGo);
            ZDOID cookZ    = GetZdoId(cookGo);
            ZDOID terrainZ = GetZdoId(terrainGo);

            // Configure the chest's marker.
            var marker = chestGo.AddComponent<BackpackCampMarker>();
            marker.OwnerPlayerId       = player.GetPlayerID();
            marker.CampfireZdoId       = fireZ;
            marker.WorkbenchZdoId      = wbZ;
            marker.BedZdoId            = bedZ;
            marker.WoodpileZdoId       = woodZ;
            marker.ChairZdoId          = chairZ;
            marker.CookingStationZdoId = cookZ;
            marker.TerrainModZdoId     = terrainZ;
            marker.BlueprintPieceZdoIds = blueprintZdoIds;
            marker.TerrainSnapshotBase64 = terrainSnapshotB64;
            marker.BackpackPrefabName  = backpackPrefab;
            marker.BackpackRows        = cfg.Rows;
            marker.BackpackColumns     = cfg.Columns;
            marker.CampCenter          = campCenter;     // blueprint center (or fire when procedural)
            marker.CampRadius          = terrainRadius;     // matches the dirt patch + blueprint footprint
            marker.WriteToZdo();
            BackpackCampRegistry.Register(marker);

            // Resize the chest's inventory to match the backpack and load
            // the saved contents from the dropped item's customData.
            ResizeContainer(chestGo, cfg.Rows, cfg.Columns);
            LoadBackpackContentsIntoContainer(chestGo, drop.m_itemData);

            // Restrict the chest to its creator. Vanilla Container has
            //   if (!CheckAccess(playerId)) deny;
            // and CheckAccess returns true on Private only when the
            // piece's GetCreator() matches the requesting player.
            // Result: only the camp's deployer can open the chest.
            // Other players see the standard "$piece_noaccess" message.
            try
            {
                var chestContainer = chestGo.GetComponent<Container>();
                if (chestContainer != null)
                    chestContainer.m_privacy = Container.PrivacySetting.Private;

                var chestPiece = chestGo.GetComponent<Piece>();
                if (chestPiece != null)
                {
                    // SetCreator is no-op if creator already non-zero or
                    // ZNetView isn't owned. The chest was just instantiated
                    // by us, so we own it and creator is 0 — this sticks.
                    chestPiece.SetCreator(player.GetPlayerID(), Splatform.PlatformManager.DistributionPlatform.LocalUser.PlatformUserID);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] Failed to set chest privacy/creator: {ex.Message}");
            }

            // Stick a visual copy of the backpack's mesh against the
            // chest so it reads as "your bag is still right there" at a
            // glance. Purely cosmetic — no collider, no ZNetView.
            AttachBackpackVisualToChest(chestGo, backpackPrefab);

            // Make every camp piece invulnerable so the camp can't be
            // griefed by other players or destroyed by mobs. Pack-up
            // bypasses this via ZNetView.Destroy(). The wood pile self-
            // destructs through its own E-interact path which also
            // bypasses damage — so taking wood still works.
            CampImmortalPatch.MakeImmortal(chestGo);
            CampImmortalPatch.MakeImmortal(fireGo);
            CampImmortalPatch.MakeImmortal(wbGo);
            CampImmortalPatch.MakeImmortal(bedGo);
            CampImmortalPatch.MakeImmortal(woodGo);
            CampImmortalPatch.MakeImmortal(chairGo);
            CampImmortalPatch.MakeImmortal(cookGo);
            CampImmortalPatch.MakeImmortal(terrainGo);
            // Blueprint pieces too — re-enumerate from the captured ZDOIDs.
            foreach (var z in blueprintZdoIds)
            {
                if (z == ZDOID.None) continue;
                var nv = ZNetScene.instance?.FindInstance(z);
                if (nv != null) CampImmortalPatch.MakeImmortal(nv.gameObject);
            }

            // Add a 2m hover-radius trigger so the player can engage the
            // chest's hover/interact text from anywhere within 2m, not
            // just when their crosshair lands on the small chest collider.
            AttachHoverTrigger(chestGo, 2f);

            // Mark the player as having an active camp.
            SetActiveCamp(player, chestZ);

            // Pin the camp on the deployer's minimap. save=true persists
            // it across logout (in the player's profile, not on the chest's
            // ZDO — only THIS player sees the pin). PinType.Bed gives a
            // bed-roll silhouette which reads cleanly as "rest stop".
            // The pack-up flow walks Minimap.m_pins and removes any
            // matching the camp center + name.
            try
            {
                // Use campCenter so the pin matches what pack-up's
                // Minimap.m_pins scan compares against (marker.CampCenter
                // is also campCenter), and so the icon points to the
                // structure's heart rather than a fire on the perimeter
                // when a blueprint is in use.
                Minimap.instance?.AddPin(campCenter, Minimap.PinType.Bed, "Camp",
                                         save: true, isChecked: false,
                                         ownerID: player.GetPlayerID());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] Failed to add camp map pin: {ex.Message}");
            }

            // Despawn the original ItemDrop (we've moved its data into
            // the chest). Use ZNetScene to destroy properly.
            DestroyZNetObject(drop.gameObject);

            // Show both Center and TopLeft messages so players get clear
            // feedback regardless of where their attention is.
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                $"Camp deployed: {backpackPrefab}");
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft,
                "Camp deployed");
        }

        /// <summary>
        /// Resolve a prefab from a comma-separated fallback chain. Tries
        /// each name in order, returns the first one ZNetScene knows about.
        /// Returns null if the chain is empty or none resolve.
        ///
        /// Used so config defaults like "rawood_bed_bal,bed" prefer
        /// Balrond's Amazing Nature prefab when that mod is loaded but
        /// gracefully fall back to vanilla otherwise.
        /// </summary>
        // Per-prefab "deploy already scheduled" guard. A mashed-MMB on
        // one weapon can't queue several deploys at once and consume
        // multiple copies in a single swing. Different slam-deploy
        // weapons can run simultaneously (rare but valid — e.g. one
        // HoboStick deploy mid-flight + a StaffFiresWizard MMB on the
        // same frame from a multi-arm script wouldn't deadlock).
        private readonly System.Collections.Generic.HashSet<string> _slamDeployPending
            = new System.Collections.Generic.HashSet<string>();

        /// <summary>
        /// Public entry from HoboStickAttackPatch — schedules the actual
        /// deploy for `delay` seconds in the future, so the swing's
        /// animation has time to slam down before the camp pops in.
        /// Subsequent calls for the same prefab while a deploy is pending
        /// are ignored. <paramref name="prefabName"/> identifies which
        /// slam-deploy weapon-bag to consume — must be a member of
        /// BackpackManager.SlamDeployBackpacks.
        /// </summary>
        public void RequestSlamDeploy(Player player, string prefabName, Vector3 targetPos, float delay)
        {
            if (player == null) return;
            if (string.IsNullOrEmpty(prefabName)) return;
            if (_slamDeployPending.Contains(prefabName)) return;
            _slamDeployPending.Add(prefabName);

            // Hard floor of 2s regardless of config. The sledge swing
            // animation lands its slam-impact frame around 1.5-1.8s into
            // the swing, so 2s lines the deploy up with the impact —
            // earlier than that the weapon is yanked out of the player's
            // hand mid-swing, which looks broken.
            // The previous 3s clamp made the deploy ~1s late relative
            // to the visible slam.
            float clamped = Mathf.Max(2f, delay);
            Debug.Log($"[VABackpacks] {prefabName} deploy scheduled in {clamped:F1}s (config={delay:F1}s, clamped to >=2s)");
            StartCoroutine(SlamDeployCoroutine(player, prefabName, targetPos, clamped));
        }

        /// <summary>
        /// Back-compat shim. Older callers hardcoded HoboStick — route
        /// them onto the generic path.
        /// </summary>
        public void RequestHoboStickDeploy(Player player, Vector3 targetPos, float delay)
            => RequestSlamDeploy(player, HoboStickAttackPatch.HOBOSTICK_PREFAB, targetPos, delay);

        private IEnumerator SlamDeployCoroutine(Player player, string prefabName, Vector3 targetPos, float delay)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);

            // Run the actual deploy as part of the coroutine so we can
            // yield mid-flow (the unequip → sync → clone path needs a
            // frame for VAInventory's BackpackStorageSync to settle).
            yield return DeploySlamBackpackAtCoroutine(player, prefabName, targetPos);

            _slamDeployPending.Remove(prefabName);
        }

        /// <summary>
        /// Special-case deploy used by the MMB attack on slam-deploy
        /// weapon-bags (HoboStick, StaffFiresWizard, …). Coroutine
        /// variant — yields between unequip and clone so any deferred
        /// VAInventory sync (BackpackStorageSync.HandleUnequipped writing
        /// the live bp-block items back into the weapon's customData)
        /// completes before we capture the weapon's data.
        ///
        /// Order of operations is the fix for two long-standing bugs:
        ///   A) "Camp chest comes up empty" — the old order cloned the
        ///      weapon BEFORE unequip, so the clone carried stale
        ///      customData (the live bp items were still parked in the
        ///      player's main grid by VAInventory). Unequip-then-clone
        ///      lets HandleUnequipped serialize the items into the
        ///      weapon's customData first, so the clone is fresh.
        ///   B) "Weapon stays in player's hand after deploy" — the
        ///      old RemoveItem call could silently fail (returns false)
        ///      and the deploy continued anyway, leaving the player
        ///      holding the weapon AND a duplicated chest. Now we
        ///      verify the removal and abort if it didn't take.
        /// </summary>
        private IEnumerator DeploySlamBackpackAtCoroutine(Player player, string prefabName, Vector3 targetPos)
        {
            if (player == null) yield break;
            if (string.IsNullOrEmpty(prefabName)) yield break;
            if (HasActiveCamp(player))
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                    "You already have a camp deployed. Pack it up first.");
                yield break;
            }

            var inv = player.GetInventory();
            if (inv == null) yield break;

            // Find the weapon-bag instance in the player's inventory by
            // prefab-name match.
            ItemDrop.ItemData weapon = null;
            foreach (var it in inv.GetAllItems())
            {
                if (it?.m_dropPrefab != null && it.m_dropPrefab.name == prefabName)
                {
                    weapon = it;
                    break;
                }
            }
            if (weapon == null)
            {
                Debug.LogWarning($"[VABackpacks] {prefabName} deploy: no instance in inventory.");
                yield break;
            }

            // STEP 1: Unequip first. This is the critical re-order. When
            // the weapon is bp-tagged, VAInventory's BackpackStorageSync
            // (or VABackpacks's legacy unequip path) listens for the
            // unequip event and serializes the bp-block items into the
            // weapon's customData. We can't clone the weapon until that
            // has run, or we'd take a stale snapshot and the chest
            // would come up empty.
            bool wasEquipped = player.IsItemEquiped(weapon);
            if (wasEquipped)
            {
                try { player.UnequipItem(weapon); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[VABackpacks] {prefabName} unequip threw: {ex.Message}");
                }
            }

            // Yield exactly one frame so any deferred sync work
            // (Inventory.Changed events, BackpackStorageSync.HandleUnequipped's
            // add/remove pass, VABackpacks legacy customData write-back)
            // gets a tick to complete before we capture the weapon's data.
            // Used to be 0.5s but the user asked to trim that beat —
            // one frame is enough on this Valheim build because the
            // sync listeners run synchronously inside Inventory.Changed,
            // not on a coroutine.
            yield return null;

            // STEP 2: Spawn a fresh ItemDrop at the target.
            var prefab = ZNetScene.instance?.GetPrefab(prefabName);
            if (prefab == null)
            {
                Debug.LogError($"[VABackpacks] {prefabName} deploy: prefab missing from ZNetScene.");
                yield break;
            }

            var dropGo = UnityEngine.Object.Instantiate(prefab, targetPos, Quaternion.identity);
            var drop = dropGo.GetComponent<ItemDrop>();
            if (drop == null)
            {
                UnityEngine.Object.Destroy(dropGo);
                Debug.LogError($"[VABackpacks] {prefabName} deploy: spawned prefab has no ItemDrop.");
                yield break;
            }

            // STEP 3: Capture the weapon's data into the drop AFTER the
            // sync ran. We use Clone() for the bulk of the fields, then
            // FORCE a fresh deep-copy of m_customData on top. Earlier
            // bug: the weapon's customData was written async by the
            // unequip listener; if Clone()'s shallow dictionary copy
            // happened mid-write we'd get a partial / empty bag in the
            // chest. Re-copying after the wait above guarantees we read
            // the weapon's fully-populated final state.
            try
            {
                drop.m_itemData = weapon.Clone();

                // Re-populate the cloned customData with the weapon's
                // current dictionary so we never miss a late write.
                drop.m_itemData.m_customData = weapon.m_customData != null
                    ? new System.Collections.Generic.Dictionary<string, string>(weapon.m_customData)
                    : new System.Collections.Generic.Dictionary<string, string>();

                // Diagnostic — confirms the bp inventory blob actually
                // round-tripped into the drop. If this logs "size=0"
                // when you have a full bag, the unequip-sync didn't
                // run before the wait expired.
                int blobSize = 0;
                if (drop.m_itemData.m_customData.TryGetValue(BackpackApi.CUSTOM_DATA_KEY, out var blob))
                    blobSize = blob?.Length ?? 0;
                Debug.Log($"[VABackpacks] {prefabName} deploy: captured customData with " +
                          $"{drop.m_itemData.m_customData.Count} entries, BackpackInventory blob size={blobSize}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] {prefabName} deploy clone failed: {ex.Message}");
            }

            // STEP 4: Remove from inventory + verify. If RemoveItem
            // returns false, abort the deploy so we don't end up with a
            // duplicated weapon (one in hand + one as the new camp).
            bool removed = false;
            try
            {
                removed = inv.RemoveItem(weapon);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VABackpacks] {prefabName} RemoveItem threw: {ex.Message}");
            }

            if (!removed)
            {
                Debug.LogError($"[VABackpacks] {prefabName} deploy aborted: RemoveItem failed. " +
                               "The weapon is still in inventory; not duplicating it.");
                DestroyZNetObject(drop.gameObject);
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                    $"Camp deploy failed — could not consume {prefabName}.");
                yield break;
            }

            // The hand visual refreshes naturally on the next Player
            // animator tick after the unequip + RemoveItem above. If a
            // build hits a one-frame lingering visual, fix it here with
            // a player.m_visEquipment.UpdateEquipmentVisuals() (if that
            // method exists on this Valheim build).

            // STEP 5: Run the standard deploy. It consumes the spawned
            // drop and builds the camp around it.
            //
            // BELT-AND-SUSPENDERS: even if DeployFromItemDrop throws
            // partway (e.g. an NRE in AttachBackpackVisualToChest), we
            // MUST guarantee the spawned drop is destroyed. Otherwise
            // the drop sits on the ground at targetPos, the player
            // auto-picks it up, and the weapon magically reappears in
            // their inventory after a "successful" deploy — exactly
            // the symptom the ZSyncTransform NRE was producing.
            bool deployFailed = false;
            try
            {
                DeployFromItemDrop(drop);
            }
            catch (Exception ex)
            {
                deployFailed = true;
                Debug.LogError($"[VABackpacks] {prefabName} deploy failed: {ex}");
            }

            if (deployFailed && drop != null && drop.gameObject != null)
            {
                try { DestroyZNetObject(drop.gameObject); }
                catch { /* defensive — already torn down somehow */ }
            }
        }

        /// <summary>
        /// Synchronous shim for any external caller wanting to trigger a
        /// slam-deploy from code. Hops onto the coroutine path so the
        /// same correctness behavior (unequip-then-clone, RemoveItem
        /// verify) applies.
        /// </summary>
        public bool DeploySlamBackpackAt(Player player, string prefabName, Vector3 targetPos)
        {
            if (player == null) return false;
            if (string.IsNullOrEmpty(prefabName)) return false;
            StartCoroutine(DeploySlamBackpackAtCoroutine(player, prefabName, targetPos));
            return true;
        }

        /// <summary>
        /// Back-compat shim — older callers hardcoded HoboStick.
        /// </summary>
        public bool DeployHoboStickAt(Player player, Vector3 targetPos)
            => DeploySlamBackpackAt(player, HoboStickAttackPatch.HOBOSTICK_PREFAB, targetPos);

        private static GameObject SafeGetPrefab(string nameOrChain)
        {
            if (string.IsNullOrEmpty(nameOrChain)) return null;
            var scene = ZNetScene.instance;
            if (scene == null) return null;

            // Single-name fast path
            if (nameOrChain.IndexOf(',') < 0)
            {
                return scene.GetPrefab(nameOrChain.Trim());
            }

            // Comma-separated chain — first match wins.
            foreach (var part in nameOrChain.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length == 0) continue;
                var p = scene.GetPrefab(trimmed);
                if (p != null) return p;
            }
            return null;
        }

        private static ZDOID GetZdoId(GameObject go)
        {
            if (go == null) return ZDOID.None;
            var nv = go.GetComponent<ZNetView>();
            return nv?.GetZDO()?.m_uid ?? ZDOID.None;
        }

        /// <summary>
        /// Clone the backpack prefab's mesh + materials onto a child of
        /// the chest, sized down and tilted slightly so it reads as the
        /// backpack leaning against the chest. Strips ItemDrop / Rigidbody
        /// / Collider / ZNetView from the clone so it can't be picked up
        /// or interfere with physics — purely visual.
        /// </summary>
        private static void AttachBackpackVisualToChest(GameObject chestGo, string backpackPrefabName)
        {
            if (chestGo == null || string.IsNullOrEmpty(backpackPrefabName)) return;
            var prefab = ZNetScene.instance?.GetPrefab(backpackPrefabName);
            if (prefab == null) return;

            // CRITICAL: temporarily deactivate the source prefab before
            // Instantiate. If we don't, Unity calls Awake() on the clone
            // immediately when Instantiate returns — and ItemDrop.Awake /
            // ZNetView.Awake REGISTER A ZDO with ZDOMan. Even after we
            // Destroy() those components, the ZDO persists in the world
            // save and respawns a fresh, real backpack ItemDrop next to
            // the chest on chunk reload / log-back-in. That's the
            // "duplicate bag dropped inside the camp" bug.
            //
            // SetActive(false) on the prefab BEFORE Instantiate means the
            // clone starts inactive, no Awake runs, no ZDO is created,
            // and we can safely strip its game-logic components before
            // ever activating it.
            bool prefabWasActive = prefab.activeSelf;
            try
            {
                if (prefabWasActive) prefab.SetActive(false);

                var visual = UnityEngine.Object.Instantiate(prefab, chestGo.transform);
                visual.name = "VA_BackpackVisual";

                // Position: place the bag NEXT to the chest, not under it.
                // The chest's local mesh occupies roughly ±0.5m on X,
                // ±0.4m on Z, so an offset of 0.85 X / 0.3 Z puts the
                // bag clear of the chest's footprint and resting on the
                // ground beside it.
                visual.transform.localPosition = new Vector3(0.85f, 0f, 0.30f);
                // Tilt: lean against the chest's side (slight X tilt)
                // and a Y rotation so the bag's strap-side faces away
                // from the camera angle most players use.
                visual.transform.localRotation = Quaternion.Euler(-15f, 200f, 0f);
                visual.transform.localScale    = Vector3.one * 0.8f;

                // Strip every component that would make this a real
                // game object. DestroyImmediate on the inactive clone is
                // safe and guarantees the ZNetView/ItemDrop are GONE
                // before SetActive(true) triggers Awake on the clone.
                StripGameLogic(visual, immediate: true);

                // Force-enable every renderer + mesh holder. Armor-style
                // bag prefabs (capes, shoulder bags) often start with
                // their model GameObject disabled and rely on
                // VisEquipment to switch it on when worn. Without this,
                // the visual clone would be invisible for any non-
                // weapon bag — only HoboStick (a TwoHandedWeapon with
                // an always-on root mesh) would render.
                EnableAllVisuals(visual);

                // Now safe to activate — every behaviour that would have
                // run Awake has been removed.
                visual.SetActive(true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] Failed to attach backpack visual: {ex.Message}");
            }
            finally
            {
                // Always restore the prefab's active state, even if our
                // setup threw mid-flow — leaving Valheim's prefab inactive
                // would break everyone else's ability to spawn it.
                if (prefabWasActive && !prefab.activeSelf) prefab.SetActive(true);
            }
        }

        /// <summary>
        /// Attach a wide trigger collider to the chest so the player's
        /// hover ray hits it when they're near the camp, even if their
        /// crosshair isn't pointed at the chest's visual mesh. Pure UX —
        /// the player no longer has to "find" the chest to pack the camp.
        /// </summary>
        private static void AttachHoverTrigger(GameObject chestGo, float radius)
        {
            if (chestGo == null) return;
            // Don't double-add if Awake already ran and we're rehydrating.
            var existing = chestGo.transform.Find("VA_HoverTrigger");
            if (existing != null) return;

            var triggerGo = new GameObject("VA_HoverTrigger");
            triggerGo.transform.SetParent(chestGo.transform, false);
            triggerGo.transform.localPosition = Vector3.zero;
            // The trigger needs to be on a layer the player's hover ray
            // reads. Vanilla uses "piece" or "item"; we mirror the chest's
            // own layer so any layer-mask gating sees us correctly.
            triggerGo.layer = chestGo.layer;

            var sphere = triggerGo.AddComponent<SphereCollider>();
            sphere.radius = radius;
            sphere.isTrigger = true;
        }

        /// <summary>
        /// Recursively strip components that would make a cloned prefab
        /// behave like a "real" object: pickup logic, physics,
        /// interaction colliders, network sync, AND the visual/audio
        /// drop FX (the floating glow particles, pickup-light, and
        /// "ping" sound that vanilla ItemDrops emit).
        /// Renderers, MeshFilters, and the Transform hierarchy survive.
        /// </summary>
        private static void StripGameLogic(GameObject root, bool immediate = false)
        {
            if (root == null) return;

            // When `immediate` is true we use DestroyImmediate, which
            // removes the component synchronously. That's required when
            // the caller is about to SetActive(true) — without it, the
            // queued Destroy hasn't run yet by the time Awake() fires
            // on the (still-existing) ZNetView/ItemDrop, and they
            // register a ZDO that respawns the prefab on chunk reload.
            System.Action<UnityEngine.Object> kill = immediate
                ? (UnityEngine.Object o) => UnityEngine.Object.DestroyImmediate(o)
                : (UnityEngine.Object o) => UnityEngine.Object.Destroy(o);

            // Game-logic components.
            //
            // ORDERING MATTERS: kill ZSyncTransform / ZSimpleSync FIRST,
            // BEFORE ZNetView. Both depend on a live ZNetView in their
            // Awake() to find the network owner — if we strip ZNetView
            // first and then SetActive(true), ZSyncTransform.Awake fires
            // and NRE's on the missing ZNetView reference. That NRE
            // aborts the entire deploy mid-flow, leaving the spawned
            // ItemDrop on the ground for the player to auto-pick-up
            // (the original "stick stays in inventory after deploy" bug).
            foreach (var c in root.GetComponentsInChildren<ZSyncTransform>(true)) kill(c);
            foreach (var c in root.GetComponentsInChildren<ItemDrop>(true))         kill(c);
            foreach (var c in root.GetComponentsInChildren<ZNetView>(true))         kill(c);
            foreach (var c in root.GetComponentsInChildren<Rigidbody>(true))        kill(c);
            foreach (var c in root.GetComponentsInChildren<Collider>(true))         kill(c);
            foreach (var c in root.GetComponentsInChildren<Floating>(true))         kill(c);
            foreach (var c in root.GetComponentsInChildren<TimedDestruction>(true)) kill(c);

            // Drop-FX components — the glowy beam, sparkles, and pickup
            // sound that make a dropped item visible in tall grass. We
            // don't want any of them on a stationary chest decoration.
            //
            // ORDERING MATTERS again: LightLod / LightFlicker are
            // Valheim-side companions that depend on the Light component
            // — both NRE in their Awake() if the Light has already been
            // destroyed. Kill the companions FIRST so they're gone
            // before EnableAllVisuals reactivates their parent GameObject
            // (StaffFiresWizard's glowing-tip child carries this stack).
            foreach (var c in root.GetComponentsInChildren<LightLod>(true))         kill(c);
            foreach (var c in root.GetComponentsInChildren<LightFlicker>(true))     kill(c);
            foreach (var c in root.GetComponentsInChildren<Light>(true))            kill(c);
            foreach (var c in root.GetComponentsInChildren<ParticleSystem>(true))   kill(c);
            foreach (var c in root.GetComponentsInChildren<AudioSource>(true))      kill(c);
            foreach (var c in root.GetComponentsInChildren<AudioListener>(true))    kill(c);
        }

        /// <summary>
        /// Walk the prefab hierarchy and force every GameObject that
        /// holds a renderer to be active + enabled. Armor-equip prefabs
        /// (capes, shoulder bags) often ship with the model child
        /// disabled — vanilla VisEquipment switches it on when the item
        /// is equipped on a player. For our purely-visual chest decoration
        /// we don't run VisEquipment, so we have to flip it on ourselves
        /// or the bag would be invisible.
        /// </summary>
        private static void EnableAllVisuals(GameObject root)
        {
            if (root == null) return;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                r.enabled = true;
                var go = r.gameObject;
                // Walk up to the visual root, enabling each ancestor's
                // GameObject. Stop at the clone root — we already did
                // its SetActive(true) on the outside.
                var t = go.transform;
                while (t != null && t.gameObject != root)
                {
                    if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                    t = t.parent;
                }
            }
        }

        /// <summary>
        /// Apply an InfinityHammer-style data blob to a freshly-spawned
        /// piece's ZDO. The blob carries things like sign text, chest
        /// inventory, fermenter progress, plant growth, fireplace fuel,
        /// container contents, etc.
        ///
        /// Format reference: WorldEditCommands' Data.DataEntry.Load(ZPackage):
        /// the blob is a base64-encoded ZPackage with this layout —
        ///
        ///   int  flags
        ///   if (flags &amp; 0x001) byte n; for n: int key, float  value
        ///   if (flags &amp; 0x002) byte n; for n: int key, Vector3 value
        ///   if (flags &amp; 0x004) byte n; for n: int key, Quaternion value
        ///   if (flags &amp; 0x008) byte n; for n: int key, int    value (Ints+Hashes+Bools merged)
        ///   if (flags &amp; 0x040) byte n; for n: int key, long   value     ← read BEFORE strings
        ///   if (flags &amp; 0x010) byte n; for n: int key, string value
        ///   if (flags &amp; 0x080) byte n; for n: int key, byte[] value
        ///   if (flags &amp; 0x100) byte connectionType, int connectionHash
        ///   if (flags &amp; 0x200) bool persistent
        ///   if (flags &amp; 0x400) bool distant
        ///   if (flags &amp; 0x800) byte priority
        ///
        /// CRITICAL: even though the bit number for Longs (0x40) is higher
        /// than for Strings (0x10), the SERIALIZED ORDER puts longs first.
        /// Reading them in bit-value order (strings before longs) was the
        /// bug that caused "Unable to read beyond the end of the stream"
        /// on every piece that had both — string parser ate long bytes,
        /// then long parser ran off the end of the buffer.
        ///
        /// Note: this is NOT the same as vanilla ZDO.Deserialize (which
        /// expects a full ZDO header with prefab/sector/rotation first).
        /// Calling ZDO.Deserialize on this blob is what was producing
        /// "Unable to read beyond the end of the stream" for every piece.
        ///
        /// On any exception we log + return false; the piece is still
        /// placed, just without its custom state.
        /// </summary>
        // One-shot flag — when set, the next failing TryApplyPieceData
        // logs an in-depth byte trace (which section it was reading,
        // how far in, and the flags it saw). Lets us diagnose blob
        // formats we don't yet handle without spamming hundreds of
        // identical messages. Reset between deploys.
        private static bool _logNextDataBlobFailure;
        public static void EnableDataBlobFailureDump() => _logNextDataBlobFailure = true;

        private static bool TryApplyPieceData(GameObject pieceGo, string base64Data)
        {
            if (pieceGo == null || string.IsNullOrEmpty(base64Data)) return false;
            string failingSection = "header";
            int flagsForLog = 0;
            try
            {
                var nview = pieceGo.GetComponent<ZNetView>();
                if (nview == null) return false;
                var zdo = nview.GetZDO();
                if (zdo == null) return false;

                var pkg = new ZPackage(base64Data);
                pkg.SetPos(0);

                int flags = pkg.ReadInt();
                flagsForLog = flags;

                if ((flags & 0x001) != 0)
                {
                    failingSection = "floats";
                    byte n = pkg.ReadByte();
                    for (int i = 0; i < n; i++)
                    {
                        int key = pkg.ReadInt();
                        float v = pkg.ReadSingle();
                        zdo.Set(key, v);
                    }
                }
                if ((flags & 0x002) != 0)
                {
                    failingSection = "vecs";
                    byte n = pkg.ReadByte();
                    for (int i = 0; i < n; i++)
                    {
                        int key = pkg.ReadInt();
                        Vector3 v = pkg.ReadVector3();
                        zdo.Set(key, v);
                    }
                }
                if ((flags & 0x004) != 0)
                {
                    failingSection = "quats";
                    byte n = pkg.ReadByte();
                    for (int i = 0; i < n; i++)
                    {
                        int key = pkg.ReadInt();
                        Quaternion v = pkg.ReadQuaternion();
                        zdo.Set(key, v);
                    }
                }
                if ((flags & 0x008) != 0)
                {
                    // Ints + Hashes + Bools all share the int storage.
                    failingSection = "ints";
                    byte n = pkg.ReadByte();
                    for (int i = 0; i < n; i++)
                    {
                        int key = pkg.ReadInt();
                        int v = pkg.ReadInt();
                        zdo.Set(key, v);
                    }
                }
                // Longs (0x40) come BEFORE Strings (0x10) in the serialized
                // byte stream — see DataEntry.Load order. Reading these in
                // bit-numerical order is what was producing the "Unable to
                // read beyond the end of the stream" spam.
                if ((flags & 0x040) != 0)
                {
                    failingSection = "longs";
                    byte n = pkg.ReadByte();
                    for (int i = 0; i < n; i++)
                    {
                        int key = pkg.ReadInt();
                        long v = pkg.ReadLong();
                        zdo.Set(key, v);
                    }
                }
                if ((flags & 0x010) != 0)
                {
                    failingSection = "strings";
                    byte n = pkg.ReadByte();
                    for (int i = 0; i < n; i++)
                    {
                        int key = pkg.ReadInt();
                        string v = pkg.ReadString();
                        zdo.Set(key, v);
                    }
                }
                if ((flags & 0x080) != 0)
                {
                    failingSection = "bytes";
                    byte n = pkg.ReadByte();
                    for (int i = 0; i < n; i++)
                    {
                        int key = pkg.ReadInt();
                        byte[] v = pkg.ReadByteArray();
                        zdo.Set(key, v);
                    }
                }
                if ((flags & 0x100) != 0)
                {
                    // Connection (portal pairs, spawned-by relationships).
                    // Skip — we can't usefully resolve cross-piece IDs from
                    // a fresh deploy. Just consume the bytes so subsequent
                    // sections still align.
                    failingSection = "connection";
                    pkg.ReadByte();   // connection type
                    pkg.ReadInt();    // connection hash
                }
                if ((flags & 0x200) != 0)
                {
                    failingSection = "persistent";
                    bool persistent = pkg.ReadBool();
                    zdo.Persistent = persistent;
                }
                if ((flags & 0x400) != 0)
                {
                    failingSection = "distant";
                    bool distant = pkg.ReadBool();
                    zdo.Distant = distant;
                }
                if ((flags & 0x800) != 0)
                {
                    failingSection = "priority";
                    byte priority = pkg.ReadByte();
                    zdo.Type = (ZDO.ObjectType)priority;
                }

                zdo.IncreaseDataRevision();
                return true;
            }
            catch (Exception ex)
            {
                // First failure of this deploy: dump full diagnostic info
                // (flags + failing section + raw bytes preview) so we can
                // identify any format we don't yet handle. Subsequent
                // failures use the terse warning to avoid log spam.
                if (_logNextDataBlobFailure)
                {
                    _logNextDataBlobFailure = false;
                    string preview = base64Data.Length > 80 ? base64Data.Substring(0, 80) + "..." : base64Data;
                    Debug.LogError($"[VABackpacks] DataBlob FIRST-FAIL on {pieceGo?.name}: " +
                                   $"section='{failingSection}', flags=0x{flagsForLog:X4} ({flagsForLog}), " +
                                   $"blob_len={base64Data.Length}, preview={preview}, ex={ex.Message}");
                }
                Debug.LogWarning($"[VABackpacks] Couldn't apply data blob to {pieceGo?.name}: {ex.Message} (in section '{failingSection}')");
                return false;
            }
        }

        /// <summary>
        /// Resize a freshly-spawned TerrainModifier's level/smooth/paint
        /// radii to cover the camp area. Vanilla prefabs ship with small
        /// (~2m) radii — we want the dirt patch to reach every piece.
        ///
        /// Must run BEFORE the modifier's first Awake/OnEnable applies
        /// the operation, or the original radius gets baked into the
        /// terrain. Instantiate doesn't trigger the heightmap modify
        /// until the next physics tick, so setting radii right after
        /// Instantiate is in time.
        /// </summary>
        private static void ApplyCampRadiusToTerrainModifier(GameObject go, float radius)
        {
            if (go == null) return;
            var mod = go.GetComponent<TerrainModifier>();
            if (mod == null) return;
            mod.m_levelRadius  = radius;
            mod.m_smoothRadius = radius;
            mod.m_paintRadius  = radius;
            // Square=false gives a circular footprint matching the camp's
            // shape better than the default square level area.
            mod.m_square = false;
        }

        // ── Pack-up ───────────────────────────────────────────────────

        private void PackUpCamp(BackpackCampMarker marker)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            if (marker == null) return;

            // Ownership check (admin override would go here).
            if (marker.OwnerPlayerId != player.GetPlayerID())
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                    "This camp belongs to someone else.");
                return;
            }

            // Save the chest's contents back into a backpack item's
            // customData blob, then spawn that item on the ground.
            var chestGo = marker.gameObject;
            var container = chestGo.GetComponent<Container>();
            string base64 = SerializeContainerToBase64(container);

            // Resolve the original backpack prefab so we can spawn a
            // matching ItemDrop with the saved storage.
            var backpackPrefab = ZNetScene.instance?.GetPrefab(marker.BackpackPrefabName);
            if (backpackPrefab == null)
            {
                Debug.LogError($"[VABackpacks] Pack-up failed: prefab '{marker.BackpackPrefabName}' missing.");
                return;
            }

            // Hand the backpack data back to the player. Two paths:
            //
            //   A. PREFERRED — they already have one of this backpack
            //      type in their inventory (e.g. a previous deploy
            //      didn't fully consume it, or HoboStick MMB-deploy
            //      had a partial failure last session). Just merge the
            //      chest's contents into that existing item's
            //      customData. NEVER spawn a duplicate — that's the
            //      bug we're guarding against.
            //
            //   B. FALLBACK — they have nothing matching. Spawn a fresh
            //      ItemDrop on the ground above the chest with the
            //      contents stamped into its customData.
            //
            // This eliminates the "duplicated HoboStick after logout/
            // login" issue: even if the original removal silently failed
            // last session, pack-up now reconciles instead of doubling.
            bool gaveToInventory = false;
            try
            {
                var inv = player.GetInventory();
                if (inv != null)
                {
                    foreach (var existing in inv.GetAllItems())
                    {
                        if (existing?.m_dropPrefab == null) continue;
                        if (existing.m_dropPrefab.name != marker.BackpackPrefabName) continue;

                        if (existing.m_customData == null)
                            existing.m_customData = new System.Collections.Generic.Dictionary<string, string>();
                        if (!string.IsNullOrEmpty(base64))
                            existing.m_customData[BackpackApi.CUSTOM_DATA_KEY] = base64;
                        gaveToInventory = true;
                        Debug.Log($"[VABackpacks] Pack-up: merged contents into existing {marker.BackpackPrefabName} in player inventory (no duplicate spawned).");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] Pack-up inventory-merge check failed: {ex.Message}");
            }

            if (!gaveToInventory)
            {
                Vector3 spawnPos = chestGo.transform.position + Vector3.up * 0.5f;
                var newDrop = UnityEngine.Object.Instantiate(backpackPrefab, spawnPos, Quaternion.identity);
                var newItem = newDrop.GetComponent<ItemDrop>();
                if (newItem != null && newItem.m_itemData != null)
                {
                    if (newItem.m_itemData.m_customData == null)
                        newItem.m_itemData.m_customData = new System.Collections.Generic.Dictionary<string, string>();
                    if (!string.IsNullOrEmpty(base64))
                        newItem.m_itemData.m_customData[BackpackApi.CUSTOM_DATA_KEY] = base64;
                }
            }

            // Restore the heightmap + paint mask from the snapshot we
            // took at deploy. This is the WEC undo pattern — exact
            // pre-deploy state, no rogue TerrainModifier piece needed.
            // (If terrainSnapshotBase64 is empty, we ran the legacy
            // mud_road fallback at deploy and rely on its destroy below.)
            if (!string.IsNullOrEmpty(marker.TerrainSnapshotBase64))
            {
                try
                {
                    var snap = CampTerrain.Deserialize(marker.TerrainSnapshotBase64);
                    if (snap != null) CampTerrain.Restore(snap);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[VABackpacks] Terrain restore failed: {ex.Message}");
                }
            }

            // CLEAR THE PLAYER'S BOUND SPAWN BEFORE we destroy the bed.
            // Vanilla Bed.IsCurrent() reads m_spawnPoint.position, and
            // Game.FindSpawnPoint walks every Bed in the scene calling
            // IsCurrent. Once we've destroyed the bed's GameObject the
            // m_spawnPoint Transform is gone too — but if any code path
            // queries the bed before it's torn down (or a half-stripped
            // copy lingers because of immortality flags), IsCurrent NREs.
            // World load then fails with the trace the user reported.
            //
            // Strategy: snapshot the bed's spawn-point position now,
            // then call Game.RemoveCustomSpawnPoint(point) — vanilla
            // only clears the profile entry if it actually matches
            // (`==` on Vector3 is exact-component equality), so a
            // separate base bed the player slept in earlier remains
            // bound. If the live bed lookup fails (already gone), fall
            // back to a radius-based clear against the camp center so
            // any stale binding from this camp is reset regardless.
            try
            {
                Vector3? bedSpawnPos = null;
                if (marker.BedZdoId != ZDOID.None)
                {
                    var bedNview = ZNetScene.instance?.FindInstance(marker.BedZdoId);
                    var bedComp  = bedNview?.GetComponent<Bed>()
                                ?? bedNview?.GetComponentInChildren<Bed>();
                    if (bedComp != null && bedComp.m_spawnPoint != null)
                        bedSpawnPos = bedComp.GetSpawnPoint();
                }

                var profile = Game.instance?.GetPlayerProfile();
                if (profile != null && profile.HaveCustomSpawnPoint())
                {
                    if (bedSpawnPos.HasValue)
                    {
                        Game.instance.RemoveCustomSpawnPoint(bedSpawnPos.Value);
                    }
                    else
                    {
                        // Bed already gone — fall back to radius check.
                        Vector3 spawn = profile.GetCustomSpawnPoint();
                        float dx = spawn.x - marker.CampCenter.x;
                        float dz = spawn.z - marker.CampCenter.z;
                        float r  = marker.CampRadius;
                        if (dx * dx + dz * dz <= r * r)
                            profile.ClearCustomSpawnPoint();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] Pre-destroy spawn clear failed: {ex.Message}");
            }

            // Despawn every linked piece by ZDOID. Order matters slightly:
            //   - terrain modifier (legacy fallback) first so the ground
            //     starts restoring immediately and the player isn't
            //     standing on a hole when the chest disappears.
            //   - cooking station before campfire (it sits on the fire).
            //   - everything else can drop in any order.
            DestroyByZdoId(marker.TerrainModZdoId);
            DestroyByZdoId(marker.CookingStationZdoId);
            DestroyByZdoId(marker.CampfireZdoId);
            DestroyByZdoId(marker.WorkbenchZdoId);
            DestroyByZdoId(marker.BedZdoId);
            DestroyByZdoId(marker.WoodpileZdoId);
            DestroyByZdoId(marker.ChairZdoId);
            // Blueprint-spawned pieces (if a blueprint was used) — same
            // destroy-by-ZDOID treatment.
            if (marker.BlueprintPieceZdoIds != null)
            {
                foreach (var bpId in marker.BlueprintPieceZdoIds)
                    DestroyByZdoId(bpId);
            }

            // Defense-in-depth: sweep the camp radius for any piece
            // tagged with our immortal flag and destroy it. This catches
            // pieces whose ZDOID linkage broke between sessions (the
            // bed/fire-left-behind bug after logout/login). Every piece
            // we spawn at deploy gets the immortal tag, so this is a
            // tight match — non-camp pieces in the area are unaffected
            // because they don't carry the tag.
            try
            {
                float scanRadius = Mathf.Max(marker.CampRadius + 2f, 8f);
                var hits = Physics.OverlapSphere(marker.CampCenter, scanRadius);
                foreach (var hit in hits)
                {
                    if (hit == null) continue;
                    var nview = hit.GetComponentInParent<ZNetView>();
                    if (nview == null || !nview.IsValid()) continue;
                    var zdo = nview.GetZDO();
                    if (zdo == null) continue;
                    if (zdo.GetInt(CampImmortalPatch.KEY_IMMORTAL, 0) != 1) continue;
                    // Don't destroy the chest here — it's handled below
                    // after the map-pin removal so the marker survives
                    // until we're done reading from it.
                    if (nview.gameObject == chestGo) continue;
                    DestroyZNetObject(nview.gameObject);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] Pack-up radius-scan cleanup failed: {ex.Message}");
            }

            // Remove the camp's map pin (only on the local player who
            // placed it — vanilla pins are local-profile-scoped). We
            // walk m_pins for any pin named "Camp" within ~1m of the
            // camp center and drop it. A radius-only match would also
            // hit any manual pins the player placed near the camp,
            // which we don't want.
            try
            {
                if (Minimap.instance != null)
                {
                    const float MATCH_RADIUS_SQ = 2f * 2f;   // ~2m tolerance
                    var pins = Minimap.instance.m_pins;
                    for (int i = pins.Count - 1; i >= 0; i--)
                    {
                        var pin = pins[i];
                        if (pin == null) continue;
                        if (pin.m_name != "Camp") continue;
                        float dx = pin.m_pos.x - marker.CampCenter.x;
                        float dz = pin.m_pos.z - marker.CampCenter.z;
                        if (dx * dx + dz * dz > MATCH_RADIUS_SQ) continue;
                        Minimap.instance.RemovePin(pin);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] Failed to remove camp map pin: {ex.Message}");
            }

            // Unregister and despawn the chest itself last.
            BackpackCampRegistry.Unregister(marker);
            UnregisterVegetationClearArea(marker.CampCenter);
            DestroyZNetObject(chestGo);

            // Reset grass over the camp footprint so the area visually
            // matches the now-restored heightmap (tall grass blades that
            // were hidden under the camp pieces re-render correctly).
            try { ClutterSystem.instance?.ResetGrass(marker.CampCenter, marker.CampRadius); }
            catch { /* defensive — clutter might be unloading */ }

            ClearActiveCamp(player);

            // (Spawn-point clearing happens up-front, BEFORE the bed is
            // destroyed — see "CLEAR THE PLAYER'S BOUND SPAWN BEFORE…"
            // block above. Doing it here, after destruction, was the
            // cause of vanilla FindSpawnPoint NREs on next world load:
            // half-torn-down beds were still being queried.)

            // Center + TopLeft feedback so the action reads from any
            // attention focus.
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                "Camp packed up");
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft,
                "Camp packed up");
        }

        // ── Vegetation suppression (ZoneSystem.ClearArea) ─────────────
        //
        // ZoneSystem keeps a private List<ClearArea> (m_tempClearAreas)
        // that PlaceVegetation reads when populating a fresh zone. Any
        // veg point inside an active ClearArea is skipped. We mirror that
        // pattern so deployed camps don't get reclaimed by trees/grass
        // when their host zone refreshes.
        //
        // The list is process-only (vanilla rebuilds it from location
        // instances each generation pass), so we rebuild ours from the
        // BackpackCampRegistry on hydrate too — see HydrateFromZdo.

        private static readonly System.Collections.Generic.Dictionary<Vector3, float> s_clearAreas = new();

        public static void RegisterVegetationClearArea(Vector3 center, float radius)
        {
            s_clearAreas[center] = radius;
        }

        public static void UnregisterVegetationClearArea(Vector3 center)
        {
            s_clearAreas.Remove(center);
        }

        /// <summary>True if <paramref name="point"/> is in any registered
        /// camp clear-area. Called by the ZoneSystem patch to suppress
        /// veg-spawn inside camps.</summary>
        public static bool IsPointInVegetationClearArea(Vector3 point)
        {
            foreach (var kv in s_clearAreas)
            {
                float dx = point.x - kv.Key.x;
                float dz = point.z - kv.Key.z;
                if (dx * dx + dz * dz <= kv.Value * kv.Value) return true;
            }
            return false;
        }

        // ── One-camp-per-player tracking ──────────────────────────────

        private static bool HasActiveCamp(Player player)
        {
            if (player == null) return false;
            if (player.m_customData == null) return false;
            if (!player.m_customData.TryGetValue(KEY_PLAYER_CAMP, out var raw) || string.IsNullOrEmpty(raw))
                return false;

            var zdoId = ParseZdoId(raw);
            if (zdoId == ZDOID.None) { player.m_customData.Remove(KEY_PLAYER_CAMP); return false; }

            var zdo = ZDOMan.instance?.GetZDO(zdoId);
            if (zdo == null)
            {
                // Stale — the camp was destroyed externally. Clear it.
                player.m_customData.Remove(KEY_PLAYER_CAMP);
                return false;
            }
            return true;
        }

        private static void SetActiveCamp(Player player, ZDOID id)
        {
            if (player?.m_customData == null) return;
            player.m_customData[KEY_PLAYER_CAMP] = $"{id.UserID}:{id.ID}";
        }

        private static void ClearActiveCamp(Player player)
        {
            player?.m_customData?.Remove(KEY_PLAYER_CAMP);
        }

        private static ZDOID ParseZdoId(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return ZDOID.None;
            int colon = raw.IndexOf(':');
            if (colon < 1 || colon == raw.Length - 1) return ZDOID.None;
            if (!long.TryParse(raw.Substring(0, colon), out long userId)) return ZDOID.None;
            if (!uint.TryParse(raw.Substring(colon + 1), out uint id)) return ZDOID.None;
            return new ZDOID(userId, id);
        }

        // ── Spawn-position helpers ────────────────────────────────────

        /// <summary>
        /// Pick a point at the given radius from the anchor, in a random
        /// direction, that's at least <paramref name="minSep"/> away from
        /// every "occupied" point. Falls back to a deterministic angle
        /// after a few retries to avoid an infinite loop.
        /// </summary>
        private static Vector3 PickOffsetPosition(Vector3 anchor, float radius, float minSep, params Vector3[] occupied)
        {
            for (int i = 0; i < 16; i++)
            {
                float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                Vector3 candidate = anchor + offset;

                bool ok = true;
                foreach (var p in occupied)
                {
                    if (Vector3.Distance(candidate, p) < minSep) { ok = false; break; }
                }
                if (ok) return candidate;
            }
            // Last-resort: opposite direction of the previous occupied point.
            if (occupied.Length > 0)
            {
                Vector3 away = (anchor - occupied[occupied.Length - 1]).normalized * radius;
                return anchor + away;
            }
            return anchor + new Vector3(radius, 0f, 0f);
        }

        /// <summary>
        /// Like PickOffsetPosition but at a FIXED radius from the anchor.
        /// Used for the chair which the user wants exactly N meters from
        /// the fire. Sweeps angles to find a non-overlapping spot; falls
        /// back to "180° from the closest occupied point" if every angle
        /// collides.
        /// </summary>
        private static Vector3 PickFixedRadiusPosition(Vector3 anchor, float radius,
                                                       float minSep, params Vector3[] occupied)
        {
            for (int i = 0; i < 24; i++)
            {
                float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                Vector3 candidate = anchor + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;

                bool ok = true;
                foreach (var p in occupied)
                {
                    if (Vector3.Distance(candidate, p) < minSep) { ok = false; break; }
                }
                if (ok) return candidate;
            }

            // Fallback: opposite direction of the closest occupied point.
            Vector3 closest = anchor;
            float closestDist = float.MaxValue;
            foreach (var p in occupied)
            {
                float d = Vector3.Distance(p, anchor);
                if (d < closestDist) { closestDist = d; closest = p; }
            }
            Vector3 away = (anchor - closest);
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
            else away.Normalize();
            return anchor + away * radius;
        }

        private static Vector3 SnapToGround(Vector3 pos)
        {
            if (ZoneSystem.instance != null
                && ZoneSystem.instance.GetGroundHeight(pos, out float groundY))
            {
                return new Vector3(pos.x, groundY, pos.z);
            }
            return pos;
        }

        // ── Container plumbing ────────────────────────────────────────

        private static void ResizeContainer(GameObject chestGo, int rows, int cols)
        {
            if (chestGo == null || rows <= 0 || cols <= 0) return;
            var container = chestGo.GetComponent<Container>();
            if (container == null) return;
            container.m_width  = cols;
            container.m_height = rows;

            try
            {
                var inv = container.GetInventory();
                if (inv == null) return;
                var wField = typeof(Inventory).GetField("m_width",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var hField = typeof(Inventory).GetField("m_height",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                wField?.SetValue(inv, cols);
                hField?.SetValue(inv, rows);
            }
            catch (Exception ex) { Debug.LogWarning($"[VABackpacks] ResizeContainer: {ex.Message}"); }
        }

        private static void LoadBackpackContentsIntoContainer(GameObject chestGo, ItemDrop.ItemData backpackItem)
        {
            if (chestGo == null || backpackItem == null)
            {
                Debug.LogWarning($"[VABackpacks] LoadBackpackContentsIntoContainer: chestGo={chestGo!=null}, backpackItem={backpackItem!=null} — bailing.");
                return;
            }
            var container = chestGo.GetComponent<Container>();
            if (container == null)
            {
                Debug.LogWarning("[VABackpacks] LoadBackpackContentsIntoContainer: chest has no Container component.");
                return;
            }
            var inv = container.GetInventory();
            if (inv == null)
            {
                Debug.LogWarning("[VABackpacks] LoadBackpackContentsIntoContainer: container.GetInventory() returned null.");
                return;
            }

            string base64 = backpackItem.m_customData != null
                && backpackItem.m_customData.TryGetValue(BackpackApi.CUSTOM_DATA_KEY, out var s)
                ? s : null;
            if (string.IsNullOrEmpty(base64))
            {
                Debug.LogWarning($"[VABackpacks] LoadBackpackContentsIntoContainer: drop's customData has no '{BackpackApi.CUSTOM_DATA_KEY}' key " +
                                 $"(customData entries: {(backpackItem.m_customData != null ? backpackItem.m_customData.Count : 0)}). " +
                                 $"Chest will spawn empty — the unequip-sync didn't save the bag.");
                return;
            }
            Debug.Log($"[VABackpacks] LoadBackpackContentsIntoContainer: loading {base64.Length} chars of base64 into chest ({inv.GetWidth()}x{inv.GetHeight()}).");

            try
            {
                var pkg = new ZPackage(base64);
                inv.Load(pkg);
                Debug.Log($"[VABackpacks] LoadBackpackContentsIntoContainer: loaded {inv.GetAllItems().Count} items into chest.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] Failed to load backpack contents into chest: {ex.Message}");
            }
        }

        private static string SerializeContainerToBase64(Container container)
        {
            if (container == null) return string.Empty;
            var inv = container.GetInventory();
            if (inv == null) return string.Empty;
            try
            {
                var pkg = new ZPackage();
                inv.Save(pkg);
                return pkg.GetBase64();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] SerializeContainer failed: {ex.Message}");
                return string.Empty;
            }
        }

        // ── Despawn helpers ───────────────────────────────────────────

        private static void DestroyByZdoId(ZDOID id)
        {
            if (id == ZDOID.None) return;
            var zdo = ZDOMan.instance?.GetZDO(id);
            if (zdo == null) return;
            // Resolve to a live ZNetView if any; otherwise tombstone the
            // ZDO directly so the world save doesn't keep it.
            var nview = ZNetScene.instance?.FindInstance(zdo);
            if (nview != null) DestroyZNetObject(nview.gameObject);
            else ZDOMan.instance?.DestroyZDO(zdo);
        }

        private static void DestroyZNetObject(GameObject go)
        {
            if (go == null) return;
            var nview = go.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid())
            {
                if (nview.IsOwner()) nview.Destroy();
                else { nview.ClaimOwnership(); nview.Destroy(); }
                return;
            }
            UnityEngine.Object.Destroy(go);
        }
    }
}
