# Client FPS-drop instrumentation — handoff brief

Task: add instrumentation to a BepInEx mod (`PunkMultiverse`, a multiplayer sync layer for the
game **PUNK**) that will pinpoint the cause of intermittent **client** FPS drops / hard freezes if
they recur. This brief is self-contained — you do not need the originating chat.

## Symptoms (what we're hunting)
- FPS drops occur on the **client**, not the host. Host stays smooth.
- Often **silent**: no log spam, no exception. On hard freezes the BepInEx log simply stops
  advancing — i.e. the Unity **main thread is stalled**, so nothing new is written.
- **Progressive / "getting worse over time"** and **hitchy**, loosely correlated with terrain
  damage, combat, and dense-prop areas. Sometimes escalates to a full freeze / crash.
- Prior root causes already found & fixed this cycle (so DON'T re-chase these; they're context):
  - v0.1.61 — an entity `Die()` re-kill loop (re-streamed dead entities re-fired the whole onDeath
    chain every frame). Fixed in `EnemySync.KillInstance`.
  - v0.1.62 — terrain "pop" cascade feedback: `WorldSync` applied replicated cells with source
    `777301`, which `AutoPopper`/`CellRegrower` didn't treat as non-cascading (they only skip the
    game's `1324`/`15324`), so each replicated pop re-seeded a local cascade and ping-ponged
    host↔client into a freeze. Fixed with `WorldSync.SuppressNetCellCascade`.
  - v0.1.64 — ownership churn: the host's authority scan granted authority to **static props**
    (fruit/crates) near a client, producing assign→release→handoff waves. Now grab-eligibility
    requires the entity prefab to contain a `Unit` (`AuthorityManager.NeedsSimulation`).

## What instrumentation already exists (and its blind spot)
`src/Core/NetProfiler.cs` (v0.1.63): times the per-frame subsystem ticks called from
`NetSession.Update` (`ShipSync`, `WorldSync.Flush/Tick`, `EnemySync`, `Authority`,
`Transport.Poll(recv)`, etc.). Every ~3s logs a `[Profile]` line (avg/max ms per section + network
+ ownership-churn rates); logs `[Profile] SPIKE` when our tracked work in one frame exceeds ~20ms.
Gated on `NetConfig.ProfileFrames` (Diag section, default **on**). API: `FrameStart()`,
`Mark(name)`, `FrameEnd()`.

**Blind spot — this is the whole point of the new work.** `NetProfiler` only covers our tick path.
It does NOT measure:
1. **Our Harmony patches**, which run *inside game methods*, not on the tick path. Many are
   high-frequency and reflection-heavy (`HarmonyLib.Traverse.Create` allocates per call). The hot
   ones to instrument:
   - `WorldSync.CaptureCellChanges` — Postfix on `Level.SetCell`/`DestroyCell`; fires per cell
     change (hundreds/frame during a cascade).
   - `WorldSync.SuppressNetCellCascade` — Prefix on `AutoPopper`/`CellRegrower.OnCellChanged`; per
     cell change; reflects a field on a boxed struct each call.
   - `ProjectileSync.SuppressVisualProjectileDamage` / `SuppressVisualHitscanDamage` — Prefix on
     `HealthBase.ProjectileCollided` / `OnHitByHitscanWeapon`; per projectile/beam hit; uses
     `Traverse` (`OwnerUnit`).
   - `ProjectileSync.CaptureFire` (Prefix+Postfix on `WeaponBase.DoShoot`), `MarkVisualProjectile`/
     `MarkVisualPhysicsProjectile` (Prefix on `Projectile`/`PhysicsProjectile.Shoot`) — per shot /
     per projectile spawn.
   - `DamageSync.RouteTakeDamage` / `RouteTakeDamageList` (Prefix on `DamagableResource.TakeDamage`)
     and `DropWorldDamageOnRemoteVictims` — per damage / contact event; `Traverse` for amount/type.
   - `EnemySync.OnEntitySpawned` (Postfix on `EntityGameObjectManager.SpawnObjectForEntity`) — per
     entity stream-in; bursts hard during segment builds.
2. **GC pauses.** Our code allocates heavily per frame (new List/Dictionary/HashSet each Tick, LINQ,
   `Traverse`/reflection boxing). This is the leading suspect for "hitchy / worsening."
3. **The game's own frame cost** (rendering many puppets, physics from many projectiles/cells).

## Recommended instrumentation (build these; priority order)
1. **Main-thread hitch watchdog with stack capture (HIGHEST value — catches the silent freezes).**
   A background `System.Threading.Thread` that watches a heartbeat counter bumped once per
   `Update()` on the main thread. When the heartbeat hasn't advanced for > ~250ms, capture and log
   the **main thread's** managed stack trace (the thing our current logging can't show because the
   main thread is stuck). This is what would have instantly identified every freeze this cycle.
   Note: capturing another thread's stack in Mono/Unity is the tricky part — options: keep a
   main-thread `StackTrace` sampled cheaply each frame at suspected hot points, or use a
   watchdog that on stall dumps `Environment.StackTrace` of a main-thread continuation. At minimum,
   log "main thread stalled Nms" with the last `Mark()` section name that ran.
