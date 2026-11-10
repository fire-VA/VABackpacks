// Workbench (tool slot): the bag counts as its tier's crafting stations while no real station is in use. The station patches are in
// BagWorkbench.cs; this file registers the option.

using BepInEx.Configuration;

namespace VerdantsAscent.Managers
{
    public sealed class WorkbenchOption : IBagUpgradeOption
    {
        public string Id => BagWorkbench.UpgradeId;
        public string Name => "Workbench";
        public string Slot => BackpackSpecialSlots.SlotTool;
        public string Donor => "Hammer";
        public string[] Recipes => new[]
        {
            "forge,1,Bronze:5,FineWood:20,DeerHide:5",
            "forge,3,Iron:10,WolfPelt:5,Chain:2",
            "forge,5,BlackMetal:10,YggdrasilWood:10,LinenThread:5",
        };

        public string Describe(int tier) => $"The bag counts as a crafting station when none is in range, for crafting from your inventory and for building: {BagWorkbench.DescribeTier(tier)}.";

        public void Bind(ConfigFile cfg, ConfigSync sync) => BagWorkbench.Bind(cfg, sync);
    }
}
