// CampImmortalPatch — make every spawned camp piece invulnerable to
// damage so the camp can't be griefed (other players, mobs, fire,
// fall, etc.). Pieces still come down via our own pack-up flow which
// uses ZNetView.Destroy(), bypassing WearNTear entirely.
//
// Approach:
//   1. At deploy time, tag each spawned piece's ZDO with KEY_IMMORTAL.
//   2. Patch WearNTear.ApplyDamage (the central damage entry that
//      vanilla mobs/players/elements all funnel through) to early-return
//      when the tag is present. No damage is recorded, no health drops.
//
// The wood pile carries this same tag, but its E-interact handler
// (BackpackCampWoodPile.Interact → ZNetView.Destroy() when empty)
// doesn't go through ApplyDamage, so taking wood and emptying-the-pile
// still works. Damage from axes / mobs is blocked.

using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    [HarmonyPatch]
    public static class CampImmortalPatch
    {
        public const string KEY_IMMORTAL = "va_camp_immortal";

        /// <summary>
        /// Tag a freshly-spawned piece as a camp piece. Stamps the ZDO
        /// flag so the ApplyDamage patch (and any future protection
        /// patches) recognize it through chunk reloads. Safe to call
        /// on any GameObject — silently no-ops on objects without a
        /// valid ZNetView.
        /// </summary>
        public static void MakeImmortal(GameObject go)
        {
            if (go == null) return;
            var nview = go.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            var zdo = nview.GetZDO();
            if (zdo == null) return;
            zdo.Set(KEY_IMMORTAL, 1);
        }

        /// <summary>True if the piece's ZDO carries the immortal tag.</summary>
        public static bool IsImmortal(WearNTear wnt)
        {
            if (wnt == null) return false;
            var nview = wnt.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            var zdo = nview.GetZDO();
            return zdo != null && zdo.GetInt(KEY_IMMORTAL, 0) == 1;
        }

        // ── Damage gate ───────────────────────────────────────────────
        // ApplyDamage is the single point WearNTear processes any damage
        // event (axe hits, mob attacks, fire damage, fall, ash damage).
        // Returning false from a Prefix skips the vanilla method, and
        // setting __result = false signals "no significant damage" to
        // the caller. The piece's stored health stays untouched.

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.ApplyDamage))]
        [HarmonyPrefix]
        private static bool WearNTear_ApplyDamage_Prefix(WearNTear __instance, ref bool __result)
        {
            if (!IsImmortal(__instance)) return true;
            __result = false;
            return false;
        }

        // Damage(HitData hit) is the public entry point that mobs and
        // tools call before ApplyDamage. It RPCs through the network to
        // the owner, who then calls ApplyDamage. Skipping at THIS layer
        // saves the RPC roundtrip and prevents flash effects on the
        // piece — cheaper and less visually-noisy for the attacker.

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Damage))]
        [HarmonyPrefix]
        private static bool WearNTear_Damage_Prefix(WearNTear __instance)
        {
            return !IsImmortal(__instance);   // skip vanilla when immortal
        }

        // 1.0.35 (Fire: remove and move camp pieces with the hammer while decorating): a camp's own piece came with the bag,
        // not from the player's materials, so removing it gives nothing back (else deploy, remove, pack, repeat farms wood
        // and stone). Removal itself is vanilla's; the pack-up records what is left.
        [HarmonyPatch(typeof(Piece), nameof(Piece.DropResources))]
        [HarmonyPrefix]
        private static bool Piece_DropResources_Prefix(Piece __instance)
        {
            var wnt = __instance != null ? __instance.GetComponent<WearNTear>() : null;
            if (wnt == null || !IsImmortal(wnt)) return true;
            Debug.Log($"[VABackpacks] camp piece removed: {Utils.GetPrefabName(__instance.gameObject)} (a camp piece: no materials back; the next deploy places it fresh)");
            return false;
        }
    }
}
