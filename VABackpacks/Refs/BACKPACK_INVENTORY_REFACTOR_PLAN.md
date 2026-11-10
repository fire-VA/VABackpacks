# VABackpacks — Inventory Refactor & Compatibility Plan

**Goal.** Stop owning a separate `Container` on the Player. Instead expose
a small public API that VAInventory subscribes to, so backpack storage
becomes _real player-inventory slots_ appended to the equipment panel
when a backpack is equipped. Coin storage moves out of the backpack
entirely and into a dedicated infinite-stack coin-purse slot owned by
VAInventory.

This plan covers VABackpacks only. Companion plans live in
`VAInventory/Refs/BACKPACK_AND_COIN_SLOT_PLAN.md` and
`FiresNPCs/Refs/INVENTORY_BACKPACK_COMPAT_PLAN.md`.

---

## 1. Current state (snapshot)

- **`Managers/BackpackManager.cs`** — owns four hardcoded backpack
  prefabs (`SmallBag_va`, `DayBag_va`, `ExplorerBag_va`, `HoboStick`),
  configurable rows/columns/effects per prefab.
- **Storage strategy.** On `Player.OnSpawned`, a `Container` component
  is added to the player. On `Humanoid.EquipItem`, the manager swaps
  `Container.m_inventory` to a new `Inventory(name, null, cols, rows)`
  and loads from `item.m_customData["BackpackInventory"]`.
- **Display.** `InventoryGui.Show(container)` opens the standard
  side-panel container view. Several patches close this view when the
  backpack is unequipped or selected.
- **Effects.** `ApplyEffects()` adds/removes Cold/Wet/Feather-Fall
  status effects via `SEMan` and modifies stamina/speed.
- **Persistence.** Inventory is serialized to a `ZPackage` base64
  string stored in `item.m_customData["BackpackInventory"]`.

The Container approach works standalone, but it conflicts with
VAInventory's slot system (two systems both believing they own the
inventory grid). It also splits the player's storage across two
windows, which the user wants to consolidate.

## 2. New architecture (with VAInventory present)

VAInventory owns the equipment panel and grid. VABackpacks becomes a
thin "what's equipped, with what config" provider plus the prefabs and
status-effects.

```
[VABackpacks]                                    [VAInventory]
  Prefabs (SmallBag_va, ...) ────────────────►   Slots.IsBackpackSlotItem
  BackpackManager.IsBackpack(item)               (subscribes to events)
  BackpackManager.GetConfig(name) ──────────►    appends rows × cols
  BackpackManager.GetEquipped(player)            extra slots after misc
  events: OnEquipped / OnUnequipped ─────────►   triggers panel rebuild
  ApplyEffects (cold/wet/featherfall)            (unchanged path)

  [Coins → moved out entirely. Coin purse is a separate VAInventory slot.]
```

The big shifts:

1. **No more Player-side `Container`.** VAInventory's grid extension is
   the storage. The backpack item still owns its data via `customData`,
   but VAInventory reads/writes that field directly through the API
   defined below.
2. **No more `InventoryGui.Show(container)` flow.** The extra slots are
   visible inline in the inventory window when it's open.
3. **Effects stay in this mod.** Cold/Wet/FeatherFall/Speed/Strength all
   continue to be applied in `OnEquipItem`/`OnUnequipItem`. Those are
   gameplay, not inventory layout.
4. **Standalone fallback.** A config flag `UseLegacyContainer` (default
   `false`) keeps the old Container path so VABackpacks remains usable
   without VAInventory. Off by default; enabled at startup if
   VAInventory plugin GUID is not loaded.

## 3. Public API (new file: `Managers/BackpackApi.cs`)

