# PunkMultiverse

Four-player online co-op retrofitted into **PUNK** — a closed-source Unity game with no
multiplayer, no source access, and no server build — as a single BepInEx plugin.

The same assembly runs as a player's client, as a listen host, and as a headless dedicated
server. Players join a friend's Steam lobby with a pasted code or a public server with an
IP; the world, the enemies, and the destruction are shared, while loot and builds stay
personal.

- **Scope:** ~30k lines of C#, one DLL, four transports, 4 players per session.
- **Deployment:** Steam P2P, or a Dockerized headless server (Wine) on a Pelican panel.
- **Modes:** co-op, plus a full **[battle royale](#8-a-second-game-mode-battle-royale)** —
  drop selection, a closing zone, PvP, contested loot — built with no new sync primitive.
- **Measured:** flat server cost from 1→3 players (3.3 / 2.9 / 3.3 ms per frame),
  0.1 ms/tick client sync overhead, ~7 ms snapshot jitter through the full stack.

Player-facing docs: **[Install & play](docs/PLAYING.md)** · Test plans:
**[TESTING.md](docs/TESTING.md)** · Deep dives: **[docs/](docs/)**

---

## 1. The problem

Adding co-op to a finished single-player game is not "add sockets." The constraints below
determined nearly every decision that follows.

| Constraint | Consequence |
|---|---|
| **No source code.** The game is shipped IL; all integration is Harmony patching against decompiled internals. | Never reimplement game logic. Damage, physics, AI, and loot must run through the vanilla code paths, or single-player and multiplayer diverge in behavior. |
| **The world is 4,000,000 destructible cells** plus ~7,000 entities. | Replicating world state is off the table. The only affordable model is *deterministic generation + divergence replication*. |
| **The engine is single-threaded** and the game was never written to be authoritative. | Anything that must not stall (packet relay) has to leave the frame loop; anything expensive (ownership lookups) must not touch engine APIs per-entity per-tick. |
| **Players are not sysadmins.** | Zero configuration to join anything: no port forwarding for Steam play, no transport selection, no config file editing. One paste. |
| **Windows-only build, playtest branch.** | The dedicated server is the same Windows binary under Wine in a container. Platform overhead is a cost to design around, not eliminate. |
| **Sessions are long and unattended.** | No unbounded growth anywhere, and a server with nobody at the keyboard must recover from every terminal state by itself. |

---

## 2. System model

A star: every client holds exactly one session link to the host. Clients never exchange
control traffic with each other; the host is the single ordering point for durable facts.

```
   Steam P2P / SDR / raw UDP / loopback
                    │
   ┌──────────┐     │     ┌──────────┐
   │ Client A │◄────┼────►│   HOST   │◄────► │ Client C │
   └──────────┘     │     │  (slot 0 │       └──────────┘
   ┌──────────┐     │     │   or a   │
   │ Client B │◄────┴────►│ shipless │
   └──────────┘           │  server) │
                          └──────────┘
        simulation lives with the clients, not the host
```

**Two host flavors, one code path.** A *listen host* is a player in slot 0. A *dedicated
coordinator* is the same build seated in a reserved non-player slot (4), shipless: it
simulates nobody, owns nothing, and exists to referee. No server fork exists to maintain.

**Four transports behind one interface** (`ITransport`): Steam P2P (friends), SteamServer
over SDR (server identities), raw UDP via LiteNetLib (dedicated/LAN), and loopback (tests).
Selection is inferred from what the player pasted — a `PMV-` code, an `ip:port`, or a
SteamID64 — never from configuration.

---

## 3. Consistency model

### Generate the world; replicate only the divergence
Each machine generates the world locally from a replicated seed. What crosses the wire is
the *delta* from that deterministic baseline: destroyed terrain cells, kills, runtime
spawns, progression.

Determinism is verified rather than assumed. Before anyone plays, every machine reports a
fingerprint — terrain checksum, entity count + digest, plant count + digest, and a rendered
tile-variant digest — and the host holds a **go-live barrier** until they match. A mismatch
aborts the run with the differing values named; the alternative (starting a session that is
already diverged) produces bugs no amount of runtime healing can fix. A headless server,
which renders nothing, contributes data-only fingerprints and sits out the visual check
while rendering clients cross-check visuals against each other.

### Identity is deterministic, never positional
Entities are keyed by a deterministic instance counter assigned during generation, exchanged
as a manifest at go-live. Position-based identity was tried and abandoned: it cannot survive
movement, and it silently mismatches under floating-point drift.

---

## 4. Authority: leases, residency, dormancy

The world is a fixed grid of 25-unit **segments**. Authority is granted per segment, not per
entity, and only to a peer whose game currently has that segment streamed in.

```
 residency reports (per client)      lease table (host)        simulation
 "I have segments loaded: {…}"  ──►  segment → (owner, epoch)  ──►  owner runs the AI,
                                                                    everyone else runs a
                                                                    muted interpolated puppet
```

**Why residency gates authority.** Simulation capability is streaming-dependent — a client
whose game has unloaded a region physically cannot simulate it. Granting authority outside
reported residency produces owners that cannot exercise ownership, which manifests as frozen
enemies. Leases therefore only ever land inside residency, with a short grace window to
absorb one-frame streaming flicker.

**Dormancy is a first-class state.** Segments nobody streams are owned by nobody and frozen
by agreement. This is what makes a 7,000-entity world cheap: only the ~150 entities near
players are live at any moment, and the rest cost exactly zero.

**Handoff is prepared, not assumed.** Crossing a boundary triggers prepare → ack → commit
with epochs; receivers reject state stamped with a stale epoch, which is what prevents two
machines from briefly simulating the same enemy (split-brain).

**Rescue for the gaps.** A slow poll looks for puppets near a live player that have stopped
receiving updates and promotes ownership locally after a patience window — the safety net for
any entity that falls between the lease machinery's cracks.

**Rejected alternatives:** *server-authoritative simulation* (the server would have to run
the whole game — impossible on the target hardware, and it would add a round-trip to every
local action); *closest-player-owns recomputed continuously* (thrashes ownership at
boundaries and fabricates authority for peers that unloaded the area).

---

## 5. The wire

| Channel | Delivery | Carries |
|---|---|---|
| **Control** | reliable ordered | handshake, roster, run start, go-live, manifests, baselines |
| **Events** | reliable ordered | kills, terrain diffs, progression, lease traffic, residency |
| **Combat** | reliable ordered | fire, impacts, damage requests |
| **State** | unreliable + FEC | ship and entity snapshots (20 Hz) |

Separate ordered streams exist so a terrain burst can never head-of-line block a combat
event. State is unreliable by design: a snapshot that arrives late is worthless, so it is
never retransmitted — the interpolation buffer and parity handle loss instead.

**Send scheduling is a priority accumulator, not fixed tiers.** Each entity accrues send
weight per tick (combat proximity, movement, distance); it transmits when the accumulator
fills. Skipped entities keep accruing, so nothing starves, and rates degrade continuously
rather than snapping between tiers. Per-viewer byte budgets then cap the presentation plane
per link, and interest routing drops groups the receiver has no residency in — coarse filter
first, always.

---

## 6. Presentation pipeline

Smoothness is a separate problem from correctness, and it is solved with three well-known
techniques rather than heuristics:

1. **Sender-stamped snapshots.** Snapshots carry the *sender's* clock, so puppets interpolate
   on the sender's even spacing instead of replaying network jitter as motion.
2. **NTP-style clock filtering** (Mills). Cross-machine offset chases the *minimum*-transit
   sample of a sliding window through a bounded slew, exploiting the fact that transit noise
   is one-sided — packets arrive late, never early. Averaging every sample (the naive
   approach) feeds queueing noise straight into the rendered timeline.
3. **Percentile playout targeting** (NetEQ-style). Interpolation delay tracks the **p98 of
   observed snapshot lateness** over a sliding window: precisely enough buffer for the
   network actually present, re-derived continuously, instead of a guessed safety multiplier.

Loss is absorbed by **XOR forward error correction** on the state channel — one parity packet
per four, so any single loss in a group reconstructs on arrival with no retransmit
round-trip. At single-digit KB/s, the ~25% overhead is free; the latency it saves is not.

Rendering interpolates with Hermite curves and decays positional error rather than snapping.
Because fixed-step metrics cannot observe render-level judder, the mod carries a render-frame
benchmark (`fpsbench`) that samples every drawn frame and reports the distribution.

---

## 7. Convergence, failure, and recovery

**Durable facts are idempotent events kept as ledgers** on every machine — kills, terrain
diffs, runtime spawns, progression. That single choice collapses three features into one
mechanism:

- **Late join** — replay the ledgers.
- **Rejoin after a crash** — replay the ledgers; the slot was reserved, the build restored.
- **Host migration** — a client is elected, and it already holds identical ledgers to serve.

Terrain catch-up streams nearest-the-player-first under a per-frame byte budget with
backpressure, so a fully converted map converges without a size cutoff and the area around a
joining player is correct within seconds.

**Damage resolves on the victim's machine**, always through the vanilla pipeline: enemy fire
hit-tests locally against you; player-vs-player routes to the victim's authority. This makes
"you are only hit by shots that visibly reached you" true by construction, and keeps shields,
armor, and effects identical to single-player.

**Failure modes are explicit:**

| Failure | Behavior |
|---|---|
| Client disconnects | Slot reserved; puppet suspended; entities it owned re-lease or go dormant |
| Host disconnects (Steam) | Lobby ownership election promotes a client; same code keeps working |
| Peer's world diverges | Generation barrier refuses the run; rejoin with a mismatched world is rejected |
| Entity stops receiving updates | Starved-puppet rescue promotes ownership near a live player |
| Party wipes / everyone leaves | Dedicated server ends the run and returns to a fresh lobby by itself |

---

## 8. A second game mode: Battle Royale

Same world generation, same authority model, same wire — but the players are hostile, the
map shrinks, and the match must resolve with exactly one survivor. **It needs no new sync
primitive.** The mode composes what is already there: the terrain pipeline, the loot latch,
the kill-credit ledger, station-unlock replication, and ship authority. Mode selection rides
three existing messages (`StartRun`, `LobbyState`, `PartyLeaderSettings`); only ring state,
placements, care packages and broadcast toasts have messages of their own.

It is feature-flagged off by default (`EnableGameModes`). A dedicated server picks its
ruleset in `config.cfg`; a player hosting picks it on a GAME MODE row in the lobby. Either
way the *host* owns the ruleset for its own runs — joining someone else's BR server works
regardless of your own setting.

| | Standard | Battle Royale |
|---|---|---|
| Spawn | everyone at the start station | choose a biome, land on your own station there |
| Loadout | chosen on the selector screen | everyone flies the Gunner; no selector |
| Stations / shops | unlock by paying | all open and fully stocked from minute zero, normal prices |
| PvP | friendly-fire toggle | always on, damage ×0.25, applied after armor |
| Enemies | as tuned | HP ×0.5, damage to players ×0.5 |
| Loot | instanced — everyone gets a copy | **contested** — one pile, one taker |
| Map | trackers, scoreboard, map sharing | other players hidden until they're on your screen |
| Hazard | as generated | a closing zone; the map's own hazards are left alone |
| Join / rejoin | allowed | sealed — disconnecting is elimination |
| Win | co-op survival | last ship alive |

### The drop screen lives inside the go-live barrier

Players pick a region before the match places them, with a heat map of where everyone else is
heading. The choice has to resolve *before* any ship exists. The **go-live barrier is the only
moment when the world exists and no ship does** — clients have generated and verified the
world, the host holds every checksum, nothing is spawned — so selection is a gate inside it,
and the host holds GO LIVE until everyone has chosen or the clock expires. Anyone who runs out
of time is given a region: the timer is a decision, not a punishment. The screen is drawn in
immediate mode because it appears during loading, before the game scene's canvases exist.

Regions are built from the *stations* on the map, not from the biome list, so "a region you
can't land in" is structurally impossible rather than a check someone can forget. The heat is
a color, never a count: an exact number invites arithmetic, a color communicates the only
thing that matters — whether you are dropping somewhere contested. Sharing a region is
allowed, which is why the host simply decides and broadcasts `slot → station` instead of every
machine deriving the same answer. A coordinator has no ship and never picks; its clients do.

Each ship is then placed **on** its own station from the first frame and the opening cinematic
points there, rather than spawning everyone on the shared pad and teleporting them apart.
Players are untouchable for as long as the drop screen is up — someone reading a menu cannot
defend themselves — and that grace extends a few seconds past landing, because you arrive
somewhere you have never seen. Stations all unlock shortly *after* go-live: the vanilla
cinematic identifies the station to pan to as "the one with an installed upgrade," so
unlocking them at go-live would send the camera somewhere unstreamed. Every unlock replicates
through the existing progression path, including each machine's shop-stock parity call.

### The zone is rendered, not built

Fortnite's storm and PUBG's blue zone are not level geometry, and neither is this one. The
zone is a procedurally generated annulus mesh with a transparent hole exactly on the safe
radius — real geometry, so the molten edge sits where the damage starts at any zoom — plus a
radius check on each client. **No terrain, no collider, no contact damage.** Painting it as
terrain would mean ~2.9 million `Level.SetCell` calls a match, each one an event to eight
subscribers and a replicated diff to every client; the mesh costs none of that, and the
harness asserts the ring paints *zero* cells. Being caught in the zone hurts only through the
radius check, which is what guarantees a closing ring can never wall a player in — flying
through is always available.

The host owns center and radius and broadcasts both, so no client recomputes the geometry.
The start radius is derived from the map's **playable disc** — the generator stamps everything
past `Width/2` as void, so the world is a disc inscribed in a square array, and sizing off the
array's corner would spend a third of the ring's travel closing through nothing. The ring
**halves** rather than stepping evenly (100 → 80 → 40 → 20 → 10 → 5 → 0): equal radius steps
feel slower and slower as the circle shrinks, halving keeps pressure proportional to the room
left. Damage is a time-to-kill from full health, scaled by max health (an upgraded hull buys
proportionally more time) and multiplied per completed stage — the first zone is an escape
route, the last is not. Total match length is *derived* from the pacing knobs and logged at
match start, rather than configured as a total that leaves the hold to fall out of a division.

Bulk terrain change is expensive in vanilla for reasons that have nothing to do with this mode,
and two patches carry every mode past it: tile and lightmap refresh is skipped entirely on a
coordinator and narrowed to *visible* cells on a player's machine
(`GroundTilemapUpdater.OnCellsChanged` is otherwise 94% of all frame time — four Unity tilemap
calls per changed cell), and each level segment is handed only the changes inside it instead of
vanilla's every-segment-scans-every-change (99.3% of frame time at 979 ms per call). A large
explosion, a terrain repair chunk and a rejoining client's catch-up diff all benefit.

### PvP in a game with one faction

Every player ship in PUNK is on the same team, so the mode has to reach the few places that
encode it:

- `Projectile.FixedUpdate` asks `Owner.IsFriendsWith(hit)` and calls `MoveForward()` instead
  of registering a hit when the answer is yes. In a live BR match that answer is false between
  two different player ships — enemy AI is untouched (an `AIAgent`'s unit is never a `Ship`)
  and self-hits stay friendly. Beams and explosions carry no such filter and always landed.
- Projectile collision is gated on BR *or* friendly-fire co-op, because the physics matrix
  passes bullets through the player layer in both. A shot's cast is resolved past the
  shooter's own hull, which child colliders would otherwise return at the muzzle.
- Damage routes to the victim's machine and applies through the vanilla pipeline exactly once,
  so shields, armor and i-frames behave as in single-player. The ×0.25 PvP scale and the ×0.5
  enemy-damage scale are applied *after* the victim's armor, so armor still decides whether a
  hit lands at all rather than becoming immunity.
- A ship's real hull is about 0.70 × 0.70 world units. A square aim-assist hitbox (1.5×
  half-extent → a 1.05u square) slab-tests the swept shot against every *other* player ship and
  stands down when anything real is struck first or when vanilla's own sweep already reaches
  the hull — so it converts near-misses, never shots through cover, and never a hit that would
  have landed anyway. No collider is added or resized: enemy fire, your shots at enemies, and
  terrain all keep vanilla hit detection. `[PvPDiag]` reports how many hits the assist adds.

### Loot is contested, not instanced

Co-op instances loot: every machine drops its own copy and distant players are granted an
equivalent outright. Here the drop *is* the contest. Replicating each coin as a networked
entity would drag hundreds of short-lived pickups into the authority and streaming pools for
an object whose only interesting state is *"has someone taken it yet"* — **that single bit is
what travels.** The pile stays a local copy on every machine, named by `(group, ordinal)`:
the dying entity's netId (or the cell index, for destroyed terrain) and the item's position in
that drop's roll, both already deterministic and spawned through one funnel. Collecting is a
request — the pile visibly holds, the first claim the host sees wins, losers destroy their
copy, and the winner's pickup completes through the **untouched vanilla path**. Gating rather
than granting is the point: coins, ingredients, consumables and modules each keep their own
collection behavior and none needs a bespoke grant routine. Claims are idempotent, so a lost
verdict heals on retry without ever awarding twice. The cost is one round trip of hold; the
alternative — predicting the win — means un-granting a module or subtracting gold a player
already saw.

The economy on top is additive: no vanilla asset is edited, so co-op and single-player keep
theirs exactly. Ordinary rooms carry no containers in vanilla (every world crate comes from a
landmark prefab), so BR places them directly *inside the generator*, consuming its own seeded
RNG — which makes the go-live hash barrier itself the guarantee that every machine built the
same world. White weapons circulate through crates and the occasional kill; colored weapons
come from boss-tier kills only, bosses detected by the game's own state activator and
minibosses by an entity-id list. Death drops roll from `(runSeed × netId)` inside the same
deterministic window as vanilla loot, so every machine rolls the same item and the claim then
awards it to exactly one player.

Care packages drop in waves sized to *half* the players, minimum one — fewer packages than
players is the point, since they cannot be shared out. Each is destructible, with a screen-edge
arrow (packages are the one thing that gets an arrow in BR; players never do), and destruction
credit rides the existing kill pipeline so the reward materializes only on the destroyer's
machine.

### What a player is allowed to know

Trackers, name tags and edge arrows for other players are off, the scoreboard drops distance
and health, and map exploration is not shared — a BR map shows shops, the current and next
ring, and care packages, never people. What *is* fair information is anything already on your
screen: every remote ship carries a small health-over-fuel widget built from the game's own
segmented enemy healthbar, drawn in the same layer and camera as those bars, hidden for dead
ships and behind the map screen. It reads from tanks that ship sync already keeps current, so
it costs no traffic, and it runs in **both** modes — a co-op teammate at 20% is worth knowing
too. Kills are announced in a corner feed with the killer and the thing that killed you,
including contact damage from an entity that fires no projectile.

The loadout selector does not appear: every machine resolves the Gunner by identity (display
name, then asset name, then a deterministic fallback — never list index, since the pool is
hand-ordered) and stamps it in as the game controller wakes, which is the earliest point the
loadout pool is resident and covers every launch route including the game's own restart.

### Elimination and the end of a match

The host tracks a sealed roster; death and disconnect are both eliminations, assigned
placements from the bottom. The eliminated player gets a placement screen over the existing
game-over hook, with spectate (the spectator camera already auto-follows the living) or a
clean return to lobby or menu. Dead players seeing the living is intended; the living never
see each other off-screen. At one alive the match ends, the winner is shown it, and the normal
run-ended flow takes over — on a dedicated server, that is a fresh lobby plus pre-generation
of the next world.

### Verification

`tools/br-test.ps1` runs a compressed match against a local coordinator and two headless bots,
then asserts from the logs. Lifecycle checks read the timeline (station unlocks broadcast
*and* applied, distinct spawn stations, stage announcements, packages, placements, winner).
The behavior probes are scripted immediately after go-live because none of them are observable
in a free-running match — the mode separates players by ~1600 units on purpose, so the block
opens by collapsing that distance: `ring` (start radius against the measured playable disc,
zero cells painted, and the **worst** host frame — worst, never first-vs-last, because a host
recovers the instant a stall ends and a start/end comparison scores minutes at 0.2 fps as
healthy), `pvp` (a projectile that collided, routed, and moved the victim's HP — the whole
chain in one assertion), `bars`, `loot` (no `(group, ordinal)` awarded twice; zero distant
grants), and `sync`. Two devcmds exist for it and are useful by hand: `fire <secs> player
<slot>` (ships are keyed by slot and have no netId, so this is the only way to exercise PvP
without a second human) and `cellfanout`, which times each subscriber of the terrain-change
event — a level below what a per-frame profiler can attribute.

Full design record, config surface and rejected alternatives:
**[docs/BATTLE_ROYALE.md](docs/BATTLE_ROYALE.md)**.

---

## 9. The engine boundary: what makes it fast

Two rules produced the measured numbers, both learned by profiling rather than intuition:

**Never call the engine to answer a question you already know.** Resolving "who owns this
enemy" through the game's entity database cost a native interop call per entity per tick —
milliseconds per frame on both server and clients. Ownership now resolves from cached
component references and a segment computed from the live physics position: arithmetic plus
dictionary probes. Immutable per-entity facts are captured once at registration, never
rediscovered at 20 Hz.

**Filter coarse-to-fine, always.** Spatial questions start from the player's position, narrow
to nearby segment buckets via a reverse index (maintained from positions the sync loops
already read), and only then touch entities. Scans proportional to "everything alive" were
replaced by scans proportional to "what is near the thing asking."

**Packets ride a metronome, not the frame loop.** On the dedicated server, high-volume state
is relayed by a dedicated thread fed from the socket callback, so relay cadence is immune to
main-thread stalls — the discipline real game servers hold. The transport flushes on a 1 ms
tick with explicit wakeups, and the headless server is frame-capped because an uncapped
null-graphics Unity burns CPU for nothing.

### Measured

| Metric | Result |
|---|---|
| Server frame cost, 1 / 2 / 3 players | 3.3 / 2.9 / 3.3 ms — flat with player count |
| Mod overhead on the server | 0.3–0.6 ms per frame |
| Client sync overhead (`EnemySync.Collect`) | 0.1 ms/tick avg, 0.3 ms max |
| Client frame rate, hovering in combat (dedicated server) | 225 fps avg, p99 8.7 ms |
| Snapshot jitter, full stack incl. Wine container, LAN | 6.8–7.9 ms |
| Snapshot jitter over the public internet | ~60 ms — attributed to the WAN path by differential measurement, not the software |

---

## 10. Operations and observability

The system is designed to be debugged from logs and driven remotely, because most failures
happen on someone else's machine.

- **Always-on watchdogs** (every log level): clock dilation, main-thread hitches with phase
  attribution, unbounded-collection growth, puppet jitter, go-live stalls, transport backlog.
- **Tiered logging** — `Normal` keeps events and warnings while slowing periodic
  instrumentation; `Verbose` restores full-rate telemetry for bug reports; live-switchable.
- **One folder per run** — any player uploads a gzipped log with a keystroke; the host stamps
  the run's date so every participant's log lands in the same S3 folder regardless of clock
  skew or when they joined.
- **Remote administration** — a watched command file and the panel API expose server state,
  restarts, and dev commands (`fpsbench`, `udpstats`, `sync`, `owner`, teleport helpers).
- **Test harnesses** — headless bot fleets join a real or local server, drive scripted load,
  and report the same counters players produce, which is how the scaling and jitter numbers
  above were obtained.

---

## 11. Dedicated server

`pelican_egg/` builds a container that runs the Windows game headless under Wine with Xvfb,
self-updates the mod from GitHub releases at boot, and maps gameplay knobs to panel variables
(port, HP scaling, empty-server reset, frame cap, log level, admin commands). The first
joining player becomes token-verified session admin and starts runs; the server resets itself
to a fresh lobby after a wipe or an abandoned session. Players connect with DIRECT CONNECT —
no Steam involvement on the server side.

---

## 12. Build and release

One assembly is shipped: LiteNetLib is merged in and **internalized** at build time
(`ILRepack.targets`), so installation is a single DLL and another mod shipping its own copy
of the same library cannot collide. A pre-commit hook bumps the version, and the build bakes
it into the handshake, lobby data, menu banner, and release artifact — one source of truth,
so a version mismatch is impossible to ship. Every push to `main` publishes a release; the
proprietary reference assemblies come from a private bundle and are never committed.

```powershell
powershell -File build.ps1          # build + deploy into BepInEx\plugins
powershell -File build.ps1 -Zip     # + dist\PunkMultiverse-vX.Y.Z.zip
```

---

## 13. Repo map

| Path | Contents |
|---|---|
| `src/Core/` | Session, transports selection, authority manager, clock sync, config, diagnostics, update/log pipelines |
| `src/Sync/` | Entity/ship/world/projectile/economy replication, puppets, adaptive timing |
| `src/Transport/` | `ITransport` implementations (Steam, SteamServer, LiteNetLib UDP, loopback) |
| `src/Protocol/` | Wire messages, reader/writer, channel and sequencing contracts |
| `src/Modes/` | Battle royale: host schedule, local zone/HUD, drop selection, contested loot, loot tables |
| `src/Patches/` | Harmony patches against the game's internals |
| `src/UI/` | Lobby, direct connect, HUD, scoreboard, drop screen, zone visuals, kill feed, video options |
| `pelican_egg/` | Dedicated-server image, egg definition, boot script |
| `docs/` | Design specs, subsystem deep dives, test plans, player guide |

---

## Credits

- [IntQuant/noita_entangled_worlds](https://github.com/IntQuant/noita_entangled_worlds) — the
  blueprint for the lobby UX and proximity-authority model.
- The PUNK modding docs, and the ship-spawning work from an earlier local four-player mod.
