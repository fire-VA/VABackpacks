using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// - 10 staffs FiresStaff_&lt;Name&gt; (bundle copies of the cleaned Wizard Staff with' crown gems): item data
    ///   from the §9.1 table, set here on every ObjectDB build;
    /// - 5 eitr foods FiresFood_&lt;Name&gt; (§9.2): runtime clones of a vanilla cooked food with their own name and stats;
    /// - recipes for all 15, live only while the synced [Early Magic] Enabled is true (default false: new features ship OFF).
    ///   The prefabs are always registered so saved items still load.
    /// The 10 staffs are plain two-handed weapons, not bags. Projectiles: vanilla's own (fire = StaffFireball, frost =
    /// StaffIceShards, the rest StaffFireball); recolours them and owns Mend / the charged bolt, attunement and
    /// Ascension in RPGClasses.
    /// </summary>
    internal static class EarlyMagic
    {
        internal static ConfigEntry<bool> Enabled;

        // prefab suffix, display name, skill, element, bolt damage (q1), recipe
        private static readonly (string id, string name, Skills.SkillType skill, string element, float dmg, string recipe)[] Staffs =
        {
            ("EmberWand", "Ember Wand", Skills.SkillType.ElementalMagic, "fire", 10f, "piece_workbench,1,Wood:8,Flint:4,Resin:6,Feathers:2"),
            ("Hearthwood", "Hearthwood Staff", Skills.SkillType.BloodMagic, "spirit", 6f, "piece_workbench,1,Wood:8,Honey:4,Dandelion:6,LeatherScraps:2"),
            ("Thunderpine", "Thunderpine Staff", Skills.SkillType.ElementalMagic, "lightning", 28f, "forge,1,FineWood:8,Copper:6,GreydwarfEye:6,Resin:4"),
            ("Mossbark", "Mossbark Totem-staff", Skills.SkillType.BloodMagic, "spirit", 14f, "piece_workbench,2,FineWood:8,GreydwarfEye:4,Thistle:6,Bronze:2"),
            ("Bogfire", "Bogfire Staff", Skills.SkillType.ElementalMagic, "poison", 44f, "forge,2,ElderBark:10,Iron:6,Ooze:6,Guck:4"),
            ("Bloodroot", "Bloodroot Censer-staff", Skills.SkillType.BloodMagic, "spirit", 22f, "forge,2,ElderBark:8,Iron:4,Bloodbag:8,Turnip:6"),
            ("Frostglass", "Frostglass Staff", Skills.SkillType.ElementalMagic, "frost", 60f, "forge,3,Silver:8,Obsidian:8,FreezeGland:6,WolfFang:4"),
            ("Moonsilver", "Moonsilver Staff", Skills.SkillType.BloodMagic, "spirit", 30f, "forge,3,Silver:10,Crystal:4,WolfPelt:2,Obsidian:4"),
            ("Sunfire", "Sunfire Staff", Skills.SkillType.ElementalMagic, "fire", 76f, "forge,4,BlackMetal:8,Tar:6,LinenThread:4,Needle:6"),
            ("Barleycrown", "Barley-crown Staff", Skills.SkillType.BloodMagic, "spirit", 38f, "forge,4,BlackMetal:6,Barley:10,LinenThread:6,Honey:6"),
        };

        // prefab suffix, display name, vanilla look-alike, health, stamina, eitr, minutes, recipe (all at the cauldron)
        private static readonly (string id, string name, string donor, float hp, float stamina, float eitr, float minutes, string recipe)[] Foods =
        {
            ("MushroomTea", "Mushroom tea", "QueensJam", 8f, 8f, 15f, 20f, "piece_cauldron,1,Mushroom:3,Raspberry:2"),
            ("ThistleBroth", "Thistle broth", "CarrotSoup", 10f, 10f, 25f, 25f, "piece_cauldron,1,Thistle:3,Blueberries:3,Carrot:1"),
            ("BloodrootStew", "Bloodroot stew", "BlackSoup", 12f, 12f, 35f, 25f, "piece_cauldron,1,Bloodbag:2,Turnip:3,Thistle:2"),
            ("FrostLichenPie", "Frost-lichen pie", "OnionSoup", 14f, 14f, 45f, 30f, "piece_cauldron,1,Onion:3,Crystal:1,Honey:2"),
            ("BarleyMeadBread", "Barley mead-bread", "Bread", 16f, 16f, 55f, 30f, "piece_cauldron,1,BarleyFlour:3,Cloudberry:3,Honey:2"),
        };

        internal static IEnumerable<string> StaffPrefabs => Staffs.Select(s => "FiresStaff_" + s.id);

        private static readonly Dictionary<string, GameObject> FoodClones = new Dictionary<string, GameObject>();
        private static GameObject _holder;

        internal static void Bind(ConfigFile cfg, ConfigSync sync)
        {
            Enabled = cfg.Bind("Early Magic", "Enabled", false,
                "The early magic gear (10 staffs + 5 eitr foods, Tools\\EARLY_MAGIC_GEAR_DESIGN.md): when false nothing can be crafted; items already owned still load. Server-synced.");
            sync?.AddConfigEntry(Enabled);
            Enabled.SettingChanged += (_, __) => ApplyEnabled(ObjectDB.instance);
            // A staff's q1 damage =
            // its tier's vanilla one-hand melee total (read from ObjectDB at load) x the share x the tier multiplier
            const string sec = "Early Magic Damage";
            for (int i = 0; i < TierNames.Length; i++)
            {
                TierMult[i] = cfg.Bind(sec, TierNames[i] + " multiplier", 1f, $"Multiplies every {TierNames[i]} staff's damage (reference: vanilla {TierRefItem[i]}). Server-synced.");
                sync?.AddConfigEntry(TierMult[i]);
                TierMult[i].SettingChanged += (_, __) => ReapplyStaffs();
            }
            ElementalShare = cfg.Bind(sec, "Elemental share", 0.9f, "Elemental staff bolt damage as a share of the tier's vanilla one-hand melee damage. Server-synced.");
            BloodShare = cfg.Bind(sec, "Blood share", 0.6f, "Blood magic staff bolt damage as a share of the tier's vanilla one-hand melee damage (they also heal). Server-synced.");
            PerQuality = cfg.Bind(sec, "Per quality", 0.12f, "Damage added per quality level, as a share of the q1 damage. Server-synced.");
            MistileShare = cfg.Bind(sec, "Mistile share", 0.5f, "The homing Mistile (Bogfire, Sunfire) hits for this share of the bolt's damage, on top of the bolt. Server-synced.");
            foreach (var e in new[] { ElementalShare, BloodShare, PerQuality, MistileShare }) { sync?.AddConfigEntry(e); e.SettingChanged += (_, __) => ReapplyStaffs(); }
        }

        internal static ConfigEntry<float> ElementalShare, BloodShare, PerQuality, MistileShare;
        private static readonly string[] TierNames = { "Meadows", "Black Forest", "Swamp", "Mountain", "Plains" };
        private static readonly string[] TierRefItem = { "Club", "SwordBronze", "SwordIron", "SwordSilver", "SwordBlackmetal" };
        private static readonly float[] TierRefFallback = { 12f, 35f, 55f, 105f, 95f };   // used only when the vanilla item is missing
        private static readonly ConfigEntry<float>[] TierMult = new ConfigEntry<float>[5];
        private static int TierOf(string id)
        {
            switch (id)
            {
                case "EmberWand": case "Hearthwood": return 0;
                case "Thunderpine": case "Mossbark": return 1;
                case "Bogfire": case "Bloodroot": return 2;
                case "Frostglass": case "Moonsilver": return 3;
                default: return 4;   // Sunfire, Barleycrown
            }
        }
        private static float TierRef(ObjectDB db, int t, out bool live)
        {
            var sh = db != null ? db.GetItemPrefab(TierRefItem[t])?.GetComponent<ItemDrop>()?.m_itemData?.m_shared : null;
            float v = sh != null ? sh.m_damages.GetTotalDamage() : 0f;
            live = v > 0f;
            return live ? v : TierRefFallback[t];
        }
        private static float StaffDamage(ObjectDB db, string id, Skills.SkillType skill, float old, out string why)
        {
            int t = TierOf(id);
            float r = TierRef(db, t, out bool live);
            float share = skill == Skills.SkillType.BloodMagic ? (BloodShare?.Value ?? 0.6f) : (ElementalShare?.Value ?? 0.9f);
            float m = TierMult[t]?.Value ?? 1f;
            float v = Mathf.Round(r * share * m);
            why = $"{TierNames[t]} ref {TierRefItem[t]} {r:0.#}{(live ? "" : " (FALLBACK, item missing)")} x {share:0.##} x {m:0.##} = {v:0.#} (was {old:0.#})";
            return v;
        }

        // ── staffs ───────────────────────────────────────────────────────────────────────────────────────────────────────
        private static void ApplyStaffData(ObjectDB db)
        {
            var fireball = db.GetItemPrefab("StaffFireball")?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_attack;
            var shards = db.GetItemPrefab("StaffIceShards")?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_attack;
            int done = 0;
            foreach (var s in Staffs)
            {
                var sh = db.GetItemPrefab("FiresStaff_" + s.id)?.GetComponent<ItemDrop>()?.m_itemData?.m_shared;
                if (sh == null) continue;
                sh.m_name = s.name;
                sh.m_itemType = ItemDrop.ItemData.ItemType.TwoHandedWeapon;
                sh.m_skillType = s.skill;
                sh.m_weight = 2.5f;
                sh.m_maxDurability = 150f;
                sh.m_durabilityPerLevel = 50f;
                sh.m_maxQuality = 4;
                float dmg = StaffDamage(db, s.id, s.skill, s.dmg, out string why);
                sh.m_damages = Damage(s.element, dmg);
                sh.m_damagesPerLevel = Damage(s.element, dmg * (PerQuality?.Value ?? 0.12f));
                Debug.Log($"{MyMod.PluginName}: early magic: FiresStaff_{s.id} tier damage: {why}, +{PerQuality?.Value ?? 0.12f:0.##}/q");
                // Each staff its OWN array
                var icon = StaffIcon(s.id);
                if (icon != null) sh.m_icons = new[] { icon };
                var donor = s.element == "frost" ? shards ?? fireball : fireball;
                if (sh.m_attack != null)
                {
                    if (donor?.m_attackProjectile != null) sh.m_attack.m_attackProjectile = donor.m_attackProjectile;
                    var styled = StaffProjectiles.For(s.id);   // Styled mode, once the clones exist (ZNetScene.Awake)
                    // Staff Attack Styles rides on the staff's own clone, so it uses it even in Shared mode
                    if (styled == null && StaffAttackStyles.On) styled = StaffProjectiles.Clone(s.id);
                    if (styled != null) sh.m_attack.m_attackProjectile = styled;
                    sh.m_attack.m_attackEitr = 4f;      // Early pools are 15-55
                    sh.m_attack.m_attackStamina = 6f;
                    StaffAttackStyles.Apply(sh, s.id, StaffProjectiles.Clone(s.id));   // Primary extras + the secondary (or back to plain when off)
                }
                Debug.Log($"{MyMod.PluginName}: early magic: FiresStaff_{s.id} damage q1 blunt {sh.m_damages.m_blunt:0.#} fire {sh.m_damages.m_fire:0.#} frost {sh.m_damages.m_frost:0.#} lightning {sh.m_damages.m_lightning:0.#} poison {sh.m_damages.m_poison:0.#} spirit {sh.m_damages.m_spirit:0.#} (+{sh.m_damagesPerLevel.GetTotalDamage():0.#}/q), skill {sh.m_skillType}");   // Per staff, at load
                done++;
            }
            Debug.Log($"{MyMod.PluginName}: early magic: {done}/{Staffs.Length} staff(s) set up (projectiles: fireball {(fireball?.m_attackProjectile != null)}, ice shards {(shards?.m_attackProjectile != null)})");
        }

        // The staff icons (Resources\StaffIcons\item_Staff_<Name>.png,), loaded once; a missing one keeps the old icon
        private static readonly Dictionary<string, Sprite> Icons = new Dictionary<string, Sprite>();
        private static Sprite StaffIcon(string id)
        {
            if (Icons.TryGetValue(id, out var have)) return have;
            // A headless server never draws an icon; no PNG decode there
            if (VerdantsAscent.Utilities.DedicatedStrip.Headless) { Icons[id] = null; return null; }
            Sprite s = null;
            try
            {
                var asm = typeof(EarlyMagic).Assembly;
                string res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("item_Staff_" + id + ".png", StringComparison.OrdinalIgnoreCase));
                if (res != null)
                    using (var st = asm.GetManifestResourceStream(res))
                    using (var ms = new System.IO.MemoryStream())
                    {
                        st.CopyTo(ms);
                        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        var load = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule")?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });
                        if (load != null && (bool)load.Invoke(null, new object[] { tex, ms.ToArray() }))
                        {
                            tex.name = "FiresStaff_" + id + "_icon";
                            s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                            s.name = tex.name;
                        }
                    }
                Debug.Log($"{MyMod.PluginName}: early magic: FiresStaff_{id} icon {(s != null ? $"{s.texture.width}x{s.texture.height} from {res}" : "MISSING (keeps the base staff's)")}");
            }
            catch (Exception e) { Debug.LogWarning($"{MyMod.PluginName}: early magic: FiresStaff_{id} icon failed: {e.Message}"); }
            Icons[id] = s;
            return s;
        }

        // On a fresh game the held effect was not tinted to match the gem
        // because the held tint ran only from ReapplyStaffs, which returns early when ZNetScene wakes before
        // ObjectDB.instance is set - so it never ran. It now also runs right after the staff data in the ObjectDB hooks (Awake / CopyOtherDB).
        private static void HeldTint(ObjectDB db)
        {
            try { StaffProjectiles.ApplyHeld(db); }
            catch (Exception e) { Debug.LogWarning($"{MyMod.PluginName}: early magic: held glow tint failed: {e.Message}"); }
        }

        // [Early Magic] Staff Projectiles changed, or the clones were just built
        internal static void ReapplyStaffs()
        {
            if (ObjectDB.instance == null) { Debug.Log($"{MyMod.PluginName}: early magic: re-apply skipped (ObjectDB not up yet; the ObjectDB hooks apply the staffs + held glow)"); return; }
            try { ApplyStaffData(ObjectDB.instance); StaffProjectiles.ApplyHeld(ObjectDB.instance); } catch (Exception e) { Debug.LogWarning($"{MyMod.PluginName}: early magic: staff re-apply failed: {e.Message}"); }
            Debug.Log($"{MyMod.PluginName}: early magic: staff projectiles {(StaffProjectiles.Styled ? "Styled" : "Shared")}: " + string.Join(", ", Staffs.Select(s => s.id + "=" + (ObjectDB.instance.GetItemPrefab("FiresStaff_" + s.id)?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_attack?.m_attackProjectile?.name ?? "none")).ToArray()));
        }

        private static HitData.DamageTypes Damage(string element, float v)
        {
            var d = new HitData.DamageTypes();
            switch (element)
            {
                case "fire": d.m_fire = v; break;
                case "frost": d.m_frost = v; break;
                case "lightning": d.m_lightning = v; break;
                case "poison": d.m_poison = v; break;
                // Healer bolts would do knockback but no damage, as vanilla
                // creatures are immune to spirit: the healer value is BLUNT, plus SPIRIT at half on top (undead take extra)
                default: d.m_blunt = v; d.m_spirit = v * 0.5f; break;
            }
            return d;
        }

        // ── foods (runtime clones: no new art) ───────────────────────────────────────────────────────────────────────────
        private static void MakeFoods(ObjectDB db)
        {
            if (_holder == null) { _holder = new GameObject("VABackpacks_EarlyMagicFoods"); _holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(_holder); }
            foreach (var f in Foods)
            {
                string name = "FiresFood_" + f.id;
                if (!FoodClones.TryGetValue(name, out var clone) || clone == null)
                {
                    var donor = db.GetItemPrefab(f.donor);
                    if (donor == null) { Debug.LogWarning($"{MyMod.PluginName}: early magic: no '{f.donor}' to make {name} from"); continue; }
                    clone = UnityEngine.Object.Instantiate(donor, _holder.transform, false);
                    clone.name = name;
                    var drop = clone.GetComponent<ItemDrop>();
                    drop.m_itemData.m_dropPrefab = clone;
                    var sh = drop.m_itemData.m_shared;
                    sh.m_name = f.name;
                    sh.m_description = $"+{f.eitr:0} max eitr for {f.minutes:0} minutes. A small meal for a spellcaster.";
                    sh.m_food = f.hp; sh.m_foodStamina = f.stamina; sh.m_foodEitr = f.eitr;
                    sh.m_foodBurnTime = f.minutes * 60f; sh.m_foodRegen = 1f;
                    FoodClones[name] = clone;
                }
                if (db.GetItemPrefab(name) == null)
                {
                    db.m_items.Add(clone);
                    var byHash = (Dictionary<int, GameObject>)AccessTools.Field(typeof(ObjectDB), "m_itemByHash").GetValue(db);
                    byHash[name.GetStableHashCode()] = clone;
                }
            }
        }

        private static void AddFoodsToZNetScene(ZNetScene zs)
        {
            if (zs == null) return;
            var named = (Dictionary<int, GameObject>)AccessTools.Field(typeof(ZNetScene), "m_namedPrefabs").GetValue(zs);
            foreach (var kv in FoodClones)
                if (kv.Value != null && zs.GetPrefab(kv.Key) == null) { zs.m_prefabs.Add(kv.Value); named[kv.Key.GetStableHashCode()] = kv.Value; }
        }

        // ── recipes: always added, m_enabled follows the synced switch ───────────────────────────────────────────────────
        private static readonly List<Recipe> Made = new List<Recipe>();

        private static void AddRecipes(ObjectDB db)
        {
            Made.RemoveAll(r => r == null);
            foreach (var (prefab, text) in Staffs.Select(s => ("FiresStaff_" + s.id, s.recipe)).Concat(Foods.Select(f => ("FiresFood_" + f.id, f.recipe))))
            {
                var item = db.GetItemPrefab(prefab)?.GetComponent<ItemDrop>();
                if (item == null) continue;
                var existing = db.m_recipes.FirstOrDefault(r => r != null && r.m_item == item);
                if (existing != null) { if (!Made.Contains(existing)) Made.Add(existing); continue; }
                var recipe = BackpackCraftingAndLoot.ParseRecipe(db, item, text, out string why);
                if (recipe == null) { Debug.LogWarning($"{MyMod.PluginName}: early magic: no recipe for {prefab}: {why}"); continue; }
                if (prefab.StartsWith("FiresStaff_", StringComparison.Ordinal))
                    foreach (var r in recipe.m_resources) r.m_amountPerLevel = Mathf.Max(1, Mathf.CeilToInt(r.m_amount / 2f));   // §9.1: half the recipe per quality
                db.m_recipes.Add(recipe);
                Made.Add(recipe);
            }
            ApplyEnabled(db);
        }

        private static void ApplyEnabled(ObjectDB db)
        {
            bool on = Enabled != null && Enabled.Value;
            foreach (var r in Made) if (r != null) r.m_enabled = on;
            Debug.Log($"{MyMod.PluginName}: early magic {(on ? "ON" : "OFF")}: {Made.Count(r => r != null)} recipe(s) {(on ? "craftable" : "hidden")} ([Early Magic] Enabled = {on})");
        }

        [HarmonyPatch(typeof(ObjectDB), "Awake")]
        private static class ObjectDB_Awake
        {
            [HarmonyPriority(Priority.VeryLow)]
            private static void Postfix(ObjectDB __instance)
            {
                if (__instance == null || __instance.m_items == null || __instance.m_items.Count == 0) return;
                try { ApplyStaffData(__instance); MakeFoods(__instance); AddRecipes(__instance); HeldTint(__instance); }
                catch (Exception e) { Debug.LogWarning($"{MyMod.PluginName}: early magic setup failed: {e}"); }
            }
        }

        [HarmonyPatch(typeof(ObjectDB), "CopyOtherDB")]
        private static class ObjectDB_CopyOtherDB
        {
            [HarmonyPriority(Priority.VeryLow)]
            private static void Postfix(ObjectDB __instance)
            {
                try { ApplyStaffData(__instance); MakeFoods(__instance); HeldTint(__instance); } catch (Exception e) { Debug.LogWarning($"{MyMod.PluginName}: early magic (menu DB) failed: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class ZNetScene_Awake
        {
            [HarmonyPriority(Priority.VeryLow)]
            private static void Postfix(ZNetScene __instance)
            {
                if (ObjectDB.instance != null) { try { MakeFoods(ObjectDB.instance); } catch { } }
                AddFoodsToZNetScene(__instance);
                try { StaffProjectiles.Build(__instance); } catch (Exception e) { Debug.LogWarning($"{MyMod.PluginName}: staff projectiles failed: {e}"); }
                // The attack styles' AoEs + heal effects, after the projectile clones; its own try so it can't stop them
                try { StaffAttackStyles.Build(__instance); StaffAttackStyles.RegisterEffects(ObjectDB.instance); } catch (Exception e) { Debug.LogWarning($"{MyMod.PluginName}: staff attack styles failed: {e}"); }
                try { ReapplyStaffs(); } catch (Exception e) { Debug.LogWarning($"{MyMod.PluginName}: staff re-apply failed: {e}"); }
            }
        }
    }
}
