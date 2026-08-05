# Battle Royale Mode — Specification

Status: **SPEC — approved for implementation** (2026-07-26). Product decisions confirmed
with Omar: last-alive wins immediately; dead players may spectate or leave; the ring is
real painted terrain advancing in discrete stages; a started match is sealed (no joins,
**no rejoins** — disconnecting counts as elimination); the ring center prefers open
areas; care packages drop every 10 minutes with a destroyer-exclusive reward.

Every mechanism below is grounded in the existing codebase — file references name the
exact reuse points. Nothing here requires a new sync primitive: the mode composes the
terrain pipeline, the loot latch, the kill-credit pipeline, the station-unlock
replication, and the ship-authority model that already exist.

---

## 1. Overview

A second game mode, `BattleRoyale`, alongside the existing (implicit) `Standard` mode.

| | Standard | Battle Royale |
|---|---|---|
| Spawn | everyone at the start station | scattered across distant stations |
| Stations | unlock by paying | **all open from the start** |
| Coins | normal (vanilla starts at 0) | 0 (defensively enforced) |
| Shop stock | grows per station unlock | **fully stocked from the start**, normal prices |
| Loadout | player's choice, chosen on the selector screen | **everyone is the Gunner; no selection screen** |
| PvP | FriendlyFire toggle | **always on, damage scaled down (0.25x)** |
| Player damage to enemies | 1x | **effectively 2x** (enemy HP halved) |
| Environment | as generated | hazard cells cleared; **closing lava ring** |
| Care packages | — | **every 10 min, destroyer-takes-all** |
| Match length | open-ended | **45 minutes hard cap** (ring fully closed) |
| Win | co-op survival | **last player alive** |
| Other players' location | trackers/scoreboard/map-share | **hidden** |
| Mid-match join/rejoin | allowed | **sealed** — disconnect = elimination |

### Mode selection (two surfaces)

- **UDP dedicated server**: `GameMode = Standard | BattleRoyale` in `config.cfg`
  (`[Session]`, same pattern as `EmptyServerResetSeconds`). Applied on **server
  restart**; every run the server starts uses the configured mode.
- **Self-hosted**: a new **MODE** row on the GAME SETTINGS panel of PLAY ONLINE
  (`LobbyScreen.BuildSeedPanel`, `src/UI/LobbyScreen.cs:688`), using the same
  paired-button toggle pattern as FRIENDLY FIRE (`MakeSettingsRow` +
  `UiTheme.MakeButton`/`SetToggled`, `LobbyScreen.cs:703-772`): `STANDARD` (default) /
  `BATTLE ROYALE`. Plumbs through `HostWithSeed → NetSession.HostOnline(...)` exactly
  like `friendlyFire` (`src/Core/NetSession.cs:300`), including the sidecar leader
  relay. The lobby seed readout line (`LobbyScreen.cs:1142`) shows the mode.

## 2. Wire & mode plumbing

- `GameMode : byte { Standard = 0, BattleRoyale = 1 }` in `src/Protocol/Messages.cs`.
- Carried in three existing messages:
  - `StartRunMsg` (+`Mode`) — parsed by every client at run start; stored as
    `NetSession.CurrentMode` in `StartRun`/`HandleStartRun` (`NetSession.cs:770/808`).
  - `LobbyStateMsg` (+`Mode`) — every lobby screen shows the mode before START.
  - `PartyLeaderSettingsMsg` (+`Mode`) — self-hosted leader's choice reaches a sidecar
    coordinator (adoption at `NetSession.cs:2836`).
- Four new messages (all host→clients, reliable, Control channel, same send idiom as
  `RunEnded` at `NetSession.cs:2134`):
  - `AnnounceMsg { Text, Seconds }` — server-driven toast on all clients (received →
    `UI.Toast.Show`). No broadcast toast exists today; this is also generally useful
    (MOTD, admin messages).
  - `RingStateMsg { CenterX, CenterY, SafeRadius, Stage, NextShrinkAtClock }` — drives
    the ring HUD and each client's own kill-zone damage. `NextShrinkAtClock` is in the
    shared session clock (`ClockSync`) so countdowns agree.
  - `PlacementMsg { Slot, Placement, AliveRemaining }` — elimination callouts + the
    dead player's placement screen.
  - `CarePackageMsg { NetId, X, Y }` — package spawn announcement + arrow target.
- Protocol version 16 → **17**.
- **Pre-generation is mode-agnostic**: the world builds identically; every BR change
  happens at or after go-live, so the pre-built world stays reusable and the
  determinism barrier is untouched.

## 3. Match setup (at go-live)

1. **Open every station** (host). Enumerate
   `EntityManager.GetEntitiesWithComponent<Station.Data>()`, call
   `data.Install(allUpgrades[0])` on each locked one — the game's own unlock primitive
   (`IsUnlocked == installedUpgrades.Count > 0`; unlocking disables the station's
   `enemyCollider` + `enemyTrackingSystem` and opens the platform). Done **after
   GO_LIVE** so the existing `ProgressionSync.CaptureUpgrade` postfix
   (`src/Sync/ProgressionSync.cs:89`) replicates each unlock to all clients for free —
   including the shop-stock parity call each machine already makes on unlock.
   The start-sequence cinematic still targets the generation-time start station (it
   already has FuelDispenser installed at generation) — unchanged, no softlock risk.
