// VoidPicker: the Void upgrade's picker (the shared BagPickerWindow). Sets VoidOption.OpenPicker once ZNet is up, on a client with
// a Core that has KitWindow; otherwise OpenPicker stays null and the right-click keeps its clear-the-list popup.

using System.Collections.Generic;
using HarmonyLib;

namespace VerdantsAscent.Managers
{
    /// <summary>Sets VoidOption.OpenPicker once ZNet is up. Idempotent: ZNet.Awake runs per world load.</summary>
    [HarmonyPatch(typeof(ZNet), "Awake")]
    internal static class VoidPickerInit
    {
        [HarmonyPostfix]
        private static void Postfix() => VoidPicker.Install();
    }

    public static class VoidPicker
    {
        private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks." + VoidOption.UpgradeId);
        private static bool s_installed, s_noCoreLogged;

        internal static readonly BagPickerKind Kind = new BagPickerKind
        {
            Index = 1,
            Id = VoidOption.UpgradeId,
            Title = "Void",
            Section = "DESTROY ON PICK-UP",
            KnownHeader = "ITEMS YOU KNOW",
            EmptyText = "Pick an item…",
            NoneKnown = "No items you know yet",
            FilledSub = "destroyed when picked up",
            Note = "Items on this list are destroyed as you pick them up. What you already carry is kept.",
            Warning = "Destroyed items are gone for good.",
            Noun = "items",
            SlotKind = BackpackSpecialSlots.SlotBypass,
            ShowBagCount = false,
            Accepts = VoidOption.IsItem,
            Get = VoidOption.GetFilter,
            Set = (bag, upgrade, list, tier) => VoidOption.SetFilter(bag, upgrade, list, tier),
            CountFor = VoidOption.EntriesFor,
            Known = KnownItems,
            Log = Log,
        };

        internal static void Install()
        {
            if (!BagPickerWindow.CanInstall(Kind, ref s_noCoreLogged)) return;
            VoidOption.OpenPicker = OnOpen;   // the same delegate each world load
            if (!s_installed) { s_installed = true; Log.LogDebug("[Void] picker subscribed to OpenPicker (the clear-the-list popup is off while subscribed)"); }
        }

        private static void OnOpen(ItemDrop.ItemData bag, ItemDrop.ItemData upgrade, int tier) => BagPickerWindow.Open(Kind, bag, upgrade, tier);

        /// <summary>The item prefabs this player knows that can go on a Void list.</summary>
        public static List<string> KnownItems(Player player) => FeedingPicker.Known(player, VoidOption.IsItem);
    }
}
