# Client Hitch Instrumentation Specification

Status: Proposed
Scope: `PunkMultiverse` BepInEx/Unity Mono client and host runtime
Primary input: `INSTRUMENTATION_BRIEF.md`

## 1. Purpose

Add low-overhead runtime instrumentation that can distinguish among these causes of an
intermittent client FPS drop or hard freeze:

1. PunkMultiverse work in `NetSession.Update`.
2. PunkMultiverse Harmony patch work inside game methods.
3. Managed allocation pressure and garbage collection.
4. Accumulating network/game objects or collections.
5. Unity/game rendering, physics, or other work outside PunkMultiverse.

The instrumentation must leave useful evidence when the Unity main thread is unable to log. It
must be enabled independently of the verbose `SyncDiagnostics` event stream and must not generate
per-event log traffic during normal play.

This work is diagnostic only. It must not change network authority, damage, terrain, projectile,
or entity behavior. Any leak or performance defect discovered while implementing the probes is a
separate fix.

## 2. Success criteria

The feature is successful when a client log from a bad run can answer all of the following:

- Did the Unity main thread stop advancing for at least the configured hitch threshold?
- What PunkMultiverse or Unity-between-frames phase was active when it stopped?
- If supported by the installed Mono runtime, what was the main thread's managed stack?
- What was whole-frame average, maximum, and p99 time around the incident?
- Which hot Harmony patches ran, how often, and for how long?
- Did a GC collection or a rise in managed allocation coincide with the incident?
- Were puppet, projectile, ownership, cell, or total GameObject counts growing?
- Was existing `NetProfiler` tracked work high or low at the same time?

All normal interval reports must use the same monotonic elapsed-time basis and include a monotonic
timestamp so adjacent lines can be correlated without relying only on wall-clock log prefixes.

## 3. Out of scope

- Re-investigating the already-fixed death loop, terrain cascade feedback, or static-prop
  ownership churn.
- Automatically changing gameplay limits when a counter becomes large.
- Shipping a native debugger or claiming that a watchdog-thread stack is the main-thread stack.
- Treating managed heap growth as an allocation-rate measurement.
- Requiring an attachable Unity Profiler or a development game build.

## 4. Diagnostic interpretation

| Evidence | Primary interpretation |
| --- | --- |
| `[Profile]` high, named tick section high | PunkMultiverse `NetSession.Update` work |
| `[PatchProfile]` patch total/max high | PunkMultiverse Harmony patch work |
| Frame p99 high, mod tick and patch totals low | Work outside measured mod paths; rendering/physics/game code is likely |
| Allocation bytes rise, gen-0 delta rises, and frame/hitch occurs | Managed allocation/GC pressure is likely |
| A live count or collection size trends upward with frame time | Object or collection accumulation is likely |
| `[Hitch]` names a patch or tick phase | Main thread was last observed inside that measured region |
| `[Hitch]` names `Unity.BetweenUpdates` while mod costs are low | Stall happened outside the instrumented mod update; not proof of a specific Unity subsystem |

These are correlations, not automatic root-cause verdicts. Reports must use words such as
`phase`, `observed`, and `coincident`, not `caused by`.

## 5. Proposed components

### 5.1 `Core/RuntimeInstrumentation.cs`

Own the feature lifecycle and interval reporting. Responsibilities:

- Start and stop the hitch watchdog.
- Capture the main Unity thread reference during `Plugin.Awake` or the first `NetSession.Update`.
- Receive frame heartbeats and phase changes.
- Sample whole-frame time and GC counters.
- Report patch aggregates and object counters.
- Reset run-scoped aggregates without losing process-wide watchdog state.

The component must expose only cheap methods for hot paths:

```csharp
RuntimeInstrumentation.HeartbeatStart(SessionState state);
RuntimeInstrumentation.SetPhase(PerfPhase phase);
RuntimeInstrumentation.HeartbeatEnd();
RuntimeInstrumentation.SampleFrame(float unscaledDeltaTime);
RuntimeInstrumentation.ResetRun();
RuntimeInstrumentation.Shutdown();
```

`PerfPhase` must be a fixed enum. Do not pass dynamically-created strings from a hot path.

