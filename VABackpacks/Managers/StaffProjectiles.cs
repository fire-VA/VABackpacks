using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // One projectile per staff, by element and style. Each is a runtime clone of a
    // vanilla projectile (and of its hit effects), retinted ONCE at load: no bundle, no per-frame code, damage untouched (it comes from the
    // staff's item data). [Early Magic] Staff Projectiles = Shared (today's fireball / ice shard, the default) | Styled. The clones are
    // always registered in ZNetScene, so every peer knows them whichever mode is on; the synced switch only picks what the staff fires.
    internal static class StaffProjectiles
    {
        internal static ConfigEntry<string> Mode;

        // staff id, clone name, source candidates (first found wins; the last is the fallback), tint, particle size scale
        private static readonly (string staff, string clone, string[] sources, Color tint, float size)[] Styles =
        {
            ("EmberWand",   "FiresBolt_Ember",   new[] { "staff_fireball_projectile" },                                               new Color(1.00f, 0.55f, 0.15f), 0.6f),
            ("Hearthwood",  "FiresBolt_Hearth",  new[] { "staff_fireball_projectile" },                                               new Color(1.00f, 0.85f, 0.45f), 0.8f),
            ("Thunderpine", "FiresBolt_Thunder", new[] { "staff_lightning_projectile", "staff_fireball_projectile" },                  new Color(0.55f, 0.75f, 1.00f), 0.8f),
            ("Mossbark",    "FiresBolt_Moss",    new[] { "staff_greenroots_projectile", "staff_fireball_projectile" },                 new Color(0.65f, 1.00f, 0.60f), 0.8f),
            ("Bogfire",     "FiresBolt_Bog",     new[] { "bonemass_throw_projectile", "blob_tar_projectile", "staff_fireball_projectile" }, new Color(0.35f, 0.85f, 0.20f), 1.0f),
            ("Bloodroot",   "FiresBolt_Blood",   new[] { "staff_fireball_projectile" },                                               new Color(0.75f, 0.08f, 0.10f), 0.9f),
            ("Frostglass",  "FiresBolt_Frost",   new[] { "staff_iceshard_projectile" },                                               new Color(0.70f, 0.95f, 1.00f), 1.0f),
            ("Moonsilver",  "FiresBolt_Moon",    new[] { "DvergerStaffIce_projectile", "dvergr_ice_projectile", "staff_fireball_projectile" }, new Color(0.85f, 0.90f, 1.00f), 0.9f),
            ("Sunfire",     "FiresBolt_Sun",     new[] { "staff_fireball_projectile" },                                               new Color(1.00f, 0.95f, 0.60f), 1.2f),
            ("Barleycrown", "FiresBolt_Barley",  new[] { "Eikthyr_antler_projectile", "eikthyr_projectile", "staff_fireball_projectile" }, new Color(1.00f, 0.95f, 0.75f), 1.0f),
        };

        private static readonly Dictionary<string, GameObject> Clones = new Dictionary<string, GameObject>();   // staff id -> projectile clone
        private static readonly List<GameObject> Registered = new List<GameObject>();                           // every clone with a ZNetView
        private static GameObject _holder;
        private static bool _listed;

        internal static void Bind(ConfigFile cfg, ConfigSync sync)
        {
            Mode = cfg.Bind("Early Magic", "Staff Projectiles", "Shared",
                new ConfigDescription("Shared = every staff fires the vanilla fireball (Frostglass the ice shard) as before 1.0.60. Styled = each staff fires its own retinted projectile and hit effect, plus an extra element particle system on the held staff (Tools\\STAFF_PROJECTILES_PLAN.md). Either way the held staff glows in its own colour (1.0.89). Visual only; damage is the same. Server-synced.",
                    new AcceptableValueList<string>("Shared", "Styled")));
            sync?.AddConfigEntry(Mode);
            Mode.SettingChanged += (_, __) => EarlyMagic.ReapplyStaffs();
        }

        internal static bool Styled => Mode != null && string.Equals(Mode.Value, "Styled", StringComparison.OrdinalIgnoreCase);

        // the staff's projectile in Styled mode, or null (keep the shared one)
        internal static GameObject For(string staffId) => Styled && Clones.TryGetValue(staffId, out var go) && go != null ? go : null;

        // The staff's own clone whatever the mode; Staff Attack Styles needs a per-staff projectile to carry its behaviour
        internal static GameObject Clone(string staffId) => Clones.TryGetValue(staffId, out var go) && go != null ? go : null;

        // ZNetScene.Awake: build the clones once (the sources are ZNetScene prefabs), then register them in every new ZNetScene
        internal static void Build(ZNetScene zs)
        {
            if (zs == null) return;
            if (_holder == null) { _holder = new GameObject("VABackpacks_StaffProjectiles"); _holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(_holder); }
            bool anyFallback = false;
            foreach (var st in Styles)
            {
                if (Clones.TryGetValue(st.staff, out var have) && have != null) continue;
                GameObject src = null; string used = null;
                foreach (var n in st.sources) { src = zs.GetPrefab(n); if (src != null) { used = n; break; } }
                if (src == null) { Debug.LogWarning($"{MyMod.PluginName}: staff projectiles: FiresStaff_{st.staff} -> {st.clone}: NO source ({string.Join(" / ", st.sources)}); this staff keeps the shared projectile"); anyFallback = true; continue; }
                bool fallback = used != st.sources[0];
                anyFallback |= fallback;
                var clone = UnityEngine.Object.Instantiate(src, _holder.transform, false);
                clone.name = st.clone;
                int tinted = Tint(clone, st.tint, st.size);
                int fx = 0;
                var proj = clone.GetComponent<Projectile>();
                if (proj != null) { proj.m_hitEffects = CloneEffects(proj.m_hitEffects, st.clone + "_hit", st.tint, st.size, ref fx); }
                Clones[st.staff] = clone;
                if (clone.GetComponent<ZNetView>() != null) Registered.Add(clone);
                Debug.Log($"{MyMod.PluginName}: staff projectiles: FiresStaff_{st.staff} -> {st.clone} from '{used}'{(fallback ? $" (FALLBACK: '{st.sources[0]}' not found)" : "")}; tint {ColorUtility.ToHtmlStringRGB(st.tint)}, size x{st.size:0.##}, {tinted} part(s) tinted, {fx} hit effect(s) cloned");
            }
            // a fallback means a guessed name was wrong: list the real projectile prefabs once, so the next build can pick them
            if (anyFallback && !_listed)
            {
                _listed = true;
                var names = zs.m_prefabs.Where(p => p != null && p.GetComponent<Projectile>() != null).Select(p => p.name).OrderBy(n => n).ToArray();
                Debug.Log($"{MyMod.PluginName}: staff projectiles: {names.Length} projectile prefab(s) in ZNetScene: {string.Join(", ", names)}");
            }
            var named = (Dictionary<int, GameObject>)AccessTools.Field(typeof(ZNetScene), "m_namedPrefabs").GetValue(zs);
            foreach (var go in Registered)
                if (go != null && zs.GetPrefab(go.name) == null) { zs.m_prefabs.Add(go); named[go.name.GetStableHashCode()] = go; }
            Debug.Log($"{MyMod.PluginName}: staff projectiles: {Clones.Count}/{Styles.Length} built, {Registered.Count} registered in ZNetScene; mode {Mode?.Value ?? "Shared"}");
        }

        private static EffectList CloneEffects(EffectList src, string name, Color tint, float size, ref int count)
        {
            if (src?.m_effectPrefabs == null) return src;
            var list = new EffectList { m_effectPrefabs = new EffectList.EffectData[src.m_effectPrefabs.Length] };
            for (int i = 0; i < src.m_effectPrefabs.Length; i++)
            {
                var d = src.m_effectPrefabs[i];
                var nd = (EffectList.EffectData)AccessTools.Method(typeof(object), "MemberwiseClone").Invoke(d, null);   // own copy: the source's stays as it is
                if (d.m_prefab != null && d.m_prefab.GetComponentInChildren<ParticleSystem>(true) != null)
                {
                    var fx = UnityEngine.Object.Instantiate(d.m_prefab, _holder.transform, false);
                    fx.name = $"{name}_{i}";
                    Tint(fx, tint, size);
                    if (fx.GetComponent<ZNetView>() != null) Registered.Add(fx);
                    nd.m_prefab = fx;
                    count++;
                }
                list.m_effectPrefabs[i] = nd;
            }
            return list;
        }

        private static readonly int[] ColorProps = { Shader.PropertyToID("_Color"), Shader.PropertyToID("_TintColor"), Shader.PropertyToID("_BaseColor") };
        private static readonly int EmissionProp = Shader.PropertyToID("_EmissionColor");

        // once per clone: particles, trails, lines, lights and their materials (own copies), alpha kept
        private static int Tint(GameObject root, Color tint, float size)
        {
            int n = 0;
            foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main;
                main.startColor = Keep(main.startColor.color, tint);
                main.startSizeMultiplier *= size;
                var col = ps.colorOverLifetime;
                if (col.enabled)
                {
                    var g = new Gradient();
                    var a = col.color.gradient?.alphaKeys ?? new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) };
                    g.SetKeys(new[] { new GradientColorKey(tint, 0f), new GradientColorKey(tint, 1f) }, a);
                    col.color = g;
                }
                n++;
            }
            foreach (var t in root.GetComponentsInChildren<TrailRenderer>(true)) { t.startColor = Keep(t.startColor, tint); t.endColor = Keep(t.endColor, tint); n++; }
            foreach (var l in root.GetComponentsInChildren<LineRenderer>(true)) { l.startColor = Keep(l.startColor, tint); l.endColor = Keep(l.endColor, tint); n++; }
            foreach (var li in root.GetComponentsInChildren<Light>(true)) { li.color = tint; n++; }
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    var m = new Material(mats[i]);
                    foreach (int p in ColorProps) if (m.HasProperty(p)) m.SetColor(p, Keep(m.GetColor(p), tint));
                    if (m.HasProperty(EmissionProp)) { var e = m.GetColor(EmissionProp); float k = Mathf.Max(e.r, e.g, e.b); if (k > 0f) m.SetColor(EmissionProp, tint * k); }
                    mats[i] = m;
                }
                r.sharedMaterials = mats;
            }
            return n;
        }


        // ── held FX ─────────────────────────────────────
        // The staff's own particle systems + lights (the item prefab, so every new held/dropped copy follows) are retinted to the
        // staff's style in Styled mode and put back exactly in Shared. Originals are kept per component; nothing runs per frame.
        // Only particles and lights: the staff's wood/metal materials stay as they are. A staff already in hand follows on re-equip.
        private struct HeldOrig { public ParticleSystem.MinMaxGradient start; public bool colEnabled; public ParticleSystem.MinMaxGradient col; public float size; public Material[] mats; }
        private static readonly Dictionary<ParticleSystem, HeldOrig> HeldPs = new Dictionary<ParticleSystem, HeldOrig>();
        private static readonly Dictionary<Light, Color> HeldLights = new Dictionary<Light, Color>();

        internal static void ApplyHeld(ObjectDB db)
        {
            if (db == null) return;
            bool styled = Styled;
            // The held spores
            // and light ALWAYS take the staff's own colour; most servers run the default Staff Projectiles = Shared, which kept
            // the donor's green. The setting now only picks the projectile (and the extra style system).
            bool tint = true;
            foreach (var st in Styles)
            {
                var item = db.GetItemPrefab("FiresStaff_" + st.staff);
                if (item == null) { Debug.Log($"[EarlyMagic] {st.staff} held glow tint: no FiresStaff_{st.staff} item in this ObjectDB yet"); continue; }
                int ps = 0, lights = 0;
                foreach (var p in item.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (p.gameObject.name == HeldFxName) continue;   // Our own style system, handled by HeldExtra
                    var main = p.main; var col = p.colorOverLifetime; var pr = p.GetComponent<ParticleSystemRenderer>();
                    if (!HeldPs.TryGetValue(p, out var o))
                    {
                        o = new HeldOrig { start = main.startColor, colEnabled = col.enabled, col = col.color, size = main.startSizeMultiplier, mats = pr != null ? pr.sharedMaterials : null };
                        HeldPs[p] = o;
                    }
                    if (tint)
                    {
                        main.startColor = Keep(o.start.color, st.tint);
                        main.startSizeMultiplier = o.size * Mathf.Lerp(1f, st.size, 0.5f);
                        if (o.colEnabled)
                        {
                            var g = new Gradient();
                            var a = o.col.gradient?.alphaKeys ?? new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) };
                            g.SetKeys(new[] { new GradientColorKey(st.tint, 0f), new GradientColorKey(st.tint, 1f) }, a);
                            col.color = g;
                        }
                        if (pr != null && o.mats != null)
                        {
                            var m = new Material[o.mats.Length];
                            for (int i = 0; i < m.Length; i++) { m[i] = o.mats[i] == null ? null : new Material(o.mats[i]); if (m[i] != null) TintMaterial(m[i], st.tint); }
                            pr.sharedMaterials = m;
                        }
                    }
                    else
                    {
                        main.startColor = o.start; main.startSizeMultiplier = o.size; col.color = o.col;
                        if (pr != null && o.mats != null) pr.sharedMaterials = o.mats;
                    }
                    ps++;
                }
                foreach (var li in item.GetComponentsInChildren<Light>(true))
                {
                    if (!HeldLights.TryGetValue(li, out var c)) HeldLights[li] = c = li.color;
                    li.color = tint ? st.tint : c;
                    lights++;
                }
                int extra = HeldExtra(item, st.staff, st.tint, styled);
                Debug.Log($"[EarlyMagic] {st.staff} held glow tint: #{ColorUtility.ToHtmlStringRGB(st.tint)} on {ps} particle system(s)/{lights} light(s); {extra} {(styled ? "Styled" : "Shared")} style system(s) on{(ps + lights == 0 ? " - NOTHING to tint on this prefab" : "")}");
            }
        }

        // ── Colour alone does not read as frost/fire/lightning: one style system per staff ──────────────────
        // A clone of the staff's own spore system (same material, so no new art), next to it on the prefab, with its modules
        // reshaped once at load: frost mist, rising embers, crackle sparks, dripping poison, a slow spirit halo. Max particles is
        // capped at 24 and the emission stays at or under the source's, so the budget is at most the spore system's again.
        // Active only in Styled; Shared turns it off (the prefab keeps it inactive, so held copies made in Shared have none).
        private const string HeldFxName = "FiresHeldFx";
        private static readonly Dictionary<string, string> HeldKind = new Dictionary<string, string>
        {
            { "EmberWand", "embers" }, { "Sunfire", "embers" }, { "Frostglass", "mist" }, { "Thunderpine", "crackle" }, { "Bogfire", "drip" },
            { "Hearthwood", "halo" }, { "Mossbark", "halo" }, { "Bloodroot", "halo" }, { "Moonsilver", "halo" }, { "Barleycrown", "halo" },
        };

        private static int HeldExtra(GameObject item, string staff, Color tint, bool styled)
        {
            var existing = item.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == HeldFxName);
            if (existing == null)
            {
                if (!styled) return 0;   // built on first Styled use only
                var src = item.GetComponentsInChildren<ParticleSystem>(true).FirstOrDefault(p => p.gameObject.name != HeldFxName);
                if (src == null) { Debug.LogWarning($"{MyMod.PluginName}: staff held fx: FiresStaff_{staff} has no particle system to clone a style from"); return 0; }
                var go = UnityEngine.Object.Instantiate(src.gameObject, src.transform.parent, false);
                go.name = HeldFxName;
                foreach (Transform child in go.transform) UnityEngine.Object.Destroy(child.gameObject);   // only the one system
                Shape(go.GetComponent<ParticleSystem>(), HeldKind.TryGetValue(staff, out var k) ? k : "halo", tint, src);
                existing = go.transform;
                Debug.Log($"{MyMod.PluginName}: staff held fx: FiresStaff_{staff} + {HeldFxName} '{(HeldKind.TryGetValue(staff, out var k2) ? k2 : "halo")}' cloned from '{src.gameObject.name}' under '{src.transform.parent?.name}'");
            }
            existing.gameObject.SetActive(styled);
            return styled ? 1 : 0;
        }

        private static void Shape(ParticleSystem ps, string kind, Color tint, ParticleSystem src)
        {
            var main = ps.main;
            float srcRate = src.emission.rateOverTimeMultiplier;
            main.maxParticles = Mathf.Min(main.maxParticles, 24);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var em = ps.emission; var shape = ps.shape; var vel = ps.velocityOverLifetime; var noise = ps.noise;
            var col = ps.colorOverLifetime; col.enabled = true;
            Color a = new Color(tint.r, tint.g, tint.b, 1f);
            Gradient Fade(float peak) { var g = new Gradient(); g.SetKeys(new[] { new GradientColorKey(a, 0f), new GradientColorKey(a, 1f) }, new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(peak, 0.2f), new GradientAlphaKey(0f, 1f) }); return g; }
            switch (kind)
            {
                case "mist":      // slow cold haze + a few bright glints
                    main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2.0f); main.startSpeed = new ParticleSystem.MinMaxCurve(0.02f, 0.1f);
                    main.startSize = new ParticleSystem.MinMaxCurve(0.15f, 0.35f); main.gravityModifier = 0.02f;
                    em.rateOverTimeMultiplier = Mathf.Min(srcRate, 10f);
                    shape.enabled = true; shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = 0.12f;
                    col.color = Fade(0.35f);
                    main.startColor = new ParticleSystem.MinMaxGradient(new Color(a.r, a.g, a.b, 0.5f), Color.white);   // most mist, some white glints
                    break;
                case "embers":    // small bright motes rising and drifting
                    main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.2f); main.startSpeed = new ParticleSystem.MinMaxCurve(0.1f, 0.35f);
                    main.startSize = new ParticleSystem.MinMaxCurve(0.015f, 0.04f); main.gravityModifier = -0.25f;
                    em.rateOverTimeMultiplier = Mathf.Min(srcRate, 14f);
                    shape.enabled = true; shape.shapeType = ParticleSystemShapeType.Cone; shape.angle = 15f; shape.radius = 0.05f;
                    noise.enabled = true; noise.strength = 0.3f; noise.frequency = 1.5f;
                    col.color = Fade(1f); main.startColor = a;
                    break;
                case "crackle":   // short fast sparks in bursts
                    main.startLifetime = new ParticleSystem.MinMaxCurve(0.06f, 0.14f); main.startSpeed = new ParticleSystem.MinMaxCurve(1.2f, 2.5f);
                    main.startSize = new ParticleSystem.MinMaxCurve(0.01f, 0.03f); main.gravityModifier = 0f;
                    em.rateOverTimeMultiplier = 0f;
                    em.SetBursts(new[] { new ParticleSystem.Burst(0f, 4, 7, 1, 0.35f) });   // a few sparks, ~3x a second
                    shape.enabled = true; shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = 0.06f;
                    col.color = Fade(1f); main.startColor = new ParticleSystem.MinMaxGradient(a, Color.white);
                    break;
                case "drip":      // heavy drops falling from the head
                    main.startLifetime = new ParticleSystem.MinMaxCurve(0.7f, 1.0f); main.startSpeed = 0f;
                    main.startSize = new ParticleSystem.MinMaxCurve(0.025f, 0.05f); main.gravityModifier = 0.6f;
                    em.rateOverTimeMultiplier = Mathf.Min(srcRate, 5f);
                    shape.enabled = true; shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = 0.07f;
                    col.color = Fade(0.9f); main.startColor = a;
                    break;
                default:          // "halo": slow motes circling the head
                    main.startLifetime = new ParticleSystem.MinMaxCurve(1.0f, 1.6f); main.startSpeed = 0f;
                    main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.05f); main.gravityModifier = 0f;
                    em.rateOverTimeMultiplier = Mathf.Min(srcRate, 8f);
                    shape.enabled = true; shape.shapeType = ParticleSystemShapeType.Circle; shape.radius = 0.14f;
                    vel.enabled = true; vel.space = ParticleSystemSimulationSpace.Local; vel.orbitalY = 1.5f; vel.x = 0f; vel.y = 0.05f; vel.z = 0f;
                    col.color = Fade(0.8f); main.startColor = a;
                    break;
            }
        }

        private static void TintMaterial(Material m, Color tint)
        {
            foreach (int p in ColorProps) if (m.HasProperty(p)) m.SetColor(p, Keep(m.GetColor(p), tint));
            if (m.HasProperty(EmissionProp)) { var e = m.GetColor(EmissionProp); float k = Mathf.Max(e.r, e.g, e.b); if (k > 0f) m.SetColor(EmissionProp, tint * k); }
        }
        private static Color Keep(Color was, Color tint) => new Color(tint.r, tint.g, tint.b, was.a);
    }
}
