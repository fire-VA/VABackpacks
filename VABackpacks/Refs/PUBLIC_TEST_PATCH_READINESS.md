# Valheim Public-Test → Live Patch-Readiness Tracker

**Scope:** FiresNPCs (FiresRPGmaker), VAInventory, VABackpacks, VAGhettoNetworking
**Compared:** `assembly_valheim` (current live) vs `assembly_valheim_publicTest` (next release candidate)
**Created:** 2026-05-07
**Status:** ⚠️ Not yet patched — gameplan only. Update each row when public test promotes to live and we begin fixing.

> **Patch day procedure:** when public test promotes to live, walk this doc top to bottom. Each row has the file/line in our mods that needs a touch and the fix shape. Tick the checkbox when done. Treat the doc as the canonical pre-flight; everything we noticed during the diff sweep is here so we don't have to re-derive it.

---

## 0. Headline summary

The test build is dominated by **four** structural game changes:

1. **`Vector2i` → `Vector2s` migration** — every zone/sector identity in the game has a new struct type. `ZoneSystem.*`, `ZNetScene.InActiveArea/OutsideActiveArea/HaveInstanceInSector`, `ZDOMan` sector APIs, `Plant.SUpdate`, `Location.GetZoneLocation`, `ZDO.GetSector`/`SetSector` (the latter retyped to `ZoneSystem.SectorIndex`).
2. **Save-system overhaul** — `ZDOMan.SaveAsync` removed (replaced by chunked saves with new `ChunkSaveMapping` and `SaveFileHelper` classes). `ZNet.LoadWorld()` returns `bool` (was `void`). `Load(…, int version)` becomes `Load(…, Version.World version)` everywhere. Save format versions for `ZDO`, `Inventory` (106→108), `Player` (29→32), `RandEventSystem`, `ZoneSystem` all rev. Network protocol bumps **36 → 37** so clients must update in lockstep with servers.
3. **`VisEquipment` string→int hash migration** — every `Set*Item(string name, …)` becomes `Set*Item(int itemHash, …)`; every backing slot field flips type. (`Humanoid` slot fields stay as `ItemDrop.ItemData` references — those are NOT affected.)
4. **`ItemDrop` storage merger** — `SaveToZDO`/`LoadFromZDO` overload pairs collapsed to single methods with `int index = -1` default; ZDO storage flips to a single `s_itemData` byte[] using new `ItemData.Save(ZPackage)` / `static Load(ZPackage)`. Per-field ZDOVars (`s_durability`, `s_stack`, `s_quality`, `s_variant`, `s_crafterID`, `s_crafterName`, `s_dataCount`, `s_worldLevel`, `s_pickedUp`, `data_*`, `data__*`) are no longer written on the indexed path.

Plus a long tail of smaller renames, added-overload ambiguities, public→static conversions, and behavior changes.

**Counts:**
- 🔴 Hard breaks (won't bind / won't compile / will throw): **17 sites**
- 🟡 Soft breaks (signature ok, body/behavior changed; transpilers at risk): **14 sites**
- 🟢 Confirmed safe (body changes but no surface drift): the rest of the patched surface

**Per-mod tally:**

| Mod | Hard breaks | Soft breaks | Risky reflection |
|-----|-------------|-------------|---------------|
| FiresNPCs | 7 | 5 | 2 |
| VAInventory | 1 | 2 | 1 |
| VABackpacks | 0 | 1 | 1 |
| VAGhettoNetworking | **9** | **6** | **5** |

VAGhettoNetworking is the most exposed by a wide margin — it's a networking-focused mod that sits squarely in the path of the save-system overhaul and the `Vector2i`→`Vector2s` migration. See §7.

---

## 1. 🔴 Hard breaks — must fix before patch day

Each row below identifies the game-side change, then enumerates exact mod-side files/lines that need to be updated.

### 1.1 `Vector2i` → `Vector2s` on patched / called methods

**Game-side rename matrix:**

| Old signature | New signature |
|---|---|
| `ZoneSystem.SpawnZone(Vector2i, SpawnMode, out GameObject)` | `(Vector2s, SpawnMode, out GameObject)` |
| `ZoneSystem.GetZone(Vector3) → Vector2i` | `→ Vector2s` (return-type change) |
| `ZoneSystem.GetZonePos(Vector2i) → Vector3` | `(Vector2s) →` |
| `ZoneSystem.IsZoneLoaded(Vector2i)` | `(Vector2s)` |
| `ZoneSystem.IsZoneReadyForType(Vector2i, ZDO.ObjectType)` | `(Vector2s, …)` |
| `ZoneSystem.PokeLocalZone(Vector2i)` | `(Vector2s)` |
| `ZoneSystem.SetZoneGenerated(Vector2i)` | `(Vector2s)` |
| `ZoneSystem.IsZoneGenerated(Vector2i)` | `(Vector2s)` |
| `ZoneSystem.m_locationInstances Dictionary<Vector2i, …>` | `<Vector2s, …>` |
| `ZoneSystem.m_zones / m_generatedZones / m_loadingObjectsInZones / m_tempGeneratedZonesSaveClone` | all keys flip to `Vector2s` and become `readonly` |
| `ZoneSystem.GetRandomZone(float)` | returns `Vector2s` |
| `ZoneSystem.GetRandomPointInZone(Vector2i, float)` | `(Vector2s, float)` |
| `ZNetScene.InActiveArea(Vector2i, Vector3)` | `(Vector2s, Vector3)` |
| `ZNetScene.InActiveArea(Vector2i, Vector2i)` | `(Vector2s, Vector2s)` |
| `ZNetScene.InActiveArea(Vector2i, Vector2i, int)` | `(Vector2s, Vector2s, int)` |
| `ZNetScene.OutsideActiveArea(Vector3, Vector2i, int)` | `(Vector3, Vector2s, int)` |
| `ZNetScene.HaveInstanceInSector(Vector2i)` | `(Vector2s)` |
| `ZDO.GetSector() → Vector2i` | `→ Vector2s` |
| `ZDO.SetSector(Vector2i)` | `(ZoneSystem.SectorIndex)` ← param TYPE is `SectorIndex`, not `Vector2s` |
| `ZDOMan.AddToSector(ZDO, Vector2i)` | `(ZDO, ZoneSystem.SectorIndex)` |
| `ZDOMan.RemoveFromSector(ZDO, Vector2i)` | `(ZDO, ZoneSystem.SectorIndex)` |
| `ZDOMan.FindObjects(Vector2i, List<ZDO>)` | `(Vector2s, List<ZDO>, HashSet<SectorIndex>)` ← extra param |
| `ZDOMan.FindDistantObjects(Vector2i, List<ZDO>)` | `(Vector2s, List<ZDO>, HashSet<SectorIndex>)` |
| `ZDOMan.IsInPeerActiveArea(Vector2i, long)` | `(Vector2s, long)` |
| `ZDOMan.FindSectorObjects(Vector2i, int, int, List<ZDO>, List<ZDO>)` | needs verification — probably `(Vector2s, …)` |
| `Plant.SUpdate(float, Vector2i)` | `(float, Vector2s)` |
| `Location.GetZoneLocation(Vector2i)` | `(Vector2s)` |

> Note: `Vector2s` has the same `int x, int y` shape as `Vector2i` but is a different struct (probably "short" or "small"). Implicit conversion is unlikely; you'll need to call new helpers (`ZoneSystem.GetSectorIndex`, `SectorToIndex`, `IndicesToIndex`) to bridge.

**New related public types added (`ZoneSystem`):**
- `struct SectorIndex { uint Sector; }`
- `readonly struct ChunkIndex(ushort, byte)`
- Helpers: `GetSectorIndex`, `SectorToIndex(Vector2s)`, `SectorToIndex(int,int)`, `IndicesToIndex`, `IndexToSector`, `GetZonesChunk`, `GetZoneFromChunk`, `ChunkIndexFromXY`, `ChunkIndexFromIndexAndSize`
- Statics: `SectorZero`, `ChunkZero`, `ChunkPortal`, `c_ZonesPerChunk = 8`, `c_MaxChunkSize = 3`

**Mod sites to update:**

#### FiresNPCs
- [ ] `Modules\CustomWater\River\AutoWaterPatches.cs:739` — `[HarmonyPatch(typeof(ZoneSystem), "SpawnZone")]` Prefix. Patch must change parameter from `Vector2i zoneID` → `Vector2s zoneID`. Harmony binds args by name, but the type is the breaker.
- [ ] Project-wide grep for `Vector2i` in `Modules\CustomTerrain\*` and `Modules\CustomWater\*` — anything that came from a Valheim API will fail to compile. Let the build walk through them.
- [ ] `Modules\Companions\AI\CompanionAI\*` — pathfinding and zone-aware movement may construct `Vector2i` keys to look into `ZoneSystem.m_zones` etc. Update key type.