### 5.2 `Core/PatchProfiler.cs`

Aggregate Harmony patch calls using a fixed `PatchId` enum and fixed arrays indexed by that enum.
Each record contains:

- Call count.
- Sum of elapsed `Stopwatch` ticks.
- Maximum elapsed ticks for one call.
- Optional number of calls over the single-call spike threshold.

The hot-path API is:

```csharp
long started = PatchProfiler.Enter(PatchId.WorldCaptureCellChanges);
try
{
    // Existing patch body, unchanged.
}
finally
{
    PatchProfiler.Exit(PatchId.WorldCaptureCellChanges, started);
}
```

Requirements:

- Use `Stopwatch.GetTimestamp()`, not a new `Stopwatch` per call.
- When disabled, `Enter` returns zero and `Exit` is a single predictable branch.
- Do not allocate strings, tuples, delegates, scopes, dictionaries, or log messages per call.
- Aggregation occurs on the Unity main thread. Reporting snapshots and clears the fixed arrays.
- A patch exception or early return must still reach `Exit` through `finally`.
- Patch timing measures the patch body only, not the original game method.
- Calls/sec and ms/sec use actual measured report duration, not an assumed three seconds.

### 5.3 `Core/MainThreadWatchdog.cs`

Run one named background thread for process lifetime while the feature is enabled. The thread
watches a heartbeat sequence number and the monotonic timestamp of the most recent main-thread
heartbeat or phase transition.

The watchdog must not call Unity APIs. Data shared with it must be primitive or immutable and
published with `Volatile.Read/Write` or `Interlocked`.

### 5.4 Optional `Core/ObjectCounters.cs`

Keep cheap lifecycle-backed counters separate from slow Unity scene scans. The implementation may
be folded into `RuntimeInstrumentation` if doing so remains clear.

## 6. Configuration

Add these entries in the `Diag` section of `NetConfig`:

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `ProfileFrames` | bool | existing `true` | Master switch for interval frame, patch, GC, and cheap-count reports |
| `HitchWatchdog` | bool | `true` | Enables the independent main-thread watchdog |
| `HitchThresholdMs` | int | `250` | First stalled-main-thread warning; clamp to 100-5000 ms |
| `HitchRepeatMs` | int | `2000` | Repeat warning interval during one continuous stall; clamp to 500-30000 ms |
| `ProfileReportInterval` | float | `3` | Normal aggregate interval; clamp to 1-30 seconds |
| `ProfileObjectScanInterval` | float | `15` | Expensive Unity object scan interval; 0 disables it |
| `CaptureHitchStack` | bool | `true` | Attempt a main-thread managed stack only when runtime support is detected |

`SyncDiagnostics` must not gate any feature in this specification. Changing a config entry at
runtime must take effect without restarting where BepInEx config entries already support it.
Starting/stopping the watchdog may be applied on the next main-thread update.

Do not expose a configurable per-call patch log mode.

## 7. Main-thread hitch watchdog

### 7.1 Heartbeat and phase tracking

At the beginning of every `NetSession.Update`, including menu/loading states:

1. Publish the current session state.
2. Increment the heartbeat sequence.
3. Publish the current monotonic `Stopwatch` timestamp.
4. Set phase to `NetSession.Update`.

Before each measured subsystem call in the in-game loop, publish a fixed phase that matches the
existing `NetProfiler` section. After `NetSession.Update` finishes, publish
`Unity.BetweenUpdates` and another heartbeat timestamp. Use `try/finally` so the end heartbeat is
not skipped by a normal early return or exception.

Patch profiler `Enter` publishes `Harmony.<PatchId>` as the active phase. `Exit` restores the
previous phase. This requires a fixed-size main-thread phase stack or explicit previous phase;
recursive/nested patches must not incorrectly reset the outer phase.

A phase is the last observed region, not a sampled stack. The log must say `phase=` rather than
`stuck in=`.

### 7.2 Stall state machine

The watchdog polls no more frequently than every 25 ms. A stall begins when the heartbeat age is
at least `HitchThresholdMs` and the session state is `InGame` or `Loading`.

For each continuous stall:

- Assign an incrementing process-local `hitchId`.
- Immediately write one warning containing hitch ID, age, phase, heartbeat sequence, session
  state, and monotonic time.
