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
| Loadout | player's choice | **forced standard loadout** |
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
5. **Forced standard loadout**: `RunStarter.InjectSeed.Prefix`
   (`src/Core/RunStarter.cs:59-69`) overrides `RunArguments.startingLoadout =
   LoadoutPool.loadouts[0]` when the mode is BR — one line, runs identically on every
   machine, skips nothing else in the flow. (`loadouts[0]` is the canonical standard
   loadout — the same one dev auto-pick selects; loadout assets have no stable name in
   the DLL, so the index, not a name string, is authoritative.)
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
host samples 64 candidate centers (seeded RNG) within the middle 50% of the map and
scores each by the fraction of `Empty (0)` cells in a sampling disc read directly from
`Level.cellTypes` (cheap host-side array scan); the most open candidate wins. The host
is authoritative — center + radius ride `RingStateMsg`, so clients never recompute.
Radius starts large enough to cover the whole map from that center (distance to the
farthest map corner) and reaches **0 at t=45:00**, collapsing on the chosen center.

**Timeline** (t = time since go-live, host clock):

| t | Event |
|---|---|
| 5:00 | `AnnounceMsg` "THE LAVA RING IS CLOSING" — stage 1 begins |
| 10:00 → 40:00 (every 5:00) | announce "THE RING IS CLOSING — SAFE ZONE SHRINKING" — stages 2–7 |
| 10/20/30/40:00 | care package drops (§6) |
| 45:00 | stage 8 completes: radius 0, whole map lethal |

Each stage advances the boundary inward by ⅛ of the start radius across its 5 minutes —
a smooth creep (~0.6 cells/s of front advance) rather than a teleporting wall, because
painted terrain cannot un-burn; each 5-minute toast marks a stage. `RingStateMsg` is
re-broadcast at each stage boundary and periodically (~10s) for late HUD refresh.

**Physical form — two layers:**

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

**Fixed size, normalized fill.** This is the one place we deliberately do NOT reuse the
vanilla widget: `HealthbarOwner`/`HealthbarWidget` build **segmented** bars through
`ResourceBar` rows at a constant 20 px per resource unit, so a ship that has upgraded
its health draws a physically longer (and eventually wrapped, multi-row) bar. Instead
the mod draws its own fixed-width bar and fills it by **fraction** (`tank.Value /
tank.Capacity`), so a fully-upgraded ship and a starter ship show the same size widget —
only the fill differs. Width/height are tuned to match a grunt's bar so it reads as part
of the game's visual language, and the widget is scale-stable regardless of upgrades.

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
| `BrMatchMinutes` | `45` | total match length; ring reaches 0 at this time |
| `BrRingStartMinutes` | `5` | first-shrink announcement time |
| `BrRingStages` | `8` | discrete shrink stages |
| `BrCarePackageMinutes` | `10` | care-package drop interval (0 = disabled) |
| `PvPDamageScale` | `0.25` | ship→ship damage multiplier in BR |
| `BrEnemyHpScale` | `0.5` | enemy HP multiplier in BR (0.5 ≈ double damage) |
| `BrMinPlayers` | `2` | minimum connected players for START in BR (1 allowed with a logged warning, for testing) |
| `ShipStatusBars` | `true` | draw health/fuel bars above other players' ships (BOTH modes; see 6b) |

## 9. Out of scope (candidate follow-ups)

(Section 6b's ship status bars are NOT deferred and are not BR-only — they ship in both
modes; they are documented here because BR motivated them.)

Teams/duos; kill feed beyond the elimination toasts; BR-specific meta progression;
cinematic skip with direct scattered spawns; ring-center drift between stages.

## 10. Verification plan

1. **Harness** (extend the `pregen-test.ps1` family): coordinator with
   `GameMode = BattleRoyale` (+ shortened timers: `BrMatchMinutes 6`,
   `BrRingStartMinutes 1`, `BrCarePackageMinutes 2`) + 3 bots → assert from logs:
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
3. **Status bars** (6b): verified visually — they are a world-space widget, invisible to
   headless bots. Confirm identical widget size between a starter ship and a
   health/fuel-upgraded one (only the fill fraction differs), correct red/blue mapping,
   and that they never appear for off-screen or dead ships.
4. **Live**: a human match on the dedicated server — feel checks: spawn separation,
   ring pressure pacing, PvP time-to-kill at `PvPDamageScale 0.25`, care-package
   contest moments, placement/spectate flow, and the lobby → next-match loop.