#### VAGhettoNetworking
- [ ] `Networking\ServerAuthorityPatches.cs:63, 107, 198` — direct `ZoneSystem.GetZone(Vector3)` calls; result was `Vector2i`. Must become `Vector2s`.
- [ ] `Networking\ServerAuthorityPatches.cs:64` — direct `ZDOMan.instance.FindSectorObjects(Vector2i, int, int, List<ZDO>, List<ZDO>)`. Verify new signature in test build, swap to `Vector2s`.
- [ ] `Networking\ServerAuthorityPatches.cs:100, 113` — `m_zones.ContainsKey(new Vector2i(x, y))`. Replace `Vector2i` with `Vector2s` (or use the new `SectorIndex` if the dictionary key type changed further).
- [ ] `Networking\ServerAuthorityPatches.cs:185` — `[HarmonyPatch(typeof(ZNetScene), "OutsideActiveArea", new[]{ typeof(Vector3) })]` overload-pinned. Confirm the `(Vector3)` single-arg overload still exists; the 3-arg overload separately needs `Vector2s`.
- [ ] `Networking\ServerAuthorityPatches.cs:198` — direct `ZNetScene.OutsideActiveArea(Vector3, Vector2i, int)` static call. Update to `Vector2s`.
- [ ] `Networking\MonsterAiPatches.cs:372, 379, 380` — direct `ZoneSystem.GetZone(Vector3)` and `ZNetScene.InActiveArea(Vector2i, Vector2i, int)` static. Update both.
- [ ] `Networking\MonsterAiPatches.cs:373` — reads `ZoneSystem.m_zones` (publicized). Key type changes.
- [ ] `Networking\RpcHandlers\SpawnedZoneHandler.cs:79` — `ZoneSystem.GetZonePos(Vector2i)` direct call. Update to `Vector2s`.

> **Estimated effort for the migration alone:** 1–2 hours of mechanical type-replacement + compile loop, assuming the only changes are `Vector2i` → `Vector2s` literal substitutions. If any API became `SectorIndex`-keyed instead of `Vector2s`-keyed, that's another hour because you can't directly construct `SectorIndex` — you have to go through `ZoneSystem.GetSectorIndex(...)` or `SectorToIndex(...)`.

### 1.2 `ZDOMan.SaveAsync(BinaryWriter)` removed entirely

**Game-side change:** save flow rewritten to chunked saves: new methods `SaveChunks(string, FileHelpers.FileSource)`, `LoadChunks`, `DeleteCurrentChunksAndRevert`, `DeleteOldChunks`, `SaveCleanup`, `SetDirtySector`, `SetDirtyPortals`, `EndSave`. New nested enum `ZDOMan.SaveState`. Helper classes `ChunkSaveMapping` (with nested `ChunkInfo`) and `SaveFileHelper` (with `SaveWrite`/`SaveReader` IDisposables) added.

Field changes inside `ZDOMan`:
- `m_saveData.m_zdos` REMOVED → `m_saveData.m_objectsByChunk` (`List<Tuple<ChunkIndex, List<ZDO>>>`)
- `m_saveData.m_sessionID` REMOVED
- `m_saveData.m_nextUid` REMOVED
- `m_numFiles`, `m_dirtyFiles` ADDED
- `private readonly List<ZDO> m_portalObjects` → `Dictionary<ZoneSystem.SectorIndex, List<ZDO>> m_portalObjects` ← **type changed**
- `m_objectsByOutsideSector` REMOVED entirely
- `m_halfWidth` REMOVED
- `private static int SectorToIndex(Vector2i)` REMOVED (replaced by static `ZoneSystem.SectorToIndex`)
- `private void AddPortal(ZDO)` → `private void AddIfPortal(ZDO zdo, int prefabHash)` ← name + signature change
- `private void FilterZDO(...)` instance → `private static FilterZDO(bool newVersion, ...)` — instance→static + new param
- `private void WarnAndRemoveBrokenZDOs(...)` instance → static
- `public List<ZDO> GetPortals()` → `public Dictionary<SectorIndex, List<ZDO>> GetPortals()` (return type changed). New helper `public List<ZDO> GetPortalList()` returns the old shape for backwards compat.
- `public void Load(BinaryReader, int)` → `(BinaryReader, Version.World)` (param type)

**Mod sites:**

- [ ] **FiresNPCs / VAInventory / VABackpacks / VAGhettoNetworking** — none patch `SaveAsync` directly per the inventory pass.
- [ ] **VAGhettoNetworking** — `RoutedRpcManager.cs:161`, `RpcHandlers\SetTargetHandler.cs:40` access `ZDOMan.instance.m_objectsByID` (publicized direct), still present. Safe.
- [ ] **VAGhettoNetworking** — `Networking\ZDOMemoryManager.cs:55, 79` reflects on `m_objectsByID`. Field still present. Safe.
- [ ] **Anywhere** that calls `ZDOMan.GetPortals()` and treats the return as `List<ZDO>` — switch to `GetPortalList()`. Audit on patch day.
- [ ] **Anywhere** that reads `m_saveData.m_zdos` / `m_sessionID` / `m_nextUid` via reflection — these fields are gone. None spotted in current grep, but verify on first compile.

### 1.3 `ZNet.LoadWorld()` return type `void` → `bool`

**Game-side:** body completely rewritten (chunked loader). New private helper `LoadOldWorld()` added alongside.

**Mod sites:**

- [ ] **FiresNPCs** `Modules\Discord\DiscordIntegrationPatches.cs:41` — `[HarmonyPatch(typeof(ZNet), "LoadWorld")]` PrePost. If the patch declared `void __result` the patch errors; remove or retype to `bool __result`. Postfix logic must consider that the method now reports success/failure.

### 1.4 `ZoneSystem.SaveASync` renamed to `Save`; `Load(BinaryReader, int)` → `Load(BinaryReader, Version.World)`

**Game-side:** `SaveASync` was renamed to `Save`. Body completely rewritten (now compresses to ZPackage and writes a length-prefixed blob). Old-format reader is now `LoadOld(BinaryReader, Version.World)`. Param renamed `reader` → `binaryReader`.

**Mod sites:**

- [ ] **FiresNPCs** `Modules\Progression\ProgressionPatches.cs:184` — `[HarmonyPatch(typeof(ZoneSystem), nameof(Load))]` PrePost. The `nameof` resolution still finds `Load`, but if the Postfix declared the second param as `int version` it must become `Version.World version`. **Param renamed `reader` → `binaryReader`** — Harmony binds by name, so any patch declaring `BinaryReader reader` must be updated to `BinaryReader binaryReader`.

### 1.5 `RandEventSystem.SaveAsync(BinaryWriter)` renamed to `Save(BinaryWriter)`; `Load(..., int)` → `Load(..., Version.World)`

**Game-side:** `SaveAsync` renamed to `Save`. Internal version compare `version < 25` → `version < Version.World.SupportRandomEvent2`.

**Mod sites:**

- [ ] **FiresNPCs** does not patch `SaveAsync`/`Load` directly. Only patches `RefreshPlayerEventData`, `HaveGlobalKeys`, `SetActiveEvent` — all signatures intact. Safe.
- [ ] **VAGhettoNetworking** `Networking\MonsterAiPatches.cs:281` patches `SetActiveEvent(RandomEvent, bool)` — overload-pinned, signature unchanged in the diff. Safe.
- [ ] **VAGhettoNetworking** `Networking\MonsterAiPatches.cs:258, 310` patches `FixedUpdate` (private). Signature unchanged. Safe.

### 1.6 `Inventory.Load(ZPackage)` becomes ambiguous

**Game-side:** new sibling overload `Load(ZPackage, bool _)` added. `Save(ZPackage)` writes version 108 (was 106). The old method body was renamed to `OldSave(ZPackage)`. New private `m_temoraryInventory` field [vanilla typo preserved]. New `AddItem(int prefabHash, ItemDrop.ItemData)` and `AddItem(int prefabHash, int, float, …)` overloads added. New `Inventory(bool _)` ctor.

**Mod sites:**

- [ ] **VAInventory** `Managers\CoinPurseStackPatch.cs:191` — `[HarmonyPatch(typeof(Inventory), nameof(Load))]` Postfix. **Will throw `AmbiguousMatchException` at startup** because `Load` now has two overloads. Fix:
  ```csharp
  [HarmonyPatch(typeof(Inventory), nameof(Inventory.Load), new[] { typeof(ZPackage) })]
  ```
- [ ] **VAInventory** `Managers\CoinPurseStackPatch.cs:184` — `[HarmonyPatch(typeof(Inventory), nameof(Save))]` Prefix. Save is still a single overload; `nameof(Save)` resolves uniquely. Safe.
- [ ] **VAInventory** `Managers\CoinPurseStackPatch.cs:165` — `AddItem(ItemDrop.ItemData)` patch with explicit `[typeof(ItemDrop.ItemData)]` overload spec. Pinned, safe (the new `int` overloads have different param types).
- [ ] **VAInventory** `Managers\InventoryInteraction.cs:279` — `AddItem(ItemDrop.ItemData)` postfix with explicit spec. Pinned, safe.
- [ ] **VABackpacks** `BackpackManager.cs:940` — `AddItem(ItemDrop.ItemData)` patch with explicit `new[]{typeof(ItemDrop.ItemData)}`. Pinned, safe.
- [ ] **VABackpacks** direct call `inventory.Save(ZPackage)` / `Load(ZPackage)` at `BackpackManager.cs:764, 788, 825`, `Camp/BackpackCampManager.cs:2092, 2109` — overload resolution still picks single-arg. Safe.
- [ ] **VABackpacks/VAInventory** any reflective ctor enumeration on `Inventory` — none in current grep. Verify on patch day; if any future code reflects on the constructor, pin the parameter list `[typeof(string), typeof(Sprite), typeof(int), typeof(int)]`.

### 1.7 `Container.m_lastDataString` private field removed; ZDO storage migrated string→byte[]

**Game-side:** `Container.Load`/`Save` body rewritten to read/write `ZDOVars.s_items` as `byte[]` instead of base64 string.

**Mod sites:**

- [ ] **VABackpacks** uses `Container.m_inventory` heavily — that field is unchanged. Safe.
- [ ] **VABackpacks** `Camp/BackpackCampManager.cs` Save/Load via `Inventory.Save(ZPackage)`/`Load(ZPackage)` directly — also safe.
- [ ] No mod reflects `m_lastDataString`. Safe.
- [ ] **Anywhere** that read `ZDO.GetString(ZDOVars.s_items)` to peek container contents from outside will now read empty. None spotted in our four mods, but flag at compile time.

