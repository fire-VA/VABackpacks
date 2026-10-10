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
using System.Collections.Generic;
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
            if (!_pinsCleaned && Minimap.instance != null) { _pinsCleaned = true; CleanCampPins(Player.m_localPlayer, null, "load"); }
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

            // 1.0.19 (Fire 10-07): HOLD E on a dropped backpack deploys its camp, tap E picks it up.
            if (HandleHoldUse()) return;

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
            // 1.0.43: never while a text field has input (the hammer filter, chat, a Fires panel).
            bool gestureHeld = shift && secondKey && !BackpackManager.IsFiresInputCaptured();

            if (!gestureHeld)
            {
                ResetGesture();
                return;
            }

            // No camp-eligible target → don't accumulate.
            if (_deployTarget == null && _packTarget == null)
            {
                _holdStart = 0f;
                // 1.0.46: say once per hold why nothing armed (no bag or camp chest looked at, within reach, or ring).
                if (!_noTargetLogged)
                {
                    _noTargetLogged = true;
                    GestureLog.LogDebug($"camp gesture held: no target (no backpack drop or camp chest in the crosshair or within {ConfigManager.Instance.configCampDeployRadius.Value:0.#} m, and not inside one of your camp rings)");
                }
                return;
            }

            // 1.0.34 (Fire: "Packing camp... 0.6s" kept rolling after the pack): once the action fired, the held keys do
            // nothing more until they are let go; before, the reset right after the action restarted the timer while held.
            if (_fired) return;

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

            if (_deployTarget != null && !_fired) RequestDropOwnership(_deployTarget);   // 1.0.25: the deploy takes the drop as its owner

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
                // _fired stays set until the keys are released (ResetGesture on release).
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

            // 1.0.46 ([visual]'s camp_carry: Shift+B by the camp did nothing, the tester was past 3 m from the chest and not
            // looking at it): the owner standing inside their own camp's save ring is targeting that camp.
            if (newDeploy == null && newPack == null && Player.m_localPlayer != null)
            {
                var me = Player.m_localPlayer;
                Vector3 pos = me.transform.position;
                foreach (var m in BackpackCampRegistry.All)
                {
                    if (m == null || m.OwnerPlayerId != me.GetPlayerID()) continue;
                    Vector3 d = pos - CampRing.Centre(m);
                    float rr = CampRing.Radius(m);
                    if (d.x * d.x + d.z * d.z <= rr * rr) { newPack = m; break; }
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

        // ── Hold E on a dropped backpack (1.0.19) ─────────────────────
        //
        // Vanilla picks an ItemDrop up on the Use keydown, before any hold could be seen. CampInteractPatches'
        // ItemDrop.Interact prefix holds that first press back for a backpack (BeginHoldUse) and this loop decides:
        // let go before the camp hold time = the pickup vanilla would have done (AllowPickup lets it through once);
        // held for the hold time = the camp, the same DeployFromItemDrop the Shift+key gesture uses.
        private static ItemDrop _useDrop;
        private static float _useStart;
        private static bool _useFired;
        internal static bool AllowPickup;
        private const float UseProgressGrace = 0.35f;   // no progress text for an ordinary tap

        internal static void BeginHoldUse(ItemDrop drop)
        {
            _useDrop = drop;
            _useStart = Time.unscaledTime;
            _useFired = false;
            // 1.0.25: ask for the drop the vanilla way (ItemDrop.RequestOwn, as a pickup does) while E is held, so the
            // deploy can take it out of the world as its owner.
            RequestDropOwnership(drop);
        }

        private static void RequestDropOwnership(ItemDrop drop)
        {
            if (drop == null) return;
            var nview = drop.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid() && !nview.IsOwner()) drop.RequestOwn();
        }

        /// <summary>True while a hold-E is in progress (the Shift+key gesture waits).</summary>
        private bool HandleHoldUse()
        {
            if (_useDrop == null) return false;
            Player player = Player.m_localPlayer;
            if (player == null || _useDrop.m_itemData == null) { _useDrop = null; return false; }
            bool down = ZInput.GetButton("Use") || ZInput.GetButton("JoyUse");
            float duration = ConfigManager.Instance.configCampHoldDuration.Value;
            float elapsed = Time.unscaledTime - _useStart;
            if (!down)
            {
                ItemDrop drop = _useDrop;
                _useDrop = null;
                if (_useFired) return false;
                AllowPickup = true;   // a tap: the pickup vanilla would have done
                var tapView = drop != null ? drop.GetComponent<ZNetView>() : null;
                ZDOID tapId = tapView != null && tapView.IsValid() ? tapView.GetZDO().m_uid : ZDOID.None;
                try { drop.Interact(player, false, false); }
                finally { AllowPickup = false; }
                // 1.0.31 (round 14 pickup 'drop gone False'): vanilla's pickup destroys the drop as its owner, which only QUEUES
                // the ZDO (see DestroyedOrQueued); the line says which it is, and the 2 s check confirms it stays gone.
                if (tapId != ZDOID.None)
                {
                    bool gone = DestroyedOrQueued(tapId);
                    Debug.Log($"[VABackpacks] tap pickup: drop {tapId} {(gone ? "destroyed (gone or queued)" : "still in the world (not owner yet; vanilla asked for it)")}");
                    if (gone) StartCoroutine(CheckPickupStaysGone(tapId));
                }
                return false;
            }
            if (_useFired) return true;
            RequestDropOwnership(_useDrop);   // RequestOwn backs off by itself
            if (elapsed >= UseProgressGrace)
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, $"Deploying camp... {Mathf.Max(0f, duration - elapsed):F1}s");
            if (elapsed < duration) return true;
            _useFired = true;
            try { DeployFromItemDrop(_useDrop); }
            catch (Exception ex) { Debug.LogError($"[VABackpacks] Camp deploy (hold E) failed: {ex}"); }
            return true;
        }

        private void ResetGesture()
        {
            _holdStart = 0f;
            _fired = false;
            _noTargetLogged = false;
        }

        private static bool _noTargetLogged;
        private static readonly BepInEx.Logging.ManualLogSource GestureLog = BepInEx.Logging.Logger.CreateLogSource("VABackpacks.CampGesture");

        // ── Deploy ────────────────────────────────────────────────────

        // 1.0.25 (Fire 10-08: a camp from a dropped bag left the bag on the ground too). The drop used to be destroyed as the
        // LAST step, after the whole camp was built, by claiming its ZDO locally: an exception anywhere in the build, or a
        // destroy that did not take (a drop another peer owns: ZNetScene.Destroy only deletes the ZDO of an owner, so the
        // object came back on the next CreateDestroyObjects), left the bag AND the camp with its contents. Now the drop is
        // taken out of the world FIRST, only by its owner (RequestOwn during the hold, as vanilla's pickup does), and its ZDO
        // is checked gone before anything is built; a failed build is rolled back and the bag is dropped again, once.
        private bool DeployFromItemDrop(ItemDrop drop)
        {
            var player = Player.m_localPlayer;
            if (player == null) return false;
            if (drop == null || drop.m_itemData == null) return false;

            // One-camp-per-player check
            if (HasActiveCamp(player))
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                    "You already have a camp deployed. Pack it up first.");
                return false;
            }

            string backpackPrefab = drop.m_itemData.m_dropPrefab?.name;
            if (string.IsNullOrEmpty(backpackPrefab) || !BackpackApi.IsBackpack(backpackPrefab))
            {
                Debug.LogWarning($"[VABackpacks] DeployFromItemDrop called on non-backpack: {backpackPrefab}");
                return false;
            }

            var cfg = BackpackApi.GetConfig(backpackPrefab);
            if (cfg == null)
            {
                Debug.LogWarning($"[VABackpacks] Backpack {backpackPrefab} has no config registered.");
                return false;
            }

            var pcfg = ConfigManager.Instance;
            if (ZNetScene.instance?.GetPrefab(pcfg.configCampCampfirePrefab.Value) == null
                || ZNetScene.instance?.GetPrefab(pcfg.configCampWorkbenchPrefab.Value) == null
                || ZNetScene.instance?.GetPrefab(pcfg.configCampContainerPrefab.Value) == null)
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                    "Camp deploy failed: prefabs missing (check config).");
                Debug.LogError("[VABackpacks] Camp deploy: a campfire/workbench/container prefab from the config is missing; nothing deployed.");
                return false;
            }

            // 1.0.35 (Fire: "make sure placement checks prevent overlapping pieces"): the camp and its saved layout come back
            // exactly as packed, so the site must be clear of other buildings; nothing is placed on top of them.
            {
                Vector3 fwd = player.transform.forward; fwd.y = 0f;
                fwd = fwd.sqrMagnitude < 0.01f ? Vector3.forward : fwd.normalized;
                Vector3 site = drop.transform.position + fwd * pcfg.configCampFireOffsetForward.Value;
                // 1.0.37 (Fire: refused on empty ground, "65 building piece(s) within 40 m, nearest sfx_unarmed_swing"): the
                // camp footprint + 2 m, not the level radius, and only real player-built pieces count.
                float clear = pcfg.configCampRadius.Value + pcfg.configCampMinSeparation.Value + 2f;
                var near = new System.Collections.Generic.List<Piece>();
                Piece.GetAllPiecesInRadius(site, clear, near);
                var blocking = near.Where(IsRealBuilding).ToList();
                if (blocking.Count > 0)
                {
                    var first = blocking.OrderBy(pc => (pc.transform.position - site).sqrMagnitude).First();
                    Debug.Log($"[VABackpacks] camp deploy refused: {blocking.Count} building piece(s) within {clear:0} m of the camp site, nearest {Utils.GetPrefabName(first.gameObject)} at {Vector3.Distance(first.transform.position, site):0.0} m");
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"Camp NOT deployed: {blocking.Count} building piece(s) within {clear:0} m, nearest {Utils.GetPrefabName(first.gameObject)} {Vector3.Distance(first.transform.position, site):0.0} m away. Move somewhere clear.");
                    return false;
                }
            }

            // Take the drop out of the world first, as its owner.
            var dropView = drop.GetComponent<ZNetView>();
            if (dropView == null || !dropView.IsValid())
            {
                Debug.LogWarning($"[VABackpacks] camp from dropped bag: the {backpackPrefab} drop has no live ZDO; nothing deployed.");
                return false;
            }
            ZDOID dropId = dropView.GetZDO().m_uid;
            if (!dropView.IsOwner())
            {
                drop.RequestOwn();
                Debug.Log($"[VABackpacks] camp from dropped bag: drop {dropId} is still owned by peer {dropView.GetZDO().GetOwner()}; " +
                          "ownership requested, nothing deployed.");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "The bag is still syncing. Hold E on it again.");
                return false;
            }
            ItemDrop.ItemData item = drop.m_itemData.Clone();
            Vector3 dropPos = drop.transform.position;
            Quaternion dropRot = drop.transform.rotation;
            ZNetScene.instance.Destroy(drop.gameObject);
            if (!DestroyedOrQueued(dropId))
            {
                // The local object is gone but the ZDO is not: the drop comes back on its own, so nothing was taken.
                Debug.LogError($"[VABackpacks] camp from dropped bag: drop {dropId} is still in the world after the destroy; nothing deployed.");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "Camp deploy failed: the bag could not be taken.");
                return false;
            }

            var state = new DeployState();
            try
            {
                int moved = BuildCamp(player, item, dropPos, backpackPrefab, cfg, state);
                Debug.Log($"[VABackpacks] camp from dropped bag: drop {dropId} destroyed, contents {moved} moved");
                StartCoroutine(CheckDropStaysGone(dropId));
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VABackpacks] Camp deploy failed, rolling back: {ex}");
                RollBackDeploy(player, state);
                ItemDrop back = ItemDrop.DropItem(item, 0, dropPos, dropRot);
                Debug.Log($"[VABackpacks] camp from dropped bag: deploy rolled back, {backpackPrefab} back on the ground as {GetZdoId(back != null ? back.gameObject : null)} (drop {dropId} was taken)");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "Camp deploy failed. Your bag is back on the ground.");
                return false;
            }
        }

        /// <summary>What a deploy created, so a failed one can be undone.</summary>
        private sealed class DeployState
        {
            public readonly System.Collections.Generic.List<GameObject> Spawned = new System.Collections.Generic.List<GameObject>();
            public string TerrainSnapshot = string.Empty;
            public string GroundBefore = string.Empty;   // 1.0.36: FAT voxel snapshot of the site
            public bool VoxelGround;   // 1.0.38: FAT owns the ground (levels, pads, restore)
            public float SaveRadius;   // 1.0.40
            public bool VegetationRegistered;
            public Vector3 Center;
            public float Yaw;   // 1.0.35: the camp facing
            public BackpackCampMarker Marker;
            public bool ActiveCampSet;
            public bool PinAdded;
        }

        private static void RollBackDeploy(Player player, DeployState st)
        {
            foreach (var go in st.Spawned)
            {
                try { if (go != null) DestroyZNetObject(go); }
                catch (Exception ex) { Debug.LogWarning($"[VABackpacks] Rollback: could not remove {go?.name}: {ex.Message}"); }
            }
            try
            {
                if (!string.IsNullOrEmpty(st.TerrainSnapshot))
                {
                    var snap = CampTerrain.Deserialize(st.TerrainSnapshot);
                    if (snap != null) CampTerrain.Restore(snap);
                }
                CampTerrainCarry.RestoreGround(st.GroundBefore, st.Center, st.Yaw, "rollback");
            }
            catch (Exception ex) { Debug.LogWarning($"[VABackpacks] Rollback: terrain restore failed: {ex.Message}"); }
            try { if (st.VegetationRegistered) UnregisterVegetationClearArea(st.Center); } catch { }
            try { if (st.Marker != null) BackpackCampRegistry.Unregister(st.Marker); } catch { }
            try { if (st.ActiveCampSet) ClearActiveCamp(player); } catch { }
            try
            {
                if (st.PinAdded && Minimap.instance != null)
                {
                    var pins = Minimap.instance.m_pins;
                    for (int i = pins.Count - 1; i >= 0; i--)
                    {
                        var pin = pins[i];
                        if (pin == null || pin.m_name != "Camp") continue;
                        float dx = pin.m_pos.x - st.Center.x, dz = pin.m_pos.z - st.Center.z;
                        if (dx * dx + dz * dz <= 4f) Minimap.instance.RemovePin(pin);
                    }
                }
            }
            catch { }
            Debug.Log($"[VABackpacks] Rollback: removed {st.Spawned.Count} camp object(s)");
        }

        // 1.0.28: ZNetScene.Destroy only QUEUES the owner's ZDO (ZDOMan.DestroyZDO adds it to m_destroySendList; the ZDO is
        // removed and the destroy sent to peers on a later ZDOMan update), so GetZDO is still non-null in the same frame. That
        // made 1.0.26 refuse every deploy and pack-up (round 13). Gone or queued for destroy by us both mean it will not come back.
        private static readonly System.Reflection.FieldInfo DestroySendListField = HarmonyLib.AccessTools.Field(typeof(ZDOMan), "m_destroySendList");

        internal static bool DestroyedOrQueued(ZDOID id)
        {
            var zm = ZDOMan.instance;
            if (zm == null || zm.GetZDO(id) == null) return true;
            return DestroySendListField?.GetValue(zm) is System.Collections.Generic.List<ZDOID> queued && queued.Contains(id);
        }

        private System.Collections.IEnumerator CheckPickupStaysGone(ZDOID id)
        {
            yield return new WaitForSeconds(2f);
            bool back = ZDOMan.instance?.GetZDO(id) != null && !DestroyedOrQueued(id) || ZNetScene.instance?.FindInstance(id) != null;
            Debug.Log($"[VABackpacks] tap pickup: drop {id} {(back ? "CAME BACK after 2 s" : "still gone after 2 s")}");
        }

        // A drop that comes back after the camp took its bag (a ZDO a peer re-sent) is a duplicate: remove it again.
        private System.Collections.IEnumerator CheckDropStaysGone(ZDOID id)
        {
            yield return new WaitForSeconds(2f);
            var zdo = ZDOMan.instance?.GetZDO(id);
            var inst = ZNetScene.instance?.FindInstance(id);
            if ((zdo == null || DestroyedOrQueued(id)) && inst == null)
            {
                Debug.Log($"[VABackpacks] camp from dropped bag: drop {id} still gone after 2 s");
                yield break;
            }
            Debug.LogWarning($"[VABackpacks] camp from dropped bag: drop {id} CAME BACK (zdo {zdo != null}, object {inst != null}); removing it again");
            if (inst != null) DestroyZNetObject(inst.gameObject);
            else if (zdo != null) { zdo.SetOwner(ZDOMan.GetSessionID()); ZDOMan.instance.DestroyZDO(zdo); }
        }

        private int BuildCamp(Player player, ItemDrop.ItemData item, Vector3 dropPos, string backpackPrefab, BackpackConfig cfg, DeployState st)
        {
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
                // Checked before the drop was taken; a throw here rolls the deploy back and gives the bag back.
                throw new InvalidOperationException($"Missing camp prefabs fire={firePrefab != null} wb={wbPrefab != null} chest={chestPrefab != null}");
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
                // 1.0.41 (Fire: the CHEST is the camp centre): the fire sits beside the chest.
                firePos     = PickOffsetPosition(anchor, radius * 0.5f, minSep, occupied.ToArray()); occupied.Add(firePos);
                // woodpile placeholder — picked below at radius*0.7.
                woodpilePos = anchor;
            }
            // 1.0.41 (Fire): the chest IS the camp centre; every saved offset is measured from it.
            Vector3 chestPos     = anchor;
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
            // 1.0.40 (Fire: edits outside the saved ring were lost; pieces went to 40 m, terrain only 11 m): ONE save radius, the
            // camp level's, for pieces, plants, terrain capture, the pre-camp snapshot and the restore. The camp workbench shows it.
            // Capped at FAT 0.2.863's 48 m capture limit (Fire: "cap it at what FAT supports").
            float saveRadius = Mathf.Min(48f, Mathf.Max(terrainRadius, CampLevels.Radius(CampLevels.Level(item))));
            st.SaveRadius = saveRadius;
            string terrainSnapshotB64 = string.Empty;
            GameObject terrainGo = null;     // legacy fallback only
            if (spawnTerrain)
            {
                try
                {
                    // 1.0.36: on voxel ground the heightmap snapshot below cannot put the site back, so FAT takes one too.
                    st.GroundBefore = CampTerrainCarry.Capture(campCenter, CampLayout.YawOf(forward), saveRadius) ?? string.Empty;
                    // 1.0.38 ([worldgen]: on a voxel world the voxel field IS the drawn ground; TerrainComp height does not show, and
                    // the old heightmap level left a raised ring behind after pack-up). A site FAT could capture is voxel ground:
                    // FAT levels and paints it, and the pack-up puts snapshot0 back. Heightmap worlds keep the old path.
                    st.VoxelGround = !string.IsNullOrEmpty(st.GroundBefore) && CampTerrainCarry.CanLevel;
                    if (st.VoxelGround)
                        CampTerrainCarry.Level(campCenter, terrainRadius, campCenter.y, 1, "camp level");
                    else
                    {
                    var snap = CampTerrain.Snapshot(campCenter, terrainRadius);
                    terrainSnapshotB64 = CampTerrain.Serialize(snap);
                    st.TerrainSnapshot = terrainSnapshotB64;

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
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[VABackpacks] Direct terrain ops failed: {ex.Message}. Falling back to mud_road if configured.");
                    if (terrainPrefab != null)
                    {
                        terrainGo = UnityEngine.Object.Instantiate(terrainPrefab, campCenter, Quaternion.identity);
                        st.Spawned.Add(terrainGo);
                        ApplyCampRadiusToTerrainModifier(terrainGo, terrainRadius);
                    }
                }

                // Vegetation suppression travels with the terrain patch —
                // no point suppressing veg if we didn't flatten the area.
                RegisterVegetationClearArea(campCenter, Mathf.Max(terrainRadius, CampLevels.Radius(CampLevels.Level(item))));   // 1.0.29: the clearing follows the camp level
                st.VegetationRegistered = true;
            }
            st.Center = campCenter;
            // 1.0.36: the ground the player shaped at the last camp, stamped around this one at this facing.
            CampTerrainCarry.Apply(item, campCenter, CampLayout.YawOf(forward));

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

            // 1.0.35: a saved layout puts every camp piece back where it stood, turned to this deploy's facing.
            float campYaw = CampLayout.YawOf(forward);
            st.Yaw = campYaw;
            var layout = CampLayout.Read(item);
            Quaternion fireRot = Quaternion.identity;
            Quaternion wbRot = Quaternion.Euler(0, UnityEngine.Random.Range(0f, 360f), 0);
            Quaternion chestRot = Quaternion.identity;   // 1.0.41 (Fire): the chest always faces north
            Quaternion woodRot = Quaternion.Euler(0, UnityEngine.Random.Range(0f, 360f), 0);
            Quaternion? bedRotSaved = null, chairRotSaved = null, cookRotSaved = null;
            Vector3? cookPosSaved = null;
            int fromLayout = 0;
            bool Saved(string role, out Vector3 pos, out Quaternion rot)
            {
                pos = default; rot = Quaternion.identity;
                if (!layout.TryGetValue(role, out var spot)) return false;
                pos = CampLayout.WorldPos(campCenter, campYaw, spot);
                rot = CampLayout.WorldRot(campYaw, spot);
                fromLayout++;
                return true;
            }
            { if (Saved("fire", out var p, out var r)) { firePos = p; fireRot = r; } }
            { if (Saved("workbench", out var p, out var r)) { workbenchPos = p; wbRot = r; } }
            // 1.0.41: the chest is the centre and faces north; a saved chest spot (older bags) is not used.
            { if (Saved("woodpile", out var p, out var r)) { woodpilePos = p; woodRot = r; } }
            { if (Saved("bed", out var p, out var r)) { bedPos = p; bedRotSaved = r; } }
            { if (Saved("chair", out var p, out var r)) { chairPos = p; chairRotSaved = r; } }
            { if (Saved("cook", out var p, out var r)) { cookPosSaved = p; cookRotSaved = r; } }
            // Fire 10-08 ("stay removed"): with a saved layout, a camp piece the player removed with the hammer is not in it
            // and does not come back. The chest always does (it is the camp; it cannot be hammer-removed).
            if (layout.Count > 0)
            {
                var gone = new System.Collections.Generic.List<string>();
                if (spawnFire && !layout.ContainsKey("fire")) { spawnFire = false; gone.Add("fire"); }
                if (spawnWorkbench && !layout.ContainsKey("workbench")) { spawnWorkbench = false; gone.Add("workbench"); }
                if (bedPrefab != null && !layout.ContainsKey("bed")) { bedPrefab = null; gone.Add("bed"); }
                if (woodPrefab != null && !layout.ContainsKey("woodpile")) { woodPrefab = null; gone.Add("woodpile"); }
                if (chairPrefab != null && !layout.ContainsKey("chair")) { chairPrefab = null; gone.Add("chair"); }
                if (cookPrefab != null && !layout.ContainsKey("cook")) { cookPrefab = null; gone.Add("cook"); }
                if (gone.Count > 0) Debug.Log($"[VABackpacks] camp layout: removed by the player, not placed: {string.Join(", ", gone)}");
            }
            Debug.Log(layout.Count == 0
                ? $"[VABackpacks] camp layout: none saved in the bag; camp pieces placed around the fire (facing {campYaw:0})"
                : $"[VABackpacks] camp layout: {fromLayout} camp piece(s) from the bag's saved layout, turned to facing {campYaw:0}");

            // Spawn the chest (always). Fire and workbench respect their
            // blueprint toggles — without a blueprint both spawn; with a
            // blueprint each only spawns when its toggle is true.
            GameObject fireGo = spawnFire
                ? UnityEngine.Object.Instantiate(firePrefab, firePos, fireRot)
                : null;
            GameObject wbGo = spawnWorkbench
                ? UnityEngine.Object.Instantiate(wbPrefab, workbenchPos, wbRot)
                : null;
            var chestGo = UnityEngine.Object.Instantiate(chestPrefab, chestPos, chestRot);
            st.Spawned.Add(fireGo);
            st.Spawned.Add(wbGo);
            // 1.0.35 (Fire: could not build at the camp without nobuild): the workbench must be a working vanilla station.
            if (wbGo != null)
            {
                var station = wbGo.GetComponent<CraftingStation>();
                Debug.Log(station == null
                    ? $"[VABackpacks] camp workbench: {wbPrefab.name} has NO CraftingStation; it gives no build range"
                    : $"[VABackpacks] camp workbench: {station.m_name}, build range {station.m_rangeBuild:0.#} m, ZDO valid {wbGo.GetComponent<ZNetView>()?.IsValid() == true}");
                if (station != null) StartCoroutine(CheckStationLater(station));
            }
            st.Spawned.Add(chestGo);

            // Spawn optional decoration / utility pieces. Each get-or-skip
            // is tolerant of missing prefabs so a server can disable any
            // of them by emptying the config string.
            //
            // Bed orientation: face away from the campfire so the player
            // wakes up looking at it.
            GameObject bedGo = null;
            if (bedPrefab != null)
            {
                Quaternion bedRot = bedRotSaved ?? Quaternion.LookRotation(bedPos - firePos);
                bedGo = UnityEngine.Object.Instantiate(bedPrefab, bedPos, bedRot);
                st.Spawned.Add(bedGo);
            }
            // 1.0.31 (round 14 camp: 'missing within 6 m: rawood_bed_bal|bed'): where the bed went, from the fire and the chest.
            Debug.Log(bedPrefab == null
                ? $"[VABackpacks] camp bed: none ('{fcfg.configCampBedPrefab.Value}' resolved to no prefab, or beds are off for blueprints)"
                : $"[VABackpacks] camp bed: {bedPrefab.name} at {Vector3.Distance(bedPos, firePos):0.0} m from the fire, {Vector3.Distance(bedPos, chestPos):0.0} m from the chest");
            {
            }

            GameObject woodGo = woodPrefab != null
                ? UnityEngine.Object.Instantiate(woodPrefab, woodpilePos, woodRot)
                : null;
            st.Spawned.Add(woodGo);

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
                Quaternion chairRot = chairRotSaved ?? Quaternion.LookRotation(chairPos - firePos);
                chairGo = UnityEngine.Object.Instantiate(chairPrefab, chairPos, chairRot);
                st.Spawned.Add(chairGo);
            }

            // Cooking station goes ON the fire (small Y offset) so it
            // gets heat for cooking. Random Y rotation just for variety.
            // Earlier 0.55m offset left the grill visibly floating
            // above the campfire — sitting just barely above ground
            // (0.05m) reads as "resting on the stones".
            GameObject cookGo = cookPrefab != null
                ? UnityEngine.Object.Instantiate(cookPrefab, cookPosSaved ?? firePos + Vector3.up * 0.05f, cookRotSaved ?? Quaternion.Euler(0, UnityEngine.Random.Range(0f, 360f), 0))
                : null;
            st.Spawned.Add(cookGo);

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
                // 1.0.38: on voxel ground the pads go through FAT too (TerrainComp height does not show there).
                void Pad(Vector3 at)
                {
                    if (st.VoxelGround) CampTerrainCarry.Level(at, PIECE_PAD_RADIUS, at.y, 1, null);
                    else CampTerrain.Level(at, PIECE_PAD_RADIUS, PIECE_PAD_SMOOTH, at.y);
                }
                try
                {
                    if (fireGo  != null) Pad(firePos);
                    if (wbGo    != null) Pad(workbenchPos);
                    if (chestGo != null) Pad(chestPos);
                    if (bedGo   != null) Pad(bedPos);
                    if (woodGo  != null) Pad(woodpilePos);
                    if (chairGo != null) Pad(chairPos);
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

                Quaternion blueprintFacing = Quaternion.LookRotation(forward);
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
                    Vector3 worldPos = campCenter + (blueprintFacing * piece.Pos);
                    worldPos.y += blueprintYOffset;     // per-backpack vertical adjust
                    Quaternion worldRot = blueprintFacing * piece.Rot;

                    var pieceGo = UnityEngine.Object.Instantiate(prefab, worldPos, worldRot);
                    st.Spawned.Add(pieceGo);

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
            StampCampUid(item);   // 1.0.34: the pack-up finds this exact item again (no second bag while it exists)
            marker.BackpackItemState   = CaptureItemState(item); // 1.0.22: quality, special slots … come back on pack-up
            marker.CampCenter          = campCenter;     // blueprint center (or fire when procedural)
            marker.CampYaw             = st.Yaw;        // 1.0.35
            marker.GroundBefore        = st.GroundBefore;   // 1.0.36
            marker.SaveRadius          = st.SaveRadius;     // 1.0.40
            marker.CampRadius          = terrainRadius;     // matches the dirt patch + blueprint footprint
            marker.WriteToZdo();
            BackpackCampRegistry.Register(marker);
            st.Marker = marker;

            // Resize the chest's inventory to match the backpack and load
            // the saved contents from the dropped item's customData.
            // 1.0.25: the drop is already gone, so contents that fail to load roll the deploy back instead of being lost.
            ResizeContainer(chestGo, cfg.Rows, cfg.Columns);
            int moved = LoadBackpackContentsIntoContainer(chestGo, item);
            if (moved < 0) throw new InvalidOperationException("the bag's contents did not load into the camp chest");

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
            st.ActiveCampSet = true;

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
                CleanCampPins(player, null, "deploy");   // 1.0.35: the camp pins this mod recorded and no other
                Minimap.instance?.AddPin(campCenter, Minimap.PinType.Bed, "Camp",
                                         save: true, isChecked: false,
                                         ownerID: player.GetPlayerID());
                st.PinAdded = true;
                RecordCampPin(player, campCenter);   // 1.0.35: only recorded pins are ever removed
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] Failed to add camp map pin: {ex.Message}");
            }

            // 1.0.29 (camp upgrades step 2): the pieces the bag packed are placed again around the camp. Into st.Spawned
            // first, so a failure rolls them back and the dropped bag keeps its blueprint.
            int spawnedBefore = st.Spawned.Count;
            CampCapture.Place(player, item, campCenter, st.Yaw, st.Spawned, TryApplyPieceData);
            // 1.0.44: the camp records the pieces its deploy placed, so an over-cap pack-up takes these back first.
            if (st.Marker != null)
            {
                st.Marker.DeployedPieceIds.Clear();
                for (int k = spawnedBefore; k < st.Spawned.Count; k++)
                {
                    var nv = st.Spawned[k] != null ? st.Spawned[k].GetComponent<ZNetView>() : null;
                    if (nv != null && nv.IsValid()) st.Marker.DeployedPieceIds.Add(nv.GetZDO().m_uid);
                }
                st.Marker.WriteToZdo();
            }

            // (1.0.25: the original ItemDrop was taken out of the world before the build, in DeployFromItemDrop.)

            // Show both Center and TopLeft messages so players get clear
            // feedback regardless of where their attention is.
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                $"Camp deployed: {backpackPrefab}");
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft,
                "Camp deployed");
            return moved;
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

            // The weapon-bag that did the slam: the one in the right hand. 1.0.34 (Fire's hand test, a camp from an
            // RPGClasses bar slot "does not remove the staff"): this took the FIRST inventory item with the prefab name,
            // so with a second staff anywhere in the inventory that one was consumed and the held bar-slot staff stayed.
            // Only without a held match does it fall back to the first one in the inventory.
            ItemDrop.ItemData weapon = null;
            var held = player.RightItem;
            if (held?.m_dropPrefab != null && held.m_dropPrefab.name == prefabName && inv.ContainsItem(held))
                weapon = held;
            if (weapon == null)
            {
                foreach (var it in inv.GetAllItems())
                {
                    if (it?.m_dropPrefab != null && it.m_dropPrefab.name == prefabName)
                    {
                        weapon = it;
                        break;
                    }
                }
            }
            if (weapon == null)
            {
                Debug.LogWarning($"[VABackpacks] {prefabName} deploy: no instance in inventory.");
                yield break;
            }
            int sameBefore = inv.GetAllItems().Count(it => it?.m_dropPrefab != null && it.m_dropPrefab.name == prefabName);

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
                StampCampUid(weapon, fresh: true);   // 1.0.34: copied into the drop below, so a left-behind staff matches
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

            // 1.0.25: the drop's ZDO carries the weapon too, so a drop that stays on the ground (a deploy that did not run)
            // still holds its contents after a reload or for another peer.
            try
            {
                var spawnedView = drop.GetComponent<ZNetView>();
                if (spawnedView != null && spawnedView.IsValid() && spawnedView.IsOwner())
                    ItemDrop.SaveToZDO(drop.m_itemData, spawnedView.GetZDO());
            }
            catch (Exception ex) { Debug.LogWarning($"[VABackpacks] {prefabName} deploy: could not write the drop's ZDO: {ex.Message}"); }

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

            if (removed)
            {
                // Fire's screenshot (bar-slot camp): the bag section went but the staff stayed in the hand. Something can
                // equip the staff again in the frame between the unequip and the removal (the RPGClasses bar re-arms its
                // slot weapon), so a removed staff still held is unequipped here, which clears the hand and its visual.
                if (player.IsItemEquiped(weapon) || player.RightItem == weapon || player.LeftItem == weapon)
                {
                    Debug.Log($"[VABackpacks] {prefabName} deploy: the consumed staff was still in the hand; unequipping it (no ghost staff)");
                    try { player.UnequipItem(weapon, false); }
                    catch (Exception ex) { Debug.LogWarning($"[VABackpacks] {prefabName} deploy: hand clear failed: {ex.Message}"); }
                }
                weapon.m_equipped = false;
                StartCoroutine(CheckHandClear(player, weapon, prefabName));
                int sameAfter = inv.GetAllItems().Count(it => it?.m_dropPrefab != null && it.m_dropPrefab.name == prefabName);
                Debug.Log($"[VABackpacks] {prefabName} deploy: consumed the {(weapon == held ? "held" : "first inventory")} one " +
                          $"({sameBefore} -> {sameAfter} in the inventory, right hand now {player.RightItem?.m_dropPrefab?.name ?? "empty"})");
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
            //
            // 1.0.25: DeployFromItemDrop now takes the drop out of the world itself BEFORE it builds, and a failed build
            // is rolled back with the item dropped again. If it stops before taking the drop, the drop is still on the
            // ground holding the weapon for the player to pick up; destroying it here (as before) lost the weapon, which
            // already left the inventory above.
            bool deployed = false;
            try
            {
                deployed = DeployFromItemDrop(drop);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VABackpacks] {prefabName} deploy failed: {ex}");
            }
            if (!deployed && drop != null)
                Debug.Log($"[VABackpacks] {prefabName} deploy did not run; the {prefabName} stays on the ground at {targetPos} to pick up.");
        }

        // 1.0.34: the id that ties a camp to the item it was made from. The slam deploy stamps the held weapon before it is
        // cloned, so a copy that stayed behind carries the same id; any other deploy stamps the item if it has none.
        internal const string CampUidKey = "va_camp_uid";

        private static void StampCampUid(ItemDrop.ItemData item, bool fresh = false)
        {
            if (item == null) return;
            if (item.m_customData == null) item.m_customData = new System.Collections.Generic.Dictionary<string, string>();
            if (fresh || !item.m_customData.ContainsKey(CampUidKey))
                item.m_customData[CampUidKey] = Guid.NewGuid().ToString("N");
        }

        private static ItemDrop.ItemData FindCampOriginal(Player player, Inventory inv, string uid)
        {
            if (string.IsNullOrEmpty(uid) || player == null) return null;
            bool Match(ItemDrop.ItemData it) => it?.m_customData != null && it.m_customData.TryGetValue(CampUidKey, out var v) && v == uid;
            if (inv != null) foreach (var it in inv.GetAllItems()) if (Match(it)) return it;
            if (Match(player.RightItem)) return player.RightItem;
            if (Match(player.LeftItem)) return player.LeftItem;
            return null;
        }

        // 1.0.35: one second after the deploy the workbench has run its Start; vanilla's own range query must find it.
        private IEnumerator CheckStationLater(CraftingStation station)
        {
            yield return new WaitForSeconds(1f);
            if (station == null) { Debug.Log("[VABackpacks] camp workbench: gone 1 s after the deploy"); yield break; }
            Vector3 probe = station.transform.position + station.transform.right * 3f;
            bool inRange = CraftingStation.HaveBuildStationInRange(station.m_name, probe);
            Debug.Log($"[VABackpacks] camp workbench after 1 s: vanilla build-station query 3 m away finds it: {inRange} (range {station.m_rangeBuild:0.#} m)");
        }

        // 1.0.37: a building in the way of a camp is a piece a player built and that stands in the world: a live ZDO, a
        // creator, a WearNTear, not a plant, not a camp piece, and not an effect or terrain object that carries a Piece
        // (sfx_/vfx_ objects, VAVoxel chunks, pets).
        private static bool IsRealBuilding(Piece pc)
        {
            if (pc == null || !pc.gameObject.activeInHierarchy) return false;
            var nview = pc.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            if (pc.GetCreator() == 0) return false;
            if (pc.GetComponent<WearNTear>() == null) return false;
            if (pc.GetComponent<Plant>() != null || pc.GetComponent<Character>() != null) return false;
            if (nview.GetZDO().GetInt(CampImmortalPatch.KEY_IMMORTAL, 0) == 1) return false;
            string name = Utils.GetPrefabName(pc.gameObject);
            if (name.StartsWith("sfx_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("vfx_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("fx_", StringComparison.OrdinalIgnoreCase) || name.IndexOf("Voxel", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return true;
        }

        // 1.0.41 (Fire fell through the world when he packed his camp: the ground under him changed during the terrain restore).
        // Every player and tame this client owns inside the ring before the restore is checked for 3 s afterwards; one found
        // below the ground (the new surface) is set on it, 0.5 m up, with its fall stopped.
        private static List<Character> CharactersInRing(Vector3 centre, float radius)
        {
            var list = new List<Character>();
            float r = radius + 2f;
            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == null || c.IsDead() || !(c.IsPlayer() || c.IsTamed())) continue;
                var nview = c.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid() || !nview.IsOwner()) continue;
                Vector3 p = c.transform.position;
                float dx = p.x - centre.x, dz = p.z - centre.z;
                if (dx * dx + dz * dz <= r * r) list.Add(c);
            }
            return list;
        }

        private IEnumerator LiftAboveGround(List<Character> chars)
        {
            if (chars == null || chars.Count == 0 || ZoneSystem.instance == null) yield break;
            var lifted = new HashSet<Character>();
            float until = Time.time + 3f;
            while (Time.time < until)
            {
                foreach (Character c in chars)
                {
                    if (c == null || lifted.Contains(c)) continue;
                    Vector3 p = c.transform.position;
                    float ground = ZoneSystem.instance.GetGroundHeight(p);
                    if (p.y >= ground - 0.3f) continue;
                    Vector3 to = new Vector3(p.x, ground + 0.5f, p.z);
                    c.transform.position = to;
                    var body = c.GetComponent<Rigidbody>();
                    if (body != null) { body.position = to; body.velocity = Vector3.zero; }
                    lifted.Add(c);
                    Debug.Log($"[VABackpacks] pack-up: lifted {c.GetHoverName()} from y {p.y:0.00} to {to.y:0.00} (below the restored ground)");
                }
                yield return null;
            }
        }

        /// <summary>1.0.44: the camp level of a placed camp (its bag's saved quality), as pack-up reads it.</summary>
        internal static int LevelOf(BackpackCampMarker marker)
        {
            if (marker == null || ZNetScene.instance == null) return 1;
            var prefab = ZNetScene.instance.GetPrefab(marker.BackpackPrefabName);
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null) return 1;
            var probe = drop.m_itemData.Clone();
            probe.m_customData = new System.Collections.Generic.Dictionary<string, string>();
            RestoreItemState(probe, marker.BackpackItemState);
            return CampLevels.Level(probe);
        }

        // A second look 1 s after a slam deploy: the consumed staff must not be back in a hand (re-armed by a bar poll).
        private IEnumerator CheckHandClear(Player player, ItemDrop.ItemData weapon, string prefabName)
        {
            yield return new WaitForSeconds(1f);
            if (player == null || weapon == null) yield break;
            bool held = player.RightItem == weapon || player.LeftItem == weapon;
            if (held)
            {
                try { player.UnequipItem(weapon, false); } catch { }
                weapon.m_equipped = false;
            }
            Debug.Log($"[VABackpacks] {prefabName} deploy: hand check after 1 s: {(held ? "the consumed staff was held again; unequipped" : "clear")}, right hand {player.RightItem?.m_dropPrefab?.name ?? "empty"}");
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
        internal const string ChestVisualName = "VA_BackpackVisual";
        private const float BagLeanDegrees = 12f;

        internal static void AttachBackpackVisualToChest(GameObject chestGo, string backpackPrefabName)
        {
            if (chestGo == null || string.IsNullOrEmpty(backpackPrefabName)) return;
            if (chestGo.transform.Find(ChestVisualName) != null) return;   // 1.0.25: the deploy and the chest's load both call this
            var prefab = ZNetScene.instance?.GetPrefab(backpackPrefabName);
            if (prefab == null) return;

            // 1.0.25 (Fire 10-08: "the backpack placed next to the chest was floating in the air, its base needs to rest on the
            // ground like it's leaning against the chest"). The old copy switched on EVERY renderer of the bag prefab, worn
            // model included, and that model is posed ~1.4 m up on a player's back. A shoulder bag now uses that worn mesh
            // alone, upright, with its base on the ground beside the chest and its top leaning on the chest's side.
            try
            {
                var standing = BackpackWornVisual.BuildStandingCopy(prefab, ChestVisualName);
                if (standing != null)
                {
                    LeanAgainstChest(chestGo, standing, prefab.name);
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] Camp bag visual for {backpackPrefabName} failed: {ex.Message}");
                var half = chestGo.transform.Find(ChestVisualName);
                if (half != null) UnityEngine.Object.Destroy(half.gameObject);
                return;
            }

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
                visual.name = ChestVisualName;

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

                // 1.0.25: a worn model ("attach_<bone>", posed on a player) never goes on the chest copy; switched on below it
                // would float in the air.
                foreach (var t in visual.GetComponentsInChildren<Transform>(true))
                    if (t != null && t != visual.transform && t.name.StartsWith("attach_", StringComparison.Ordinal))
                        UnityEngine.Object.DestroyImmediate(t.gameObject);

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

                // 1.0.25: its lowest point on the ground (the old fixed offset ignored where the model's pivot is).
                if (TryWorldRendererBounds(visual.transform, out Bounds wb))
                {
                    float ground = GroundHeightAt(visual.transform.position, chestGo, chestGo.transform.position.y, out string from);
                    visual.transform.position += Vector3.up * (ground - wb.min.y);
                    Debug.Log($"[VABackpacks] camp bag visual: {prefab.name} beside the chest, base on {from} at y={ground:F2} (moved {ground - wb.min.y:+0.00;-0.00} m)");
                }
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

        // 1.0.25: the standing worn mesh (back panel toward +Z) put beside the chest's right side: its bottom back edge on the
        // ground, turned so the back panel faces the chest, and tilted BagLeanDegrees about that edge so its top rests on the
        // chest's side. The gap at the base is exactly what the lean needs, so the top touches without going in.
        // Each shoulder bag's measured pose (Tools\_stage_seasons_vabp125\camp_pose.txt; the rip's
        // FiresVABackpacksBundle.MeasureCampPose on the 1.0.25 meshes, which the game can't read back): the tilt that stands the
        // worn mesh upright (the Explorer Bag's frame, the others' thinnest front-to-back), and at that tilt + BagLeanDegrees
        // the lowest point (MinY), the point nearest the chest (MaxZ) and the middle along X, in the worn mesh's frame.
        private readonly struct CampPose
        {
            public readonly float Tilt, MinY, MaxZ, CentreX;
            public CampPose(float tilt, float minY, float maxZ, float centreX) { Tilt = tilt; MinY = minY; MaxZ = maxZ; CentreX = centreX; }
        }
        private static readonly System.Collections.Generic.Dictionary<string, CampPose> CampPoses = new System.Collections.Generic.Dictionary<string, CampPose>
        {
            { "SmallBag_va",    new CampPose(2.50f, 1.2079f, 0.3338f, 0.0423f) },
            { "DayBag_va",      new CampPose(6.00f, 1.1762f, 0.4596f, 0.0184f) },
            { "ExplorerBag_va", new CampPose(-9.70f, 1.0511f, 0.1443f, 0.0291f) },
        };

        private static void LeanAgainstChest(GameObject chestGo, GameObject bag, string prefabName)
        {
            if (CampPoses.TryGetValue(prefabName, out CampPose cp))
            {
                Transform ct = chestGo.transform;
                Vector3 s = ct.right; s.y = 0f;
                s = s.sqrMagnitude > 1e-4f ? s.normalized : Vector3.right;
                Quaternion look = Quaternion.LookRotation(-s, Vector3.up);
                Vector3 target = ct.position + s * (ChestHalfWidth(chestGo, s) + 0.02f);
                target.y = GroundHeightAt(target + s * 0.2f, chestGo, ct.position.y, out string groundFrom);
                bag.transform.SetParent(ct, false);
                bag.transform.rotation = look * Quaternion.Euler(cp.Tilt + BagLeanDegrees, 0f, 0f);
                bag.transform.position = target - look * new Vector3(cp.CentreX, cp.MinY, cp.MaxZ);
                Vector3 chestScale = ct.lossyScale;
                bag.transform.localScale = new Vector3(1f / Mathf.Max(chestScale.x, 1e-3f), 1f / Mathf.Max(chestScale.y, 1e-3f), 1f / Mathf.Max(chestScale.z, 1e-3f));
                Debug.Log($"[VABackpacks] camp bag visual: {prefabName} leans on the chest, base on {groundFrom} at y={target.y:F2}, " +
                          $"{BagLeanDegrees:F0} deg lean (upright {cp.Tilt:F1} deg), top 0.02 m from the chest's side");
                return;
            }

            Bounds b = LocalMeshBounds(bag.transform);
            var pose = new GameObject("pose").transform;
            pose.SetParent(bag.transform, false);
            var parts = new System.Collections.Generic.List<Transform>();
            foreach (Transform c in bag.transform) if (c != pose) parts.Add(c);
            foreach (var c in parts) c.SetParent(pose, false);
            pose.localPosition = -new Vector3(b.center.x, b.min.y, b.max.z);

            Transform chest = chestGo.transform;
            Vector3 side = chest.right; side.y = 0f;
            side = side.sqrMagnitude > 1e-4f ? side.normalized : Vector3.right;
            float height = b.size.y;
            float gap = height * Mathf.Sin(BagLeanDegrees * Mathf.Deg2Rad) + 0.02f;
            Vector3 spot = chest.position + side * (ChestHalfWidth(chestGo, side) + gap);
            spot.y = GroundHeightAt(spot, chestGo, chest.position.y, out string from);

            bag.transform.SetParent(chest, false);
            bag.transform.position = spot;
            bag.transform.rotation = Quaternion.LookRotation(-side, Vector3.up) * Quaternion.Euler(BagLeanDegrees, 0f, 0f);
            Vector3 cs = chest.lossyScale;
            bag.transform.localScale = new Vector3(1f / Mathf.Max(cs.x, 1e-3f), 1f / Mathf.Max(cs.y, 1e-3f), 1f / Mathf.Max(cs.z, 1e-3f));

            float lowest = TryWorldRendererBounds(bag.transform, out Bounds wb) ? wb.min.y : spot.y;
            Debug.Log($"[VABackpacks] camp bag visual: {prefabName} leans on the chest, base on {from} at y={spot.y:F2} " +
                      $"(lowest point {lowest:F2}), {height:F2} m tall, {BagLeanDegrees:F0} deg lean, {gap:F2} m from the chest's side");
        }

        // How far the chest reaches along 'side' from its position (its solid colliders; 0.5 m if it has none).
        private static float ChestHalfWidth(GameObject chestGo, Vector3 side)
        {
            float best = 0f;
            Vector3 origin = chestGo.transform.position;
            foreach (var col in chestGo.GetComponentsInChildren<Collider>())
            {
                if (col == null || col.isTrigger || !col.enabled) continue;
                // The collider's own box (the chest is turned at random, and a world-aligned box around it reaches further
                // than its side does).
                Bounds cb;
                Matrix4x4 toWorld = col.transform.localToWorldMatrix;
                if (col is MeshCollider mc && mc.sharedMesh != null) cb = mc.sharedMesh.bounds;
                else if (col is BoxCollider bc) cb = new Bounds(bc.center, bc.size);
                else { cb = col.bounds; toWorld = Matrix4x4.identity; }
                for (int i = 0; i < 8; i++)
                {
                    var corner = toWorld.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? cb.min.x : cb.max.x, (i & 2) == 0 ? cb.min.y : cb.max.y, (i & 4) == 0 ? cb.min.z : cb.max.z));
                    best = Mathf.Max(best, Vector3.Dot(corner - origin, side));
                }
            }
            return best > 0.05f ? best : 0.5f;
        }

        // The ground under 'pos': the terrain height (the camp's levelled heightmap), or a floor piece above it (a
        // blueprint camp). The chest's own colliders don't count.
        private static float GroundHeightAt(Vector3 pos, GameObject chestGo, float fallback, out string from)
        {
            float y = fallback;
            from = "the chest's base";
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(pos, out float terrain))
            {
                y = terrain;
                from = "terrain";
            }
            int mask = LayerMask.GetMask("piece", "Default", "static_solid", "Default_small");
            foreach (var hit in Physics.RaycastAll(new Vector3(pos.x, y + 1.5f, pos.z), Vector3.down, 2.5f, mask, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider == null || hit.collider.transform.IsChildOf(chestGo.transform)) continue;
                if (hit.point.y > y + 0.01f && hit.point.y < y + 1.2f)
                {
                    y = hit.point.y;
                    from = "piece " + hit.collider.name;
                }
            }
            return y;
        }

        private static Bounds LocalMeshBounds(Transform root)
        {
            bool any = false;
            var bounds = new Bounds();
            Matrix4x4 toRoot = root.worldToLocalMatrix;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Matrix4x4 m = toRoot * mf.transform.localToWorldMatrix;
                Bounds mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var p = m.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? mb.min.x : mb.max.x, (i & 2) == 0 ? mb.min.y : mb.max.y, (i & 4) == 0 ? mb.min.z : mb.max.z));
                    if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                    else bounds.Encapsulate(p);
                }
            }
            return bounds;
        }

        private static bool TryWorldRendererBounds(Transform root, out Bounds bounds)
        {
            bounds = new Bounds();
            bool any = false;
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                if (r == null || !r.enabled || r is ParticleSystemRenderer) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
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
            // Joints before their Rigidbodies (R76/R77: "Can't remove Rigidbody because ConfigurableJoint depends on it"):
            // the HoboStick's AttachPoint carries a ConfigurableJoint (HoboStickPhysicsSetup), and Unity refuses to remove
            // a Rigidbody a joint still needs, so the decoration kept its physics body.
            foreach (var c in root.GetComponentsInChildren<Joint>(true))            kill(c);
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

        private void PackUpCamp(BackpackCampMarker marker, bool packAnyway = false)
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

            // 1.0.35: where every camp piece stands, in the camp frame, read before anything is taken down.
            string campLayout = null;
            string campTerrain = null;   // 1.0.36: the camp ground, carried in the bag (FAT)
            try
            {
                var spots = CampLayout.Capture(marker);
                campLayout = CampLayout.Encode(spots);
                Debug.Log($"[VABackpacks] camp layout saved: {spots.Count} camp piece(s) ({string.Join(", ", spots.Keys)}), facing {marker.CampYaw:0}");
                campTerrain = CampTerrainCarry.Capture(CampRing.Centre(marker), marker.CampYaw, CampRing.Radius(marker));   // 1.0.41: the same ring   // 1.0.40: the save ring
            }
            catch (Exception ex) { Debug.LogWarning($"[VABackpacks] camp layout capture failed: {ex.Message}"); }

            // Save the chest's contents back into a backpack item's
            // customData blob, then spawn that item on the ground.
            var chestGo = marker.gameObject;
            var container = chestGo.GetComponent<Container>();

            // Resolve the original backpack prefab so we can spawn a
            // matching ItemDrop with the saved storage.
            var backpackPrefab = ZNetScene.instance?.GetPrefab(marker.BackpackPrefabName);
            if (backpackPrefab == null)
            {
                Debug.LogError($"[VABackpacks] Pack-up failed: prefab '{marker.BackpackPrefabName}' missing.");
                return;
            }

            // 1.0.44 (Fire: "the whole building stayed standing": the capture refused, 183 pieces over the level-1 cap of 100,
            // but the pack-up went on and took the chest, the camp pieces and the ground). The capture's read-only checks run
            // FIRST; a refusal stops the pack-up with nothing touched, and its message says why.
            try
            {
                var check = backpackPrefab.GetComponent<ItemDrop>().m_itemData.Clone();
                check.m_customData = new System.Collections.Generic.Dictionary<string, string>();
                RestoreItemState(check, marker.BackpackItemState);
                string ok = CampCapture.Capture(player, CampRing.Centre(marker), CampRing.Radius(marker), 0f, CampLevels.Level(check),
                    out string checkLine, out _, checkOnly: true);
                if (!packAnyway && checkLine != null && checkLine.Contains("refused"))
                {
                    // An open chest is a hard refusal (its message is already up). Over the cap or the data limit, Fire's
                    // choice (1.0.44): ask. No = nothing touched; Yes = pack up to the limit and leave the rest standing.
                    if (checkLine.Contains("chest is open"))
                    {
                        Debug.Log($"[VABackpacks] {checkLine} (pack-up stopped before anything was removed; the camp stays as it is)");
                        return;
                    }
                    int level = CampLevels.Level(check), cap = CampLevels.PieceCap(level), ringCount = CampCapture.LastInRing;
                    string text = checkLine.Contains("blueprint is")
                        ? $"The buildings in this camp are more data than one bag holds ({ringCount} pieces in the ring). Packing will leave the farthest pieces behind, standing. Pack anyway?"
                        : $"Packing this camp will leave {ringCount - cap} pieces behind ({ringCount} in the ring, the bag holds {cap}). Pack anyway?";
                    Debug.Log($"[VABackpacks] {checkLine}; asking the player (Yes = pack up to the limit, No = nothing touched)");
                    var target = marker;
                    UnifiedPopup.Push(new YesNoPopup("Camp over its limit", text,
                        () => { UnifiedPopup.Pop(); Debug.Log("[VABackpacks] pack anyway: Yes"); if (target != null) PackUpCamp(target, packAnyway: true); },
                        () => { UnifiedPopup.Pop(); Debug.Log("[VABackpacks] pack anyway: No; the camp stays as it is"); },
                        localizeText: false));
                    return;
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[VABackpacks] camp pack-up check failed: {ex.Message}"); }

            // 1.0.25 (Fire 10-08, camp dupes): the chest goes FIRST and its ZDO is checked gone before the bag comes back.
            // Before, the bag came back first and the chest was destroyed last by a local ownership claim, so a chest whose
            // destroy did not take (or an exception in between) left the chest with its contents AND the bag with them.
            var chestView = chestGo.GetComponent<ZNetView>();
            if (chestView == null || !chestView.IsValid())
            {
                Debug.LogWarning("[VABackpacks] Pack-up: the camp chest has no live ZDO; nothing packed.");
                return;
            }
            ZDOID chestId = chestView.GetZDO().m_uid;
            if (!chestView.IsOwner()) chestView.ClaimOwnership();
            string base64 = SerializeContainerToBase64(container);
            int contentCount = container?.GetInventory()?.GetAllItems().Count ?? 0;
            BackpackCampRegistry.Unregister(marker);
            ZNetScene.instance.Destroy(chestGo);
            if (!DestroyedOrQueued(chestId))
            {
                // The local object is gone but the ZDO is not, so the chest comes back with its contents: give nothing back.
                Debug.LogError($"[VABackpacks] Pack-up: chest {chestId} is still in the world after the destroy; nothing packed.");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "Pack-up failed: the camp chest is still in use. Try again.");
                return;
            }

            // 1.0.29 (camp upgrades step 2): the player's own pieces inside the camp level's radius go into the bag. The
            // level is the bag's saved quality. Nothing is taken when the capture refuses (cap, size, an open chest).
            string campBlueprint = null;
            CampStorage.Totals campStorage = null;
            string campUid = null;
            try
            {
                var probe = backpackPrefab.GetComponent<ItemDrop>().m_itemData.Clone();
                probe.m_customData = new System.Collections.Generic.Dictionary<string, string>();
                RestoreItemState(probe, marker.BackpackItemState);
                probe.m_customData.TryGetValue(CampUidKey, out campUid);
                Vector3 ringCentre = CampRing.Centre(marker);
                float ringRadius = CampRing.Radius(marker);
                Debug.Log($"[VABackpacks] capture disc centre {ringCentre} radius {ringRadius:0.##} == ring centre {CampRing.Centre(marker)} radius {CampRing.Radius(marker):0.##} (CampRing, one definition)");
                campBlueprint = CampCapture.Capture(player, ringCentre, ringRadius, 0f, CampLevels.Level(probe), out string captureLine, out campStorage,
                    partial: packAnyway, first: marker.DeployedPieceIds);
                if (captureLine != null) Debug.Log($"[VABackpacks] {captureLine}");
            }
            catch (Exception ex) { Debug.LogError($"[VABackpacks] camp pack-up capture failed: {ex}"); }

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
            //
            // 1.0.22: only a HOLLOW copy (not worn, no contents, no special-slot items) counts as the camp's own bag. A bag the
            // player carries with things in it is never overwritten (1.0.21 replaced its contents with the chest's). Either way
            // the bag gets back its own saved state (quality, special slots, …; marker.BackpackItemState) before the contents.
            string state = marker.BackpackItemState;
            ItemDrop.ItemData returned = null;
            bool gaveToInventory = false;
            try
            {
                var inv = player.GetInventory();
                if (inv != null)
                {
                    // 1.0.34 (Fire: a bar-slot camp left the staff in the hand and the pack-up gave a SECOND staff): the
                    // item the camp was made from carries the camp's id. If it is still on the player, the pack-up returns
                    // into THAT item and never makes a new one. A copy left only in a hand (not in the inventory) is a
                    // ghost: it is unequipped, and the bag comes back the normal way.
                    ItemDrop.ItemData original = FindCampOriginal(player, inv, campUid);
                    if (original != null)
                    {
                        bool inInventory = inv.ContainsItem(original);
                        if (player.IsItemEquiped(original) || player.RightItem == original || player.LeftItem == original)
                        {
                            try { player.UnequipItem(original, false); } catch { }
                            original.m_equipped = false;
                        }
                        Debug.Log($"[VABackpacks] Pack-up: the camp's own {marker.BackpackPrefabName} (camp id {campUid}) is still on the player " +
                                  $"({(inInventory ? "in the inventory: the contents go back into it, no new one" : "only in a hand: unequipped as a ghost")})");
                        if (inInventory)
                        {
                            RestoreItemState(original, state);
                            if (!string.IsNullOrEmpty(base64)) original.m_customData[BackpackApi.CUSTOM_DATA_KEY] = base64;
                            if (campBlueprint != null) { original.m_customData[CampCapture.CustomDataKey] = campBlueprint; CampStorage.Write(original, campStorage); }
                            CampLayout.Write(original, campLayout);
                            CampTerrainCarry.Write(original, campTerrain);
                            returned = original;
                            gaveToInventory = true;
                        }
                    }
                    var worn = BackpackApi.GetEquippedBackpacks(player);
                    if (!gaveToInventory)
                    foreach (var existing in inv.GetAllItems())
                    {
                        if (existing?.m_dropPrefab == null) continue;
                        if (existing.m_dropPrefab.name != marker.BackpackPrefabName) continue;
                        if (existing.m_equipped || (worn != null && worn.Contains(existing)) || !IsHollowBag(existing)) continue;

                        RestoreItemState(existing, state);
                        if (existing.m_customData == null)
                            existing.m_customData = new System.Collections.Generic.Dictionary<string, string>();
                        if (!string.IsNullOrEmpty(base64))
                            existing.m_customData[BackpackApi.CUSTOM_DATA_KEY] = base64;
                        if (campBlueprint != null) { existing.m_customData[CampCapture.CustomDataKey] = campBlueprint; CampStorage.Write(existing, campStorage); }
                        CampLayout.Write(existing, campLayout);
                        CampTerrainCarry.Write(existing, campTerrain);
                        returned = existing;
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

            string where = "the carried bag";
            if (!gaveToInventory)
            {
                // 1.0.26 (round 12: "after the pack-up the bag is missing", a quality-2 bag "no bag came back"): the bag comes
                // back INTO the inventory, as a pickup would. 1.0.22-1.0.25 spawned it as a drop on top of the chest, where the
                // player doesn't look and the terrain restore right after can bury it. Only a full inventory drops it, at the
                // player's feet.
                ItemDrop.ItemData bagItem = null;
                try
                {
                    bagItem = backpackPrefab.GetComponent<ItemDrop>().m_itemData.Clone();
                    bagItem.m_dropPrefab = backpackPrefab;
                    bagItem.m_stack = 1;
                    bagItem.m_equipped = false;
                    bagItem.m_customData = new System.Collections.Generic.Dictionary<string, string>();
                    RestoreItemState(bagItem, state);
                    if (bagItem.m_customData == null)
                        bagItem.m_customData = new System.Collections.Generic.Dictionary<string, string>();
                    if (!string.IsNullOrEmpty(base64))
                        bagItem.m_customData[BackpackApi.CUSTOM_DATA_KEY] = base64;
                    if (campBlueprint != null) { bagItem.m_customData[CampCapture.CustomDataKey] = campBlueprint; CampStorage.Write(bagItem, campStorage); }
                    CampLayout.Write(bagItem, campLayout);
                    CampTerrainCarry.Write(bagItem, campTerrain);
                    bagItem.m_equipped = false;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[VABackpacks] Pack-up: could not rebuild the {marker.BackpackPrefabName}: {ex}");
                }
                if (bagItem != null)
                {
                    var inv = player.GetInventory();
                    bool added = false;
                    try { added = inv != null && inv.CanAddItem(bagItem, 1) && inv.AddItem(bagItem); }
                    catch (Exception ex) { Debug.LogWarning($"[VABackpacks] Pack-up: adding the bag to the inventory failed: {ex.Message}"); }
                    if (added)
                    {
                        returned = bagItem;
                        where = "the inventory";
                    }
                    else
                    {
                        Vector3 feet = player.transform.position + player.transform.forward * 0.6f + Vector3.up * 0.6f;
                        ItemDrop back = ItemDrop.DropItem(bagItem, 0, feet, Quaternion.identity);
                        returned = back != null ? back.m_itemData : bagItem;
                        where = $"a drop at the player's feet {GetZdoId(back != null ? back.gameObject : null)} (the inventory is full)";
                    }
                }
            }
            Debug.Log($"[VABackpacks] Pack-up: chest {chestId} destroyed, contents {contentCount} moved into {where}");
            if (returned != null)
                Debug.Log($"[VABackpacks] Pack-up: {marker.BackpackPrefabName} back at quality {returned.m_quality}, " +
                          $"{(returned.m_customData?.Keys.Count(k => k.StartsWith(SpecialSlotKeyPrefix, StringComparison.Ordinal)) ?? 0)} special slot(s) filled" +
                          (string.IsNullOrEmpty(state) ? " (a camp from before 1.0.22: its bag's quality and special slots were not kept)" : ""));

            // Restore the heightmap + paint mask from the snapshot we
            // took at deploy. This is the WEC undo pattern — exact
            // pre-deploy state, no rogue TerrainModifier piece needed.
            // (If terrainSnapshotBase64 is empty, we ran the legacy
            // mud_road fallback at deploy and rely on its destroy below.)
            // 1.0.41 (Fire fell through the world on a pack-up): who stands in the ring before the ground changes under them.
            var inRing = CharactersInRing(CampRing.Centre(marker), CampRing.Radius(marker));
            // 1.0.44 (Fire: never flatten the ground under pieces left behind): a partial pack keeps the ground as it is.
            bool keepGround = packAnyway && CampCapture.LastLeftBehind > 0;
            if (keepGround)
                Debug.Log($"[VABackpacks] pack-up: {CampCapture.LastLeftBehind} piece(s) left standing, so the ground stays as it is (no restore)");
            if (!keepGround && !string.IsNullOrEmpty(marker.TerrainSnapshotBase64))
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
            if (!keepGround) CampTerrainCarry.RestoreGround(marker.GroundBefore, marker.CampCenter, marker.CampYaw, "pack-up");   // 1.0.36
            StartCoroutine(LiftAboveGround(inRing));

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
            // 1.0.25: each on its own, so one piece that throws can't stop the rest (the bag is already back).
            foreach (var pieceId in new[] { marker.TerrainModZdoId, marker.CookingStationZdoId, marker.CampfireZdoId,
                                            marker.WorkbenchZdoId, marker.BedZdoId, marker.WoodpileZdoId, marker.ChairZdoId })
            {
                try { DestroyByZdoId(pieceId); }
                catch (Exception ex) { Debug.LogWarning($"[VABackpacks] Pack-up: could not remove camp piece {pieceId}: {ex.Message}"); }
            }
            // Blueprint-spawned pieces (if a blueprint was used) — same
            // destroy-by-ZDOID treatment.
            if (marker.BlueprintPieceZdoIds != null)
            {
                foreach (var bpId in marker.BlueprintPieceZdoIds)
                {
                    try { DestroyByZdoId(bpId); }
                    catch (Exception ex) { Debug.LogWarning($"[VABackpacks] Pack-up: could not remove blueprint piece {bpId}: {ex.Message}"); }
                }
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

            CleanCampPins(player, null, "pack-up");   // 1.0.35: the recorded camp pins go, no other pin

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

            // (1.0.25: the chest itself went first, above.)
            UnregisterVegetationClearArea(marker.CampCenter);

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

        // 1.0.35 (Fire's map: several stacked CAMP pins; never delete a player's own pins). Every pin a deploy adds is
        // recorded on the player (position, name "Camp"); only a pin that matches a recorded one is ever removed. Deploy and
        // pack-up remove all recorded pins; at load the newest is kept while the player has a camp. Old unrecorded pins
        // are left alone (pack-up still removes a "Camp" pin within 2 m of the camp it packs, as before).
        private const string KEY_CAMP_PINS = "va_camp_pins";   // "x,z;x,z;..." oldest first
        private static bool _pinsCleaned;

        private static List<Vector2> RecordedPins(Player player)
        {
            var list = new List<Vector2>();
            if (player?.m_customData == null || !player.m_customData.TryGetValue(KEY_CAMP_PINS, out var raw) || string.IsNullOrEmpty(raw)) return list;
            foreach (string e in raw.Split(';'))
            {
                string[] f = e.Split(',');
                if (f.Length == 2 && float.TryParse(f[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(f[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                    list.Add(new Vector2(x, z));
            }
            return list;
        }

        private static void SaveRecordedPins(Player player, List<Vector2> list)
        {
            if (player?.m_customData == null) return;
            if (list.Count == 0) { player.m_customData.Remove(KEY_CAMP_PINS); return; }
            player.m_customData[KEY_CAMP_PINS] = string.Join(";", list.Select(v =>
                v.x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "," + v.y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
        }

        private static void RecordCampPin(Player player, Vector3 pos)
        {
            var list = RecordedPins(player);
            list.Add(new Vector2(pos.x, pos.z));
            SaveRecordedPins(player, list);
        }

        private static void CleanCampPins(Player player, Vector3? unused, string why)
        {
            if (Minimap.instance == null || player == null) return;
            try
            {
                var recorded = RecordedPins(player);
                if (recorded.Count == 0) return;
                bool keepNewest = why == "load" && player.m_customData != null
                    && player.m_customData.TryGetValue(KEY_PLAYER_CAMP, out var raw) && !string.IsNullOrEmpty(raw);
                var remove = keepNewest ? recorded.Take(recorded.Count - 1).ToList() : recorded;
                int removed = 0;
                var pins = Minimap.instance.m_pins;
                foreach (Vector2 r in remove)
                {
                    for (int i = pins.Count - 1; i >= 0; i--)
                    {
                        var pin = pins[i];
                        if (pin == null || pin.m_name != "Camp" || pin.m_type != Minimap.PinType.Bed) continue;
                        float dx = pin.m_pos.x - r.x, dz = pin.m_pos.z - r.y;
                        if (dx * dx + dz * dz > 0.01f) continue;   // the exact recorded spot
                        Minimap.instance.RemovePin(pin);
                        removed++;
                    }
                }
                SaveRecordedPins(player, keepNewest ? recorded.Skip(recorded.Count - 1).ToList() : new List<Vector2>());
                Debug.Log($"[VABackpacks] camp pins ({why}): {removed} recorded camp pin(s) removed{(keepNewest ? ", the current camp's kept" : "")}; other pins untouched");
            }
            catch (Exception ex) { Debug.LogWarning($"[VABackpacks] camp pin cleanup ({why}) failed: {ex.Message}"); }
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

        // 1.0.25: returns the number of items loaded, 0 for an empty bag, -1 when the contents could not be loaded.
        private static int LoadBackpackContentsIntoContainer(GameObject chestGo, ItemDrop.ItemData backpackItem)
        {
            if (chestGo == null || backpackItem == null)
            {
                Debug.LogWarning($"[VABackpacks] LoadBackpackContentsIntoContainer: chestGo={chestGo!=null}, backpackItem={backpackItem!=null} — bailing.");
                return -1;
            }
            var container = chestGo.GetComponent<Container>();
            if (container == null)
            {
                Debug.LogWarning("[VABackpacks] LoadBackpackContentsIntoContainer: chest has no Container component.");
                return -1;
            }
            var inv = container.GetInventory();
            if (inv == null)
            {
                Debug.LogWarning("[VABackpacks] LoadBackpackContentsIntoContainer: container.GetInventory() returned null.");
                return -1;
            }

            string base64 = backpackItem.m_customData != null
                && backpackItem.m_customData.TryGetValue(BackpackApi.CUSTOM_DATA_KEY, out var s)
                ? s : null;
            if (string.IsNullOrEmpty(base64))
            {
                // An empty bag writes no key: VAInventory's BackpackStorageSync.HandleUnequipped only writes when it captured
                // items, and a fresh bag has never had one (R77's drill stick). So no key reads as an EMPTY bag, not a loss;
                // a lost bag would show items in the bag block before the unequip and none after.
                Debug.Log($"[VABackpacks] LoadBackpackContentsIntoContainer: no saved '{BackpackApi.CUSTOM_DATA_KEY}' in the bag " +
                          $"(customData entries: {(backpackItem.m_customData != null ? backpackItem.m_customData.Count : 0)}): an empty bag, so the chest starts empty.");
                return 0;
            }
            Debug.Log($"[VABackpacks] LoadBackpackContentsIntoContainer: loading {base64.Length} chars of base64 into chest ({inv.GetWidth()}x{inv.GetHeight()}).");

            try
            {
                var pkg = new ZPackage(base64);
                inv.Load(pkg);
                Debug.Log($"[VABackpacks] LoadBackpackContentsIntoContainer: loaded {inv.GetAllItems().Count} items into chest.");
                return inv.GetAllItems().Count;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] Failed to load backpack contents into chest: {ex.Message}");
                return -1;
            }
        }

        // ── 1.0.22: the bag's own item state across a camp ────────────────────────────────────────────────────────────────
        // Deploy destroys the dropped bag; its contents go into the chest, and (before 1.0.22) everything else about the bag was
        // lost: a quality-2 bag came back quality 1 with its tool/resources/bypass items gone. The state is kept in vanilla's own
        // item format (ItemData.Save/Load, the bytes an ItemDrop keeps in its ZDO), minus the contents blob, on the chest's ZDO.
        private const string SpecialSlotKeyPrefix = "BackpackSpecial.";
        private static readonly byte ItemFormatVersion =
            (byte)Enum.GetValues(typeof(global::Version.Item)).Cast<int>().Max();

        private static string CaptureItemState(ItemDrop.ItemData item)
        {
            if (item == null) return string.Empty;
            try
            {
                ItemDrop.ItemData copy = item.Clone();
                copy.m_customData = item.m_customData != null
                    ? new System.Collections.Generic.Dictionary<string, string>(item.m_customData)
                    : new System.Collections.Generic.Dictionary<string, string>();
                copy.m_customData.Remove(BackpackApi.CUSTOM_DATA_KEY);
                copy.m_equipped = false;
                var pkg = new ZPackage();
                pkg.Write(ItemFormatVersion);
                copy.Save(pkg);
                return pkg.GetBase64();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] Camp: could not keep the bag's item state: {ex.Message}");
                return string.Empty;
            }
        }

        // Puts a captured state back onto a bag item, keeping the item's own place in the inventory. Nothing changes for an empty
        // or unreadable state (camps deployed before 1.0.22).
        private static void RestoreItemState(ItemDrop.ItemData item, string state)
        {
            if (item == null || string.IsNullOrEmpty(state)) return;
            try
            {
                var pkg = new ZPackage(state);
                var version = (global::Version.Item)pkg.ReadByte();
                Vector2i place = item.m_gridPos;
                bool equipped = item.m_equipped;
                ItemDrop.ItemData.Load(pkg, item, version);
                CampStorage.Clear(item);   // 1.0.32: the packed camp's storage totals belong with its blueprint
                item.m_customData?.Remove(CampCapture.CustomDataKey);   // 1.0.29: a blueprint lives in the bag OR in the world, never in the saved state
                item.m_gridPos = place;
                item.m_equipped = equipped;
                if (item.m_customData == null) item.m_customData = new System.Collections.Generic.Dictionary<string, string>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VABackpacks] Camp: could not restore the bag's item state: {ex.Message}");
            }
        }

        // A bag with nothing in it: no contents blob and no special-slot items.
        private static bool IsHollowBag(ItemDrop.ItemData item)
        {
            if (item?.m_customData == null) return true;
            if (item.m_customData.TryGetValue(BackpackApi.CUSTOM_DATA_KEY, out var blob) && !string.IsNullOrEmpty(blob)) return false;
            if (CampCapture.HasBlueprint(item)) return false;   // 1.0.29: a bag carrying a packed camp is never overwritten
            return !item.m_customData.Keys.Any(k => k.StartsWith(SpecialSlotKeyPrefix, StringComparison.Ordinal));
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