2. **Scatter the players — one station each, never shared.** No wire needed: station
   positions are seed-deterministic, so every machine computes the identical
   `slot → station` assignment by farthest-point sampling over the station list
   (seeded by run seed): the first player takes a station, and each subsequent player
   takes the station whose nearest already-assigned station is farthest away. This
   maximizes the minimum pairwise spawn distance **and guarantees distinct stations by
   construction** — a station is removed from the candidate pool once assigned, so two
   players can never start at the same shop. If a map somehow generates fewer stations
   than players (not observed; maps carry dozens), the surplus players fall back to the
   farthest open cells from every assigned station, logged as a warning. Ships spawn
   normally through the vanilla flow (start cinematic untouched); when control is
   restored (`ShipSync.ReleaseStartGate` + cinematic end), each client teleports its
   OWN ship to its assigned station via the existing
   `ShipSync.TeleportLocalShip(stationNetId)` (`src/Sync/ShipSync.cs:765`); puppets
   follow through normal ship sync. The assignment is logged host-side
   (`[BR] spawn slot N -> station #id at (x,y), nearest peer M units`) so the harness
   can assert both distinctness and separation.
   (Polish, deferred: skip the cinematic in BR and spawn scattered directly.)
3. **Coins**: vanilla already starts every run at 0 (shared `ResourceTank.Value`
   defaults to 0; nothing grants starting money — confirmed by decompile of
   `RunData.Initialize`/`ShipManager.FillEveryResourceExceptFuel`). BR adds only a
   defensive zeroing of the shared resource tanks at match start.
4. **Full shop stock**: each machine calls `RunData.AddAllItemsToShop()` at match
   start — every tier's modules purchasable everywhere from minute zero. Explicitly
   **NOT** `RunData.AllShopItemsAreFree` — prices stay normal; players buy with gold
   they earn. Price parity across machines already replicates
   (`ProgressionSync.cs:204`).
5. **Everyone flies the Gunner, and there is no class-selection screen.**
   - *Which ship*: `RunStarter.FindBattleRoyaleLoadout()` matches on identity —
     `displayName == "GUNNER"`, else asset name `Starter_Popper`, else the
     alphabetically first template so every machine still agrees. **Never by list
     index**: `LoadoutPool.loadouts` is a hand-ordered serialized list (real order
     `4,2,1,5,3,6`), so `loadouts[0]` being the Gunner today is luck, not a contract.
   - *No selector*: `RunStarter.LaunchRun` skips the `LoadoutSelector` scene in BR and
     calls `GameScene.GoToGameScene(RunArguments.NewRun(false))` directly — the game's
     own Continue/QuickLoad flows do the same thing, and the selector is only a producer
     of `RunArguments`.
   - *Where the ship is actually stamped in*: a postfix on `GameController.Awake`
     (`RunStarter.ForceBattleRoyaleLoadout`), **not** at launch time. The loadout assets
     live in the selector scene's bundle and are provably not resident while the lobby is
     open — the first implementation resolved at launch and every machine logged
     "assets are not loaded yet". `GameController.Awake` is the earliest point the pool
     is loaded (the Game scene references it via `LoadoutUnlocker`) and it runs before
     `BuildLevel` reads `startingLoadout`. Hooking there also covers a selector run and
     the game's own `Restart()`, so no launch route can put another ship in a BR match.
     The log line states the count it chose from: `chosen from 6 loadouts` means the
     whole pool was visible; a `1` there would mean only the fallback default had loaded.
6. **Clear pre-existing hazard cells** (host). The game has **no literal lava**; the
   damaging terrain on today's maps is biome `CellType`s with `contactDamage > 0`. At
   match start the host scans `Level.cellTypes` and converts any cell whose registered
   type has `contactDamage > 0` to empty (`Level.SetCell(i, 0)`), budget-paced through
   the existing terrain sync (`WorldSync.CaptureCellChanges → Flush`,
   `src/Sync/WorldSync.cs:171/390`, 2 500 cells/frame send budget). The ring is then
   the only lethal terrain in the match.
7. **Hide player locations** while `CurrentMode == BattleRoyale`:
   - `PlayerTracker.LateUpdate` short-circuits for players (no names, no edge arrows)
     (`src/UI/PlayerTracker.cs:35`). Care-package arrows (§6) are the one exception.
   - Scoreboard hides the distance and HP columns (kills/deaths stay)
     (`src/UI/Scoreboard.cs:153`).
   - Map exploration sharing and fog-diff merging disabled for the match
     (`ShareMapExploration` gate + `MapShareSync`).
8. **Seal the match** (host): snapshot `matchPlayers` = connected non-coordinator
   slots, `N = count`. Joins while a BR match is live are rejected with "MATCH IN
   PROGRESS" (joiner waits in the connecting screen). The rejoin window is disabled
   for this run; a disconnect is an immediate elimination (§7).

## 4. The lava ring

**Model.** Host-authoritative circle. **Center prefers open areas**: at match start the
host samples 64 candidate centers (seeded RNG) and scores each by the fraction of
`Empty (0)` cells in a sampling disc read directly from `Level.cellTypes` (cheap
host-side array scan); the most open candidate wins. The host is authoritative — center
+ radius ride `RingStateMsg`, so clients never recompute.

**The ring is sized to the PLAYABLE DISC, not the cell array.** `BorderGenerator` stamps
every cell further than `Width/2` from the grid centre as the **void biome**, so the
world is a disc inscribed in a square array. Sizing the start radius off the array's
farthest CORNER (the original implementation) put ~29% of the ring's entire travel
outside the world before it touched anything a player could stand on, and painted its
lava wall into the void along the way — field-reported 2026-07-27 as *"the ring is
starting from way out in the 3D world space"* (that match logged centre `(1282,955)`
`r=1654` on a disc of radius ~1000). Now:

- `MeasurePlayableArea` derives the disc empirically — the bounding box of every
  non-void cell in `Level.bioms` — rather than assuming the generator's radius.
- Candidate centres are drawn from within `CenterDriftFraction` (0.15) of the MAP's
  centre, and void cells no longer count as "open" (they are empty, so the old scoring
  rated the border as the most fightable ground on the map).
- `startRadius = mapRadius + |center − mapCenter|` — the smallest circle around the
  chosen centre that still contains the whole disc. Everyone starts inside (nobody
  burns at t=0) and no part of the schedule is spent closing through nothing.
- `PaintRing` skips void cells, so the wall exists only where the world does and those
  cells are not replicated as terrain diffs.

