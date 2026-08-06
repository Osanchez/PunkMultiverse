# PUNK Multiverse — dedicated server image

This folder builds the Docker image that runs a **headless PUNK Multiverse coordinator**: the
Windows game under Wine, with BepInEx and the mod layered on at boot.

**For how the live server is actually deployed and administered** — the host, SSH access, which
config file wins, how to ship a mod update — see [`../docs/SERVER_DEPLOY.md`](../docs/SERVER_DEPLOY.md).
This file is about the *image*.

## Why a custom image

Two facts about PUNK force it:

1. **PUNK is a Steam *playtest* (appid `2850470`).** Playtest depots are *not* downloadable with
   `steamcmd +login anonymous` — they need a Steam account granted the playtest. Any "SteamCMD
   fetches the server files on install" image is therefore a non-starter. **The game files are
   supplied by you, and baked in here.**
2. **PUNK ships only a Windows Unity build (`Punk.exe`).** There is no Linux dedicated-server
   binary, so it runs under **Wine** with Xvfb.

What makes a Steam-free headless server possible at all is the mod's **`Udp` transport**
(LiteNetLib). The coordinator uses it, so it needs no Steam networking — no client, no login, no
SDR. A `steam_appid.txt` is still written so the base game doesn't try to bounce through the Steam
client at launch.

## What comes from where

- **The base game is baked into the image** (`Punk.exe`, `Punk_Data/`, `MonoBleedingEdge/`,
  `UnityPlayer.dll`, staged at `/opt/game`). On **first boot only**, `start-server.sh` copies it
  into `/home/container`, because the volume mounted there would otherwise shadow anything the
  image put in that path.

  > **This is why rebuilding the image does not update an existing server's game files.** The
  > volume already has a copy and is not re-seeded. A client reporting a `0.12.x` version mismatch
  > is a *base game* mismatch, and no image rebuild will fix it — you have to remove the volume
  > (destructive) or update the files in place.

  > **This bakes the PUNK playtest build into the image.** Keep the Docker Hub repo scoped to who
  > should have it; a public repo makes the playtest `docker pull`-able by anyone. (Currently
  > public — an informed choice.)

- **The image provides BepInEx** — the Doorstop loader (`winhttp.dll`) plus `BepInEx/core`, baked
  in and overlaid onto the game files each boot, so even a vanilla game copy becomes mod-ready.
- **GitHub provides the mod** — every boot pulls the latest (or a pinned) release from
  `Osanchez/PunkMultiverse` into the plugins folder. Publish a release and every server picks it up
  on its next restart; no image rebuild.

## Files

| File | Purpose |
|------|---------|
| `Dockerfile` | Builds the Wine + Xvfb image, with `jq` and the baked BepInEx and game layers. |
| `build-image.sh` | Stages BepInEx and the base game from a local install, then builds (and optionally pushes). |
| `entrypoint.sh` | Container entrypoint (baked in). Prints the banner, execs the server script under tini. |
| `start-server.sh` | Overlays BepInEx, self-updates the mod, writes `config.cfg`, launches headless (baked in). |

## Building

```bash
# Docker Desktop must be running; `docker version` failing with an npipe error means it is not.
cd server_image
./build-image.sh                 # build only
./build-image.sh --push          # build then push to Docker Hub
GAME_DIR=/path/to/PUNK ./build-image.sh
IMAGE=osanchezdev/punk-punkmultiverse:latest ./build-image.sh --push
```

It stages from a local PUNK install (needs `winhttp.dll`, `BepInEx/core`, `Punk.exe`,
`UnityPlayer.dll`, `Punk_Data/`, `MonoBleedingEdge/`). Credentials are the Docker Hub token already
in `~/.docker/config.json`; if a machine has never pushed, `docker login -u osanchezdev` with a
Personal Access Token as the password stores one.

You only rebuild the **image** when the runtime changes (Wine, these scripts, the baked BepInEx or
game). **Mod updates do not need a rebuild** — the mod self-updates from GitHub at boot.

Then roll it out: `tools/srv.ps1 recreate`.

## Environment variables

Read by `start-server.sh`. **Anything not set is left alone** — the persisted `config.cfg` wins, so
an unset knob never silently reverts a hand-edit.

> Match tuning (`BR_*`) is deliberately **not** listed here. It belongs in
> `/home/container/server.cfg`, which is applied last and beats these. Setting it in both places
> means the `-e` value silently loses. See [`../docs/SERVER_DEPLOY.md`](../docs/SERVER_DEPLOY.md).

