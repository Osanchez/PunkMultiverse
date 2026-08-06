# Enemies

## Anatomy of an enemy entity

Every enemy is one `SavableEntity` with this component stack (prefab-configured — the
variety between enemy types is data + which behaviors are present, not new classes):

```
SavableEntity (root)
├── Unit                : SavableComponent<Unit.Data>       — the "alive thing" aspect
│     Data: resourceTanks (Dictionary<Resource,ResourceTank> — HP IS a tank),
│           resourceRechargers, shields (List<ShieldData>: resource+effectiveness),
│           burnProperties + _burnLevel (IsOnFire above threshold), minions (HashSet),
│           hasInfiniteResource  <-- see below,
│           Owner (EntityData), ConnectionToOwner (OwnerConnectionType),
│           IsInvisible, SpawnRoomIndex
├── Enemy               : SavableComponent<Enemy.Data>      — enemy-specific metadata
│     Data: coopResourceMultiplier, countsAsKill
│     also: embeddedModule (ModuleData), primaryWeapon (WeaponModuleData)
├── AIAgent             : SavableComponent<AIAgent.Data>    — targeting brain
│     Data: enemyBlackList / friendWhiteList (HashSet<int>)
│     runtime: Vision, A* Seeker, currentTarget, targetLastKnownPosition,
│              visibleEnemies/Friends, GotAttackedByUnit event
├── DamagableResource   : HealthBase — damage router into the HP tank; IsDead/IsInvincible
├── Rigidbody2D (root)  + optional articulated CHILD Rigidbody2Ds (see fish note below)
├── StateMachine        — AI states are child GameObjects toggled on/off
│     └── State children, each holding Action/Condition Behaviours (composition list below)
├── Movement Behaviour  — one of: UnitMovement, SwimmingMovement, SwayMovement, PushMovement
├── Shooter + WeaponHolder + WeaponBase (Projectile/Hitscan/Physics/MinionSpawner weapon)
│     └── BarrelTransform child — the game's single source of truth for aim direction
├── LootDropper (dropTable / loot, drop force/angle)
└── optional: ModuleGridOwner (module-bearing enemies), DestroyWhenResourceDrained,
              BossStateActivator (bosses), DamageHighlight (hit flash)
```

**Behavior building blocks** (Actions/Conditions composed under State children):
`AimAction, AimAtTargetAction, AimAtLastKnownPositionAction, AimInRandomDirectionAction,
MoveTowardsTargetAction, MoveAwayFromTargetAction, MoveAroundTargetAction,
MoveAroundOwnerAction, MoveToPositionAction, MoveToTargetLastKnownPositionAction,
MoveInRandomDirectionAction, ShootAction, ShootComplexAction, ActivateShooterAction,
SelfDestructAction, PushSelfAction, ApplyTorqueAction, ReduceAngularVelocityAction,
StopAction, WaitForTargetAction, ForgetTargetAction, ChangeAnimatorParamAction,
RepeateChildrenAction` — gated by `TargetVisibleCondition, TargetIsCloseCondition,
TargetIsAheadCondition, EnemyVisibleCondition, GotAttackedCondition, TimeoutCondition,
IsInLightCondition, HasOwnerCondition, OwnerIsWithinRangeCondition, HasLessMinionCondition`.

