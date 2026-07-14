# Automated two-instance test harness

Reproduce multiplayer scenarios deterministically — no human piloting, no boss hunting.
Combines the loopback transport, clickless autostart, the game's own developer debug menu,
and a polled command file that lets an external driver (a script, or Claude) act inside a
running session and assert against the logs.

## The pieces

| Piece | Config (`[Debug]` in config.cfg) | What it does |
|---|---|---|
| Loopback transport | `[Transport] Transport = Loopback` | two instances on one PC, no Steam |
| Autostart | `AutoStart = Host` / `Join`, `AutoReady = true`, host `AutoLaunchRun = true` | boots straight into a running co-op session |
| Scripted flight | `AutoFlySeconds = N` (or `autofly N` command) | separates ships without input injection |
| Debug menu | `DebugMenuKey = true` → **F1** in game | game's own dev menu: spawn lists, noclip, loadouts, free camera |
| Command file | `CommandFile = devcmd.txt` | the automation hook (below) |

**Spawn replication is already generic**: every mid-run `EntityGameObjectManager.CreateEntity`
(debug menu clicks, the `spawn` command, spawner enemies) broadcasts `ENTITY_SPAWNED` with a
runtime netId and joins the normal authority pool — identical entity on every machine.
Exclusions by design: ships, and per-player loot.

## Command file protocol

Set `CommandFile = devcmd.txt` in an instance's plugin config. The mod polls
`BepInEx/plugins/PunkMultiverse/devcmd.txt` twice a second, executes each line, empties the
file, and logs every action as `[Dev] ...` in `LogOutput.log`.

```
spawn <EntityId> [x y]      # world position; default = 3 units right of the local ship
spawn <EntityId> rel dx dy  # relative to the local ship
tp <x> <y>                  # teleport local ship (children carried, velocity zeroed)
tp rel <dx> <dy>
autofly <seconds>           # re-arm scripted flight mid-run
say <marker text>           # timestamped marker in the log — bracket your scenarios
# lines starting with # are ignored
```

EntityIds are the prefab ids ([enemies.md](enemies.md) roster: `Unit_Fly`, `Enemy_Turret_Worm`,
`Crate2`, `Box_Money`, ...). Each instance has its OWN command file — drive host and client
independently.

## Worked example — reproduce "teammate-owned boss won't shoot"

Directories: main install = host, `PUNK Playtest - OD Test2` = client. Both configs:
Loopback + AutoReady + `CommandFile = devcmd.txt`; host adds `AutoStart = Host`,
`AutoLaunchRun = true`; client adds `AutoStart = Join`.

```powershell
# 1. launch both (order matters: host first)
& "C:\...\PUNK Playtest\Punk.exe"; Start-Sleep 5
& "C:\...\PUNK Playtest - OD Test2\Punk.exe"

# 2. wait for go-live on both logs
#    grep "GO LIVE" both LogOutput.log files (poll)

# 3. CLIENT devcmd.txt: park and spawn the boss it will own
say scenario:boss-owned-by-client begin
spawn Enemy_Turret_Worm rel 10 0

# 4. HOST devcmd.txt: fly to the client's area (or tp next to the boss position
#    echoed in the client log's "[Dev] spawned ..." line)
tp <x-10> <y>

# 5. Let them fight ~10s, then close both games (logs auto-flush; quit uploads optional)
```

Assertions, all greppable:
- client log `[Dev] spawned Enemy_Turret_Worm ...` + host log `[Spawns] runtime spawn ...`
  → replication worked
- client `[FireAudit] owned #N entered fire=..` → the owner's AI pulled the trigger
- host `[FireAudit] puppet #N entered fire=..` → fire state reached the viewer
- host `Projectile.MarkVisual calls` in `[PatchProfile]` → replayed projectiles rendered
- decision table in [enemies.md](enemies.md) maps which lines appeared → root cause

## Other ready-made scenarios

- **Dormant wake-on-hit**: host `spawn` an enemy far away (`spawn Unit_Grunt 200 0` relative
  to nothing near anyone), fly within sight, shoot it → expect
  `[Damage] dormant hit on #N — claiming its segment` + `[Availability] lease flush woke`.
- **Articulated teleport**: spawn `Enemy_Fish`, let the owner die/dormancy-commit, reclaim —
  tail must arrive with the body.
- **Station respawn**: kill one instance's ship (`autofly` it into hazards or spawn a pack on
  it), other instance unlocks a station → expect `station upgrade '<id>' broadcast` +
  `respawned local ship at the new station` + `stationRespawns=1` in `[Counts]`.
- **Perf soak**: `spawn` N enemies in a loop around both ships and watch `[Profile]` /
  `[Frame]` / SPIKE attribution under load you control exactly.

## Notes

- Debug menu (F1) and all `[Debug]` config are dev-only and default OFF — ship configs are
  unaffected.
- The `[Dev]`/`[Spawns]`/`[FireAudit]`/`[Availability]`/`[Damage]` log lines are the harness's
  assertion surface — treat renaming them as a breaking change to the test suite.
- Runtime spawns are owned by the spawning machine until normal authority handoff — spawn on
  the instance that should OWN the entity for your scenario.