```csharp
public static class BackpackApi
{
    // ── Identification ─────────────────────────────────────────────
    public static bool IsBackpack(ItemDrop.ItemData item);
    public static bool IsBackpack(string prefabName);

    // ── Per-prefab config lookup ──────────────────────────────────
    // Returns null if the prefab isn't a registered backpack.
    public static BackpackConfig GetConfig(string prefabName);
    public static BackpackConfig GetConfig(ItemDrop.ItemData item);

    // ── Equipped state ────────────────────────────────────────────
    public static ItemDrop.ItemData GetEquippedBackpack(Player p);
    public static bool TryGetEquippedConfig(Player p, out BackpackConfig cfg);
    public static int GetEquippedRows(Player p);
    public static int GetEquippedColumns(Player p);
    public static int GetEquippedSlotCount(Player p);   // rows * columns

    // ── Storage round-trip (raw, used by VAInventory) ─────────────
    // Returns base64 ZPackage string for the backpack's stored items.
    public static string ReadStorage(ItemDrop.ItemData backpackItem);
    public static void   WriteStorage(ItemDrop.ItemData backpackItem, string base64);

    // ── Events ────────────────────────────────────────────────────
    public static event Action<Player, ItemDrop.ItemData, BackpackConfig> OnEquipped;
    public static event Action<Player, ItemDrop.ItemData> OnUnequipped;
}
```

`BackpackConfig` already exists; expose it (currently nested inside
`BackpackManager`). Move it to `Managers/BackpackConfig.cs` so external
mods can name the type without poking at internals.

## 4. Backpack registration becomes data-driven

Today the four prefab names are baked in. Open it up so other mods (or
content packs) can add backpacks at startup without forking this code.

```csharp
public static void RegisterBackpack(string prefabName, BackpackConfig cfg);
public static void UnregisterBackpack(string prefabName);
public static IReadOnlyCollection<string> GetRegisteredPrefabs();
```

The four built-in prefabs are still registered by VABackpacks itself
in `BackpackManager.Initialize()` — no behavior change for end-users.

## 5. Refactor checklist (file-by-file)

### `Managers/BackpackManager.cs`
- **Move out** `BackpackConfig` to its own file.
- **Move out** Equip/Unequip event firing to `BackpackApi` (keep the
  Harmony patches here, just publish events from them).
- **Delete** `Player_OnSpawned_Patch` (no Container injection any more).
- **Delete** `InventoryGui_Show_Patch` (no container-window swap).
- **Delete** `OnSelectedItem_Patch` close-on-select branch (no container).
- **Keep** the equip/unequip detection patches, but their bodies now
  just fire `BackpackApi.OnEquipped/OnUnequipped` and apply effects.
- **Keep** `ApplyEffects()` exactly as-is.
- **Add** `_legacyMode` bool — set true at startup if VAInventory isn't
  loaded; if true, restore the Container-based path verbatim (existing
  code, gated). This is the standalone fallback.

### `Managers/BackpackApi.cs` (new)
- Holds the static API listed in §3.
- Holds the registered-backpack dictionary.
- Holds the events.
- `ReadStorage`/`WriteStorage` thinly wrap `customData["BackpackInventory"]`.

### `Ascend.cs`
- Detect VAInventory at `Awake`: `Chainloader.PluginInfos.ContainsKey("com.Fire.VAInventory")`.
- If present → log "VAInventory detected, using slot-based storage" and
  set `BackpackManager.LegacyMode = false`.
- If absent → leave LegacyMode true; old Container path applies.
- Add `BackpackManager.RegisterDefaultBackpacks()` after item-prefab
  load so the registry is populated before any equip event fires.

### `Managers/ConfigManager.cs`
- No new configs required. Optionally add `[Compatibility] UseLegacyContainer`
  to force-override the auto-detect (debugging aid).

## 6. Asset / prefab changes

None. The existing prefabs work as-is; what changes is how their
`customData["BackpackInventory"]` round-trips and what the player sees
when they open inventory.

