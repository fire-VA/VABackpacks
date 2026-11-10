// Bedroll (bypass slot): lay the bag's bedroll down as a bed, away from base, to sleep and set the spawn point (Traveler's sleeping bag).
//   1.0.119 (Fire 10-23: "the bedroll should lay down a deerskin rug, and that would be interactable as a bed"): it lays down
//              BedrollRug's FiresBagUp_BedrollRug (rug_deer's look + a Bed), not vanilla's bed; the "Bedroll piece" config key is gone
//              (an existing cfg would keep "bed" and override the design).
//   lay down : right-click the slotted Bedroll in the rail -> the bedroll rug goes down
//              BedrollAhead m in front of the wearer, built by the wearer, so vanilla's own Bed rules apply (except roof + fire, below): it needed a roof
//              and a fire to sleep, no enemies near, and Use on it sets the spawn point.
//   1.0.118 (Fire 10-23, live: "does not actually place down a bedroll ... and it should not require a roof and fire"): a bedroll
//              sleeps WITHOUT a roof or a fire (Bed.CheckExposure / CheckFire pass for it); "no enemies near" and the daily uses stay.
//              It goes down on the wearer's own floor (a short down-cast from just above their feet), not on whatever is highest
//              (ZoneSystem.GetSolidHeight casts from the sky, so indoors it landed on the ROOF), and 2 s later the log says whether it is
//              still there.
//   uses     : the tier's uses per in-game day ("Bedroll I uses per day" 1 / 2 / 3), counted on the slotted upgrade (BagUp.Bedroll.day /
//              .uses), so they travel with the bag and survive a relog.
//   one at a time: laying a new bedroll takes up the previous one (when loaded). Right-click again while standing within PackUpRadius
//              of your bedroll packs it up (no use spent). It stays put otherwise, so a spawn point set on it keeps working.
//   no free wood: a bedroll gives no resources back when removed with the hammer (Piece.DropResources skipped for it).
// Local player only.