- Attempt at most one managed stack capture.
- Repeat a compact warning no more often than `HitchRepeatMs` while the heartbeat remains stale.
- When the main thread advances, log one recovery line with total observed stall duration and the
  same hitch ID.
- Do not report a new hitch ID until at least one heartbeat has advanced.

Example:

```text
[Hitch] id=7 detected age=286ms mono=1842.553s state=InGame phase=Harmony.WorldCaptureCellChanges heartbeat=109233
[Hitch] id=7 main-stack-supported=false fallback=phase-marker
[Hitch] id=7 ongoing age=2291ms phase=Harmony.WorldCaptureCellChanges
[Hitch] id=7 recovered duration=2478ms mono=1844.745s
```

### 7.3 Logging while the main thread is stopped

The first detection line must be emitted by the watchdog thread before attempting stack capture.
Use `Plugin.Log.LogWarning` only after confirming BepInEx logging is safe from a background thread
in the deployed runtime. Guard it with `try/catch`.

Also maintain a small dedicated append-only fallback file, for example
`BepInEx/PunkMultiverse.hitch.log`, opened once with auto-flush. This prevents loss if the normal
logger is blocked on a lock held by the main thread. The watchdog is the only writer. Rotate or
truncate it at plugin startup to a bounded size (recommended maximum: 1 MiB), and never write more
than the throttled stall lines above.

On recovery, mirror the incident summary to the normal BepInEx log. Extend the F8/close log upload
payload to include the bounded watchdog file, or append its contents under a clear separator before
gzip compression.

### 7.4 Managed stack capture

Capturing another thread's managed stack is runtime-specific. The implementation must probe the
installed Unity Mono runtime once at startup for a supported cross-thread `StackTrace` mechanism.

- If supported, capture the thread stored as the Unity main thread and label the result
  `main-stack`.
- Perform the attempt on a single helper worker after the initial stall line is safely written.
- Allow only one capture worker at a time. A timed-out worker must not cause new workers to be
  created for repeated warnings.
- Bound logged stack text to 16 KiB and one stack per hitch.
- If unsupported, fails, or times out, log the capability/failure once and retain the phase marker
  fallback.
- Never use `Environment.StackTrace` from the watchdog and label it as the main-thread stack; it
  is the watchdog's stack and is diagnostically misleading.
- Never suspend/abort the Unity main thread directly from mod code.

At startup, log exactly one capability line:

```text
[Hitch] watchdog enabled threshold=250ms repeat=2000ms main-stack=supported|unsupported
```

## 8. Whole-frame sampler

On every main-thread update while `ProfileFrames` is enabled, record
`Time.unscaledDeltaTime * 1000`. This measures time between Unity frames and is independent of
`NetProfiler`'s mod-only window.

Maintain a fixed histogram or preallocated sample buffer. The sampler must not allocate per frame.
At each actual report interval, log:

- Sample count.
- Average milliseconds.
- Maximum milliseconds.
- p99 milliseconds (an approximate fixed histogram is acceptable if documented).
- Effective FPS derived from average frame time.
- Counts of frames at or above 16.7, 33.3, 50, 100, and 250 ms.

Example:

```text
[Frame] mono=1842.300s n=181 avg=16.8ms p99=31.0ms max=84.2ms fps=59.5 over=16.7:72,33.3:3,50:1,100:0,250:0
```

The current interval must be reset only after a successful snapshot. An exception in another
reporter must not discard frame data.

## 9. GC and allocation instrumentation

At startup, baseline:

- `GC.CollectionCount(0)`, `(1)`, and `(2)`.
- `GC.GetTotalMemory(false)`.
- `GC.GetAllocatedBytesForCurrentThread()` if the deployed runtime implements it.

At each report, log collection deltas, current managed heap bytes, and heap delta. If the allocated
bytes API is available, log main-thread allocated-byte delta and bytes/sec. Handle counter reset or
wrap without emitting a negative rate.

`GC.GetTotalMemory(false)` delta must be labeled `heapDelta`, never `allocated` or `allocRate`.
It is retained-object/heap-size movement and misses objects allocated and collected within the
interval. If the allocation API is unsupported, emit `alloc=unsupported` rather than deriving an
allocation rate from heap delta.

