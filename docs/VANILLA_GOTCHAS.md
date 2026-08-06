# Vanilla gotchas

**Read this before changing anything that touches game code.**

Every entry below is a place where the base game does something surprising, and where that
surprise has already cost real debugging time on this project. None of them are visible from
the API surface — you can read the signature, write correct-looking code against it, and be
wrong. That is what makes them worth writing down.

The rule this file exists to serve: **never reimplement game logic.** Damage, physics, AI,
loot and generation must run through the vanilla code paths, or single-player and multiplayer
diverge. Which means the vanilla code paths' quirks become your quirks.

Each entry states the mechanism, then where it bit. Entries marked **verified** were re-read in
the decompiled source for the current game build (`gamescan/cache/<build>/`); the rest are
recorded from the debugging session that found them.

---

## Resources & damage

### An infinite tank silently refuses to decrease

**verified — `ResourceTank.cs`, the `Value` setter**

```csharp
set
{
    if (!isInfinite || !(_value > value))   // an infinite tank ignores ANY decrease
    { ... }
}
```

HP is a resource tank like any other. So the vanilla "unlimited resources" option makes *every*
tank `isInfinite`, including health — and an infinite health tank cannot be reduced. The ship
is not merely tanky, it is unkillable, and the symptom is a damage pipeline that looks like it
is working: `applied=True hp=N->N`, forever, from the moment the option is on.

**It propagates by two independent paths** (`Unit.cs`), which is what makes a one-time fix
insufficient:

```csharp
public bool HasInfiniteResource
{
    set
    {
        hasInfiniteResource = value;
        foreach (ResourceTank allTank in GetAllTanks())
            allTank.isInfinite = value;          // path 1: flips every EXISTING tank
    }
}

public void InstallNewTank(Resource resource, float capacity)
{
    resourceTanks.Add(resource, new ResourceTank
    {
        ...,
        isInfinite = hasInfiniteResource         // path 2: every FUTURE tank inherits it
    });
}
```

So clearing `isInfinite` on the health tank once is not enough — any later `InstallNewTank`
(a module install, a respawn, a loadout change) silently re-creates it as infinite.

**Where it bit:** the final PvP culprit in v0.1.222. Damage was routing, scaling and applying
correctly the whole time. The health tank is now excluded from the infinite set in net runs.

### Armour's `minDamage` gate runs on whatever you hand it

If you scale a damage value *before* the armour check, the gate sees the scaled number. With
PvP damage at ×0.25, any armour at all became total immunity.

**Where it bit:** v0.1.221. Fixed by putting raw damage on the wire and applying the scale
*after* armour (protocol 19).

### `damageBlockers` is a real vanilla concept and the menu canopy is one

A blocked hit reports as applied and changes nothing — the `hp=4->4` freeze. Defeated
explicitly in net runs rather than worked around.

---

## Projectiles & collision

### Friendly fire passes through only if the rigidbody itself carries the `Unit`

**verified — `Projectile.cs`, `FixedUpdate`**

```csharp
RaycastHit2D hit = Physics2D.CircleCast(transform.position, Radius, Velocity,
                                        Velocity.magnitude * Time.deltaTime, collisionLayerMask);
if (hit.collider != null)
{
    if (hit.rigidbody != null && Owner != null
        && hit.rigidbody.TryGetComponent<Unit>(out var component)
        && Owner.IsFriendsWith(component))
        MoveForward();      // pass through
    else
        OnObjectHit(hit);   // detonate
}
```

Three things follow, and all three have bitten:

1. The friendly check needs `Unit` on **the same GameObject as the rigidbody the collider
   belongs to**. When a hit resolves to a body that does not carry `Unit`, the guard silently
   fails and the projectile detonates — including on the shooter's own hull at the muzzle,
   on the very first cast.
2. It is `CircleCast`, not `CircleCastAll` — **one** hit per step. The nearest collider wins,
   and a self-hit at the muzzle means nothing behind it is ever considered.
