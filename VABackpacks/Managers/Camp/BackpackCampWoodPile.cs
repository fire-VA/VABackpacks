// BackpackCampWoodPile — converts a decorative wood-stack piece into a
// rate-limited wood source. Press E to take WOOD_PER_TAKE pieces of
// vanilla "Wood" from the pile, capped at DAILY_LIMIT per in-game day.
// Once the daily limit is hit the pile shows a flavor message until
// the world rolls over to the next day, when the quota resets.
//
// Persistence (per pile, on its ZNetView ZDO):
//   KEY_DAY      : last in-game day the quota was checked / reset on
//   KEY_TODAY    : wood given out so far on KEY_DAY's day
//   KEY_MARKER   : presence flag so the WearNTear.Awake re-attach patch
//                  can identify camp wood piles after a chunk reload
//                  without colliding with non-camp wood_stack pieces.

using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    public class BackpackCampWoodPile : MonoBehaviour, Hoverable, Interactable
    {
        public const string KEY_DAY     = "va_camp_wood_day";
        public const string KEY_TODAY   = "va_camp_wood_today";
        public const string KEY_MARKER  = "va_camp_woodpile";

        public const int DAILY_LIMIT  = 10;
        public const int WOOD_PER_TAKE = 2;

        private ZNetView _nview;

        private void Awake() => EnsureNview();

        private void EnsureNview()
        {
            if (_nview != null && _nview.IsValid()) return;
            _nview = GetComponent<ZNetView>();
        }

        // ── Daily-quota math ──────────────────────────────────────────

        private static int CurrentDay()
            => EnvMan.instance != null ? EnvMan.instance.GetDay() : 0;

        /// <summary>How much wood has been taken so far today, after
        /// applying any pending day-rollover reset.</summary>
        private int TakenToday(ZDO zdo)
        {
            if (zdo == null) return 0;
            int storedDay = zdo.GetInt(KEY_DAY, -1);
            int today = CurrentDay();
            if (storedDay != today)
            {
                // Day rollover (or first interaction ever) → reset.
                zdo.Set(KEY_DAY,   today);
                zdo.Set(KEY_TODAY, 0);
                return 0;
            }
            return zdo.GetInt(KEY_TODAY, 0);
        }

        private int RemainingToday(ZDO zdo) => Mathf.Max(0, DAILY_LIMIT - TakenToday(zdo));

        // ── Hoverable / Interactable ──────────────────────────────────

        public string GetHoverName() => "Camp Wood Pile";

        public string GetHoverText()
        {
            EnsureNview();
            var zdo = _nview?.GetZDO();
            if (zdo == null) return "Camp Wood Pile";

            int remaining = RemainingToday(zdo);
            if (remaining <= 0)
            {
                return Localization.instance.Localize(
                    $"Camp Wood Pile (out for today)\n" +
                    $"<color=#aaaaaa>Free wood quota refreshes at dawn.</color>");
            }
            return Localization.instance.Localize(
                $"Camp Wood Pile (×{remaining} today)\n" +
                $"[<color=yellow><b>$KEY_Use</b></color>] Take {Mathf.Min(WOOD_PER_TAKE, remaining)} Wood");
        }

        public float GetHoverOffset() => 0f;

        public bool Interact(Humanoid character, bool repeat, bool alt)
        {
            if (repeat) return false;
            var player = character as Player;
            if (player == null || player != Player.m_localPlayer) return false;

            EnsureNview();
            var zdo = _nview?.GetZDO();
            if (zdo == null) return false;

            int taken = TakenToday(zdo);
            int remaining = DAILY_LIMIT - taken;
            if (remaining <= 0)
            {
                player.Message(MessageHud.MessageType.Center,
                    "Out of free government wood for the day. Collect your own.");
                return false;
            }

            int give = Mathf.Min(WOOD_PER_TAKE, remaining);
            if (!GiveWoodToPlayer(player, give))
            {
                player.Message(MessageHud.MessageType.Center, "$inventory_full");
                return false;
            }

            // Claim ZDO ownership before write so the new value sticks
            // even if the local player isn't the original spawner.
            if (!_nview.IsOwner()) _nview.ClaimOwnership();
            zdo.Set(KEY_TODAY, taken + give);
            zdo.Set(KEY_DAY,   CurrentDay());

            player.Message(MessageHud.MessageType.TopLeft, $"+{give} Wood");
            return true;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;

        // ── Helpers ───────────────────────────────────────────────────

        private static bool GiveWoodToPlayer(Player player, int amount)
        {
            var prefab = ZNetScene.instance?.GetPrefab("Wood");
            if (prefab == null) return false;
            var drop = prefab.GetComponent<ItemDrop>();
            if (drop == null) return false;

            var added = player.GetInventory().AddItem("Wood", amount,
                drop.m_itemData.m_quality, drop.m_itemData.m_variant,
                player.GetPlayerID(), player.GetPlayerName(), cheated: false);
            return added != null;
        }

        // ── Re-attach on chunk reload ─────────────────────────────────
        //
        // Vanilla wood_stack respawns from its ZDO on chunk load WITHOUT
        // our MonoBehaviour. WearNTear.Awake fires once per spawned
        // instance, so we postfix it and re-attach if the marker tag is
        // present. Untagged wood_stacks elsewhere in the world are
        // unaffected (early return on missing marker).

        [HarmonyPatch(typeof(WearNTear), "Awake")]
        public static class WearNTear_Awake_AttachPatch
        {
            private static void Postfix(WearNTear __instance)
            {
                if (__instance == null) return;
                var nview = __instance.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) return;
                var zdo = nview.GetZDO();
                if (zdo == null) return;
                if (zdo.GetInt(KEY_MARKER, 0) != 1) return;
                if (__instance.GetComponent<BackpackCampWoodPile>() == null)
                    __instance.gameObject.AddComponent<BackpackCampWoodPile>();
            }
        }
    }
}
