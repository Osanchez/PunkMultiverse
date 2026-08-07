# Custom weapons (WeaponForge) in multiplayer

[WeaponForge](https://github.com/Sugarheady/WeaponForge) is a community mod by Sugarheady that
lets players author custom weapons from JSON. It is optional, installs like any BepInEx plugin,
and PunkMultiverse never requires it.

This doc is about what happens when it *is* installed and more than one machine is involved.

---

## The one thing that makes this hard

A custom weapon is not just an extra item. It widens the **module registry**, and this mod keys
several load-bearing things on that registry:

| Consumer | What it does with the registry | What a difference between machines causes |
|---|---|---|
| `Modes/BattleRoyaleLootTables` | builds drop pools from `AllItems` ordered by id, then picks `pool[rnd.Next(pool.Count)]` with a per-entity seed **rolled independently on every machine** | one extra module shifts every index — the whole BR drop table diverges, not just the custom item |
| `Sync/ModuleGridSync` | puts the module's **string id** on the wire; the receiver does `Get(id).DeepCopy()` | an unknown id NREs inside the game's own `Restore()` |
| `Sync/ProjectileSync` | hashes weapon identity for entity-fire replay | a mismatched hash rebuilds the wrong weapon on the peer |

BR's contested-loot identity is `(Group, Ordinal)` — parallel deterministic rolls that every
machine is expected to reproduce. So divergent registries do not merely give somebody an extra
gun; they make the machines disagree about what every ordinal *names*. Silently.

**That is why the barrier exists.**

---

## The barrier

`LevelReadyMsg` carries the installed module set — a count plus an FNV-1a 64 digest over the ids,
ordinal-sorted — and `CheckGoLive` compares it alongside terrain, entities, plants and visuals.
Mismatch refuses the run.

```
[Determinism] modules=148/7296FDA93CA002E3
```

Two deliberate choices:

- **Digested by module ID**, not by any mod's own notion of its content. Ids are what every
  consumer above actually keys on, they exist on a headless coordinator with no graphics device,
  and this catches *any* mod that mutates `ModuleRegistry` — not only the one we know about.
- **No headless exemption.** The visual digest exempts a coordinator (it never renders). Modules
  do not: a shipless coordinator still rolls BR loot from that registry, so its module set has to
  match the players'.

The refusal names the dimension that actually diverged. "World generation diverged" would send a
player whose weapon mods differ chasing an imaginary problem, so a modules-only mismatch says so
and names content mods as the fix.

Verify with `moduledigest` on two machines — it prints exactly what the barrier compares.
`tools/module-barrier-test.ps1` stages a divergence (via `modulefake`) and asserts the refusal.

---

## Identity: module id, never weapon id

WeaponForge builds a weapon by **cloning a stock one** and overriding fields. `WeaponBuilder.cs`
sets the clone's module id explicitly:

```csharp
weapon.name = "Forge Weapon " + name;                          // unique
module.name = "Forge Module " + name;                          // unique
idField.SetValue(module, "FORGE-" + name.ToUpperInvariant());  // unique
// ...and nothing sets the weapon's id
```

So a custom weapon's `WeaponData.Id` is **the template's id**. PLASMA LANCE reports
`"Weapon White Popper"` — identical to the stock Popper it came from.

This is not a bug in their mod. Nothing in WeaponForge's own flow resolves weapons by id
(`Module.Restore()` uses the *module* id, which they made unique precisely because save/load
depends on it). It is latent, and only becomes a hazard when something like this mod starts using
`WeaponData.Id` as an identity.

Consequences here, both handled:

- **Diagnostics and any "is this custom?" test must key on the MODULE id.** Keying on weapon id
  would label every stock Popper shot as custom.
- **`ProjectileSync.WeaponIdentityHash` hashes id AND name**, rather than preferring id. The
  asset *name* is unique for a Forge clone, both fields are deterministic per asset, so combining
  them is strictly more discriminating and still agrees across machines. Without this, an entity
  firing a custom weapon — WeaponForge's turret and minion weapons do exactly that — would pass
  the replay identity check while the peer rebuilt the template instead.

### How unique is `FORGE-<NAME>`, really?

| Scope | Guaranteed | Why |
|---|---|---|
| Within one pack | **yes** | `_builtNames` is `OrdinalIgnoreCase`; a duplicate name is skipped at build |
| Against vanilla | **effectively** | no vanilla id has that shape |
| Across locales | **yes** | `ToUpperInvariant()` — no Turkish dotted-İ surprise |
| **Across two packs** | **no** | two authors shipping `Railgun.json` both produce `FORGE-RAILGUN` |

That last row is real, and **replace-not-merge is what closes it**: only one content set is ever
live in a session, so two same-named-but-different weapons cannot coexist. This is the reason that
decision is load-bearing rather than a preference — merging a client's own weapons into a session
would make that row an active bug.

---

## Vanilla data is borrowed, not given

This mod's rule for BR loot is that vanilla data is never edited. WeaponForge does not share it:
`ForgeLootPatch` appends its weapons straight into the live `DropTableWeightedGroup.itemDistribution`
assets, and `ForgeLoadoutPatch` appends into `LoadoutPool.loadouts`. Those are ScriptableObjects —
the additions outlive the run and every later run in the same process.

Two directions, both wrong:

- weapons a player forged in **solo** ride into a session the host may not have;
- the **host's** weapons stay in the player's crate tables afterwards, visible in their next solo
  run until they restart PUNK.

`Content/VanillaContentGuard` takes a pristine copy of every drop group and the loadout pool
*before* a content mod touches them — `Priority.First` prefixes on the very methods it patches,
which is what makes "before" true rather than hopeful — and restores them when a net run starts
and when a session ends.

`Content/ForgeBridge` additionally holds WeaponForge's loot injection off for the duration of a
net run, so custom weapons reach BR loot only through this mod's own seeded, id-ordered drop code
(`BattleRoyaleLootTables`), which the match harness covers. Solo play is untouched: suppression is
released and their group cache cleared at session end.

---

## Tracing a custom weapon across machines

`FireEventMsg` carries the shooter's slot and *which holder* fired — **not the weapon**. The peer
resolves the weapon from the puppet's own module grid, which arrived earlier as a bare string id
over `ModuleGridSync`, which only resolves if that id is registered on the peer.

So a custom weapon can fire locally with the right sprite, animation and sound, and arrive on the
other machine as nothing at all, or as a different weapon. `Content/ForgeDiag` makes that visible:

```
[ForgeDiag] shot LOCAL    'FORGE-MVPLASMALANCE' P2 — first of this kind on this machine
[ForgeDiag] shot REPLAYED 'FORGE-MVPLASMALANCE' P2 — first of this kind on this machine
[ForgeDiag] damage 2 from 'FORGE-MVPLASMALANCE' on P3 (applied from the wire) total=7
[ForgeDiag] pickup 'FORGE-MVSHARDLOBBER' (SHARD LOBBER) by P2
```

Same id on both machines means the chain held. A different id, or a replay line that never
appears, is the bug. Damage is correlated by `ShotId` (the wire carries a shot id but never a
weapon), so a victim can name the weapon that hit it.

Devcmds: `forgeids` prints what is recognised as custom **and** what the ship is actually holding,
with both the weapon and module id — the fastest way to tell "the id sets are empty" from "the
weapon we fired is not in them".

---

## Content and tooling

`tools/forge-content/` holds the test weapons, sprites and sounds, with generators
(`generate.py`, `assemble_sheet.py`) so everything is byte-reproducible — which matters, because
every machine in a session must end up with identical bytes or the digest diverges.

`dumpsprites` exports the game's **own** sprites to PNG at runtime, so custom art can be authored
against PUNK's real palette rather than a guess at it. It needs a rendering instance: the test
harness runs `-nographics` with a null device, where the Blit it relies on produces nothing.

## Tests

| Script | Proves |
|---|---|
| `tools/module-barrier-test.ps1` | a divergent module set is refused, attributed to modules and not world |
| `tools/forge-sync-test.ps1` | identical content across three machines, go-live, equip, grid replication, asset loading, and the LOCAL-vs-REPLAYED weapon id comparison |

Harness note worth keeping: the fire devcmd is `fire <seconds> player <slot>`. `parts[1]` is
parsed as the duration, so `fire player 1 8` makes the parse fail, leaves the duration at 0, and
hits the "fire 0 = stop" branch — silently turning a firing test into a stop command.

---

## Not yet built

Host-to-client **content distribution**: the host offering a manifest on connect, clients
downloading what they lack on a background thread, caching by content hash, and being gated out of
ship selection until they match. Until that lands, every machine must be given the same content
files by hand, and the barrier is what catches it when they are not.

## See also

- [`BATTLE_ROYALE.md`](BATTLE_ROYALE.md) — contested loot and the drop pools this feeds
- [`VANILLA_GOTCHAS.md`](VANILLA_GOTCHAS.md)
