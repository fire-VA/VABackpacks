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
    /// Each early staff gets its own primary behaviour and a
    /// secondary attack. [Early Magic] Staff Attack Styles = false (default: new features ship OFF). OFF = today's bolts, unchanged.
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
        // Tuning values (metres, seconds, degrees or damage shares).
        private const float AoeDamagePerLevelShare = 0.2f;   // AoE damage added per item quality level, as a share of its base
        private const float FlareAttackForce = 60f;          // Solar Flare knockback
        private const float FlareBurstBaseRadius = 1.6f;     // the radius the vanilla burst effect is drawn at; larger flares scale it up
        private const float FanHoldSeconds = 1.0f;           // how long the Ember fan keeps the player facing its aim
        private const float AimRange = 20f;                  // max distance of a two-step placement ring from the player
        private const float AimFallbackDistance = 4f;        // default ring point in front of the player before the camera aim is read
        private const float AimRingLifetime = 600f;          // ghost ring life; it is released explicitly on cast or cancel
        private const float DefaultHomingConeDegrees = 12f;  // default of the "Homing cone" setting
        private const float HomingTargetRange = 40f;         // how far homing looks for a target
        private const float HomingRetargetInterval = 0.25f;  // seconds between homing target re-checks
        private const float BloodrootLifestealShare = 0.2f;  // share of a Bloodroot bolt's damage returned to the caster
        private const float ChainJumpRadius = 5f;            // Thunderpine chain reach from the first target
        private const float ChainDamageShare = 0.6f;         // chained hit damage as a share of the bolt's lightning
        private const float PierceLength = 8f;               // pierce reach beyond the first target
        private const float PierceHalfWidth = 1.2f;          // pierce half-width
        private const float PartyLogInterval = 10f;          // seconds between repeated party-guard log lines per pair

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
            public string Element;               // the staff's element (Damage)
            public Sec Secondary;
            public bool TwoStep, SelfCentred;
            public float Radius, Ttl, Interval, Value, Eitr, Cooldown;
            public Color Tint;
            // The primary spawns vanilla's Mistile creature with its OWN
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
                Secondary = Sec.Flare, SecAnim = "staff_summon", TwoStep = true, Radius = 4f, Ttl = 0.3f, Interval = 1f, Value = 40f, Eitr = 18f, Cooldown = 10f, Tint = new Color(1f, 0.95f, 0.6f) },
            new Def { Staff = "Hearthwood", PrimaryAnim = "staff_fireball", HealOnHit = true, HitAoe = "heal", HitRadius = 1.5f, HitValue = 4f, Element = "holy",
                Secondary = Sec.Ring, SecAnim = "staff_summon", TwoStep = true, Radius = 4f, Ttl = 4f, Interval = 1f, Value = 8f, Eitr = 10f, Cooldown = 8f, Tint = new Color(1f, 0.85f, 0.45f) },
            new Def { Staff = "Mossbark", PrimaryAnim = "staff_fireball", HitAoe = "heal", HitRadius = 2f, HitValue = 8f, Element = "holy",
                Secondary = Sec.Ward, SecAnim = "staff_shield", Eitr = 12f, Cooldown = 10f, Tint = new Color(0.65f, 1f, 0.6f) },
            new Def { Staff = "Bloodroot", PrimaryAnim = "staff_lightningshot", Drain = true, Element = "holy",
                Secondary = Sec.Mist, SecAnim = "staff_summon", TwoStep = true, Radius = 4f, Ttl = 5f, Interval = 1f, Value = 13f, Eitr = 14f, Cooldown = 8f, Tint = new Color(0.75f, 0.08f, 0.1f) },
            new Def { Staff = "Moonsilver", PrimaryAnim = "staff_thrust", Homing = 120f, Element = "holy",
                Secondary = Sec.MoonRing, SecAnim = "staff_summon", TwoStep = true, Radius = 4f, Ttl = 4f, Interval = 1f, Value = 18f, Eitr = 16f, Cooldown = 9f, Tint = new Color(0.85f, 0.9f, 1f) },
            new Def { Staff = "Barleycrown", PrimaryAnim = "staff_fireball", MendEvery3 = true, Element = "holy",
                Secondary = Sec.Harvest, SecAnim = "staff_summon", TwoStep = true, Radius = 6f, Ttl = 6f, Interval = 1f, Value = 24f, Eitr = 18f, Cooldown = 10f, Tint = new Color(1f, 0.95f, 0.75f) },
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
            BindHoming(cfg, sync);
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
                    case "fire": aoe.m_damage.m_fire = value; aoe.m_damagePerLevel.m_fire = value * AoeDamagePerLevelShare; break;
                    case "poison": aoe.m_damage.m_poison = value; aoe.m_damagePerLevel.m_poison = value * AoeDamagePerLevelShare; break;
                    case "lightning": aoe.m_damage.m_lightning = value; aoe.m_damagePerLevel.m_lightning = value * AoeDamagePerLevelShare; break;
                }
                if (d.Secondary == Sec.Flare && key.EndsWith("_sec")) aoe.m_attackForce = FlareAttackForce;
            }
            Tint(go, heal ? d.Tint : d.Tint);
            if (d.Secondary == Sec.Flare && key.EndsWith("_sec")) FlareBurst(go, radius);   // Untinted (real fire colours)
            Aoes[key] = go;
            if (go.GetComponent<ZNetView>() != null) Registered.Add(go);
        }

        // Vanilla StaffFireball's own explosion (its projectile's hit effects) is parented
        // to the Flare AoE prefab, scaled to its radius. The AoE is a networked object, so every peer that spawns it shows the burst; the
        // AoE lives 1.5 s (was 0.3) so the effect plays out, with a 5 s hit interval so it still hits only once.
        private static void FlareBurst(GameObject aoeGo, float radius)
        {
            int made = 0; string names = "";
            try
            {
                var zs = ZNetScene.instance;
                var proj = zs?.GetPrefab("StaffFireball")?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_attack?.m_attackProjectile?.GetComponent<Projectile>();
                var fx = proj != null && proj.m_hitEffects != null ? proj.m_hitEffects.m_effectPrefabs : null;
                if (fx != null)
                    foreach (var e in fx)
                    {
                        if (e == null || !e.m_enabled || e.m_prefab == null) continue;
                        var child = UnityEngine.Object.Instantiate(e.m_prefab, aoeGo.transform, false);
                        child.name = "FlareBurst_" + e.m_prefab.name;
                        child.transform.localPosition = Vector3.up * 0.3f;
                        child.transform.localScale = Vector3.one * Mathf.Max(1f, radius / FlareBurstBaseRadius);
                        foreach (var zs2 in child.GetComponentsInChildren<ZSyncTransform>(true)) UnityEngine.Object.DestroyImmediate(zs2);
                        foreach (var td in child.GetComponentsInChildren<TimedDestruction>(true)) UnityEngine.Object.DestroyImmediate(td);   // it dies with the AoE
                        foreach (var nv in child.GetComponentsInChildren<ZNetView>(true)) UnityEngine.Object.DestroyImmediate(nv);
                        foreach (var ps in child.GetComponentsInChildren<ParticleSystem>(true)) { var main = ps.main; main.scalingMode = ParticleSystemScalingMode.Hierarchy; }
                        foreach (var li in child.GetComponentsInChildren<Light>(true)) li.range *= Mathf.Max(1f, radius / FlareBurstBaseRadius);
                        made++; names += (names.Length > 0 ? ", " : "") + e.m_prefab.name;
                    }
            }
            catch (Exception ex) { Debug.LogWarning($"{MyMod.PluginName}: staff attack styles: Solar Flare burst failed: {ex.Message}"); }
            var aoe = aoeGo.GetComponent<Aoe>();
            if (made > 0 && aoe != null) { aoe.m_ttl = Mathf.Max(aoe.m_ttl, 1.5f); aoe.m_hitInterval = Mathf.Max(aoe.m_hitInterval, 5f); }
            Debug.Log($"{MyMod.PluginName}: staff attack styles: Solar Flare burst {(made > 0 ? $"= {names} x{Mathf.Max(1f, radius / FlareBurstBaseRadius):0.#} (radius {radius:0.#} m), AoE life {aoe?.m_ttl:0.#} s, one hit" : "NOT found (no StaffFireball hit effect) - the flare keeps its plain look")}");
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
        // The clone's OWN spawn-on-hit (the vanilla fireball's explosion etc.), kept so OFF restores the original exactly
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
            // A left click could start an attack and then do nothing (no
            // projectile, no damage, no exception). Fixes: (1) the primary only uses an animation the player's controller really has - one a
            // vanilla item uses (ObjectDB, collected once); 'staff_frostorb' (Bogfire, Sunfire) and 'staff_thrust' (Moonsilver) are not
            // vanilla, so the trigger frame that spawns the projectile never came - those fall back to StaffFireball's own animation;
            // (2) a projectile primary IS a Projectile attack: the gem prefab's own type was kept before, and only the projectile was copied.
            if (sh.m_attack != null)
            {
                string anim = d.PrimaryAnim;
                if (!KnownAttackAnims().Contains(anim))
                {
                    string fallback = FireballAnim() ?? "staff_fireball";
                    Debug.LogWarning($"{MyMod.PluginName}: staff attack styles: FiresStaff_{staffId}: primary animation '{anim}' is not one any vanilla item uses; using '{fallback}'");
                    anim = fallback;
                }
                sh.m_attack.m_attackAnimation = anim;
            }
            // the Mistile staffs fire no projectile: the trigger frame spawns the Mistile (Attack_OnAttackTrigger)
            // The Mistile
            // staffs (Bogfire, Sunfire) fire their normal styled bolt too; the Mistile is spawned where it hits (Projectile_OnHit)
            if (sh.m_attack != null) sh.m_attack.m_attackType = Attack.AttackType.Projectile;
            if (proj != null) proj.m_spawnOnHit = !string.IsNullOrEmpty(d.HitAoe) && Aoes.TryGetValue(d.Staff + "_hit", out var hitAoe) ? hitAoe : OrigSpawnOnHit[staffId];
            if (projectile != null && d.Homing > 0f)
            {
                if (homing == null) homing = projectile.AddComponent<FiresStaffHoming>();
                homing.TurnDegrees = d.Homing; homing.StaffName = d.Staff;
                homing.enabled = true;
            }
            sh.m_secondaryAttack = MakeSecondary(sh, d, projectile);
            Debug.Log($"{MyMod.PluginName}: staff attack styles: FiresStaff_{staffId}: primary '{d.PrimaryAnim}'{(d.Homing > 0f ? $", homing {d.Homing:0}/s" : "")}{(d.Chain ? ", chain" : "")}{(d.Drain ? ", drain 20%" : "")}{(d.MendEvery3 ? ", mend every 3rd" : "")}{(proj?.m_spawnOnHit != null ? ", on hit " + proj.m_spawnOnHit.name : "")}; "
                + $"secondary {d.Secondary} '{d.SecAnim}'{(d.TwoStep ? " (two-step ring)" : d.SelfCentred ? " (at your feet)" : "")}, {d.Eitr:0} eitr, {d.Cooldown:0} s cooldown");
        }

        // Every attack animation a vanilla item's primary or secondary uses (ObjectDB, once, after it is populated)
        private static HashSet<string> s_knownAnims;
        private static HashSet<string> KnownAttackAnims()
        {
            if (s_knownAnims != null && s_knownAnims.Count > 0) return s_knownAnims;
            var set = new HashSet<string>(StringComparer.Ordinal);
            var db = ObjectDB.instance;
            if (db != null && db.m_items != null)
                foreach (var go in db.m_items)
                {
                    if (go == null || go.name.StartsWith("FiresStaff_", StringComparison.Ordinal)) continue;
                    var s = go.GetComponent<ItemDrop>()?.m_itemData?.m_shared;
                    if (s == null) continue;
                    if (!string.IsNullOrEmpty(s.m_attack?.m_attackAnimation)) set.Add(s.m_attack.m_attackAnimation);
                    if (!string.IsNullOrEmpty(s.m_secondaryAttack?.m_attackAnimation)) set.Add(s.m_secondaryAttack.m_attackAnimation);
                }
            if (set.Count > 0)
            {
                s_knownAnims = set;
                Debug.Log($"{MyMod.PluginName}: staff attack styles: {set.Count} vanilla attack animation(s) known; staff ones: {string.Join(", ", set.Where(n => n.StartsWith("staff", StringComparison.Ordinal)).OrderBy(n => n).ToArray())}");
            }
            return set;
        }
        private static string FireballAnim() => ObjectDB.instance?.GetItemPrefab("StaffFireball")?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_attack?.m_attackAnimation;

        // The point-of-use line for a gem staff's PRIMARY when it starts
        [HarmonyPatch(typeof(Attack), nameof(Attack.Start))]
        private static class Attack_Start_PrimaryLog
        {
            private static void Postfix(Attack __instance, bool __result)
            {
                var who = Traverse.Create(__instance).Field("m_character").GetValue<Humanoid>();
                if (who == null || who != Player.m_localPlayer) return;
                var w = Traverse.Create(__instance).Field("m_weapon").GetValue<ItemDrop.ItemData>();
                if (w?.m_dropPrefab == null || !w.m_dropPrefab.name.StartsWith("FiresStaff_", StringComparison.Ordinal) || (w.m_shared.m_secondaryAttack != null && __instance.m_attackAnimation == w.m_shared.m_secondaryAttack.m_attackAnimation && __instance.m_attackAnimation != w.m_shared.m_attack?.m_attackAnimation)) return;
                Debug.Log($"[EarlyMagic] {w.m_dropPrefab.name.Substring("FiresStaff_".Length)} primary: Start {(__result ? "ran" : "REFUSED")} - animation '{__instance.m_attackAnimation}', type {__instance.m_attackType}, projectile '{(__instance.m_attackProjectile != null ? __instance.m_attackProjectile.name : "NONE")}'");
            }
        }

        private static Attack MakeSecondary(ItemDrop.ItemData.SharedData sh, Def d, GameObject projectile)
        {
            var a = (Attack)AccessTools.Method(typeof(object), "MemberwiseClone").Invoke(sh.m_attack, null);
            a.m_attackAnimation = d.SecAnim;
            a.m_attackEitr = 0f;   // The secondary's eitr (d.Eitr) is checked + spent by StartAttack below, never by vanilla (no double charge)
            a.m_attackStamina = 4f;
            a.m_spawnOnTrigger = null;
            switch (d.Secondary)
            {
                case Sec.Fan:
                    a.m_attackType = Attack.AttackType.Projectile;
                    a.m_projectiles = 3; a.m_projectileAccuracy = 14f; a.m_projectileAccuracyMin = 14f;
                    a.m_damageMultiplier = d.Value;
                    // Vanilla's attack
                    // origin joint = the left hand bone, spawn right at it (no range / height / side offset); the aim is still the look direction
                    a.m_attackOriginJoint = "LeftHand";
                    a.m_attackRange = 0.15f; a.m_attackHeight = 0f; a.m_attackOffset = 0f;
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
                // While a
                // two-step ring is being aimed, the attack click (primary) places it too - players click to place, not press MMB again
                // Without this it sometimes cast with no selection, or the shown placement could not be
                // clicked to cast: vanilla calls StartAttack EVERY frame while the button is HELD, so one MMB press entered the aim and, a frame
                // later, cast it (no selection seen); and right after a cast the still-held button re-entered the aim. A two-step staff now
                // needs the button RELEASED between steps: the press that opens the aim (or casts) must be let go before the next one counts.
                _prefixFrame = Time.frameCount;
                if (d.TwoStep && _needRelease)
                {
                    if (_aimingStaff == staff || secondaryAttack) { LogAim(staff, $"StartAttack({(secondaryAttack ? "secondary" : "primary")}) ignored: the press that {_releaseFor} is still held"); __result = false; return false; }
                }
                // Vanilla re-sends its own queued attack from the held press (StartAttack(secondary) again the frame after the
                // release), which used to cast at once: while aiming, only a
                // NEW press (a button DOWN seen after the release) casts; anything else vanilla sends is ignored
                // While aiming,
                // the middle button never casts (the ring already follows your aim); only a fresh LEFT click does
                if (d.TwoStep && _aimingStaff == staff && secondaryAttack)
                {
                    LogAim(staff, "StartAttack(secondary) while aiming: MMB only aims - left click casts at the ring");
                    __result = false; return false;
                }
                if (d.TwoStep && _aimingStaff == staff && !_freshPress)
                {
                    LogAim(staff, $"StartAttack({(secondaryAttack ? "secondary" : "primary")}) ignored while aiming: no new press since the aim opened (vanilla's queued / repeated attack)");
                    __result = false; return false;
                }
                if (!secondaryAttack && d.TwoStep && _aimingStaff == staff) { LogAim(staff, $"the attack click (LMB) places the aimed {d.Secondary}"); secondaryAttack = true; }
                if (!secondaryAttack) { _lastWasSecondary = false; _castPoint = null; return true; }
                if (d.TwoStep) LogAim(staff, $"StartAttack(secondary) - state {(_aimingStaff == staff ? "AIMING" : "idle")}, eitr {(__instance as Player)?.GetEitr() ?? 0f:0.#}/{d.Eitr:0}, cooldown {(ReadyAt.TryGetValue(staff, out float rdy) ? Mathf.Max(0f, rdy - Time.time) : 0f):0.0} s, aim valid {_aimValid}");
                if (ReadyAt.TryGetValue(staff, out float at) && Time.time < at)
                {
                    __instance.Message(MessageHud.MessageType.TopLeft, $"{d.Secondary} ready in {at - Time.time:0.0} s");
                    __result = false; return false;
                }
                // The secondary is cast through
                // this mod's own clip, which skipped vanilla's eitr check (Flare cast twice at 0.5 max eitr). Now: too little eitr = nothing
                // happens + vanilla's centre "$hud_eitrrequired" message and eitr-bar flash, exactly like the primary; the cost is spent in Postfix.
                // MMB attacks that place an AoE need one click to show the placement ring and a second click
                // to attack: entering the aim never checks eitr (the ring shows so the player sees the target); the placing click does,
                // and a refused placement keeps the ring up
                bool entering = d.TwoStep && _aimingStaff != staff;
                if (!entering && __instance is Player pl && pl.GetEitr() < d.Eitr)
                {
                    pl.Message(MessageHud.MessageType.Center, "$hud_eitrrequired");
                    try { Hud.instance?.EitrBarEmptyFlash(); } catch { }
                    Debug.Log($"[EarlyMagic] {staff} secondary: {d.Secondary} refused - eitr {pl.GetEitr():0.#}/{pl.GetMaxEitr():0.#}, needs {d.Eitr:0}");
                    __result = false; return false;
                }
                if (d.TwoStep)
                {
                    if (_aimingStaff != staff)
                    {
                        _aimingStaff = staff;
                        _needRelease = true; _releaseFor = "opened the aim"; _freshPress = false;
                        _aimHitLogged = null;
                        AimClip.Play(__instance, "fires.staff.cast.hold");   // The aiming pose while the ghost ring is up (no attack runs now)
                        Debug.Log($"[EarlyMagic] {staff} secondary: {d.Secondary} aiming - the ring follows your aim; LEFT click casts at it; right click / block / Escape cancels");
                        __instance.Message(MessageHud.MessageType.TopLeft, $"{d.Secondary}: aim the ring, LEFT click to cast (right click cancels)");
                        __result = false; return false;
                    }
                    // The ring always stands on a reachable ground spot (the aim ray's hit, else the ground at range), so a click there casts;
                    // "Can't place it there" refused clicks while the ring was plainly drawn
                    _castPoint = _aimPoint;
                    StopAiming(false);
                    _needRelease = true; _releaseFor = "cast"; _freshPress = false;
                    LogAim(staff, $"CAST at {_castPoint.Value:F1}");
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
                // While the Fan cast plays
                // the body keeps the facing it had (Player.FaceLookDirection held), and the yaw change over the cast is logged
                if (d.Secondary == Sec.Fan) { _fanHoldUntil = Time.time + FanHoldSeconds; _fanYaw0 = __instance.transform.eulerAngles.y; _fanYawLogged = false; }
                // The secondary's eitr is spent here, once, when the attack really started (its Attack carries 0 for vanilla)
                if (__instance is Player sp && d.Eitr > 0f) sp.UseEitr(d.Eitr);
                Debug.Log($"[EarlyMagic] {staff} secondary: {d.Secondary} cast ('{d.SecAnim}', {d.Eitr:0} eitr spent, {(__instance as Player)?.GetEitr() ?? 0f:0.#} left){(_castPoint.HasValue ? $" at {_castPoint.Value:F1}" : "")}; next in {d.Cooldown:0} s");
                SecClip.Begin(__instance, staff, d);   // The matching UAL clip (key off = nothing)
            }
        }

        // The EmberWand Fan's no-turn hold (see StartAttack's postfix)
        private static float _fanHoldUntil = -1f, _fanYaw0;
        private static bool _fanYawLogged = true;

        [HarmonyPatch]
        private static class Player_FaceLookDirection_FanHold
        {
            private static MethodBase s_target;
            private static bool Prepare()
            {
                s_target = AccessTools.Method(typeof(Player), "FaceLookDirection");
                if (s_target == null) Debug.LogWarning($"{MyMod.PluginName}: staff attack styles: Player.FaceLookDirection not found; the EmberWand Fan cannot hold the facing");
                return s_target != null;
            }
            private static MethodBase TargetMethod() => s_target;
            private static bool Prefix(Player __instance) => !(__instance == Player.m_localPlayer && Time.time < _fanHoldUntil);
        }

        [HarmonyPatch(typeof(Player), "Update")]
        private static class Player_Update_FanYawLog
        {
            private static void Postfix(Player __instance)
            {
                if (_fanYawLogged || __instance != Player.m_localPlayer || Time.time < _fanHoldUntil) return;
                _fanYawLogged = true;
                Debug.Log($"[EarlyMagic] EmberWand secondary: Fan cast - the body turned {Mathf.Abs(Mathf.DeltaAngle(_fanYaw0, __instance.transform.eulerAngles.y)):0.#} deg over the cast (facing held, fired from the left hand)");
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
                if (__instance != Player.m_localPlayer || !On) return;
                string staff = StaffOf(__instance);
                var sd = DefOf(staff);
                if (sd != null && sd.TwoStep) InputLog(__instance, staff);
                if (_aimingStaff == null) return;
                string why = StaffOf(__instance) != _aimingStaff ? "staff changed" : __instance.IsBlocking() ? "block" : Input.GetKeyDown(KeyCode.Escape) ? "Escape"
                    : Input.GetMouseButtonDown(1) ? "right click" : __instance.IsDead() ? "dead" : null;
                if (why != null) { LogAim(_aimingStaff, "cancel: " + why); StopAiming(true); return; }
                var d = DefOf(_aimingStaff);
                _aimValid = AimPoint(__instance, AimRange, out _aimPoint);
                ShowGhost(_aimPoint, d.Radius, _aimValid, __instance);
            }
        }

        // One line per mouse press / release while a
        // two-step staff is in hand - the state, whether the release gate is up, and whether vanilla's StartAttack saw the press (2 frames)
        private static bool _needRelease, _freshPress; private static string _releaseFor = "";
        private static int _prefixFrame = -10, _pendingPressFrame = -1; private static string _pendingPress;
        private static void InputLog(Player p, string staff)
        {
            if (_needRelease && !Input.GetMouseButton(0) && !Input.GetMouseButton(2) && !ZInput.GetButton("Attack") && !ZInput.GetButton("SecondaryAttack")) { _needRelease = false; LogAim(staff, $"buttons released after the press that {_releaseFor}: the next press counts"); }
            foreach (var (btn, name) in new[] { (2, "MMB"), (0, "LMB") })
            {
                // The game's own "Attack" button (a rebound key, a gamepad) must count too, not
                // only the raw left mouse button: the cast press is LMB OR vanilla's "Attack" button going down
                if (btn == 0 && (Input.GetMouseButtonDown(btn) || ZInput.GetButtonDown("Attack") || ZInput.GetButtonDown("JoyAttack")) && _aimingStaff != null && !_needRelease && !_freshPress) { _freshPress = true; LogAim(staff, $"input {name}: a NEW press while aiming - the next StartAttack casts"); }   // LMB only casts
                if (Input.GetMouseButtonDown(btn)) { LogAim(staff, $"input {name} DOWN - state {(_aimingStaff != null ? "AIMING" : "idle")}, release gate {(_needRelease ? "UP (" + _releaseFor + ")" : "down")}, in attack {p.InAttack()}, blocking {p.IsBlocking()}, UI {(InventoryGui.IsVisible() || Menu.IsVisible() || TextInput.IsVisible() || Minimap.IsOpen())}"); _pendingPress = name; _pendingPressFrame = Time.frameCount; }
                if (Input.GetMouseButtonUp(btn)) LogAim(staff, $"input {name} up - state {(_aimingStaff != null ? "AIMING" : "idle")}");
            }
            if (_pendingPress != null && Time.frameCount >= _pendingPressFrame + 2)
            {
                if (_prefixFrame < _pendingPressFrame) LogAim(staff, $"input {_pendingPress}: vanilla never called StartAttack for this press (in attack {p.InAttack()}, blocking {p.IsBlocking()}) - the press was eaten before this mod");
                _pendingPress = null;
            }
        }
        private static void LogAim(string staff, string what) => Debug.Log($"[EarlyMagic] {staff} aim: {what} (frame {Time.frameCount})");

        private static bool AimPoint(Player p, float range, out Vector3 point)
        {
            point = p.transform.position + p.transform.forward * AimFallbackDistance;
            var cam = GameCamera.instance;
            if (cam == null) return false;
            // The aim ray hits the GROUND and
            // build pieces (floors) only - rocks, trees, creatures and carts ("Default", "static_solid", "vehicle", characters) are seen through
            int mask = LayerMask.GetMask("terrain", "piece");
            bool hitOk = Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, range + 10f, mask) && Vector3.Distance(hit.point, p.transform.position) <= range;
            if (hitOk) point = hit.point;
            else
            {
                Vector3 fwd = cam.transform.forward; fwd.y = 0f;
                point = p.transform.position + (fwd.sqrMagnitude > 0.01f ? fwd.normalized : p.transform.forward) * range;
                if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(point, out float ground)) point.y = ground;
            }
            string src = hitOk ? $"ray hit '{hit.collider.name}' layer {LayerMask.LayerToName(hit.collider.gameObject.layer)}" : $"no ground/floor hit within {range:0} m: ground at range";
            if (src != _aimHitLogged) { _aimHitLogged = src; Debug.Log($"[EarlyMagic] {_aimingStaff} aim point: {src} at {point:F1} (mask terrain+piece = {mask}); the ring's ground under it = Core GroundHeightSampler (layers terrain, static_solid, Default, Default_small, piece)"); }
            return true;   // the ring always marks a reachable ground spot, so a click there casts
        }
        private static string _aimHitLogged;

        // Core's ground ring (the class skills' GroundRingManager), soft-linked.
        private static MethodInfo _show, _release;
        private static Type _kindType;
        private static bool _ringLooked, _nullSaid;

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
            if (_show == null) return;
            // the ring was released and re-shown EVERY frame (Core logged the first one, then each new ring was gone before it drew). Now it is
            // shown ONCE per aim (and again only when valid/invalid flips or the radius changes) and then MOVED to the aim point each frame.
            string wantKind = valid ? "Damage" : "Invalid";
            if (_ghost != null && _ghostKind == wantKind && Mathf.Approximately(_ghostRadius, radius) && MoveGhost(_ghost, at)) return;
            ReleaseGhost();
            try
            {
                // A missing "Damage" kind no longer throws - the first defined kind stands in
                string want = wantKind;
                if (!Enum.IsDefined(_kindType, want)) want = Enum.IsDefined(_kindType, "Damage") ? "Damage" : Enum.GetNames(_kindType).FirstOrDefault();
                object kind = Enum.Parse(_kindType, want);
                _ghost = _show.Invoke(null, new object[] { kind, at, radius, AimRingLifetime, p, true });   // Lives until released (StopAiming / cast)
                _ghostKind = wantKind; _ghostRadius = radius; _ghostAt = at;
                // Remember which life of the pooled ring is ours, and say what was drawn on EVERY aim start / re-show
                _ringStart ??= _ghost != null ? AccessTools.Property(_ghost.GetType(), "StartTime") : null;
                _ghostStartTime = _ghost != null && _ringStart != null ? (float)_ringStart.GetValue(_ghost, null) : -1f;
                var rgo = (_ghost as Component)?.gameObject;
                int rn = rgo != null ? rgo.GetComponentsInChildren<Renderer>(true).Length : 0, rv = rgo != null ? rgo.GetComponentsInChildren<Renderer>(false).Count(r => r.enabled) : 0;
                Debug.Log($"[EarlyMagic] {_aimingStaff} aim ring: {(_ghost != null ? $"shown ({want} disc, radius {radius:0.#} m) at {at:F1}, active {rgo != null && rgo.activeInHierarchy}, renderers {rv}/{rn} enabled" : "NOT shown (Core returned no ring)")}");
                if (_ghost != null) GroundOnlyRing(_ghost, at);
                if (_ghost != null && !_ghostSaid)
                {
                    _ghostSaid = true;
                    Debug.Log($"[EarlyMagic] ghost ring shown once per aim and moved each frame: Core returned {_ghost.GetType().Name}, movable {GhostMovable(_ghost)}");
                }
                // Core's Show returns null WITHOUT a throw when its ring can't draw (GroundRingSegmentSource not available, or the
                // visibility rule says no) - said once per session, with the segment source's state
                if (_ghost == null && !_nullSaid)
                {
                    _nullSaid = true;
                    object avail = null;
                    try { avail = AccessTools.TypeByName("FiresCore.Npc.Archetypes.Effects.GroundRingSegmentSource")?.GetProperty("Available", BindingFlags.Public | BindingFlags.Static)?.GetValue(null); } catch { }
                    Debug.LogWarning($"{MyMod.PluginName}: staff attack styles: Core GroundRingManager.Show returned NO ring for the aim (kind {want}, radius {radius:0.#}; GroundRingSegmentSource.Available = {avail ?? "?"})");
                }
            }
            catch (Exception e)
            {
                // This used to switch the ring off for the session SILENTLY; now it says why, once
                var x = e.InnerException ?? e;
                Debug.LogWarning($"{MyMod.PluginName}: staff attack styles: ghost ring OFF - Core GroundRingManager.Show threw {x.GetType().Name}: {x.Message}");
                _show = null;
            }
        }

        private static void ReleaseGhost()
        {
            if (_ghost != null && _release != null) { try { _release.Invoke(null, new[] { _ghost }); } catch { } }
            _ghost = null; _ghostKind = null;
        }

        // Core's GroundRing builds its mesh in WORLD space around its private _center (transform stays at zero), and every Show /
        // Configure restarts its fade-in from 0 - so a ring re-shown each frame never became visible. The live ring is moved by setting
        // _center and re-sampling the ground under it (SampleAllHeights); its own Update rebuilds the mesh and keeps the fade.
        private static string _ghostKind; private static float _ghostRadius; private static bool _ghostSaid;
        private static FieldInfo _ringCenter; private static MethodInfo _ringResample; private static bool _ringMoveLooked;
        private static Vector3 _ghostAt;
        private static PropertyInfo _ringStart; private static float _ghostStartTime = -1f;
        private static bool GhostMovable(object g)
        {
            if (!_ringMoveLooked && g != null)
            {
                _ringMoveLooked = true;
                _ringCenter = AccessTools.Field(g.GetType(), "_center");
                _ringResample = AccessTools.Method(g.GetType(), "SampleAllHeights");
                if (_ringCenter == null || _ringCenter.FieldType != typeof(Vector3))
                    Debug.LogWarning($"{MyMod.PluginName}: staff attack styles: Core's ring has no '_center' (Vector3) to move; the ring stays where the aim began until it is re-aimed");
            }
            return _ringCenter != null && _ringCenter.FieldType == typeof(Vector3);
        }
        // GroundRing.GroundOnly (the disc samples terrain + floors only, so it does not wrap itself up
        // over other objects) and GroundRing.SetCenter(Vector3) (moves a live ring without restarting its fade). Used when present; an older
        // Core keeps the older path (private _center + SampleAllHeights) and its draping disc.
        private static MethodInfo _ringSetCenter; private static PropertyInfo _ringGroundOnly; private static bool _ringApiLooked;
        private static void RingApi(object g)
        {
            if (_ringApiLooked || g == null) return;
            _ringApiLooked = true;
            _ringSetCenter = AccessTools.Method(g.GetType(), "SetCenter", new[] { typeof(Vector3) });
            _ringGroundOnly = AccessTools.Property(g.GetType(), "GroundOnly");
            Debug.Log($"[EarlyMagic] ghost ring API: SetCenter {(_ringSetCenter != null ? "yes" : "no (Core < 0.2.722: private centre)")}, GroundOnly {(_ringGroundOnly != null ? "yes - the disc samples terrain + floors only" : "no (Core < 0.2.722: the disc drapes over rocks/trees)")}");
        }
        private static void GroundOnlyRing(object g, Vector3 at)
        {
            RingApi(g);
            if (_ringGroundOnly == null || _ringSetCenter == null) return;
            try { _ringGroundOnly.SetValue(g, true, null); _ringSetCenter.Invoke(g, new object[] { at }); }   // GroundOnly set after Show, so SetCenter re-samples
            catch (Exception e) { Debug.LogWarning($"{MyMod.PluginName}: staff attack styles: GroundOnly ring failed: {(e.InnerException ?? e).Message}"); _ringGroundOnly = null; }
        }
        private static bool MoveGhost(object g, Vector3 at)
        {
            if (g is Component c && c == null) return false;   // released / destroyed by Core
            // Core's GroundRingManager caps live rings and releases the OLDEST
            // when a new one is shown - the aiming ring is always the oldest, so a companion's skill ring took it; the released ring goes back
            // to Core's pool (maybe reused for someone else's effect). Gone inactive or restarted (StartTime changed) = it isn't ours any more:
            // drop it WITHOUT releasing and show a fresh one.
            if (g is Component rc && (!rc.gameObject.activeInHierarchy || (_ringStart != null && !Mathf.Approximately((float)_ringStart.GetValue(g, null), _ghostStartTime))))
            {
                Debug.Log($"[EarlyMagic] {_aimingStaff} aim ring: Core took it back (ring cap / pool: {(rc.gameObject.activeInHierarchy ? "restarted for another effect" : "released")}); showing a fresh one");
                _ghost = null; _ghostKind = null;
                return false;
            }
            RingApi(g);
            if ((at - _ghostAt).sqrMagnitude < 0.0025f) return true;
            if (_ringSetCenter != null)
            {
                _ghostAt = at;
                try { _ringSetCenter.Invoke(g, new object[] { at }); return true; }
                catch (Exception e) { Debug.LogWarning($"{MyMod.PluginName}: staff attack styles: ring SetCenter failed: {(e.InnerException ?? e).Message}"); _ringSetCenter = null; }
            }
            if (!GhostMovable(g)) return true;                 // can't move it: keep the one ring (never re-show per frame)
            _ghostAt = at;
            try { _ringCenter.SetValue(g, at); _ringResample?.Invoke(g, null); }
            catch (Exception e) { Debug.LogWarning($"{MyMod.PluginName}: staff attack styles: ring move failed: {(e.InnerException ?? e).Message}"); _ringCenter = null; }
            return true;
        }

        // ── the trigger frame: spawn the secondary's AoE, count primaries for Barley-crown ────────────────────────────────
        [HarmonyPatch(typeof(Attack), nameof(Attack.OnAttackTrigger))]
        private static class Attack_OnAttackTrigger
        {
            // While a secondary clip owns the cast, only the clip's own event (SecClip) lets the trigger run, never twice
            private static bool Prefix(Attack __instance) => !SecClip.Blocks(__instance);

            private static void Postfix(Attack __instance)
            {
                if (!On) return;
                var who = Traverse.Create(__instance).Field("m_character").GetValue<Humanoid>();
                if (who == null || who != Player.m_localPlayer) return;
                string staff = StaffOf(who);
                var d = DefOf(staff);
                if (d == null) return;
                if (!_lastWasSecondary)   // The primary's trigger frame (the projectile spawns here)
                    Debug.Log($"[EarlyMagic] {staff} primary: trigger frame - animation '{__instance.m_attackAnimation}', type {__instance.m_attackType}, projectile '{(__instance.m_attackProjectile != null ? __instance.m_attackProjectile.name : "NONE")}'");
                var item = who.GetCurrentWeapon();
                bool secondary = item != null && __instance.m_attackAnimation == d.SecAnim && _lastWasSecondary;
                if (!secondary)
                {
                    // No Mistile at the trigger frame any more - the bolt flies, and its impact spawns the Mistile
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
            public Character Locked;        // The one target it flies at (never switched)
            public bool ShieldLogged;
            public Vector3 AimFrom, AimDir; // The crosshair ray when the bolt was fired
            public float NextLook;
        }

        // ── Homing locks onto the enemy closest to the crosshair, not the closest enemy overall,
        // and only after travelling a few metres: one target pick for every homing projectile ──────────────────
        internal static ConfigEntry<float> HomingCone, HomingArm;
        internal static void BindHoming(ConfigFile cfg, ConfigSync sync)
        {
            HomingCone = cfg.Bind("Early Magic", "Homing cone", DefaultHomingConeDegrees, new ConfigDescription("Homing staff projectiles lock onto the enemy closest to the crosshair within this many degrees of the aim (line of sight, never a party member); none in the cone = they fly straight. Server-synced.", new AcceptableValueRange<float>(2f, 45f)));
            HomingArm = cfg.Bind("Early Magic", "Homing arm distance", 3.5f, new ConfigDescription("Metres a homing projectile flies straight before it starts turning toward its locked target. Server-synced.", new AcceptableValueRange<float>(0f, 15f)));
            sync?.AddConfigEntry(HomingCone); sync?.AddConfigEntry(HomingArm);
        }
        internal static Vector3 CrosshairFrom(Character caster, out Vector3 dir)
        {
            var cam = GameCamera.instance;
            if (cam != null && caster == Player.m_localPlayer) { dir = cam.transform.forward; return cam.transform.position; }
            dir = caster.GetLookDir(); return caster.GetEyePoint();
        }
        internal static Character ConeTarget(Character caster, Vector3 from, Vector3 dir, float range, out float angle)
        {
            angle = 0f;
            float cone = HomingCone?.Value ?? DefaultHomingConeDegrees;
            int block = LayerMask.GetMask("terrain", "static_solid", "Default", "piece");
            Character best = null; float bestAngle = float.MaxValue;
            foreach (var c in Character.GetAllCharacters())
            {
                if (c == null || c == caster || !ValidPrey(caster, c) || InParty(caster, c)) continue;
                Vector3 to = c.GetCenterPoint() - from;
                if (to.magnitude > range) continue;
                float a = Vector3.Angle(dir, to);
                if (a > cone || a >= bestAngle) continue;
                if (Physics.Linecast(from, c.GetCenterPoint(), out RaycastHit hit, block) && hit.collider.GetComponentInParent<Character>() != c) continue;   // no line of sight
                best = c; bestAngle = a;
            }
            angle = best != null ? bestAngle : 0f;
            return best;
        }

        private const int MistilesPerCaster = 2;
        private static readonly List<MistileRun> Mistiles = new List<MistileRun>();
        private static readonly FieldInfo TargetField = AccessTools.Field(typeof(MonsterAI), "m_targetCreature");

        private static void SpawnMistile(Humanoid caster, Def d, ItemDrop.ItemData item) => SpawnMistile(caster, d, item, null, Vector3.zero);

        // Spawned at `at` (the bolt's impact point), facing `dir` (its flight); null = in front of the caster as before
        private static void SpawnMistile(Humanoid caster, Def d, ItemDrop.ItemData item, Vector3? impact, Vector3 dir)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Mistile") : null;
            if (prefab == null) { Debug.LogWarning($"[EarlyMagic] {d.Staff} primary: no 'Mistile' prefab in ZNetScene; nothing spawned"); return; }
            // the per-caster cap: the oldest goes first
            var mine = Mistiles.Where(m => m.Caster == caster && m.Body != null).ToList();
            while (mine.Count >= MistilesPerCaster) { Despawn(mine[0], "cap (" + MistilesPerCaster + " per caster)"); mine.RemoveAt(0); }

            Vector3 face = impact.HasValue && dir.sqrMagnitude > 0.01f ? dir.normalized : caster.GetLookDir();
            Vector3 at = impact.HasValue ? impact.Value + Vector3.up * 0.5f - face * 0.4f : caster.GetEyePoint() + caster.GetLookDir() * 1.5f;   // a hair back from the hit, off the ground
            var go = UnityEngine.Object.Instantiate(prefab, at, Quaternion.LookRotation(face));   // ours: this client owns it
            var body = go.GetComponent<Character>();
            var ai = go.GetComponent<MonsterAI>();
            if (body == null || ai == null) { Debug.LogWarning($"[EarlyMagic] {d.Staff} primary: the Mistile has no Character/MonsterAI; removed"); ZNetScene.instance.Destroy(go); return; }
            body.m_faction = Character.Faction.Players;   // the player side: it never picks the caster, allies or other players (PvP off)
            body.m_flyFastSpeed = d.MistileSpeed; body.m_flySlowSpeed = d.MistileSpeed; body.m_flyTurnSpeed = d.MistileTurn;
            ai.m_enableHuntPlayer = false;
            Tint(go, d.Tint);
            // The enemy closest to the crosshair ray the bolt was fired along (from the camera), else none yet - it keeps looking
            // along that same ray, locks the first one it finds and never switches
            Vector3 aimFrom = CrosshairFrom(caster, out Vector3 aimDir);
            Character target = ConeTarget(caster, aimFrom, aimDir, HomingTargetRange, out float lockAngle);
            if (target != null) TargetField?.SetValue(ai, target);
            Debug.Log($"[EarlyMagic] {d.Staff} homing: Mistile {(target != null ? $"locked {target.m_name} (angle {lockAngle:0.#} deg, dist {Vector3.Distance(at, target.transform.position):0.#} m)" : $"no enemy within {HomingCone?.Value ?? 12f:0} deg of the aim yet (keeps looking along it)")}, arms at 0 m (it spawns at the impact)");
            var dmg = item != null ? item.GetDamage() : new HitData.DamageTypes();
            dmg.Modify(1f + caster.GetSkillLevel(Skills.SkillType.ElementalMagic) / 100f);   // tier + quality (item) and skill: no cap
            dmg.Modify(EarlyMagic.MistileShare?.Value ?? 0.5f);   // The Mistile rides on the bolt, so it hits for a share of it
            var run = new MistileRun { Caster = caster, Body = body, Def = d, Damage = dmg, DieAt = Time.time + d.MistileLife, Last = at, Locked = target, AimFrom = aimFrom, AimDir = aimDir };
            Mistiles.Add(run);
            Debug.Log($"[EarlyMagic] {d.Staff} primary: Mistile spawned at {at:F1} (faction Players, speed {d.MistileSpeed:0.#}, turn {d.MistileTurn:0}, life {d.MistileLife:0} s, cap {MistilesPerCaster}/caster, burst {dmg.GetTotalDamage():0.#}: fire {dmg.m_fire:0.#} poison {dmg.m_poison:0.#}); target {(target != null ? target.m_name : "its own pick among your enemies")}");
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
                    if (ai == null) continue;
                    // The lock is held every frame (MonsterAI would re-pick the nearest foe itself); no lock yet = look again along the
                    // ORIGINAL aim ray every 0.25 s and lock the first enemy in the cone; a dead lock is not replaced
                    if (r.Locked == null && Time.time >= r.NextLook)
                    {
                        r.NextLook = Time.time + HomingRetargetInterval;
                        r.Locked = ConeTarget(r.Caster, r.AimFrom, r.AimDir, HomingTargetRange, out float a2);
                        if (r.Locked != null) Debug.Log($"[EarlyMagic] {r.Def.Staff} homing: Mistile locked {r.Locked.m_name} (angle {a2:0.#} deg, dist {Vector3.Distance(r.Body.transform.position, r.Locked.transform.position):0.#} m) along the original aim");
                    }
                    TargetField?.SetValue(ai, r.Locked != null && !r.Locked.IsDead() ? r.Locked : null);
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
                __instance.m_healthReturn = hitData.GetTotalDamage() * BloodrootLifestealShare;   // Bloodroot: 20% of the bolt's damage back to the caster
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
                // Vanilla OnHit returns early for the owner and non-enemies (IsValidTarget:
                // no damage, no spawn-on-hit, the bolt flies on), so those contacts are named as passes, and chain / pierce never start on them
                if (hit != null && (hit == owner || !BaseAI.IsEnemy(owner, hit)))
                {
                    Debug.Log($"[EarlyMagic] {staff} {(bolt.EndsWith("FrostLance") ? "secondary" : "primary")}: {bolt} passed through {(hit == owner ? "the caster" : "friendly " + hit.m_name)} (vanilla ignores it: no damage, the bolt flies on)");
                    return;
                }
                Debug.Log($"[EarlyMagic] {staff} {(bolt.EndsWith("FrostLance") ? "secondary" : "primary")}: {bolt} -> {(hit != null ? hit.m_name : "terrain")}{(__instance.m_spawnOnHit != null ? $" (+{__instance.m_spawnOnHit.name})" : "")}{(__instance.m_healthReturn > 0f ? $", drain {__instance.m_healthReturn:0.#} health to you" : "")}");
                // A Mistile staff's bolt spawns the homing Mistile where it hits (enemy or terrain) - an extra on top of the
                // bolt's own hit; the Mistile keeps the staff's settings (Players faction, the caster as attacker, the per-caster cap)
                var md = DefOf(staff);
                if (md != null && md.Mistile && !bolt.EndsWith("FrostLance"))
                {
                    Vector3 vel = Traverse.Create(__instance).Field("m_vel").GetValue<Vector3>();
                    Vector3 point = hit != null ? hit.GetCenterPoint() : __instance.transform.position;
                    Debug.Log($"[EarlyMagic] {staff} primary: {bolt} impact at {point:F1} ({(hit != null ? hit.m_name : "terrain")}) -> spawning the homing Mistile");
                    SpawnMistile(owner as Humanoid, md, (owner as Humanoid)?.GetCurrentWeapon(), point, vel);
                }
                if (hit == null) return;
                if (bolt.StartsWith("FiresBolt_Thunder")) ChainFrom(owner, hit, __instance);
                if (bolt.EndsWith("FrostLance")) Pierce(owner, hit, __instance);
            }
        }

        private static void ChainFrom(Character owner, Character first, Projectile p)
        {
            var next = Character.GetAllCharacters().Where(c => c != null && c != first && !c.IsDead() && BaseAI.IsEnemy(owner, c) && Vector3.Distance(c.transform.position, first.transform.position) <= ChainJumpRadius)
                .OrderBy(c => Vector3.Distance(c.transform.position, first.transform.position)).FirstOrDefault();
            if (next == null) return;
            var h = new HitData { m_point = next.GetCenterPoint(), m_dir = (next.transform.position - first.transform.position).normalized, m_skill = Skills.SkillType.ElementalMagic };
            h.m_damage.m_lightning = Traverse.Create(p).Field("m_damage").GetValue<HitData.DamageTypes>().m_lightning * ChainDamageShare;
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
                if (along <= 0f || along > PierceLength || (rel - dir * along).magnitude > PierceHalfWidth) continue;
                var h = new HitData { m_point = c.GetCenterPoint(), m_dir = dir, m_skill = Skills.SkillType.ElementalMagic };
                h.m_damage = dmg.Clone();
                h.SetAttacker(owner);
                c.Damage(h);
                n++;
            }
            Debug.Log($"[EarlyMagic] Frostglass secondary: Glacial Lance pierced {n} more foe(s) in 8 m");
        }

        // Log only: where a hit's damage actually lands on a live Mistile (Character.ApplyDamage), whatever path it came by,
        // so a Mistile that dies early can be traced to its cause
        [HarmonyPatch(typeof(Character), "ApplyDamage")]
        private static class Character_ApplyDamage_MistileTrace
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                if (__instance == null || hit == null || Mistiles.Count == 0) return;
                foreach (var m in Mistiles) if (m.Body == __instance) { var by = hit.GetAttacker(); Debug.Log($"[EarlyMagic] {m.Def.Staff} Mistile trace: damage LANDS on it - attacker {(by != null ? by.m_name : "none")}, {hit.GetTotalDamage():0.#}, hp {__instance.GetHealth():0.#}"); }
            }
        }

        // A Mistile spawned at a bolt's impact used to die at once: the Mistile
        // spawned at the bolt's impact, then "Sunfire damage applied: 20 to $enemy_mistile" - the bolt's own impact AoE, same point, same frame -
        // and counted as a kill. A live staff Mistile takes NO hit except its own (its 9999999 self-destruct on contact).
        // The whole Damage call is skipped, so no damage, no stagger, no LocationalDamage, no kill credit / drops / class XP.
        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class Character_Damage_MistileShield
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Character __instance, HitData hit)
            {
                if (__instance == null || hit == null || Mistiles.Count == 0) return true;
                // Every Damage call that touches a
                // live Mistile - on it, by it, or with no attacker within 4 m of it - is logged (who, whom, damage), so an early death shows its cause
                { var by = hit.GetAttacker(); foreach (var m in Mistiles) { if (m.Body == null) continue; bool on = m.Body == __instance, byIt = by == m.Body, near = by == null && Vector3.Distance(m.Body.transform.position, __instance.transform.position) < 4f; if (on || byIt || near) Debug.Log($"[EarlyMagic] {m.Def.Staff} Mistile trace: {(on ? "hit ON it" : byIt ? "hit BY it" : "no-attacker hit near it")} - attacker {(by != null ? by.m_name : "none")}, target {__instance.m_name}, damage {hit.GetTotalDamage():0.#} (fire {hit.m_damage.m_fire:0.#} poison {hit.m_damage.m_poison:0.#} blunt {hit.m_damage.m_blunt:0.#}), Mistile hp {m.Body.GetHealth():0.#}"); } }
                MistileRun run = null;
                foreach (var m in Mistiles) if (m.Body == __instance) { run = m; break; }
                if (run == null) return true;
                var from = hit.GetAttacker();
                if (from == __instance) return true;   // its own explosion / self-destruct still kills it
                if (!run.ShieldLogged) { run.ShieldLogged = true; Debug.Log($"[EarlyMagic] {run.Def.Staff} primary: a hit on the Mistile from {(from != null ? from.m_name : "an AoE / no attacker")} ({hit.GetTotalDamage():0.#}) ignored - it only dies by its own explosion"); }
                return false;
            }
        }

        // ── the damage / heal applied (point of use) ──────────────────────────────────────────────────────────────────────
        // Our attacks must not hurt our own group, companions included (a Hearthwood bolt used to hit
        // a companion): EVERY early-magic hit (bolt, AoE tick, Mistile, Thunderpine chain, Frostglass pierce) reaches a target
        // through Character.Damage with the caster as attacker, so this one guard covers them all: a target in the caster's party (Core's
        // ClassTargeting.IsPartyMember - the owner, allied owners, their companions) takes no damage, push or stagger; heal / buff effects
        // on the same hit still land. Runs after the Mistile prefix (which hands its hits to the caster).
        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class Character_Damage_PartyGuard
        {
            [HarmonyPriority(Priority.Low)]
            private static void Prefix(Character __instance, HitData hit)
            {
                if (!On || hit == null || __instance == null) return;
                var attacker = hit.GetAttacker();
                if (attacker == null || attacker == __instance || !(attacker is Humanoid h) || StaffOf(h) == null) return;
                if (hit.m_damage.GetTotalDamage() <= 0f || !InParty(attacker, __instance)) return;
                hit.m_damage = new HitData.DamageTypes();
                hit.m_pushForce = 0f; hit.m_staggerMultiplier = 0f;
                string key = attacker.GetZDOID() + ">" + __instance.GetZDOID();
                if (!PartyLogAt.TryGetValue(key, out float t) || Time.time >= t)
                {
                    PartyLogAt[key] = Time.time + PartyLogInterval;
                    Debug.Log($"[EarlyMagic] skipped party member {__instance.m_name} ({StaffOf(h)} hit from {attacker.m_name}: no damage, heal/buff kept)");
                }
            }
        }
        private static readonly Dictionary<string, float> PartyLogAt = new Dictionary<string, float>();
        private static MethodInfo _isParty; private static bool _isPartyLooked;
        private static bool InParty(Character caster, Character target)
        {
            if (!_isPartyLooked)
            {
                _isPartyLooked = true;
                _isParty = AccessTools.TypeByName("FiresCore.Npc.Archetypes.ClassTargeting")?.GetMethod("IsPartyMember", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Character), typeof(Character) }, null);
                Debug.Log($"[EarlyMagic] party guard: {(_isParty != null ? "Core ClassTargeting.IsPartyMember" : "Core missing: not-an-enemy = party")}");
            }
            if (_isParty != null) { try { return (bool)_isParty.Invoke(null, new object[] { caster, target }); } catch { } }
            return !BaseAI.IsEnemy(caster, target);
        }

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
    /// Core's ClipSlots (FiresCore.Animation, Core 0.2.493+), by an exact-name
    /// Type.GetType (no assembly scan; rule), for the moments where NO vanilla attack runs: the two-step aiming pose.
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
    /// Behind
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
            // A stagger / dodge / weapon swap / death ends the attack before its cast frame; never cast
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

    /// <summary>A staff bolt that turns toward the nearest foe ahead (owner-side; vanilla syncs the projectile's position).</summary>
    internal sealed class FiresStaffHoming : MonoBehaviour
    {
        public float TurnDegrees = 90f;
        private static readonly FieldInfo VelField = AccessTools.Field(typeof(Projectile), "m_vel");
        private static readonly FieldInfo OwnerField = AccessTools.Field(typeof(Projectile), "m_owner");
        private Projectile _p;
        private ZNetView _nview;
        private Character _target;
        private bool _picked;
        private Vector3 _start;
        public string StaffName = "staff";

        private void Awake() { _p = GetComponent<Projectile>(); _nview = GetComponent<ZNetView>(); }

        // The target is picked ONCE,
        // at the first owner-side frame, as the enemy closest to the crosshair ray (cone + line of sight, never a party member); none = fly
        // straight. No steering until the bolt has flown the arm distance; the lock is never switched (a dead lock = fly straight).
        private void FixedUpdate()
        {
            if (_p == null || !StaffAttackStyles.On || (_nview != null && _nview.IsValid() && !_nview.IsOwner())) return;
            var owner = OwnerField?.GetValue(_p) as Character;
            Vector3 vel = (Vector3)VelField.GetValue(_p);
            if (owner == null || vel.sqrMagnitude < 0.01f) return;
            float arm = StaffAttackStyles.HomingArm?.Value ?? 3.5f;
            if (!_picked)
            {
                _picked = true; _start = transform.position;
                Vector3 from = StaffAttackStyles.CrosshairFrom(owner, out Vector3 dir);
                _target = StaffAttackStyles.ConeTarget(owner, from, dir, 40f, out float ang);
                Debug.Log($"[EarlyMagic] {StaffName} homing: {(_target != null ? $"locked {_target.m_name} (angle {ang:0.#} deg, dist {Vector3.Distance(transform.position, _target.transform.position):0.#} m)" : $"no enemy within {StaffAttackStyles.HomingCone?.Value ?? 12f:0} deg of the crosshair - flies straight")}, arms at {arm:0.#} m");
            }
            if (_target == null || _target.IsDead()) return;
            if (Vector3.Distance(transform.position, _start) < arm) return;
            Vector3 want = (_target.GetCenterPoint() - transform.position).normalized * vel.magnitude;
            VelField.SetValue(_p, Vector3.RotateTowards(vel, want, TurnDegrees * Mathf.Deg2Rad * Time.fixedDeltaTime, 0f));
        }
    }

    /// <summary>The healer staffs' heal over time, scaled by the caster's item quality and Blood Magic skill (no flat cap).</summary>
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
