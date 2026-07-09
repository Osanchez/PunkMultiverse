# PunkMultiverse

**Online co-op for PUNK — up to 4 players.**
Host a Steam lobby, send a friend a code, and play a shared run together: same world,
same enemies, same progression — your own ship, loot, and build.

Inspired by [Noita Entangled Worlds](https://github.com/IntQuant/noita_entangled_worlds),
built as a single BepInEx plugin. No companion app, no port forwarding — networking rides
Steam's relay, so a pasted lobby code just works.

## Features

- **Lobby in the main menu** — PLAY ONLINE → host, copy a code (`PMV-XXXXX-XXXXX-XXXX`),
  friends join from clipboard or a Steam invite. Pick ship colors, the host can set the
  world seed, ready up, go.
- **One shared world** — identical seed-generated terrain (verified by checksum before the
  run starts), synced destruction, shared station upgrades, scanner/instrument map reveals,
  and merged map exploration.
- **Real co-op combat** — every enemy is simulated exactly once (the closest player runs it,
  authority hands off as you move), damage applies exactly once through the game's full
  shields/armor pipeline, and you see teammates' gunfire, minions, boosts, and hooks.
- **Per-player economy** — drops, gold, vault, and shop are yours alone. No loot stealing;
  progression is shared, purchases are not.
- **Find your friends** — name labels in each player's color, screen-edge arrows with
  distance when they're off-screen, and a hold-**Tab** scoreboard (HP, kills, deaths, distance).
- **Drop-proof** — if someone crashes, their slot is reserved; REJOIN LAST SESSION puts them
  back into the same run with their build, vault, and gold restored.
- **Version safety** — everyone must run the same mod version. Mismatched joins are rejected
  with both versions named, and the lobby shows an UPDATE banner when a newer release exists.

## Installation (players)

1. Install [BepInEx 6 (Unity Mono)](https://github.com/BepInEx/BepInEx) into your
   `PUNK Playtest` folder.
2. Download the latest zip from [Releases](https://github.com/Osanchez/PunkMultiverse/releases)
   and extract it over the game folder (it lands in `BepInEx/plugins/PunkMultiverse/`).
3. Launch the game through Steam. You'll see **PLAY ONLINE** in the main menu.

Everyone in a lobby needs the **same mod version** — the lobby screen shows yours under the
title, and it tells you when you're out of date.

## Playing

- **Host:** PLAY ONLINE → HOST LOBBY → COPY CODE → send it to your friends (or INVITE FRIENDS
  for the Steam overlay). Optionally paste a WORLD SEED. When everyone's ready: START GAME.
- **Join:** copy the code your host sent → PLAY ONLINE → JOIN FROM CLIPBOARD.
- **Reconnect:** crashed or dropped? PLAY ONLINE → REJOIN LAST SESSION.
- **In game:** hold **Tab** for the party scoreboard. F11 opens a small network debug overlay.

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
- Bump `<Version>` in `PunkMultiverse.csproj` when cutting a release — it drives both the
  version handshake and the update banner.

## How it works (short version)

Host-relay star over Steam Networking Messages (loopback UDP transport for solo testing).
The run seed replicates and every client generates the same world, verified by terrain
checksum. Entities get network identity from a deterministic fingerprint manifest; the
closest player simulates each enemy (NEW-style proximity authority with a host registrar)
while everyone else runs muted, interpolated puppets. Damage routes to the victim's
authority and applies once through the vanilla pipeline; weapon fire replays as visual-only
projectiles. Terrain diffs, kills, runtime spawns, progression events, and fog exploration
replicate as reliable events — and a rejoining player gets the whole ledger replayed.

## Known behavior / limitations

- Loot, gold, vault, and shop stock are per-player **by design**.
- Merged-cell terrain patches and projectile spread can look slightly different per client
  (game RNG; damage and terrain state are authoritative).
- Menus don't pause the world in multiplayer; slow-mo effects are disabled; Save & Exit is
  disabled during net runs (rejoin covers crashes). Rejoiners respawn at the start station.
- Daily-challenge runs and other installed mods aren't supported in net runs yet.
- Hook tethers to unstreamed targets and hooked-prop spring physics are approximate on peers.

## Credits

- [IntQuant/noita_entangled_worlds](https://github.com/IntQuant/noita_entangled_worlds) —
  the blueprint for the lobby UX and the proximity-authority model.
- The PUNK modding docs and prior mods in the community Mods repo, especially the local
  four-player proof of concept whose ship-spawning recipe this builds on.
