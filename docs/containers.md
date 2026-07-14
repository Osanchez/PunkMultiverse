# Containers & destructible props

## Anatomy

Containers are the simplest entity shape — no `Unit`, no AI. Two sub-shapes:

```
Loot container (Box_*/Crate*)
SavableEntity (root)
├── DamagableResource        — HP tank; the "openable by shooting" part
├── DestroyWhenResourceDrained — polls the tank; on empty: Object.Destroy(entity) +
│                                spawns its spawnOnDeath drop (NOT via Die())
├── LootDropper              — dropTable/loot, dropForce/dropAngle/spawnOffset
├── Rigidbody2D (some)       — pushable physics props
└── optional SaveDestroyedObjects — persists which tracked CHILD objects were destroyed
                                    (Data: List<string> destroyedObjects, by path)
```

Key vanilla quirk the mod codifies: `DestroyWhenResourceDrained` entities die through
`Object.Destroy` + drop spawn, **never through `Die()`** — so the kill path and loot path
differ from enemies (see `DamageSync.KillInstance`, which replicates exactly this shape).

## Observed roster

| Prefab | Contents (inferred from name) |
|---|---|
| `Crate`, `Crate2`, `CrateGreen`, `CratePurple`, `CrateTech`, `CrateCaps` | tiered/biome loot crates |
| `Box_Money` | currency |
| `Box_Health` | healing |
| `Box_Bomb` | explosive (hazard when shot — chain-damage source) |
| `Box_Beacon` | beacon/utility |
| `Box_Matchbox` | fire-related |

Spawn: level generation (`RandomObjectGenerator` / room generators) — deterministic.

## Sync status

| Aspect | Status | Mechanism |
|---|---|---|
| Existence/placement | **DET** | generated from seed, in the netId manifest |
| Position (pushable ones) | **STATE** | prop path: streams on movement ≥0.05u + 0.75s rest-pose heartbeat; `PropPuppet` holds non-owners kinematic |
| Destruction | **EVENT** | kill broadcast; `KillInstance` replicates the `spawnOnDeath` drop locally per machine |
| Drop contents | **LOCAL** | loot is instanced per player by design — each machine spawns ITS OWN copy of the drop ("shared progression, per-player loot") |
| `SaveDestroyedObjects` children | **GAP?** | vanilla persistence of destroyed child objects — not explicitly synced; if a container's sub-parts can be shot off independently, machines could disagree until re-stream. Verify whether any prop uses it for gameplay-relevant children. |
| Puppet muting | mechanism | `DestroyWhenResourceDrained` is muted on puppets so the un-synced local resource can't destroy/loot out of step; the owner's kill event drives removal |

**Known incidents encoded here:** the `Box_Money` re-kill storm (killed containers
re-streaming and re-running death chains at 60 Hz) is why `KillInstance` destroys the
EntityData too and why `DeathEffectsDone` dedups the death chain.