3. `collisionLayerMask` decides what is even a candidate. Co-op's physics matrix deliberately
   lets bullets pass through players, so widening this mask is what exposed (1).

**Where it bit:** the whole PvP hunt, v0.1.202 → v0.1.227. Widening the mask to include the
Player layer is what made a bullet's first cast return the shooter's own hull.

### The gate-ladder diagnostic lied for four rounds

A proximity test is not a hit test. The old PvP diagnostic reported `castHitShip` from a
2-unit proximity check that a real hit fails, and one of its gates counted enemy fire too.
Use the `pvpprobe` devcmd, which asks the physical question: real mask, real colliders, real
`CircleCastAll`, real weapon range, real `IsFriendsWith`.

**Lesson worth generalising:** when a diagnostic and reality disagree four times, suspect the
diagnostic.

### Beams cannot hit moving ships

Hitscan aims where the puppet is drawn, which is behind where it is. This is aim-lag, not a
bug, and it is fair and visually coherent. Do not "fix" it by making beams predictive.

---

## Pickups & loot

### A pickup that fails to grant becomes an infinite faucet

**verified — `Pickup.cs`**

```csharp
public void GetPickedUp(Unit unit)
{
    OnPickedUp(unit);              // if this throws...
    Object.Destroy(gameObject);    // ...this never runs
}
```

`FixedUpdate` re-runs the magnet-and-collect check every physics step. So any exception inside
`OnPickedUp` leaves the pickup alive and it re-grants **at frame rate**.

This is not duplicate *minting* — the item is granted once per frame by a pickup that refuses
to die. Chasing it as a networking/duplication bug is the wrong tree entirely.

**Where it bit:** 1035 stacks in a single Player.log. The thrower was an item wheel that had
missed `GameStarted` and therefore had zero slots, so every grant threw.

### Entities drop loot at most once per machine

Dedup is per-machine. Do **not** suppress drops from received kills — that starves clients of
loot entirely.

### Loot is contested, not instanced — except gold

Gold materialises as expiring coins for everyone, so every loot type ends up "pick up your
own". Coin prefabs are discovered at runtime via `FindObjectsOfTypeAll`.

---

## Terrain & cells

### Bulk terrain change is quadratic-feeling and it is a *vanilla* defect

**verified — `GroundTilemapUpdater.cs`**

```csharp
private void OnCellsChanged(IEnumerable<Level.CellChange> changedCells)
{
    if (!ServiceLocator.Get<LocalConfig>().data.enableCellRefresh) return;
    foreach (Level.CellChange changedCell in changedCells)
        ... Refresh(changedCell.position.x, changedCell.position.y);   // one refresh PER CELL
}
```

One `Refresh` per changed cell, with no batching. At 2.9M painted cells per match this reached
**94% of frame time** and 9252 ms worst frames.

This hits ordinary co-op too — it is not a mod bug. The fix was to stop painting cells at all:
the battle-royale ring is now a rendered annulus mesh plus a radius check (no terrain, no
collider), which took worst frames 9252 ms → 18 ms.

Note the `enableCellRefresh` LocalConfig flag in that first line — vanilla can switch the whole
path off.

### 1 cell = 1 world unit, index = `y * Width + x`

Do not re-derive this. And use `ilspycmd` on `Punk.Main.dll` — reflection-only assembly load
fails on it.

---

## Entities, spawning & lifetime

### Ship placement is ASYNC and lands during Loading

Vanilla places ship entities asynchronously, and the placement completes *during* the Loading
state — before the first `InGame` tick. Anything gating on "first InGame frame" runs too late.

**Where it bit:** spawn-before-selection. The fix holds the pen every **render** frame from
`Toast.Update`, keyed on `CurrentMode` (never `LobbyMode`), with an explicit `ReleaseHold` for
the no-options fallback.

### The coordinator never simulates anything

A spawn that pulls its lease to the coordinator will simply never run. Coordinator spawns must
stay `Dormant`.

