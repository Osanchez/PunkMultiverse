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
- **FINAL VERDICT (2026-07-14, probe-verified): puppet ships are fully visible, targetable,
  and FIRED UPON — there is no puppet-targeting bug.** Probe evidence: an owned grunt with
  only a puppet ship in range showed `enemies=1 target=shipP2(puppet)/visible`, fired, and
  the viewer logged the matching `puppet` FireAudit. The silent-enemy phenomenon decomposed
  into real bugs, all fixed: (1) runtime-spawn MUTUAL-PUPPET ZOMBIE — `Owners[netId]` was
  written but never consulted for non-minions, so a spawn landing in a foreign/dormant
  segment became a puppet on its own spawner while the lease holder also puppeted its
  replica: nobody simulated it (fixed 0.1.90: the spawner pulls the lease, same mechanism
  as wake-on-hit); (2) spawn-registration race (0.1.87); (3) EntityMapItem inactive-bind
  NRE killing replicas (0.1.87); (4) the ORIGINAL natural silent mini-boss = the
  pre-0.1.85 dormant mannequin (frozen attack-loop animator + activation lag), fixed in
  0.1.85. FireAudit stays armed to catch any natural counterexample.
- Scenario rules learned: use `probe` FIRST (senses beat fire-counting); expect projectile
  KNOCKBACK to displace ships (drift/falls near platforms are physics, not desync —
  re-assert positions with `tp` between phases); verify spawn ownership with `probe`
  (aiOn=False PUPPET on the spawner = the zombie signature).

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

## 13. stall-reconnect (loopback false-migration guard) — VERIFIED 2026-07-15

- Both live, then HOST devcmd: `stall 15` (freezes the host main thread past the 10s
  loopback peer timeout — same signature as a level-load/GC stall).
- PASS (client log, in order): `[Loopback] peer 1 disconnected (timeout)` →
  `treating as a host stall; reconnecting in place` → `reattached to new host (slot 1,
  host slot 0)`. Host log: `reattached after host migration` (the resume path) and NO
  second `joined` line (no duplicate slot). Post-reconnect `status`/`entities` on both
  sides still InGame with mirrored state.
- FAIL signatures: `loopback host election starting` or `promoted to host (migration)`
  after a mere stall (regression to false migration), a duplicate `joined (mid-run)` on
  the host (reconnect beat the old route's timeout and got a second slot), or the client
  failing with `Could not reach the host to resume the run.` while the host lives.
- A REAL host departure (kill the host process) should still migrate: client elects
  itself, binds the now-free port, `promoted to host (migration)`.

## 14. weapon-loadout-sync (secondary/build replication) — VERIFIED 2026-07-15

- Both live: `loadout` on both sides must agree for every ship (holder weapons AND grid
  cluster ids). Then CLIENT: `equip <weaponModuleId> sec`, wait ≥6s (grid broadcast is
  5s-gated), HOST `loadout`: the P2 puppet must show the new secondary weapon and module.
- Then CLIENT `knockback off` (both sides) + `fire 5 sec dir 0 1`; HOST `status`:
  `shipFireReplays` must increase (host replayed the secondary shots) with zero
  `has no weapon on the puppet` / `replay failed` warnings.
- Log markers: `[Grid] module grid broadcast (raw -> packed bytes)` on the owner,
  `[Grid] applied Px's module grid` on every viewer. A `[Grid]`-silent session is the
  historical failure mode (sync dead since inception via a bad reflection lookup —
  masked because default builds are identical on all machines).
- FAIL signatures: `MessageSize` send failures (grid memento outgrew compression),
  `[Fire] Px's holder N has no weapon on the puppet`, loadout divergence after 10s.

## 15. multi-weapon-bidirectional (both slots, both directions) — VERIFIED 2026-07-15

Proves every weapon slot's projectiles are visible on the OTHER machine, in both
directions, using `shipFireReplays` (in `status`) sampled between fire windows —
each window's delta attributes replays to one slot of one shooter.

- Setup: both live, `knockback off` on BOTH. Pick two DISTINCT plain projectile
  weapons from `equip list` (avoid minion/summon weapons — their spawns add noise;
  Weapon_Fly spawns flies). HOST: `equip <A> sec`. CLIENT: `equip <B> sec`. Wait ≥8s.
- Loadout parity (assert first, both sides): on the host, P2(puppet) row must equal the
  client's P2(local) row; on the client, P1(puppet) must equal the host's P1(local) —
  holder weapons AND gridPri/gridSec ids. Different secondaries on each ship also catch
  cross-application bugs (a grid applied to the wrong slot/ship).
- Fire matrix — four 4s windows, sampling `status` on BOTH sides before/after each:
  1. HOST `fire 4 dir 0 1`        → CLIENT shipFireReplays delta > 0 (host primary)
  2. HOST `fire 4 sec dir 0 1`    → CLIENT delta > 0 again (host secondary)
  3. CLIENT `fire 4 dir 0 1`      → HOST delta > 0 (client primary)
  4. CLIENT `fire 4 sec dir 0 1`  → HOST delta > 0 again (client secondary)
  The non-observing side's counter must NOT move (nobody replays their own shots).
- Zero tolerance in both logs: `has no weapon on the puppet`, `replay failed`,
  `MessageSize`, `[Grid] apply failed`.
- Fire SIDEWAYS (`dir 1 0`), never up, and avoid explosive weapons in the test slots:
  rockets fired upward at spawn splash the station's overhead terrain and party-wipe the
  run (learned live — the wipe resets counters, equips, and both grids mid-scenario).
  Recoil may drift the shooter — irrelevant here, counters are the assertion.
- Command-file discipline: back-to-back devcmd writes <4s apart can race the 2Hz
  rename-consume and drop one — batch each sample point into a single multi-line write.
- First run's numbers (White Popper primary / Rocket Rookie + BouncerRed secondaries,
  4s windows): host fire → client replays 0→16→19; client fire → host replays 0→16→53;
  the shooter's own counter stayed 0 throughout; zero warnings.
