// FeedingPicker: the Feeding upgrade's picker (the shared BagPickerWindow). Sets FeedingOption.OpenPicker once ZNet is up, on a client
// with a Core that has KitWindow; otherwise OpenPicker stays null and the right-click keeps its console hint.

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    /// <summary>Sets FeedingOption.OpenPicker once ZNet is up. Idempotent: ZNet.Awake runs per world load.</summary>
    [HarmonyPatch(typeof(ZNet), "Awake")]
    internal static class FeedingPickerInit
    {
        [HarmonyPostfix]
        private static void Postfix() => FeedingPicker.Install();
    }

    public static class FeedingPicker
    {
        private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks." + FeedingOption.UpgradeId);
        private static bool s_installed, s_noCoreLogged;

        internal static readonly BagPickerKind Kind = new BagPickerKind
        {
            Index = 0,
            Id = FeedingOption.UpgradeId,
            Title = "Feedbag",
            Section = "FEED ME (IN ORDER)",
            KnownHeader = "FOODS YOU KNOW",
            EmptyText = "Pick a food…",
            NoneKnown = "No foods you know yet",
            Note = "Fills an empty food slot from this bag, never in combat.",
            Warning = null,
            Noun = "foods",
            SlotKind = BackpackSpecialSlots.SlotResources,
            ShowBagCount = true,
            Accepts = FeedingOption.IsFood,
            Get = FeedingOption.GetFoods,
            Set = (bag, upgrade, list, tier) => FeedingOption.SetFoods(bag, upgrade, list, tier),
            CountFor = FeedingOption.ChoicesFor,
            Known = KnownFoods,
            Log = Log,
        };

        internal static void Install()
        {
            if (!BagPickerWindow.CanInstall(Kind, ref s_noCoreLogged)) return;
            FeedingOption.OpenPicker = OnOpen;   // the same delegate each world load
            if (!s_installed) { s_installed = true; Log.LogDebug("[Feeding] picker subscribed to OpenPicker (the console hint is off while subscribed)"); }
        }

        private static void OnOpen(ItemDrop.ItemData bag, ItemDrop.ItemData upgrade, int tier) => BagPickerWindow.Open(Kind, bag, upgrade, tier);

        /// <summary>The food prefabs this player knows (Player.m_knownMaterial holds shared names such as "$item_cookedmeat").</summary>
        public static List<string> KnownFoods(Player player) => Known(player, FeedingOption.IsFood);

        internal static List<string> Known(Player player, Func<string, bool> accepts)
        {
            var list = new List<string>();
            if (player == null || ObjectDB.instance == null || player.m_knownMaterial == null) return list;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameObject go in ObjectDB.instance.m_items)
            {
                if (go == null || seen.Contains(go.name)) continue;
                string shared = go.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_name;
                if (string.IsNullOrEmpty(shared) || !player.m_knownMaterial.Contains(shared) || !accepts(go.name)) continue;
                seen.Add(go.name);
                list.Add(go.name);
            }
            return list;
        }
    }
}