2. **GC instrumentation.** Per interval, log `GC.CollectionCount(0/1/2)` deltas and
   `GC.GetTotalMemory(false)` delta (bytes allocated/sec). A gen-0 spike coinciding with a hitch =
   allocation pressure; then hunt the per-frame allocators (Traverse, LINQ, new collections).
3. **Harmony-patch profiler.** Give the hot patches above a cheap `Stopwatch` + call counter,
   aggregated per interval (calls/sec + total ms/sec + max single call). Reuse the `NetProfiler`
   aggregate/report style. This reveals whether reflection in our patches is the cost.
4. **Object / collection-size counters.** Per interval: active `RemoteEntityPuppet` +
   `RemotePuppet` counts, live projectile count, `ProjectileSync.VisualProjectiles` set size (known
   unbounded — only cleared on Reset; a candidate leak), `EnemySync.Owners` dict size, cells
   changed/sec, total `GameObject` count. A count that climbs with the FPS drop points at
   accumulation.
5. **Real Unity frame-time / FPS sampler** (independent of our ticks): sample `Time.unscaledDeltaTime`,
   log rolling avg + p99 frame time each interval. Baseline: is the *whole* frame slow, or just our
   part? Correlate with (1)-(4).

Optional: wrap suspect regions in `UnityEngine.Profiling.Profiler.BeginSample/EndSample` so IF a
development build / attachable Unity Profiler is available they light up. (The playtest build is
likely non-development, so treat in-process logging as the primary path.)

## Codebase map (where to hook)
- `src/Core/NetProfiler.cs` — existing profiler; extend/mirror its aggregate+report pattern.
- `src/Core/NetSession.cs` `Update()` (~line 803) — the per-frame loop; heartbeat bump goes here.
- `src/Core/NetStats.cs` — cumulative counters (BytesIn/Out, MsgsIn/Out, AuthFlips, AuthReleases).
- `src/Core/NetConfig.cs` — add config toggles here (BepInEx `cfg.Bind`, "Diag" section). Existing:
  `ProfileFrames`, `SyncDiagnostics`.
- `src/Core/NetDiag.cs` — gated logging helpers + an F11 on-screen overlay (good place to surface
  live counters visually) + throttled logging helpers.
- Harmony patches to instrument live in: `src/Sync/WorldSync.cs`, `src/Sync/ProjectileSync.cs`,
  `src/Sync/DamageSync.cs`, `src/Sync/EnemySync.cs`, `src/Sync/UnitStatus.cs` (reflection-heavy
  reads), `src/Sync/RemotePuppet.cs`, `src/Sync/RemoteEntityPuppet.cs`.
- Logging: `Plugin.Log.LogInfo/LogWarning`. Log file: `<gameDir>/BepInEx/LogOutput.log`. In-game
  **F8** uploads a gzipped log to a Discord webhook (`src/UI/LogUploader.cs`); **F11** = diag
  overlay; `SyncDiagnostics` toggles verbose per-event `[Diag:*]` logs (chatty — its own disk I/O
  can distort measurements, so keep the profiler independent of it, as `ProfileFrames` already is).

## Build / deploy / test loop
- Two local copies of the game run as host + client (loopback):
  - Host: `C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest`
  - Client: `C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Test2`
- Build + deploy: `powershell -File build.ps1` deploys to the host copy;
  `powershell -File build.ps1 -GameDir "<path>"` deploys to another copy. Run BOTH.
- Bump `<Version>` in `PunkMultiverse.csproj` each build.
- A new DLL only loads on a full game restart (BepInEx loads plugins at launch).
- Verify deployed version: check `PunkMultiverse.dll` FileVersion in each copy's
  `BepInEx/plugins/PunkMultiverse/`.

## Reading the game's own code (needed to know what game methods to patch/measure)
- `ilspycmd` is installed. Decompile from the game assembly:
  `Punk_Data/Managed/Punk.Main.dll`.
  - List classes: `ilspycmd -l c "<...>/Punk.Main.dll"`
  - Decompile a type: `ilspycmd -t <TypeName> "<...>/Punk.Main.dll"`
- Reflection-only assembly load fails on this DLL — use `ilspycmd`, not `Assembly.ReflectionOnlyLoad`.
- Key game types already mapped: `LevelSegmentComponentManager` (streams segments within 3 of the
  local player; everything else is data-only `EntityData`), `EntityGameObjectManager`
  (`SpawnObjectForEntity` / `InstantiateGameObjects` / `UnloadEntity`), `Level` (`SetCell`/
  `DestroyCell`/`CellChanged`, changeSource `1324`=auto-pop, `15324`=burn), `AutoPopper`/
  `CellRegrower` (cascade seeders), `Unit` (base for anything with AI/behaviors — the "needs
  simulation" marker).

## First thing to try when a drop recurs
With `ProfileFrames` on, read the client `LogOutput.log`: the `[Profile]` lines localize our tick
cost and give the `authChurn` rate; `[Profile] SPIKE` names the worst tick section. If those are
low while FPS is bad, the cost is in a Harmony patch, GC, or game rendering/physics — which is
exactly what instrumentation items (1)-(5) are designed to disambiguate.
