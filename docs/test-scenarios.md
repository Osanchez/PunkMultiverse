# Multiplayer test scenarios

The scenario catalog for harness runs (see [harness.md](harness.md) for mechanics, and the
`/mp-test` skill for AI-driven execution). Each scenario lists its goal, script, and PASS
criteria as greppable log assertions. Log markers are API: renaming them breaks this suite.

Conventions: `H>` = host devcmd, `C>` = client devcmd. Every scenario starts with
`say scenario:<name> begin` on the driving instance so log windows are bracketable.
Baseline health checks for EVERY run: both logs reach `GO LIVE` with identical level
checksums; no `[Warning]` storms; `terrainMismatch=0`; no `replica failed`.

---

## 1. spawn-replication (smoke — run first, ~2 min)

Runtime spawns must exist identically on both machines. *(Regression test for the two
0.1.87 bugs: spawn-hook registration race, EntityMapItem inactive-bind NRE.)*

- `C> spawn Unit_Grunt rel 10 0` then `H> spawn Crate2 rel 10 0`
- PASS: spawner logs `[Dev] spawned` + `[Spawns] runtime spawn '<id>' -> netId N`;
  the OTHER machine logs `[Spawns] replica '<id>' netId N (Px)` — no `replica failed`.
- PASS: spawner's next `[Population] unitTypes=` census includes the spawned unit type.
- PASS: receiver's EntityAudit does NOT show the netId as starved after 30s (owner is
  streaming its state — proves the LiveEntities registration).

## 2. boss-fire-puppet (the "silent boss" question)

Does a teammate-owned shooter engage a player it only knows as a puppet?

- `C> spawn Enemy_Fly_Laser rel 55 0` (+ `Unit_Cross_JockRocket rel 55 5`), client stays.
- `H> tp <spawnX-8> <spawnY>` (coords from the client's `[Dev] spawned` line). Wait 30s.
- Decision table (also in [enemies.md](enemies.md)):
  | client `[FireAudit] owned #N` | host `[FireAudit] puppet #N` | host sees projectiles | verdict |
  |---|---|---|---|
  | no | no | no | owner AI never fired → targeting/vision vs puppet ships |
  | yes | no | no | fire-STATE sync gap |
  | yes | yes | no | fire-EVENT capture/replay gap for that weapon |
  | yes | yes | yes | working |
- RESOLVED (2026-07-14): spawned enemies are PASSIVE-UNTIL-ATTACKED, not inert — `poke`
  aggros them (verified: poked grunt went fire=2 and its owner/puppet FireAudit pair
  replicated end-to-end). SCRIPT REQUIREMENT: after the client claims its area, WAIT for
  its `[Lease] ... ->P2` commit on the spawn segment BEFORE the host moves in — authority
  follows proximity, and if the host arrives before the client's lease commits, the
  nearest-resident rule gives the host ownership and the puppet-target geometry is lost
  (sticky leases only protect a COMMITTED owner).

## 3. debug-spawn-activation — RESOLVED

Spawned enemies are passive until provoked (vanilla aggro behavior); `poke <netId>` (routed
damage) aggros them reliably. Poked enemies move, enter fire=2, and their fire state
replicates (owner `[FireAudit] owned` + viewer `puppet` pair verified live 2026-07-14).

## 4. dormant-wake-on-hit

Shooting a dormant mannequin must claim + wake it for the attacker.
- Fly toward (not into) unexplored territory until frozen puppets are visible at the edge;
  shoot one. (Human trigger, or SimController input injection if wired later.)
- PASS: `[Damage] dormant hit on #N — claiming its segment for Px` then
  `[Availability] lease flush woke ...` within ~1s; enemy visibly animates and fights.
- FAIL signature: hits land (or are eaten) with NO dormant-hit line → the routing hole
  (dormantDamage has read 0/0 across all sessions — this path is still UNPROVEN live).

## 5. activation-latency (frontier wake)

Enemies must be simulated before the player can meaningfully engage them.
- `H> autofly 20` through fresh territory (or tp in ~80-unit hops). Watch for frozen-looking
  enemies on arrival.
- PASS: `[Availability] lease flush woke N` lines track movement; EntityAudit shows no
  `gate=no-authoritative-pose` entries within TransferRadius (45u) of a ship for >1s;
  no `worst section: Authority` SPIKE lines (batched flush regression check).

## 6. articulated-teleport (fish tail)

Multi-part bodies must survive hard teleports intact.
- `C> spawn Enemy_Fish rel 20 0`; verify replica on host; then force a dormancy cycle:
  client flies away (`autofly`), waits for `P2->dormant` lease lines, host approaches and
  reclaims. Visual check: tail attached. (Automatable later: log child-body distance on
  TeleportWithChildren if regression suspected.)
- PASS: no visibly detached parts; kill it — death chain runs once.

## 7. station-respawn (co-op revive loop)

- Kill one instance's ship (spawn a hostile pack on it: `spawn Unit_Grunt rel 2 0` ×4, or
  fly into hazards), wait for `[Damage] local ship died — broadcast` + spectator.
