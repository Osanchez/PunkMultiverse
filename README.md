# PunkMultiverse

**Online co-op for PUNK — up to 4 players.**
Host a Steam lobby, send a friend a code, and play a shared run together: same world,
same enemies, same progression — your own ship, loot, and build.

Inspired by [Noita Entangled Worlds](https://github.com/IntQuant/noita_entangled_worlds),
built as a single BepInEx plugin. No companion app, no port forwarding — networking rides
Steam's relay, so a pasted lobby code just works.

## Features

- **Lobby in the main menu** — PLAY ONLINE → a GAME SETTINGS screen (world seed: type,
  paste, or random; friendly fire toggle, off by default) → host, copy a code
  (`PMV-XXXXX-XXXXX-XXXX`), friends join from clipboard or a Steam invite. Pick ship colors,
  ready up, go. The host can kick players from the roster.
- **One shared world** — identical seed-generated terrain (verified by checksum before the
  run starts), synced destruction, shared station upgrades, scanner/instrument map reveals,
  and merged map exploration.
- **Real co-op combat** — every enemy is simulated exactly once (the closest player runs it,
  authority hands off as you move), damage applies exactly once through the game's full
  shields/armor pipeline, and you only get hit by shots that visibly reach you on your own
  screen. Enemies aim, animate, telegraph, and home their missiles identically for everyone;
  teammates' gunfire, minions, boosts, hovers, dashes, and hooks all replicate with their
  sounds and effects.
- **Per-player economy** — drops, gold, vault, and shop are yours alone. No loot stealing;
  progression is shared, purchases are not.
- **Find your friends** — name labels in each player's color, screen-edge arrows with
  distance when they're off-screen, and a hold-**Tab** scoreboard (HP, kills, deaths, distance).
- **Death spectating** — when you die, the camera follows an alive teammate; **Q/E** (or the
  arrow keys) cycle between them until you're revived.
- **Drop-proof, host included** — if someone crashes, their slot is reserved; joining again
  with the lobby code puts them back into the same run with their build, vault, and gold
  restored, spawning at the party's most recently unlocked station. New players can join a
  run already in progress the same way. If the **host** quits or crashes mid-run, the run
  survives: a remaining player is promoted (announced on screen), the same lobby code keeps
  working, and the old host can rejoin like anyone else.
- **Version & mod-set safety, auto-updates** — everyone must run the same mod version;
  mismatched joins are rejected with both versions named. New releases download
  automatically at startup and apply on the next launch (the main menu banner says
  RESTART TO APPLY; turn off with `[Update] AutoUpdate=false`). Joiners' other installed
  BepInEx mods are compared against the host's too — see *Configuration*.

## Installation (players)

1. Install [BepInEx 6 (Unity Mono)](https://github.com/BepInEx/BepInEx) into your
   `PUNK Playtest` folder.
2. Download the latest zip from [Releases](https://github.com/Osanchez/PunkMultiverse/releases)
   and extract it over the game folder (it lands in `BepInEx/plugins/PunkMultiverse/`).
3. Launch the game through Steam. You'll see **PLAY ONLINE** in the main menu, and the mod
   version at the bottom of the menu — it says **UP TO DATE** or names the newer release.

Everyone in a lobby needs the **same mod version** — mismatches are rejected with a message
naming both versions. You normally never update by hand: new releases download at startup
and apply on the next launch (the previous build is kept as `PunkMultiverse.dll.bak` if you
ever need to roll back).

### Configuration

Settings live in `BepInEx/plugins/PunkMultiverse/config.cfg` (created on first launch).
The one you're most likely to touch:

- `[Update] AutoUpdate` — `true` *(default)* downloads new releases at startup and stages
  them; the update applies on the next launch. `false` = check only, update manually.
- `[Session] ModManifestPolicy` — what the **host** does when a joiner's installed BepInEx
  mod set differs from the host's:
  - `Reject` *(default)* — the join is refused, with the differing mods named.
  - `Warn` — they join, and everyone sees a `[!] MODS` marker next to their name.
  - `Ignore` — no check. Other mods aren't *synced* either way — mixing gameplay mods means
    different rules per player; cosmetic/UI mods are generally fine.

## Playing

- **Host:** PLAY ONLINE → HOST LOBBY → GAME SETTINGS (WORLD SEED: type it, PASTE from
  clipboard, or leave empty for random; FRIENDLY FIRE on/off — off by default) → COPY CODE →
  send it to your friends (or INVITE FRIENDS for the Steam overlay). When everyone's ready:
  START GAME. You can also start solo — friends join mid-run with the code. KICK buttons on
  the roster remove troublemakers (kick, not ban).
- **Join:** copy the code your host sent → PLAY ONLINE → JOIN FROM CLIPBOARD. Works while the
  run is already going: you'll spawn at the party's latest unlocked station, caught up to the
  world's state.
- **Reconnect:** crashed or dropped? JOIN FROM CLIPBOARD with the same code — your build,
  vault, and gold come back with you, and you spawn at the latest unlocked station.
- **Stop and play later:** net runs auto-save continuously on every machine (the pause menu
  says EXIT — no manual save needed). Whoever hosts next can RESUME LAST RUN from the PLAY
  ONLINE screen: same world, terrain damage, kills, and unlocks; everyone rejoins with the
  code, gets their build back, and spawns at the checkpoint.
- **Host leaving:** the run keeps going — a remaining player becomes the host (a banner names
  them), the lobby code stays the same, and the old host can rejoin with it.
- **When you die:** the camera follows an alive teammate; **Q/E** switch between them.
- **In game:** hold **Tab** for the party scoreboard. **F9** opens a small network debug
  overlay (**F10** toggles verbose sync diagnostics, **F11** dumps the ownership table to the log).

See **[TESTING.md](TESTING.md)** for the full test checklist and a solo two-instance setup
that needs no second Steam account.

## Building from source

Requires the .NET SDK. The repo expects to sit inside the game install
(`...\PUNK Playtest\PunkMultiverse\`); otherwise pass `-GameDir`.

```powershell
powershell -File build.ps1            # Release build + deploy to BepInEx\plugins
powershell -File build.ps1 -Debug     # Debug + pdb
powershell -File build.ps1 -Zip       # + dist\PunkMultiverse-vX.Y.Z.zip
```

Reference DLLs come from your game install and are never committed.

## CI / Releases

Every push to `main` builds and publishes a release zip via GitHub Actions. The proprietary
reference DLLs come from a private refs bundle:

- One-time: add a `REFS_TOKEN` repo secret (fine-grained PAT with read access to
  `Osanchez/PunkMods-refs`) and run `tools\update-refs.ps1` locally once to upload the bundle.
- After a game update or a new csproj reference: rerun `tools\update-refs.ps1`.
- Versioning is automatic: a pre-commit hook bumps the csproj `<Version>` patch number on
  every non-docs commit to main, and the build bakes it into the plugin (handshake, lobby
  data, menu banner, zip name — single source of truth). Stage a manual `<Version>` change
  to bump minor/major instead.

## How it works (short version)

Host-relay star over Steam Networking Messages (loopback UDP transport for solo testing).
The run seed replicates and every client generates the same world, verified by terrain
checksum. Entities get network identity from a deterministic fingerprint manifest; the
closest player simulates each enemy (NEW-style proximity authority with a host registrar,
with per-entity handoff cooldowns) while everyone else runs muted, interpolated puppets that
mirror the authority's aim, AI state, and weapon audio. Damage applies once on the victim's
own machine — enemy fire hit-detects against you locally, player-vs-player routes to the
victim's authority — always through the vanilla pipeline; weapon fire replays as visual
projectiles that re-target homing at the authority's victim. Terrain diffs, kills, runtime
spawns, and progression events replicate as idempotent reliable events, kept as ledgers on
every machine — so a rejoiner gets the whole run replayed, and when the host disappears the
Steam lobby's ownership migration elects a replacement who serves the same ledgers without
anyone reloading. Fog is a global gas simulation, so only the host runs it; the terrain
cells it converts ride the same terrain-diff ledger, and clients render fog from those
synced cell types. Terrain catch-up streams in 64×64-cell chunks,
nearest the player first, under a per-frame byte budget with send backpressure — even a
fully converted map syncs without a size cutoff, a local save, or a buffer overflow, with
the area around the player correct within a couple of seconds.

## Known behavior / limitations

- Loot, gold, vault, and shop stock are per-player **by design**.
- Menus don't pause the world in multiplayer; slow-mo effects are disabled. The vanilla
  suspend-save is replaced in net runs by the continuous run auto-save (the pause menu button
  reads EXIT). Rejoiners and late joiners spawn at the party's most recently unlocked station
  (the run start until the first unlock).
- Host migration only engages mid-run — if the host leaves while everyone is still in the
  lobby or loading, the session ends (it's cheap to recreate). Kicks aren't bans: a kicked
  player can rejoin with the code.
- Other installed mods are detected and policed (see *Configuration*) but never synced;
  daily-challenge runs aren't supported in net runs yet.

## Credits

- [IntQuant/noita_entangled_worlds](https://github.com/IntQuant/noita_entangled_worlds) —
  the blueprint for the lobby UX and the proximity-authority model.
- The PUNK modding docs and my earlier mods in the Mods repo — this builds directly on the
  ship-spawning recipe from my local four-player mod (PunkFourPlayer).
