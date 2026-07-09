# Punk Multiverse

Online co-op mod for **PUNK**, inspired by [Noita Entangled Worlds](https://github.com/IntQuant/noita_entangled_worlds).
Host a Steam lobby, friends join by pasted lobby code or Steam invite, pick ship colors, and play a shared run — up to **4 players**.

Everything runs in-game via BepInEx; there is no companion app. Networking uses the Steamworks
that already ships with PUNK (Steam lobbies + Steam Datagram Relay), so there's no port
forwarding and no extra dependencies.

## Design (v1)

| Concern | Approach |
|---|---|
| Join | Clipboard lobby code, Steam friend invite/overlay, rejoin live session |
| Topology | Host-relay star over ISteamNetworkingMessages |
| Enemies | Proximity authority — the closest player simulates each enemy (host is registrar/fallback) |
| Damage | Applied once, by the entity's current authority; others send damage requests |
| Projectiles | Fire events replicated; remote projectiles are visual-only |
| Loot & economy | **Per player** — your drops, gold, vault, and shop are your own |
| World | Destructible terrain synced; map progression (stations, scanner reveals) shared |
| Saves | Save & exit disabled during net runs; live-session rejoin covers crash recovery |

## Building

Requires the .NET SDK. The repo folder is expected to sit inside the game install
(`...\PUNK Playtest\PunkMultiverse\`); if it doesn't, pass `-GameDir`.

```powershell
powershell -File build.ps1            # Release + deploy to BepInEx\plugins
powershell -File build.ps1 -Debug     # Debug + pdb
powershell -File build.ps1 -Zip       # + dist\PunkMultiverse-vX.Y.Z.zip
```

## Dev testing without Steam

Set `Transport = Loopback` in `BepInEx\plugins\PunkMultiverse\config.cfg`, copy the game
folder, launch both `Punk.exe` directly, then use the **F8** overlay to Host in one instance and
Join in the other.

## Status

- [x] M0 — scaffold, loopback + Steam transports, handshake, ping (F11 overlay)
- [x] M1 — Steam lobby, lobby UI ("PLAY ONLINE" in main menu), clipboard code, invites, version handshake
- [x] M2 — synced run start (seed + level checksum barrier), puppet ships, position interpolation, ship colors
- [x] M3 — fire-event replay (visual-only projectiles), authority-routed ship damage, death/resurrect events, terrain destruction sync, save/leaderboard guards
- [x] Native host seed selection (lobby seed row: PASTE from clipboard / RND, host-only; shown to all)
- [x] Native player tracking (colored ring + name label on teammates; screen-edge arrows with distance when offscreen; Tracker config section)
- [x] M4 — proximity enemy authority: fingerprint→netId manifest (~98% match; stragglers are drifted
      decorative props), host registrar with hysteresis, ENTITY_STATE streaming both directions,
      puppet AI muting (RemoteEntityPuppet), ENTITY_KILLED, entity damage routing
- [x] M5 — minion sync (fixed owner-authority, prefab replay), shared station upgrades + scanner
      reveals (with pending queues for unstreamed entities); game over/won needs no messages —
      synced HP/kill state drives local end checks
- [x] M6 — live-session rejoin (slot reservation, seed replay, catch-up stream: cell ledger +
      kills + owners + upgrades), module-grid sync (Odin memento, 5s change detection), net-run
      pause policy, release packaging (build.ps1 -Zip)

Known v1 gaps (by design or deferred):
- Merged-cell terrain visuals can differ cosmetically between clients (unseeded game RNG).
- Projectile spread replays with local randomness; replayed projectiles skip explosion VFX
  (area damage/terrain arrive authoritatively instead — correctness over cosmetics).
- Puppet cosmetics: no aim/turret tracking, boost particles, hook, or leg animation yet.
- Plant destruction visuals are per-client.
- Fast travel briefly teleports puppets locally (snapshots correct it within ~100 ms).
- Steam two-account join implemented but needs a two-account validation pass.
- Minion / station / scanner / instrument / scoreboard paths are patch-validated but need a
  real gameplay pass.

## Mod interop

Net runs bypass `RunSetupScreen.StartGame` interceptors (e.g. PunkSeedPicker) — the host's
seed is authoritative. Mods that gate `GameScene.GoToGameScene` behind their own UI (e.g.
PunkMetaLoadout's profile picker) will block net-run starts; disable them for online play
until they check `NetSession.Active`.

## Party UI

- Teammate name labels in their chosen color above their ships (no ring), always readable.
- Screen-edge arrows in the teammate's color with name + distance while they're offscreen;
  they disappear the moment the teammate is visible.
- Hold **Tab** for the party scoreboard: HP bar, kills, deaths, distance per player.
- Explored map regions merge between players (`ShareMapExploration` toggle).

## CI / Releases

Pushes to `main` build the mod and publish a zip Release via GitHub Actions.
The proprietary reference DLLs are never committed: CI downloads `punkmultiverse-refs.zip`
from the `refs` release of the private `Osanchez/PunkMods-refs` repo using the
`REFS_TOKEN` secret (fine-grained PAT with read access to that repo).

One-time setup / after a game update or new csproj reference:

```powershell
powershell -ExecutionPolicy Bypass -File tools\update-refs.ps1   # rebuild + upload the refs bundle
```
