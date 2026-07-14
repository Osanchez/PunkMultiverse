# Environment

Non-entity systems that shape the play space. Mostly deterministic or cosmetic — listed
so nothing is silently assumed synced.

## Fog of war / map reveal

```
FogManager / FogSource / fog render passes (BlurFogMaskPass, RenderFogPass, …)
MapIconManager (+ Memento)  — map icons; SetIconToOverdrawn = permanent discovery
MapIcon / StationMapIcon / InstrumentMapIcon
```

| Aspect | Status | Mechanism |
|---|---|---|
| Fog reveal (exploration) | **EVENT** | mod `FogSync` (+ `FogHostAuthority` patches); `ShareMapExploration` config |
| Permanent map discoveries | **EVENT** | `MapDiscoveredMsg` on `SetIconToOverdrawn` (ProgressionSync) with catch-up ledger |
| Scanner area reveals | **EVENT** | `ScannerUsedMsg` (see interactables.md) |

## Lighting

```
LightSource / LightSensor / LightShapeBuilder / BlinkingLight / LightBasedAnimation
StationLightManager / StationLightSource — lights that turn on when a station unlocks
LightmapGenerator(+Jobs) — baked from terrain at gen time
```

| Aspect | Status | Notes |
|---|---|---|
| Baked lightmap | **DET** | derived from (synced) terrain |
| Station lights | **EVENT**-derived | driven by `Data.UpgradeInstalled` — correct on every machine since upgrade events replicate |
| `IsInLightCondition` (AI reads light!) | **LOCAL** | enemy AI behavior can branch on light — owner-side only, consistent because one owner simulates |

## Hazards & ambience

| System | Status | Notes |
|---|---|---|
| `Hazard` (contact damage volumes) | **LOCAL** victim-side | damage applies on the toucher's machine — matches the victim-side damage rule |
| `StationDistressSignalUpdater` | **EVENT**-derived | `emitDistressWhenLocked` — reads station Data; consistent via upgrade sync |
| `AmbientSoundManager`, `BackgroundGenerator`(+Job), `BackgroundTilemapUpdater`, parallax | **DET/LOCAL** | cosmetic, seed-derived |
| `EnemyTrackingCamera` / ProCamera2D | **LOCAL** | per-player camera |
| Fast travel (`FastTravelManager`) | **derived** | destination unlock state syncs via station upgrades; the travel itself is a local ship teleport that streams out via ship snapshots. `Station.Data.isFastTravelDestination` + `PlayTeleportArrivalSequence` handle arrival visuals. **GAP?** — verify the arriving ship's teleport distance doesn't fight ship-snapshot smoothing on remote machines (large teleports should hard-snap puppet ships — HardSnapDistance covers it). |

## Time

`TimeManager` + `TimeScaleModifierSetup` (hit-pause/slow-mo effects on damage).
**LOCAL by design** — each machine's cosmetic time effects run independently; the mod's
`PausePolicy` governs pause behavior in net play. Snapshot timing uses unscaled time, so
local slow-mo doesn't distort sync.
