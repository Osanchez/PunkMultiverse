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
poke <netId> [amount=5]     # ROUTED damage: puppets forward to owner, dormant queues a
                            # wake-on-hit claim — a projectile hit minus the projectile
entities [radius=60]        # structured dump of nearby units -> devout.txt:
                            # netId, type, pos, dist, owner/puppet, hp, fire state
status                      # session state, slot, host?, ship position -> devout.txt
fire <seconds>              # hold the local ship's trigger (Shooter.SetShooting — the
                            # game's own API); fire 0 stops early
fire <seconds> sec          # ...the SECONDARY holder's shooter instead of the primary
fire <seconds> [sec] at <netId>   # ...steering the barrels at that entity every frame
fire <seconds> [sec] dir <dx dy>  # ...or in a fixed direction. Stand off 8-12u from targets
loadout                     # every ship's holder weapons + grid weapon/active clusters +
                            # module count — the weapon-sync diagnostic (puppet == owner)
equip <id|list> [sec|act1|act2|act3]  # install a weapon, weapon-based active, or minion
                            # (SpawnMinionModule) module on the LOCAL ship's grid — the
                            # real gameplay path, so ModuleGridSync must replicate it.
                            # `list` prints id + name, tagged (active) / (minion).
useactive <1-3>             # trigger an ability-slot module (the ModuleActivator path,
                            # minus cooldown). Weapon actives fire FROM THE SHIP'S OWN
                            # POSITION — explosive ones can self-kill the test ship.
owner [x y | rel dx dy]     # segment + current lease owner at a position (default: ship)
                            # -> poll until "= P2" to wait for a lease commit
probe <netId>               # the target's OWN senses: AIAgent/Vision seen/target/shooter
                            # + probe2 line (forced scan, layer mask, raw physics overlaps)
sync <netId>                # one line of sync truth for one entity on THIS machine:
                            # live/puppet/killed/fixed/owner, lastSent age (am I streaming
                            # it?), recvFrom (am I receiving it?), simSegment, lifetime.
                            # Run on BOTH sides to bisect owner-vs-viewer in one step.
knockback off|on            # suppress projectile impulses ON THIS MACHINE — fire tests
                            # stop shoving ships off their marks (send to BOTH instances)
god [on|off]                # dev shield: local ship damage blocks at the routing
                            # chokepoints (every hit still audits as [CombatHit]
                            # applied=False with source) AND weapon resource is
                            # infinite + tanks refill — fire forever, survive the zoo
roster [unit|damageable]    # every spawnable entityId with class flags
                            # (unit/body/damageable/shooter/loot) -> devout.txt
spawn <EntityId> ... pin    # trailing `pin` freezes the spawn's POSITION in place
                            # (rotation free: turrets aim, AI/fire live) — park test
                            # targets at exact offsets without fighting chase AI
pin <netId> [off]           # same freeze for an already-live entity; only bites on
                            # the machine that SIMULATES it (warns if run on a puppet)
stall <secs>                # freeze the main thread 1-25s: reproduces a load/GC stall
                            # (exercises the loopback reconnect-in-place path)
autofly <seconds>           # re-arm scripted flight mid-run
say <marker text>           # timestamped marker in the log — bracket your scenarios
# lines starting with # are ignored
```

**Response channel**: every command's result is appended to `devout.txt` next to the
command file (`[<mono>] <result>` lines) AND mirrored to the log as `[Dev] ...`. The
driving harness reads devout.txt and truncates it — no log parsing needed for queries.

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
