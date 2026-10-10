// BackpackConfig — per-prefab gameplay + storage profile.
//
// Extracted from BackpackManager so other mods (notably VAInventory) can
// reference the type by name when reading the config off an equipped
// backpack. Lives in the same namespace as BackpackManager, so existing
// BackpackManager code keeps using `BackpackConfig` as before.

namespace VerdantsAscent.Managers
{
    public class BackpackConfig
    {
        // Storage dimensions — VAInventory uses these to size the
        // dynamic equipment-panel expansion block.
        public int Rows { get; set; } = 4;
        public int Columns { get; set; } = 6;

        // Carried-weight reduction (1.0 = no change, 0.5 = half weight, etc.).
        // Currently applied in BackpackManager.GetWeight_Patch on the
        // backpack ItemData itself.
        public float WeightReduction { get; set; } = 0.5f;

        // Status-effect modifiers — applied in BackpackManager.ApplyEffects
        // on equip/unequip. These are gameplay (not storage), and stay in
        // BackpackManager regardless of which storage path is active.
        public bool  ColdResistance { get; set; } = false;
        public bool  WetResistance  { get; set; } = false;
        public bool  Teleportable   { get; set; } = true;
        public bool  FeatherFall    { get; set; } = false;
        public float SpeedModifier  { get; set; } = 0f;
        public bool  StrengthBonus  { get; set; } = false;
    }
}