### 1.8 `ItemDrop.SaveToZDO` / `LoadFromZDO` overloads collapsed

**Game-side:**
- `SaveToZDO(ItemData, ZDO)` and `SaveToZDO(int, ItemData, ZDO)` → single `SaveToZDO(ItemData itemData, ZDO zdo, int index = -1)`
- `LoadFromZDO(ItemData, ZDO)` (was private) and `LoadFromZDO(int, ItemData, ZDO)` → single `LoadFromZDO(ItemData itemData, ZDO zdo, int index = -1)` (now public — accessibility flip!)
- Storage flipped to a single `s_itemData` byte[] using new `ItemData.Save(ZPackage)` / `ItemData.Load(ZPackage)`. The per-field ZDOVars (`s_durability`, `s_stack`, `s_quality`, `s_variant`, `s_crafterID`, `s_crafterName`, `s_dataCount`, `s_worldLevel`, `s_pickedUp`, `data_*`, `data__*`) are no longer written on the indexed path. (Note: `s_quality`/`s_variant` are still written on the no-index path.)
- New public methods on `ItemDrop.ItemData`: `Save(ZPackage)`, static `Load(ZPackage)` returning tuple `(int prefabHash, ItemData)`, static `Load(ZPackage, ItemData)` returning `int prefabHash`.

**Mod sites:**

- [ ] **FiresNPCs** `Modules\Vault\PrefabDataFactory.cs` — confirm whether it calls `SaveToZDO`/`LoadFromZDO`. If so, update to the merged signature with `int index = -1`.
- [ ] **VAInventory / VABackpacks / VAGhettoNetworking** — no direct calls per current grep.
- [ ] **Anywhere** that reads `s_durability`/`s_stack`/etc. directly off a Container/ItemStand/Tombstone ZDO will get defaults. Flag at compile time.

### 1.9 `VisEquipment` string→int hash migration

**Game-side:**
- Private fields type-flipped (string → int): `m_leftItem`, `m_rightItem`, `m_chestItem`, `m_legItem`, `m_helmetItem`, `m_shoulderItem`, `m_beardItem`, `m_hairItem`, `m_utilityItem`, `m_leftBackItem`, `m_rightBackItem`.
- All public `Set*Item(string name, …)` flipped to `Set*Item(int itemHash, …)`. **Param renamed AND retyped.** Affects: `SetItem(VisSlot, …)`, `SetLeftItem`, `SetRightItem`, `SetRightItemVisual`, `SetLeftBackItem`, `SetRightBackItem`, `SetChestItem`, `SetLegItem`, `SetHelmetItem`, `SetShoulderItem`, `SetBeardItem`, `SetHairItem`, `SetUtilityItem`.
- `UpdateEquipmentVisuals` body rewritten with new local names (`hash1..hash8` → `leftItem1/2`, `rightItem1/2`, `chestItem`, `legItem`, `helmetItem`, `shoulderItem`, `utilityItem`, `hash`).

**Mod sites:**

- [ ] **FiresNPCs** `Modules\Companions\CompanionWeaponScaler.cs:116` — accesses VisEquipment fields by dynamic name. If it stored field values as `string`, it'll throw `InvalidCastException` on the new build. Cast to `int` and resolve via `ObjectDB.instance.GetItemPrefab(int hash)` to recover the prefab.
- [ ] **FiresNPCs** `Modules\Companions\CompanionPatches.cs:2986, 3026, 3065, 3072, 3079, 3086` patches `VisEquipment.AttachItem`/`GetModelIndex`/`UpdateVisuals`/`UpdateBaseModel`/`UpdateEquipmentVisuals`/`UpdateColors`. Method *names* are intact, but Postfix on `UpdateEquipmentVisuals` that read named locals via `__local` will miss — verify locals.
- [ ] **FiresNPCs** `UI\NpcVisEquipment.cs:112, 551` reflects `m_nview` only — unchanged. Safe.
- [ ] **FiresNPCs** anywhere that calls `vis.SetRightItem("PrefabName")` or similar must wrap in `.GetStableHashCode()`. Compile errors will surface these.

### 1.10 `ZDO.Load(ZPackage, int)` / `LoadOldFormat(ZPackage, int)` → `Version.World`

**Game-side:** param 2 retyped. `private int ReadNumItems(ZPackage, int)` (instance) → `public static int ReadNumItems(ZPackage, Version.World)` plus a new overload `public static int ReadNumItems(ZPackage)`. Visibility, instance→static, and signature changes all combined.

**Mod sites:**

- [ ] No mod patches these directly per the inventory pass. Safe.

### 1.11 `ZNet.IsDedicated()` flipped from `=> true` to `=> false`

**Game-side:** This is a **behavior change**, not a signature change. Any mod logic that branched on `ZNet.IsDedicated()` to detect a headless server will now see the *opposite* result. Also note: `FejdStartup.Awake` removed the `if (graphicsDeviceType != Null) Application.Quit()` guard.

**Mod sites:**

- [ ] **FiresNPCs** `Modules\VAngarde\VAngardeCore.cs:108`, `Modules\VAngarde\VAngardeModValidator.cs:132` reflect `ZNet.m_serverSteamID`/`m_hostSocket` — independent of `IsDedicated`. Safe.
- [ ] **VAGhettoNetworking** `Networking\ServerClientUtils.cs:83-145` — already has multi-fallback detection (probes exe name first, then `IsDedicated`, then field/property reflection). Verify the fallback chain still picks the correct branch on the new build.
- [ ] **VAGhettoNetworking** `Networking\ShipFixesGroup.cs`, `Networking\ServerAuthorityPatches.cs` and others use `IsDedicated()` to gate server-only logic. **Audit carefully** — semantics may have flipped meaning.
- [ ] **VAGhettoNetworking** `Networking\NetworkRatesGroup.cs:113-114` — Steam networking utils selection branches on `IsDedicated()`. Confirm correct branch.

### 1.12 `Inventory(bool _)` ctor added

- [ ] No mod uses reflective ctor enumeration on `Inventory`. Safe.
- [ ] If any future code reflects on the constructor, pin the parameter list.

### 1.13 `ZDO.Serialize(ZPackage)` body refactor (SAME signature, NEW wire format)

**Game-side:** `Serialize`/`Deserialize` body massively refactored to use bitflag enum `ExtraDataFlags` and `ZDODataHelper.WriteData/WriteToDisk`. Also: `DataFlags.Valid = 0x80` enum member was repurposed to `DataFlags.OutsideZones = 0x80` — same bit, different meaning. The `Valid` property body changed from `=> this.Valid` to `=> this.m_prefab != -1`. The `m_sector` field is **removed entirely**.

**Mod sites:**

- [ ] **VAGhettoNetworking** `Networking\ZDODeltaPatches.cs:82, 103` — `[HarmonyPatch(typeof(ZDO), "Serialize")]` Prefix and Postfix. The patch **replaces** vanilla serialize with a custom delta wire format and writes a header containing m_uid, prefab int, optional Vector3 euler rotation, then 6 typed lists. **Tightly coupled to the old wire format.** The chunked save / `Version.World` work in vanilla almost certainly changed the header byte layout — if your delta format and the new vanilla deserializer disagree on header bits, the receiver throws and the player ZDO desyncs.
- [ ] **VAGhettoNetworking** `Networking\PlayerPositionSyncPatches.cs:111` — `[HarmonyPatch(typeof(ZDO), "Deserialize")]` Postfix that reads `GetPosition()`/`GetRotation()` after deserialize. Should still work since signatures are unchanged, but verify the deserialize body still produces fresh position values at the postfix point.
- [ ] **VAGhettoNetworking** any code that read `ZDO.m_sector` — that field is gone. Use `GetSector() → Vector2s` or the new `GetSectorIndex() → SectorIndex`.
- [ ] **VAGhettoNetworking** any code that checked `ZDO.DataFlags.Valid` (the enum value) — gone. The bit `0x80` now means `OutsideZones`.

> This is the single highest-risk patch in our entire surface. Test the multiplayer client/server protocol thoroughly on the new build.

### 1.14 `ZDOMan.SendZDOs(ZDOMan.ZDOPeer, bool)` body changes

