# Save & serialization

Two representations of every entity — a `MonoBehaviour` in the scene and a plain data object —
kept in sync by an event loop. Saving is Odin serialization plus LZF compression, on a thread.

Exact signatures: [`api/save-and-serialization.md`](api/save-and-serialization.md).

## The two halves

```
SavableEntity : MonoBehaviour, ISeedProvider      the scene object
  ├── string entityId          prefab identity
  ├── bool destroyWhenUnloaded
  └── EntityData EntityData    <-- the data half

EntityData : IMementoOriginator<EntityData.Memento>     the serializable object
  ├── string entityId
  ├── int    instanceId        deterministic counter — THIS is identity
  ├── Vector3 position, Quaternion rotation
  ├── Dictionary<Type, ComponentData> components
  ├── event Action<EntityData, Vector3, Vector3> Moved
  └── event Action<EntityData> Destroyed
```

Per-component, the same split:

```
SavableComponent<T> : MonoBehaviour, IEntityBindingListener, IComponentDataCreator
    where T : ComponentData
  ├── T ComponentData
  ├── abstract T CreateData()
  ├── virtual OnFirstBind() / Bind(T) / Unbind(T)
  └── events OnBindToData / OnUnbindToData

ComponentData (abstract)
  ├── EntityData entity        back-reference
  ├── abstract ComponentData Clone()
  └── virtual OnCreate() / OnDestroy()
```

Components are keyed **by `Type`** in the dictionary, so an entity holds at most one of each
component data type. `TryGetComponent<T>`, `TryGetComponentImplementing<T>` and the
non-generic `TryGetComponent(Type, out …)` are the accessors.

**Identity is `instanceId`, a deterministic counter — never position.** Position fingerprinting
was removed once and must not come back.

## The transform ↔ data loop, and the jitter it caused

This is a small piece of code with outsized consequences:

```csharp
private void Update()
{
    if (EntityData != null && transform.hasChanged)
    {
        EntityData.rotation = transform.rotation;
        EntityData.MoveTo(transform.position);      // transform  ->  data
    }
}

private void OnEntityMoved(EntityData data, Vector3 oldPosition, Vector3 newPosition)
{
    if (newPosition != transform.position)
        transform.position = newPosition;           // data  ->  transform, HARD SET
}
```

`EntityData.MoveTo` raises `Moved`, which `SavableEntity` handles by **assigning
`transform.position` directly** — no interpolation, no physics, a teleport.

For local entities this is a harmless round trip. For a puppet being driven from the network it
was the bug: applying a snapshot by calling `data.MoveTo(wirePosition)` teleported the transform
to the raw wire position ~30 times a second, producing a yank-back sawtooth and resetting
interpolation every time.

The fix is to wire `MoveTo` only for **non-live** entities. Live entities keep their data fresh
for free, because the `Update` above writes the transform back into the data every frame anyway.

Fixed-step metrics cannot see this class of problem. Compare `rendersmooth` owner-vs-puppet.

## Mementos

The game's snapshot/restore mechanism, used for saving, for undo in the module grid, and as the
basis of several replication paths:

```csharp
public abstract class IMemento { }                 // an abstract CLASS, despite the name

public interface IMementoOriginator { ... }
public interface IMementoOriginator<TMemento> : IMementoOriginator where TMemento : IMemento
{
    TMemento CreateMemento();
    void RestoreFromMemento(TMemento memento);
}
```

Note `IMemento` is an abstract class, not an interface — the `I` prefix is misleading, and you
cannot mix it into a type that already has a base class.

Implemented by, among others: `EntityData`, `Module`, `ModuleGrid`, `ModuleGridOwner.Data`,
`ShopItem`, `Price`, `Vault`, `RunData`. `EntityData` additionally exposes
`RestoreComponentsFromMemento` separately from `RestoreFromMemento`, so component state can be
restored without disturbing the entity header.

## GameSaver

`Punk.SaveLoad.GameSaver` — note the namespace; the type is **not** in the global namespace like
most of the game.

| Constant | Value |
|---|---|
| `NORMAL_SAVE_FOLDER_NAME` | `save001` |
| `COOP_SAVE_FOLDER_NAME` | `coop_save001` |

Single-slot per mode. `GetSaveFolderName(bool coop)` is the static resolver.

### Pipeline

```
Save:  entities ──SaveWithOdin──> bytes ──CompressAndSave (CLZF2)──> file
Load:  file ──LoadAndDecompress──> bytes ──LoadWithOdin──> entities
```

Serialization is **Odin** (`Sirenix.Serialization`), which is why authored assets derive from
`SerializedScriptableObject` and why fields that look unserializable to Unity still round-trip.
Compression is `CLZF2`.

### Saving is asynchronous and split in two

```csharp
private async void SaveOnThread(SaveFolder folder);
private void SaveEntities(SaveFolder folder);
private void SaveFoW(SaveFolder folder);

public bool IsSaveInProgress    // backed by TWO flags: dataSaveInProgress, fowSaveInProgress
```

Entity data and fog-of-war are saved separately and tracked by separate flags. A save is only
fully complete when both are clear — checking one is not enough.

`Load(folderName)` is the path `GameController.LoadLevel` takes for a continued run, and it
substitutes entirely for level generation: no `GenerateLevel`, no ship placement.

## Fog of war is a simulation, not exploration

`fogLevels` is a gas simulation living in the `Level` native arrays. Sync it host-authoritatively
via terrain; never diff it as if it were explored/unexplored bits. It is saved separately from
entities for the same reason.

## Multiplayer notes

- Never fabricate baselines. Leases exist only within reported residency, and `Dormant` is a
  first-class state. See [`ENTITY_SYNC_ARCHITECTURE.md`](ENTITY_SYNC_ARCHITECTURE.md).
- `SavableEntity.Bind(entityData, isFirstTime)` distinguishes first bind from rebind —
  `OnFirstBind` exists so one-time setup does not re-run on reload.
- Joins must never block or diverge on terrain. Sync large state in player-vicinity chunks
  rather than all-or-nothing.

## See also

- [`ENTITY_SYNC_ARCHITECTURE.md`](ENTITY_SYNC_ARCHITECTURE.md) — the residency/authority contract
- [`game-state-flow.md`](game-state-flow.md) — where load sits in the boot sequence
- [`VANILLA_GOTCHAS.md`](VANILLA_GOTCHAS.md)