- Surviving instance unlocks a station (needs the resource cost — F1 menu's infinite
  resource button helps).
- PASS: unlocker logs `[Progress] station upgrade '<id>' broadcast`; dead instance logs
  `applied remote station upgrade` + `[Progress] station unlock — respawned local ship`;
  `stationRespawns=1` in the next `[Counts]`; spectator cam released.

## 8. handoff-kiting (authority migration)

- Both ships near one enemy pack; owner flies away (`autofly`), other stays.
- PASS: `[Lease] segment ... P1(host)->P2` (or reverse) `handoff=` lines; no enemy
  teleports/duplicates (`duplicateNetIds=0`, `identityOverlaps=0`); enemies keep fighting
  the remaining player after handoff (aggro-amnesia check — currently expected-weak, see
  enemies.md GAP).

## 9. projectile-impact-dedup (phantom no-damage hits)

- Sustained fire between a player and enemies with multi-hit weapons for 60s.
- PASS: `duplicateImpactDrops` in `[Counts]` stays flat while hits visibly land.
  Growth correlated with felt no-damage hits = the dedup-key collision (open suspect).

## 10. perf-soak (controlled load)

- `H> spawn Unit_Fly rel <spread>` ×20 (mix types), fight in place 60s.
- PASS: `[Frame]` over=16.7ms bucket stays near baseline; no SPIKE with
  `worst section: EnemySync.Collect` above ~15ms; `Transport.Poll(recv)` max ≤ budget+ε;
  `[GC]` heapDelta swings not growing vs baseline.

## 11. death-avalanche (dormancy burst)

- One player with many owned segments dies (see #7 setup).
- PASS: survivor's log shows the `P2->dormant` wave WITHOUT a `Transport.Poll(recv)`
  SPIKE >50ms and without a `[Hitch]` entry (budgeted-drain regression check —
  the pre-fix signature was a 668ms freeze).

## 12. rejoin-catchup

- Kill the client process mid-run; relaunch it (AutoStart=Join).
- PASS: rejoiner reaches the same world (`catching up` line, checksum match), receives
  kill ledger / upgrades / terrain repair (`[World] terrain repair sent` on host),
  spawns at `LatestStationNetId` if one was unlocked, and its old ship was despawned
  (`disconnectDespawns` incremented) — no ghost ship.

---

### Cadence

- **Every build**: 1 (smoke), 5, 10 — fully automatable today.
- **Every protocol change**: + 8, 11, 12.
- **Feature-specific**: the scenario matching the touched system.
- **Blocked on manual/aim input**: 4, 6 (visual), 7 (kill setup), 9 — candidates for a
  future `poke`/`fire` command or SimController integration.