Do not call `GC.Collect`, request a compacting collection, or call `GetTotalMemory(true)`.

Example:

```text
[GC] mono=1842.300s gen=14/1/0 heap=286.4MiB heapDelta=+3.1MiB mainAlloc=42.7MiB rate=14.2MiB/s
```

The first report after startup/reset is a baseline and must not report process-lifetime collection
counts as interval deltas.

## 10. Harmony patch profiler

### 10.1 Required patch IDs

Instrument at least these bodies:

| Patch ID | Source patch |
| --- | --- |
| `WorldCaptureCellChanges` | `WorldSync.CaptureCellChanges.Postfix` |
| `WorldSuppressNetCellCascade` | `WorldSync.SuppressNetCellCascade.Prefix` |
| `ProjectileCaptureFirePrefix` | `ProjectileSync.CaptureFire.Prefix` |
| `ProjectileCaptureFirePostfix` | `ProjectileSync.CaptureFire.Postfix` |
| `ProjectileMarkVisual` | `ProjectileSync.MarkVisualProjectile.Prefix` |
| `ProjectileMarkVisualPhysics` | `ProjectileSync.MarkVisualPhysicsProjectile.Prefix` |
| `ProjectileSuppressDamage` | `ProjectileSync.SuppressVisualProjectileDamage.Prefix` |
| `ProjectileSuppressHitscanDamage` | `ProjectileSync.SuppressVisualHitscanDamage.Prefix` |
| `DamageRouteSingle` | `DamageSync.RouteTakeDamage.Prefix` |
| `DamageRouteList` | `DamageSync.RouteTakeDamageList.Prefix` |
| `DamageDropWorldRemote` | `DamageSync.DropWorldDamageOnRemoteVictims.Prefix` |
| `EnemyOnEntitySpawned` | `EnemySync.OnEntitySpawned.Postfix` |

Reflection-heavy `UnitStatus` reads may be added in a second pass, but they must use the same fixed
ID mechanism.

Count every invocation during `InGame` and `Loading`, including invocations that take an early
return. Do not count menu calls. For multi-target Harmony patches, aggregate by logical patch ID;
the initial version does not need a separate row per target method.

### 10.2 Report format

Every normal report interval, emit one bounded summary line. Include only IDs with calls or a
non-zero maximum. Sort a copied reporting snapshot by total ms/sec; do not sort or mutate hot-path
storage.

For each shown ID include calls/sec, total ms/sec, and maximum single-call ms. Keep a stable short
name table allocated at initialization.

```text
[PatchProfile] mono=1842.300s World.CaptureCell calls=8420/s total=21.4ms/s max=2.7ms; Projectile.Damage calls=91/s total=1.8ms/s max=0.3ms
```

Show all active IDs unless doing so would exceed 8 KiB. If capped, include `omitted=N`.

A single patch call at or above 10 ms should set a pending spike record. Emit at most one
`[PatchProfile] SPIKE` line per frame from the main-thread frame end, selecting the worst call.
Do not synchronously log from the hot patch itself.

## 11. Object and collection counters

### 11.1 Cheap counters reported every normal interval

Report:

- Active `RemotePuppet` count.
- Active `RemoteEntityPuppet` count.
- `ProjectileSync.VisualProjectiles.Count`.
- `EnemySync.Owners.Count`.
- `EnemySync.FixedOwners.Count`.
- `EnemySync.KilledCount`.
- `WorldSync.Pending.Count`.
- World cells observed by `CaptureCellChanges` per second.
- Projectile spawns observed by the two visual-mark patches per second.

Expose read-only internal count properties rather than exposing private collections. Puppet counts
should be maintained by idempotent lifecycle registration (`OnEnable`/`OnDisable`, with destroy
safety), not `FindObjectsByType` every three seconds. Resetting a run must reconcile lifecycle
counters rather than blindly setting them to zero while objects remain active.

Do not prune or clear `VisualProjectiles` as part of instrumentation. Its unbounded size is one of
the values under investigation; changing it would mix diagnosis with a behavior fix.

### 11.2 Slow Unity object scan

