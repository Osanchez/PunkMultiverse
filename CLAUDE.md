# PunkMultiverse — working notes for agents

Online co-op and battle royale for **PUNK**, a Unity game shipped as compiled IL. This mod is a
BepInEx plugin that Harmony-patches the game's internals.

**Read [`docs/VANILLA_GOTCHAS.md`](docs/VANILLA_GOTCHAS.md) before changing anything that touches
game code.** It is the shortest path to not repeating a debugging session someone already paid
for.

---

## The one rule

**Never reimplement game logic.** Damage, physics, AI, loot and world generation must run
through the vanilla code paths. Reimplementing any of them makes single-player and multiplayer
diverge in behaviour, which is the expensive kind of bug.

The consequence: you are always working *around* the game's quirks, never replacing them. So
when vanilla does something surprising, the fix is to accommodate it and write it down — not to
route around it with your own version.

## The second rule

**There is no game source.** The game ships as `Punk.Main.dll`. Everything you know about it
comes from decompilation, and the DLL is not committed (this repo is public).

Before answering "what is the signature of X" from memory — don't. Look it up:

| Need | Where |
|---|---|
| A signature, a member list, what type something is | [`docs/api/`](docs/api/) — generated, complete, 1201 types |
| How a system actually works, and why | the curated docs below |
| What a method *does* | `gamescan/cache/<build>/Type.cs` — full decompiled source, local only |
| A type not in the cache | `ilspycmd -t <TypeName> "../Punk_Data/Managed/Punk.Main.dll"` |

`ilspycmd` is required (`dotnet tool install -g ilspycmd`). **Reflection-only assembly load
fails on this DLL** — use `ilspycmd`, not `Assembly.ReflectionOnlyLoad`.

Decompile the DLL *where it sits*, next to the Unity assemblies. ILSpy resolves them from
there; without them it emits defensive casts and `Unknown result type` comments that look like
real differences and are not.

---

## Layout

| Path | Contents |
|---|---|
| `src/Core/` | Session, transport selection, authority, clock sync, config, diagnostics |
| `src/Sync/` | Entity/ship/world/projectile/economy replication, puppets, adaptive timing |
| `src/Transport/` | `ITransport` implementations (Steam, SteamServer, LiteNetLib UDP, loopback) |
| `src/Protocol/` | Wire messages, reader/writer, channel and sequencing contracts |
| `src/Modes/` | Battle royale: ring schedule, drop selection, contested loot |
| `src/Patches/` | Harmony patches against game internals |
| `src/UI/` | Lobby, direct connect, HUD, scoreboard, drop screen, zone visuals |
| `tools/` | Test harnesses and admin scripts — never part of the mod DLL |
| `docs/` | Design specs, subsystem deep dives, test plans |
| `docs/api/` | **Generated** API index — do not edit |
| `gamescan/` | Game-API hashes + the mod's dependency contract |

**All mod sources live in `src/`.** The csproj sets `EnableDefaultCompileItems=false` and
whitelists `src/**/*.cs` — because default globbing otherwise compiled the decompiled game tree
and produced 424 errors. Put new code in `src/`; nothing else needs excluding.

---

## Documentation map

Start with the area doc, then the API index for exact signatures.

**Core architecture**
- [`ENTITY_SYNC_ARCHITECTURE.md`](docs/ENTITY_SYNC_ARCHITECTURE.md) — residency/authority contract, the design of record
- [`BATTLE_ROYALE.md`](docs/BATTLE_ROYALE.md) — ring schedule, drop, contested loot, PvP
- [`GAMESCAN.md`](docs/GAMESCAN.md) — detecting what a game update changed
- [`CONTENT_SYNC.md`](docs/CONTENT_SYNC.md) — serving the host's custom content to joiners, and why the drop table depends on it

**Game systems** (what the game does, independent of this mod)
- [`enemies.md`](docs/enemies.md) · [`players-and-projectiles.md`](docs/players-and-projectiles.md) · [`plants.md`](docs/plants.md) · [`bosses.md`](docs/bosses.md)
- [`terrain.md`](docs/terrain.md) · [`environment.md`](docs/environment.md) · [`level-generation.md`](docs/level-generation.md)
- [`pickups-and-loot.md`](docs/pickups-and-loot.md) · [`containers.md`](docs/containers.md) · [`interactables.md`](docs/interactables.md)
- [`modules-and-ship-building.md`](docs/modules-and-ship-building.md) · [`shops-and-economy.md`](docs/shops-and-economy.md)
- [`ui-and-screens.md`](docs/ui-and-screens.md) · [`map-and-minimap.md`](docs/map-and-minimap.md) · [`fog-and-lighting.md`](docs/fog-and-lighting.md)
- [`save-and-serialization.md`](docs/save-and-serialization.md) · [`game-state-flow.md`](docs/game-state-flow.md)
- [`audio.md`](docs/audio.md) · [`input.md`](docs/input.md) · [`camera.md`](docs/camera.md)

**Operations**
- [`SERVER_DEPLOY.md`](docs/SERVER_DEPLOY.md) — dedicated server (plain `docker run`, not Pelican)
- [`TESTING.md`](docs/TESTING.md) · [`harness.md`](docs/harness.md) · [`test-scenarios.md`](docs/test-scenarios.md)

---

## Build, test, release

```powershell
powershell -File build.ps1          # build + deploy into BepInEx\plugins
powershell -File build.ps1 -Zip     # + dist\PunkMultiverse-vX.Y.Z.zip
```

One shipped assembly: LiteNetLib is merged and internalized at build time (`ILRepack.targets`).

**Every push to `main` publishes a release.** A pre-commit hook bumps the patch version, and the
version is baked into the handshake, lobby data, menu banner and artifact from one source (the
csproj `<Version>`). Paths that never reach the DLL — `tools/`, `docs/`, `gamescan/`,
`server_image/`, `infra/`, `.github/`, `.claude/` — are excluded from both the hook and CI so
they do not burn a version. **Keep those two lists identical.**

Do not commit or push unless asked. On this repo a commit to `main` is a release.

---

## After a game update

Steam updates the base game silently. When something inexplicable starts happening, this is the
first thing to run:

```powershell
tools\gamescan.ps1
```

It reports only what changed *that the mod depends on*, split into breaking (Harmony will
throw), behavioural (nothing will warn you), and irrelevant. See
[`GAMESCAN.md`](docs/GAMESCAN.md). The mod also logs a `[GameScan]` line at boot when the game
no longer matches its baseline.

A version mismatch of the form **`0.12.x` is the base game, not the mod.**

---

## Conventions

- **Logging:** `Plugin.Log.LogInfo($"[Tag] ...")` with a bracketed subsystem tag. Existing tags
  include `[Config]`, `[GameScan]`, `[Clock]`, `[Jitter]`, `[Heal]`, `[PhantomHit]`,
  `[ShipLatency]`, `[Growth]`. Grep logs by tag.
- **Config:** every setting is bound in `NetConfig.Init`. Deleting a key means adding a line to
  `ConfigAudit.Retired`, or the boot-time audit will not report it. Changing a *default* needs a
  key rename, since existing users have the old value persisted.
- **PowerShell** is 5.1 here: no `&&`, no ternary, no `??`. Scripts with non-ASCII inside
  strings need a UTF-8 BOM or 5.1 reads them as ANSI and mis-parses.
- **Diagnostics before conclusions.** Several long hunts on this project were extended by a
  diagnostic that lied. If a probe and reality disagree repeatedly, suspect the probe.