- Active-slot window (FIXED + VERIFIED 2026-07-15, v0.1.95 — holder ids 2/3/4 on the
  wire): CLIENT `equip <weapon-based active id> act1` (pick from `equip list`'s
  `(active)` entries), wait ≥8s, assert host loadout shows it in `acts=`; then CLIENT
  `useactive 1` → HOST shipFireReplays delta > 0. First pass: MISSILE_SURGE, host 0→1,
  zero warnings. HAZARD: actives fire from the ship's own position (game passes a
  barrel at owner pos) — explosive actives can SELF-KILL the firing ship; sample the
  observer counter after the FIRST activation, and treat a `status ... DEAD` on the
  shooter as an invalidated window, not a sync failure.

## 16. minion-sync (summoned drones tracked remotely) — VERIFIED 2026-07-15

- HOST: `equip <SpawnMinionModuleData id> act1` (from `equip list`'s `(minion)` entries,
  e.g. COMBAT_DRONE), then `useactive 1` — repeat spaced ≥5s apart until
  `[Spawns] runtime spawn 'Ally_Drone'` appears (the first activations can fail silent
  resource gates). Extract the netId from that line.
- `sync <netId>` is the assertion tool (one line of send/receive truth per machine):
  - OWNER must show `live=True fixed=True owner=P1 lastSent=<0.5s ago`.
  - VIEWER must show `live=True puppet=True owner=P1 recvFrom=P1/e0` — and its
    `entities` position for the drone must CHANGE across ≥15s samples (orbits the ship).
  - Viewer `owner` must STAY the summoner across ≥40s (a flip to the viewer's own slot =
    the starved-recovery theft regression; fixed owners are exempt from promotion).
- Both logs: zero `owner wiring failed`, zero `DROPPED starved request ... gate=fixed-owner`
  storms (one-off drops during the spawn window are fine).
- FAIL signatures (all were live bugs): viewer `live=False` (replica missing from
  LiveEntities — Align not run on the replica side); viewer `recvFrom=never` while owner
  `lastSent` ticks (epoch-0 fixed groups gated behind the lease-baseline handshake);
  `owner wiring failed: Unit cannot be converted to SavableEntity`.

## 17. entity-sweep (one of everything — the pre-playtest zoo)

