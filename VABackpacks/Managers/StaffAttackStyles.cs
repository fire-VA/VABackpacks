using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// 1.0.64 ([wishbone], Fire 10-14, approved table in the lead's thread): each early staff gets its own primary behaviour and a
    /// secondary attack. [Early Magic] Staff Attack Styles = false (default; Fire: new = config OFF). OFF = today's bolts, unchanged.
    /// Built only from vanilla parts so the game networks it: per-staff projectile clones (StaffProjectiles), vanilla Aoe clones for the
    /// clouds / rings / mines, an SE_Stats heal-over-time per healer staff, and vanilla Attack objects for the secondaries.
    /// Primary (on the styled projectile): homing (Ember light, Bogfire / Sunfire / Moonsilver strong), a chain jump (Thunderpine),
    /// spawn-on-hit AoEs (Ember splash, Bogfire poison cloud, Sunfire fire burst, Hearthwood / Mossbark heal mist), a drain
    /// (Bloodroot, health back to the caster), a group mend every 3rd shot (Barley-crown). Frostglass slows through vanilla frost.
    /// Secondary: a fan (Ember), a charged piercing lance (Frostglass), self-centred AoEs at your feet (Solar Flare, Hearth Ring,
    /// Harvest Circle), the vanilla protection bubble (Warding Moss), and two-step placed AoEs (Static Mine, Miasma Pool, Blood Mist,
    /// Moon Ring): press to show Core's ground ghost ring (the class skills' GroundRingManager, by reflection), aim, press again to
    /// cast; block / Escape / a weapon change cancels. Eitr cost + a per-staff cooldown. Animations are the player controller's own
    /// staff states (staff_summon = the ground strike, staff_shield = support raise, staff_charging + staff_charge_attack = charge).
    /// Every use logs one point-of-use line: "[EarlyMagic] &lt;staff&gt; &lt;primary|secondary&gt;: &lt;what&gt; -&gt; &lt;target&gt; &lt;applied&gt;".
    /// </summary>
    internal static class StaffAttackStyles
    {
        internal static ConfigEntry<bool> Enabled;
        internal static bool On => Enabled != null && Enabled.Value;

        internal enum Sec { Fan, Mine, Pool, Lance, Flare, Ring, Ward, Mist, MoonRing, Harvest }

        internal sealed class Def
        {
            public string Staff, PrimaryAnim, SecAnim;
            public float Homing;                 // degrees a second the bolt turns toward a foe (0 = none)
            public bool Chain, Drain, MendEvery3, HealOnHit;
            public string HitAoe;                // spawn-on-hit AoE kind ("" = none)
            public float HitRadius, HitValue;    // its radius and damage / heal
            public string Element;               // the staff's element (Damage())
            public Sec Secondary;
            public bool TwoStep, SelfCentred;
            public float Radius, Ttl, Interval, Value, Eitr, Cooldown;
            public Color Tint;
            // Fire 10-14 (option B, "the real misile ai ... clean and tuned"): the primary spawns vanilla's Mistile creature with its OWN
            // seek + explode AI, tuned through its own fields (speed, turn), set to the player side, its target the caster's aim
            // target (else its own pick, restricted to the caster's enemies), a lifetime and a per-caster cap.
            public bool Mistile;
            public float MistileSpeed, MistileTurn, MistileLife;
        }

        // Values are q1 / the staff's base; quality, skill, FLO caster stats and Ascension scale them (no flat caps).
        private static readonly Def[] Defs =
        {
            new Def { Staff = "EmberWand", PrimaryAnim = "staff_fireball", Homing = 90f, HitAoe = "fire", HitRadius = 1.5f, HitValue = 3f, Element = "fire",
                Secondary = Sec.Fan, SecAnim = "staff_rapidfire", Value = 0.6f, Eitr = 10f, Cooldown = 4f, Tint = new Color(1f, 0.55f, 0.15f) },
            new Def { Staff = "Thunderpine", PrimaryAnim = "staff_lightningshot", Chain = true, Element = "lightning",
                Secondary = Sec.Mine, SecAnim = "staff_summon", TwoStep = true, Radius = 2f, Ttl = 4f, Interval = 1f, Value = 10f, Eitr = 12f, Cooldown = 6f, Tint = new Color(0.55f, 0.75f, 1f) },
            new Def { Staff = "Bogfire", PrimaryAnim = "staff_frostorb", Mistile = true, MistileSpeed = 7f, MistileTurn = 400f, MistileLife = 8f, HitAoe = "poison", HitRadius = 3f, HitValue = 10f, Element = "poison",
                Secondary = Sec.Pool, SecAnim = "staff_summon", TwoStep = true, Radius = 3f, Ttl = 5f, Interval = 1f, Value = 8f, Eitr = 14f, Cooldown = 8f, Tint = new Color(0.35f, 0.85f, 0.2f) },
            new Def { Staff = "Frostglass", PrimaryAnim = "staff_rapidfire", Element = "frost",
                Secondary = Sec.Lance, SecAnim = "staff_charge_attack", Value = 2f, Eitr = 14f, Cooldown = 6f, Tint = new Color(0.7f, 0.95f, 1f) },
            new Def { Staff = "Sunfire", PrimaryAnim = "staff_frostorb", Mistile = true, MistileSpeed = 9f, MistileTurn = 500f, MistileLife = 8f, HitAoe = "fire", HitRadius = 3f, HitValue = 20f, Element = "fire",
                Secondary = Sec.Flare, SecAnim = "staff_summon", SelfCentred = true, Radius = 4f, Ttl = 0.3f, Interval = 1f, Value = 40f, Eitr = 18f, Cooldown = 10f, Tint = new Color(1f, 0.95f, 0.6f) },
            new Def { Staff = "Hearthwood", PrimaryAnim = "staff_fireball", HealOnHit = true, HitAoe = "heal", HitRadius = 1.5f, HitValue = 4f, Element = "holy",
                Secondary = Sec.Ring, SecAnim = "staff_summon", SelfCentred = true, Radius = 4f, Ttl = 4f, Interval = 1f, Value = 8f, Eitr = 10f, Cooldown = 8f, Tint = new Color(1f, 0.85f, 0.45f) },
            new Def { Staff = "Mossbark", PrimaryAnim = "staff_fireball", HitAoe = "heal", HitRadius = 2f, HitValue = 8f, Element = "holy",
                Secondary = Sec.Ward, SecAnim = "staff_shield", Eitr = 12f, Cooldown = 10f, Tint = new Color(0.65f, 1f, 0.6f) },
            new Def { Staff = "Bloodroot", PrimaryAnim = "staff_lightningshot", Drain = true, Element = "holy",
                Secondary = Sec.Mist, SecAnim = "staff_summon", TwoStep = true, Radius = 4f, Ttl = 5f, Interval = 1f, Value = 13f, Eitr = 14f, Cooldown = 8f, Tint = new Color(0.75f, 0.08f, 0.1f) },
            new Def { Staff = "Moonsilver", PrimaryAnim = "staff_thrust", Homing = 120f, Element = "holy",
                Secondary = Sec.MoonRing, SecAnim = "staff_summon", TwoStep = true, Radius = 4f, Ttl = 4f, Interval = 1f, Value = 18f, Eitr = 16f, Cooldown = 9f, Tint = new Color(0.85f, 0.9f, 1f) },
            new Def { Staff = "Barleycrown", PrimaryAnim = "staff_fireball", MendEvery3 = true, Element = "holy",
                Secondary = Sec.Harvest, SecAnim = "staff_summon", SelfCentred = true, Radius = 6f, Ttl = 6f, Interval = 1f, Value = 24f, Eitr = 18f, Cooldown = 10f, Tint = new Color(1f, 0.95f, 0.75f) },
        };

        private static Def DefOf(string staffId) => Defs.FirstOrDefault(d => d.Staff == staffId);

        internal static void Bind(ConfigFile cfg, ConfigSync sync)
        {
            Enabled = cfg.Bind("Early Magic", "Staff Attack Styles", false,
                "Each early staff gets its own primary behaviour (homing bombs, chain bolts, heal mists, drains) and a secondary attack on the secondary-attack key (eitr + cooldown; placed ones show a ground ring first, press again to cast). False = the plain bolts, as before 1.0.64. On also fires each staff's own styled projectile even when Staff Projectiles = Shared (the behaviours ride on it). Server-synced.");
            sync?.AddConfigEntry(Enabled);
            Enabled.SettingChanged += (_, __) => EarlyMagic.ReapplyStaffs();
            SecClip.Enabled = cfg.Bind("Early Magic", "Staff secondary clips", false,
                "With Staff Attack Styles on: each staff's secondary plays a matching full-body animation (ground slam for the at-your-feet AoEs, a two-hand cast for the placed rings, a quick cast for the fan / ward) instead of the vanilla staff pose, and the secondary goes off on the clip's own cast frame. Needs Fires Unified Core 0.2.493+ and RPGClasses 0.1.141+ (the clips); without them, the vanilla pose. Client-side.");
        }

        // ── built once per process (ZNetScene.Awake): AoE clones + heal SEs ───────────────────────────────────────────────
        private static GameObject _holder;
        private static readonly Dictionary<string, GameObject> Aoes = new Dictionary<string, GameObject>();   // key -> AoE prefab clone
        private static readonly List<GameObject> Registered = new List<GameObject>();
        private static GameObject _wardBubble;   // vanilla StaffShield's own protection AoE
        private static GameObject _lanceProjectile;

        internal static void Build(ZNetScene zs)
        {
            if (zs == null) return;
            if (_holder == null) { _holder = new GameObject("VABackpacks_StaffAttackStyles"); _holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(_holder); }
            GameObject healDonor = zs.GetPrefab("DvergerStaffHeal_aoe") ?? zs.GetPrefab("shaman_heal_aoe");
            if (healDonor == null) Debug.LogWarning($"{MyMod.PluginName}: staff attack styles: no DvergerStaffHeal_aoe / shaman_heal_aoe to clone the rings from; heal AoEs off");
            foreach (var d in Defs)
            {
                if (!string.IsNullOrEmpty(d.HitAoe)) MakeAoe(d.Staff + "_hit", healDonor, d, d.HitAoe, d.HitRadius, d.HitAoe == "heal" ? 2f : 2f, 1f, d.HitValue);
                switch (d.Secondary)
                {
                    case Sec.Mine: MakeAoe(d.Staff + "_sec", healDonor, d, "lightning", d.Radius, d.Ttl, d.Interval, d.Value); break;
                    case Sec.Pool: MakeAoe(d.Staff + "_sec", healDonor, d, "poison", d.Radius, d.Ttl, d.Interval, d.Value); break;
                    case Sec.Flare: MakeAoe(d.Staff + "_sec", healDonor, d, "fire", d.Radius, d.Ttl, d.Interval, d.Value); break;
                    case Sec.Ring: case Sec.Mist: case Sec.MoonRing: case Sec.Harvest: MakeAoe(d.Staff + "_sec", healDonor, d, "heal", d.Radius, d.Ttl, d.Interval, d.Value); break;
                }
                if (d.MendEvery3) MakeAoe(d.Staff + "_mend", healDonor, d, "heal", 5f, 2f, 1f, 12f);
            }
            _wardBubble = zs.GetPrefab("staff_shield_aoe");
            if (_wardBubble == null) Debug.LogWarning($"{MyMod.PluginName}: staff attack styles: no staff_shield_aoe; Warding Moss has no bubble");
            var named = (Dictionary<int, GameObject>)AccessTools.Field(typeof(ZNetScene), "m_namedPrefabs").GetValue(zs);
            foreach (var go in Registered)
                if (go != null && zs.GetPrefab(go.name) == null) { zs.m_prefabs.Add(go); named[go.name.GetStableHashCode()] = go; }
            Debug.Log($"{MyMod.PluginName}: staff attack styles: {Aoes.Count} AoE(s) built ({string.Join(", ", Aoes.Keys.ToArray())}), {Registered.Count} registered; ward bubble {(_wardBubble != null)}; mode {(On ? "ON" : "OFF")}");
        }

        private static void MakeAoe(string key, GameObject donor, Def d, string kind, float radius, float ttl, float interval, float value)
        {
            if (donor == null || (Aoes.TryGetValue(key, out var have) && have != null)) return;
            var go = UnityEngine.Object.Instantiate(donor, _holder.transform, false);
            go.name = "FiresAoe_" + key;
            var aoe = go.GetComponent<Aoe>();
            if (aoe == null) { UnityEngine.Object.Destroy(go); return; }
            bool heal = kind == "heal";
            aoe.m_name = go.name;
            aoe.m_useAttackSettings = false;
            aoe.m_radius = radius;
            aoe.m_ttl = ttl;
            aoe.m_hitInterval = interval;
            aoe.m_hitOwner = heal;
            aoe.m_hitFriendly = heal;
            aoe.m_hitEnemy = !heal;
            aoe.m_hitProps = false;
            aoe.m_skill = d.Element == "holy" ? Skills.SkillType.BloodMagic : Skills.SkillType.ElementalMagic;
            aoe.m_damage = new HitData.DamageTypes();
            aoe.m_damagePerLevel = new HitData.DamageTypes();
            aoe.m_statusEffect = "";
            if (heal)
            {
                var se = HealEffect(d.Staff + "_" + key, value, ttl, d.Secondary == Sec.MoonRing && key.EndsWith("_sec"), d.Secondary == Sec.Harvest && key.EndsWith("_sec"));
                aoe.m_statusEffect = se.name;
            }
            else
            {
                switch (kind)
                {
                    case "fire": aoe.m_damage.m_fire = value; aoe.m_damagePerLevel.m_fire = value * 0.2f; break;
                    case "poison": aoe.m_damage.m_poison = value; aoe.m_damagePerLevel.m_poison = value * 0.2f; break;
                    case "lightning": aoe.m_damage.m_lightning = value; aoe.m_damagePerLevel.m_lightning = value * 0.2f; break;
                }
                if (d.Secondary == Sec.Flare && key.EndsWith("_sec")) aoe.m_attackForce = 60f;
            }
            Tint(go, heal ? d.Tint : d.Tint);
            Aoes[key] = go;
            if (go.GetComponent<ZNetView>() != null) Registered.Add(go);
        }

        // ── heal over time: one SE_Stats per AoE, scaled by the caster's quality and skill (SetLevel) ───────────────────────
        private static readonly List<StatusEffect> HealSEs = new List<StatusEffect>();

        private static StatusEffect HealEffect(string id, float total, float ttl, bool cleanse, bool stamina)
        {
            var se = ScriptableObject.CreateInstance<SE_FiresStaffHeal>();
            se.name = "SE_FiresHeal_" + id;
            se.m_name = "Mending";
            se.m_tooltip = "Healing over time.";
            se.m_ttl = Mathf.Max(ttl, 2f);
            se.m_baseHeal = total;
            se.m_healthOverTimeInterval = 1f;
            se.m_healthOverTime = total;
            se.m_cleanse = cleanse;
            if (stamina) se.m_staminaRegenMultiplier = 1.15f;
            se.m_icon = ObjectDB.instance?.GetStatusEffect("Rested".GetStableHashCode())?.m_icon;
            HealSEs.Add(se);
            return se;
        }

        internal static void RegisterEffects(ObjectDB db)
        {
            if (db == null) return;
            foreach (var se in HealSEs)
                if (se != null && db.GetStatusEffect(se.name.GetStableHashCode()) == null) db.m_StatusEffects.Add(se);
        }

        // ── per staff: on / off (EarlyMagic.ApplyStaffData calls this for every staff) ─────────────────────────────────────
        private static readonly Dictionary<string, Attack> OrigSecondary = new Dictionary<string, Attack>();
        private static readonly Dictionary<string, string> OrigPrimaryAnim = new Dictionary<string, string>();
        private static readonly Dictionary<string, Attack.AttackType> OrigPrimaryType = new Dictionary<string, Attack.AttackType>();
        // [seasons] review: the clone's OWN spawn-on-hit (the vanilla fireball's explosion etc.), kept so OFF = 1.0.63 exactly
        private static readonly Dictionary<string, GameObject> OrigSpawnOnHit = new Dictionary<string, GameObject>();

        internal static void Apply(ItemDrop.ItemData.SharedData sh, string staffId, GameObject projectile)
        {
            var d = DefOf(staffId);
            if (sh == null || d == null) return;
            if (!OrigSecondary.ContainsKey(staffId)) OrigSecondary[staffId] = sh.m_secondaryAttack;
            if (!OrigPrimaryAnim.ContainsKey(staffId) && sh.m_attack != null) OrigPrimaryAnim[staffId] = sh.m_attack.m_attackAnimation;
            var proj = projectile != null ? projectile.GetComponent<Projectile>() : null;
            var homing = projectile != null ? projectile.GetComponent<FiresStaffHoming>() : null;
            if (proj != null && !OrigSpawnOnHit.ContainsKey(staffId)) OrigSpawnOnHit[staffId] = proj.m_spawnOnHit;
            if (!OrigPrimaryType.ContainsKey(staffId) && sh.m_attack != null) OrigPrimaryType[staffId] = sh.m_attack.m_attackType;
            if (!On)
            {
                sh.m_secondaryAttack = OrigSecondary[staffId];
                if (sh.m_attack != null && OrigPrimaryAnim.TryGetValue(staffId, out var anim)) sh.m_attack.m_attackAnimation = anim;
                if (sh.m_attack != null && OrigPrimaryType.TryGetValue(staffId, out var type)) sh.m_attack.m_attackType = type;
                if (proj != null && OrigSpawnOnHit.TryGetValue(staffId, out var origHit)) proj.m_spawnOnHit = origHit;
                if (homing != null) homing.enabled = false;
                return;
            }
            if (sh.m_attack != null) sh.m_attack.m_attackAnimation = d.PrimaryAnim;
            // the Mistile staffs fire no projectile: the trigger frame spawns the Mistile (Attack_OnAttackTrigger)
            if (sh.m_attack != null) sh.m_attack.m_attackType = d.Mistile ? Attack.AttackType.None : OrigPrimaryType[staffId];
            if (proj != null) proj.m_spawnOnHit = !string.IsNullOrEmpty(d.HitAoe) && Aoes.TryGetValue(d.Staff + "_hit", out var hitAoe) ? hitAoe : OrigSpawnOnHit[staffId];
            if (projectile != null && d.Homing > 0f)
            {
                if (homing == null) homing = projectile.AddComponent<FiresStaffHoming>();
                homing.TurnDegrees = d.Homing;
                homing.enabled = true;
            }
            sh.m_secondaryAttack = MakeSecondary(sh, d, projectile);
            Debug.Log($"{MyMod.PluginName}: staff attack styles: FiresStaff_{staffId}: primary '{d.PrimaryAnim}'{(d.Homing > 0f ? $", homing {d.Homing:0}/s" : "")}{(d.Chain ? ", chain" : "")}{(d.Drain ? ", drain 20%" : "")}{(d.MendEvery3 ? ", mend every 3rd" : "")}{(proj?.m_spawnOnHit != null ? ", on hit " + proj.m_spawnOnHit.name : "")}; "
                + $"secondary {d.Secondary} '{d.SecAnim}'{(d.TwoStep ? " (two-step ring)" : d.SelfCentred ? " (at your feet)" : "")}, {d.Eitr:0} eitr, {d.Cooldown:0} s cooldown");
        }

        private static Attack MakeSecondary(ItemDrop.ItemData.SharedData sh, Def d, GameObject projectile)
        {
            var a = (Attack)AccessTools.Method(typeof(object), "MemberwiseClone").Invoke(sh.m_attack, null);
            a.m_attackAnimation = d.SecAnim;
            a.m_attackEitr = d.Eitr;
            a.m_attackStamina = 4f;
            a.m_spawnOnTrigger = null;
            switch (d.Secondary)
            {
                case Sec.Fan:
                    a.m_attackType = Attack.AttackType.Projectile;
                    a.m_projectiles = 3; a.m_projectileAccuracy = 14f; a.m_projectileAccuracyMin = 14f;
                    a.m_damageMultiplier = d.Value;
                    break;
                case Sec.Lance:
                    a.m_attackType = Attack.AttackType.Projectile;
                    a.m_chargeAnimationBool = "staff_charging";
                    a.m_damageMultiplier = d.Value;
                    a.m_projectileVel = Mathf.Max(a.m_projectileVel, 40f);
                    a.m_attackProjectile = LanceProjectile(projectile) ?? a.m_attackProjectile;
                    break;
                case Sec.Ward:
                    a.m_attackType = Attack.AttackType.None;
                    a.m_spawnOnTrigger = _wardBubble;
                    break;
                default:   // the AoE secondaries: no projectile; OnAttackTrigger spawns the AoE (at your feet or at the aim point)
                    a.m_attackType = Attack.AttackType.None;
                    break;
            }
            return a;
        }

        private static GameObject LanceProjectile(GameObject frost)
        {
            if (_lanceProjectile != null || frost == null) return _lanceProjectile;
            _lanceProjectile = UnityEngine.Object.Instantiate(frost, _holder.transform, false);
            _lanceProjectile.name = "FiresBolt_FrostLance";
            foreach (var h in _lanceProjectile.GetComponentsInChildren<FiresStaffHoming>(true)) UnityEngine.Object.Destroy(h);
            _lanceProjectile.transform.localScale = frost.transform.localScale * 1.6f;
            if (ZNetScene.instance != null && ZNetScene.instance.GetPrefab(_lanceProjectile.name) == null && _lanceProjectile.GetComponent<ZNetView>() != null)
            {
                ZNetScene.instance.m_prefabs.Add(_lanceProjectile);
                ((Dictionary<int, GameObject>)AccessTools.Field(typeof(ZNetScene), "m_namedPrefabs").GetValue(ZNetScene.instance))[_lanceProjectile.name.GetStableHashCode()] = _lanceProjectile;
                Registered.Add(_lanceProjectile);
            }
            return _lanceProjectile;
        }

        // ── which staff is in hand (local player) ─────────────────────────────────────────────────────────────────────────
        private static string StaffOf(Humanoid h)
        {
            var item = h?.GetCurrentWeapon();
            string n = item?.m_dropPrefab != null ? item.m_dropPrefab.name : null;
            return n != null && n.StartsWith("FiresStaff_", StringComparison.Ordinal) ? n.Substring("FiresStaff_".Length) : null;
        }

        // ── secondary: cooldown + two-step aiming ─────────────────────────────────────────────────────────────────────────
        private static readonly Dictionary<string, float> ReadyAt = new Dictionary<string, float>();
        private static string _aimingStaff;
        private static Vector3 _aimPoint;
        private static bool _aimValid;
        private static object _ghost;
        private static bool _lastWasSecondary;
        private static Vector3? _castPoint;
        private static int _shots;

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
        private static class Humanoid_StartAttack
        {
            private static bool Prefix(Humanoid __instance, bool secondaryAttack, ref bool __result)
            {
                if (!On || __instance != Player.m_localPlayer) return true;
                string staff = StaffOf(__instance);
                var d = DefOf(staff);
                if (d == null) return true;
                if (!secondaryAttack) { _lastWasSecondary = false; _castPoint = null; return true; }
                if (ReadyAt.TryGetValue(staff, out float at) && Time.time < at)
                {
                    __instance.Message(MessageHud.MessageType.TopLeft, $"{d.Secondary} ready in {at - Time.time:0.0} s");
                    __result = false; return false;
                }
                if (d.TwoStep)
                {
                    if (_aimingStaff != staff)
                    {
                        _aimingStaff = staff;
                        AimClip.Play(__instance, "fires.staff.cast.hold");   // 1.0.66: the aiming pose while the ghost ring is up (no attack runs now)
                        Debug.Log($"[EarlyMagic] {staff} secondary: {d.Secondary} aiming (press again to cast; block / Escape cancels)");
                        __result = false; return false;
                    }
                    if (!_aimValid) { __instance.Message(MessageHud.MessageType.TopLeft, "Can't place it there"); __result = false; return false; }
                    _castPoint = _aimPoint;
                    StopAiming(false);
                }
                else _castPoint = null;
                _lastWasSecondary = true;
                return true;
            }

            private static void Postfix(Humanoid __instance, bool secondaryAttack, bool __result)
            {
                if (!On || __instance != Player.m_localPlayer || !secondaryAttack || !__result) return;
                string staff = StaffOf(__instance);
                var d = DefOf(staff);
                if (d == null) return;
                ReadyAt[staff] = Time.time + d.Cooldown;
                Debug.Log($"[EarlyMagic] {staff} secondary: {d.Secondary} cast ('{d.SecAnim}', {d.Eitr:0} eitr){(_castPoint.HasValue ? $" at {_castPoint.Value:F1}" : "")}; next in {d.Cooldown:0} s");
                SecClip.Begin(__instance, staff, d);   // 1.0.68: the matching UAL clip (key off = nothing)
            }
        }

        private static void StopAiming(bool cancelled)
        {
            if (_aimingStaff != null && cancelled) Debug.Log($"[EarlyMagic] {_aimingStaff} secondary: aiming cancelled");
            _aimingStaff = null;
            AimClip.Stop(Player.m_localPlayer);
            ReleaseGhost();
        }

        [HarmonyPatch(typeof(Player), "Update")]
        private static class Player_Update
        {
            private static void Postfix(Player __instance)
            {
                if (__instance == Player.m_localPlayer) SecClip.Tick(__instance);
                if (_aimingStaff == null || __instance != Player.m_localPlayer) return;
                if (!On || StaffOf(__instance) != _aimingStaff || __instance.IsBlocking() || Input.GetKeyDown(KeyCode.Escape) || __instance.IsDead()) { StopAiming(true); return; }
                var d = DefOf(_aimingStaff);
                _aimValid = AimPoint(__instance, 20f, out _aimPoint);
                ShowGhost(_aimPoint, d.Radius, _aimValid, __instance);
            }
        }

        private static bool AimPoint(Player p, float range, out Vector3 point)
        {
            point = p.transform.position + p.transform.forward * 4f;
            var cam = GameCamera.instance;
            if (cam == null) return false;
            int mask = LayerMask.GetMask("Default", "static_solid", "terrain", "vehicle", "piece");
            if (Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, range + 10f, mask) && Vector3.Distance(hit.point, p.transform.position) <= range)
            { point = hit.point; return true; }
            Vector3 fwd = cam.transform.forward; fwd.y = 0f;
            point = p.transform.position + (fwd.sqrMagnitude > 0.01f ? fwd.normalized : p.transform.forward) * range;
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(point, out float ground)) point.y = ground;
            return false;
        }

        // Core's ground ring (the class skills' GroundRingManager), soft-linked.
        private static MethodInfo _show, _release;
        private static Type _kindType;
        private static bool _ringLooked;

        private static void ShowGhost(Vector3 at, float radius, bool valid, Player p)
        {
            if (!_ringLooked)
            {
                _ringLooked = true;
                var mgr = AccessTools.TypeByName("FiresCore.Npc.Archetypes.Effects.GroundRingManager");
                _kindType = AccessTools.TypeByName("FiresCore.Npc.Archetypes.Effects.GroundRingKind");
                if (mgr != null && _kindType != null)
                {
                    _show = mgr.GetMethod("Show", new[] { _kindType, typeof(Vector3), typeof(float), typeof(float), typeof(Character), typeof(bool) });
                    _release = mgr.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "Release" && m.GetParameters().Length == 1);
                }
                Debug.Log($"{MyMod.PluginName}: staff attack styles: ghost ring {(_show != null ? "Core GroundRingManager" : "not available (Core missing or older): aiming works without a ring")}");
            }
            ReleaseGhost();
            if (_show == null) return;
            try
            {
                object kind = Enum.Parse(_kindType, valid ? "Damage" : (Enum.IsDefined(_kindType, "Invalid") ? "Invalid" : "Damage"));
                _ghost = _show.Invoke(null, new object[] { kind, at, radius, 0.25f, p, true });
            }
            catch { _show = null; }
        }

        private static void ReleaseGhost()
        {
            if (_ghost != null && _release != null) { try { _release.Invoke(null, new[] { _ghost }); } catch { } }
            _ghost = null;
        }

        // ── the trigger frame: spawn the secondary's AoE, count primaries for Barley-crown ────────────────────────────────
        [HarmonyPatch(typeof(Attack), nameof(Attack.OnAttackTrigger))]
        private static class Attack_OnAttackTrigger
        {
            // 1.0.68: while a secondary clip owns the cast, only the clip's own event (SecClip) lets the trigger run, never twice
            private static bool Prefix(Attack __instance) => !SecClip.Blocks(__instance);

            private static void Postfix(Attack __instance)
            {
                if (!On) return;
                var who = Traverse.Create(__instance).Field("m_character").GetValue<Humanoid>();
                if (who == null || who != Player.m_localPlayer) return;
                string staff = StaffOf(who);
                var d = DefOf(staff);
                if (d == null) return;
                var item = who.GetCurrentWeapon();
                bool secondary = item != null && __instance.m_attackAnimation == d.SecAnim && _lastWasSecondary;
                if (!secondary)
                {
                    if (d.Mistile) { SpawnMistile(who, d, item); return; }
                    if (d.MendEvery3 && ++_shots % 3 == 0 && Aoes.TryGetValue(d.Staff + "_mend", out var mend))
                    { Spawn(mend, who, who.transform.position, item); Debug.Log($"[EarlyMagic] {staff} primary: 3rd shot -> group mend {mend.name} at your feet (5 m)"); }
                    return;
                }
                _lastWasSecondary = false;
                if (d.Secondary == Sec.Ward) { Debug.Log($"[EarlyMagic] {staff} secondary: Warding Moss -> vanilla protection bubble (staff_shield_aoe) on you + allies near you"); return; }
                if (d.Secondary == Sec.Fan || d.Secondary == Sec.Lance) return;   // projectiles: logged on hit
                if (!Aoes.TryGetValue(d.Staff + "_sec", out var aoe)) { Debug.LogWarning($"[EarlyMagic] {staff} secondary: no AoE built for {d.Secondary}"); return; }
                Vector3 at = d.SelfCentred || !_castPoint.HasValue ? who.transform.position : _castPoint.Value;
                Spawn(aoe, who, at, item);
                Debug.Log($"[EarlyMagic] {staff} secondary: {d.Secondary} -> {aoe.name} at {at:F1} (radius {d.Radius:0.#} m, {d.Ttl:0.#} s)");
                _castPoint = null;
            }
        }

        // ── the real Mistile (Bogfire / Sunfire primary) ──────────────────────────────────────────────────────────────────
        private sealed class MistileRun
        {
            public Character Caster, Body;
            public Def Def;
            public HitData.DamageTypes Damage;
            public float DieAt;
            public bool Burst;
            public Vector3 Last;
        }

        private const int MistilesPerCaster = 2;
        private static readonly List<MistileRun> Mistiles = new List<MistileRun>();
        private static readonly FieldInfo TargetField = AccessTools.Field(typeof(MonsterAI), "m_targetCreature");

        private static void SpawnMistile(Humanoid caster, Def d, ItemDrop.ItemData item)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Mistile") : null;
            if (prefab == null) { Debug.LogWarning($"[EarlyMagic] {d.Staff} primary: no 'Mistile' prefab in ZNetScene; nothing spawned"); return; }
            // the per-caster cap: the oldest goes first
            var mine = Mistiles.Where(m => m.Caster == caster && m.Body != null).ToList();
            while (mine.Count >= MistilesPerCaster) { Despawn(mine[0], "cap (" + MistilesPerCaster + " per caster)"); mine.RemoveAt(0); }

            Vector3 at = caster.GetEyePoint() + caster.GetLookDir() * 1.5f;
            var go = UnityEngine.Object.Instantiate(prefab, at, Quaternion.LookRotation(caster.GetLookDir()));   // ours: this client owns it
            var body = go.GetComponent<Character>();
            var ai = go.GetComponent<MonsterAI>();
            if (body == null || ai == null) { Debug.LogWarning($"[EarlyMagic] {d.Staff} primary: the Mistile has no Character/MonsterAI; removed"); ZNetScene.instance.Destroy(go); return; }
            body.m_faction = Character.Faction.Players;   // the player side: it never picks the caster, allies or other players (PvP off)
            body.m_flyFastSpeed = d.MistileSpeed; body.m_flySlowSpeed = d.MistileSpeed; body.m_flyTurnSpeed = d.MistileTurn;
            ai.m_enableHuntPlayer = false;
            Tint(go, d.Tint);
            Character target = AimTarget(caster);
            if (target != null) TargetField?.SetValue(ai, target);
            var dmg = item != null ? item.GetDamage() : new HitData.DamageTypes();
            dmg.Modify(1f + caster.GetSkillLevel(Skills.SkillType.ElementalMagic) / 100f);   // tier + quality (item) and skill: no cap
            var run = new MistileRun { Caster = caster, Body = body, Def = d, Damage = dmg, DieAt = Time.time + d.MistileLife, Last = at };
            Mistiles.Add(run);
            Debug.Log($"[EarlyMagic] {d.Staff} primary: Mistile spawned at {at:F1} (faction Players, speed {d.MistileSpeed:0.#}, turn {d.MistileTurn:0}, life {d.MistileLife:0} s, cap {MistilesPerCaster}/caster, burst {dmg.GetTotalDamage():0.#}: fire {dmg.m_fire:0.#} poison {dmg.m_poison:0.#}); target {(target != null ? target.m_name : "its own pick among your enemies")}");
        }

        // the creature under the crosshair (within 40 m) if it's the caster's enemy and not a passive animal
        private static Character AimTarget(Humanoid caster)
        {
            var cam = GameCamera.instance;
            if (cam == null) return null;
            foreach (var h in Physics.SphereCastAll(cam.transform.position, 0.6f, cam.transform.forward, 40f, LayerMask.GetMask("character", "character_net", "character_noenv")).OrderBy(x => x.distance))
            {
                var c = h.collider != null ? h.collider.GetComponentInParent<Character>() : null;
                if (c != null && c != caster && ValidPrey(caster, c)) return c;
            }
            return null;
        }

        // the caster's enemy, alive, and not a passive / neutral animal (no stray aggro on deer, boars that weren't provoked, etc.)
        private static bool ValidPrey(Character caster, Character c) =>
            c != null && !c.IsDead() && BaseAI.IsEnemy(caster, c) && c.m_faction != Character.Faction.AnimalsVeg && !c.IsPlayer();

        private static void Despawn(MistileRun r, string why)
        {
            if (r.Body != null)
            {
                var nv = r.Body.GetComponent<ZNetView>();
                if (nv != null && nv.IsValid() && nv.IsOwner()) ZNetScene.instance.Destroy(r.Body.gameObject);
            }
            Debug.Log($"[EarlyMagic] {r.Def.Staff} primary: Mistile despawned: {why}");
            r.Body = null;
        }

        [HarmonyPatch(typeof(Player), "Update")]
        private static class Player_Update_Mistiles
        {
            private static void Postfix(Player __instance)
            {
                if (Mistiles.Count == 0 || __instance != Player.m_localPlayer) return;
                for (int i = Mistiles.Count - 1; i >= 0; i--)
                {
                    var r = Mistiles[i];
                    if (r.Body == null || r.Body.IsDead()) { if (r.Body != null || !r.Burst) Debug.Log($"[EarlyMagic] {r.Def.Staff} primary: Mistile despawned: {(r.Burst ? "exploded" : "gone")}"); Mistiles.RemoveAt(i); continue; }
                    if (!On || Time.time >= r.DieAt) { Despawn(r, On ? $"lifetime {r.Def.MistileLife:0} s" : "Staff Attack Styles turned off"); Mistiles.RemoveAt(i); continue; }
                    r.Last = r.Body.transform.position;
                    var ai = r.Body.GetComponent<MonsterAI>();
                    var cur = TargetField?.GetValue(ai) as Character;
                    if (ai != null && !ValidPrey(r.Caster, cur))
                    {
                        // its own pick, kept to the caster's enemies: the nearest valid one within its view range
                        var pick = Character.GetAllCharacters().Where(c => ValidPrey(r.Caster, c) && Vector3.Distance(c.transform.position, r.Body.transform.position) <= ai.m_viewRange)
                            .OrderBy(c => Vector3.Distance(c.transform.position, r.Body.transform.position)).FirstOrDefault();
                        TargetField?.SetValue(ai, pick);
                        if (pick != null) Debug.Log($"[EarlyMagic] {r.Def.Staff} primary: Mistile target -> {pick.m_name}{(cur != null ? $" (was {cur.m_name}, not a valid foe)" : "")}");
                    }
                }
            }
        }

        // its explosion: the staff's damage, the caster as attacker (so the caster's PvP / friendly-fire rules apply), the burst AoE once
        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class Character_Damage_Mistile
        {
            [HarmonyPriority(Priority.High)]
            private static void Prefix(Character __instance, HitData hit)
            {
                if (hit == null || Mistiles.Count == 0) return;
                var from = hit.GetAttacker();
                var r = from != null ? Mistiles.FirstOrDefault(m => m.Body == from) : null;
                if (r == null || r.Caster == null || __instance == r.Body) return;   // its own self-damage (9999999, vanilla) must still kill it
                if (__instance == r.Caster || (__instance.IsPlayer() && !BaseAI.IsEnemy(r.Caster, __instance))) { hit.m_damage = new HitData.DamageTypes(); return; }   // never the caster or a friendly player
                hit.m_damage = r.Damage.Clone();
                hit.SetAttacker(r.Caster);
                hit.m_skill = Skills.SkillType.ElementalMagic;
                if (!r.Burst && Aoes.TryGetValue(r.Def.Staff + "_hit", out var burst)) { Spawn(burst, r.Caster as Humanoid, r.Last, (r.Caster as Humanoid)?.GetCurrentWeapon()); }
                r.Burst = true;
                Debug.Log($"[EarlyMagic] {r.Def.Staff} primary: Mistile exploded on {__instance.m_name}: {hit.m_damage.GetTotalDamage():0.#} (fire {hit.m_damage.m_fire:0.#} poison {hit.m_damage.m_poison:0.#}) as {r.Caster.m_name}'s hit{(Aoes.ContainsKey(r.Def.Staff + "_hit") ? " + " + r.Def.Staff + " burst" : "")}");
            }
        }

        private static void Spawn(GameObject prefab, Humanoid owner, Vector3 at, ItemDrop.ItemData item)
        {
            var go = UnityEngine.Object.Instantiate(prefab, at, Quaternion.identity);
            go.GetComponent<IProjectile>()?.Setup(owner, Vector3.zero, -1f, null, item, null);
        }

        // ── primary on-hit extras: chain, lance pierce, the hit log ───────────────────────────────────────────────────────
        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
        private static class Projectile_Setup
        {
            private static void Postfix(Projectile __instance, HitData hitData)
            {
                if (!On || hitData == null || !__instance.name.StartsWith("FiresBolt_Blood", StringComparison.Ordinal)) return;
                __instance.m_healthReturn = hitData.GetTotalDamage() * 0.2f;   // Bloodroot: 20% of the bolt's damage back to the caster
            }
        }

        [HarmonyPatch(typeof(Projectile), nameof(Projectile.OnHit))]
        private static class Projectile_OnHit
        {
            private static void Postfix(Projectile __instance, Collider collider)
            {
                if (!On || __instance == null || !__instance.name.StartsWith("FiresBolt_", StringComparison.Ordinal)) return;
                var owner = Traverse.Create(__instance).Field("m_owner").GetValue<Character>();
                if (owner == null || owner != Player.m_localPlayer) return;
                var hit = collider != null ? Projectile.FindHitObject(collider)?.GetComponent<Character>() : null;
                string bolt = __instance.name.Replace("(Clone)", "");
                string staff = StaffOf(owner as Humanoid) ?? "?";
                // 1.0.65 (R-FCC-ON2: "-> Human" read as a self-hit): vanilla OnHit returns early for the owner and non-enemies (IsValidTarget:
                // no damage, no spawn-on-hit, the bolt flies on), so those contacts are named as passes, and chain / pierce never start on them
                if (hit != null && (hit == owner || !BaseAI.IsEnemy(owner, hit)))
                {
                    Debug.Log($"[EarlyMagic] {staff} {(bolt.EndsWith("FrostLance") ? "secondary" : "primary")}: {bolt} passed through {(hit == owner ? "the caster" : "friendly " + hit.m_name)} (vanilla ignores it: no damage, the bolt flies on)");
                    return;
                }
                Debug.Log($"[EarlyMagic] {staff} {(bolt.EndsWith("FrostLance") ? "secondary" : "primary")}: {bolt} -> {(hit != null ? hit.m_name : "terrain")}{(__instance.m_spawnOnHit != null ? $" (+{__instance.m_spawnOnHit.name})" : "")}{(__instance.m_healthReturn > 0f ? $", drain {__instance.m_healthReturn:0.#} health to you" : "")}");
                if (hit == null) return;
                if (bolt.StartsWith("FiresBolt_Thunder")) ChainFrom(owner, hit, __instance);
                if (bolt.EndsWith("FrostLance")) Pierce(owner, hit, __instance);
            }
        }

        private static void ChainFrom(Character owner, Character first, Projectile p)
        {
            var next = Character.GetAllCharacters().Where(c => c != null && c != first && !c.IsDead() && BaseAI.IsEnemy(owner, c) && Vector3.Distance(c.transform.position, first.transform.position) <= 5f)
                .OrderBy(c => Vector3.Distance(c.transform.position, first.transform.position)).FirstOrDefault();
            if (next == null) return;
            var h = new HitData { m_point = next.GetCenterPoint(), m_dir = (next.transform.position - first.transform.position).normalized, m_skill = Skills.SkillType.ElementalMagic };
            h.m_damage.m_lightning = Traverse.Create(p).Field("m_damage").GetValue<HitData.DamageTypes>().m_lightning * 0.6f;
            h.SetAttacker(owner);
            next.Damage(h);
            Debug.Log($"[EarlyMagic] Thunderpine primary: chain -> {next.m_name} {h.m_damage.m_lightning:0.#} lightning (60%)");
        }

        private static void Pierce(Character owner, Character first, Projectile p)
        {
            Vector3 dir = Traverse.Create(p).Field("m_vel").GetValue<Vector3>(); dir.y = 0f; dir = dir.sqrMagnitude > 0.01f ? dir.normalized : owner.transform.forward;
            var dmg = Traverse.Create(p).Field("m_damage").GetValue<HitData.DamageTypes>();
            int n = 0;
            foreach (var c in Character.GetAllCharacters())
            {
                if (c == null || c == first || c.IsDead() || !BaseAI.IsEnemy(owner, c)) continue;
                Vector3 rel = c.transform.position - first.transform.position; rel.y = 0f;
                float along = Vector3.Dot(rel, dir);
                if (along <= 0f || along > 8f || (rel - dir * along).magnitude > 1.2f) continue;
                var h = new HitData { m_point = c.GetCenterPoint(), m_dir = dir, m_skill = Skills.SkillType.ElementalMagic };
                h.m_damage = dmg.Clone();
                h.SetAttacker(owner);
                c.Damage(h);
                n++;
            }
            Debug.Log($"[EarlyMagic] Frostglass secondary: Glacial Lance pierced {n} more foe(s) in 8 m");
        }

        // ── the damage / heal applied (point of use) ──────────────────────────────────────────────────────────────────────
        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class Character_Damage
        {
            private static void Postfix(Character __instance, HitData hit)
            {
                if (!On || hit == null || hit.GetAttacker() != Player.m_localPlayer) return;
                string staff = StaffOf(Player.m_localPlayer);
                if (staff == null || hit.GetTotalDamage() <= 0f) return;
                Debug.Log($"[EarlyMagic] {staff} damage applied: {hit.GetTotalDamage():0.#} to {__instance.m_name} (blunt {hit.m_damage.m_blunt:0.#} fire {hit.m_damage.m_fire:0.#} frost {hit.m_damage.m_frost:0.#} lightning {hit.m_damage.m_lightning:0.#} poison {hit.m_damage.m_poison:0.#} spirit {hit.m_damage.m_spirit:0.#})");
            }
        }

        private static readonly int[] ColorProps = { Shader.PropertyToID("_Color"), Shader.PropertyToID("_TintColor"), Shader.PropertyToID("_BaseColor") };

        private static void Tint(GameObject root, Color tint)
        {
            foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true)) { var main = ps.main; main.startColor = new Color(tint.r, tint.g, tint.b, main.startColor.color.a); }
            foreach (var li in root.GetComponentsInChildren<Light>(true)) li.color = tint;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    var m = new Material(mats[i]);
                    foreach (int p in ColorProps) if (m.HasProperty(p)) { var c = m.GetColor(p); m.SetColor(p, new Color(tint.r, tint.g, tint.b, c.a)); }
                    mats[i] = m;
                }
                r.sharedMaterials = mats;
            }
        }
    }

    /// <summary>
    /// 1.0.66 ([wishbone], Fire 10-14 "use our 250 animations"): Core's ClipSlots (FiresCore.Animation, Core 0.2.493+), by an exact-name
    /// Type.GetType (no assembly scan; [generator]'s rule), for the moments where NO vanilla attack runs: the two-step aiming pose.
    /// Attack frames stay vanilla: their projectile / AoE fires from the vanilla clip's animation event. RPGClasses registers the
    /// "fires.staff.*" ids (its firesmovementclips bundle); without them, or an older Core, Play returns false and nothing changes.
    /// </summary>
    internal static class AimClip
    {
        private static bool _looked;
        private static System.Reflection.MethodInfo _play, _stop;
        private static string _playing;

        private static void Look()
        {
            if (_looked) return;
            _looked = true;
            var t = Type.GetType("FiresCore.Animation.ClipSlots, FiresUnifiedCore", false);
            _play = t?.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).FirstOrDefault(m => m.Name == "Play" && m.GetParameters().Length == 3);
            _stop = t?.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).FirstOrDefault(m => m.Name == "Stop" && m.GetParameters().Length == 2);
            Debug.Log($"{MyMod.PluginName}: staff attack styles: clip slots {(_play != null ? "found (Core ClipSlots)" : "not available (Core older than 0.2.493): vanilla poses")}");
        }

        internal static void Play(Character body, string id)
        {
            Look();
            if (_play == null || body == null) return;
            bool ok = false;
            try { ok = _play.Invoke(null, new object[] { body, id, null }) is bool b && b; } catch { }
            _playing = ok ? id : null;
            Debug.Log($"[EarlyMagic] aim pose: clip '{id}' {(ok ? "playing" : "not played (id not registered / can't play): vanilla pose")}");
        }

        internal static void Stop(Character body)
        {
            if (_playing == null || _stop == null || body == null) return;
            try { _stop.Invoke(null, new object[] { body, -1f }); } catch { }
            Debug.Log($"[EarlyMagic] aim pose: clip '{_playing}' stopped");
            _playing = null;
        }
    }

    /// <summary>
    /// 1.0.68 ([wishbone], Fire 10-15 via the lead: the secondaries with real animations that match the action): behind
    /// "[Early Magic] Staff secondary clips" (default off). Core's ClipSlots swaps the attack state for the UAL clip, so vanilla's
    /// attack anim event (Attack.OnAttackTrigger) no longer fires on its own. Each clip gets a ClipSlots event at its cast frame
    /// ("fires.staff.trigger"), and that event runs the vanilla trigger, once. If no event comes within the clip's time + 0.5 s, the
    /// trigger runs anyway and the line says "fallback" (a missed event is visible, the cast is never lost). Lance keeps the vanilla
    /// charge pose (its hold is round B). Point-of-use line per cast: staff, attack, clip, and how the trigger ran.
    /// </summary>
    internal static class SecClip
    {
        internal static ConfigEntry<bool> Enabled;
        private const string Evt = "fires.staff.trigger";
        private const float FallbackSeconds = 2.5f;
        // secondary -> (clip id registered by RPGClasses' MovementClips, the cast frame as normalized time)
        private static readonly Dictionary<StaffAttackStyles.Sec, (string id, float at)> Map = new Dictionary<StaffAttackStyles.Sec, (string, float)>
        {
            { StaffAttackStyles.Sec.Flare, ("fires.staff.groundslam", 0.45f) },       // self AoE at your feet: ground slam
            { StaffAttackStyles.Sec.Ring, ("fires.staff.groundslam", 0.45f) },
            { StaffAttackStyles.Sec.Harvest, ("fires.staff.groundslam", 0.45f) },
            { StaffAttackStyles.Sec.Mine, ("fires.staff.channel.release", 0.35f) },   // placed (two-step ring): two-hand cast
            { StaffAttackStyles.Sec.Pool, ("fires.staff.channel.release", 0.35f) },
            { StaffAttackStyles.Sec.Mist, ("fires.staff.channel.release", 0.35f) },
            { StaffAttackStyles.Sec.MoonRing, ("fires.staff.channel.release", 0.35f) },
            { StaffAttackStyles.Sec.Fan, ("fires.staff.cast", 0.35f) },               // quick casts
            { StaffAttackStyles.Sec.Ward, ("fires.staff.cast", 0.35f) },
        };
        private static bool _looked;
        private static MethodInfo _play, _isRegistered, _addEvent;
        private static readonly HashSet<string> Evented = new HashSet<string>();
        private static Attack _pending;
        private static string _staff, _clip;
        private static StaffAttackStyles.Sec _sec;
        private static float _since;
        private static bool _firing;

        private static bool On => Enabled != null && Enabled.Value && StaffAttackStyles.On;

        private static void Look()
        {
            if (_looked) return;
            _looked = true;
            var t = Type.GetType("FiresCore.Animation.ClipSlots, FiresUnifiedCore", false);
            if (t == null) { Debug.Log($"{MyMod.PluginName}: staff secondary clips: no Core ClipSlots (older than 0.2.493): vanilla poses"); return; }
            var all = t.GetMethods(BindingFlags.Public | BindingFlags.Static);
            _play = all.FirstOrDefault(m => m.Name == "Play" && m.GetParameters().Length == 3);
            _isRegistered = all.FirstOrDefault(m => m.Name == "IsRegistered" && m.GetParameters().Length == 1);
            _addEvent = all.FirstOrDefault(m => m.Name == "AddEvent" && m.GetParameters().Length == 3);
            var ev = t.GetEvent("ClipEvent", BindingFlags.Public | BindingFlags.Static);
            if (ev != null && _addEvent != null)
                ev.AddEventHandler(null, Delegate.CreateDelegate(ev.EventHandlerType, typeof(SecClip).GetMethod(nameof(OnClipEvent), BindingFlags.NonPublic | BindingFlags.Static)));
            Debug.Log($"{MyMod.PluginName}: staff secondary clips: Core ClipSlots {(_play != null && ev != null && _addEvent != null ? "found (Play + AddEvent + ClipEvent)" : "without AddEvent / ClipEvent: vanilla poses")}");
            if (ev == null || _addEvent == null) _play = null;
        }

        /// <summary>The secondary's StartAttack went through: play its clip and take over the trigger.</summary>
        internal static void Begin(Humanoid who, string staff, StaffAttackStyles.Def d)
        {
            if (!On || who == null || d == null || !Map.TryGetValue(d.Secondary, out var m)) return;
            Look();
            if (_play == null) return;
            try
            {
                if (!(_isRegistered.Invoke(null, new object[] { m.id }) is bool reg && reg))
                { Debug.Log($"[EarlyMagic] {staff} secondary: {d.Secondary} clip '{m.id}' not registered (RPGClasses' clips off / older): vanilla pose"); return; }
                if (Evented.Add(m.id)) _addEvent.Invoke(null, new object[] { m.id, m.at, Evt });
                var attack = Traverse.Create(who).Field("m_currentAttack").GetValue<Attack>();
                if (attack == null) { Debug.Log($"[EarlyMagic] {staff} secondary: {d.Secondary} no current attack: vanilla pose"); return; }
                bool ok = _play.Invoke(null, new object[] { who, m.id, null }) is bool b && b;
                if (!ok) { Debug.Log($"[EarlyMagic] {staff} secondary: {d.Secondary} clip '{m.id}' didn't play: vanilla pose"); return; }
                _pending = attack; _staff = staff; _clip = m.id; _sec = d.Secondary; _since = Time.time;
                Debug.Log($"[EarlyMagic] {staff} secondary: {d.Secondary} clip '{m.id}' playing, cast at {m.at:0.00} of the clip ('{d.SecAnim}' replaced)");
            }
            catch (Exception e) { Debug.LogWarning($"[EarlyMagic] {staff} secondary: clip failed ({e.GetBaseException().Message}): vanilla pose"); _pending = null; }
        }

        /// <summary>True = skip this vanilla trigger (the clip owns it and this isn't the clip's event).</summary>
        internal static bool Blocks(Attack a) => _pending != null && a == _pending && !_firing;

        private static void OnClipEvent(Character who, string clipId, string evtName)
        {
            if (evtName != Evt || _pending == null || who != Player.m_localPlayer || clipId != _clip) return;
            Fire("clip event");
        }

        internal static void Tick(Player p)
        {
            if (_pending == null) return;
            if (p.IsDead()) { _pending = null; return; }
            if (Time.time - _since > FallbackSeconds) Fire($"fallback: no clip event in {FallbackSeconds:0.0} s");
        }

        private static void Fire(string how)
        {
            var a = _pending;
            _pending = null;
            // 1.0.69 ([seasons] review): a stagger / dodge / weapon swap / death ends the attack before its cast frame; never cast
            // on a stale Attack
            var me = Player.m_localPlayer;
            if (me == null || me.IsDead() || !ReferenceEquals(Traverse.Create(me).Field("m_currentAttack").GetValue<Attack>(), a))
            { Debug.Log($"[EarlyMagic] {_staff} secondary: {_sec} attack ended before the cast frame ({how}): cast dropped (clip '{_clip}', {Time.time - _since:0.00} s after the press)"); return; }
            _firing = true;
            try { a.OnAttackTrigger(); }
            catch (Exception e) { Debug.LogWarning($"[EarlyMagic] {_staff} secondary: trigger threw {e.GetBaseException().Message}"); }
            finally { _firing = false; }
            Debug.Log($"[EarlyMagic] {_staff} secondary: {_sec} cast frame via {how} (clip '{_clip}', {Time.time - _since:0.00} s after the press)");
        }
    }

    /// <summary>1.0.64: a staff bolt that turns toward the nearest foe ahead (owner-side; vanilla syncs the projectile's position).</summary>
    internal sealed class FiresStaffHoming : MonoBehaviour
    {
        public float TurnDegrees = 90f;
        private static readonly FieldInfo VelField = AccessTools.Field(typeof(Projectile), "m_vel");
        private static readonly FieldInfo OwnerField = AccessTools.Field(typeof(Projectile), "m_owner");
        private Projectile _p;
        private ZNetView _nview;
        private Character _target;
        private float _nextLook;

        private void Awake() { _p = GetComponent<Projectile>(); _nview = GetComponent<ZNetView>(); }

        private void FixedUpdate()
        {
            if (_p == null || !StaffAttackStyles.On || (_nview != null && _nview.IsValid() && !_nview.IsOwner())) return;
            var owner = OwnerField?.GetValue(_p) as Character;
            Vector3 vel = (Vector3)VelField.GetValue(_p);
            if (owner == null || vel.sqrMagnitude < 0.01f) return;
            if (Time.time >= _nextLook || _target == null || _target.IsDead())
            {
                _nextLook = Time.time + 0.25f;
                Vector3 fwd = vel.normalized;
                _target = Character.GetAllCharacters().Where(c => c != null && !c.IsDead() && BaseAI.IsEnemy(owner, c)
                        && Vector3.Distance(c.GetCenterPoint(), transform.position) <= 25f && Vector3.Angle(fwd, c.GetCenterPoint() - transform.position) <= 60f)
                    .OrderBy(c => Vector3.Distance(c.GetCenterPoint(), transform.position)).FirstOrDefault();
            }
            if (_target == null) return;
            Vector3 want = (_target.GetCenterPoint() - transform.position).normalized * vel.magnitude;
            VelField.SetValue(_p, Vector3.RotateTowards(vel, want, TurnDegrees * Mathf.Deg2Rad * Time.fixedDeltaTime, 0f));
        }
    }

    /// <summary>1.0.64: the healer staffs' heal over time, scaled by the caster's item quality and Blood Magic skill (no flat cap).</summary>
    internal sealed class SE_FiresStaffHeal : SE_Stats
    {
        public float m_baseHeal;
        public bool m_cleanse;
        private static readonly string[] Cleansable = { "Poison", "Burning", "Frost", "Wet", "Tared", "Smoked" };

        public override void SetLevel(int itemLevel, float skillLevel)
        {
            base.SetLevel(itemLevel, skillLevel);
            int q = Mathf.Max(1, itemLevel);
            float heal = m_baseHeal * (1f + 0.2f * (q - 1)) * (1f + skillLevel / 100f);
            m_healthOverTime = heal;
            m_healthOverTimeTicks = m_healthOverTimeDuration > 0f ? m_healthOverTimeDuration / m_healthOverTimeInterval : m_ttl / m_healthOverTimeInterval;
            m_healthOverTimeTickHP = m_healthOverTimeTicks > 0f ? heal / m_healthOverTimeTicks : heal;
            Debug.Log($"[EarlyMagic] heal applied: {name} on {m_character?.m_name ?? "?"}: {heal:0.#} health over {m_ttl:0.#} s (quality {q}, skill {skillLevel:0}){(m_cleanse ? ", cleanse" : "")}");
        }

        public override void Setup(Character character)
        {
            base.Setup(character);
            if (!m_cleanse || character == null) return;
            foreach (string n in Cleansable)
            {
                int h = n.GetStableHashCode();
                if (character.GetSEMan().HaveStatusEffect(h)) { character.GetSEMan().RemoveStatusEffect(h); Debug.Log($"[EarlyMagic] Moonsilver secondary: Moon Ring cleansed '{n}' from {character.m_name}"); break; }
            }
        }
    }
}
