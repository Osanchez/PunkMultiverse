# Modules & ship building

The build system: a sparse grid of modules, wired into clusters, gated by power.

Exact signatures: [`api/modules-and-ship-building.md`](api/modules-and-ship-building.md).

## The shape of it

```
ModuleGridOwner : SavableComponent<ModuleGridOwner.Data>, IGridOwner
└── Data : ComponentData, IMementoOriginator<Data.Memento>
      └── ModuleGrid : IModuleGrid, IMementoOriginator<ModuleGrid.Memento>
            ├── Dictionary<Vector2Int, Module>         modules      — the build, sparse
            ├── Dictionary<Vector2Int, ModuleSlotType> slotTypes    — special slots
            ├── Dictionary<ClusterType, ModuleCluster> clusters     — one per main slot
            ├── Dictionary<Vector2Int, int>            levelDeltas  — level modification
            ├── HashSet<Vector2Int>                    poweredSlots
            └── ModuleGridPreview                      preview      — drag/placement ghost
```

Ships are not the only module bearers — enemies can carry a `ModuleGridOwner` too, and an
`Enemy` has an `embeddedModule`. Anything holding the component gets the same rules.

## The grid is sparse, and its anchors are constants

`ModuleGrid` is a dictionary keyed by `Vector2Int`, not an array. There is no width or height —
positions are wherever modules were placed, around six hard-coded anchors:

| Slot | Position |
|---|---|
| Ship (the hull) | `(50, 50)` |
| Primary weapon | `(46, 50)` |
| Secondary weapon | `(54, 50)` |
| Active 1 / 2 / 3 | `(46, 46)` · `(50, 46)` · `(54, 46)` |

Exposed as `ModuleGrid.MainSlotPositions`. The 50/50 origin is arbitrary headroom, not a
meaningful center — do not compute offsets from an assumed grid size.

## Modules

`Module` is a runtime instance; `ModuleData` is the authored asset (`SerializedScriptableObject`,
so Odin-serialized — see [`save-and-serialization.md`](save-and-serialization.md)).

| Member | Meaning |
|---|---|
| `Data` | the `ModuleData` asset this was instantiated from |
| `Level` / `BaseLevel` | effective level vs. authored level; the grid's `levelDeltas` move it |
| `PowerLevel` | on a **main** module, how many power cores the cluster may use (see below) |
| `North/East/South/West` | connection flags — this is the entire adjacency model |
| `Effects` | `List<ModuleEffect>` — where all behavior lives |
| `PowerCore` | a `ModuleEffectField` shape, or null |
| `LevelModificationField` | a `ModuleEffectField` shape, or null |

Connections are randomised per instance (`RandomizeConnections`, driven by
`ModuleData.connectionCountDistribution`), so two drops of the same module are not
interchangeable. `CopyConnectionsFrom` exists for preserving them across a copy.

### Effect fields are authored as sprites

`ModuleEffectField(Sprite shape)` converts a sprite into a `bool[]` mask with a width and
height. `GetPositionsRelative()` yields the offsets where the field applies.

That means a module's power-core footprint and its level-modification footprint are **drawn**,
not typed. If a shape looks wrong in game, the asset is a sprite, not a number.

## Clusters and connectivity

A `ModuleCluster` is rooted at one main slot and holds everything reachable from it:

```csharp
public void RefreshConnectedModules()
{
    connectedModules.Clear();
    GridHelper.CollectConnectedModulesRecursive(grid.Modules, rootPosition, connectedModules);
}
```

Reachability is a flood fill over the four `North/East/South/West` booleans — adjacency alone is
not enough, **both** modules must face each other. A module physically touching the cluster but
not connected is inert.

## Power — the rule worth knowing

`ModuleCluster.RefreshPoweredSlots` is the whole economy of the build:

```csharp
foreach (var connectedModule in ConnectedModules)
{
    var value = connectedModule.Value;
    if (value.PowerCore == null) continue;

    foreach (var item in value.PowerCore.GetPositionsRelative())
    {
        var pos = connectedModule.Key + item;
        if (connectedPowerCores < MainModule.PowerLevel)      // <-- the cap
        {
            reservedSlots.Add(pos);
            if (grid.GetSlotType(pos).canBePowered)
                poweredSlots.Add(pos);
        }
    }
    if (value != MainModule) connectedPowerCores++;           // main core is free
}
```

Four consequences:

1. **The main module's `PowerLevel` caps how many power cores actually do anything.** Extra
   cores beyond it are connected, counted, and ignored.
2. **The main module's own core does not count against the cap** — it is incremented only for
   `value != MainModule`.
3. **Reserved ≠ powered.** A position inside a core's footprint is always *reserved*; it only
   becomes *powered* if the slot type says `canBePowered`.
4. Iteration order over `ConnectedModules` decides which cores win when the cap binds. It is
   dictionary order — do not depend on it.

`RefreshPoweredModules` then intersects connected with powered and fires
`Module.OnContainingClusterRefreshed` on **every connected module** (not only the powered ones),
followed by the `ModulesRefreshed` event.

## Where behavior lives

`ModuleEffect` is the extension point. All of it is virtual, all of it is optional:

| Hook | Called when |
|---|---|
| `OnInstalled(Unit.Data)` / `OnUninstalled(Unit.Data)` | module enters/leaves a grid |
| `OnUpdate(Unit.Data)` | per frame, from `ModuleGridOwner.Update` |
| `OnContainingClusterRefreshed(IModuleCluster)` | cluster recomputed — power may have changed |
| `OnRecalculateUnitStats(Unit.Data)` | stat rollup |
| `ModifyWeapon(Unit, WeaponBase)` | weapon stat modification |

`ModuleEffect.Clone()` is abstract — every effect must deep-copy, because `Module.DeepCopy`
is how the shop preview, drag ghost and loadout templates all work.

## Slot types

`ModuleSlotType` is a `ScriptableObject` with well-known statics installed at load via
`SetValues`: `Normal`, `Embedded`, `Weapon`, `Active`, `Invalid`. Each carries
`compatibleModuleTypes`, `canBePowered`, and placement-rect sizing. `IsCompatible(Module)`
is the gate.

`ModuleType` is coarser — a display name, a shop ordering, and `isMain` (which makes it
cluster-rootable).

## Mementos

`Module`, `ModuleGrid` and `ModuleGridOwner.Data` all implement `IMementoOriginator<T>`. That
is the game's save/restore and undo mechanism, and it is what the module-grid replication rides
on. See [`save-and-serialization.md`](save-and-serialization.md).

## Multiplayer notes

Build changes replicate as **events** via `ModuleGridSync`, not as state — the grid is large,
changes are discrete, and a snapshot would be wasteful. Ship stats recalculate on change.

`ShipManager.OnUpgradeInstalled` normally triggers a station respawn cascade; the mod reroutes
that in net play. See [`ENTITY_SYNC_ARCHITECTURE.md`](ENTITY_SYNC_ARCHITECTURE.md).

Module **trading** between players was considered and rejected.

## See also

- [`players-and-projectiles.md`](players-and-projectiles.md) — the ship the grid sits on
- [`shops-and-economy.md`](shops-and-economy.md) — where modules come from
- [`VANILLA_GOTCHAS.md`](VANILLA_GOTCHAS.md)
