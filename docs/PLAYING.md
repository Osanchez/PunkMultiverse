# Install & play

Player-facing guide for PunkMultiverse. For how the system is built, see the
[architecture README](../README.md).

## Install

1. Install [BepInEx 6 (Unity Mono)](https://github.com/BepInEx/BepInEx) into your
   `PUNK Playtest` folder.
2. Download the latest zip from
   [Releases](https://github.com/Osanchez/PunkMultiverse/releases) and extract it over the
   game folder — a single `PunkMultiverse.dll` lands in `BepInEx/plugins/PunkMultiverse/`.
3. Launch through Steam. **PLAY ONLINE** appears in the main menu, with the mod version and
   update status at the bottom of the screen.

Everyone in a session needs the **same mod version**; mismatched joins are rejected with
both versions named. You normally never update by hand — new releases download at startup
and apply on the next launch (the previous build is kept as `PunkMultiverse.dll.bak`).

## Playing together

- **Host over Steam:** PLAY ONLINE → HOST LOBBY → GAME SETTINGS (world seed: type, paste, or
  leave empty for random; friendly fire; enemy HP scaling) → COPY CODE → send it to friends,
  or INVITE FRIENDS through the Steam overlay. Ready up, then START GAME. Starting solo is
  fine — friends can join mid-run with the same code.
- **Join anything:** copy whatever your host sent you and use JOIN FROM CLIPBOARD. It accepts
  a `PMV-XXXXX-XXXXX-XXXX` lobby code, a dedicated server's `ip:port`, or a server SteamID64
  — the right connection method is chosen for you.
- **Join a dedicated server:** PLAY ONLINE → DIRECT CONNECT → type or PASTE the address and
  port → CONNECT. Failures return you to the screen with the reason.
- **Reconnect:** REJOIN LAST SESSION appears whenever your previous session is verified still
  running. Your build, vault, and gold come back, and you spawn at the party's latest
  unlocked station. It hides itself once the session is gone, and never appears after a kick
  or after deliberately leaving from the lobby.
- **If the host leaves:** the run continues. A remaining player is promoted (a banner names
  them), the lobby code keeps working, and the old host can rejoin like anyone else.

## In game

| Input | Action |
|---|---|
| Hold **Tab** | Party scoreboard (HP, kills, deaths, distance) |
| **Q / E** (or arrows) | Cycle spectated teammate after you die |
| **F9** | Network debug overlay |
| **F10** | Verbose sync diagnostics |
| **F11** | Dump the ownership table to the log |
| **F8** | Upload this machine's log for the current run id |

Controllers are fully supported on the PLAY ONLINE screens: d-pad or left stick to move
between rows, **A** to activate, **B** to go back.

## Video options

The mod adds an **FPS LIMIT** row to the game's own VIDEO options tab. It defaults to **MAX**
(your monitor's refresh rate) and steps down to 60. Note that with VSYNC on, the display sync
governs and the cap has no effect — that's how Unity works, which is why the row sits
directly under the vsync toggle.

In windowed mode the game window is freely resizable (drag any edge, or maximize). Turn it
off with `[Video] ResizableWindow = false`.

## Configuration

`BepInEx/plugins/PunkMultiverse/config.cfg`, created on first launch. The settings most
worth knowing:

- `[Update] AutoUpdate` — `true` *(default)* downloads new releases at startup and applies
  them on the next launch. `false` = check only.
- `[Session] ModManifestPolicy` — what the **host** does when a joiner's other installed
  BepInEx mods differ:
  - `Reject` *(default)* — refuse the join, naming the differing mods.
  - `Warn` — allow it; everyone sees a `[!] MODS` marker by their name.
  - `Ignore` — no check. Other mods are never *synced* either way, so mixing gameplay mods
    means different rules per player; cosmetic and UI mods are generally fine.
- `[Video] FpsLimit` / `ResizableWindow` — see above.
- `[Diag] LogLevel` — `Normal` *(default)*, `Verbose` (full-rate instrumentation; use this
  when reporting a bug), or `Quiet`. Warning-class watchdogs run at every level.

## Known behavior

- Loot, gold, vault, and shop stock are **per-player by design** — there is no loot stealing,
  and progression is shared while purchases are not.
- Menus don't pause the world in multiplayer, and slow-motion effects are disabled. The
  vanilla suspend-save is replaced by a continuous run auto-save (the pause menu reads EXIT).
- Late joiners and rejoiners spawn at the party's most recently unlocked station.
- Host migration only engages mid-run; if the host leaves during the lobby or loading, the
  session ends (it's cheap to recreate).
- Kicks are not bans — a kicked player can rejoin with the code.
- Daily-challenge runs aren't supported in net runs yet.
- Dedicated servers are open by design: anyone with the address and a matching mod version
  can join.

## Reporting a problem

Set `[Diag] LogLevel = Verbose`, reproduce the issue, then press **F8** (or use SEND LOGS in
the pause menu) to upload your log. Quote the run id printed in the log — every player's log
for that run lands in the same folder, which is what makes cross-machine diagnosis possible.
