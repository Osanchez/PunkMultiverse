---
name: mp-server
description: Deploy to and test against the dedicated PunkMultiverse server (Docker Desktop container on the Windows desktop `osanchez-dt`, reached over SSH) — fast DLL push, bot-driven match, and the full diagnostic devcmd set (hostinfo, freezeprobe, allocprof, simprof, ShipLatency/SendCadence). Use when asked to deploy the server, run a dedicated-server or Battle Royale test, measure server frame time / GC / freezes, or investigate PvP position lag. Args: optional focus, e.g. "br", "latency", "freeze", "alloc".
---

# Dedicated server: deploy + test

The dedicated server is a Docker container running the Windows game under Wine, hosted on
**Docker Desktop on the Windows desktop `osanchez-dt`** (Windows 11 Pro, i7-8700K, 12
threads). This replaced the shared Pelican VPS, whose hypervisor suspended the whole VM
~650ms every second under load — see `[[jitter-investigation]]` memory for that saga; do not
re-diagnose it.

> **Migrated off docker-in-WSL on 2026-07-27.** Everything now runs on the Docker Desktop
> daemon (`docker context` = `desktop-linux`). Docker commands are plain `docker ...` — do
> NOT prefix with `wsl -e`, and do not look for the stack inside Ubuntu. The old WSL daemon
> still holds stale copies of these containers; ignore it.

## Access

```bash
ssh punkdt                    # key ~/.ssh/punkdt, user omar@100.107.235.97 (Windows host)
```

**Quoting through cmd.exe breaks constantly** (`;` gets eaten, `$(...)` explodes, and Go
`--format` templates get mangled — `{{index .Config.Labels "com.x.y"}}` comes back as
`function "com" not defined` even from a single-quoted PowerShell string). ALWAYS write a
PowerShell script locally and scp it:

```bash
cat > /tmp/x.ps1 <<'PSEOF'
docker ps --filter name=punkmv --format '{{.Names}} {{.Status}}'
PSEOF
scp -q /tmp/x.ps1 punkdt:C:/Users/omar/x.ps1 \
  && ssh punkdt 'powershell -NoProfile -ExecutionPolicy Bypass -File C:\Users\omar\x.ps1'
```

For anything with nested quotes, skip templates entirely and parse JSON in PowerShell:
`$o = docker inspect punkmv | ConvertFrom-Json; $o[0].Config.Env`.

Never set `$ErrorActionPreference = "Stop"` in these scripts — docker writes benign progress
text to stderr and it turns into a fatal error mid-script.

## Addresses

| | Address | Notes |
|---|---|---|
| LAN clients | **`192.168.1.226:7778`** | The desktop's Ethernet IP; this is `SERVER_ADDRESS` |
| Tailnet clients | **`100.107.235.97:7778`** | Same host as SSH — works from anywhere on the tailnet |
| Internet (live) | **`punk-mv.playit.game:17201`** | playit.gg UDP tunnel — `playit` container on the same box; tunnel LOCAL address must be `192.168.1.226:7778`, never 127.0.0.1. Adds ~85ms vs direct. |
| Internet (alt) | router → UDP 7778 → `192.168.1.226` | Direct port-forward on the modem — lower latency than the tunnel, exposes the home IP |

