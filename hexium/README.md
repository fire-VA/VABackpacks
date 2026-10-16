# VABackpacks

Real backpacks with real storage for Valheim: five bags, workbench upgrades with special slots, and bags that
unpack into camps. Part of the Fires mod family.

## What you get

- **Five bags.** The Small Bag, Day Bag and Explorer Bag wear on your shoulder and show on your back, cape or not.
  The Hobo Stick and the Fires Wizard's Staff carry their load in hand.
- **Crafted or found.** Each bag is made at the workbench. The shoulder bags can also turn up in location chests of
  their biome: the Small Bag in the Meadows, the Day Bag in the Black Forest, the Explorer Bag in the Swamp and
  Mountains.
- **The contents travel with the bag.** Everything is stored in the bag item itself. Give it to a friend, put it in
  a chest or drop it, and the contents go along. On death the bag lands in your tombstone with its contents intact.
- **Open it anywhere.** Equip a bag and press B to open its storage.
- **Workbench upgrades.** A quality 2 bag gets special slots beside its grid:
  - Small Bag: a tool slot (one weapon or tool).
  - Day Bag: tool + resources slot (one stack that weighs a quarter as much).
  - Explorer Bag, Hobo Stick, Staff: tool + resources + teleport bypass slot (one non-teleportable stack goes
    through portals).
  - Quality 3 and 4 make the bag's camp bigger.
- **Bags become camps.** Drop a bag, look at it and hold the Use key (E) for 3 seconds, or hold Shift+B. It unpacks
  into a camp: a campfire with a cooking grill, the bag standing open as your chest with everything inside, a
  workbench, bed, log bench and a wood pile that gives a small daily wood ration, on smoothed ground. Swing the Hobo
  Stick or Staff with the middle mouse button to slam-deploy a camp where it lands.
- **Pack it back up.** Look at the camp chest and hold Shift+B again. The camp folds back into the bag and the
  ground is restored. The bag goes back into your inventory, or at your feet if it is full. Your own buildings inside
  the camp radius pack up with it and come back on the next deploy. One camp per character.
- **Bag perks**, set per bag in the config: cold and wet resistance, feather fall, extra carry strength. All off by
  default.

Rules: no bag inside a bag, and a bag can't be quick-moved into itself.

## Requires

- BepInExPack for Valheim
- FiresUnifiedCore 0.2.389 or newer
- VAInventory (optional) - shows each worn bag's grid inside the inventory panel.
- FiresGuilds (optional) - guildmates can use your camp chest.

## Installation

- Install with a mod manager, or copy the `VABackpacks` folder into `BepInEx/plugins`.
- Client and dedicated server.

## Configuration

Settings live in `BepInEx/config/com.Fire.VA_Backpacks.cfg`.

- `[Backpacks] OpenHotkey` (default B).
- `[Backpack.<bag>]` per bag (`SmallBag_va`, `DayBag_va`, `ExplorerBag_va`, `HoboStick`, `StaffFiresWizard`):
  - `Rows` / `Columns` - storage size. Defaults: Small 5x2, Day 6x3, Explorer 7x4, Hobo Stick and Staff 5x3.
  - `WeightReductionMultiplier` (default 0.5) - contents weight when used without VAInventory's panels.
  - `ColdResistance`, `WetResistance`, `FeatherFall`, `StrengthBonus` (all default off).
  - `CampBlueprint` (+ `CampBlueprintYOffset`) - optional PlanBuild blueprint to use as this bag's camp layout.
  - `SpeedModifier` and `Teleportable` are in the file but do nothing yet.
- `[Recipes.<bag>]` - `Recipe` (station, level, Item:amount pairs; empty = not craftable) and `Upgrade` (the quality
  2 cost; empty = half of each crafting ingredient). `[General] EnableRecipes` (default on) is the master switch.
- `[Loot] One chest in` (default 40) - about one chest in this many in a bag's biome holds that bag. 0 = never.
- `[Camp]` - `Radius` (10,20,30,40 m) and `PieceCap` (500 to 2000) per camp level, `Level3Extra` (Iron:5) and
  `Level4Extra` (Silver:5) upgrade costs, `CustomCamps` (default on).
- `[CampStorage]` - how much items weigh while packed inside a camp's chests, by kind (food and gear 0, ore and bars
  5%, everything else 1%).
- `[Backpacks.Camp]` - `Enabled` (default on), `GestureKey` (B; Shift is always required), `HoldDurationSeconds`
  (3), and the prefab used for each camp piece. `[Backpacks.Camp.Blueprint]` decides which normal camp pieces still
  spawn next to a blueprint camp.

## Multiplayer

Recipes, upgrade costs, chest loot, camp levels, camp storage weights, `EnableRecipes` and `VerboseLogging` are
server-synced. Bag sizes, perks and the camp layout are read from each player's own config, so ship the same config
with your modpack if you want every player's bags to match.

## Issues / questions

https://discord.gg/zQRgHbqms4

## Licence

MIT. See the `LICENSE` file.
