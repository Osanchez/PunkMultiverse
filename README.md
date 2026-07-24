# PunkMultiverse

**Online co-op for PUNK — up to 4 players.**
Host a Steam lobby and send a friend a code, or run a 24/7 dedicated server anyone can
join by IP — same world, same enemies, same progression; your own ship, loot, and build.

Inspired by [Noita Entangled Worlds](https://github.com/IntQuant/noita_entangled_worlds),
built as a single BepInEx plugin — one DLL, no companion app, no port forwarding for
Steam play.

## Features

- **Lobby in the main menu** — PLAY ONLINE → GAME SETTINGS (world seed: type, paste, or
  random; friendly fire; enemy HP scaling) → host, copy a code, friends join from
  clipboard or a Steam invite. Ship colors, ready-up, kick. Full controller navigation.
- **One join button, any server** — JOIN auto-detects whatever you paste: a `PMV-` lobby
  code joins over Steam, an `ip:port` direct-connects to a dedicated server, a SteamID64
  targets a server identity. There's also a DIRECT CONNECT screen with address/port
  fields and a PASTE button. Players never edit config to join anything.
- **Dedicated servers** — the same mod runs headless in Docker (Wine) as a shipless
  coordinator: 24/7 sessions that survive everyone leaving, auto-reset after a party
  wipe or abandonment, and self-update from GitHub releases. Ships as a Pelican egg.
- **One shared world** — identical seed-generated terrain (fingerprint-verified before
  the run starts), synced destruction, shared station upgrades, merged map exploration.
- **Real co-op combat** — every enemy is simulated exactly once, damage applies exactly
  once through the game's full pipeline, and you only get hit by shots that visibly
  reach you on your own screen.
- **Per-player economy** — drops, gold, vault, and shop are yours alone; progression is
  shared, purchases are not.
- **Drop-proof, host included** — crashed players rejoin the same run with their build
  restored; late joiners enter mid-run; host migration keeps the run alive if the host
  disappears; a REJOIN button appears whenever your last session still exists.
- **Quality of life** — an FPS LIMIT setting added to the game's own video options
  (default: your monitor's refresh rate, adjustable down to 60), a freely resizable
  game window in windowed mode, name labels/off-screen arrows/scoreboard/death
  spectating, and honest-clock protection when the game window loses focus.
- **Version & mod-set safety, auto-updates** — everyone must run the same mod version;
  new releases download at startup and apply on next launch (previous build kept as
  `.bak` for rollback). Joiners' other BepInEx mods are policed per host policy.

## Installation (players)

1. Install [BepInEx 6 (Unity Mono)](https://github.com/BepInEx/BepInEx) into your
   `PUNK Playtest` folder.
2. Download the latest zip from [Releases](https://github.com/Osanchez/PunkMultiverse/releases)
   and extract over the game folder (a single `PunkMultiverse.dll` lands in
   `BepInEx/plugins/PunkMultiverse/`).
3. Launch through Steam — **PLAY ONLINE** appears in the main menu, with the mod version
   and update status at the bottom.

### Configuration

`BepInEx/plugins/PunkMultiverse/config.cfg` (created on first launch). Highlights:

- `[Update] AutoUpdate` — auto-download releases (default on).
- `[Session] ModManifestPolicy` — `Reject` (default) / `Warn` / `Ignore` joiners whose
  BepInEx mod set differs from the host's.
- `[Video] FpsLimit` / `ResizableWindow` — the in-game FPS LIMIT row's backing value
  (0 = monitor max) and the windowed-mode resize frame.
- `[Diag] LogLevel` — `Normal` (default), `Verbose` (full instrumentation for bug
  reports), `Quiet`. Warning-class watchdogs run at every level.

## Dedicated servers

`pelican_egg/` contains everything to run a server on the [Pelican](https://pelican.dev)
panel: a Docker image (Wine + the baked game) that boots headless, self-updates the mod
from releases, and exposes gameplay knobs as egg variables (port, HP scaling, empty-server
reset, frame cap, log level, admin command file). Players join it with DIRECT CONNECT —
no Steam required on the server. See `pelican_egg/README.md`.

## Architecture — the design decisions and why

### One DLL, many transports
The mod ships as a single assembly (LiteNetLib is merged in and internalized at build
time) because install friction and dependency conflicts kill mods; internalizing means
another mod shipping its own LiteNetLib can never collide. Underneath, one star-topology
session runs over interchangeable transports — Steam P2P (friends), SteamServer/SDR
(server identities), raw UDP via LiteNetLib (dedicated/LAN), loopback (dev) — selected
automatically from what the player pastes, never from config, because "it just works"
beats a settings page.

### Generate the world, sync the differences
Every machine generates the identical world from a replicated seed, verified by
terrain/entity/plant/visual fingerprints at a go-live barrier before anyone plays.
Syncing a 4-million-cell world is intractable; syncing *divergence from a deterministic
baseline* is cheap. A headless server contributes data-only fingerprints (it renders
nothing), and rendering clients cross-check visuals against each other. Entity identity
is a deterministic instance counter — never position — so identity survives movement.

### Simulation is distributed; the server is a referee
The world is a fixed grid of 25-unit segments. The host grants **leases** (segment →
owner) only within each player's *reported residency* — the segments their game actually
has loaded — because simulation capability is streaming-dependent: authority anywhere
else would be authority without a simulator. Enemies near you are simulated *by you* at
full fidelity; segments nobody streams go **dormant** (first-class state: frozen by
agreement, costing nothing). The dedicated server therefore owns almost nothing — it
referees leases, relays state, and enforces the barrier, which is why a modest Wine box
scales flat with player count. A slow-polled rescue promotes ownership of any puppet
starving near a live player, so no entity can fall through the cracks permanently.

### Lookups filter coarse-to-fine, dictionaries over engine calls
All hot-path spatial questions run big-filter-first: player position → nearby segment
buckets (a reverse index maintained from positions the sync loops already read) →
entities. Ownership resolution is pure math plus dictionary probes — native engine
queries were measured at whole milliseconds per tick on both server and clients and are
now confined to cold-path fallbacks. Per-entity component references are cached once per
lifetime; nothing re-discovers immutable facts at 20Hz.

### Packets on a metronome
On the dedicated server, high-volume presentation state (ship and entity snapshots) is
relayed by a dedicated thread fed directly from the socket callback — never from the
frame loop — so relay cadence is immune to main-thread stalls. This is the discipline
real game servers hold: the network never waits for the simulation. Sends flush on a
1ms transport tick with explicit wakeups; the headless server itself is frame-capped
(default 120fps) because an uncapped null-graphics Unity burns CPU for nothing.

### Presentation: honest clocks, measured delays, loss that heals itself
Snapshots carry the **sender's** timestamps so puppets interpolate on the sender's even
spacing rather than replaying network jitter as motion. Cross-machine time mapping uses
an NTP-style (Mills) clock filter — offsets chase the *minimum*-transit sample of a
sliding window, exploiting the fact that network delay noise is one-sided. Each puppet's
interpolation delay targets the **98th percentile of actually-observed snapshot
lateness** (NetEQ-style) instead of heuristic multipliers — exactly enough buffer for
the real network, re-derived continuously. The unreliable state channel carries XOR
parity (one per four packets), so any single loss reconstructs on arrival without a
retransmit round-trip. Rendering uses Hermite interpolation with error decay, and a
render-level benchmark (`fpsbench`) exists because fixed-step metrics provably cannot
see render-level jitter.

### Reliability where it's owed, idempotence everywhere
Four channels: Control, Events, and Combat are independent reliable-ordered streams (so
a terrain burst can never head-of-line-block combat), state is unreliable. Durable facts
— kills, terrain diffs, runtime spawns, progression — replicate as **idempotent events
kept as ledgers** on every machine, which is what makes rejoin, late join, and host
migration the same operation: replay the ledgers. Terrain catch-up streams
nearest-the-player-first under a byte budget, so even a fully-converted map syncs
without cutoffs.

### Damage happens on the victim's machine
Enemy fire hit-detects against you locally; player-vs-player routes to the victim's
authority — always through the vanilla damage pipeline. This buys fairness (you're only
hit by what visibly reached you) and correctness (shields, armor, and effects behave
exactly like single-player) without reimplementing any game logic.

### The server is the same game, operated like a service
The dedicated coordinator is the unmodified game under Wine, seated in a reserved
non-player slot — one codebase, no server fork to maintain. The first joiner becomes
session admin (token-verified) and drives START; party wipes and abandoned runs reset
the server to a fresh lobby automatically, because a server with no human at it must
never need one. Everything is observable and drivable remotely: watchdogs for clock
drift, memory growth, jitter, and hitches run at every log level; every player's
diagnostics upload to one dated S3 folder per run (the host stamps the folder date so
skewed clocks and midnight joins can't split a session); a command file and panel API
allow full remote administration.

### Safety rails
Version-locked handshakes (exact mod + protocol match), mod-manifest policing, staged
auto-updates with rollback, and a determinism barrier that refuses to start a diverged
run rather than desync into one.

## Playing

- **Host (Steam):** PLAY ONLINE → HOST LOBBY → GAME SETTINGS → COPY CODE → friends join
  from clipboard or Steam invite → START GAME. Solo starts work; friends join mid-run.
- **Join anything:** copy what your host sent (lobby code or `ip:port`) → PLAY ONLINE →
  JOIN FROM CLIPBOARD. Or DIRECT CONNECT and type/paste the address.
- **Reconnect:** the REJOIN button appears whenever your last session is still alive.
- **When you die:** camera follows an alive teammate; **Q/E** cycle; party wipe ends the
  run to the lobby for another go.
- **In game:** hold **Tab** for the scoreboard; **F9** net overlay; **F10** sync
  diagnostics; **F8** uploads your log for the current run id.

See **[TESTING.md](docs/TESTING.md)** for the test checklist and two-instance solo setup.

## Building from source

Requires the .NET SDK. The repo expects to sit inside the game install
(`...\PUNK Playtest\PunkMultiverse\`); otherwise pass `-GameDir`.

```powershell
powershell -File build.ps1            # Release build + deploy to BepInEx\plugins
powershell -File build.ps1 -Debug     # Debug + pdb
powershell -File build.ps1 -Zip       # + dist\PunkMultiverse-vX.Y.Z.zip
```

LiteNetLib is merged into the output DLL by an ILRepack post-build step (see
`ILRepack.targets`). Reference DLLs come from your game install and are never committed.

## CI / Releases

Every push to `main` builds and publishes a release zip via GitHub Actions; the
proprietary reference DLLs come from a private refs bundle (`tools\update-refs.ps1`,
`REFS_TOKEN` secret). Versioning is automatic: a pre-commit hook bumps the csproj
`<Version>` on every non-docs commit, and the build bakes it into the handshake, lobby
data, menu banner, and zip name — one source of truth.

## Known behavior / limitations

- Loot, gold, vault, and shop stock are per-player **by design**.
- Menus don't pause the world in multiplayer; slow-mo effects are disabled; the vanilla
  suspend-save is replaced by the continuous run auto-save.
- Host migration engages mid-run only; lobby/loading sessions are cheap to recreate.
- Kicks aren't bans. Other installed mods are policed but never synced. Daily-challenge
  runs aren't supported in net runs yet.
- Dedicated servers are open by design: anyone with the address and a matching mod
  version can join.

## Credits

- [IntQuant/noita_entangled_worlds](https://github.com/IntQuant/noita_entangled_worlds) —
  the blueprint for the lobby UX and the proximity-authority model.
- The PUNK modding docs and my earlier mods in the Mods repo — this builds directly on
  the ship-spawning recipe from my local four-player mod (PunkFourPlayer).