The match-start log states the resulting closure RATE outright (`each closing Nu over
Ns = X u/s`), because that number — not the four configs behind it — is what decides
whether the ring reads as pressure or as scenery.

**Paint cost (measured 2026-07-28, `tools/br-test.ps1 -Phases ring -ProfileRing`).** The
wall is written through `Level.SetCell`, so every cell is a terrain diff replicated to
every client — the one part of the ring that scales with the map. Two defects were found
and fixed, and they were not the same problem:

1. **The painting itself.** `PaintRing` scanned the boundary's bounding box testing every
   cell — O(radius²) work to paint O(radius) cells (3M distance tests to write 22k cells
   at r=868) — and the band width was however far the ring had moved since the last pass,
   so a slow frame widened the next band and made the next frame slower. Worst single pass
   **221ms**. Now it solves each row for the two x-spans inside the annulus, and the band
   is capped by a self-tuning budget (`BrRingPaintMs`, from the MEASURED cost per cell).
   Worst pass **3.2ms**.
2. **Everything downstream of the painting, which was 100x larger.** See below.

**⚠ The ring exposed a vanilla defect that has nothing to do with Battle Royale.**
Painting drove the coordinator from 120fps to **0.1fps**, with 9-second frames, while the
painting itself stayed under 50ms per ten seconds. `simprof` blamed
`LevelChangeBuffer.Update` — true and useless, since that method's whole body is
`CellsChanged?.Invoke(...)`. Timing its eight subscribers individually (the `cellfanout`
devcmd, added for this) named the real one:

```
GroundTilemapUpdater   12583.4ms  avg=1398ms/call  worst=4381ms   <- 94%
LightmapGenerator       1410.5ms  avg= 470ms/call
MapDrawer                 11.3ms
LevelSegmentComponent      1.5ms
NavigationManager          0.8ms
```

`GroundTilemapUpdater.OnCellsChanged` calls `Refresh` per changed cell, and each `Refresh`
issues four Unity Tilemap calls (`SetTile`, `SetTransformMatrix`, `SetTileFlags`,
`SetColor`) — ~0.6ms per cell, per tilemap layer. It is **pure presentation**, and a
headless coordinator renders none of it. `Patches/TerrainPresentationTrim.cs` skips it
outright on a coordinator and narrows it to VISIBLE cells on a player's machine (lossless:
`TilemapUpdater` already refreshes a cell via `UnityTilemapRenderer.CellBecameVisible`
when it scrolls into view, and both paths read the same `visibleCells` set).

Result on the same match: the host holds **119.5fps** through every closure, paint passes
go from 2-15 per 10s to **1192 per 10s**, throughput from ~1.5k to **57k cells/s**, and
`behind=` drops from 400-928 units to **0.0** — the visible wall now sits exactly on the
boundary that is burning you, which was a gameplay bug as much as a performance one.

Any bulk terrain change pays this: a large explosion, a terrain repair chunk, a rejoining
client's catch-up diff. BR was just the first thing to change enough cells to make it
fatal.

A stage is one **wait** (the zone sits still and is fought over) followed by one
**closure** (the boundary draws inward to the next radius). `RingStateMsg` is re-broadcast
at each stage boundary and periodically (~5s) for late HUD refresh.

**The wait and the closure are different at every stage.** A constant hold gives the
twelfth zone the same rhythm as the first, which is the one thing a battle royale must not
do, and it is what produced *"the closing ring is too slow… I want to feel like I'm
rushing to the centre"* (Omar, 2026-08-05). The schedule follows the Fortnite blueprint:
long safe windows early, collapsing to nothing late, so the back half of the match is
almost continuous movement.

Both curves are *evaluated* per stage rather than typed out as a table, so the shape holds
for any `BrRingStages` — a 4-zone test match tightens the same way a 12-zone real one
does, instead of a magic 12-row list silently stopping meaning anything. Only the ratios
in the curve constants matter; the whole ladder is then scaled to hit `BrMatchMinutes`
exactly.

**Timeline** at the defaults (12 zones, 20 minutes; t = time since go-live, host clock).
There is no separate grace period — zone 1's wait *is* the opening window, so the first
countdown on screen is the time until the ground actually moves:

| zone | wait | closure | radius (% of start) | closed by |
|---|---|---|---|---|
| 1 | 2:07 | 88s | 100 → 87 | 3:35 |
| 2 | 1:40 | 83s | 87 → 75 | 6:39 |
| 3 | 1:17 | 78s | 75 → 63 | 9:14 |
| 4 | 57s | 73s | 63 → 52 | 11:25 |
| 5 | 41s | 69s | 52 → 42 | 13:14 |
| 6 | 28s | 64s | 42 → 33 | 14:46 |
| 7 | 18s | 59s | 33 → 25 | 16:02 |
| 8 | 10s | 54s | 25 → 17 | 17:07 |
| 9 | 5s | 49s | 17 → 11 | 18:01 |
| 10 | 2s | 44s | 11 → 6 | 18:46 |
| 11 | 0s | 39s | 6 → 2 | 19:26 |
| 12 | 0s | 34s | 2 → 0 | 20:00 |

The radius follows a curve too (`ShrinkCurve`), not equal steps and no longer by halving.
Halving was right for six closures and wrong for twelve — it reaches a pinpoint zone by
stage 5 and leaves the rest of the match with nothing left to take. The exponent form
trims gently while the zone still encloses most of the world (nobody has flown anywhere
yet) and then accelerates: zone 11 → 12 gives up two thirds of what remains.

Every stage is logged individually at match start (`[BR] zone n/12: wait …s, close …s, r …
-> …`), because with a variable schedule no single averaged number describes the pacing.

The original 45-minute / 120-second schedule was reported as *"the lava is going way too
slow"* (2026-07-27): eight stages spread over 40 minutes, on a radius that was itself ~41%
too large, meant the wall was never anywhere near anyone. That was cut to 18 minutes, then
to a configurable 5-minute uniform hold — which reintroduced the same complaint from the
other direction, since a flat hold cannot tighten. Note these are **defaults** — a server
that has already written its `config.cfg` keeps the old values until that file is edited,
though it will now be *told* so on boot (§8b).

