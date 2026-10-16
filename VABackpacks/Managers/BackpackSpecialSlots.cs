// BackpackSpecialSlots — 1.0.20 (Fire 10-07): upgraded bags (quality 2+) get special slots on a rail right of the grid.
//   Small = tool; Day = tool + resources; Explorer, HoboStick, StaffFiresWizard = tool + resources + bypass.
//   tool      : one weapon or tool, outside the grid.
//   resources : one material stack; its weight counts a quarter.
//   bypass    : any one stack; a non-teleportable item here still goes through portals (same rule as VAInventory's pocket).
// Each slot is a 1x1 Inventory kept per bag ItemData (same instance every read) and saved into the bag's
// m_customData["BackpackSpecial.<kind>"] on every change, so it survives drop / pickup / logout in both storage modes.
// Items here are not in any player inventory: their weight is added to the bag item, and tool / resources items
// still block portals like they would anywhere else.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public static class BackpackSpecialSlots
    {
        public const string SlotTool = "tool";
        public const string SlotResources = "resources";
        public const string SlotBypass = "bypass";
        public static readonly string[] Kinds = { SlotTool, SlotResources, SlotBypass };

        public const int UpgradedQuality = 2;
        public const float ResourceWeightFactor = 0.25f;
        private const string KeyPrefix = "BackpackSpecial.";

        private static readonly Dictionary<string, string[]> SlotsByPrefab = new Dictionary<string, string[]>
        {
            { "SmallBag_va",      new[] { SlotTool } },
            { "DayBag_va",        new[] { SlotTool, SlotResources } },
            { "ExplorerBag_va",   new[] { SlotTool, SlotResources, SlotBypass } },
            { "HoboStick",        new[] { SlotTool, SlotResources, SlotBypass } },
            { "StaffFiresWizard", new[] { SlotTool, SlotResources, SlotBypass } },
        };

        public static readonly Dictionary<string, Color> Colors = new Dictionary<string, Color>
        {
            { SlotTool,      new Color32(0xd9, 0xa3, 0x5b, 0xff) },
            { SlotResources, new Color32(0x8f, 0xbf, 0x6a, 0xff) },
            { SlotBypass,    new Color32(0x8a, 0xb8, 0xe6, 0xff) },
        };

        public static readonly Dictionary<string, string> Tooltips = new Dictionary<string, string>
        {
            { SlotTool,      "Tool slot: Holds one weapon or tool. It doesn't take a grid slot." },
            { SlotResources, "Resources slot: Holds one resource stack. Whatever sits here weighs a quarter as much." },
            { SlotBypass,    "Teleport bypass: One non-teleportable item here can pass through portals." },
        };

        private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks.Slots");
        private static readonly string[] None = new string[0];
        private static int _version;
        public static int Version => _version;

        private sealed class SlotSet
        {
            public readonly Dictionary<string, Inventory> Inv = new Dictionary<string, Inventory>();
        }
        private static readonly ConditionalWeakTable<ItemDrop.ItemData, SlotSet> Sets = new ConditionalWeakTable<ItemDrop.ItemData, SlotSet>();

        // ── which slots ────────────────────────────────────────────────────────────────────────────

        public static string[] SlotsFor(string prefabName)
            => prefabName != null && SlotsByPrefab.TryGetValue(prefabName, out string[] s) ? s : None;

        /// <summary>The slots this bag has right now: none below quality 2.</summary>
        public static string[] GetSpecialSlots(ItemDrop.ItemData bag)
        {
            if (bag?.m_dropPrefab == null || bag.m_quality < UpgradedQuality || !BackpackManager.IsBackpack(bag)) return None;
            return (string[])SlotsFor(bag.m_dropPrefab.name).Clone();
        }

        public static bool HasSlot(ItemDrop.ItemData bag, string kind)
            => bag?.m_dropPrefab != null && bag.m_quality >= UpgradedQuality && Array.IndexOf(SlotsFor(bag.m_dropPrefab.name), kind) >= 0;

        // ── fit rule ───────────────────────────────────────────────────────────────────────────────

        public static bool Accepts(ItemDrop.ItemData bag, string kind, ItemDrop.ItemData item)
        {
            if (item == null || !HasSlot(bag, kind) || BackpackManager.IsBackpack(item) || item == bag) return false;
            var t = item.m_shared.m_itemType;
            switch (kind)
            {
                case SlotTool:
                    return item.IsWeapon() || t == ItemDrop.ItemData.ItemType.Tool;
                case SlotResources:
                    return t == ItemDrop.ItemData.ItemType.Material;
                case SlotBypass:
                    return true;
            }
            return false;
        }

        // ── storage ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The 1x1 inventory behind a slot (created and loaded on first use), or null when the bag has no such slot kind.</summary>
        internal static Inventory GetInventory(ItemDrop.ItemData bag, string kind)
        {
            if (bag?.m_dropPrefab == null || Array.IndexOf(SlotsFor(bag.m_dropPrefab.name), kind) < 0) return null;
            SlotSet set = Sets.GetOrCreateValue(bag);
            if (set.Inv.TryGetValue(kind, out Inventory inv)) return inv;
            inv = new Inventory("$vab_slot_" + kind, null, 1, 1);
            if (bag.m_customData != null && bag.m_customData.TryGetValue(KeyPrefix + kind, out string data) && !string.IsNullOrEmpty(data))
            {
                try { inv.Load(new ZPackage(data)); }
                catch (Exception ex) { Log.LogWarning($"{MyMod.PluginName}: special slot '{kind}' on {bag.m_dropPrefab.name} did not load: {ex.Message}"); }
            }
            ItemDrop.ItemData owner = bag;
            string k = kind;
            inv.m_onChanged = (Action)Delegate.Combine(inv.m_onChanged, new Action(() => Save(owner, k, inv)));
            set.Inv[kind] = inv;
            return inv;
        }

        private static void Save(ItemDrop.ItemData bag, string kind, Inventory inv)
        {
            if (bag == null || inv == null) return;
            if (inv.NrOfItems() == 0) bag.m_customData.Remove(KeyPrefix + kind);
            else
            {
                var pkg = new ZPackage();
                inv.Save(pkg);
                bag.m_customData[KeyPrefix + kind] = pkg.GetBase64();
            }
            _version++;
            RefreshHolderWeight(bag);
        }

        // 1.0.26 (round 12: "Wood x20 in the rail added 1.50 weight, need 10.00"): vanilla caches an inventory's total weight
        // and refreshes it only when that inventory changes. A slot item lives in the bag's customData, so a slot write
        // changed the bag's weight (Weight_Patch) without refreshing the inventory that holds the bag: VAInventory takes the
        // item off the grid (refresh) and THEN writes the slot (none), so the total kept the old number.
        private static readonly MethodInfo UpdateTotalWeightMethod = AccessTools.Method(typeof(Inventory), "UpdateTotalWeight");

        private static void RefreshHolderWeight(ItemDrop.ItemData bag)
        {
            Inventory holder = Player.m_localPlayer != null ? Player.m_localPlayer.GetInventory() : null;
            if (holder == null || UpdateTotalWeightMethod == null || !holder.ContainsItem(bag)) return;
            try { UpdateTotalWeightMethod.Invoke(holder, null); }
            catch (Exception ex) { Log.LogWarning($"{MyMod.PluginName}: weight refresh after a special-slot change failed: {ex.Message}"); }
        }

        /// <summary>True when the bag carries anything in a special slot (cheap: no inventory is created).</summary>
        private static bool AnyStored(ItemDrop.ItemData bag)
        {
            if (bag?.m_customData == null || bag.m_customData.Count == 0) return false;
            foreach (string kind in Kinds)
                if (bag.m_customData.ContainsKey(KeyPrefix + kind)) return true;
            return false;
        }

        public static ItemDrop.ItemData Read(ItemDrop.ItemData bag, string kind)
        {
            if (!AnyStored(bag) && (bag == null || !Sets.TryGetValue(bag, out _))) return null;
            return GetInventory(bag, kind)?.GetItemAt(0, 0);
        }

        public static bool Write(ItemDrop.ItemData bag, string kind, ItemDrop.ItemData item)
        {
            Inventory inv = GetInventory(bag, kind);
            if (inv == null) return false;
            if (item != null && !Accepts(bag, kind, item)) return false;
            inv.RemoveAll();
            if (item == null) { Save(bag, kind, inv); return true; }
            if (inv.AddItem(item, new Vector2i(0, 0))) return true;
            Save(bag, kind, inv);
            return false;
        }

        /// <summary>Copies every special slot from one bag to another (the vanilla upgrade makes a fresh ItemData).</summary>
        internal static void CarryOver(ItemDrop.ItemData from, ItemDrop.ItemData to)
        {
            if (from?.m_customData == null || to == null) return;
            foreach (KeyValuePair<string, string> kv in from.m_customData)
                if (!to.m_customData.ContainsKey(kv.Key)) to.m_customData[kv.Key] = kv.Value;
            _version++;
        }

        // ── icons ──────────────────────────────────────────────────────────────────────────────────

        private static readonly Dictionary<string, Sprite> Icons = new Dictionary<string, Sprite>();

        public static Sprite GetIcon(string kind)
        {
            if (kind == null || Array.IndexOf(Kinds, kind) < 0) return null;
            if (Icons.TryGetValue(kind, out Sprite s)) return s;
            s = null;
            try
            {
                Assembly asm = typeof(BackpackSpecialSlots).Assembly;
                string res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("slot_" + kind + ".png", StringComparison.OrdinalIgnoreCase));
                if (res != null)
                {
                    using (Stream st = asm.GetManifestResourceStream(res))
                    using (var ms = new MemoryStream())
                    {
                        st.CopyTo(ms);
                        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        // ImageConversion lives in a module built against a newer netstandard than this project; call it by name.
                        MethodInfo load = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule")
                            ?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });
                        if (load != null && (bool)load.Invoke(null, new object[] { tex, ms.ToArray() }))
                        {
                            tex.name = "vab_slot_" + kind;
                            s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                            s.name = tex.name;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.LogWarning($"{MyMod.PluginName}: slot icon '{kind}' did not load: {ex.Message}"); }
            Icons[kind] = s;
            return s;
        }

        // ── weight / teleport / upgrade patches ────────────────────────────────────────────────────

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetWeight))]
        public static class Weight_Patch
        {
            public static void Postfix(ItemDrop.ItemData __instance, ref float __result)
            {
                if (!AnyStored(__instance) || !BackpackManager.IsBackpack(__instance)) return;
                foreach (string kind in SlotsFor(__instance.m_dropPrefab.name))
                {
                    ItemDrop.ItemData it = Read(__instance, kind);
                    if (it == null) continue;
                    float w = it.GetWeight();
                    __result += kind == SlotResources ? w * ResourceWeightFactor : w;
                }
            }
        }

        private static bool Blocks(ItemDrop.ItemData it, bool all)
            => it != null && (it.m_shared.m_toolTier >= 1000 || (!all && !it.m_shared.m_teleportable));

        // Saved bag grids, parsed once per distinct string: [0] = any non-teleportable, [1] = any toolTier >= 1000.
        private static readonly Dictionary<string, bool[]> SavedCache = new Dictionary<string, bool[]>();

        private static bool SavedBlocks(string saved, bool all)
        {
            if (string.IsNullOrEmpty(saved)) return false;
            if (!SavedCache.TryGetValue(saved, out bool[] f))
            {
                f = new bool[2];
                try
                {
                    var inv = new Inventory("vab_saved", null, 32, 32);
                    inv.Load(new ZPackage(saved));
                    foreach (ItemDrop.ItemData it in inv.GetAllItems())
                    {
                        if (!it.m_shared.m_teleportable) f[0] = true;
                        if (it.m_shared.m_toolTier >= 1000) f[1] = true;
                    }
                }
                catch (Exception ex) { Log.LogWarning($"{MyMod.PluginName}: saved bag contents did not parse: {ex.Message}"); }
                if (SavedCache.Count > 256) SavedCache.Clear();
                SavedCache[saved] = f;
            }
            return f[1] || (!all && f[0]);
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.IsTeleportable))]
        public static class Teleport_Patch
        {
            public static void Postfix(Inventory __instance, bool allowAllItems, ref bool __result)
            {
                if (!__result) return;
                Player p = Player.m_localPlayer;
                if (p == null || __instance != p.GetInventory()) return;
                bool all = allowAllItems || (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.TeleportAll));

                // Fire 10-07 (Q1 a): bag contents block portals in both modes. Legacy mode: the worn bag's live container.
                Inventory live = BackpackManager.LegacyLiveInventory(p, out ItemDrop.ItemData liveOwner);
                if (live != null)
                    foreach (ItemDrop.ItemData it in live.GetAllItems())
                        if (Blocks(it, all)) { __result = false; return; }
                // Any carried bag that isn't worn: its saved grid (worn bags are live in the container or, with VAInventory,
                // in the player inventory, which vanilla already checked).
                IReadOnlyList<ItemDrop.ItemData> worn = BackpackApi.GetEquippedBackpacks(p);
                foreach (ItemDrop.ItemData bag in __instance.GetAllItems())
                {
                    if (bag == liveOwner || !BackpackManager.IsBackpack(bag) || (worn != null && worn.Contains(bag))) continue;
                    if (bag.m_customData != null && bag.m_customData.TryGetValue("BackpackInventory", out string saved) && SavedBlocks(saved, all))
                    { __result = false; return; }
                }

                foreach (ItemDrop.ItemData bag in __instance.GetAllItems())
                {
                    if (!AnyStored(bag) || !BackpackManager.IsBackpack(bag)) continue;
                    foreach (string kind in SlotsFor(bag.m_dropPrefab.name))
                    {
                        ItemDrop.ItemData it = Read(bag, kind);
                        if (it == null) continue;
                        if (it.m_shared.m_toolTier >= 1000 || (!all && kind != SlotBypass && !it.m_shared.m_teleportable))
                        {
                            __result = false;
                            return;
                        }
                    }
                }
            }
        }

        // Vanilla upgrades by removing the old item and adding a fresh one, which drops m_customData — that is where
        // the bag's grid contents and special slots live. Copy it onto the new bag.
        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        public static class Upgrade_Patch
        {
            public static void Prefix(ItemDrop.ItemData ___m_craftUpgradeItem, out ItemDrop.ItemData __state)
            {
                __state = ___m_craftUpgradeItem != null && BackpackManager.IsBackpack(___m_craftUpgradeItem) ? ___m_craftUpgradeItem : null;
            }

            public static void Postfix(Player player, ItemDrop.ItemData __state)
            {
                if (__state == null || player == null) return;
                Inventory inv = player.GetInventory();
                if (inv.ContainsItem(__state)) return;   // not crafted (missing items etc.)
                ItemDrop.ItemData fresh = inv.GetItemAt(__state.m_gridPos.x, __state.m_gridPos.y);
                if (fresh == null || fresh.m_dropPrefab == null || fresh.m_dropPrefab.name != __state.m_dropPrefab.name || fresh.m_quality <= __state.m_quality)
                    fresh = inv.GetAllItems().FirstOrDefault(i => i != null && i.m_dropPrefab != null && i.m_dropPrefab.name == __state.m_dropPrefab.name
                                                                  && i.m_quality == __state.m_quality + 1 && !AnyStored(i) && !i.m_customData.ContainsKey("BackpackInventory"));
                if (fresh == null) { Log.LogWarning($"{MyMod.PluginName}: upgraded {__state.m_dropPrefab.name} not found; contents stay on the old item"); return; }
                CarryOver(__state, fresh);
                Log.LogDebug($"{MyMod.PluginName}: {__state.m_dropPrefab.name} upgraded to quality {fresh.m_quality}; contents carried over ({__state.m_customData.Count} key(s))");
            }
        }
    }
}
