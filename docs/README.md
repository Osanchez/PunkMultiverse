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

## Files

- [enemies.md](enemies.md) — enemy anatomy, behavior composition, full observed roster
- [bosses.md](bosses.md) — boss state machinery, minibosses
- [containers.md](containers.md) — crates/boxes and destructible props
- [plants.md](plants.md) — plants, branches, fruits
- [terrain.md](terrain.md) — the cell grid, destruction, regrowth, burning
- [environment.md](environment.md) — fog, light, background, hazards, ambient
- [interactables.md](interactables.md) — stations, scanners, instruments
- [pickups-and-loot.md](pickups-and-loot.md) — loot pipeline and pickups
- [players-and-projectiles.md](players-and-projectiles.md) — ships, minions, weapons, projectiles
