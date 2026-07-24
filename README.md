# PunkMultiverse

Four-player online co-op retrofitted into **PUNK** — a closed-source Unity game with no
multiplayer, no source access, and no server build — as a single BepInEx plugin.

The same assembly runs as a player's client, as a listen host, and as a headless dedicated
server. Players join a friend's Steam lobby with a pasted code or a public server with an
IP; the world, the enemies, and the destruction are shared, while loot and builds stay
personal.

- **Scope:** ~30k lines of C#, one DLL, four transports, 4 players per session.
- **Deployment:** Steam P2P, or a Dockerized headless server (Wine) on a Pelican panel.
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

## 8. The engine boundary: what makes it fast

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

## 9. Operations and observability

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

## 10. Dedicated server

`pelican_egg/` builds a container that runs the Windows game headless under Wine with Xvfb,
self-updates the mod from GitHub releases at boot, and maps gameplay knobs to panel variables
(port, HP scaling, empty-server reset, frame cap, log level, admin commands). The first
joining player becomes token-verified session admin and starts runs; the server resets itself
to a fresh lobby after a wipe or an abandoned session. Players connect with DIRECT CONNECT —
no Steam involvement on the server side.

---

## 11. Build and release

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

## 12. Repo map

| Path | Contents |
|---|---|
| `src/Core/` | Session, transports selection, authority manager, clock sync, config, diagnostics, update/log pipelines |
| `src/Sync/` | Entity/ship/world/projectile/economy replication, puppets, adaptive timing |
| `src/Transport/` | `ITransport` implementations (Steam, SteamServer, LiteNetLib UDP, loopback) |
| `src/Protocol/` | Wire messages, reader/writer, channel and sequencing contracts |
| `src/Patches/` | Harmony patches against the game's internals |
| `src/UI/` | Lobby, direct connect, HUD, scoreboard, video options integration |
| `pelican_egg/` | Dedicated-server image, egg definition, boot script |
| `docs/` | Design specs, subsystem deep dives, test plans, player guide |

---

## Credits

- [IntQuant/noita_entangled_worlds](https://github.com/IntQuant/noita_entangled_worlds) — the
  blueprint for the lobby UX and proximity-authority model.
- The PUNK modding docs, and the ship-spawning work from an earlier local four-player mod.
