# Game state & run flow

There is **no state enum and no state machine.** The run's lifecycle is a chain of `async`
methods on `GameController` plus four static events. Knowing the order matters, because most of
it is asynchronous and several steps land earlier than you would expect.

Exact signatures: [`api/game-state-flow.md`](api/game-state-flow.md).

## The four static events

```csharp
public static Action<Level> LevelGenerated;
public static Action        GameStarted;
public static Action        GameOver;
public static Action        GameWon;
```

They are **static**, so a subscription outlives the scene that made it. Anything subscribing
must unsubscribe, or it accumulates one handler per run and fires N times on the Nth run.

## Boot order

```
Start()                        coroutine; yields ONE frame first
 ├── isContinue → LoadLevel()  GameSaver.Load(saveFolder)
 └── else      → BuildLevel()
                   await levelGenerator.GenerateLevel(level, Seed)
                   await shipManager.PlaceShipEntitiesToStartPosition(level, isCoop, loadout)
                   OnLevelGenerated()

OnLevelGenerated()             async
 ├── EntityGenerator.PlaceGameObjectsForRooms(level)
 ├── ConnectStations()
 ├── shipManager.SpawnShipGameObjects(level, runArguments)
 ├── AssignHuds()
 ├── level.generationFinished = true
 ├── LevelGenerated?.Invoke(level)
 ├── every PlayerInput → action map "Menu"
 ├── await UniTask.DelayFrame(2)          <-- a real two-frame gap
 ├── shipManager.CheckShipsAlive()
 ├── camera: move instantly to FirstAliveShip, then proCamera.enabled = false
 ├── if (!gameStarted) timeManager.Pause(this)
 └── EntityManager.EntityDestroyed += OnEntityDestroyed

StartGame()                    called by the UI, not automatically
 ├── timeManager.RemoveAllModifiers(this)     <-- this is the unpause
 ├── every PlayerInput → action map "ShipControl"
 ├── gameStarted = true
 ├── shipManager.OnGameStarted()
 ├── GameStarted?.Invoke()
 └── isContinue ? (boss log, camera on, headlights on) : PlayStartSequence()
```

### Ship placement completes before the game is "in game"

`PlaceShipEntitiesToStartPosition` is awaited inside `BuildLevel`, which runs during loading.
Ships therefore exist, positioned, **before** the first in-game tick and before `StartGame`.

Any logic gated on "the first `InGame` frame" runs too late to influence where a player starts.
This is the root of the spawn-before-selection bug: the fix holds the pen every *render* frame
from `Toast.Update`, keyed on `CurrentMode` and never on `LobbyMode`. See
[`VANILLA_GOTCHAS.md`](VANILLA_GOTCHAS.md#ship-placement-is-async-and-lands-during-loading).

### Input is gated by action map, not by a flag

Loading switches every `PlayerInput` to `"Menu"`; `StartGame` switches them to
`"ShipControl"`. If input is not reaching the ship, check the current action map before
suspecting the input system.

### Pause is owner-keyed and compositional

`TimeManager` does not hold a single time scale. It holds a list of registrations:

```csharp
void AddModifier(TimeScaleModifier modifier, object owner);
void RemoveAllModifiers(object owner);
void Pause(object owner);
void SetTimeScale(float timeScale, object owner);
```

`GameController` pauses with `this` as the owner during loading and releases with
`RemoveAllModifiers(this)` in `StartGame`. Because everything is keyed by owner, several systems
can slow or pause time simultaneously without stomping each other — but an owner that forgets to
release leaves the game paused with no obvious culprit.

## `GameScene` — the entry point

```csharp
public static class GameScene
{
    public static RunArguments arguments;                  // global, mutable, static
    public static void GoToGameScene(RunArguments args);
    public static void Continue(bool coop);
}
```

`GameScene.arguments` is static state read from all over — `Shop` reads `arguments.isCoop` to
pick its price multiplier, for instance. It is set before the scene loads.

## `RunData` — everything scoped to one run

`IInitializable, IMementoOriginator<RunData.Memento>`.

| Holds | Notes |
|---|---|
| `GeneralShopItemList` | the single shop list shared by every station |
| `ConsumableShopItems` | |
| `unlockedShopCount`, `purchasedStationUpgradeCounts` | progression within the run |
| `shopUnlockRnd` | a dedicated `Rnd` — shop unlocks are seeded, not ad-hoc random |
| `ingredientsEverOwned`, `droppedModules`, `modulesAddedToShop`, `modulesPickedUp` | drop-history bookkeeping that feeds future rolls |
| `sharedResourceTanks` | **score lives here** — `Score` is a resource tank |
| `killedBossCount`, `killedEnemyCount`, `TotalRunTime` | |
| `AllShopItemsAreFree` | bypasses the whole purchase path |

Score being a `ResourceTank` rather than an int is worth remembering: it inherits tank
behavior, including the infinite-tank rule.

## `Level` — the world

`Level : IInitializable, IDisposable`. Bulk world data lives in `NativeArray`s, indexed
`y * Width + x`, one cell per world unit:

```
heightMap, mainBioms, bioms, cellTypes, foreGroundCellTypes, backGroundCellTypes,
luminocity, scannerAreas, fogLevels, plants, burnLevels (NativeHashMap),
obstackles (NativeHashSet<int2>), mergedCells, containingMergedCellRelativePosition
```

`Level.SegmentSize = 25`. `generationFinished` flips at the end of `OnLevelGenerated`.
`Level.CellChange` is the struct broadcast on terrain edits — see
[`terrain.md`](terrain.md) for why bulk changes are expensive.

Registries (`CellType`, `Biom`, `PlantType`, `MergedCellData`) map bytes in those arrays back to
assets — the arrays store byte ids, never references.

## Ending a run

- `OnGameOver` / `GameOver` and `GameWon` events.
- `Restart()` does **not** restart in place — it routes to `RunSetupScene.GoToLoadoutSelector`.
- `SaveAndExit()` is an `async UniTaskVoid`.

## Multiplayer notes

The mod adds a go-live barrier over this sequence, and the drop screen lives inside it — see
[`BATTLE_ROYALE.md`](BATTLE_ROYALE.md). Two ordering hazards have already been paid for:

- `GO_LIVE` was once applied three times per match, re-opening the drop screen and
  un-deploying the player. It must be idempotent.
- Broadcasts sent before the `InGame` flip are dropped (all 44 shop unlocks, once).

## See also

- [`level-generation.md`](level-generation.md) — what `GenerateLevel` actually does
- [`save-and-serialization.md`](save-and-serialization.md) — `LoadLevel` and mementos
- [`ENTITY_SYNC_ARCHITECTURE.md`](ENTITY_SYNC_ARCHITECTURE.md)
