// Tool Swapper (tool slot): swinging at a tree, log, rock or ore with the wrong thing in hand puts the right tool from the bag in
// hand first (an axe for wood, a pickaxe for stone and ore), and puts the previous weapon back after a few seconds without such
// a target. Tier I: axes only; II: + pickaxes; III: picks the BEST tool of the bag (the highest tool tier, then the most damage)
// even when a weaker one is already in hand. Local player only.

using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public sealed class ToolSwapperOption : IBagUpgradeOption
    {
        public const string UpgradeId = "ToolSwapper";
        private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks.ToolSwapper");

        private const float DefaultReach = 4f, DefaultSwitchBack = 5f;
        private static ConfigEntry<float> s_reach, s_switchBack;
        private static readonly int TargetMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain");

        public string Id => UpgradeId;
        public string Name => "Tool Swapper";
        public string Slot => BackpackSpecialSlots.SlotTool;
        public string Donor => "PickaxeAntler";
        public string[] Recipes => new[]
        {
            "forge,1,Bronze:5,FineWood:10,Flint:10",
            "forge,3,Iron:10,ElderBark:10,Chain:1",
            "forge,5,BlackMetal:10,YggdrasilWood:10,Iron:5",
        };

        public string Describe(int tier) => tier switch
        {
            1 => "Swing at a tree or log with the wrong thing in hand and the bag hands you an axe; your weapon comes back after a few seconds.",
            2 => "Swing at a tree, rock or ore with the wrong thing in hand and the bag hands you an axe or a pickaxe; your weapon comes back after a few seconds.",
            _ => "Swing at a tree, rock or ore and the bag hands you its BEST axe or pickaxe, even over a weaker one in hand; your weapon comes back after a few seconds.",
        };

        public void Bind(ConfigFile cfg, ConfigSync sync)
        {
            s_reach = cfg.Bind(BagUpgrades.Section, "Tool Swapper reach", DefaultReach, "How far ahead (m) a tree / rock / ore counts as the swing's target. Server-synced.");
            s_switchBack = cfg.Bind(BagUpgrades.Section, "Tool Swapper switch back seconds", DefaultSwitchBack, "Seconds without hitting such a target before the previous weapon is put back. Server-synced.");
            sync?.AddConfigEntry(s_reach); sync?.AddConfigEntry(s_switchBack);
            BagUpgrades.Tick += OnTick;
        }

        private enum Need { None, Axe, Pickaxe }

        private static ItemDrop.ItemData s_previous;   // what was in hand before the swap (null = nothing to put back)
        private static float s_lastSwapUse;

        // Every layer a swing can hit a tree / rock / ore on; characters and items never count (they'd shadow the target behind them).
        private static readonly int RayMask = ~LayerMask.GetMask("character", "character_net", "character_ghost", "character_noenv", "item", "UI", "ragdoll", "viewblock", "WaterVolume", "Water", "smoke", "effect");
        private static readonly RaycastHit[] s_hits = new RaycastHit[16];

        /// <summary>
        /// What the thing in front of the player wants: an axe (trees, logs, wood destructibles) or a pickaxe (rocks, ore), and the
        /// tool tier it needs. The ray looks past the ground and anything that isn't a target to the first tree / rock / ore within
        /// reach: a copper / tin / silver deposit (MineRock5) sits half in the ground, so the aim ray met the terrain first and nothing
        /// swapped (Fire 10-23, MineRock_Copper). Every miss says why.
        /// </summary>
        private static Need NeedAhead(Player p, out string what, out int minTier)
        {
            what = null; minTier = 0;
            Transform cam = GameCamera.instance != null ? GameCamera.instance.transform : null;
            if (cam == null) return Need.None;
            float reach = (s_reach?.Value ?? DefaultReach) + Vector3.Distance(cam.position, p.GetEyePoint());
            int n = Physics.RaycastNonAlloc(cam.position, cam.forward, s_hits, reach, RayMask, QueryTriggerInteraction.Collide);
            if (n == 0) { what = "nothing within " + reach.ToString("0.#") + " m"; return Need.None; }
            System.Array.Sort(s_hits, 0, n, Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance)));
            var seen = new List<string>();
            for (int i = 0; i < n; i++)
            {
                Transform t = s_hits[i].collider != null ? s_hits[i].collider.transform : null;
                if (t == null || t.GetComponentInParent<Player>() != null) continue;
                Need need = Classify(t, out what, out minTier);
                // vanilla's own: what the swing would damage (Projectile.FindHitObject: the IDestructible above, or the rigidbody)
                if (need == Need.None)
                {
                    var hitGo = FindHitObject(s_hits[i].collider);
                    if (hitGo != null && hitGo.transform != t) need = Classify(hitGo.transform, out what, out minTier);
                }
                // a networked object's own root (a deposit whose pieces sit beside its MineRock5, not under it)
                if (need == Need.None)
                {
                    var nv = t.GetComponentInParent<ZNetView>();
                    if (nv != null)
                    {
                        var r5 = nv.GetComponentInChildren<MineRock5>(); var r = nv.GetComponentInChildren<MineRock>();
                        if (r5 != null) { what = nv.name; minTier = r5.m_minToolTier; need = Need.Pickaxe; }
                        else if (r != null) { what = nv.name; minTier = r.m_minToolTier; need = Need.Pickaxe; }
                    }
                }
                if (need != Need.None) { what += $" (resolved from {t.name})"; return need; }
                if (seen.Count < 4) seen.Add($"{t.name} ({LayerMask.LayerToName(t.gameObject.layer)}, {s_hits[i].distance:0.#} m, under {Chain(t)})");
            }
            what = "no tree / rock / ore on the ray: " + string.Join(", ", seen);
            return Need.None;
        }

        private static System.Reflection.MethodInfo s_findHit; private static bool s_findHitLooked;

        private static GameObject FindHitObject(Collider c)
        {
            if (c == null) return null;
            if (!s_findHitLooked) { s_findHitLooked = true; s_findHit = AccessTools.Method(typeof(Projectile), "FindHitObject", new[] { typeof(Collider) }); }
            try { if (s_findHit != null) return s_findHit.Invoke(null, new object[] { c }) as GameObject; } catch { }
            return c.attachedRigidbody != null ? c.attachedRigidbody.gameObject : c.gameObject;
        }

        // the parent chain (up to 4) with the components on each, for a miss line
        private static string Chain(Transform t)
        {
            var parts = new List<string>();
            for (var p = t.parent; p != null && parts.Count < 4; p = p.parent)
                parts.Add(p.name + "[" + string.Join(",", p.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name).Take(4)) + "]");
            return parts.Count == 0 ? "the scene root" : string.Join(" < ", parts);
        }

        private static Need Classify(Transform t, out string what, out int minTier)
        {
            what = t.root.name; minTier = 0;
            var tree = t.GetComponentInParent<TreeBase>();
            if (tree != null) { minTier = tree.m_minToolTier; return Need.Axe; }
            var log = t.GetComponentInParent<TreeLog>();
            if (log != null) { minTier = log.m_minToolTier; return Need.Axe; }
            var rock5 = t.GetComponentInParent<MineRock5>();
            if (rock5 != null) { minTier = rock5.m_minToolTier; return Need.Pickaxe; }
            var rock = t.GetComponentInParent<MineRock>();
            if (rock != null) { minTier = rock.m_minToolTier; return Need.Pickaxe; }
            var d = t.GetComponentInParent<Destructible>();
            if (d != null)
            {
                what = d.name; minTier = d.m_minToolTier;
                if (d.m_destructibleType == DestructibleType.Tree) return Need.Axe;
                bool chopImmune = d.m_damages.m_chop == HitData.DamageModifier.Immune || d.m_damages.m_chop == HitData.DamageModifier.Ignore;
                bool pickImmune = d.m_damages.m_pickaxe == HitData.DamageModifier.Immune || d.m_damages.m_pickaxe == HitData.DamageModifier.Ignore;
                if (chopImmune && !pickImmune) return Need.Pickaxe;
                if (pickImmune && !chopImmune) return Need.Axe;
            }
            return Need.None;
        }

        private static float Power(ItemDrop.ItemData it, Need need) => need == Need.Axe ? it.m_shared.m_damages.m_chop : it.m_shared.m_damages.m_pickaxe;

        private static bool Fits(ItemDrop.ItemData it, Need need) => it != null && it.IsWeapon() && Power(it, need) > 0f;

        private static ItemDrop.ItemData Best(IEnumerable<ItemDrop.ItemData> items, Need need)
            => items.Where(i => Fits(i, need)).OrderByDescending(i => i.m_shared.m_toolTier).ThenByDescending(i => Power(i, need)).FirstOrDefault();

        /// <summary>Called on the swing (StartAttack): put the right tool in hand when the bag has one and the tier allows it.</summary>
        private static void BeforeSwing(Player p)
        {
            int tier = BagUpgrades.WornTier(p, UpgradeId);
            if (tier == 0) return;
            Need need = NeedAhead(p, out string what, out int minTier);
            if (need == Need.None) { Log.LogDebug($"[{UpgradeId}] swing: no tool rule - {what}"); return; }
            if (need == Need.Pickaxe && tier < 2) { Log.LogDebug($"[{UpgradeId}] swing at {what}: a pickaxe target, Tool Swapper I swaps axes only"); return; }
            ItemDrop.ItemData inHand = p.GetCurrentWeapon();
            bool handFits = Fits(inHand, need) && inHand.m_shared.m_toolTier >= minTier;   // a tool too weak for the target (antler pick on copper) is swapped at any tier
            if (handFits && tier < 3) { s_lastSwapUse = Time.time; return; }
            ItemDrop.ItemData bag = BagUpgrades.WornBagWith(p, UpgradeId);
            ItemDrop.ItemData best = Best(BagUpgrades.ItemsInBag(p, bag).Concat(p.GetInventory().GetAllItems()).Distinct().Where(i => i != null && i.m_shared.m_toolTier >= minTier), need);
            if (best == null) { Log.LogDebug($"[{UpgradeId}] swing at {what}: {(need == Need.Axe ? "axe" : "pickaxe")} of tool tier {minTier}+ needed, none in the bag or inventory"); s_lastSwapUse = Time.time; return; }
            if (best == null || best == inHand || (handFits && best.m_shared.m_toolTier <= inHand.m_shared.m_toolTier && Power(best, need) <= Power(inHand, need))) { s_lastSwapUse = Time.time; return; }
            if (s_previous == null && inHand != null && !handFits) s_previous = inHand;
            p.EquipItem(best, false);
            s_lastSwapUse = Time.time;
            Log.LogDebug($"[{UpgradeId}] swing at {what}: {(need == Need.Axe ? "axe" : "pickaxe")} needed, {(inHand != null ? inHand.m_shared.m_name : "nothing")} in hand -> {best.m_shared.m_name} (tool tier {best.m_shared.m_toolTier}, tier {tier})");
        }

        private static void OnTick(Player p, float dt)
        {
            if (s_previous == null) return;
            if (Time.time - s_lastSwapUse < (s_switchBack?.Value ?? DefaultSwitchBack) || p.InAttack()) return;
            ItemDrop.ItemData prev = s_previous;
            s_previous = null;
            if (!p.GetInventory().ContainsItem(prev) || p.IsItemEquiped(prev)) return;
            p.EquipItem(prev, false);
            Log.LogDebug($"[{UpgradeId}] no tree / rock for {(s_switchBack?.Value ?? DefaultSwitchBack):0.#} s: {prev.m_shared.m_name} back in hand");
        }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
        private static class StartAttack_Patch
        {
            private static void Prefix(Humanoid __instance, bool secondaryAttack)
            {
                if (secondaryAttack || __instance != Player.m_localPlayer || __instance.InAttack()) return;
                try { BeforeSwing((Player)__instance); }
                catch (Exception e) { Log.LogWarning($"[{UpgradeId}] swap check threw {e.Message}"); }
            }
        }
    }
}
