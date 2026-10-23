# VABackpacks

Real backpacks with real storage for Valheim. Five bags you can craft or find, upgrade at the workbench, and unpack
into a full camp wherever you stop for the night. Part of the Fires mod family.

## What you get

- **Five bags.** The Small Bag, Day Bag and Explorer Bag are worn on your shoulder and show on your back, cape or not.
  The Hobo Stick and the Fires Wizard's Staff carry their load in hand.
- **Crafted or found.** Every bag is made at the workbench. The shoulder bags can also turn up in chests: the Small
  Bag in the Meadows, the Day Bag in the Black Forest, the Explorer Bag in the Swamp and Mountains.
- **The contents travel with the bag.** Give it to a friend, put it in a chest or drop it, and everything inside goes
  with it. If you die, the bag lands in your tombstone with its contents intact.
- **Workbench upgrades.** A quality 2 bag gets special slots beside its grid:
  - Small Bag: a tool slot (one weapon or tool).
  - Day Bag: tool slot + resources slot (one stack that weighs a quarter as much).
  - Explorer Bag, Hobo Stick, Staff: tool + resources + teleport bypass slot (one non-teleportable stack can go
    through portals).
  - Quality 3 and 4 make the bag's camp bigger.
- **Bags become camps.** A campfire with a cooking grill, the bag standing open as your chest, a workbench, bed, log
  bench and a wood pile that gives a little wood each day, all on flattened ground.
- **Pack it back up.** The camp folds back into the bag and the ground is put back. Anything you built inside the
  camp packs up with it and comes back next time. One camp per character.
- **Bag perks** (set per bag in the config, all off by default): cold and wet resistance, feather fall, extra carry
  strength.
- **Early magic staffs** (off by default, turn on `[Early Magic] Enabled`): ten early staffs and five eitr foods. With
  `Staff Attack Styles` on, each staff has its own primary and secondary attack; placed attacks show a ground ring you
  aim and left click to cast.

## How to use

- Equip a bag and press **B** to open it.
- **Make camp:** drop the bag, look at it and hold **E** for 3 seconds, or hold **Shift+B**. With the Hobo Stick or
  Staff, swing with the middle mouse button to slam a camp down where it lands.
- **Pack up:** look at the camp chest and hold **Shift+B**. The bag goes back into your inventory (or at your feet if
  you're full).
- You can't put a bag inside another bag.

## Install

- Install with a mod manager, or copy the `VABackpacks` folder into `BepInEx/plugins`.
- Install on every client and on the dedicated server.

## Requires

- BepInExPack for Valheim
- FiresUnifiedCore
- VAInventory (optional) - shows each worn bag's grid inside the inventory panel.
- FiresGuilds (optional) - guildmates can use your camp chest.

## Good to know

- Settings are in `BepInEx/config/com.Fire.VA_Backpacks.cfg`. Things you might change: the open key, each bag's
  size (rows and columns), bag perks, speed and whether it can go through portals, recipes, how often bags show up
  in chests, and camp sizes.
- On a server, recipes, chest loot and camp settings come from the server. Bag sizes and perks come from each
  player's own config, so share the same config with your group if you want everyone's bags to match.

## Issues / questions

https://discord.gg/zQRgHbqms4

## Licence

MIT. See the `LICENSE` file.
