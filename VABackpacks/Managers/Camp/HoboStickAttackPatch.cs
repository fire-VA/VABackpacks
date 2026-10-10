// HoboStickAttackPatch — MMB camp-deploy gesture for any backpack that
// is also a weapon (TwoHandedWeapon). Originally HoboStick-only; now
// dispatches against BackpackManager.SlamDeployBackpacks, so adding a
// new prefab name to that set is enough to give it the same MMB →
// sledgehammer slam → camp deploy flow.
//
// When the player has one of those weapons equipped in their right
// hand and presses MMB:
//
//   1. The standard primary-attack swing animation plays (so the
//      player gets visual feedback).
//   2. We raycast from the camera to find where the swing "lands" —
//      same heuristic Valheim uses for melee aiming.
//   3. The weapon is removed from the player's inventory, an
//      ItemDrop of it is spawned at the hit point, and the existing
//      camp deploy flow runs from there.
//
// Pack-up is unchanged — Shift+B (or whatever GestureKey) on the
// resulting chest restores the weapon to the player.
//
// Why MMB and not LMB: LMB is the player's regular weapon attack and
// we don't want these weapons to feel useless in a pinch. MMB is the
// special "deploy at target" verb. Conventional weapons on Valheim
// don't bind anything to MMB by default for armed players, so we're
// not overriding any existing function.

using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    [HarmonyPatch]
    public static class HoboStickAttackPatch
    {
        // Kept for backward references in this codebase. The set in
        // BackpackManager.SlamDeployBackpacks is what actually drives
        // dispatch — this is just the original first entry.
        public const string HOBOSTICK_PREFAB = "HoboStick";
        // Reasonable melee-distance range for the deploy raycast. Matches
        // vanilla weapon m_attackRange ballpark for short-staff weapons.
        private const float MAX_AIM_DIST = 8f;

        private static int _lastFiredFrame = -1;

        /// <summary>
        /// Polled from BackpackCampManager.Update each frame. Detects the
        /// MMB-press while a slam-deploy weapon-bag is equipped and routes
        /// to the special-case deploy. Returns true if it consumed the
        /// gesture (caller should skip its normal MMB handling, although
        /// the vanilla unarmed-kick suppress patch also applies).
        /// </summary>
        public static bool TryHandleHoboStickMmb()
        {
            if (!ConfigManager.Instance.configCampEnabled.Value) return false;
            if (Time.frameCount == _lastFiredFrame) return true;     // dedupe within frame

            var player = Player.m_localPlayer;
            if (player == null) return false;

            // Must have a slam-deploy weapon-bag equipped in the right hand.
            var right = player.m_rightItem;
            if (right == null || right.m_dropPrefab == null) return false;
            string prefabName = right.m_dropPrefab.name;
            if (!BackpackManager.IsSlamDeployBackpack(prefabName)) return false;

            if (!Input.GetMouseButtonDown(2)) return false;       // MMB
            if (BackpackManager.IsFiresInputCaptured()) return false;   // 1.0.43: not while a text field has input
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                return false;                                     // Shift+MMB has other meanings

            _lastFiredFrame = Time.frameCount;

            // Find the impact position — same heuristic the player would
            // expect from a swing: forward-cast from the camera, fall back
            // to "X meters in front of the player at sea level" if the ray
            // misses.
            Vector3 targetPos;
            var cam = Camera.main;
            if (cam != null && Physics.Raycast(cam.transform.position, cam.transform.forward,
                                                out RaycastHit hit, MAX_AIM_DIST,
                                                Physics.DefaultRaycastLayers,
                                                QueryTriggerInteraction.Ignore))
            {
                targetPos = hit.point;
            }
            else
            {
                Vector3 fwd = player.transform.forward; fwd.y = 0f; fwd.Normalize();
                targetPos = player.transform.position + fwd * 3f;
            }

            // Visual feedback: trigger the player's primary-attack
            // animation. With the weapon equipped this is the same
            // sledgehammer-style swing LMB would do — we just want the
            // animation/SFX/screen-shake to play before the camp pops in.
            try { player.StartAttack(null, false); } catch { /* defensive */ }

            // Schedule the actual deploy for after the swing's slam
            // frame lands. The manager owns the coroutine so the delay
            // survives even if this static patch context goes out of
            // scope between frames.
            float delay = ConfigManager.Instance.configCampHoboStickDeployDelay.Value;
            BackpackCampManager.EnsureExists();
            BackpackCampManager.Instance?.RequestSlamDeploy(player, prefabName, targetPos, delay);

            return true;
        }
    }
}
