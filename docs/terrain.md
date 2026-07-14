# Terrain (the cell grid)

## Anatomy — cells are NOT entities

Terrain is its own grid system, parallel to the entity world:

```
Cell (plain class, one per grid coordinate)
  height, luminosity, variant
  cellType / backgroundType / foregroundType : CellType
  position (world grid), positionInSegment
  events: Destroyed(pos, CellType), ForegroundDestroyed(pos)

CellType : SerializedScriptableObject (the "material")
  id (byte; 0 = Empty), damageConditions, contactDamage + contactPushbackForce,
  colliderType, isWalkable, blocksEnemyPlacement, hasBackground/hasForeground,
  map/minimap textures, impact/destroy particles, ShakeSettings

Dynamic cell behaviours (per-type, applied to cells at runtime):
  DamageCellBehaviour(+Target)   — cells that hurt on contact
  BurnCellBehaviour(+Target)     — flammable cells; CellBurningManager spreads fire
  DragCellBehaviour(+Target)     — movement-slowing cells (liquids?)
  CellRegrowBehaviour / CellRegrower / CellRegrowJob — regrowing terrain
  CellShakeAnimation / CellAnimationClip — impact wobble

Batching/render: MergedCellData / MergedCellsGenerator / MergedCellsRegistry,
LevelChangeBuffer (accumulates cell edits), LevelSegmentComponent(Manager) — the
streaming unit the whole authority system keys on.
```

Generation: `LevelGenerator` + biome pipeline (`BiomeMapGenerator`, `HeightmapGenerator`,
`DungeonGenerator`, `SubBiomGenerator`, `BorderGenerator`, `OutlineGenerator`, …) — all
seed-deterministic.

## Sync status

| Aspect | Status | Mechanism |
|---|---|---|
| Initial terrain | **DET** | seed-generated; level checksum verified at GO_LIVE |
| Cell destruction/changes | **EVENT** | `WorldSync`: `World.CaptureCellChanges` patch captures local edits → cell messages; `SuppressNetCellCascade` prevents replayed edits re-triggering capture; bulk terrain streaming paces itself off the Steam send-buffer return value |
| Terrain agreement | verified | rolling `terrain=<count>/<hash>/<total>` in `[Counts]` — `terrainMismatch` counter should stay 0 |
| Fire spread (`CellBurningManager`) | **GAP?** | burning propagates via local simulation from an ignition. If ignitions replicate (the damage/cell-change events) the spread SHOULD re-derive identically, but propagation timing under frame-rate differences is unverified. Watch matchbox/burn fights for divergent burn fronts → then decide: sync ignition events only, or per-cell burn state. |
| Regrowth (`CellRegrower`) | **GAP?** | regrow timing is a local job; if regrow is gameplay-relevant (blocking paths), machines may disagree until the next cell-change event overwrites. Verify a regrowing biome in co-op. |
| Cell contact damage | **LOCAL** victim-side | contact damage applies on the machine whose ship/entity touches — consistent with victim-side damage design |

**Why segments matter beyond terrain:** `LevelSegmentComponent` stream-in/out is the
residency signal driving the entire authority system (see ENTITY_SYNC_ARCHITECTURE.md) —
terrain streaming and simulation authority are deliberately the same spatial unit.
