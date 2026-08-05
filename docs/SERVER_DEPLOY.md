# Dedicated server: how it runs and how to deploy it

The public server is a **plain `docker run` container on Omar's old desktop**, reached over
**Tailscale SSH**. There is no control panel, no SFTP, and no hosting provider. Everything below
is `ssh <host> docker ...`, so the only credential involved is the Tailscale SSH key already in
`~/.ssh/config`.

> **Why dedicated hardware and not a VPS.** A *shared* VPS is not reliable enough for a
> high-throughput, UDP-reliant game server. Sharing the box means contending for it, and the
> throttling that follows compromises network stability directly: the transport needs to send and
> receive on a steady cadence, and a host that deschedules the guest holds packets that were
> already late. We measured it — the hypervisor suspended the whole 4-vCPU VM for ~650 ms at a
> time under load and the guest kernel lost 31.6% of wall time, which surfaced as position gaps in
> PvP that looked exactly like a netcode bug. There is no code fix for time the machine never gave
> you; it needs cores that are actually yours and no one else's.

## The machine

| | |
|---|---|
| Host | `osanchez-dt` (Omar's old desktop; `hostname` reports `Osanchez-DT`) |
| Reached by | Tailscale SSH — `ssh punkdt` (alias in `~/.ssh/config` → `100.107.235.97`, user `omar`) |
| OS | **Windows.** SSH lands in `cmd.exe`, so no `head`/`tail`/`uname` in one-liners — use `findstr`, or run the tool *inside* the container where you get a real shell |
| Docker | Docker Desktop. If `docker` fails with an npipe error, Docker Desktop is not running |
| Container | `punkmv`, image `osanchezdev/punk-punkmultiverse:latest`, `--restart unless-stopped` |
| Ports | `7778/udp` (also published publicly through a playit.gg UDP tunnel at `punk-mv.playit.game:17201`) |
| Volume | `punkmv-data` → `/home/container` — the game files, `config.cfg`, `server.cfg`, logs and saves all live here and **survive image updates** |

`tools/srv.ps1` wraps all of the below (`state`, `log`, `cmd`, `get`, `put`, `config`, `restart`,
`recreate`). Tailscale SSH prints a post-quantum advisory on every connection; it is noise.

## Where settings actually come from

Three layers write `config.cfg`, and they are applied in this order — **later wins**:

1. the mod's own defaults (`src/Core/NetConfig.cs`)
2. `server_image/start-server.sh`, from `-e` variables on the container, but **only for variables
   the operator actually set** (the `was_set` guard) — an unset knob leaves the file alone
3. **`/home/container/server.cfg` on the volume**, applied last, beating both

**`server.cfg` is the file you want.** It survives restarts *and* image updates, and it is the
only one of the three that is not overwritten by something else. Setting match tuning as `-e`
variables looks like it works — the boot banner echoes the shell variable — and then loses
silently to `server.cfg` two steps later. Don't keep tuning in both places.

```
tools/srv.ps1 config                      # read it
tools/srv.ps1 put local.cfg /home/container/server.cfg
tools/srv.ps1 restart
```

Format: one `Key = Value` per line, `[Session]` assumed, `Section.Key` for anything else.

### Verifying a setting really took

The mod prints a `[Config]` block on every boot: the settings that differ from defaults, then a
warning for every key on disk it no longer reads (retired, misfiled under the wrong section, or
misspelled). **Trust that block over `start-server.sh`'s banner** — the banner prints its own shell
variables and cannot see the overrides file applied after it. The two disagreeing is exactly how a
"fixed" server ran the wrong ring schedule on 2026-08-05.

```
tools/srv.ps1 state
```

## Updating the mod

The container **self-updates from GitHub releases** on every boot (`MOD_VERSION=latest`), so
shipping the mod is just:

```
git push origin main      # pre-commit bumps the version; CI builds and publishes the release
tools/srv.ps1 restart     # server pulls the new release on boot
```

The join handshake requires an **exact** mod-version match, so server and clients must be on the
same release. Plan a protocol or version bump as one coordinated step.

> **Ordering trap.** Release the mod *before* changing pacing config. A new `BrRingStages` read by
> an old mod build multiplies against the old uniform hold — 12 zones × a 5-minute hold is a
> 69-minute match. See [`BATTLE_ROYALE.md`](BATTLE_ROYALE.md) §4.

## Updating the image

Only needed when `server_image/` itself changes (Dockerfile, `start-server.sh`, `entrypoint.sh`) —
not for mod changes.

```
"C:\Program Files\Docker\Docker\Docker Desktop.exe"      # start it if `docker version` errors
cd server_image && ./build-image.sh --push               # stages BepInEx + the base game, builds, pushes
tools/srv.ps1 recreate                                   # pull + rebuild the container
```

Registry credentials are the Docker Hub token already in `~/.docker/config.json` (user
`osanchezdev`); `docker push` picks it up with no extra setup.

**The base game is baked into the image but copied to the volume only on FIRST boot.** An image
rebuild therefore does *not* update the game files on an existing server — that is why a client
reporting a `0.12.x` version mismatch is a *base game* mismatch, not a mod one, and why rebuilding
alone never fixes it. Removing the volume is the (destructive) way to re-seed it.

## Container recipe

Only these six variables are ours. Everything else in `docker inspect` (`PATH`, `WINEPREFIX`,
`DISPLAY`, `HOME`, …) comes from the image and must not be re-passed. Note the deliberate absence
of any `BR_*` variables — match tuning lives in `server.cfg`.

```bash
docker run -d --name punkmv --restart unless-stopped \
  -p 7778:7778/udp \
  -v punkmv-data:/home/container \
  -e ENABLE_ADMIN_COMMANDS=1 \
  -e SERVER_PORT=7778 \
  -e SERVER_ADDRESS=192.168.1.226 \
  -e ENABLE_GAME_MODES=1 \
  -e GAME_MODE=BattleRoyale \
  -e LOG_LEVEL=Verbose \
  osanchezdev/punk-punkmultiverse:latest
```

## Admin commands

The mod polls `BepInEx/plugins/PunkMultiverse/devcmd.txt` twice a second and truncates it after
executing, so queuing a command is an append:

```
tools/srv.ps1 cmd "simprof 30"
tools/srv.ps1 cmd "uploadlogs"
```

`uploadlogs` pushes this server's log for the current run id to S3 — see
`infra/diagnostics-s3-setup.ps1`.