**UDP now works from the LAN and the router.** Docker Desktop publishes `7778/udp` directly
on the Windows host (`0.0.0.0:7778`), so the old WSL constraint is gone — no `netsh
portproxy` (TCP-only), no WSL NAT IP that moves on restart, and Tailscale is no longer the
only path. The box was upgraded Win10 → Win11 26200 for this; `networkingMode=mirrored` was
tried first and abandoned (Docker's published ports stayed unreachable under it) — don't
retry it, the Docker Desktop migration is what actually solved UDP.

## Deploying a build

**Fast path (seconds) — for iteration.** The container normally self-updates from GitHub
releases at boot, which would overwrite a hand-placed DLL, so disable that first. The
container running today was created **without** `MOD_AUTO_UPDATE` set, so it self-updates —
check `docker inspect punkmv` before assuming a pushed DLL will survive a restart:

```bash
# once: recreate with MOD_AUTO_UPDATE=0 (keeps the volume, so it's quick)
docker rm -f punkmv
docker run -d --name punkmv --restart unless-stopped -p 7778:7778/udp \
  -v punkmv-data:/home/container \
  -e SERVER_PORT=7778 -e SERVER_ADDRESS=192.168.1.226 \
  -e ENABLE_GAME_MODES=1 -e GAME_MODE=BattleRoyale \
  -e LOG_LEVEL=Verbose -e ENABLE_ADMIN_COMMANDS=1 -e MOD_AUTO_UPDATE=0 \
  osanchezdev/punk-punkmultiverse:latest

# then per build: push the merged DLL straight in
docker cp PunkMultiverse.dll punkmv:/home/container/BepInEx/plugins/PunkMultiverse/
docker restart punkmv
```

Build it with `powershell -File build.ps1`; the artifact is
`bin/Release/merged/PunkMultiverse.dll` (ILRepack output — **never** the un-merged one).

**Release path (~4 min)** — only when the change must reach OTHER machines: commit to main
(CI auto-releases, version auto-bumps via the pre-commit hook), wait for the tag, then
`docker restart punkmv` with `MOD_AUTO_UPDATE=1`.

**Version lockstep is mandatory**: the manifest policy is `Reject`, so bots/clients must run
the same version as the server. Deploy the same build to every dev install
(`build.ps1 -GameDir "<install>"`) or joins fail with "Version mismatch".

## Running a match

Bot harness (2 headless bots, god + orbit + fire, from the dev machine):

```powershell
tools/ship-ab.ps1 -SampleSeconds 150 -Ceilings auto -Server 192.168.1.226 -Port 7778
```

Wait for `GO LIVE` in the server log before probing; then drive devcmds:

```bash
docker exec punkmv sh -c 'echo "status" >> /home/container/BepInEx/plugins/PunkMultiverse/devcmd.txt'
```

## Diagnostics (all devcmds, all off by default)

| cmd | what it answers |
|---|---|
| `hostinfo <s>` | Is the HOST stealing time? Reads `/proc` via Wine's `Z:\`. **`VERDICT-DATA` jiffies ≈100% = healthy; ~68% = hypervisor suspending the VM.** |
| `freezeprobe <s>` | 4 sentinel threads (spin/sleep/wsrv/sock). Gaps on the pure-**spin** thread = the OS stopped scheduling the process. All-zero = clean. |
| `allocprof on\|off` | Allocation by phase, by MsgType, and by labelled sub-step. Found the 64KB/call `GetEntity` LINQ scan. |
| `simprof <s>` | Times all ~170 vanilla per-frame methods **plus the mod's own** (`*`-prefixed). Read `total`, never `max` — a global pause lands on whatever ran. |
| `livedemand on\|off\|report` | Who resolves entity data → live GameObject (measured 7036 lookups, 0 hits on a coordinator). |
| `nostream on\|off` | Block segment-streaming instantiation (experiment; measured 0 blocks — that path is unused on a coordinator). |
| `htrim <names\|all\|off>` | Skip presentation systems (lighting/particles/minimap). Measured ~12% fps, no effect on stalls. Default OFF. |
| `shipdelay <ms\|auto>` | Live A/B of the ship playout ceiling (compiled 120ms). |
| `orbit <s> <period>` / `tpplayer <slot>` / `shipsmooth <slot>` | Full-throttle circles / collapse spawn distance / drawn-pose smoothness of another player's ship. |

## Reading the logs

```bash
docker logs punkmv 2>&1 | grep -aE "\[Frame\]|\[SendCadence\]|\[HostInfo\]|\[FreezeProbe\]"
```

Healthy dedicated-hardware baseline (measured 2026-07-27, i7-8700K, 2 bots):

```
[Frame]        avg 20-22ms  fps 44-48   ZERO frames >250ms
[SendCadence]  gapAvg 26ms  gapMax 52ms  over250ms=0  sendCall 0.005ms
[HostInfo]     jiffies 104%  0 stretched samples  steal 0%
[FreezeProbe]  0 gaps >100ms on all four sentinels
client [ShipLatency]  p98 5-7ms  wanted 76ms  saturated 0%  underruns 0/s  jitter 2ms
```

Anything far off those numbers is a regression worth chasing. `saturated=100%` with
`p98` in the hundreds of ms is the signature of server freezes, NOT a netcode bug.

## Gotchas that cost real time

- **Stale X lock after an abrupt daemon/host stop**: Xvfb refuses `:0`, Unity dies with
  `Failed to create batch mode window`, and auto-restart reuses the same `/tmp` so it loops
  forever. Fix: `docker rm -f punkmv` + recreate (fresh `/tmp`); the volume is fine.
- **Docker Desktop does not start over SSH.** It needs an interactive desktop session, so a
  headless `docker desktop start` fails and every docker command then errors. If the daemon
  is down, Omar has to sign in on the machine. "Start Docker Desktop when you sign in" is the
  standing fix — confirm it is enabled before relying on a reboot bringing the server back.
- **`docker pull` "A specified logon session does not exist"** — the Windows credential
  helper. Fixed by removing `credsStore` from `~/.docker/config.json` (backup at
  `.bak-migration`); if it returns, use an image already present locally.
- **`simprof` only completed while InGame** before v0.1.195 — an ended run left ~170 patches
  applied, silently slowing the server until restart.
- The desktop also runs ~24 other containers (media stack, bots, TeslaMate). Load is
  normally ~2 of 12 threads, but a heavy neighbour can produce stalls that mimic the bug —
  `hostinfo` distinguishes them (real CPU pressure vs the phantom-idle VPS signature).
- **Check what depends on a container before deleting it.** Removing the ollama containers
  silently broke MemMachine's embedder and left it crash-looping 74 times before anyone
  noticed. Grep other containers' env/config for the name first.
