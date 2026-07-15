---
name: mp-test
description: Run an automated PunkMultiverse two-instance multiplayer test scenario (or the smoke suite) end-to-end — build+deploy, launch loopback pair, drive via devcmd files, assert against logs, teardown — and report a verdict. Use when asked to test multiplayer sync, reproduce a sync bug, or validate a build before a playtest. Args: a scenario name from docs/test-scenarios.md (e.g. "spawn-replication", "boss-fire-puppet", "perf-soak"), or "smoke" for the default set.
---

# PunkMultiverse multiplayer test run

You are driving TWO real game instances on this machine through a scripted scenario and
grading the result from their logs. Read these two docs before acting (repo paths):
`PunkMultiverse/docs/harness.md` (mechanics) and `PunkMultiverse/docs/test-scenarios.md`
(the scenario catalog with PASS criteria). The requested scenario comes from the args;
default to `spawn-replication` if none given.

## Layout (this machine)

- Repo + source: `C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest\PunkMultiverse`
- HOST install: `C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest`
- CLIENT install: `C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Test2`
- Per-install paths (substitute install root):
  log `<install>\BepInEx\LogOutput.log`, config + command file
  `<install>\BepInEx\plugins\PunkMultiverse\config.cfg` / `devcmd.txt`
- Deploy: `powershell -File <repo>\build.ps1` (host) and `... -GameDir "<client install>"`.

## Hard rules

1. **Process discipline**: the user often has their OWN Punk.exe running. Record the PIDs
   you launch; at teardown kill ONLY those PIDs. Never `Stop-Process -Name Punk`.
2. Deploy the current build to BOTH installs before launching — mixed versions invalidate
   everything (the handshake logs `host mod vX.Y.Z`; verify both logs agree).
3. Foreground `sleep` is blocked in this environment — run waits as background bash
   `until <grep>; do sleep 5; done` loops (with a timeout guard) and act on notification.
4. If the session never reaches GO LIVE in 4 minutes, capture both log tails in the report
   and tear down — don't retry blindly.
5. BepInEx recreates `LogOutput.log` per launch; there is no need to rotate logs, but read
   NOTHING from before your own launch timestamps.

## Required config (verify, arm if missing)

Both installs `[Transport] Transport = Loopback`; `[Debug]` section:
host `AutoStart = Host`, `AutoReady = true`, `AutoLaunchRun = true`;
client `AutoStart = Join`, `AutoReady = true`;
both `CommandFile = devcmd.txt`, `AutoFlySeconds = 0`, `DebugMenuKey = true`.
Delete stale `devcmd.txt` files before launching.

## Run procedure

1. Build + deploy both installs; abort on compile errors.
2. Launch host `Punk.exe` (working dir = its install), wait ~8s, launch client. Record PIDs.
3. Wait until BOTH logs contain `GO LIVE` (client may show `catching up` — acceptable),
   then wait 10 more seconds for settling. Verify identical `level generated, checksum`.
4. Drive the scenario: write command lines to the correct instance's `devcmd.txt`
   (`say scenario:<name> begin` first; the mod polls at 2 Hz and empties the file).
   Command results append to `devout.txt` in the same folder (read it, then truncate) and
   mirror to the log as `[Dev] ...`. Commands: spawn, tp, poke (routed damage — wakes
   dormant targets, exercises the request pipeline), fire <secs> [at <netId> | dir dx dy]
   (hold the local ship's trigger, optionally steering the barrels — stand off 8-12u),
   owner [pos] (segment lease owner — poll until '= P2' to await a lease commit before
   moving the other ship in), entities (structured
   nearby-unit dump: netId/type/pos/owner/puppet/hp/fire; ships labeled Px(local/puppet)),
   status, autofly, say.
   Use `entities` to discover netIds for poke and to assert world state directly instead
   of inferring from telemetry. Positions: parse `spawned <id> at X,Y` results.
5. Wait the scenario's combat/soak window (30-60s) via a background until-loop.
6. Collect assertions per the scenario's PASS list. Core marker vocabulary:
   - `[Dev]` command echoes ·· `[Spawns] runtime spawn` (sender) / `replica '<id>'`
     (receiver; `replica failed` = bug, stack follows)
   - `[FireAudit] owned #N` (owner's AI pulled trigger) / `puppet #N` (state reached viewer)
   - `[Availability] lease flush woke N` ·· `[Damage] dormant hit on #N`
   - `[Lease] segment (x,y) A->B epoch=` ·· `[Progress] station upgrade ... broadcast`
   - `[Counts]` cumulative counters (duplicateImpactDrops, stationRespawns, terrainMismatch,
     disconnectDespawns...) ·· `[Frame]`/`[Profile] SPIKE`/`[PatchProfile]` for perf
   - `[EntityAudit] ... starved=#N/Type@(seg) gate=...` for starvation/puppet state
7. Teardown: kill the recorded PIDs only. Confirm they're gone.
8. Report: scenario, PASS/FAIL per assertion with the exact log lines as evidence, any
   UNEXPECTED warnings/errors observed outside the assertions (always grep both logs for
   `Warning|Error|Exception|SPIKE|replica failed` and triage), and concrete next steps.
   If a finding is code-diagnosable, locate the responsible code before reporting.

## Known quirks (context that saves you an hour)

- Spawned enemies are PASSIVE until damaged — `poke` them to aggro before grading fire
  behavior. For client-owned-vs-host-puppet geometry, wait for the client's `[Lease] ->P2`
  commit on the spawn segment BEFORE moving the host in, or ownership flips to the host.
- Minion prefabs have no map icon — they mask EntityMapItem-class replica bugs; always
  smoke-test with a regular enemy AND a crate.
- The `[FireAudit]`/`[Dev]`/`[Spawns]` markers are per-entity rate-limited or one-shot;
  absence within a bracketed window is meaningful, absence across a busy session is not.
- `exit code 1` from an assertion command is often just `grep -c` finding 0 matches —
  grade on content, not exit codes.
- The stale-instance trap: a user instance may predate your run by hours; check
  `Get-Process Punk | Select Id,StartTime` BEFORE launching so your PID set is unambiguous.
- `probe <netId>` is the FIRST diagnostic for any behavior question — it reads the enemy's
  own AIAgent/Vision (seen/enemies/target/shooter + probe2's scanNow/mask/rawHits). An
  owned spawn showing `PUPPET aiOn=False` on its spawner = the mutual-puppet zombie
  signature (fixed 0.1.90 via spawner lease-pull; watch for regressions).
- Projectile hits KNOCK SHIPS BACK — drifting ships and deaths near platform edges during
  fire tests are physics, not desync. Re-assert positions with `tp` between phases.
- Scenario-killers learned the hard way: NEVER `spawn ... rel 0 0` (contact damage kills
  the ship → party-wipe reset); spawn coords must be VETTED open space (near a spot a
  ship physically occupies) or enemy Vision may be terrain-blocked and the test reads as
  a false sync bug; aggro'd mobile enemies chase and wreck the geometry (prefer turrets
  for positional tests); a long host stall can trigger FALSE host-migration on loopback —
  the promoted client can't bind the shared port and its session dies (check for
  "promoted to host (migration)" in a wedged client's log).
