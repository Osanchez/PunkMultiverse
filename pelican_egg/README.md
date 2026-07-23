# PUNK Multiverse — Pelican egg (dedicated server)

This folder deploys a **headless PUNK Multiverse coordinator** as a Docker game server managed by
[Pelican](https://pelican.dev/) (the panel; also imports into Pterodactyl). It follows the
[`pelican-eggs/games-steamcmd`](https://github.com/pelican-eggs/games-steamcmd) pattern — but with
one deliberate departure explained below.

## Why this is a custom image, not a stock SteamCMD egg

Two facts about PUNK force a custom approach:

1. **PUNK is a Steam *playtest* (appid `2850470`).** Playtest depots are *not* downloadable with
   `steamcmd +login anonymous` — they require a Steam account that has been granted the playtest.
   The whole premise of the games-steamcmd eggs ("SteamCMD fetches the server files on install")
   therefore cannot work here. **You supply the game files yourself.**
2. **PUNK ships only a Windows Unity build (`Punk.exe`).** There is no Linux dedicated-server
   binary, so the server runs the Windows game under **Wine**.

What makes a Steam-free headless server possible at all is the mod's **`Udp` transport**
(LiteNetLib, added on `feat/litenetlib-transport`). The coordinator uses it, so it needs **no Steam
networking** — no Steam client, no login, no SDR. A `steam_appid.txt` is still written so the base
game doesn't try to bounce through the Steam client at launch.

## What you supply vs. what the image provides

To keep things easy and self-updating, everything is provisioned automatically — there is nothing to
configure to get the game onto the server:

- **The base game is baked into the image** (`Punk.exe`, `Punk_Data/`, `MonoBleedingEdge/`,
  `UnityPlayer.dll`, staged at `/opt/game`). On first boot `start-server.sh` copies it into
  `/home/container` (Pelican's volume mounts over `/home/container`, which would otherwise shadow
  anything placed there in the image). No download, no S3, no `GAME_FILES_URL`. **Note:** this bakes
  the PUNK *playtest* build into the image — keep the image's Docker Hub repo scoped to who should
  have it (a public repo makes the playtest `docker pull`-able by anyone).
- **The image provides: BepInEx** — the Doorstop loader (`winhttp.dll`) + `BepInEx/core` are baked
  into the image and overlaid onto your game files at boot, so even a *vanilla* game copy becomes
  mod-ready. You never install BepInEx yourself.
- **GitHub provides: the mod** — on every boot the server pulls the latest (or a pinned) mod release
  from `Osanchez/PunkMultiverse` and drops it into the plugins folder. **Self-updating**: push a new
  release and servers pick it up on their next restart.

## Files

| File | Purpose |
|------|---------|
| `egg-punk-multiverse.json` | The Pelican egg. Import this into the panel. |
| `Dockerfile` | Builds the Wine + Xvfb image, with `jq` and the baked BepInEx layer. |
| `build-image.sh` | Stages BepInEx from a local install, then builds (and optionally pushes) the image. |
| `entrypoint.sh` | Container entrypoint (baked into the image). |
| `start-server.sh` | Overlays BepInEx, self-updates the mod, writes `config.cfg` from env, launches headless (baked in). |

## One-time setup

### 1. Build and push the image

The egg points at the Docker Hub image `docker.io/osanchezdev/punk-punkmultiverse:latest`. Use
`build-image.sh`, which stages the BepInEx loader from a local PUNK install (it needs `winhttp.dll`
and `BepInEx/core`), then builds and pushes:

```bash
# One-time auth: log in with your Docker Hub username + a Personal Access Token as the password.
# Create the token at https://app.docker.com/settings/personal-access-tokens (scope: Read & Write).
docker login -u osanchezdev

cd pelican_egg
./build-image.sh --push
# Uses the install two levels up by default; override with GAME_DIR=/path/to/PUNK ./build-image.sh
```

`docker login` stores the credential locally (in `~/.docker/config.json` or the OS keychain), so you
only do it once per machine. You only rebuild the **image** when the runtime (Wine, scripts, or the
baked BepInEx) changes — **mod updates do not need an image rebuild**, since the mod self-updates from
GitHub at boot.

### 2. Import the egg

Panel → **Admin → Eggs → Import Egg** → upload `egg-punk-multiverse.json`.

### 3. Create a server

Create a server from the egg, **install it, and start it** — that's it. The base game (baked into
the image) is copied into place on first boot, BepInEx is overlaid, and the mod self-updates. No
game upload, no download, nothing to configure.

The baked base game (staged at `/opt/game` in the image) is only:

```
Punk.exe
UnityPlayer.dll
Punk_Data/                  <- the whole folder (steam_api64.dll lives here)
MonoBleedingEdge/           <- the whole folder (game's Mono runtime)
```

You do **not** need `steam_appid.txt` (the server writes it), the Steam client, BepInEx, or the mod.
To ship a new game build, rebuild the image with `build-image.sh` (it re-stages the base game from
your local install) and push — servers pick it up when the node pulls the new image.

## How players connect

The server binds the **primary allocation port** (UDP) and hosts on the `Udp` transport. Players set
`Transport = Udp` in their own mod config and join via `join <host:port>` (or the default
`UdpAddress:UdpPort`). Set the **Advertised Address** variable to your public IP/DNS so the in-game
join code is copy-pasteable. Session cap is **4 players** (a mod compile constant).

The **first player to join becomes the session admin** and gets host-like controls (START / KICK)
via a secret capability token — the headless server has no UI of its own. With **Auto-Start Run = 0**
(default) that admin presses START; set it to `1` for a hands-off server that launches the run by
itself. World settings (seed, friendly-fire) are chosen by that admin, not by the server.

## Variables (full list)

| Variable | Default | What it does |
|----------|---------|--------------|
| `MOD_AUTO_UPDATE` | `1` | `1` = check GitHub for a newer mod build each boot; `0` = keep the installed one. |
| `MOD_VERSION` | `latest` | `latest` or a release tag (e.g. `v0.1.131`) to pin the mod build. |
| `MOD_RELEASE_REPO` | `Osanchez/PunkMultiverse` | GitHub owner/repo the mod is pulled from. |
| `GITHUB_TOKEN` | *(blank)* | Optional token for API rate limits / a private mod repo. |
| `INSTALL_BEPINEX` | `1` | `1` = overlay the image's baked BepInEx each boot; `0` = use the game copy's own. |
| `STARTUP_EXE` | `Punk.exe` | Executable to launch. |
| `GAME_DIR` | `/home/container` | Game install path in the container. |
| `SERVER_ADDRESS` | `0.0.0.0` | Advertised join host (written into the join code). |
| `AUTO_START_RUN` | `0` | `1` auto-launches the run; `0` waits for the admin to press START. |
| `HP_SCALING_PER_PLAYER` | `0.25` | Enemy HP multiplier added per player (`EnemyHealthScalePerPlayer`). |
| `COIN_DESPAWN_SECONDS` | `45` | Gold-coin lifetime (`CoinDespawnSeconds`). |
| `MOD_MANIFEST_POLICY` | `Reject` | `Reject`/`Warn` on mod-version mismatch. |
| `ENABLE_ADMIN_COMMANDS` | `1` | Watch `devcmd.txt` for runtime dev/admin commands. |
| `SYNC_DIAGNOSTICS` | `0` | Verbose `[Diag]` sync logging. |
| `STOP_GRACE_SECONDS` | `20` | Seconds to wait for a clean save on Stop before force-kill. |
| `STEAM_APPID` | `2850470` | Written to `steam_appid.txt` (keeps the game off the Steam client). |
| `WINEDEBUG` | `-all` | Wine debug channels. |
| `EXTRA_ARGS` | *(blank)* | Extra args appended to the Punk.exe command line. |

The port is the panel's primary allocation (`SERVER_PORT`), mapped to the mod's `UdpPort`.

## Lifecycle

- **Startup / done** — the panel marks the server "running" when it sees `[Udp] hosting on` in the
  console (the coordinator is listening, even before anyone joins). `start-server.sh` streams the
  BepInEx log to stdout so the console and this regex work.
- **Stop / Restart** — the panel sends `^C`; `start-server.sh` traps it and (when admin commands
  are enabled) writes `quit` to the command file. The mod's `quit` devcmd ends the session — saving
  the economy stash and sending clients a clean disconnect — then exits the process. If the game
  hasn't exited within `STOP_GRACE_SECONDS`, the script escalates to a signal and finally
  `wineserver -k`. Restart is a stop followed by a fresh start; stale command-file leftovers are
  cleared on boot so a restart can't loop.
- **Self-update** — on each boot the server overlays BepInEx from the image, then queries
  `MOD_RELEASE_REPO` for the wanted release (`MOD_VERSION`, default `latest`), and installs it only
  if the tag differs from `BepInEx/plugins/PunkMultiverse/.installed_version`. If GitHub is
  unreachable it keeps the installed copy. So publishing a new mod release updates every server on
  its next restart — no image rebuild.
- **Admin commands** — with `ENABLE_ADMIN_COMMANDS=1` you can drive the running server by writing
  devcmds into `BepInEx/plugins/PunkMultiverse/devcmd.txt` (e.g. `status`, `roster`, `start`,
  `spawn ...`). Output goes to `devout.txt` and the console.

## Testing the egg

You can smoke-test the image locally before importing into the panel:

```bash
docker build -t punkmv-server:test .
docker run --rm -it \
  -p 7778:7778/udp \
  -e SERVER_PORT=7778 \
  -v /path/to/your/modded/PUNK:/home/container \
  punkmv-server:test
```

Watch for `[Udp] hosting on *:7778`. Then, from a normal game client with `Transport = Udp`,
`join <docker-host-ip>:7778`. (Mount just the base game — BepInEx and the mod install themselves.)

## Known risks / open items

- **The Udp transport must be in a release for the server to work.** The self-update pulls the mod
  from `Osanchez/PunkMultiverse` **releases**, which are cut from `main`. The `Udp` transport (and
  `LiteNetLib.dll`) currently live on the `feat/litenetlib-transport` branch — until that merges to
  `main` and a release is published, `MOD_VERSION=latest` installs a mod **without** Udp support and
  the server won't host on Udp. The start script logs a warning if `LiteNetLib.dll` is absent from
  the pulled release. **Action:** merge the branch (an auto-release follows), or point `MOD_VERSION`
  at a tag that includes it.
- **Wine boot of a Unity+Steam game is the main unknown.** `SteamAPI.Init` will fail with no Steam
  running; the mod handles that gracefully, but whether the *base* game boots fully headless under
  Wine needs a real run — this is the first thing to verify when you test.
- **No password gate yet.** Anyone who can reach the port and passes the mod-version check can join
  (the transport's connection key filters stray packets, it is not a password). A server password is
  a future mod feature.
- **WAN play needs a routable port** (port-forward or a VPS/cloud host). localhost/LAN works out of
  the box.
