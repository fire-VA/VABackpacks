// BagUpgrades - the framework for consumable upgrades that sit in a bag's special slots (Tools\PLAN_BAG_UPGRADES_20261023.md,
// contract: Tools\BAG_UPGRADES_CONTRACT.md).
// An upgrade is an item that fits ONLY its own slot kind. Once slotted it belongs to the bag: VAInventory's rail asks before a swap
// or a removal and destroys the old upgrade (never handed back), so an upgrade can't move to another bag.
// Each option is ONE file under Managers\BagUpgradeOptions\ with a class implementing IBagUpgradeOption; the framework finds every
// such class at startup, makes its three tier items ("FiresBagUp_<Id>_<tier>"), adds their recipes behind [Bag Upgrades] Enabled,
// and binds its config. The option's own Harmony patches ask WornTier(player, Id) for the tier the local player's worn bag has.

using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    /// <summary>One bag upgrade option. Implement it in Managers\BagUpgradeOptions\&lt;Id&gt;.cs; a public parameterless constructor.</summary>
    public interface IBagUpgradeOption
    {
        /// <summary>Prefab id, no spaces: the items are FiresBagUp_&lt;Id&gt;_1..3.</summary>
        string Id { get; }
        /// <summary>Display name: the item reads "&lt;Name&gt; Bag Upgrade I/II/III".</summary>
        string Name { get; }
        /// <summary>BackpackSpecialSlots.SlotTool / SlotResources / SlotBypass.</summary>
        string Slot { get; }
        /// <summary>The vanilla item cloned for the look (icon, model) until it has its own art.</summary>
        string Donor { get; }
        /// <summary>Tiers 1..3: "station,level,Item:amount,..." (BackpackCraftingAndLoot.ParseRecipe format).</summary>
        string[] Recipes { get; }
        /// <summary>What the tier does, one sentence for the item description.</summary>
        string Describe(int tier);
        /// <summary>Bind the option's tuning numbers under [Bag Upgrades], each server-synced.</summary>
        void Bind(ConfigFile cfg, ConfigSync sync);
    }

    public static class BagUpgrades
    {
        public const string PrefabPrefix = "FiresBagUp_";
        public const int TierCount = 3;
        public const string Section = "Bag Upgrades";
        public static readonly string[] TierNumerals = { "I", "II", "III" };
        private const float UpgradeWeight = 0.5f;

        internal static ConfigEntry<bool> Enabled;

        private static readonly List<IBagUpgradeOption> Options = FindOptions();
        private static readonly Dictionary<string, GameObject> Clones = new Dictionary<string, GameObject>();
        private static readonly List<Recipe> Made = new List<Recipe>();
        private static GameObject _holder;
        private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks.Upgrades");

        private static List<IBagUpgradeOption> FindOptions()
        {
            var found = new List<IBagUpgradeOption>();
            foreach (Type t in typeof(BagUpgrades).Assembly.GetTypes())
            {
                if (t.IsAbstract || t.IsInterface || !typeof(IBagUpgradeOption).IsAssignableFrom(t)) continue;
                try { found.Add((IBagUpgradeOption)Activator.CreateInstance(t)); }
                catch (Exception e) { BepInEx.Logging.Logger.CreateLogSource("VABackpacks.Upgrades").LogWarning($"bag upgrade option {t.Name} did not load: {e.Message}"); }
            }
            return found.OrderBy(o => o.Slot).ThenBy(o => o.Id).ToList();
        }

        internal static void Bind(ConfigFile cfg, ConfigSync sync)
        {
            Enabled = cfg.Bind(Section, "Enabled", false, "Bag upgrades can be crafted. Upgrades already slotted keep working. Server-synced.");
            sync?.AddConfigEntry(Enabled);
            Enabled.SettingChanged += (_, __) => ApplyEnabled();
            foreach (IBagUpgradeOption o in Options)
            {
                try { o.Bind(cfg, sync); }
                catch (Exception e) { Log.LogWarning($"{MyMod.PluginName}: bag upgrade {o.Id}: config did not bind: {e.Message}"); }
            }
            Log.LogDebug($"{MyMod.PluginName}: bag upgrade options: {string.Join(", ", Options.Select(o => $"{o.Id} ({o.Slot})").ToArray())}");
        }

        // ── what an upgrade is ─────────────────────────────────────────────────────────────────────────

        public static string PrefabName(string id, int tier) => $"{PrefabPrefix}{id}_{tier}";

        public static bool IsUpgrade(ItemDrop.ItemData item) => Parse(item, out _, out _) != null;

        /// <summary>The slot kind an upgrade fits, or null when the item is no upgrade.</summary>
        public static string SlotOf(ItemDrop.ItemData item) => Parse(item, out _, out _)?.Slot;

        private static IBagUpgradeOption Parse(ItemDrop.ItemData item, out string id, out int tier)
        {
            id = null; tier = 0;
            string name = item?.m_dropPrefab != null ? item.m_dropPrefab.name : null;
            if (name == null || !name.StartsWith(PrefabPrefix, StringComparison.Ordinal)) return null;
            int cut = name.LastIndexOf('_');
            if (cut <= PrefabPrefix.Length || !int.TryParse(name.Substring(cut + 1), out tier) || tier < 1 || tier > TierCount) return null;
            string parsedId = name.Substring(PrefabPrefix.Length, cut - PrefabPrefix.Length);
            id = parsedId;
            return Options.FirstOrDefault(o => o.Id == parsedId);
        }

        /// <summary>The tier (1-3) of the given upgrade id slotted in this bag, or 0.</summary>
        public static int SlottedTier(ItemDrop.ItemData bag, string id)
        {
            foreach (string kind in BackpackSpecialSlots.Kinds)
            {
                ItemDrop.ItemData it = BackpackSpecialSlots.Read(bag, kind);
                if (Parse(it, out string got, out int tier) != null && got == id) return tier;
            }
            return 0;
        }

        /// <summary>The highest tier of the upgrade id in the player's worn bags (0 = none).</summary>
        public static int WornTier(Player player, string id)
        {
            if (player == null) return 0;
            int tier = 0;
            foreach (ItemDrop.ItemData bag in BackpackApi.GetEquippedBackpacks(player)) tier = Math.Max(tier, SlottedTier(bag, id));
            return tier;
        }

        /// <summary>The worn bag holding the upgrade id (its highest tier), or null.</summary>
        public static ItemDrop.ItemData WornBagWith(Player player, string id)
        {
            if (player == null) return null;
            ItemDrop.ItemData best = null; int bestTier = 0;
            foreach (ItemDrop.ItemData bag in BackpackApi.GetEquippedBackpacks(player))
            {
                int t = SlottedTier(bag, id);
                if (t > bestTier) { bestTier = t; best = bag; }
            }
            return best;
        }

        // ── what an option works on ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Set by VAInventory: the items in one worn bag's cells (with VAInventory a bag's contents are cells of the player inventory).
        /// Null without VAInventory; then the legacy bag container is used.
        /// </summary>
        public static Func<Player, ItemDrop.ItemData, List<ItemDrop.ItemData>> BagCellsProvider;

        /// <summary>The items in the worn bag's contents (cells with VAInventory, the bag container in legacy mode).</summary>
        public static List<ItemDrop.ItemData> ItemsInBag(Player player, ItemDrop.ItemData bag)
        {
            if (player == null || bag == null) return new List<ItemDrop.ItemData>();
            if (BagCellsProvider != null) { try { return BagCellsProvider(player, bag) ?? new List<ItemDrop.ItemData>(); } catch { } }
            Inventory live = BackpackManager.LegacyLiveInventory(player, out ItemDrop.ItemData owner);
            return live != null && owner == bag ? live.GetAllItems().ToList() : new List<ItemDrop.ItemData>();
        }

        /// <summary>
        /// The Inventory a bag's items belong to, for an atomic change (remove + add in the same frame): the player inventory with
        /// VAInventory, the bag container in legacy mode. Add only where it fits (CanAddItem first), never half a conversion.
        /// </summary>
        public static Inventory BagInventory(Player player, ItemDrop.ItemData bag)
        {
            if (player == null || bag == null) return null;
            if (BagCellsProvider != null) return player.GetInventory();
            Inventory live = BackpackManager.LegacyLiveInventory(player, out ItemDrop.ItemData owner);
            return owner == bag ? live : null;
        }

        /// <summary>
        /// Big stacks (Compactor / Trophy Pouch) never leave the bag whole (Tools\PLAN_BIG_STACKS_SAFETY_20261023.md, "B"): every stack
        /// above the item's vanilla max is split into vanilla-size pieces in the bag's free cells, and the returned list holds only
        /// vanilla-size stacks to send. A big stack that can't be split (no free cell) stays in the bag, unsent. The total is exact:
        /// pieces are moved out of the stack, never copied.
        /// </summary>
        public static List<ItemDrop.ItemData> SplitForSend(Player player, ItemDrop.ItemData bag, List<ItemDrop.ItemData> items)
        {
            var send = new List<ItemDrop.ItemData>();
            Inventory inv = BagInventory(player, bag);
            List<Vector2i> free = MagnetOption.FreeBagCells?.Invoke(player, bag) ?? new List<Vector2i>();
            int freeAt = 0;
            foreach (ItemDrop.ItemData it in items)
            {
                if (it == null) continue;
                int max = Math.Max(1, it.m_shared.m_maxStackSize);
                if (it.m_stack <= max) { send.Add(it); continue; }
                int before = it.m_stack, pieces = 0;
                while (it.m_stack > max && inv != null && freeAt < free.Count)
                {
                    ItemDrop.ItemData piece = it.Clone();
                    piece.m_stack = max;
                    if (!inv.AddItem(piece, free[freeAt++])) break;
                    it.m_stack -= max;
                    send.Add(piece);
                    pieces++;
                }
                if (it.m_stack <= max) send.Add(it);
                inv?.Changed();
                Log.LogDebug($"[BagUpgrades] split for send: {it.m_dropPrefab?.name} {before} -> {pieces} x {max} + {it.m_stack}{(it.m_stack > max ? " (kept in the bag: no free cell to split into)" : "")}");
            }
            return send;
        }

        /// <summary>
        /// Per-upgrade saved state lives in the SLOTTED UPGRADE ITEM's m_customData under "BagUp.&lt;Id&gt;.&lt;name&gt;", so it travels with
        /// the bag and survives a relog. After changing it, call this so the bag saves the slot.
        /// </summary>
        public static void SaveUpgradeState(ItemDrop.ItemData bag, string kind) => BackpackSpecialSlots.Persist(bag, kind);

        /// <summary>Raised every frame for the LOCAL player only (dt = Time.deltaTime). Options throttle themselves.</summary>
        // 1.0.152 (Fire 11-05 perf pass, hot path Player.Update -> GetInvocationList, a new array every frame): the list is kept and rebuilt when a subscriber changes
        private static Action<Player, float> s_tick;
        private static Delegate[] s_tickList;
        public static event Action<Player, float> Tick { add { s_tick += value; s_tickList = null; } remove { s_tick -= value; s_tickList = null; } }

        /// <summary>A right-click on a slotted upgrade (VAInventory's rail calls it). Return true when the option handled it
        /// (e.g. opened its fuel pouch); otherwise the rail does its usual remove-with-confirm.</summary>
        public static event Func<ItemDrop.ItemData, string, ItemDrop.ItemData, bool> UpgradeRightClick;

        /// <summary>An item dropped onto a slotted upgrade (e.g. fuel). Return true when the option took it (part or all; the rest stays on
        /// the cursor); otherwise the rail refuses it, or asks to swap when it's another upgrade.</summary>
        public static event Func<ItemDrop.ItemData, string, ItemDrop.ItemData, Inventory, bool> UpgradeDropOnto;

        /// <summary>A short badge drawn over the slotted upgrade's rail cell (e.g. "Coal 12", or "" for none), and whether it is a
        /// warning (out of fuel: red, greyed icon). First non-null answer wins.</summary>
        public static event Func<ItemDrop.ItemData, (string text, bool warn)?> UpgradeBadge;

        /// <summary>Just BEFORE the rail destroys a slotted upgrade (removed, or swapped for another): give back what it holds
        /// (e.g. pouch fuel to the bag, else at the feet). Never destroy the player's items with the upgrade.</summary>
        public static event Action<Player, ItemDrop.ItemData, string, ItemDrop.ItemData> UpgradeRemoving;

        /// <summary>A line appended to the remove / swap confirm (e.g. "The fuel in its pouch (Coal 12) goes back to your bag ...");
        /// null when there's nothing to say. All non-null answers are shown.</summary>
        public static event Func<ItemDrop.ItemData, string> UpgradeRemoveWarning;

        internal static void RaiseRemoving(ItemDrop.ItemData bag, string kind, ItemDrop.ItemData upgrade)
        {
            if (UpgradeRemoving == null) return;
            foreach (Action<Player, ItemDrop.ItemData, string, ItemDrop.ItemData> h in UpgradeRemoving.GetInvocationList())
                try { h(Player.m_localPlayer, bag, kind, upgrade); } catch (Exception e) { Log.LogWarning($"{MyMod.PluginName}: an UpgradeRemoving handler threw {e.Message}"); }
        }

        internal static string RaiseRemoveWarning(ItemDrop.ItemData upgrade)
        {
            if (UpgradeRemoveWarning == null) return null;
            var lines = new List<string>();
            foreach (Func<ItemDrop.ItemData, string> h in UpgradeRemoveWarning.GetInvocationList())
                try { string s = h(upgrade); if (!string.IsNullOrEmpty(s)) lines.Add(s); } catch { }
            return lines.Count == 0 ? null : string.Join("\n", lines.ToArray());
        }

        internal static bool RaiseRightClick(ItemDrop.ItemData bag, string kind, ItemDrop.ItemData upgrade)
        {
            if (UpgradeRightClick == null) return false;
            foreach (Func<ItemDrop.ItemData, string, ItemDrop.ItemData, bool> h in UpgradeRightClick.GetInvocationList())
                try { if (h(bag, kind, upgrade)) return true; } catch (Exception e) { Log.LogWarning($"{MyMod.PluginName}: an UpgradeRightClick handler threw {e.Message}"); }
            return false;
        }

        internal static bool RaiseDropOnto(ItemDrop.ItemData bag, string kind, ItemDrop.ItemData upgrade, ItemDrop.ItemData dropped, Inventory from)
        {
            if (UpgradeDropOnto == null) return false;
            foreach (Func<ItemDrop.ItemData, string, ItemDrop.ItemData, Inventory, bool> h in UpgradeDropOnto.GetInvocationList())
                try { if (h(bag, kind, dropped, from)) return true; } catch (Exception e) { Log.LogWarning($"{MyMod.PluginName}: an UpgradeDropOnto handler threw {e.Message}"); }
            return false;
        }

        internal static (string text, bool warn)? RaiseBadge(ItemDrop.ItemData upgrade)
        {
            if (UpgradeBadge == null) return null;
            foreach (Func<ItemDrop.ItemData, (string text, bool warn)?> h in UpgradeBadge.GetInvocationList())
                try { var r = h(upgrade); if (r != null) return r; } catch { }
            return null;
        }

        [HarmonyPatch(typeof(Player), "Update")]
        private static class Player_Update_Tick
        {
            private static void Postfix(Player __instance)
            {
                if (s_tick == null || __instance != Player.m_localPlayer) return;
                if (s_tickList == null || !ConfigManager.Instance.configPerfBagTickList.Value) s_tickList = s_tick.GetInvocationList();
                foreach (Action<Player, float> h in s_tickList)
                    try { h(__instance, Time.deltaTime); } catch (Exception e) { Log.LogWarning($"{MyMod.PluginName}: a bag upgrade Tick handler threw {e.Message}"); }
            }
        }

        // ── items and recipes (runtime clones of a vanilla look-alike, no new art yet) ────────────────────

        private static void MakeItems(ObjectDB db)
        {
            if (_holder == null) { _holder = new GameObject("VABackpacks_BagUpgrades"); _holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(_holder); }
            var byHash = (Dictionary<int, GameObject>)AccessTools.Field(typeof(ObjectDB), "m_itemByHash").GetValue(db);
            foreach (IBagUpgradeOption o in Options)
                for (int tier = 1; tier <= TierCount; tier++)
                {
                    string name = PrefabName(o.Id, tier);
                    if (!Clones.TryGetValue(name, out GameObject clone) || clone == null)
                    {
                        GameObject donor = db.GetItemPrefab(o.Donor);
                        if (donor == null) { MyMod.Warn(db, $"{MyMod.PluginName}: bag upgrades: no '{o.Donor}' to make {name} from"); continue; }
                        clone = UnityEngine.Object.Instantiate(donor, _holder.transform, false);
                        clone.name = name;
                        var drop = clone.GetComponent<ItemDrop>();
                        drop.m_itemData.m_dropPrefab = clone;
                        var sh = drop.m_itemData.m_shared;
                        sh.m_name = $"{o.Name} Bag Upgrade {TierNumerals[tier - 1]}";
                        sh.m_itemType = ItemDrop.ItemData.ItemType.Misc;
                        sh.m_maxStackSize = 1;
                        sh.m_weight = UpgradeWeight;
                        sh.m_teleportable = true;
                        Clones[name] = clone;
                    }
                    // the description is refreshed every time: it can name config values and other items that load later
                    string slot = o.Slot == BackpackSpecialSlots.SlotTool ? "tool" : o.Slot == BackpackSpecialSlots.SlotResources ? "resources" : "bypass";
                    string effect;
                    try { effect = o.Describe(tier); } catch (Exception e) { effect = $"({o.Id} tier {tier})"; Log.LogWarning($"{MyMod.PluginName}: bag upgrade {o.Id}: Describe threw {e.Message}"); }
                    clone.GetComponent<ItemDrop>().m_itemData.m_shared.m_description =
                        $"{effect}\nFits a bag's <color=#{SlotColorHex(o.Slot)}>{slot} slot</color>. Once slotted it belongs to that bag: swapping or removing it destroys it.";
                    if (db.GetItemPrefab(name) == null) { db.m_items.Add(clone); byHash[name.GetStableHashCode()] = clone; }
                }
        }

        /// <summary>The slot's own icon colour as hex, so the tooltip names the slot in the same colour as its rail cell.</summary>
        private static string SlotColorHex(string kind)
            => BackpackSpecialSlots.Colors.TryGetValue(kind, out Color c) ? ColorUtility.ToHtmlStringRGB(c) : "FFFFFF";

        private static void AddToZNetScene(ZNetScene zs)
        {
            if (zs == null) return;
            var named = (Dictionary<int, GameObject>)AccessTools.Field(typeof(ZNetScene), "m_namedPrefabs").GetValue(zs);
            foreach (KeyValuePair<string, GameObject> kv in Clones)
                if (kv.Value != null && zs.GetPrefab(kv.Key) == null) { zs.m_prefabs.Add(kv.Value); named[kv.Key.GetStableHashCode()] = kv.Value; }
        }

        private static void AddRecipes(ObjectDB db)
        {
            Made.RemoveAll(r => r == null);
            foreach (IBagUpgradeOption o in Options)
                for (int tier = 1; tier <= TierCount; tier++)
                {
                    var item = db.GetItemPrefab(PrefabName(o.Id, tier))?.GetComponent<ItemDrop>();
                    if (item == null) continue;
                    Recipe existing = db.m_recipes.FirstOrDefault(r => r != null && r.m_item == item);
                    if (existing != null) { if (!Made.Contains(existing)) Made.Add(existing); continue; }
                    string text = o.Recipes != null && o.Recipes.Length >= tier ? o.Recipes[tier - 1] : null;
                    if (string.IsNullOrEmpty(text)) { Log.LogWarning($"{MyMod.PluginName}: bag upgrades: {item.name} has no recipe text"); continue; }
                    Recipe recipe = BackpackCraftingAndLoot.ParseRecipe(db, item, text, out string why);
                    if (recipe == null) { MyMod.Warn(db, $"{MyMod.PluginName}: bag upgrades: no recipe for {item.name}: {why}"); continue; }
                    db.m_recipes.Add(recipe);
                    Made.Add(recipe);
                }
            ApplyEnabled();
        }

        private static void ApplyEnabled()
        {
            bool on = Enabled != null && Enabled.Value;
            foreach (Recipe r in Made) if (r != null) r.m_enabled = on;
            Log.LogDebug($"{MyMod.PluginName}: bag upgrades {(on ? "ON" : "OFF")}: {Made.Count(r => r != null)} recipe(s) {(on ? "craftable" : "hidden")}, {Clones.Count} upgrade item(s)");
        }

        [HarmonyPatch(typeof(ObjectDB), "Awake")]
        private static class ObjectDB_Awake
        {
            [HarmonyPriority(Priority.VeryLow)]
            private static void Postfix(ObjectDB __instance)
            {
                if (__instance == null || __instance.m_items == null || __instance.m_items.Count == 0) return;
                try { MakeItems(__instance); AddRecipes(__instance); }
                catch (Exception e) { Log.LogWarning($"{MyMod.PluginName}: bag upgrades setup failed: {e}"); }
            }
        }

        [HarmonyPatch(typeof(ObjectDB), "CopyOtherDB")]
        private static class ObjectDB_CopyOtherDB
        {
            [HarmonyPriority(Priority.VeryLow)]
            private static void Postfix(ObjectDB __instance)
            {
                try { MakeItems(__instance); } catch (Exception e) { Log.LogWarning($"{MyMod.PluginName}: bag upgrades (menu DB) failed: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class ZNetScene_Awake
        {
            [HarmonyPriority(Priority.VeryLow)]
            private static void Postfix(ZNetScene __instance)
            {
                if (ObjectDB.instance != null) { try { MakeItems(ObjectDB.instance); } catch { } }
                try { AddToZNetScene(__instance); } catch (Exception e) { Log.LogWarning($"{MyMod.PluginName}: bag upgrades (ZNetScene) failed: {e.Message}"); }   // 1.0.132 (load/save audit M12)
            }
        }
    }
}