using System;
using System.Globalization;
using BepInEx.Configuration;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public sealed class BedrollOption : IBagUpgradeOption
    {
        public const string UpgradeId = "Bedroll";
        private static readonly int[] UsesDefaults = { 1, 2, 3 };
        private const float BedrollAhead = 1.6f, PackUpRadius = 4f;
        private const string OwnerKey = "va_bedroll_owner";
        private const string KeyDay = "BagUp.Bedroll.day", KeyUses = "BagUp.Bedroll.uses", KeyZdo = "BagUp.Bedroll.zdo";
        private static readonly ConfigEntry<int>[] Uses = new ConfigEntry<int>[BagUpgrades.TierCount];
        internal static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks.Bedroll");

        public string Id => UpgradeId;
        public string Name => "Bedroll";
        public string Slot => BackpackSpecialSlots.SlotBypass;
        public string Donor => "DeerHide";
        public string[] Recipes => new[]
        {
            "forge,1,Bronze:5,LeatherScraps:10,DeerHide:5",
            "forge,3,Iron:10,WolfPelt:5,Feathers:10",
            "forge,5,BlackMetal:10,LinenThread:5,Feathers:20",
        };

        public string Describe(int tier) => $"Right-click it in the bag to lay a bedroll down: sleep and set your spawn away from base ({UsesFor(tier)} per day; no roof or fire needed).";

        public void Bind(ConfigFile cfg, ConfigSync sync)
        {
            for (int t = 0; t < BagUpgrades.TierCount; t++)
            {
                Uses[t] = cfg.Bind(BagUpgrades.Section, $"Bedroll {BagUpgrades.TierNumerals[t]} uses per day", UsesDefaults[t],
                    new ConfigDescription($"How many times a day a bag with Bedroll {BagUpgrades.TierNumerals[t]} can lay its bedroll down. Server-synced.",
                        new AcceptableValueRange<int>(1, 20)));
                sync?.AddConfigEntry(Uses[t]);
            }
            BagUpgrades.UpgradeRightClick += OnRightClick;
        }

        private static int UsesFor(int tier) => tier < 1 ? 0 : Mathf.Max(1, Uses[tier - 1]?.Value ?? UsesDefaults[tier - 1]);

        private static void Say(Player p, string text) { try { p?.Message(MessageHud.MessageType.Center, text); } catch { } }

        private static bool OnRightClick(ItemDrop.ItemData bag, string kind, ItemDrop.ItemData upgrade)
        {
            if (kind != BackpackSpecialSlots.SlotBypass || BagUpgrades.SlottedTier(bag, UpgradeId) < 1) return false;
            Player me = Player.m_localPlayer;
            if (me == null) return false;
            try
            {
                int tier = BagUpgrades.SlottedTier(bag, UpgradeId);
                // pack up: the wearer's own bedroll within reach
                GameObject near = FindOwnBedroll(me, PackUpRadius);
                if (near != null)
                {
                    Vector3 at = near.transform.position;
                    Remove(near);
                    upgrade.m_customData.Remove(KeyZdo);
                    BagUpgrades.SaveUpgradeState(bag, kind);
                    Log.LogInfo($"[Bedroll] packed up at ({at.x:0.0}, {at.z:0.0})");
                    Say(me, "Bedroll packed up");
                    return true;
                }
                // a use: per in-game day
                int day = EnvMan.instance != null ? EnvMan.instance.GetDay() : 0;
                int usedDay = Read(upgrade, KeyDay), used = usedDay == day ? Read(upgrade, KeyUses) : 0;
                int limit = UsesFor(tier);
                if (used >= limit) { Log.LogDebug($"[Bedroll] refused: {used}/{limit} use(s) today (day {day})"); Say(me, $"Bedroll: used {used}/{limit} today"); return true; }
                // 1.0.129: on a halo ring the spot is ahead along the floor and the floor cast runs along the ring's down
                Vector3 feet = me.transform.position;
                bool ring = Camp.CampFrame.InField(feet);
                Vector3 up = ring ? Camp.CampFrame.UpAt(feet) : Vector3.up;
                Vector3 ahead = ring ? Vector3.ProjectOnPlane(me.transform.forward, up).normalized : me.transform.forward;
                Vector3 pos = feet + ahead * BedrollAhead;
                if (ring) pos = RingFloorAt(pos, up);
                else pos.y = FloorAt(pos, me.transform.position.y);
                if (!PrivateArea.CheckAccess(pos, 0f, true, false)) { Log.LogDebug($"[Bedroll] refused: a ward at ({pos.x:0.0}, {pos.z:0.0})"); return true; }
                GameObject prefab = BedrollRug.Prefab(ZNetScene.instance);
                if (prefab == null || prefab.GetComponent<Bed>() == null) { Log.LogWarning($"[Bedroll] no {BedrollRug.PrefabName} prefab (see the build line above)"); return true; }
                // one at a time: the previous one (when loaded) is taken up first
                GameObject old = FindOwnBedroll(me, float.MaxValue);
                if (old != null) { Vector3 o = old.transform.position; Remove(old); Log.LogDebug($"[Bedroll] took up the previous bedroll at ({o.x:0.0}, {o.z:0.0})"); }
                GameObject go = UnityEngine.Object.Instantiate(prefab, pos, ring ? Quaternion.LookRotation(-ahead, up) : Quaternion.LookRotation(-me.transform.forward, Vector3.up));
                if (ring) Log.LogDebug($"[Bedroll] halo ring: laid along the ring's up ({up.x:F2},{up.y:F2},{up.z:F2})");
                go.GetComponent<Piece>()?.SetCreator(me.GetPlayerID(), Splatform.PlatformManager.DistributionPlatform.LocalUser.PlatformUserID);
                ZNetView view = go.GetComponent<ZNetView>();
                if (view != null && view.IsValid())
                {
                    view.GetZDO().Set(OwnerKey, me.GetPlayerID());
                    upgrade.m_customData[KeyZdo] = view.GetZDO().m_uid.ToString();
                }
                upgrade.m_customData[KeyDay] = day.ToString(CultureInfo.InvariantCulture);
                upgrade.m_customData[KeyUses] = (used + 1).ToString(CultureInfo.InvariantCulture);
                BagUpgrades.SaveUpgradeState(bag, kind);
                Log.LogInfo($"[Bedroll] laid down at ({pos.x:0.0}, {pos.y:0.00}, {pos.z:0.0}) (wearer's feet y {me.transform.position.y:0.00}), use {used + 1}/{limit} today (day {day}); sleeps without a roof or fire, no enemies near");
                me.StartCoroutine(CheckStillThere(go, pos));
                Say(me, $"Bedroll rug laid down ({used + 1}/{limit} today)");
            }
            catch (Exception e) { Log.LogWarning($"[Bedroll] right-click failed: {e.Message}"); }
            return true;
        }

        private const float FloorCastUp = 1.2f, FloorCastDepth = 4f;
        private static int s_floorMask;
        // The floor under the spot at the wearer's own level: a cast from just above their feet down FloorCastDepth m (pieces + ground),
        // so a roof or upper floor above them is never chosen; no hit = their own height.
        private static float FloorAt(Vector3 at, float feetY)
        {
            if (s_floorMask == 0) s_floorMask = LayerMask.GetMask("piece", "Default", "static_solid", "terrain", "Default_small");
            return Physics.Raycast(new Vector3(at.x, feetY + FloorCastUp, at.z), Vector3.down, out RaycastHit hit, FloorCastDepth, s_floorMask, QueryTriggerInteraction.Ignore) ? hit.point.y : feetY;
        }

        // 1.0.129: the halo-ring floor under a spot: a cast from just above it along the ring's down (no hit = the spot itself)
        private static Vector3 RingFloorAt(Vector3 at, Vector3 up)
        {
            if (s_floorMask == 0) s_floorMask = LayerMask.GetMask("piece", "Default", "static_solid", "terrain", "Default_small");
            return Physics.Raycast(at + up * FloorCastUp, -up, out RaycastHit hit, FloorCastDepth, s_floorMask, QueryTriggerInteraction.Ignore) ? hit.point : at;
        }

        // 2 s after laying it down: is it still in the world, and where (the proof for a "nothing appeared" report).
        private static IEnumerator CheckStillThere(GameObject go, Vector3 at)
        {
            yield return new WaitForSeconds(2f);
            bool there = go != null && go;
            int shown = there ? go.GetComponentsInChildren<Renderer>().Count(rd => rd != null && rd.enabled && rd.gameObject.activeInHierarchy) : 0;
            Log.LogDebug(there ? $"[Bedroll] 2 s later: in the world at ({go.transform.position.x:0.0}, {go.transform.position.y:0.00}, {go.transform.position.z:0.0}), {shown} renderer(s) showing"
                               : $"[Bedroll] 2 s later: GONE (laid at ({at.x:0.0}, {at.y:0.00}, {at.z:0.0})); something removed it");
        }

        private static int Read(ItemDrop.ItemData upgrade, string key) =>
            upgrade?.m_customData != null && upgrade.m_customData.TryGetValue(key, out string v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;

        private static bool IsBedroll(ZNetView view) => view != null && view.IsValid() && view.GetZDO().GetLong(OwnerKey, 0L) != 0L;

        private static GameObject FindOwnBedroll(Player me, float radius)
        {
            GameObject best = null; float bestD = radius;
            foreach (Bed bed in UnityEngine.Object.FindObjectsByType<Bed>(FindObjectsSortMode.None))
            {
                ZNetView view = bed != null ? bed.GetComponent<ZNetView>() : null;
                if (!IsBedroll(view) || view.GetZDO().GetLong(OwnerKey, 0L) != me.GetPlayerID()) continue;
                float d = Vector3.Distance(bed.transform.position, me.transform.position);
                if (d <= bestD) { bestD = d; best = bed.gameObject; }
            }
            return best;
        }

        private static void Remove(GameObject go)
        {
            ZNetView view = go != null ? go.GetComponent<ZNetView>() : null;
            if (view == null || !view.IsValid() || ZNetScene.instance == null) return;
            if (!view.IsOwner()) view.ClaimOwnership();
            ZNetScene.instance.Destroy(go);
        }

        // 1.0.118 (Fire 10-23): a bedroll needs no roof and no fire (vanilla Bed.CheckExposure / CheckFire pass for it)
        [HarmonyPatch]
        private static class NoRoofNoFire
        {
            private static readonly string[] Checks = { "CheckExposure", "CheckFire" };
            private static bool Prepare() => AccessTools.GetDeclaredMethods(typeof(Bed)).Any(m => Checks.Contains(m.Name) && m.ReturnType == typeof(bool));
            private static IEnumerable<MethodBase> TargetMethods() => AccessTools.GetDeclaredMethods(typeof(Bed)).Where(m => Checks.Contains(m.Name) && m.ReturnType == typeof(bool)).Cast<MethodBase>();
            private static bool Prefix(Bed __instance, MethodBase __originalMethod, ref bool __result)
            {
                if (!IsBedroll(__instance != null ? __instance.GetComponent<ZNetView>() : null)) return true;
                __result = true;
                Log.LogDebug($"[Bedroll] {__originalMethod.Name}: passes for a bedroll (no roof or fire needed)");
                return false;
            }
        }

        // no free wood: a bedroll gives nothing back when it is removed with the hammer
        [HarmonyPatch(typeof(Piece), nameof(Piece.DropResources))]
        private static class NoResources
        {
            private static bool Prefix(Piece __instance)
            {
                if (!IsBedroll(__instance != null ? __instance.GetComponent<ZNetView>() : null)) return true;
                Log.LogInfo("[Bedroll] removed with the hammer: no resources back");
                return false;
            }
        }
    }
}