One tweak worth considering (defer to follow-up): the four backpacks
currently use the `Shoulder` (cape) item type. That's fine — it conflicts
with capes but VAInventory has a separate Backpack slot now, so we
should change `m_itemType` of the backpack prefabs to a custom type or
keep `Shoulder` and let the slot validator (in VAInventory) accept only
backpack-registered prefabs. The simpler path: keep `Shoulder`, validate
by name in the new Backpack slot. Prevents capes from going into the
backpack slot.

## 7. Migration / backwards compatibility

- **Existing saves with backpack data.** The `customData["BackpackInventory"]`
  base64 string is the same on both sides — VAInventory reads it through
  `BackpackApi.ReadStorage` exactly the same way the old Container did.
  No save migration needed.
- **Players still using legacy.** If a user runs VABackpacks without
  VAInventory, the old Container path keeps working — we don't delete
  that code, we gate it.
- **Players upgrading.** First load with VAInventory present: backpack
  data is read once, populated into the equipment-panel slots. On
  unequip the data is written back. Round-trip is lossless because both
  use the same ZPackage format.

## 8. Out of scope (for this refactor)

- Coin purse logic — entirely in VAInventory.
- The legacy `VerdantsAscentBackpacks` mod (separate plugin GUID
  `com.Fire.verdantsascent`). It's a standalone older mod with its own
  `Backpack1_vad` prefab using the cape slot. It is **not** compatible
  with this slot-based system and should be marked deprecated; users
  should migrate to VABackpacks. We'll add a note to that mod's README
  in a follow-up — no code changes to it as part of this task.

## 9. Backpack Camp feature

The "deploy backpack as camp" feature is implemented as a separate
subsystem under `Managers/Camp/`. It depends on the public BackpackApi
(for `IsBackpack` + `CUSTOM_DATA_KEY` + per-prefab Rows/Columns) but
runs independently of the slots-vs-legacy storage mode debate — camps
work in either mode.

### Files

| File | Role |
|------|------|
| `Managers/Camp/BackpackCampManager.cs` | Lazy-singleton MonoBehaviour. Holds the per-frame hold-gesture state, runs the deploy + pack-up routines, enforces one-camp-per-character via `Player.m_customData["VA_BackpackCampZdoId"]`. |
| `Managers/Camp/BackpackCampMarker.cs` | MonoBehaviour added at runtime to a deployed camp container. Carries owner / linked-piece ZDOIDs / original-backpack metadata. Round-trips fields through the chest's ZDO under `va_camp_*` keys, so a chunk reload → ZDO restore → chest spawn cycle preserves the camp. Resizes the chest's inventory at hydrate time so the deployed chest matches the backpack's grid. |
| `Managers/Camp/CampInteractPatches.cs` | Harmony patches: ItemDrop + Container `GetHoverText` postfixes append the gesture prompt; `Interact` prefixes intercept Shift+E to start/continue the timer. A `Container.Awake` postfix re-attaches the marker on chunk reload by sniffing the `va_camp_owner` ZDO field. |

### Player flow

1. Drop a backpack on the ground (vanilla pickup → drop via inventory).
2. Hover at it: hover text shows the normal pickup line plus
   `[Hold L.Shift + Use] Deploy as camp (3s)`.
3. Hold Shift+E for 3 seconds (configurable). A "Deploying camp...
   Xs" message ticks down on the center HUD.