**`hasInfiniteResource` makes a unit unkillable.** `Unit.Data.HasInfiniteResource` is a single
flag that sets `isInfinite` on *every* tank the unit owns — and HP is a tank. Its setter flips
all existing tanks, and `InstallNewTank` makes every future tank inherit it, so clearing it once
does not stick. An infinite tank silently refuses to decrease, which reads downstream as a
damage pipeline that applies hits and changes nothing. See
[`VANILLA_GOTCHAS.md`](VANILLA_GOTCHAS.md#an-infinite-tank-silently-refuses-to-decrease).

**Articulated bodies:** some enemies (`Enemy_Fish`, worm types) carry jointed child
Rigidbody2Ds (tails/segments) that only local physics moves. All hard teleports must go
through `RemoteEntityPuppet.TeleportWithChildren` or the parts strand (fixed in 0.1.85).

## Observed roster (from playtest logs; prefab `entityId` names)

| Prefab | Family / movement (inferred) | Notes |
|---|---|---|
| `Unit_Fly`, `Unit_FlyAlfa`, `Unit_FlyDad` | flier (SwayMovement — firefly Perlin drift) | Alfa/Dad = elite/miniboss variants |
| `Enemy_Fly_Laser` | flier + hitscan weapon | beam drawn via `DriveBeam` on puppets |
| `Unit_Grunt` | ground walker | most common trash |
| `Unit_Cross`, `Unit_Cross_SmallPurple` | cross family base | |
| `Unit_Cross_Alpha` | cross elite | miniboss-tier |
| `Unit_Cross_JockRocket/JockLaser/JockBomber` | cross + weapon loadout variants | embedded WeaponModuleData differs |
| `Enemy_Cross_Zipper`, `Enemy_Cross_Tablet` | fast mover / tablet | tablet seen in dormant-frozen bug |
| `Unit_Swimmer_Canari`, `Unit_Swimmer_Maggot` | SwimmingMovement | |
| `Enemy_Fish` | SwimmingMovement + jointed tail | the tail-detach bug source |
| `Unit_Bouncer_Red`, `Unit_Bouncer_Worm` | PushMovement bouncers | worm = articulated |
| `Unit_Floater_Rookie/SoldierPurple/SoldierTech/OfficerCaps` | floaters, rank variants | |
| `Enemy_Turret_Sniper`, `Enemy_Turret_Laser`, `Enemy_Turret_Worm` | static turrets | long engagement range — most exposed to activation lag |

Spawn sources: `EnemyGenerator` (level gen, deterministic), `EnemyGroup` (ScriptableObject
packs), plus runtime spawns (e.g. minion weapons → see players-and-projectiles.md).

## Sync status

| Aspect | Status | Mechanism |
|---|---|---|
| Position/velocity/rotation | **STATE** | owner snapshots, 30/20/10 Hz by fire+distance; puppet root kinematic |
| Aim | **STATE** | `BarrelTransform.Direction` mirrored on puppets |
| AI state (pose/VFX) | **STATE** | current `State` child index by prefab order; `WriteState` drives puppet StateMachine |
| Fire state (sounds/beam) | **STATE** | 0/1/2 byte → warmup/continuous sounds + hitscan beam draw |
| HP / shields / burn | **STATE** | fractions in snapshots; reflection into tanks/shield charge/BurnLevel |
| Ammo (reload indicator) | **STATE** | weapon tank fraction, 255 = shared/no tank |
| Actual projectiles | **EVENT** | fire events replayed; hit detection is VICTIM-side (see players doc) |
| Death | **EVENT** | kill broadcast + kill ledger (anti-resurrect on re-stream) |
| HP scaling per player count | **EVENT** | host multiplier applied once per entity everywhere |
| AI target / aggro lists | **LOCAL** | owner's AIAgent runs alone; `AIAgent.Data` black/whitelists never sent — fine while one owner simulates, relevant on handoff (**GAP?** — a handed-off enemy forgets its target) |
| Puppet muting | mechanism | muted: `AIAgent, Vision, UnitMovement, Seeker, Shooter, StateMachine, PushMovement, SwimmingMovement, SwayMovement, ChargerRam, Shoot*/ActivateShooter/SelfDestruct actions, DestroyWhenResourceDrained` + all `AimAction`/`MovementAction` |
| Dormant enemies | mechanism | frozen at dormancy-commit pose; wake on lease claim (0.1.85: nearest-first, batched) |

**Known weak spots to watch:** activation lag on long-range turrets (they out-range the
claim radius by design), articulated parts across teleports (fixed), aggro amnesia after
authority handoff, `AIAgent.Data` lists unsynced.

**Attack-animation-without-projectiles** (observed on a client-owned miniboss viewed by
the host): the puppet's attack STATE replicates (animation plays) but no projectiles
appear. Two candidate causes — the owner's AI telegraphs without ever pulling the trigger
(its target is a puppet ship failing `TargetVisibleCondition` at range), or a fire-capture
gap for that weapon type. The `[FireAudit] owned #netId entered fire=N` log line (owner
side, 0.1.85+) decides it: audit line + no projectiles on viewers = capture gap; no audit
line = the AI never fired.
