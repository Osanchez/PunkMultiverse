# Level generation

The world is built as a **graph of rooms first**, then rasterized into cells. Everything is
seeded, and everything must stay deterministic — the multiplayer model generates the world
independently on every machine and replicates only the divergence.

Exact signatures: [`api/level-generation.md`](api/level-generation.md).

## Entry point

```csharp
public class LevelGenerator : ILevelGenerator, IGameService
{
    public async UniTask GenerateLevel(Level level, int seed);
    public async UniTask GenerateLevel(LevelGenerationContext context, int seed);
}
```

Called from `GameController.BuildLevel` and awaited before ship placement. Generators are
resolved through `ServiceLocator.Get<T>()` — some optionally, via `TryGet`.

## The declared steps, and the real order

The enum names seven steps:

```csharp
public enum LevelGenerationStep
{
    GenerateGraph, GenerateBiomeMap, PlaceDungeons, GenerateHeightMap,
    Rasterization, GrowPlants, PlaceEntities
}
```

with static progress events:

```csharp
public static Action<LevelGenerationStep>        StepStarted;
public static Action<LevelGenerationStep, float> StepFinished;   // float = seconds
```

The actual sequence does considerably more than the enum suggests, and the enum order is **not**
the execution order (`PlaceDungeons` runs before `GenerateBiomeMap`):

| # | Step | What runs |
|---|---|---|
| 1 | — | `context.Initialize(config.levelSize.x, config.levelSize.y)` |
| 2 | `GenerateGraph` | `GraphGenerator.Generate` → `InterestLevelSpreader.SpreadInterestLevel` → `ScannerGenerator.GenerateScanners` → `UpdateEdgeSettingsBasedOnBiome` |
| 3 | `PlaceDungeons` | `DungeonGenerator.PlaceDungeons` **(synchronous)** → `StationGenerator.GenerateStations` → `SpreadInterestLevel` *again* → `PoIGenerator.PlacePoIs` |
| 4 | `GenerateBiomeMap` | `BiomeMapGenerator.GenerateBiomeMap` → `SubBiomGenerator.GenerateSubBioms` |
| 5 | `GenerateHeightMap` | `HeightmapGenerator` → `BorderGenerator.GenerateBorder` → `BiomeBorderMaskGenerator` |
| 6 | `Rasterization` | `JRasterizator.Rasterize(context, seed)` → `MergedCellsGenerator.Generate` → `DepthMapGenerator` → `BackgroundGenerator` *(optional)* → `OutlineGenerator` |
| 7 | `GrowPlants` | `PlantGenerator.GeneratePlants` — **only if `config.generatePlants`** |
| 8 | `PlaceEntities` | `EntityGenerator.PlaceEntities(level, seed)` → `StationGenerator.InitializeStations` → `RandomObjectGenerator.Generate` — only if `config.generateEntities` |
| 9 | — | `ScannerAreaGenerator.Generate` |
| 10 | — | `FogManager.FillInitialFogLevels()` |

`debugSteps` is a hard-coded `static readonly bool = true`, so per-step timings are always
logged. Several sub-steps additionally log their own `StopWatch` durations. Free profiling.

### Interest level is spread twice

`SpreadInterestLevel` runs after the graph and again after stations and dungeons are placed.
It is the density/difficulty field that later steps sample (`PoIConfig.maxInterestLevel` filters
against it), so a change to placement changes it.

### When plants are disabled the array is filled with a sentinel

```csharp
level.plants[i] = new PlantCell { plantTypeIndex = byte.MaxValue };
```

`255` means "no plant". It is not a valid index — do not treat the array as dense.

## The graph

```csharp
public struct LevelGraph(int nodeCount, int edgeCount, int noiseCount) : IDisposable
{
    NativeList<LevelGraphNode> nodes;
    NativeList<LevelGraphEdge> edges;
    NativeList<int2>           triangulation;   // Delaunay
    NativeList<int2>           spanningTree;
    NativeList<float>          noiseScales;
    NativeParallelMultiHashMap<int, JNoiseLayer> noises;
    int startingNodeIndex;
}
```

Rooms become graph nodes; a triangulation plus a spanning tree decides which are connected —
the standard procedural-dungeon approach. Corridors and room outlines are then driven by
per-node noise layers, which is why the result is organic rather than rectangular.

`LevelGraph` is `IDisposable` and holds `Allocator.Persistent` native collections. It must be
disposed; `LevelGenerationContext` is a `using` in `GenerateLevel` for the same reason.

Useful members: `GetNodeWithCenter(float2)`, `AnyNodeOverlaps(node)`,
`RadiusInDirectionFromCenter(node, direction)`, `SetNodeBiome(index, biomeId)`, `StartNode`.

## Configuration

`LevelGeneratorConfig : ScriptableObject` holds `levelSize`, `defaultBiome`, `voidBiom`, the
biome border width, and the placement rules:

- **`PoIConfig`** — `maxAmount`, `canBePlacedOnMainPath`, `minDistanceFromCenter`,
  `minDistanceFromSamePoi`, `maxInterestLevel`, and a `Biom[] biomeFilter` with
  `biomeFilterIsBlacklist` (defaulting to **true** — the filter excludes by default).
- **`RequiredRoom`** — a room that must exist: its biome, its PoI, its `RoomSetup`, biome
  spread range, and optional crust generation.
- **`DungeonWithCount`** — a `DungeonSetup` plus a `MinMaxInt count`.

## The world is a disc inside a void border

`BorderGenerator` plus `voidBiom` is why the playable area is a disc surrounded by void rather
than a rectangle filling `levelSize`. Anything sizing itself to the world — the battle-royale
ring, for one — must size to the playable **disc**, not to `levelSize`.

## Determinism

The whole multiplayer consistency model rests on generation being reproducible from a seed:
generate the world independently on every machine, replicate only what diverges afterwards.

`Seed` is a readonly struct implicitly convertible to `int`. `Rnd` is the seeded generator
(`Range`, `Next`, `RandomDirection`, `Probability`, `ChangeSeed`). `SavableEntity` implements
`ISeedProvider` so per-entity randomness derives from entity identity rather than global state.

Vanilla nondeterminism has been found and patched in five places — a useful list, because each
one was a place where the world came out different on two machines:

```
RandomObjectGenerator.Generate
MergedCellsGenerator.Generate
AutoPopper.RegisterToPopIfNeeded
CellRegrower.OnCellChanged
UnityTilemapRenderer.OnLevelGenerated
```

See `src/Patches/DeterministicGeneration.cs`. If a new divergence appears after a game update,
this is the first list to re-check — and `tools/gamescan.ps1` will tell you whether any of those
five method bodies changed.

## Cost

Generation is the dominant cost of starting a run. World **pre-generation** cut START from ~30 s
to ~10 s on the dedicated server. Note that world *reuse* skips the scene reload, which has bitten
before: a stale captured reference survived across runs because the reload never happened.

## See also

- [`terrain.md`](terrain.md) — the cell arrays this produces, and why bulk edits are expensive
- [`environment.md`](environment.md) · [`plants.md`](plants.md)
- [`game-state-flow.md`](game-state-flow.md) — where generation sits in boot
- [`VANILLA_GOTCHAS.md`](VANILLA_GOTCHAS.md)