4. On completion: campfire spawns at the dropped-backpack location,
   the chest (with the backpack's contents and grid) and a workbench
   spawn ~5m away in random directions, the original ItemDrop
   despawns. The player's `m_customData` records the camp's ZDOID.
5. Use the camp normally. Press E on the chest to open like any chest;
   sit at the campfire; craft at the workbench. The chest is a vanilla
   `piece_chest_wood` so vanilla decay/security/access logic apply.
6. To pack: hover the chest, hold Shift+E for 3 seconds. The chest's
   contents are serialized into a backpack ItemDrop, the campfire and
   workbench are despawned via their stored ZDOIDs, the chest is
   despawned, and the backpack-on-ground appears at the chest's spot.
7. The player's camp marker is cleared, freeing the slot for a new
   deploy.

### Config (BepInEx config — section `[Backpacks.Camp]`)

| Key | Default | Description |
|-----|---------|-------------|
| `Enabled` | `true` | Master toggle for the whole camp feature. |
| `HoldDurationSeconds` | `3.0` | How long the player must hold Shift+E. Range 0.5–10. |
| `PieceSpawnRadius` | `5.0` | Distance from the campfire (anchor) where the chest and workbench spawn, each in a random direction. Range 2–15. |
| `PieceMinSeparation` | `2.0` | Minimum distance between any two camp pieces. Range 1–10. |
| `CampfirePrefab` | `fire_pit` | ZNetScene prefab name. Swap to any campfire-like piece (e.g. `piece_groundtorch_wood`). |
| `WorkbenchPrefab` | `piece_workbench` | ZNetScene prefab name. |
| `ContainerPrefab` | `piece_chest_wood` | ZNetScene prefab name. The chest's grid is overridden at runtime to match the backpack's `Rows × Columns`, regardless of the prefab's intrinsic size. |

### One-camp-per-character

Tracked via `Player.m_customData["VA_BackpackCampZdoId"]`, which stores
the deployed chest's ZDOID as `"userId:id"`. On a fresh deploy attempt:

- If the key is missing → allow.
- If present and the ZDO is still alive in `ZDOMan` → refuse with a
  message ("You already have a camp deployed").
- If present but the ZDO is gone (camp was destroyed externally —
  decay, admin tooling, world prune) → clear the stale key and allow.

The check is currently client-side only. Server-authoritative
enforcement (an RPC that scans for camp ZDOs by owner before allowing
a second deploy) is a follow-up — see §10.

### Deferred / follow-up items (camp)

- **Server-authoritative deploy guard.** Scan ZDOMan for any chest
  ZDO with `va_camp_owner == playerId` before honoring a deploy RPC,
  to prevent client-side cheats from doubling up.
- **Custom backpack-shaped piece prefab.** The chest currently uses
  `piece_chest_wood` for visuals. A dedicated piece (with a backpack
  mesh + a "tent" silhouette for the camp) would be nicer but
  requires registering a new prefab in the asset pipeline.
- **Snap-to-ground refinements.** The current `ZoneSystem.GetGroundHeight`
  snap can place pieces on steep slopes. A follow-up could check the
  slope and reject candidate positions above N degrees.
- **Camp expiry / decay.** Vanilla chests don't decay outside guard
  stones, so an abandoned camp persists indefinitely. A timed
  auto-pack (e.g. "8 in-game hours after owner logs off, the camp
  packs itself into a ground-drop") would prevent map litter.
- **Visual deploy / pack effects.** Particle puff + sound on
  spawn/despawn would sell the action better. Currently it's silent.
- **Admin override.** Admins should be able to pack any camp, not
  just their own.

## 10. Test plan

- [ ] VABackpacks alone (no VAInventory): legacy Container path works.
- [ ] VABackpacks + VAInventory: equip a backpack → extra slots appear
      in equipment panel; items placed there persist across logout.
- [ ] Unequip → slots disappear; items inside are written back to
      `customData["BackpackInventory"]`.
- [ ] Re-equip same backpack → items re-appear in the same grid layout.
- [ ] Equip a different backpack with smaller dimensions → overflow
      items spill back into the player's main inventory (or are dropped
      if no space).
- [ ] Cold/Wet/Feather-Fall/Speed effects still apply on equip/unequip.
- [ ] Quick-transfer (Shift+click) in/out of backpack slots works.
- [ ] Shoulder/cape items refuse to go in the backpack slot.
- [ ] Two clients on a server: equip on client A → client B's view of
      A's backpack contents stays consistent (this is just vanilla ZDO
      sync of the held item; no server-side backpack state).
