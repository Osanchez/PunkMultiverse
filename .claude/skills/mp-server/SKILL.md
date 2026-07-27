---
name: mp-server
description: Deploy to and test against the WSL-hosted dedicated PunkMultiverse server (Docker container on osanchez-olddesktop, reached over Tailscale/SSH) — fast DLL push, bot-driven match, and the full diagnostic devcmd set (hostinfo, freezeprobe, allocprof, simprof, ShipLatency/SendCadence). Use when asked to deploy the server, run a dedicated-server or Battle Royale test, measure server frame time / GC / freezes, or investigate PvP position lag. Args: optional focus, e.g. "br", "latency", "freeze", "alloc".
---

# Dedicated server: deploy + test in WSL

The dedicated server is a Docker container running the Windows game under Wine, hosted in
**WSL Ubuntu on `osanchez-olddesktop`** and reached over Tailscale. This replaced the shared
Pelican VPS, whose hypervisor suspended the whole VM ~650ms every second under load — see
`[[jitter-investigation]]` memory for that whole saga; do not re-diagnose it.

## Access

```bash
ssh punkdt                    # key ~/.ssh/punkdt, user omar@100.107.235.97 (Windows host)
```

Docker lives inside WSL, so every docker command is `wsl -e ...` from that Windows session.

**Quoting through cmd.exe breaks constantly** (`;` gets eaten, `{{.Status}}` is parsed as a
command, `$(...)` explodes). ALWAYS write a script locally and scp it:

```bash
cat > /tmp/x.sh <<'EOF'
docker ps --filter name=punkmv --format "{{.Names}} {{.Status}}"
EOF
scp -q /tmp/x.sh punkdt:C:/Users/omar/x.sh && ssh punkdt 'wsl -e bash /mnt/c/Users/omar/x.sh'
```

`sudo` needs a password in that WSL; use `wsl -u root -e ...` instead — no password needed.

## Addresses

| | Address | Notes |
|---|---|---|
| Clients (anywhere on tailnet) | **`100.110.40.88:7778`** | STABLE — survives every restart |
| From the desktop itself | `172.19.107.138:7778` | WSL NAT IP — **changes on every WSL restart** |

Windows 10 there, so `networkingMode=mirrored` is unavailable and `netsh portproxy` is
TCP-only — **UDP cannot be forwarded from the LAN or the router**. Tailscale is the only
path for remote clients until the server moves to real Linux.

## Deploying a build

**Fast path (seconds) — for iteration.** The container normally self-updates from GitHub
releases at boot, which would overwrite a hand-placed DLL, so disable that first:

```bash
# once: recreate with MOD_AUTO_UPDATE=0 (keeps the volume, so it's quick)
docker rm -f punkmv
docker run -d --name punkmv --restart unless-stopped -p 7778:7778/udp \
  -v punkmv-data:/home/container \
  -e SERVER_PORT=7778 -e SERVER_ADDRESS=100.110.40.88 \
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
scratchpad/ship-ab.ps1 -SampleSeconds 150 -Ceilings auto -Server 100.110.40.88 -Port 7778
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

- **Stale X lock after an unclean `wsl --shutdown`**: Xvfb refuses `:0`, Unity dies with
  `Failed to create batch mode window`, and auto-restart reuses the same `/tmp` so it loops
  forever. Fix: `docker rm -f punkmv` + recreate (fresh `/tmp`); the volume is fine.
- **The WSL NAT IP moves on restart** and breaks the host's 11 `netsh portproxy` rules for
  unrelated services. Use the tailnet IP for anything that must persist.
- **`docker pull` may fail** with "A specified logon session does not exist" (Windows
  credential helper). Use an image already present locally instead.
- **`simprof` only completed while InGame** before v0.1.195 — an ended run left ~170 patches
  applied, silently slowing the server until restart.
- The desktop also runs ~26 other containers (media stack, bots, databases). Load is
  normally ~2 of 12 threads, but a heavy neighbour can produce stalls that mimic the bug —
  `hostinfo` distinguishes them (real CPU pressure vs the phantom-idle VPS signature).
