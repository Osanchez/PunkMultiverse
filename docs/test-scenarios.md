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
- KNOWN GAP (do not grade): weapons socketed in Active1-3 ability slots fire via
  WeaponBasedActiveModule (no holder) — CaptureFire has no ship path for them, so
  active-slot projectiles are expected to be invisible remotely until that ships.

---

### Cadence

- **Every build**: 1 (smoke), 5, 10, 15 — fully automatable today.
- **Every protocol change**: + 8, 11, 12.
- **Feature-specific**: the scenario matching the touched system.
- **Blocked on manual/aim input**: 4, 6 (visual), 7 (kill setup), 9 — candidates for a
  future `poke`/`fire` command or SimController integration.
