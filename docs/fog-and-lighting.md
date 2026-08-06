# Fog & lighting

Two independent systems that both write per-cell byte arrays on `Level`, and both look like
"visibility" without being it.

Exact signatures: [`api/fog-and-lighting.md`](api/fog-and-lighting.md).

## Fog is a gas simulation

**This is the single most important thing on this page.** `Level.fogLevels` is not
explored/unexplored bits. It is a simulated gas with sources, emission, spreading and
thresholds.

```csharp
public class FogManager : MonoBehaviour
{
    [BurstCompile] /* jobs */

    private CellType fogCellType;
    private float    updateInterval;
    private byte     fogThreshold;          // above this, a cell counts as fogged
    private byte     fogSpreadThreshold;    // above this, it spreads to neighbours
    private byte     startingFogLevel;
    private List<FogVisual> fogVisuals;
    private HashSet<FogSource> sources;
    private NativeList<int> addedFogCells;

    public void FillInitialFogLevels();      // called at the very end of level generation
    public void Register(FogSource fogSource);
}
```

`FogSource` is a `MonoBehaviour` with an `EmissionPerTick` byte. Sources register with the
manager; the simulation ticks on `updateInterval`, not per frame.

Two thresholds, not one: `fogThreshold` decides whether a cell reads as fogged,
`fogSpreadThreshold` decides whether it propagates. A cell can be visibly fogged without
spreading.

### Consequences for multiplayer

- **Sync it host-authoritatively via terrain. Never diff it.** Treating it as exploration bits
  and diffing them produces nonsense, because the underlying value is a continuously evolving
  simulation, not a monotonic "seen" flag.
- It is saved **separately** from entities (`GameSaver.SaveFoW`, with its own in-progress flag)
  — see [`save-and-serialization.md`](save-and-serialization.md).
- The map's fog-of-war brush (`MapDrawer.fowDrawMaterial`) is a *rendering* of this, painted
  into a render target. Do not confuse the two.

### Visuals

`FogVisual` binds a `CellType` to two background colours, two particle colours, and blink
speed/smoothness. `FogType` is the GPU-side mirror with an explicit `Stride = 72` — if you add
a field to one, that constant and the shader layout must move together.

`refreshVisualsEveryFrame` exists as a serialized toggle; leave it off unless debugging.

## Lighting

```
LightmapGenerator : MonoBehaviour, IInitializable, IDisposable
  └── event Action LightmapGenerated

LightSource   : MonoBehaviour   — float intensity
LightSensor                     — reads the lightmap (drives IsInLightCondition for AI)
StationLightSource / StationLightManager / BlinkingLight
LightShapeBuilder, ParallelLightmapGeneratorJob
```

Light is baked into `Level.luminocity` (a per-cell byte array) by
`ParallelLightmapGeneratorJob`, not evaluated per pixel at runtime. `LightmapGenerator` is
`IDisposable` because it holds native collections.

`LightSensor` and the AI's `IsInLightCondition` read that array — so **lighting is gameplay**,
not decoration. Changing light propagation changes enemy behaviour.

## Rendering path

Fog rendering is a Scriptable Render Pipeline feature:

```
FogRendererFeature
 ├── FogMaskRenderPass
 ├── BlurFogMaskPass
 └── RenderFogPass
```

Plus `ResizeRenderTextureToScreenSize`. These run in the render pipeline, so they are outside
the fixed-step world and outside anything the sync layer sees.

## Performance note

The lightmap and fog both write large native arrays. Neither is the frame-time problem that
bulk terrain change is — see [`terrain.md`](terrain.md) — but both are `updateInterval`-gated
for a reason. Do not drive them per frame.

## See also

- [`terrain.md`](terrain.md) — the other per-cell arrays and their costs
- [`enemies.md`](enemies.md) — `IsInLightCondition`
- [`map-and-minimap.md`](map-and-minimap.md) — how fog is *drawn*
