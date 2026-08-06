# Map & minimap

Two separate renderers over the same world data, plus an icon layer keyed on entities.

Exact signatures: [`api/map-and-minimap.md`](api/map-and-minimap.md).

## Minimap — a Burst job into a texture

```csharp
public class Minimap : MonoBehaviour
{
    [BurstCompile] private struct UpdateTextureJob : IJobFor { ... }

    private RawImage      image;
    private RectTransform scanline;
    private Vector2Int    resolution;
    private float         updateInterval;
    private Biom          voidBiome;
}
```

The minimap is not a camera. It is a texture written by a Burst-compiled `IJobFor` sampling the
`Level` native arrays directly, refreshed on `updateInterval` rather than per frame. `voidBiome`
is passed in so the border reads as empty rather than as terrain.

Because it samples the level arrays, it reflects world data — not what is on screen, and not
what the player has seen, unless fog is applied.

## Big map — `MapDrawer`

```csharp
private RawImage  mapImage;
private Shader    mapShader;
private Material  fowDrawMaterial;      // fog of war painted with a brush texture
private Texture2D fowBrush;
private float     emptyCellAlpha;
private float     scannerBrightness;    // [0..1]
```

Fog of war on the map is **painted** with a brush texture through `fowDrawMaterial` — it is a
render-target operation, not a per-cell array write. Scanner coverage is a separate brightness
channel (`scannerAreas` in `Level`).

`MapMover` handles panning.

## Icons — `MapIconManager`

```csharp
public class MapIconManager : MonoBehaviour, IMementoOriginator<Memento>, IInitializable
{
    Dictionary<EntityData, MapIcon> icons;
    HashSet<int>                    entitiesWithVisibleIcons;
    HashSet<int>                    alwaysVisibleEntityIds;
    Dictionary<string, MapIcon>     entityIdToiconPrefab;   // keyed by prefab entityId
}
```

Icons are keyed on `EntityData`, and which prefab to use is looked up by the entity's string
`entityId`. Visibility is two sets: what is currently visible, and what is *always* visible
(`alwaysVisibleEntityIds`) regardless of discovery.

It is an `IMementoOriginator`, so discovered-icon state is part of the save.

`MapIcon` holds a `Target` `EntityData` and raises `TargetChanged`. Specialised subclasses:
`StationMapIcon`, `InstrumentMapIcon`, `EntityMapItem`.

## The information-leak surface

**This is the part that matters for multiplayer.** The map layer is the easiest place to
accidentally tell a player something they should not know, and it has gone wrong twice:

- The minimap once leaked **player positions** to everyone.
- Map icons once leaked **enemies and players** in battle royale.

Both are now filtered. When adding anything to the map, decide explicitly what each player is
allowed to know — see the "What a player is allowed to know" section of the README and
[`BATTLE_ROYALE.md`](BATTLE_ROYALE.md).

Related: `alwaysVisibleEntityIds` bypasses discovery entirely, so adding an id there makes it
visible to everyone immediately.

## Battle royale additions

- The gold crate shows on the big map during the opening hold (it was once gated behind
  `RingVisible`, so it never appeared).
- Care packages get an in-world edge arrow (`OffscreenIndicator`) as well as a map icon.
- The ring is drawn as a rendered annulus — **not** painted into terrain, and not a map-only
  overlay. See [`terrain.md`](terrain.md) for why painting it was abandoned.

## See also

- [`fog-and-lighting.md`](fog-and-lighting.md) — fog is a simulation, not exploration bits
- [`terrain.md`](terrain.md) · [`ui-and-screens.md`](ui-and-screens.md)