On the configured slower cadence, on the main thread only, count:

- Live `Projectile` objects.
- Live `PhysicsProjectile` objects.
- Total live scene `GameObject` objects.

Use the non-sorting Unity API where available. Treat these scans as intrusive:

- Time the scan itself and include `scanMs` in the log.
- Never run it inside a Harmony patch.
- Do not include its time in a patch aggregate.
- Run at most once per `ProfileObjectScanInterval` and never while a previous scan/report is in
  progress.
- If one scan exceeds 20 ms, log a warning and disable further slow scans for that run. Cheap
  counters and all other instrumentation remain enabled.
- If the exact Unity API is unavailable, log the unsupported counter rather than substituting a
  more expensive `Resources.FindObjectsOfTypeAll` scan.

Example:

```text
[Counts] mono=1842.300s remoteShips=2 remoteEntities=74 visualIds=12804 owners=41 fixed=3 killed=92 pendingCells=0 cells=8312/s visualSpawns=44/s
[Counts] mono=1845.102s scan projectiles=187 physicsProjectiles=22 gameObjects=9631 scanMs=4.8
```

## 12. Existing `NetProfiler` integration

Retain `NetProfiler` and its `[Profile]` prefix for backward log compatibility. Make these changes:

- Use `ProfileReportInterval` and actual elapsed seconds for rates.
- Add a monotonic timestamp to interval and spike lines.
- Correct the current spike description: it reports an interval maximum, not necessarily the
  current frame's section. Prefer retaining per-frame section laps so the named section is truly
  from the spike frame.
- Ensure `FrameEnd` executes through `finally` after `FrameStart`.
- Let `RuntimeInstrumentation` coordinate a common report boundary, but keep a failure in one
  reporter from preventing the others.

Do not merge patch time into `our-work/frame`; the two values measure different call contexts and
must remain separately visible.

## 13. Overlay

The F11 diagnostics overlay may show the most recent already-computed snapshot:

- Frame avg/p99/max.
- Last hitch ID/age/phase.
- Gen-0 delta and allocation rate or `unsupported`.
- Top patch by total time.
- Puppet and visual-projectile counts.

Rendering the overlay must not trigger object scans, sort profiler data, or build an unbounded
history. It must remain optional and is not an acceptance blocker for the initial implementation.

## 14. Lifecycle and safety

- Initialize after `Plugin.Log` and `NetConfig` are available.
- Store the Unity main thread reference only from `Plugin.Awake` or main-thread `Update`.
- Reset interval aggregates at run start/end alongside `NetProfiler.Reset`.
- Keep the watchdog alive across run transitions so loading freezes can be caught, but suppress
  warnings in ordinary menu/offline idle time.
- On plugin destruction, signal shutdown, close the fallback writer, and join background threads
  for no more than 250 ms. Never use `Thread.Abort`.
- Catch instrumentation exceptions at subsystem boundaries and emit a once-per-run warning.
  Instrumentation failure must not interrupt gameplay or change a Harmony prefix return value.
- Do not read Unity objects, `Time`, session objects, or BepInEx config entries from the watchdog
  thread. Publish primitive snapshots from the main thread.
- Bound every log line and in-memory history. No instrumentation collection may grow with run
  duration.

Estimated steady-state overhead target with `ProfileFrames=true` and no slow scan occurring:

- No managed allocation per frame from the frame/GC sampler.
- No managed allocation per profiled patch call.
- Less than 0.1 ms average main-thread overhead per 60 FPS frame under normal load.
- No more than one normal log line per reporter per report interval.

## 15. Logging contract

Use stable prefixes so an uploaded log can be machine-parsed:

- `[Hitch]`
- `[Frame]`
- `[GC]`
- `[PatchProfile]`
- `[Counts]`
- Existing `[Profile]`

Each interval line includes `mono=<seconds>s`. Field names and units are stable. Decimal output
must use invariant culture. Unknown or unsupported values are printed explicitly; do not omit them
in a way that could be mistaken for zero.

Do not include player names, Steam IDs, webhook URLs, filesystem user paths, or entity-specific
spam in these lines.

## 16. Implementation sequence

### Phase A: freeze evidence