### The zone DRIFTS, and closes on a shop (2026-08-05)

A fixed centre makes the strongest play "fly to the middle in minute one and never move
again". Every later zone is then information you acted on twenty minutes ago and the ring
stops being a decision. So the zone walks as it shrinks, on a path that ends **on a shop**
(Omar: *"include the off-centre drift, this makes the game more exciting — just make sure
it's an area that's accessible, perhaps closing at one of the many shops"*).

A shop is the right anchor for exactly the reasons the mode already invested in: they are
open ground by construction, all 49 are unlocked and stocked at go-live (`OpenAllStations`),
each has had its surrounding hazards scrubbed (`ClearHazardsAroundStations`), and they are
landmarks players can name — so everyone can see where the match is going to end. Candidates
are filtered to those within `AnchorMaxOffsetFraction` (55%) of the map radius, then scored
by the same open-ground probe the opening centre uses. A shop out on the rim is a legal
anchor and a miserable arena: half its approach angles are void. If a world has no central
shop, the ring closes on its opening centre as before.

**The invariant: every zone is entirely inside the one before it.** Break it and ground a
player is standing on — well inside the safe zone, no warning circle near them — turns
lethal without notice. It holds iff `|C(k+1) − C(k)| ≤ R(k) − R(k+1)`: the centre may never
move further than the radius it gives up.

`BuildRingPath` gets that for free rather than by tuning. Each centre sits as far along
*start → anchor* as the radius is along *R₀ → 0*, so a step moves
`|anchor − start| × (R(k) − R(k+1)) / R₀`, which is within budget whenever the anchor is
inside the opening circle — true by construction — and lands exactly on the anchor at the
final closure, where the radius reaches zero. Sideways jitter (`DriftJitter`, 18% of the
*current* radius, so the path wanders while there is room to rotate and straightens once
there is not) rides on top and is clamped against the same budget, so no amount of wander
can break containment. `VerifyContainment` re-checks all of it at match start and logs an
error if it ever fails; it never should, and the failure it guards against would be blamed
on the damage code for a week.

On a map of radius 1000 that is ~100 units of lateral travel per closure early, tapering to
~10 late. The per-zone `drift` figure is in the match-start log next to the radius.

**Everything reads the LIVE boundary, not the snapshot.** `RingStateMsg` arrives about every
five seconds; drawing it raw made the wall step, and a drifting zone would *lurch*. So the
message carries the closure's target centre as well as its target radius (protocol 20), and
each machine interpolates from wherever the snapshot put it toward that target over the
seconds the snapshot said remain — `BattleRoyale.RingCenter` / `.RingRadius` /
`.RingTargetCenter`. It is self-correcting (every broadcast re-anchors on host truth, so
error cannot accumulate) and it fails safe (if broadcasts stop, the boundary settles on the
target and waits — it never overshoots into ground the host still considers safe). The lava
mesh, both map overlays, the burn check and the HUD all read those accessors; nothing reads
`Ring.CenterX` directly any more.

Two consequences worth knowing:

- The **amber next-zone circle is drawn on its own centre**, offset from the current one.
  That gap is the whole message — it is what tells a player which way to cross, and drawing
  both circles concentrically would erase it.
- **Care packages scatter around the NEXT zone's centre**, not the current one. With a
  drifting ring those are different places, and a crate on the old centre can be outside the
  very closure it was placed to survive.

### The zone is RENDERED, not built (2026-07-28, Omar's call)

The ring used to be real painted terrain — the whole playable disc converted through
`Level.SetCell`, ~2.9 million cells a match. It was fixed twice for performance and was
still the most expensive thing in the mode, because every one of those cells is an event
to eight subscribers plus a replicated terrain diff to every client. **Fortnite's storm
and PUBG's blue zone are not level geometry either**: they are a rendered surface plus a
distance check. This is now that.

- **`UI/RingLavaVisual.cs`** draws it: a procedurally generated ANNULUS mesh — real
  geometry with a transparent hole exactly on the safe radius, so the lava edge sits
  precisely where the damage starts at any zoom (an alpha-cutout quad cannot keep the hole
  aligned as the ring shrinks). The molten texture is generated at runtime from tiling
  Perlin noise quantised into five hard bands — quantised because the game is 8-bit and a
  smooth gradient would read as foreign — then scrolled and pulsed. It rides
  `Sprites/Default`, because a mod cannot compile a shader (that is a build-time step).
  The zone visibly **darkens and thickens as it gets deadlier**: the same damage
  multiplier drives the colour.
- **It has NO COLLIDER and deals NO contact damage.** Being caught in it hurts only
  through the radius check in `BattleRoyale.LocalTick`. Omar, 2026-07-28: *"players can
  still go through the area... it's how players can prevent being trapped."* A closing
  ring must never wall someone in.
- **Pacing**: `BrZoneKillSeconds` (60) is seconds to die from FULL health in the FIRST
  zone — a long crossing is survivable at full health and nothing else — and
  `BrZoneDamageStageScale` (0.75) multiplies the rate per completed shrink, so the 8th
  ring kills in about an eighth of the time. Early zones are an escape route; late ones
  are not. Damage scales with MAX health, so an upgraded hull buys proportionally more
  time rather than trivialising the zone.
- The map's own hazards are **no longer cleared** at go-live. They only needed clearing so
  the painted ring could be the one lethal ground; wiping ~100k cells was itself a
  terrain-diff burst at the worst possible moment.

`tools/br-test.ps1 -Phases ring` asserts the ring paints **zero** cells, so the terrain
version cannot come back by accident.

**Historical — the terrain version, kept because the measurements are the argument:**

1. **The lava wall** (visible, physical): a ~32-cell-thick annulus of a damaging
   `CellType` at the current boundary, painted by the host via `Level.SetCell(index,
   ringCellId)` with `changeSource 0` — the existing terrain pipeline captures, batches
   (`CellDiffMsg`), and replicates it exactly like vanilla fog conversions; clients
   apply through the same code path. Cell contact damage is **victim-local** (per
   `docs/terrain.md`) — each ship burns on its own machine; zero new damage sync.
   - **Ring material** resolved at runtime: the highest-`contactDamage` `CellType` in
     the registry, logged at match start as `[BR] ring material=<id> damage=<n>`. If no
     damaging type exists, fall back to any solid type — the wall is then a physical
     barrier and layer 2 provides the lethality.
   - **Why a wall and not a full flood**: every painted cell that differs from the
     generation baseline lives in the terrain ledger forever
     (`WorldSync.RecordLedger`); flooding the outside approaches 4M entries (hundreds
     of MB across peers + digest churn). The wall caps ledger growth at ~200k cells at
     the largest annulus, shrinking after. BR disables rejoin, so the ledger's
     catch-up role is idle; the 30s full-array digest still verifies all peers'
     terrain stays identical while the ring paints.
2. **The kill zone** (authoritative backstop): any ship outside the current safe
   radius takes rapid periodic burn damage applied by its **own client** (ship HP is
   owner-authoritative — the same authority model as all ship damage). This makes the
   outside lethal even beyond the wall and covers players the front has passed.
   Radius/center come from the last `RingStateMsg`.

**HUD**: top-center line near the Toast area: match countdown `45:00 → 0:00`, ring
state (`RING: CLOSING — NEXT STAGE 2:31 — SAFE RADIUS 840`), alive count. Rendered
from `RingStateMsg` + `ClockSync`-aligned time.

## 5. Combat rules

- **Projectiles can HIT other players** (`src/Patches/BattleRoyalePvP.cs`). PUNK is
  co-op, so every player ship shares one faction, and `Projectile.FixedUpdate` asks
  `Owner.IsFriendsWith(hitUnit)` before registering a hit — if true it calls
  `MoveForward()` instead of `OnObjectHit()`. Direct-fire projectiles therefore flew
  straight THROUGH another player without ever reaching a collision, let alone the
  damage routing below: *"none of my attacks are hitting the other player"*
  (2026-07-27). Hitscan beams and explosions have no such filter, which is why they DID
  land and the symptom looked weapon-dependent. A postfix makes `IsFriendsWith` false
  in a live BR match when both units are player ships and are not the same ship —
  enemy AI is untouched (an `AIAgent`'s unit is never a `Ship`) and self-hits stay
  friendly. **Known gap:** a player's MINIONS still pass through other players; their
  projectiles' `Owner` is the minion Unit, so "which player owns this unit" would have
  to be resolved first to avoid making their fire hostile to its own owner too.
- **PvP always on**: the FriendlyFire gate (`ProjectileSync.FriendlyFireBlocked`,
  `src/Sync/ProjectileSync.cs:1302`, and `FriendlyExplosionBlocked`, `:1316`) is
  forced open when `CurrentMode == BattleRoyale`; the lobby FF toggle is ignored (UI
  greys it out for BR).
- **PvP damage scale-down** — one chokepoint: ALL player→player damage is routed
  through `DamageSync.SendDamageRequest` (`src/Sync/DamageSync.cs:444`) because ships
  are always remote-simulated by their owner (there is no local-application path for
  another player's ship). Multiply `amount` by `PvPDamageScale` (default **0.25**)
  when `!isEntity` before it goes on the wire; the victim's machine applies it
  verbatim through the full vanilla pipeline (shields, i-frames) exactly once. AoE is
  covered — explosion damage flows through the same path. No one-shots from
  late-game weapons.
- **Damage to enemies effectively ×2** — implemented as **enemy HP ×0.5**, not a
  per-hit multiplier: set `EnemyHpMult = 0.5` for BR in `StartRun`
  (`NetSession.cs:780`) and relax the scale-up-only guards (`mult <= 1.0001f`) in
  `UnitStatus.ApplyEnemyHpScale`/`RevertEnemyHpScale` (`src/Sync/UnitStatus.cs:66,92`).
  Same time-to-kill as doubled damage, but one already-replicated hook
  (`StartRunMsg.EnemyHpMult`) applied once per enemy — versus double-hooking both the
  routed (`DamageSync.cs:444`) and local (`:157`) damage paths and interacting per-hit
  with resistances. Enemy→player damage unchanged. (The per-player co-op HP scaling is
  inherently replaced — BR sets the multiplier absolutely.)

## 5b. Loot is CONTESTED, not instanced

Standard co-op instances loot: every machine drops its own copy and a player too far to
reach the pile is granted an equivalent straight into their (never-synced) Vault, so a
kill rewards the whole party. BR inverts that — the drop **is** the contest. Omar,
2026-07-27: *"gold and resources, while they should be destroyed for everyone, should
only be granting one item visible to all clients, but only one may pick it up."*

Implemented in `src/Modes/BattleRoyaleLoot.cs`:

- **The pile stays a local copy on every machine.** Replicating each coin as a real
  networked entity would drag hundreds of short-lived pickups into the
  authority/streaming pool for an object whose only interesting state is *"has someone
  taken it yet"*. That single bit is what travels instead.
- **Identity without a shared reference.** A drop is named by `(Group, Ordinal)`:
  `Group` = the dying entity's `netId`, or `-(cellIndex + 1)` for a destroyed terrain
  cell; `Ordinal` = the item's position in that drop's roll. Both halves were already
  deterministic (the death roll runs inside `LootDiag.DropLootGuard`'s seeded scope;
  cell drops are seeded from the cell position by vanilla) and everything spawns through
  the one funnel `LootFactory.Create` — so ordinal 3 of group #812 is the same item on
  every machine.
- **Collecting is a request.** The pickup is intercepted at the last moment (the coin's
  magnet reaching the ship, or the interact-pickup's fly-in), a `LootClaimMsg` goes to
  the host, and the pile visibly HOLDS until the verdict. First claim the host sees
  wins; `LootClaimedMsg` goes to everyone and the losers destroy their copy. The
  winner's own pickup then completes through the **untouched vanilla path** — which is
  the point of gating rather than granting: coins, ingredients, consumables and modules
  each keep their own collection behaviour and none needs a bespoke grant routine.
  Claims are idempotent, so a lost verdict heals on retry without ever awarding twice.
- **No distant grant** (`LootDiag.GrantRemoteLoot` returns immediately in BR) and **no
  far-drop suppression** (the pile always spawns where the death happened, so a machine
  has something to destroy when someone else claims it).
- **Remote puppets cannot collect.** Another player's ship is a real `Ship` with a real
  `LootCollector` on this machine, and vanilla's magnet was happy to let a puppet hoover
  up a pile and charge its snapshot-driven tank — consuming loot nobody claimed.

The cost is one round trip of hold before the item is yours; predicting the win and
rolling it back would mean un-granting a module or subtracting gold a player already
saw. **Known gap:** a peer for whom the dying entity was never resident never runs the
drop chain, so that pile does not exist on their machine and will not appear if they fly
over later — contested loot is correct exactly where two players are close enough to
contest it.

## 6. Care packages

Every **10 minutes** (t=10/20/30/40), host-driven:

1. Host picks a random open location **inside the current safe radius** (same
   empty-cell disc scoring used for the ring center), spawns a destructible prop
   entity there through the existing runtime-spawn path (runtime netId registration —
   the same machinery MinionSync/reconciliation already uses for mid-run spawns), and
   broadcasts `CarePackageMsg { NetId, X, Y }` + an announce toast
   ("SUPPLY DROP INBOUND").
2. Every alive player gets a **screen-edge arrow** pointing at the package (reusing
   the PlayerTracker arrow rendering, `src/UI/PlayerTracker.cs:99` — packages are the
   one thing that gets an arrow in BR; players never do). Arrow clears when the
   package dies.
3. **Destroyer takes all**: destruction credit rides the existing kill pipeline
   (`EntityKilledMsg.KillerSlot`). The reward materializes ONLY on the destroyer's
   machine — precisely the loot model's existing "killer drops locally, everyone else
   suppressed" latch (`DropLootGuard` / `EnemySync.TryMarkLootDropped`,
   `src/Patches/LootDiag.cs:343`). Reward: a large exact-value coin pile (existing
   whole-number coin materialization, `LootDiag.GrantRemoteLoot` `:173-215`) plus
   module/weapon pickups drawn from the shop's module distribution (`RunData`
   `ShopUpgradeData` distributions).
4. A package still standing when the ring front reaches it is destroyed by the host
   with no credit (no reward spawns).

## 6b. Ship status bars (health + fuel above every other ship)

Players need to read another ship's condition on sight — is that one nearly dead, is it
about to be stranded? Enemies already advertise their health this way; player ships do
not.

**What**: a small two-bar widget floating just above every **remote** player ship
(allies in Standard, opponents in BR — the local player already has the full HUD).
Stacked, health on top:

```
   ▂▂▂▂▂▂▂▂▂▂   red   = health
   ▂▂▂▂▂▂▂▂▂▂   blue  = fuel
```

**Built from the vanilla enemy healthbar** (revised 2026-07-27, Omar's call). The bars
are real `ResourceBar` instances borrowed from the game's own
`HealthbarManager → HealthbarWidget → resourceBarPrefab` chain, stacked health-over-fuel
the way `HealthbarWidget.GenerateResourceBars` stacks shield-over-health, at
`BarScale` 0.6 — so a player ship advertises itself in exactly the language every
hostile on the map already does: same segments, same shader, same pop animation. Fuel is
re-tinted blue by overriding `_ResourceColor` / `_ResourceColorEmpty` on the instanced
row material (the colour otherwise comes from the `Resource` asset, which is not ours to
edit), reapplied each frame because the vanilla bar re-instantiates its rows whenever
capacity changes.

**Trade accepted:** `ResourceBar` draws one segment per unit of **capacity**, so an
upgraded ship grows a physically longer bar rather than a fuller one — the opposite of
the fixed-size widget this replaced. `MaxResourcePerRow` (16) wraps it instead of letting
it run off across the screen.

**Not drawn over the map.** The widget is parented to `HealthbarManager`'s transform and
positioned in world space the way `HealthbarWidget.UpdateTransform` does, so it sits in
the same layer, depth and camera as the enemy bars — and it hides outright while
`ShipMenuToggler.isOpen`, because a status bar punching through the full-screen map was
the reported symptom.

**Data**: both values come from the ship's `Unit` tanks — health from
`DamagableResource.Tank`, fuel from the tank whose `Resource` is the fuel resource
(resolved the way vanilla does it, by name match through `ResourceRegistry`, cached
once). For a remote ship these tanks are already kept current by ship-state sync, so the
bars need no new network traffic.

**Visibility rules**:
- Drawn only for ships currently on screen and alive — it is a world-space widget, so it
  reveals nothing a player cannot already see. **This is why it does not conflict with
  BR's hidden locations** (§3.7): trackers and arrows that point at off-screen players
  stay disabled; once a ship is in view, its status is fair information.
- Hidden for the local player's own ship, dead ships, and the coordinator (which has no
  ship).
- Suppressed while spectating? No — a dead spectator sees them normally.

**Scope**: both game modes. It is useful in co-op ("my teammate is at 20%") and
essential in BR. Config gate `ShipStatusBars` (default on) alongside the existing
tracker toggles in `NetConfig`.

**Implementation home**: `src/UI/ShipStatusBars.cs`, driven from the same
LateUpdate-over-remote-ships loop shape `PlayerTracker` already uses
(`src/UI/PlayerTracker.cs:35`), reusing `ShipSync.ShipsBySlot` for the ship set and
`UiTheme` for sprites/colors.

## 7. Elimination, placement, win

Host tracks the sealed match roster (§3.8). Eliminations:

- **Death**: detected host-side with the same resolve loop as today's wipe check
  (`ShipSync.IsSlotDead` / `ship.IsDead`; `CheckPartyWipe`, `NetSession.cs:2100`) —
  forked into a BR last-alive check that counts **alive** players (2s debounce kept).
- **Disconnect**: elimination at the moment of disconnect. No rejoin window in BR; the
  ship despawns via the existing disconnect path. Placement is recorded server-side
  regardless.

On each elimination the host assigns the next placement from the bottom (`N`, `N-1`, …)
and broadcasts `PlacementMsg` → all clients toast "<NAME> ELIMINATED — <k> REMAIN"; the
eliminated player's client shows a game-over-style screen (hooked at the existing
`GameOverScreen.OnGameOver` postfix, `src/Patches/GuardPatches.cs:87`):

```
        YOU PLACED  #4  OF 7
   [ SPECTATE ]  [ BACK TO LOBBY ]  [ MAIN MENU ]
```

- **SPECTATE**: the existing `SpectatorCam` already auto-follows alive players when the
  local ship is dead (`src/UI/SpectatorCam.cs:24`; Q/E cycles). Dead players seeing
  alive players is intended; alive players never see each other.
- **BACK TO LOBBY** / **MAIN MENU**: the existing game-over buttons — lobby keeps the
  session for the next match; menu disconnects cleanly (both shipped in v0.1.172/173).

**Win**: the moment alive count reaches **1** (2s debounce), the match ends: the
winner's client shows `VICTORY — #1 OF N`; everyone receives a final `PlacementMsg`;
then the standard `RunEnded → EndRunToLobby` flow (dedicated server: fresh lobby +
next-world pre-generation, already shipped). Simultaneous final deaths: later death
timestamp wins; ties broken by kills (host adjudicates). Alive count 0 (mutual kill /
mass disconnect) ends the match with the last-eliminated as winner — the "last person
to die" rule for the degenerate case.

**45:00 expiry**: the ring reaches radius 0 and the kill zone covers the map — the
match resolves through eliminations within seconds; the ring IS the timeout, no
separate path.

## 8. Config surface (new `[Session]` entries)

| Key | Default | Meaning |
|---|---|---|
| `EnableGameModes` | `false` | **Master feature flag.** Off = every run is Standard, the GAME MODE row is hidden, and a server ignores `GameMode`. Joining someone else's BR server still works — the host owns its runs' ruleset. |
| `GameMode` | `Standard` | `Standard` \| `BattleRoyale` (dedicated server; restart-applied; requires `EnableGameModes`. Self-host uses the GAME SETTINGS row) |
| `BrMatchMinutes` | `20` | total match length; the final ring reaches 0 exactly here. The per-zone wait and closure times are derived from it on the curve above, so changing it stretches or compresses the pacing without flattening it |
| `BrRingStages` | `12` | how many closures the ring makes. The curve is spread across whatever count is set, so short test matches keep the shape |
| `BrZoneKillSeconds` | `60` | seconds to die from FULL health in the FIRST zone. The zone is not solid — you can always fly through — so this is the price of a crossing |
| `BrZoneDamageStageScale` | `0.75` | damage is multiplied by (1 + stage × this) per completed shrink, so late rings are lethal and early ones are an escape route |
| `ShowZoneVisual` | `true` | draw the molten zone (UI). Off leaves the damage untouched and simply hides it |
| `BrCarePackageMinutes` | `4` | care-package drop interval (0 = disabled; was 10, which fit one drop in a match) |
| `PvPDamageScale` | `0.25` | ship→ship damage multiplier in BR |
| `BrEnemyHpScale` | `0.5` | enemy HP multiplier in BR (0.5 ≈ double damage) |
| `BrMinPlayers` | `2` | minimum connected players for START in BR (1 allowed with a logged warning, for testing) |
| `ShipStatusBars` | `true` | draw health/fuel bars above other players' ships (BOTH modes; see 6b) |

`BrRingStartMinutes`, `BrRingHoldMinutes` and `BrRingCloseSeconds` are **gone**, along with
their `BR_RING_START_MINUTES` / `BR_RING_CLOSE_SECONDS` panel variables. Under a derived
schedule there is nothing left for them to mean. A server whose `config.cfg` still has them
is told so on the next boot and the lines are deleted (§8b).

### 8b. The boot-time config report

A config file outlives the code that reads it. A dedicated server writes `config.cfg` once
and keeps it forever, so a retired key sits there looking authoritative — and the operator
who set it has every reason to believe it still works. The ring schedule was tuned three
times through knobs a later redesign stopped reading, and nothing anywhere said so.

`Core/ConfigAudit.cs` runs immediately after `NetConfig.Init` and logs, under `[Config]`:

- **the state** — every setting that differs from its default, one per line, so a run's
  behaviour is explained in the log it already produces. Values of keys whose name looks
  like a credential print as `(set)`; these logs get uploaded by `uploadlogs`.
- **`RETIRED`** — a key we deliberately removed, named with the reason it went and what
  replaced it. These are **deleted from `config.cfg`**, because we know for certain they do
  nothing and leaving them is what created the problem.
- **`WRONG SECTION`** — the key is real but filed under the wrong `[Section]`, so the
  setting exists twice: a dead copy holding what someone meant and a live one quietly
  holding something else. The first run of this audit found `Diag.SummaryHeal = true`
  sitting above `Sync.SummaryHeal = false`.
- **`UNKNOWN`** — anything else, with a nearest-key suggestion. These are **kept**: an
  unrecognised key is usually a typo, and deleting it would throw away the value the
  operator wanted along with the evidence of the mistake. The warning repeats each boot,
  which is the point.

The mechanism is BepInEx's own. Its `ConfigFile` reads the whole file up front and holds
every key nobody claimed in a private `OrphanedEntries` dictionary, deleting each as a
plugin `Bind`s it — so after `Init`, what remains is exactly the set of settings on disk
that mean nothing. No parsing of our own, and no chance of the two disagreeing. The whole
audit is wrapped in a catch: a config report must never be why the mod fails to load.

**When you remove a config key, add a line to `ConfigAudit.Retired`.** It costs one line
and turns a silent behaviour change into a sentence the operator reads on the next boot.

## 9. Out of scope (candidate follow-ups)

(Section 6b's ship status bars are NOT deferred and are not BR-only — they ship in both
modes; they are documented here because BR motivated them.)

Teams/duos; kill feed beyond the elimination toasts; BR-specific meta progression;
cinematic skip with direct scattered spawns; ring-center drift between stages.

## 10. Verification plan

### `tools/br-test.ps1` — the automated match

```powershell
tools\br-test.ps1                       # everything
tools\br-test.ps1 -Phases pvp,bars      # just the probes you are iterating on
```

Runs a compressed match (6 min, 4 stages, ring at 1:00) against a local coordinator and
two headless bots, then asserts from the logs. It has two halves.

**Lifecycle** (`-Phases lifecycle`) — the timeline, from the coordinator's log: match
start, ring material, station unlocks *broadcast and applied*, distinct spawn stations
with no cross-machine disagreement, spawn clear on every machine, stage announcements,
care packages, placements, winner, winner self-destruct.

**Behaviour probes** — scripted immediately after go-live, because none of them are
observable in a free-running match. BR scatters spawns ~1600 units apart on purpose, so
the block opens by collapsing that distance with `tpplayer`; then:

| `-Phases` | Drives | Asserts |
|---|---|---|
| `ring` | — | Start radius ≤ 1.2 × the playable disc radius (catches a regression back to array-corner sizing); the closure rate is stated; worst single paint pass < 50 ms; **worst host frame < 250 ms** (the check that caught the tilemap collapse — asserted on the WORST sample, never first-vs-last, because the host recovers the instant the ring stops painting and a start/end comparison scores four minutes at 0.2fps as healthy) |
| `ring -ProfileRing` | `simprof` + `cellfanout` mid-closure | Ranked attribution of the frame. `simprof` names the per-frame method; `cellfanout` names which of `LevelChangeBuffer`'s eight subscribers is responsible, which `simprof` structurally cannot |
| `sync` | bot1 `orbit`, bot0 `shipsmooth` | Drawn-pose CV ≤ 1.5 and stall% ≤ 15 on the observed ship; `[ShipLatency]` saturation |
| `pvp` | bot0 `fire … player <slot>` | ≥1 routed PvP hit **applied** on the victim, and its hp actually moved. This is the whole chain in one line — a projectile that collided, a hit routed to the owner, damage applied through the vanilla pipeline |
| `bars` | `shipbars` before/after the burst | The observer's copy of the remote ship's health *changed* while it was being shot, and its capacity is non-zero — the bars bind these tanks, so a stale puppet tank is a full bar on a dying ship |
| `loot` | both bots mining the same ground | No `(group, ordinal)` awarded to two different players; zero distant grants (`[Loot] materialized` must be 0 in BR) |

Two devcmds exist for this harness and are useful by hand:

- `fire <secs> player <slot>` — hold the trigger and track another PLAYER's ship. Ships
  are keyed by slot and have no netId, so `fire … at <netId>` can never aim at one; this
  is the only way to exercise PvP without a second human.
- `shipbars` — print health/fuel for the local ship and every remote one, read through
  the same tanks `UI/ShipStatusBars` binds. The bars themselves are UI and a bot runs
  `-nographics`, so the data is the testable half — and the half that actually breaks.
- `cellfanout on|off` — per-handler timing of `LevelChangeBuffer.CellsChanged`. `simprof`
  can only ever blame the publisher of an event; this is the level below it. Written after
  three consecutive wrong diagnoses (the burn simulation, `LevelSegmentComponent`, then
  `MapDrawer`) that all came from reading the source instead of measuring it.

Note: god mode does **not** shield a ship from a routed damage request
(`ApplyDamageRequest` runs with `_applyingRemote` set, which the god gate sits behind),
which is what lets the probes keep both bots alive against the ring while PvP still
lands. If that ever changes, the `pvp` probe has to ungod its victim first.

### Remaining manual / follow-up coverage

1. **Harness** (extend the `pregen-test.ps1` family): coordinator with
   `GameMode = BattleRoyale` (+ shortened timers: `BrMatchMinutes 6`,
   `BrRingStages 4`, `BrCarePackageMinutes 2`) + 3 bots → assert from logs:
   all stations unlocked post-golive; scattered spawns at **distinct** stations (assert
   no station id repeats across slots) with pairwise distance above threshold;
   `[BR] ring material` resolved; ring center scored open; stage
   announcements on schedule; terrain diffs flowing (`cells/s` counter) with digest
   clean; kill-zone damage ticking on a bot parked outside the radius; care package
   spawned + reward granted only to the killing bot; placements broadcast in
   elimination order; match ends at last-alive; server returns to lobby and
   pre-generates the next world.
2. **Determinism**: the standard two-round pregen harness in BR mode — zero
   `GENERATION MISMATCH`; terrain digest stays clean while the ring paints
   (host-authored diffs verify by construction).
3. **Status bars** (6b): the DATA is covered by the `bars` probe above; the WIDGET still
   needs eyes, because it is a world-space UI object invisible to a headless bot.
   Confirm: it uses the vanilla segmented art, health over blue fuel, small enough not to
   dominate the ship, and that it never appears for off-screen or dead ships **or over
   the map screen**.
4. **Live**: a human match on the dedicated server — feel checks: spawn separation,
   ring pressure pacing, PvP time-to-kill at `PvPDamageScale 0.25`, care-package
   contest moments, placement/spectate flow, and the lobby → next-match loop.