| Variable | Default | What it does |
|----------|---------|--------------|
| `MOD_AUTO_UPDATE` | `1` | `1` = check GitHub for a newer mod build each boot; `0` = keep the installed one. |
| `MOD_VERSION` | `latest` | `latest` or a release tag (e.g. `v0.1.238`) to pin the mod build. |
| `MOD_RELEASE_REPO` | `Osanchez/PunkMultiverse` | GitHub owner/repo the mod is pulled from. |
| `GITHUB_TOKEN` | *(blank)* | Optional token for API rate limits / a private mod repo. |
| `INSTALL_BEPINEX` | `1` | `1` = overlay the image's baked BepInEx each boot; `0` = use the game copy's own. |
| `STARTUP_EXE` | `Punk.exe` | Executable to launch. |
| `GAME_DIR` | `/home/container` | Game install path in the container. |
| `SERVER_PORT` | `7778` | UDP port to bind, mapped to the mod's `UdpPort`. |
| `SERVER_ADDRESS` | `0.0.0.0` | Advertised join host (written into the join code). |
| `AUTO_START_RUN` | `0` | `1` auto-launches the run; `0` waits for the admin to press START. |
| `GAME_MODE` | `Standard` | `Standard` or `BattleRoyale`. Normalized before it is written, because the mod's value list would silently rewrite a typo to `Standard`. |
| `HP_SCALING_PER_PLAYER` | `0.25` | Enemy HP multiplier added per player. |
| `COIN_DESPAWN_SECONDS` | `45` | Gold-coin lifetime. |
| `MOD_MANIFEST_POLICY` | `Reject` | `Reject`/`Warn` on mod-version mismatch. |
| `ENABLE_ADMIN_COMMANDS` | `1` | Watch `devcmd.txt` for runtime dev/admin commands. |
| `LOG_LEVEL` | `Normal` | `Quiet`/`Normal`/`Verbose`. |
| `SYNC_DIAGNOSTICS` | `0` | Verbose `[Diag]` sync logging. |
| `STOP_GRACE_SECONDS` | `20` | Seconds to wait for a clean save on stop before force-kill. |
| `STEAM_APPID` | `2850470` | Written to `steam_appid.txt` (keeps the game off the Steam client). |
| `WINEDEBUG` | `-all` | Wine debug channels. |
| `EXTRA_ARGS` | *(blank)* | Extra args appended to the `Punk.exe` command line. |

## Lifecycle

- **Startup** — ready when `[Udp] hosting on` appears in `docker logs` (the coordinator is
  listening, even before anyone joins). `start-server.sh` streams the BepInEx log to stdout so the
  container log carries the game's console.
- **Stop / restart** — `docker stop` sends `SIGTERM`; `start-server.sh` traps it and (with admin
  commands enabled) writes `quit` to the command file. The mod's `quit` ends the session — saving
  the economy stash and sending clients a clean disconnect — then exits. If the game hasn't exited
  within `STOP_GRACE_SECONDS` the script escalates to a signal and finally `wineserver -k`. Stale
  command-file leftovers are cleared on boot so a restart can't loop.
- **Self-update** — each boot overlays BepInEx, then queries `MOD_RELEASE_REPO` for the wanted
  release and installs it only if the tag differs from `.installed_version`. If GitHub is
  unreachable it keeps the installed copy.
- **Admin commands** — with `ENABLE_ADMIN_COMMANDS=1`, write devcmds into
  `BepInEx/plugins/PunkMultiverse/devcmd.txt` (`status`, `roster`, `start`, `spawn …`). Output goes
  to `devout.txt` and the console. `tools/srv.ps1 cmd "<devcmd>"` does this over SSH.

## Smoke-testing the image locally

```bash
docker build -t punkmv-server:test .
docker run --rm -it -p 7778:7778/udp -e SERVER_PORT=7778 punkmv-server:test
```

Watch for `[Udp] hosting on *:7778`, then join from a client with `Transport = Udp` via
`join <docker-host-ip>:7778`.

## Known limits

- **Session cap is 4 players** (a mod compile constant).
- **No password gate.** Anyone who can reach the port and passes the mod-version check can join —
  the transport's connection key filters stray packets, it is not a password.
- **WAN play needs a routable port.** The live server publishes through a playit.gg UDP tunnel;
  a port-forward works equally well. LAN works out of the box.
- **The first player to join becomes session admin** and gets host-like controls (START / KICK) via
  a capability token, since the headless server has no UI. World settings are chosen by that admin.