1. Add config and lifecycle plumbing.
2. Add heartbeat, fixed phase markers, watchdog state machine, fallback file, and recovery line.
3. Add runtime capability probe and guarded main-thread stack attempt.
4. Verify loading and in-game stalls are caught without menu false positives.

### Phase B: interval correlation

1. Add whole-frame sampling.
2. Add correct GC/heap/allocation counters.
3. Put `NetProfiler` and new reporters on a common elapsed-time boundary.

### Phase C: hot patch attribution

1. Add the fixed-array `PatchProfiler`.
2. Instrument the required patch list without changing their return behavior.
3. Add pending per-frame spike reporting.

### Phase D: accumulation evidence

1. Add lifecycle-backed puppet counts and collection-size accessors.
2. Add cell/projectile event rates.
3. Add guarded slow scene scans.
4. Optionally surface the last snapshot in F11.

Each phase must build and be testable independently. A feature flag must permit disabling the
watchdog separately from the interval profiler during regression isolation.

## 17. Verification plan

### 17.1 Build and static checks

- Bump the project version only when producing a deployable build.
- Build with `powershell -File build.ps1`.
- Confirm no instrumentation hot path uses `new Stopwatch`, LINQ, `Traverse`, interpolated log
  strings, or dynamically-created keys.
- Confirm every instrumented prefix preserves all original return paths and every postfix retains
  its original behavior.
- Confirm a failure inside instrumentation cannot skip RNG restoration in `CaptureFire`.

### 17.2 Host/client runtime checks

Deploy and fully restart both local copies:

- Host: `C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest`
- Client: `C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Test2`

Run these scenarios for at least ten minutes each:

1. Idle in lobby: no watchdog false positives and no interval spam unless intended.
2. Normal flight: frame, GC, patch, count, and existing profile reports share close monotonic
   timestamps.
3. Dense terrain destruction: cell patch call rate rises; no per-cell log lines appear.
4. Sustained combat: projectile/damage patch rates rise; gameplay and damage outcomes are
   unchanged.
5. Segment streaming: `EnemyOnEntitySpawned` calls occur in bursts and puppet counts reconcile.
6. Run end/retry: aggregates reset, the watchdog stays healthy, and counts do not become negative.
7. Host/client comparison: both log instrumentation, with the client evidence independently useful.

### 17.3 Synthetic hitch test

In a temporary test-only branch or debugger, block the Unity main thread for approximately 600 ms
inside a named instrumented phase. Remove the forced delay before shipping.

Expected:

- One detection line arrives from the watchdog near 250 ms.
- The line contains the correct active phase.
- A supported runtime emits the main-thread managed stack; an unsupported runtime explicitly
  reports the phase fallback.
- One recovery line reports approximately the full delay with the same hitch ID.
- The fallback hitch file contains the incident even if the normal logger is unavailable.
- The corresponding frame report contains a frame near 600 ms.

Repeat with a delay between `NetSession.Update` calls. Expected phase:
`Unity.BetweenUpdates`.

### 17.4 Acceptance thresholds

The change is ready to deploy when:

- All five recommended instrumentation areas are represented in logs.
- A synthetic 600 ms main-thread stall is detected and recovered as specified.
- No report falsely labels watchdog stack or heap delta as main stack or allocation rate.
- No negative counters, duplicate watchdog threads, or unbounded profiler storage occur across
  three run restarts.
- Ten minutes of terrain/combat testing shows no gameplay divergence and no instrumentation log
  storm.
- Normal-load average instrumentation overhead meets the target in section 14, measured with the
  instrumentation itself disabled/enabled in comparable scenes.
- F8 upload contains both normal diagnostics and any fallback hitch incident.

## 18. First-response workflow after a real recurrence

1. Find the first `[Hitch] id=N detected` line and its recovery/ongoing lines.
2. Inspect a successful `main-stack`; otherwise use `phase` as the coarse location.
3. Compare the nearest `[Frame]`, `[Profile]`, and `[PatchProfile]` lines by `mono` time.
4. Check `[GC]` for collection and allocation coincidence.
5. Compare current and earlier `[Counts]` lines for monotonic growth.
6. Reproduce with `SyncDiagnostics=false` so verbose event logging does not distort the result.
7. Optimize or fix only after the evidence identifies a subsystem or accumulation trend.

