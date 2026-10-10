using System.Runtime.CompilerServices;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // VABackpacks' sections for the shared FiresCore help panel. Core is a SOFT
    // dependency — TryRegister checks the Chainloader before touching any
    // FiresCore type, and DoRegister is NoInlining so the JIT never resolves
    // FiresCore when Core is absent.
    internal static class VaBackpacksHelpContent
    {
        private const string ModId = "VABackpacks";
        private static bool _registered;

        public static void TryRegister()
        {
            if (_registered || Application.isBatchMode) return;
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.Fire.FiresUnifiedCore")) return;
            try { DoRegister(); _registered = true; }
            catch (System.Exception ex) { Debug.LogWarning($"[VABackpacks] Help registration failed: {ex.Message}"); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DoRegister()
        {
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Backpacks", 0, BuildBackpacks);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Camp Deployment", 1, BuildCamp);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Config Reference", 2, BuildConfig, adminOnly: true);
        }

        private static void BuildBackpacks(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("Backpacks");
            w.Paragraph("Real backpacks with real storage: the Small Bag, Day Bag, and Explorer " +
                "Bag wear on your shoulder, and the Hobo Stick and Fires Wizard's Staff carry " +
                "their load in hand. Craft them at the workbench, or find one in chests: the Small Bag " +
                "in the Meadows, the Day Bag in the Black Forest, the Explorer Bag in the Swamp.");
            w.Divider();

            w.SubHeader("Using a Bag");
            w.Bullet("Equip it, then press B (Backpacks.OpenHotkey) to open its storage.");
            w.Bullet("Storage by bag: Small 5x2, Day 6x3, Explorer 7x4, Hobo Stick and Staff 5x3 " +
                "(server-tunable per bag type).");
            w.Bullet("The worn bag shows on your back, cape or not.");
            w.Bullet("Wear multiple bags (shoulder bag + Hobo Stick in hand) and each shows its " +
                "own storage panel when VAInventory is installed.");
            w.Divider();

            w.SubHeader("The Contents Travel WITH the Bag");
            w.Paragraph("A bag's contents are stored inside the bag item itself. Hand the bag to " +
                "a friend, stash it in a chest, drop it on the ground — everything inside goes " +
                "along. On death, the bag lands in your tombstone as one item, contents intact.");
            w.Divider();

            w.SubHeader("Bag Perks (server-configured per bag)");
            w.Bullet("Weight reduction for carried contents (standalone mode)");
            w.Bullet("Cold resistance / wet resistance — a good bag keeps you warm and dry");
            w.Bullet("Feather fall, extra carry strength");
            w.Divider();

            w.SubHeader("Special Slots (quality 2 and up)");
            w.Bullet("Small Bag: a tool slot. Day Bag: tool + resources. Explorer, Hobo Stick, Staff: " +
                "tool + resources + bypass.");
            w.Bullet("Resources in the resources slot weigh a quarter.");
            w.Bullet("The bypass slot carries one non-teleportable item through portals.");
            w.Divider();

            w.SubHeader("Rules");
            w.Bullet("No backpack inside a backpack.");
            w.Bullet("A bag can't be quick-transferred into itself.");
        }

        private static void BuildCamp(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("Camp Deployment");
            w.Paragraph("Any backpack can BECOME your camp. Drop the bag on the ground, hold the " +
                "gesture, and it unpacks into a working camp site.");
            w.Divider();

            w.SubHeader("Deploying");
            w.Bullet("Drop a backpack, look at it, and HOLD E for 3 seconds (holding Shift+B works too).");
            w.Bullet("The camp unpacks: a campfire, the backpack itself standing open as your " +
                "chest (with everything inside), a workbench, bed, chair, woodpile, and cooking " +
                "station — arranged around the fire, terrain smoothed underneath.");
            w.Bullet("Hobo Stick / Wizard's Staff: swing with MIDDLE MOUSE to slam-deploy the " +
                "camp right at the point of impact.");
            w.Bullet("The woodpile grants a small daily wood ration.");
            w.Divider();

            w.SubHeader("Packing Up");
            w.Bullet("Look at your camp's chest and HOLD Shift+B again — the camp folds back " +
                "into the bag, terrain restored, nothing left behind. The bag goes back into " +
                "your inventory (or at your feet if it's full).");
            w.Bullet("While camped, the bag leans against the chest so you can spot it.");
            w.Bullet("One camp per character.");
            w.Bullet("Guildmates can use your camp chest when FiresGuilds is installed.");
        }

        private static void BuildConfig(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Config Reference");
            w.SubHeader("Per-Bag Sections (Backpack.<prefab>)");
            w.Paragraph("SmallBag_va, DayBag_va, ExplorerBag_va, HoboStick, StaffFiresWizard — " +
                "each with:");
            w.Bullet("Rows / Columns — storage size");
            w.Bullet("WeightReductionMultiplier — contents weight factor (standalone mode only; " +
                "with VAInventory's panels, contents weigh normally)");
            w.Bullet("Defaults: Small 5x2, Day 6x3, Explorer 7x4, Hobo Stick / Staff 5x3");
            w.Bullet("ColdResistance / WetResistance / FeatherFall / StrengthBonus — perks");
            w.Bullet("CampBlueprint (+YOffset) — a PlanBuild blueprint replaces the procedural " +
                "camp layout for this bag");
            w.Bullet("SpeedModifier and Teleportable are bound but not yet implemented — " +
                "changing them does nothing in this build.");
            w.AdminDivider();

            w.SubHeader("Camp (Backpacks.Camp)");
            w.Bullet("GestureKey + HoldDurationSeconds — the deploy gesture (Shift is always " +
                "required)");
            w.Bullet("CampfirePrefab / WorkbenchPrefab / ContainerPrefab / BedPrefab / " +
                "ChairPrefab / WoodpilePrefab / CookingStationPrefab — swap any camp piece " +
                "(comma-separated fallback chains; blank skips it)");
            w.Bullet("TerrainModifierPrefab — the ground patch under camp (restored on pack-up)");
            w.Bullet("Backpacks.Camp.Blueprint.* — which procedural pieces still spawn alongside " +
                "a blueprint camp");
            w.AdminDivider();

            w.Paragraph("Sync note: recipes, upgrades, chest loot, the EnableRecipes lock and " +
                "VerboseLogging are server-synced — bag capacities, perks, and camp layout are read " +
                "from each client's config, so keep the modpack's cfg canonical if you want uniform bags.");
        }
    }
}