Spawns one of every combat-relevant entity in batches, alternating owner direction, and
asserts the full lifecycle for each: replicates → wakes on hit → HP syncs → shooters fire
VISIBLY on the other machine → damage reaches the ships' pipeline (god-shielded) → dies on
both machines → droppers/containers drop on both machines. Replaces "find them one by one
in a playthrough".

Setup (both instances): normal harness config **plus `[Diag] SyncDiagnostics = true`**
(loot assertions read `[Diag:Loot]` lines). After GO LIVE send `god` to BOTH instances —
the local ship's damage blocks at the routing chokepoints while every hit still audits as
`[CombatHit] ... applied=False` with source attribution. `god off` (or run end) restores.

Algorithm (driver-side; ~1 min per batch of 4-5):

1. `roster damageable` on the host → the spawn list with per-entity flags
   (`unit/body/damageable/shooter/loot`). Skip ships and anything already covered by a
   bespoke scenario (minions → 16). Note the batch's expected runtime netIds: host spawns
   are `1048576 + n`, client spawns `2097152 + n`, n = that machine's running spawn count.
2. Per batch: the OWNER instance (alternate host/client per batch) spawns 4-5 entries in a
   ring (`spawn <id> rel ±8..12 ±4`); wait 3s.
   - ASSERT (viewer): every id in `entities 40` with `puppet` + `owner=P<spawner>`
     (or post-handoff owner — any owner is fine; `dormant` after 5s = FAIL).
3. VIEWER pokes each netId once (`poke <id> 3`); wait 3s.
   - ASSERT both sides: `entities` hp < 1.00 for each (wake + cross-machine damage);
     a dormant target must produce `[Damage] dormant hit ... claiming` (host log) and
     still end up hp-synced (the wake path).
4. Fire window: wait 8s with both ships parked in range.
   - ASSERT for every `shooter=True` entry: owner log `[FireAudit] owned #id`, viewer log
     `[FireAudit] puppet #id`, and viewer `[ProjectileTimeline] ... replayUnarmed` DID NOT
     grow during the window (the armed-replay regression signature). `fireUnresolved`
     growth on the owner = capture regression (names the weapon in a warning).
   - ASSERT ship-damage pipeline: ≥1 `[CombatHit] ... entity#<id> ... applied=False` on
     whichever machine's ship was targeted (god shield keeps it alive AND audited).
5. Kill pass: `poke <id> 9999` each from the VIEWER; wait 3s.
   - ASSERT both sides: `sync <id>` → `killed=True` on host AND client.
   - ASSERT for every `loot=True` entry: `[Diag:Loot] ... dropped loot (instanced` on
     BOTH machines (both ships are in LootReachRadius) — this is the "container contents
     visible on host and client" check, per the instanced-loot design (contents roll the
     same on every machine via the deterministic per-death seed).
6. Between batches: `entities 40` must show no leftovers; re-`tp` ships to their marks if
   knockback drifted them (or `knockback off` both at setup).
7. End: final `[Residency]`/`[ProjectileTimeline]`/`[Counts]` dumps from both logs; FAIL
   on any `Exception`, `replica failed`, `no armed Shooter`, `unresolved entity weapon`.

Report one row per entity: replicated / woke / hp-synced / fired-visibly / ship-hit-audited /
killed-both / loot-both — PASS requires every applicable column.

HAZARDS learned: spawn positions must be vetted open space near the ships (mid-air spawns
fall — grounded types end ~13u below the spawn echo; assert against `entities`, not the
spawn line); melee chasers lock aggro on first damager — poke from the machine you want
targeted; never leave a batch aggro'd while setting up the next (kill pass is mandatory).
**Use `spawn <id> rel <x> <y> pin`** — pinned spawns hold their exact offsets (rotation
free, AI/fire live), so batches stay geometric, `fire ... at <netId>` always connects,
and mobile types can't scatter or chase ships across the map. `god` also grants infinite
weapon resource, so `fire 30 at <id>` bursts never run dry.

---

### Cadence

- **Every build**: 1 (smoke), 5, 10, 15 — fully automatable today.
- **Every protocol change**: + 8, 11, 12.
- **Feature-specific**: the scenario matching the touched system.
- **Blocked on manual/aim input**: 4, 6 (visual), 7 (kill setup), 9 — candidates for a
  future `poke`/`fire` command or SimController integration.