## 19. Entity authority and identity instrumentation (v0.1.66)

The segment-lease replication layer adds bounded counters to `[Counts]`:

- `leases` / `pendingLeases`: committed leases and prepare transactions awaiting acknowledgement.
- `commits` / `acks`: cumulative lease commits and client acknowledgements.
- `staleStateDrops`: entity snapshots rejected because sender or epoch is not committed.
- `identityOverlaps`: stream-out/stream-in lifetimes that overlap for one network ID. These are
  observed without destroying either object; the game segment manager remains authoritative.
- `starvedPuppetFrames`: passive replicas held frozen because their snapshot stream is stale.
- `killLedgerIds`: static/dynamic death IDs included in bounded reconciliation chunks.

The guarded object scan also emits `[EntityAudit]` with puppet count, unique and duplicate network
IDs, never/stale snapshot counts, and the eight largest entity-type and segment populations. Lease
prepare/commit transitions use `[Lease]`; identity enforcement uses `[Identity]`. These records make
it possible to distinguish a legitimate dense streamed segment from duplicate identity tracking,
stale authority, or snapshot starvation without enabling per-entity packet logging.

Version 0.1.67 extends `[Counts]` with:

- `dormantDamage=queued/replayed`, proving visible-object hits survive an unavailable simulator.
- `terrain=revision/hash/count`, the host-ordered final-value ledger state.
- `terrainMismatch` and `repairs=sent/applied`, covering automatic convergence checks.

`[Ids] canonical entity baseline sent/applied` records the generation-time position baseline.
`[World] TERRAIN LEDGER MISMATCH` is actionable and must be followed by a repair sent/applied line
or an explicit repair-hash failure. Entity epoch drops now include the transmitted segment; a low
number during a lease commit is normal, while sustained growth indicates a protocol regression.

## 20. Population lifecycle and state-flow instrumentation (v0.1.73)

Version 0.1.73 replaces permanent duplicate quarantine with centralized, loader-safe retirement.
The previous GameObject is muted immediately, remains inert for a 0.75 second deferred-unload
grace window, and is destroyed only when it is no longer canonical. Quarantine components have no
`Update` or `LateUpdate`; one central 4 Hz pass retires them.

Authoritative position application now calls `EntityData.MoveTo` rather than assigning its
`position` field. `MoveTo` updates the game's `SpatialGrid`; the raw assignment left moving enemies
indexed in old room segments, causing those segment rebuilds to instantiate the same identity
again. Retirement remains as a bounded safety net for legitimate deferred stream overlap.

`[Population]` is an O(number of canonical identities) report and remains available even when the
guarded whole-scene scan disables itself. It includes:

- `canonical`, `units`, and `props` currently registered;
- `unitTypes` and `unitSegments`, providing exact active enemy density by type and room segment;
- `lifetimes=first/reentry/overlap`, separating genuinely first-seen identities, normal fully
  unloaded/reloaded lifetimes, and overlapping replacements;
- `lifetimeRates=first/reentry/overlap` per second;
- `quarantines=active/pending`, `retired`, `retiredRate`, and `oldest` quarantine age;
- `replacementTypes`, identifying which entity prefabs are churning.

An active quarantine count above 100 or overlap creation above 5/sec emits one bounded
`[Population] PRESSURE` warning until the condition clears.

Entity state protocol 6 uses `EntityStateBundle`: one sender tick contains multiple segment/epoch
groups. Host-owned state is sent directly to each interested peer; client-owned state is sent once
to the host, which consumes the complete bundle and forwards only groups near each other player.
The interest radius includes one segment of guard band so replicas begin receiving snapshots before
entering the visible/streamed area.

`[StateFlow]` reports per-second bundle, group, entry, and byte rates in each direction, average
groups and entries per outgoing bundle, plus groups/entries removed by peer-interest filtering.
Healthy two-player dense combat should be near the configured state rate in bundles/sec rather than
hundreds of segment datagrams/sec. A high entry rate with a low bundle rate indicates legitimate
population density; a high filtered rate indicates useful offscreen traffic suppression.
