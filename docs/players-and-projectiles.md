# Players, minions, weapons & projectiles

## Ship anatomy (the player)

```
Ship : MonoBehaviour  (composition root; NOT itself a SavableComponent)
├── Unit               — tanks (fuel is special: never auto-refilled), shields, burn
├── ModuleGridOwner    — the build: modules in a grid; stats recalc on change
├── DamagableResource  — HP; Ship.IsDead; Resurrect() refills HP tank, zeroes others,
│                        reactivates the GameObject, headlights on
├── SavableEntity      — ships ARE entities (created by ShipManager.PlaceShipEntity)
├── WeaponHolder ×2    — primary/secondary; Interactor; Crosshair; ShipInput
├── Rigidbody2D        — ShipMovement; self-destruct machinery; fuel warnings
└── ApplyShipTheme / CameraTargets / ShipLogOutput (cosmetic/UX)

ShipManager — owns List<Ship>; PlaceShipEntitiesToStartPosition, SpawnShipGameObjects,
OnUpgradeInstalled (station respawn cascade — rerouted by the mod in net play),
FirstAliveShip, AnyShipsAlive (party-wipe input)
```

| Aspect | Status | Mechanism |
|---|---|---|
| Ship pose/velocity | **STATE** | `ShipSync` at ShipStateHz (40), `RemotePuppet` on other machines |
| Life (death/resurrect) | **EVENT** | `Ship.OnDeath`/`Resurrect` hooks → `ShipLifeMsg`; life watchdog re-announces on mismatch; puppet resurrection gated to owner events |
| Module grid (build changes) | **EVENT** | `ModuleGridSync` |
| Ship resources (HP etc.) | **STATE** | flags/fractions in ship snapshots |
| Hook (grapple) | **EVENT/STATE** | `HookSync` |
| Interactions by puppets | scrubbed | `RemotePuppet.ScrubInteractions` on death — puppets must not hold shop/interact state |

## Minions (player-owned drones)

`MinionSpawnerWeapon(+Data)` spawns minions; `Unit.Data.minions` tracks them;
`HasOwnerCondition`/`MoveAroundOwnerAction`/`OwnerIsWithinRangeCondition` drive their AI;
minions share their owner's resource tank (why ammo byte 255 = "shared, don't sync").

| Aspect | Status | Mechanism |
|---|---|---|
| Minion existence | **EVENT** | `MinionSync` — replicas instantiated INACTIVE first (prefab-active actions would fire on spawn), fixed-owner entities (owner = spawner, epoch 0) |
| Minion pose/state | **STATE** | fixed-owner entries in the entity state stream |

## Weapons & projectiles

```
WeaponBase family: ProjectileWeapon / HitscanWeapon / PhysicsWeapon / MinionSpawnerWeapon
  (+ *Data configs, WeaponRegistry, WeaponFactory, WeaponModule/WeaponModuleData)
Shooter — drives a WeaponHolder + IBarrel; warmup/continuous sounds; blockers set
Projectile : MonoBehaviour, IProjectile, IDamageSource — per-listener damage/knockback
  cooldown dictionaries, noise offset (ProjectileMovementNoiseData), Shot event
```

**The victim-side hit rule** (the core damage design):
- Projectiles are simulated only on their SHOOTER's machine; everyone else sees replayed
  visual projectiles from fire events (`ProjectileSync` — muzzle timeline with correction).
- Hits on YOU are detected on YOUR machine ("you can only be hit by shots that visibly
  reach you on your own screen"). Your machine applies the damage and broadcasts it.
- Hits on entities the shooter doesn't own become damage REQUESTS routed to the owner
  (`DamageSync`), with dormant-claim queuing when nobody owns the target.

| Aspect | Status | Mechanism |
|---|---|---|
| Fire events | **EVENT** | capture on fire; replay spawns visual projectiles (marked, so raw puppet-fired ones are detectable as duplicates) |
| Projectile flight | **LOCAL** per machine | deterministic-ish from fire params + noise; muzzle correction avg is telemetered |
| Damage | **EVENT** requests | victim-side for ships; owner-routed for entities; `Replay` flag + request ids dedup |
| Impact dedup | mechanism | `duplicateImpactDrops` — **open suspect for "projectile hit me, no damage"; audit the dedup key** |
| Hitscan beams | **STATE**-derived | fire state 2 drives `OnBarrelMoved` beam drawing on puppets (draw only — damage replays separately) |
| Explosions / chain damage | **EVENT**-derived | chain kills land inside applied remote events; life watchdog catches the swallowed broadcasts |

**Known open item:** the +18 `duplicateImpactDrops` during heavy combat correlates with
reported no-damage hits — next step is auditing what keys the dedup (shot id + pellet
collisions on rapid multi-hit weapons being the suspect).