**Game-side:** internal save flow rewritten under the chunked save work. Signature is currently unchanged per our diff (the public diff covered Load/Save/portal APIs but didn't deeply read SendZDOs body). However, the IL inside is at risk because of the `m_objectsByOutsideSector` removal and `m_portalObjects` type change — both are referenced from `SendZDOs`-adjacent code.

**Mod sites:**

- [ ] **VAGhettoNetworking** `Networking\NetworkRatesGroup.cs:149` — Transpiler that scans IL for any `Ldc_I4*` constant >=10240 within 15 instructions of a `Callvirt` to a method named `GetSendQueueSize`. Will silently no-op (not throw) if Valheim renamed the call or restructured the loop. Validate by logging a transpiler-applied counter on first patched run; if zero applications, retune.
- [ ] **VAGhettoNetworking** `Networking\ZDODeltaPatches.cs:55, 72` — Prefix/Postfix on `SendZDOs(ZDOMan.ZDOPeer, bool)`. Relies on `ZDOMan.ZDOPeer` nested type and `peer.m_peer.m_uid` field chain. Verify ZDOPeer still exists and field chain is intact on new build.
- [ ] **VAGhettoNetworking** `Networking\ZDOThrottlingPatches.cs:28` — Postfix on `ZDOMan.ServerSortSendZDOS(List<ZDO>, Vector3)`. Verify exact signature `(List<ZDO>, Vector3)` still exists; if renamed/restructured under chunked save work, patch loses binding silently.
- [ ] **VAGhettoNetworking** `Networking\NetworkRatesGroup.cs:59` — Prefix on `SendZDOToPeers2(ref float dt)`. Private name + `ref float` parameter. If renamed under save work, patch loses binding.
- [ ] **VAGhettoNetworking** `Networking\ZDODeltaPatches.cs:129` — Postfix on `RemovePeer(ZNetPeer)`. Private name. Verify still present.
- [ ] **VAGhettoNetworking** `Networking\ZDOMemoryManager.cs:72` — `AccessTools.Method(typeof(ZDOMan), "RemoveOrphanNonPersistentZDOS")` invoked dynamically. Verify still present.

### 1.15 `ZNet.RPC_PeerInfo` Transpiler — networkVersion 36 → 37 + new privilege check

**Game-side:** body now adds a CrossPlatformMultiplayer privilege check that can short-circuit early. `networkVersion` literal flipped from `36U` to `37U` everywhere (PeerInfo, RegisterServer, RPC_PeerInfo).

**Mod sites:**

- [ ] **VAGhettoNetworking** `Networking\DedicatedServerGroup.cs:54` — Transpiler that searches IL for a `Call` to `GetNrOfPlayers`, then patches the next constant load (the player limit literal). The new privilege-check insertion may shift the `GetNrOfPlayers` call's relative position, so the transpiler's "next constant load" assumption may now grab the wrong literal. Walk the IL by hand on first patched run to verify the patch lands on the player limit, not the privilege-check constant.
- [ ] **All four mods** — anywhere that compares `ZNet.networkVersion` to a literal `36` will need to update to `37`. None spotted in current grep.

### 1.16 `ZRoutedRpc.RPC_RoutedRPC` body / `ZNet.OnNewConnection` privilege check

**Game-side:** `ZNet.OnNewConnection`/`Disconnect` signatures unchanged; bodies stable. `RPC_PeerInfo` adds early returns under privilege denial.

**Mod sites:**

- [ ] **VAGhettoNetworking** `Networking\RpcRouterPatches.cs:32` — full server-side replacement of `ZRoutedRpc.RPC_RoutedRPC(ZRpc, ZPackage)` (returns false). Signature unchanged; body change in vanilla is internal and shouldn't affect the prefix-replacement strategy.
- [ ] **VAGhettoNetworking** `Networking\CompressionGroup.cs:123, 133` — `ZNet.OnNewConnection`/`Disconnect` postfixes. Signatures unchanged. Safe.
- [ ] **VAGhettoNetworking** `Networking\CompressionGroup.cs:142-144` registers per-peer `ZRpc.Register<T>` for `"FiresGhetto.CompressionVersion"`/`"FiresGhetto.CompressionEnabled"`/`"FiresGhetto.CompressedStarted"` — verify ZRpc.Register signature stable. Safe per diff.

### 1.17 `Inventory.Save` writes version 108; cross-build save compatibility

**Game-side:** old `Save(ZPackage)` body renamed to `OldSave(ZPackage)`. New `Save` writes version `108` (was 106).

**Operational hazard:**

- [ ] **All four mods** — if a server is on the test build (writing v108 inventory packages) and a client is on live (expecting v106), the client **may corrupt or fail to deserialize the bag/inventory state**, especially for VABackpacks (which round-trips bag items via `customData["BackpackInventory"]` and re-uses the inventory format). This is a **forced upgrade window** — the patch-day rollout must update server and clients in lockstep, and any saved character profiles on disk that reference v106-format inventory data should be backed up before the first v108 boot.
- [ ] Document the migration in the player-facing patch notes when this ships.

---

## 2. 🟡 Soft breaks — body/behavior changed; signatures still bind

These won't error at startup, but logic that depended on the old body shape may misbehave. Validate on first run of the patched build.

### 2.1 `Character.SetHealth` early-out

`Character.SetHealth` now does `if (health.Equals(this.m_lastHealth)) return;` before the ZDO write. New private field `m_lastHealth` added (no impact unless reflectively enumerating `Character` fields). `Awake` strips the `s_health` ZDO key when at full HP. Body-only change; signature unchanged.

- [ ] **FiresNPCs** `Modules\Companions\CompanionPatches.cs:2206` patches `Character.SetHealth`. Audit for any logic that fires every call vs only on actual change. Postfix that assumed every call writes the ZDO will silently no-op for unchanged values.
- [ ] **VAGhettoNetworking** `Networking\WearNTearServerPatches.cs:61` reads `s_health` ZDO. The `Awake`-strips-at-max behavior means a freshly-loaded ZDO at max HP won't have the key — `GetFloat(ZDOVars.s_health, defaultValue)` should still return correct defaults; verify the default branch works.

### 2.2 `Player.Save` version bump 29 → 32; `Player.Load` body rewritten to use `Version.PlayerData` enum

Signatures unchanged. Transpilers indexing IL constants in `Load` would break, but no mod transpiles Player.Load. Postfix on `Player.Save` is safe.

- [ ] No transpilers in our set hit Player.Load. Safe.
- [ ] Profile save/load path: if a player profile was saved on test build (v32) and the user reverts to live, the old client may fail to load the profile. Players who migrate to public test must re-export character profile if they want to revert. Document if relevant.

### 2.3 `Inventory.AddItem(ItemData, int, int, int)` body change

New gating: `&& !this.m_temoraryInventory`. Body-change only; signature stable. Transpilers matching the original `if (x < 0 || y < 0 || x >= this.m_width || y >= this.m_height)` IL pattern will fail to find the pattern.

- [ ] No mod transpiles `Inventory.AddItem`. Safe.

### 2.4 `Humanoid.SetupEquipment` / `EquipItem` / `UnequipItem` body changes

Every `VisEquipment.Set*Item(string …)` call inside Humanoid is now `Set*Item(int hash …)` (because VisEquipment flipped — see §1.9). Method signatures of Humanoid itself are unchanged.

- [ ] **VAInventory** `Managers\InventoryInteraction.cs:152, 190` — `EquipItem`/`UnequipItem` postfixes still bind. Safe.
- [ ] **VAInventory** `Managers\ItemSlotValidation.cs:389-396` — `AccessTools.Method(typeof(Humanoid), "EquipItem"|"UnequipItem")` no-overload-spec. Still works as long as no new overload is added (none in this build).
- [ ] **VABackpacks** `BackpackManager.cs:443, 521, 576, 623` — same deal, prefix/postfix on EquipItem/UnequipItem. Safe.
- [ ] **VABackpacks** has the watch-for-duplicate-postfix concern from CLAUDE.md memory: the existing pair (Prefix-hijack + Postfix-event-fire) is intentional. Do NOT add a third postfix — it causes double-firing of `OnEquipItem` / `HandleEquipped`.

### 2.5 `Game.Update` / `Game.Awake` / `Game.Logout` body changes

`Update` now does respawn flow before reference-position update. `Logout` now passes `true` (wait for save) instead of `false`. `Awake` constructs `PlayerProfile(name, FileSource)` directly.

- [ ] All four mods have many `Game.Logout` and `Game.Start` postfixes. Signatures stable. **Behavior change is "logout is now blocking on save"** — flag for telemetry/Discord/save-cleanup patches that relied on quick logout. The companion teleport-suppression ZDO writes during shutdown may now race against the chunked save loop (see §6).

### 2.6 `WearNTear.HaveSupport` and adjacent IL local renumbering

Signature-only patches still bind. No mod transpiles these. Safe. New early-return guards comparing `this.m_support.Equals(maxSupport)` before mutating ZDO support.

- [ ] **VAGhettoNetworking** `Networking\WearNTearServerPatches.cs:37` patches `UpdateWear` (private) — different method than `HaveSupport`. Signature stable. Safe.

### 2.7 `ZNetView.SyncScale(bool skipOne = false)` → `SyncScale()`

Optional bool param removed. No longer writes `s_scaleScalarHash`. New private field `m_lastLocalScale` added.

- [ ] No mod patches `SyncScale`. Safe.
- [ ] **VAGhettoNetworking** anywhere reading `s_scaleScalarHash` from a ZDO — that key is no longer written by ZNetView. None spotted.

### 2.8 `Minimap` private static field rename `m_instance` → `s_instance`; texture path field renames; `m_explored` BitArray flip

Public property `instance` still works. None of the mods touch the static field by name. Other Minimap private fields referenced by FiresNPCs (`m_mode`, `m_mapTexture`, `m_forestMaskTexture`, `m_mapImageSmall`, `m_largeZoom`, `m_pinUpdateRequired`) are unchanged. `m_explored` / `m_exploredOthers` flipped from `bool[]` to `BitArray` — not in our reflection set.

- [ ] No mod hits any of the renamed fields. Safe.
- [ ] **TryLoadMinimapTextureData / SaveMapTextureDataToDisk** signatures changed — not in our reflection set. Safe.

### 2.9 `ZNet` public field `m_zdoSectorsWidth` removed; replaced by `private const c_ZDOSectorsWidth = 512`

No mod references `m_zdoSectorsWidth`. Safe.

### 2.10 `ZoneSystem.RemoveGlobalKey(GlobalKeys)` body change

Was `key.ToString()`, now `key.ToString().ToLower()` — packet payload differs. RPC handler param type unchanged but value sent over wire is lowercased now.

- [ ] **FiresNPCs** `Modules\Progression\ProgressionPatches.cs:130` patches `RPC_RemoveGlobalKey` — verify the prefix/postfix isn't case-comparing with a non-lowercased literal.
- [ ] All other patch sites use `SetGlobalKey(string)` / `GetGlobalKey(string)` directly — those don't go through the lowercasing path. Safe.

### 2.11 `Player` save version bump (also see §2.2) — `EquipInventoryItems` body audit

`Player.Save` writes v32 (was v29) and Load uses `Version.PlayerData` enum. `EquipInventoryItems` body unchanged in diff per inventory pass.

- [ ] **FiresNPCs** `Modules\Progression\ProgressionPatches.cs:157` patches `EquipInventoryItems` — signature stable, behavior should still match. Safe.

### 2.12 `Inventory.Save` v108 body — old body preserved as `OldSave`

Diff shows `Save` was renamed to `OldSave` and a new `Save` was added. If any mod does `MethodInfo.GetMethod("Save")` early and assumes it's the v106 writer, it now hits the v108 writer.

- [ ] No mod uses raw `GetMethod("Save")` in our four-mod set. Safe.

### 2.13 `World.m_fileName` → `World.m_worldName` rename across vanilla codebase

`FejdStartup` and `ZNet.Save` body refer to it now. Pure vanilla body change; doesn't affect our mods unless one of them reflects on `World.m_fileName` directly.

- [ ] None of the four mods reflect on `World.m_fileName`. Safe.

### 2.14 `Game.FindRandomUnconnectedPortal` filter now also checks `GetConnectionZDOID(Portal) == ZDOID.None`

Additional filter condition; method signature unchanged.

- [ ] No mod patches `FindRandomUnconnectedPortal`. Safe.

---

## 3. 🟢 Confirmed safe (validated)

These were on the patch surface but the diff showed no relevant change. Listed here so we have a ground truth — do NOT spend time re-checking these on patch day unless something else breaks.

### Inventory / UI surface
- All **`InventoryGui`** patches (Show, Hide, OnDestroy, Update, UpdateContainer, UpdateInventory, UpdateItemDrag, OnSelectedItem, OnDropOutside, SetupDragItem, BeginDragItem, EndDragItem, IsContainerOpen, IsVisible) and private fields (`m_dragItem`, `m_dragGo`, `m_dragInventory`, `m_dragAmount`, `m_currentContainer`, `m_availableRecipes`, `m_recipeElementPrefab`, `m_splitPanel`, `m_selectedItem`, `m_moveItemEffects`, `m_firstContainerUpdate`).
- All **`InventoryGrid`** patches and the nested `Element` type with `m_go`/`m_icon`/`m_amount`. Enum `Modifier` (`Select, Split, Move`) intact.
- All **`Humanoid`** equipment-slot fields (`m_rightItem`, `m_leftItem`, `m_chestItem`, `m_legItem`, `m_helmetItem`, `m_shoulderItem`, `m_ammoItem`, `m_utilityItem`, `m_trinketItem`) — these hold `ItemDrop.ItemData` references, NOT the visual identifiers that VisEquipment renamed.
- `Humanoid.IsItemEquiped` (vanilla typo preserved).
- All **`Player`** patched signatures (Awake, OnSpawned, OnDeath, Update, Save, EquipInventoryItems, TakeInput, SetControls, UpdatePlacement, Interact, TryPlacePiece, PlacePiece, UpdateTeleport, TeleportTo, CreateDeathEffects, UpdateDodge, PlayerAttackInput, PieceRayTest, FindClosestSnapPoints, OnInventoryChanged, OnDestroy).
- All **`Container`** signatures (Awake, OnDestroyed, Interact, TakeAll, CheckAccess, GetHoverText) and fields (`m_inventory`, `m_height`, `m_width`, `m_name`).
- `ItemDrop.Interact`, public `m_itemData`, static `DropItem(ItemData, int, Vector3, Quaternion)`. Nested `ItemData` public fields (`m_shared`, `m_dropPrefab`, `m_stack`, `m_gridPos`, `m_customData`, `m_quality`, `m_variant`, `m_equipped`) and methods `GetIcon`, `GetWeight`. Enum `ItemType` values.
- `ObjectDB.CopyOtherDB`, `m_items`, `m_itemByHash`, `instance`, `GetItemPrefab(string)`.

### AI / combat surface
- All **`MonsterAI`/`BaseAI`/`AnimalAI`** patched methods and private fields.
- All **`Tameable`** patched methods.
- All **`EnemyHud`** patches (`UpdateHuds`, `ShowHud`, `TestShow`) and `m_huds` dictionary.
- `SEMan.AddStatusEffect(int hash, bool, int, float)`, `Character` property, `m_character` field.
- `StatusEffect` and the `SE_*` derived chain.
- `Aoe` private fields (`m_radius`, `m_damage`, `m_ttl`).
- `ArcheryTarget.OnProjectileHit`, `ProjectileHit`.
- `Attack.Start`.
- `CharacterAnimEvent` private fields.
- `CharacterDrop.GenerateDropList`.
- All **`Character`** signatures (modulo the `SetHealth` body change in §2.1) and private fields.

### World / terrain surface
- All **`Heightmap`** patches (Awake, Initialize, Regenerate, RebuildRenderMesh, GetBiome, FindBiomeClutter, GetWorldHeight, GetHeight, IsCultivated) and private fields (`m_cornerBiomes`, `m_renderMesh`, `s_tempColors`, `m_isDistantLod`).
- **`TerrainComp`** transpiler in `Modules\HeightmapOverride\HeightmapOverridePatches.cs` — `LevelTerrain`, `RaiseTerrain`, `ApplyToHeightmap` `-8f`/`+8f` Ldc_R4 literals are byte-identical between live and test. **No transpiler retune needed.** Verified count and signedness.
- All **`WorldGenerator`** patches (Pregenerate, GetBiome, GetRiverWeight) and private fields (`m_rivers`, `m_streams`, `m_lakes`, `m_world`).
- `EnvMan` patches and private fields (`m_forceEnv`, `m_debugEnv`).
- `ClutterSystem`, `Bed`, `Beehive`, `CookingStation`, `CraftingStation`, `Door`, `Fireplace` (all patches and private fields), `Fish`, `Floating`, `FootStep`, `ItemStand` (the patched methods — see §4.1 for separate `ItemStand` RPC drift), `Leviathan`, `Plant.HaveGrowSpace`/`UpdateHealth`, `Smelter` (and `m_waterLevel` reflection), `Ship`, `SpawnSystem`, `TeleportWorld`, `TombStone`, `WaterVolume`, `Skills.OnDeath`, `Piece.CanBeRemoved`.
- `RandEventSystem.RefreshPlayerEventData`, `HaveGlobalKeys`, `SetActiveEvent`.

### Networking surface
- `ZNet`: Awake, Start, Update, OnDestroy, Shutdown, SaveWorld, OnNewConnection (incl. ZNetPeer overload), Disconnect (incl. ZNetPeer overload), RPC_PeerInfo (signature stable; body adds privilege check — §1.15), RPC_CharacterID, RPC_Disconnect, GetWorldName, property `World`, all referenced fields (`m_adminList`, `m_peers`, `m_serverSteamID`, `m_hostSocket`).
- `ZNetScene`: Awake, OnDestroy, RemoveObjects, CreateDestroyObjects, Destroy, GetPrefab(string), HasPrefab, m_namedPrefabs, m_prefabs (note: there is NO `m_prefabsByHash` in either build — that was a doc error in the inventory).
- `ZNetView`: Awake, IsValid, GetZDO, Destroy, IsOwner, ClaimOwnership.
- `ZNetPeer`: m_rpc, m_uid, m_server, IsReady — all unchanged.
- `ZRoutedRpc`: m_peers, Register, InvokeRoutedRPC, instance, Everybody — all unchanged.
- `ZRpc`: HandlePackage, Register, Invoke — all unchanged.
- `ZPackage`: ctors, Write*, Read*, GetBase64, GetArray. New methods (`GetCompressed`, `Decompress`, `WriteSmallRotation`, `ReadSmallRotation`, `Flush`) are additive. Note: `m_stream` is now `readonly` — code that *reassigns* m_stream via reflection breaks (none in our set).
- `ZDOID`: UserID, ID, None — all unchanged.

### Other
- All **`Chat`**, **`Hud`**, **`HotkeyBar`**, **`Menu`**, **`SkillsDialog`**, **`UnifiedPopup`**, **`MessageHud`** patches.
- All **`MessageType`**, `InventoryGrid.Modifier`, `ItemDrop.ItemData.ItemType` enum values.
- BepInEx surface (`ConfigEntryBase.GetSerializedValue`/`SetSerializedValue`, `ConfigDescription.<Tags>k__BackingField`) — out of scope for this diff but confirmed unchanged in BepInEx side.
- `Localization.m_translations`, `Localize(string)`, `instance` (file lives in satellite assembly — runtime verify).
- `UITooltip.Set` (file lives in `assembly_guiutils.dll` — runtime verify).
- `ZSFX.m_loop`.
- `InstanceRenderer.m_instanceData`.
- `HitData.DamageTypes.m_pickaxe`.
- `Minimap` patches (Start, Update, OnMapLeftClick, OnMapRightClick, OnMapMiddleClick, SetMapMode, LoadMapData, GenerateWorldMap, UpdatePlayerPins) and reflected fields (m_mode, m_mapTexture, m_forestMaskTexture, m_mapImageSmall, m_largeZoom, m_pinUpdateRequired).
- `Terminal` patches (Awake, InitTerminal, TryRunCommand) — diff is in unrelated method bodies only.

---

## 4. Adjacent breaks (NOT directly patched, but call sites in our mods may use them)

### 4.1 `Fermenter` and `ItemStand` string→int hash migration

- `Fermenter.RPC_AddItem(long, string)` → `(long, int nameHash)`
- `Fermenter.IsItemAllowed(string)`/`GetItemConversion(string)`/`GetContent()` flipped to int
- `Fermenter.m_delayedTapItem` field type `string ""` → `int`
- `ItemStand.RPC_SetVisualItem(string,…)` → `(int,…)`; `GetAttachedItem()` returns `int`; `m_visualName: string` → `m_visualHash: int`
- ZDO storage of `s_content` (Fermenter) and `s_item` (ItemStand) is now int

**Mod sites:**

- [ ] None of the four mods directly invoke these RPCs per the inventory pass.
- [ ] **FiresNPCs** if anywhere it synthesizes ItemStands for trophy/loot displays via `RPC_SetVisualItem`, recheck.

### 4.2 `ZoneSystem.c_ZoneHalfSize` → `c_ZoneSizeHalf` constant rename

- [ ] No mod references either symbol per the inventory. Safe.

### 4.3 `ZoneSystem.m_pgwVersion` public field removed

- [ ] No mod references it. Safe.

### 4.4 `Game.m_profileFilename`/`m_profileFileSource` static fields unchanged

Body of `Game.Awake` rewritten to consume them differently. No reflection drift.

### 4.5 `Game.SpawnPlayer` signature unchanged; `OnApplicationQuit` signature unchanged

Bodies stable enough. Safe.

### 4.6 `FejdStartup.OnCharacterStart` body restructured

Wraps the index-out-of-range check around the body now. Signature unchanged. No mod patches it.

### 4.7 `PlayerProfile.RemoveProfile(name, source)` two-arg → `RemoveProfile(name)` one-arg

Adjacent vanilla API change. No mod calls `RemoveProfile`. Safe.

### 4.8 `FejdStartup.GetMoveTarget(FileHelpers.FileSource)` removed

No mod references. Safe.

### 4.9 `Game` portal connection now uses `GetPortalList()` instead of `GetPortals()`

Vanilla body change reflecting `ZDOMan.GetPortals` return-type flip. No mod patches portal logic. Safe.

---

## 5. Files added / removed in `assembly_valheim`

**Added (test build):**
- `ChunkSaveMapping.cs` — new helper for chunked saves (used by ZDOMan). Public members: `Clone`, `CreateOrUpdate(ChunkIndex)`, `Get(ChunkIndex)`, `GetNumZDOs(uint, uint, byte)`, `RevertChunkVersion`, `GetChunkOldFilename`, `GetChunkFilename`, `Load(saveDirectory, FileSource)`, `Save(saveDirectory, FileSource) → bool`, `GetNumChunks`, `GetNumZDOs`, `Chunks` property. Nested class `ChunkInfo` with `m_chunkIndex` (readonly), `m_version` (uint), `m_numZDOs` (int), `m_sizeChanged` (bool).
- `SaveFileHelper.cs` — `SaveWrite`/`SaveReader` IDisposables wrapping ZPackage + FileWriter. Uses `BufferedStream` with 1 MB buffer (`1048576`).

Neither is patched by any of the four mods, but they signal the new save-flow architecture (see §1.2). If we ever want to plug into the chunked save flow, these are the new entry points.

**Removed (test build):**
- `PlayFabAuthWithCustomID.cs`
- `ZDOComparer.cs`

No mod references either.

**Not present in either source dump (likely live in `assembly_guiutils.dll` or other satellite assemblies):**
- `Cover.cs` (the `Cover.GetCoverForPoint` static lives in a satellite)
- `ZInput.cs`
- `Localization.cs`
- `UITooltip.cs` (per CLAUDE.md memory: `assembly_guiutils.dll`, with 4-arg `Set` in current build)

For these, on patch day, verify the satellite DLL hasn't drifted by checking the relevant patch sites still bind:
- **VAInventory** `Managers\HotbarManager.cs:32-46` — `ZInput.TryGetButtonState(string,…)` runtime overload pick
- **FiresNPCs** `UI\Patches\UITooltipStyleOverride.cs` — `UITooltip.Set` (4-arg per memory; the mod uses 4-arg too, confirmed)
- **VABackpacks** `BackpackCampWoodPile.cs` — `Localization.instance.Localize(string)`

---

## 6. Spawn-gate / load-timing concerns

Per project memory: **PlayerSpawnGate (player customData) + AreCompanionTeleportsSuppressed (companion ZDO/RPC) deadlock the zone-stream if writes happen during the closed window.** The save-system overhaul changes when that window closes.

Specific timing-sensitive surfaces in the patched code:

- [ ] `ZNet.LoadWorld()` return type flip and chunked-loader internals — verify spawn gate opens AFTER all chunks load, not just after `Awake`. The new `LoadWorld()` does staged load → `LoadChunks` → `LoadOldWorld` fallback. The bool return value tells you success/failure, but doesn't tell you when the world is "fully ready".
- [ ] `ZDOMan.SaveAsync` removal: check whether `Game.Logout` (now blocking on save with `SavePlayerProfile(true)`) holds the save gate longer; `m_dirtyFiles` count is exposed via `NumDirtySaveFiles()`. The companion teleport-suppression ZDO writes during shutdown may now race against the chunked save loop.
- [ ] `Player.OnSpawned` postfixes (7 in FiresNPCs) — should still fire at the same logical point; just verify on first patched run.
- [ ] `Game.Update` body now does respawn-flow before reference-position update — possibly affects the order of player customData reads vs. respawn detection.
- [ ] `Character.SetHealth` early-out (§2.1) — companion respawn-after-death heuristics that watched for any SetHealth call (even no-op) must be retuned.
- [ ] `ZNet.IsDedicated()` flipped (§1.11) — any spawn-gate logic that branched on dedicated vs listen-server may now take the wrong branch. Audit the gate's dedicated-server code path.

**Validation plan:** on first patched run, log timestamps for: `ZNet.LoadWorld` start/end, first `ZDOMan` chunk load, `Player.OnSpawned`, first companion respawn RPC. If the gap between LoadWorld-end and Player.OnSpawned-start has changed materially, the spawn gate may need its open-condition retuned.

**New observability surface:** `ZDOMan.NumSaveFiles()`, `NumDirtySaveFiles()`, `SetDirtySector(...)`, `SetDirtyPortals(...)`, `EndSave(...)`, nested enum `SaveState`. Useful for instrumenting load-timing telemetry without reflection.

---

## 7. VAGhettoNetworking — dedicated section

VAGhettoNetworking sits at the intersection of every change in this release. It patches `ZDO.Serialize`/`Deserialize` (replacing the wire format), runs three IL transpilers (`SendZDOs` queue limit, `RPC_PeerInfo` player-count constant, plus the WackyDatabase compat), and reaches into `ZDOMan` private state. Its risk profile is the highest of the four mods.

### 7.1 RPC names registered

Used to build the binary protocol with itself across nodes. On patch day, verify these strings still hash to non-colliding `GetStableHashCode()` values vs vanilla's expanded RPC set:

- `"ForceUpdateZDO"` — `Ascend.cs:568` (no-op stub registered with `ZRoutedRpc.instance.Register`)
- Per-peer ZRpc registrations: `"FiresGhetto.CompressionVersion"`, `"FiresGhetto.CompressionEnabled"`, `"FiresGhetto.CompressedStarted"` — `CompressionGroup.cs:142-144`
- Outbound `ZRpc.Invoke("RoutedRPC", …)` — `RoutedRpcManager.cs:149, 190`
- Routed-RPC names intercepted: `"DamageText"`, `"RPC_HealthChanged"`, `"RPC_WNTHealthChanged"`, `"RPC_SetTarget"`, `"AddNoise"`, `"TriggerAnimation"`, `"OnDeath"`, `"Say"`, `"SpawnedZone"` (registered via `RoutedRpcManager.AddHandler`).

### 7.2 Hard breaks summary (VAGhetto-specific)

| Site | Break | Fix |
|---|---|---|
| `Networking\ServerAuthorityPatches.cs:63, 107, 198` | `ZoneSystem.GetZone(Vector3)` returns `Vector2s` not `Vector2i` | Type the return as `Vector2s` |
| `Networking\ServerAuthorityPatches.cs:64` | `ZDOMan.FindSectorObjects(Vector2i, …)` param 1 may be `Vector2s` | Update to `Vector2s` |
| `Networking\ServerAuthorityPatches.cs:100, 113` | `m_zones.ContainsKey(new Vector2i(x, y))` — key type changes | Use `Vector2s` (or `SectorIndex` if dictionary key flipped further) |
| `Networking\ServerAuthorityPatches.cs:185` | `[HarmonyPatch(typeof(ZNetScene), "OutsideActiveArea", new[]{ typeof(Vector3) })]` overload-pinned | Verify single-Vector3 overload still exists |
| `Networking\ServerAuthorityPatches.cs:198` | Direct `ZNetScene.OutsideActiveArea(Vector3, Vector2i, int)` | Update to `Vector2s` |
| `Networking\MonsterAiPatches.cs:372, 379` | `ZoneSystem.GetZone(Vector3)` returning `Vector2i` | Type as `Vector2s` |
| `Networking\MonsterAiPatches.cs:380` | Direct `ZNetScene.InActiveArea(Vector2i, Vector2i, int)` static call | Update to `Vector2s` |
| `Networking\MonsterAiPatches.cs:373` | Reads `ZoneSystem.m_zones` (publicized) — key type changes | Update key type |
| `Networking\RpcHandlers\SpawnedZoneHandler.cs:79` | `ZoneSystem.GetZonePos(Vector2i)` direct call | Update to `Vector2s` |
| `Networking\ZDODeltaPatches.cs:82, 103` | `ZDO.Serialize` Prefix replaces vanilla wire format | **Highest-risk patch in mod.** Verify the new vanilla serialize format and re-author the delta wire format to match. |

### 7.3 Soft breaks summary (VAGhetto-specific)

| Site | Risk | Action |
|---|---|---|
| `Networking\NetworkRatesGroup.cs:149` | Transpiler scans IL for `Callvirt → GetSendQueueSize` then constant >=10240. May silently no-op if call structure changed under chunked save work. | Add a transpiler-applied counter; log on first run; retune pattern if zero applications. |
| `Networking\DedicatedServerGroup.cs:54` | Transpiler scans IL for `Call` to `GetNrOfPlayers` then patches next constant load. New privilege check insertion may shift the call's relative position. | Walk the IL by hand on first patched run. |
| `Networking\ZDODeltaPatches.cs:55, 72` | Prefix/Postfix on `SendZDOs(ZDOMan.ZDOPeer, bool)` — depends on `ZDOMan.ZDOPeer` nested type and `peer.m_peer.m_uid` field chain | Verify ZDOPeer still exists post-chunked-save refactor. |
| `Networking\ZDOThrottlingPatches.cs:28` | Postfix on `ZDOMan.ServerSortSendZDOS(List<ZDO>, Vector3)` — exact signature dependency | Verify method still exists with `(List<ZDO>, Vector3)` signature. |
| `Networking\NetworkRatesGroup.cs:59` | Prefix on `SendZDOToPeers2(ref float dt)` — private name + ref param | Verify still present. |
| `Networking\ZDODeltaPatches.cs:129` | Postfix on `RemovePeer(ZNetPeer)` — private name | Verify still present. |
| `Networking\ZDOMemoryManager.cs:55, 79` | Reflection on `ZDOMan.m_objectsByID` (`Dictionary<ZDOID, ZDO>`) | Field still present per diff. Verify type unchanged. |
| `Networking\ZDOMemoryManager.cs:72` | `AccessTools.Method(typeof(ZDOMan), "RemoveOrphanNonPersistentZDOS")` invoked dynamically | Verify still present. |
| `Networking\PlayerPositionSyncPatches.cs:111` | Postfix on `ZDO.Deserialize(ZPackage)` reads `GetPosition()`/`GetRotation()` | Signature stable. Body may push/pop different positions; verify on first run. |
| `Networking\MonsterAiPatches.cs:249-253` | Reflection cache on `RandEventSystem.m_randomEvent`/`m_forcedEvent`/`m_activeEvent` + private methods `SetActiveEvent(RandomEvent, bool)`, `IsAnyPlayerInEventArea(RandomEvent)` | Verify all 5 lookups still resolve on new build. |
| `Networking\ShipFixesGroup.cs:94, 101` | `AccessTools.Field(typeof(Ship), "m_speed").SetValue(...)` + `m_rudderValue` | Field names stable per diff. Safe. |
| `Networking\CompressionGroup.cs:259-261, 270` | `[HarmonyPatch(typeof(ZSteamSocket), "SendQueuedPackages")]` Prefix using publicized `___m_sendQueue` (Queue<byte[]>); `Recv` Postfix | ZSteamSocket file not extracted in source dump — runtime verify. |
| `Networking\NetworkRatesGroup.cs:141` | `ZSteamSocket.RegisterGlobalCallbacks` Postfix | Same: runtime verify. |
| `Networking\CompressionGroup.cs:296` | `AccessTools.Field(typeof(ZRoutedRpc), "m_peers")` cast to `List<ZNetPeer>` | Field unchanged per diff. Safe. |

### 7.4 Risky reflection sites (VAGhetto)

These are reflection lookups whose target may quietly disappear under the save-system refactor:

1. `ZDOMan.m_objectsByID` field reflection at `ZDOMemoryManager.cs:55, 79` — verify field still exists and is still `Dictionary<ZDOID, ZDO>`.
2. `ZDOMan.RemoveOrphanNonPersistentZDOS` method reflection at `ZDOMemoryManager.cs:72` — verify method still exists.
3. `RandEventSystem` 5-member reflection cache at `MonsterAiPatches.cs:249-253` — verify all 5 names resolve.
4. `ZRoutedRpc.m_peers` field reflection at `CompressionGroup.cs:296` — already verified safe per diff.
5. `Ship.m_speed` / `m_rudderValue` field reflection at `ShipFixesGroup.cs:94, 101` — already verified safe per diff.

### 7.5 Dormant code (excluded from `<Compile>`)

`ClientLogRelay\*` is excluded from the build per `Ascend.cs:111-118`. Its patches (`ZNet.Awake`, `ZNet.RPC_PeerInfo`, `ZNet.Disconnect`) and Steamworks reflection are all dormant. Listed for completeness in case the team re-enables before patch day:

- `ClientLogRelay\ClientLogRelayIntegration.cs:327, 379, 931`
- `ClientLogRelay\ClientLogRelayIntegration.cs:1117-1135` — `Steamworks.SteamAPI.IsSteamRunning`, `Steamworks.SteamUser.GetSteamID`, `m_SteamID` field

If re-enabled, all three ZNet patches participate in the §1.15 networkVersion 36→37 + privilege-check changes.

### 7.6 ZSteamSocket / ZPlayFabSocket runtime verification

These files were not extracted in either source dump (they may live in a separate decompiler output). VAGhetto patches ZSteamSocket directly; run a smoke test to confirm the `___m_sendQueue` triple-underscore field accessor still binds:

- [ ] `Networking\CompressionGroup.cs:259-261, 270` — `[HarmonyPatch(typeof(ZSteamSocket), "SendQueuedPackages")]` Prefix
- [ ] `Networking\CompressionGroup.cs:270` — `[HarmonyPatch(typeof(ZSteamSocket), "Recv")]` Postfix returning `ref ZPackage __result`
- [ ] `Networking\NetworkRatesGroup.cs:141` — `[HarmonyPatch(typeof(ZSteamSocket), "RegisterGlobalCallbacks")]` Postfix

VAGhetto does NOT patch `ZPlayFabSocket` despite having a `REFERENCE_ZPlayFabSocket.md` doc. Safe.

### 7.7 ZDO wire format — the elephant in the room

The single highest-risk decision for patch day:

**`Networking\ZDODeltaPatches.cs:82, 103`** patches `ZDO.Serialize(ZPackage)` Prefix and Postfix to replace vanilla's serialize body with a custom delta wire format. The vanilla `ZDO.Serialize`/`Deserialize` body has been refactored to use a new `ExtraDataFlags` bitflag enum and a `ZDODataHelper.WriteData/WriteToDisk` helper. Even though the method *signatures* are identical, the **wire format produced by vanilla and the wire format produced/consumed by VAGhetto must agree** for cross-node sync to work.

Before shipping VAGhetto on the new build:

1. Read the new `ZDO.Serialize`/`Deserialize` body in full.
2. Diff the header byte layout (DataFlags, prefab int, rotation form) against VAGhetto's delta header in `ZDODeltaPatches.cs`.
3. If the layout changed — and it almost certainly did — update the delta serializer to match.
4. Run a 2-node cross-test: client and server both on test build with VAGhetto. Spawn a player, walk around, drop an item, kill a monster. ZDO desync would manifest as items/creatures appearing in the wrong place or not at all.
5. Run a 1-node smoke test: host-and-play. Save and reload. Verify ZDOs persist correctly through the new chunked save format.

`ZDOExtraData.GetFloats/GetVec3s/GetQuaternions/GetInts/GetLongs/GetStrings/GetByteArrays/GetConnection(ZDOID)` static helpers (8 methods used at `ZDODeltaPatches.cs:141-148, 248-252`) must also still resolve. If `ZDOExtraData` was internally restructured, all 8 lookups break in unison.

---

## 8. Pre-patch-day checklist

Walk this list when public test promotes to live and we begin updating refs.

### Build prep

- [ ] Replace `Refs/assembly_valheim.dll` in all four mod projects with the public-test version
- [ ] Try a clean build of each mod. The compiler will surface all `Vector2i` → `Vector2s` mismatches and any reflective-method-not-found errors.
- [ ] **Do not** silence compile errors with `dynamic` casts or try/catch wrappers — fix each one against the new game type.
- [ ] Diff your `Refs/` folder's other DLLs (`assembly_guiutils.dll`, etc.) too — `Cover`/`ZInput`/`Localization`/`UITooltip` live there and we couldn't diff them at source.

### Per-mod pass

#### FiresNPCs (FiresRPGmaker)
- [ ] §1.1 Vector2i→Vector2s in `Modules\CustomWater\River\AutoWaterPatches.cs:739` (ZoneSystem.SpawnZone Prefix)
- [ ] §1.1 Project-wide grep for `Vector2i` in `Modules\CustomTerrain\*` and `Modules\CustomWater\*`
- [ ] §1.3 ZNet.LoadWorld return-type bool fixup at `Modules\Discord\DiscordIntegrationPatches.cs:41`
- [ ] §1.4 ZoneSystem.Load param-rename + Version.World retype at `Modules\Progression\ProgressionPatches.cs:184`
- [ ] §1.8 ItemDrop SaveToZDO/LoadFromZDO collapse — audit `Modules\Vault\PrefabDataFactory.cs`
- [ ] §1.9 VisEquipment string→int in `Modules\Companions\CompanionWeaponScaler.cs:116`; verify `CompanionPatches.cs:2986-3086` Postfixes still bind
- [ ] §1.11 `ZNet.IsDedicated()` audit — `VAngardeCore.cs:108`, `VAngardeModValidator.cs:132`
- [ ] §2.1 Character.SetHealth early-out audit at `Modules\Companions\CompanionPatches.cs:2206`
- [ ] §2.10 RPC_RemoveGlobalKey lowercase audit at `Modules\Progression\ProgressionPatches.cs:130`
- [ ] §6 Spawn-gate timing validation on first patched run (companion respawn after death; companion teleport suppression during world load)

#### VAInventory
- [ ] §1.6 Pin Inventory.Load overload at `Managers\CoinPurseStackPatch.cs:191`
  ```csharp
  [HarmonyPatch(typeof(Inventory), nameof(Inventory.Load), new[] { typeof(ZPackage) })]
  ```
- [ ] §1.6 Verify `m_temoraryInventory` typo preserved if reflectively accessed (none in current grep)
- [ ] §2.4 Verify EquipItem/UnequipItem overload selection still picks the historical method (regression test: equip/unequip an item, watch logs for double-fire)

#### VABackpacks
- [ ] §1.6 Direct `Inventory.Save(ZPackage)` / `Load(ZPackage)` calls in `BackpackManager.cs` and `BackpackCampManager.cs` — confirm overload resolution still picks single-arg
- [ ] §2.4 Same Equip/Unequip duplicate-fire watch as VAInventory — `Ascend.cs:280-293` carries the comment confirming the historical fix; **do NOT add a third Equip/Unequip postfix**
- [ ] Verify `Humanoid.m_rightItem` access at `Camp\HoboStickAttackPatch.cs:61` still compiles (the Refs publicization that lets `player.m_rightItem` resolve as public)
- [ ] Verify `Humanoid.IsItemEquiped` typo preserved (vanilla still has it per diff)

#### VAGhettoNetworking
- [ ] §1.1 Vector2i→Vector2s in 9 sites (see §7.2 table for exact lines)
- [ ] §1.11 `ZNet.IsDedicated()` audit — confirm `ServerClientUtils.cs` exe-name fallback still picks correct branch; review all `IsDedicated()` call sites
- [ ] §1.13 `ZDO.Serialize` / `ZDO.Deserialize` wire format — **highest priority**. Read new vanilla body, update delta wire format, run 2-node cross-test
- [ ] §1.14 Validate transpilers: `SendZDOs` queue-size pattern + `RPC_PeerInfo` GetNrOfPlayers pattern — log applied-counts on first run
- [ ] §1.15 networkVersion 36→37 effect on `RPC_PeerInfo` transpiler — IL walk-through
- [ ] §7.1 RPC name hash collision check — verify all 12 names still hash unique vs vanilla's expanded set
- [ ] §7.4 5 reflection lookups verified
- [ ] §7.6 ZSteamSocket runtime verify — 3 patches still bind on new build
- [ ] §7.7 ZDOExtraData 8-method lookup resolves
- [ ] Verify `ZDOMan.ZDOPeer` nested type still exists with `m_peer` field

### Smoke tests after build

- [ ] Boot game, host a fresh world. `ZNet.LoadWorld` returns successfully.
- [ ] Spawn in. `Player.OnSpawned` fires once (not zero, not twice).
- [ ] Companion summon → equip an item → unequip. No double-firing of `OnEquipItem`.
- [ ] Open inventory → drag bag (VABackpacks bag) → place in container → close → reopen. Bag state round-trips via `customData["BackpackInventory"]`.
- [ ] Save → exit to menu → reload world. Zones load via the new chunk path; companions respawn correctly.
- [ ] Test multi-player (the most exposed path):
  - Both nodes on test build with VAGhetto active
  - Both clients negotiate `"FiresGhetto.CompressionVersion"` correctly at handshake
  - Player position sync stays smooth
  - Spawn/despawn a creature near the seam — ZDOs propagate
  - ZDO wire format intact (no desync between client/server views)
  - Network version literal `37` appears in handshake logs
- [ ] Companion teleport suppression doesn't deadlock the zone-stream during world load (per CLAUDE.md spawn-gates memory)
- [ ] First chunked save: confirm `ChunkSaveMapping` files written under save directory; reload world; saves round-trip

### Telemetry to review

- [ ] Discord integration `LoadWorld` postfix message still fires (now fires with `bool __result` rather than `void`)
- [ ] Client log relay still attaches at `ZNet.OnNewConnection` (FiresNPCs version, not VAGhetto's dormant copy)
- [ ] Config sync registers RPCs at `ZNet.Awake` postfix on both client and server
- [ ] VAGhetto compression handshake completes within first 5 seconds of peer connect
- [ ] Spawn-gate close→open window stays under expected SLA (project memory says writes during the closed window deadlock the zone-stream — measure the window duration before/after)

---

## 9. Open questions / unverified

- [ ] **Cover.cs / ZInput.cs / Localization.cs / UITooltip.cs / ZSteamSocket.cs / ZPlayFabSocket.cs** could not be diffed at source (live in satellite assemblies). Spot-check those entry points on first patched run.
- [ ] **`ZDOMan.FindSectorObjects` signature** — likely `Vector2i → Vector2s` per the broader migration but our diff agent didn't read this method's body. Confirm signature shape on patch day.
- [ ] **`ZDOMan.ServerSortSendZDOS`** — exact signature `(List<ZDO>, Vector3)` not directly diffed; agent flagged as "verify". Confirm on patch day.
- [ ] **`ZDOMan.SendZDOs(ZDOPeer, bool)` body** — the chunked save work likely changed it but our diff didn't deep-read. VAGhetto's transpiler + Pre/Post will desync if body changed substantially.
- [ ] **`ZDOMan.ZDOPeer` nested type** — verify still exists, still has `m_peer.m_uid` field chain.
- [ ] **`ZDOExtraData` 8-method API + `ConnectionType` enum + `m_type`/`m_target` fields** — VAGhetto reads all of these in `ZDODeltaPatches.cs`. Confirm none renamed/removed under save refactor.
- [ ] **`ChunkSaveMapping.cs` / `SaveFileHelper.cs`** — newly added in test. If we ever want to plug into the chunked save flow (e.g. for backup/restore mods), these are the entry points.
- [ ] **`Inventory.AddItem(int prefabHash, ItemDrop.ItemData)`** and **`AddItem(int prefabHash, int, float, …)`** are NEW overloads. Reflective `AccessTools.Method(typeof(Inventory), "AddItem")` lookups without an explicit Type[] become ambiguous. None spotted in current grep but verify on patch day.
- [ ] **`Inventory.Save` writes version 108** — if anything saves a server-side inventory in the old format and a new client loads it (or vice versa during the public-test→live transition), bag/inventory state may corrupt. Recommend full-save migration on first patched server boot.
- [ ] **`m_temoraryInventory` (vanilla typo)** — preserve the typo in any reflective access; it WILL be the actual field name.
- [ ] **`OldSave(ZPackage)` ghost method** — vanilla preserves it for migration. If we ever need to handle v106 saves, call `OldSave` directly. Not currently relevant.
- [ ] **`ZDO.DataFlags.Valid` enum member** — repurposed to `OutsideZones`. Same bit `0x80`, different meaning. Verify no mod reads `DataFlags.Valid` directly.
- [ ] **`ZNet.IsDedicated()` flip from `=> true` to `=> false`** — counter-intuitive. Re-read the test build to understand the new semantic before relying on it.

---

## Appendix: tracked patch-site totals

- **FiresNPCs:** 306 patch sites + 150+ reflection lookups across 73 Valheim types
- **VAInventory:** ~80 patch sites + ~40 reflection lookups across 25 Valheim types
- **VABackpacks:** ~30 patch sites + ~25 reflection lookups across 30 Valheim types
- **VAGhettoNetworking:** 25 patch sites (3 transpilers) + 15+ reflection lookups across 35 Valheim types

Diff coverage of this document: **~75 unique types** were diffed at source level (where a `.cs` exists). Six classes (`Cover`, `ZInput`, `Localization`, `UITooltip`, `ZSteamSocket`, `ZPlayFabSocket`) live in satellite assemblies and were left for runtime verification — none are deeply reflected by any of the four mods except `ZSteamSocket` (VAGhetto, 3 sites).

## Appendix: glossary of new types

| Type | Purpose | Defined in |
|---|---|---|
| `Vector2s` | Sector identity (replaces `Vector2i` for zone keys). Same `int x, int y` shape but distinct struct. | (test-build vanilla) |
| `ZoneSystem.SectorIndex` | `struct { uint Sector; }` — packed sector index | `ZoneSystem.cs` |
| `ZoneSystem.ChunkIndex` | `readonly struct(ushort Chunk, byte m_chunkSize)` | `ZoneSystem.cs` |
| `Version.World` | Enum replacing the historical `int version` parameter on save/load methods | (test-build vanilla) |
| `Version.PlayerData` | Enum for Player.Save/Load version (`MaxHealth`, etc.) | (test-build vanilla) |
| `Version.Map` | Enum used by `Minimap.Load` | (test-build vanilla) |
| `ZDOMan.SaveState` | New enum for chunked save state machine | `ZDOMan.cs` |
| `ZDO.ExtraDataFlags` | New bitflag enum for extra-data presence (in serialize header) | `ZDO.cs` |
| `ChunkSaveMapping` | Tracks chunk file versions and ZDO counts per chunk | `ChunkSaveMapping.cs` (new file) |
| `ChunkSaveMapping.ChunkInfo` | Per-chunk metadata (`m_chunkIndex`, `m_version`, `m_numZDOs`, `m_sizeChanged`) | `ChunkSaveMapping.cs` (new file) |
| `SaveFileHelper.SaveWrite` / `SaveReader` | IDisposable wrappers around ZPackage + FileWriter/FileReader for chunked saves | `SaveFileHelper.cs` (new file) |
| `FileHelpers.FileSource` | (Likely already existed) — file source enum used by save helpers | (test-build vanilla) |

Three new helper namespaces appear in the chunked save flow: `Splatform` (used by `SaveFileHelper`), and a tighter coupling between `SaveSystem` (with new `MainDb2FileName`, `MainChunksFileName`, `IsWorldSaveFile`) and `FileHelpers`.