**Where it bit:** the BR care package, v0.1.237.

### Ownership is host-authoritative with sticky client-grab

Never reintroduce "closest player owns it" re-optimisation. When host and client disagree about
who owns an entity, the host must **restate** ownership rather than answer with silence — 31
consecutive rescue requests were answered `gate=requester-already-owner` and dropped.

### `EntityData.MoveTo` teleports the transform

**verified — `SavableEntity.cs`**

```csharp
private void OnEntityMoved(EntityData data, Vector3 old, Vector3 newPosition)
{
    if (newPosition != transform.position)
        transform.position = newPosition;    // hard set: no interpolation, no physics
}
```

`MoveTo` raises `Moved`, and `SavableEntity` handles it by assigning `transform.position`
directly. Harmless for a local entity — but applying a network snapshot by calling
`data.MoveTo(wirePosition)` teleports a puppet to the raw wire position ~30 times a second,
producing a yank-back sawtooth and resetting interpolation every time.

Wire `MoveTo` only for **non-live** entities. Live entities keep their data current for free:
`SavableEntity.Update` already writes the transform back into the data whenever
`transform.hasChanged`.

**Where it bit:** v0.1.129, the visible puppet jitter that fixed-step metrics could not see.
`rendersmooth` (render-frame drawn-pose CV / stall%) is the instrument that found it.

### Identity is `instanceId`, never position

Position fingerprinting has been removed once already. Do not bring it back.

---

## State, config & startup

### Unlock broadcasts before the state flip are dropped

`ProgressionSync` dropped all 44 shop-unlock broadcasts because the unlock ran before the
`InGame` state flip.

### A "version mismatch" of the form `0.12.x` is the BASE GAME, not the mod

Steam updates the client; a container's baked game does not move with it. The game files live
on the **volume** and are copied only on first boot, so rebuilding the image alone fixes
nothing.

This is exactly the confusion `tools/gamescan.ps1` and the boot-time `[GameScan]` line exist to
remove — see [`GAMESCAN.md`](GAMESCAN.md).

### BepInEx default flips need a key rename

Changing a config default does not reach users who already have the old value persisted.
Rename the key. And beware duplicate config keys introduced by bulk `sed` edits.

### Deleting a config key means adding a line to `ConfigAudit.Retired`

The boot-time `[Config]` report names retired, misfiled and unknown keys. It only works if you
maintain it.

---

## Working on this codebase

### The mod's csproj whitelists `src/`

`EnableDefaultCompileItems` is **off** and `<Compile Include="src/**/*.cs" />` is explicit.
Default SDK globbing would otherwise compile every `.cs` under the repo — which silently
swallowed the 776-file decompiled game tree under `gamescan/cache/` and produced 424 errors.
Add new mod code under `src/`; anything else needs no exclusion.

### `.ps1` files that contain non-ASCII inside strings need a UTF-8 BOM

Windows PowerShell 5.1 reads a BOM-less script as ANSI, so an em dash inside a double-quoted
string becomes mojibake and can break parsing. Comments survive it; strings do not.
`build.ps1` and `tools/gamescan.ps1` carry a BOM.

### `ConvertFrom-Json` cannot read the gamescan manifest

PowerShell 5.1 compares JSON keys case-insensitively, and the game genuinely declares members
differing only in case (`AIAgent.seeker` and `AIAgent.Seeker`), so it throws
`DuplicateKeysInJsonString`. Parse those files with the tool, or with a regex for the one value
you need.

### Fixed-step metrics cannot see render-level jitter

Always compare `rendersmooth` owner-vs-puppet. A whole class of visible stutter is invisible to
fixed-step measurement.

---

## See also

- [`GAMESCAN.md`](GAMESCAN.md) — detecting what a game update changed
- [`ENTITY_SYNC_ARCHITECTURE.md`](ENTITY_SYNC_ARCHITECTURE.md) — the residency/authority contract
- [`api/`](api/) — mechanically generated API index (lookup only, no explanations)
