# PunkMultiverse — Multiplayer Test Guide

How to set up a real multiplayer test, what to check feature by feature, and what to collect
when something breaks. Everything here has scripted two-instance coverage already — this guide
is for the *human* passes: real Steam accounts, real combat, 3–4 players.

## 1. Setup

### Option A — Steam (the real thing, 2+ PCs / accounts)
1. Every player installs BepInEx into their PUNK Playtest and drops the release zip's
   `BepInEx/plugins/PunkMultiverse/` folder in place (or grab `PunkMultiverse.dll` from a
   local build). **Everyone must run the same mod version** — the lobby screen shows
   `mod vX.Y.Z` under the title, and mismatched joins are rejected with both versions named.
2. Launch the game normally through Steam.
3. Host: main menu → **PLAY ONLINE** → **HOST LOBBY** → **COPY CODE** → send it (Discord etc.).
   Or **INVITE FRIENDS** for the Steam overlay invite.
4. Friends: **PLAY ONLINE** → copy the code → **JOIN FROM CLIPBOARD** (or accept the invite).
5. Pick colors, host optionally pastes a **WORLD SEED**, everyone **READY**, host **START GAME**.

### Option B — Loopback (solo, two instances on one PC — how all the scripted tests run)
1. Copy the whole game folder to a sibling directory (e.g. `PUNK Playtest - Test2`).
2. In BOTH copies edit `BepInEx\plugins\PunkMultiverse\config.cfg`:
   ```
   [Transport]
   Transport = Loopback
   ```
3. Launch both `Punk.exe` directly (not via Steam). Optional clickless automation in `[Debug]`:
   `AutoStart = Host` / `AutoStart = Join`, `AutoReady = true`, host `AutoLaunchRun = true`.
4. The F11 overlay shows session state, peers and RTT anywhere.

## 2. Test checklist

### Lobby
- [ ] Code copy → paste join works both directions; Steam overlay invite works
- [ ] Version display correct; joining with a DIFFERENT mod version is cleanly rejected and the
      message names both versions + the releases URL
- [ ] Colors change live for everyone; seed PASTE/RND (host only) shows for all
- [ ] Solo host + READY enables START GAME; a friend joining mid-run with the code lands in
      the same world and catches up

### Run start
- [ ] All players land in the SAME world (station layout, terrain) — a checksum mismatch aborts
      loudly to the menu rather than desyncing silently
- [ ] Loading screen dismisses for everyone without pressing a key
- [ ] Each player sees teammates' ships in their chosen colors, name labels above

### Movement & tracking
- [ ] Teammate motion is smooth (interpolated), no rubber-banding while they fly
- [ ] Your camera follows ONLY your ship (never zooms out to frame a distant teammate)
- [ ] Offscreen teammates get a colored edge arrow with name + distance; it vanishes when visible
- [ ] Hold **Tab**: scoreboard shows HP bars, kills, deaths, distance for the party

### Combat
- [ ] You see teammates' gunfire (visual projectiles); their shots damage enemies exactly once
      (watch an enemy HP bar while you both shoot it — no double-speed melting)
- [ ] Beam/hitscan and explosive weapons: same single-application check
- [ ] Shooting a teammate does full vanilla damage through their shields/i-frames (their HP truth
      is theirs; you'll see it drop on the scoreboard)
- [ ] Terrain destruction appears identically for everyone; loot drops are YOUR OWN (different
      per player — intended)
- [ ] Teammate death and respawn replicate; kill/death counts move on the scoreboard

### Enemies (needs flying to a combat zone)
- [ ] An enemy fought by two players behaves as ONE enemy (same position/HP for both)
- [ ] Kite an enemy toward a teammate and fly away — it hands off without teleporting (watch for
      `[Auth]` lines in the log)
- [ ] Enemies simulated by a teammate still visibly shoot at you; boss adds / spawner summons
      appear for everyone
- [ ] Burning enemies show flames for both players; charging enemies don't hit twice as hard

### Progression & world
- [ ] Station upgrade by one player: lights/shop/respawn fire for everyone (each pays their own)
- [ ] Scanner / instrument use reveals the map region for everyone
- [ ] Explored areas merge on the map (TAB map); teammate ship icons visible on it
- [ ] Minions fight alongside both players and are visible to both

### Disconnect / rejoin
- [ ] Kill a client's game mid-run: host lobby shows OFFLINE, their ship freezes, their enemies
      keep working (host takes over)
- [ ] Relaunch + JOIN FROM CLIPBOARD with the same code: back in the same run with your build,
      vault and gold (`[Stash] rejoin restore applied` in the log), terrain/kills/upgrades caught up
- [ ] Host quitting mid-run pops the session-lost screen on clients

### 3–4 players (untested territory — extra eyes here)
- [ ] Third/fourth join, colors, all-pairs visibility
- [ ] Authority handoffs between non-host players; damage routed between two clients (via host)
- [ ] Bandwidth/latency feel with 4 (F11 RTT stays sane)

## 3. When something breaks

Grab from EVERY machine involved:
- `BepInEx\LogOutput.log` (the whole file — mod lines are tagged `[Punk Multiverse]`)
- What you did in the ~30 seconds before, and which machine was host
- A screenshot if it's visual (name both players' colors)

Log markers worth searching: `GO LIVE`, `checksum`, `manifest applied` (matched/missing counts),
`[Auth]`, `REJOINED`, `[Stash]`, `Error`.

## 4. Known behavior (not bugs)

- Loot/gold/vault/shop are per-player by design; shop stock differs per player.
- Merged-cell terrain patches can look slightly different across clients (game RNG; cosmetic).
- Projectile spread patterns differ visually per client (damage is authoritative).
- Pause menus don't pause the world in multiplayer; slow-mo effects are disabled.
- Save & Exit is disabled during net runs — rejoining with the lobby code covers crashes.
- A rejoining or late-joining player's ship starts at the spawn station (not where they dropped).
