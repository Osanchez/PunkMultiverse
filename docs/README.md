# PUNK Object Taxonomy — sync tracking docs

A catalog of every object type in the game, organized by how the game composes them and
annotated with **what PunkMultiverse currently syncs and what it doesn't**. Sources: the
decompiled `Punk.Main.dll` (game v. matching playtest appid 2850470) and live session logs.

## The composition pattern (read this first)

Everything persistent in the world follows one pattern:

```
EntityData  (pure data: instanceId, position, entityId string, list of ComponentData aspects)
   │  streams in/out with its LevelSegment
   ▼
SavableEntity (GameObject root, spawned by EntityGameObjectManager when the segment is near)
   ├── SavableComponent<TData> "views" — each binds one ComponentData aspect (Bind/Unbind)
   ├── plain MonoBehaviours — runtime-only behavior, nothing persists
   └── child GameObjects — visuals, barrels, articulated Rigidbody2D parts, State children
```

The **data half exists for the whole map at all times** (deterministically generated from the
run seed on every machine — that's why netIds can be assigned by manifest and why dormant
segments cost nothing). The **GameObject half exists only while a player is near**.

There are exactly **10 savable aspects** in the game:

| ComponentData aspect | Lives on | Doc |
|---|---|---|
| `Unit.Data` | anything alive (enemies, ships) — resource tanks, shields, burn, minions | [enemies](enemies.md), [players](players-and-projectiles.md) |
| `Enemy.Data` | enemies — kill/economy metadata, embedded weapon module | [enemies](enemies.md) |
| `AIAgent.Data` | enemies — aggro black/whitelists | [enemies](enemies.md) |
| `ModuleGridOwner.Data` | ships + module-bearing enemies — the module grid | [players](players-and-projectiles.md) |
| `Station.Data` | stations — installed upgrades (`IsUnlocked` = count > 0) | [interactables](interactables.md) |
| `Scanner.Data` | map scanners — `areaId`, `isUsed` | [interactables](interactables.md) |
| `Instrument.Data` | instruments — discoverables | [interactables](interactables.md) |
| `EntityPlant.Data` | plants — branch tree + fruits | [plants](plants.md) |
| `ConsumablePickup.Data` / `IngredientPickup.Data` | pickups | [pickups-and-loot](pickups-and-loot.md) |
| `SaveDestroyedObjects.Data` | props — which tracked child objects were destroyed | [containers](containers.md) |

Terrain cells are **not** entities — they're a separate grid system ([terrain](terrain.md)).

## Sync status legend

- **STATE** — replicated continuously via EnemySync entity snapshots (owner-simulated,
  puppet elsewhere: pos/vel/rot/aim/AI-state-index/fire/ammo/hp/shield/burn).
- **EVENT** — replicated as reliable one-shot events (kills, upgrades, discoveries, fire).
- **DET** — deterministic from the shared seed; never sent, verified by checksums.
- **LOCAL** — intentionally per-machine (cosmetics, instanced loot).
- **GAP?** — not synced and it's unclear whether that's fine; verify in playtest.

## Read first

- **[VANILLA_GOTCHAS.md](VANILLA_GOTCHAS.md)** — where the base game does something surprising,
  and what it already cost. Read before changing anything that touches game code.
- [GAMESCAN.md](GAMESCAN.md) — how to tell what a game update changed, and whether it can break us.
- **[api/](api/)** — mechanically generated API index for all 1201 game types. Complete and never
  stale, but it explains nothing. Use it for signatures; use the files below for meaning.

## World & entities

- [enemies.md](enemies.md) — enemy anatomy, behavior composition, full observed roster
- [bosses.md](bosses.md) — boss state machinery, minibosses
- [players-and-projectiles.md](players-and-projectiles.md) — ships, minions, weapons, projectiles
- [plants.md](plants.md) — plants, branches, fruits
- [containers.md](containers.md) — crates/boxes and destructible props
- [interactables.md](interactables.md) — stations, scanners, instruments
- [pickups-and-loot.md](pickups-and-loot.md) — loot pipeline and pickups

## World construction

- [level-generation.md](level-generation.md) — graph → biomes → heightmap → rasterization, and determinism
- [terrain.md](terrain.md) — the cell grid, destruction, regrowth, burning
- [environment.md](environment.md) — background, hazards, ambient
- [fog-and-lighting.md](fog-and-lighting.md) — fog is a gas simulation; light is baked and gameplay-relevant

## Progression & build

- [modules-and-ship-building.md](modules-and-ship-building.md) — the grid, clusters, and the power cap
- [shops-and-economy.md](shops-and-economy.md) — currencies, purchase flow, re-rolling repeat items

## Presentation & framework

- [game-state-flow.md](game-state-flow.md) — boot order, the four static events, owner-keyed pause
- [save-and-serialization.md](save-and-serialization.md) — entity/data split, mementos, Odin + LZF
- [ui-and-screens.md](ui-and-screens.md) — two UI stacks, screens own pause and input mode
- [map-and-minimap.md](map-and-minimap.md) — and the information-leak surface
- [input.md](input.md) — action maps are the state
- [camera.md](camera.md) — ProCamera2D targets; the camera is off during the pre-start hold
- [audio.md](audio.md) — string-named sfx, handle-based playback
