# UI, screens & HUD

Exact signatures: [`api/ui-and-screens.md`](api/ui-and-screens.md).

## Two UI stacks, and you will meet both

The game mixes Unity's **uGUI** (`Canvas`, `Image`, `RawImage`, `EventSystem`, `RectTransform`)
with **UI Toolkit** (`VisualElement`, `VisualTreeAsset`, `Label`, `Button`).

- `UIScreen`, `ShipHud`, `InGameHud`, `Minimap`, `MapDrawer`, all the `*Widget` types — uGUI.
- `Popup` — UI Toolkit.

Check which one a type uses before assuming a parenting or styling approach. The mod's own UI
clones vanilla uGUI assets at runtime (`UiTheme.cs`), so it lives on the uGUI side.

## `UIScreen` — the unit of screen

A `MonoBehaviour` whose serialized fields *are* its behavior contract:

| Field | Effect |
|---|---|
| `canvas` | the screen's own canvas |
| `showCursor` | cursor visibility while open |
| `pauseTime` | **opening this screen pauses the game** |
| `inputManuActionMapName` | action map to switch to while open (note the typo — it is spelled `Manu` in the game) |
| `selectOnOpen` | initial gamepad/keyboard selection |
| `eventSystem` | |
| `closeDelay` | delay before the close completes |
| `animatedScreen` | optional open/close animation |
| `closeOnAwake` | start closed |

Two consequences worth internalising:

1. **Screens own pause and input mode.** A screen that fails to close leaves time paused and the
   action map wrong. Time pausing is owner-keyed (`TimeManager`), so the symptom is a game that
   is paused with no visible menu.
2. `selectOnOpen` is what makes a screen controller-navigable. A new screen without it is
   mouse-only — which is exactly the gap the drop screen had to close.

## `AnimatedScreen`

Sequences `AnimatedScreenElement` children with `firstAnimationDelay` and `animationSpacing`
(optionally a different `animationSpacingClose`). `AnimateOpen()` / `AnimateClose()` are
coroutines, and `RefreshElementList()` must be called if children change at runtime — the list
is cached in `Awake`.

## `UiManager` — a selection stack

```csharp
private Stack<DefaultSelector> activeSelectors;
public void Push(DefaultSelector selector);
public void Pop();
```

Nested UI restores focus by popping. Push/pop must be balanced or focus lands somewhere
unexpected.

## HUD

```
InGameHud                     one per game
 ├── damageOverlay (Image)    full-screen damage flash
 ├── ship1HudAnimator, ship2HudAnimator, sharedResourcesAnimator
 ├── DisplayDamage() / DisplayShieldDamage(Resource)
 ├── ContainingCellTypeChanged(previous, new)     — HUD reacts to the cell you are inside
 └── SetHudVisible(bool visible, bool animate)

ShipHud                       one per ship, assigned by GameController.AssignHuds()
 ├── AssignShip(Ship)
 ├── Dictionary<Resource, ResourceBar> resourceBars   — built per resource, from a prefab
 ├── abilitySlotsPanel, logDisplay, minionsWidget
 └── pcSpriteSet / xboxSpriteSet / psSpriteSet  (PlatformSpriteSet)
```

`ShipHud` picks its sprite set from the **last used input device**, not from the platform —
see [`input.md`](input.md). Button prompts change mid-session when the player touches a gamepad.

`GameController.AssignHuds()` runs during `OnLevelGenerated`, i.e. before `StartGame`.

## `ResourceBar` — rows, not a bar

A resource bar is a set of `ResourceBarRow` children, rebuilt whenever capacity changes:

```csharp
public bool CheckCapacityChanged()
{
    int num = Math.Max(0, Mathf.RoundToInt(resourceTank.Capacity));
    if (num == capacityLastFrame) return false;
    int rows = (num <= 0) ? 1 : Mathf.CeilToInt((float)num / MaxResourcePerRow);
    // destroy/instantiate rows to match, then redistribute
}
```

Rows are `Instantiate`d and `Destroy`ed as capacity moves, so capacity churn is allocation
churn. The `Math.Max(0, …)` clamp is recent — an earlier build could compute a negative
capacity here.

## Widgets

The `*Widget` suffix is a consistent convention for a reusable, data-bound UI element:
`ShopItemWidget`, `ModuleGridWidget`, `ModuleIconWidget`, `PriceWidget`, `HealthbarWidget`,
`ClusterWidget`, `ConnectionWidget`, `SpecialSlotWidget`, `MinionCountWidget`,
`ModuleEffectFieldWidget`, `PagerWidget`, `DraggedItemWidget`, `VaultGridWidget`.

If you need a UI element that already exists in some form, look for its widget first.

## Screens worth knowing

`MainMenu`, `RunSetupScreen`, `LoadoutSelector`, `ModuleGridScreen`, `ConsumablesScreen`,
`PauseScreen`, `GameOverScreen`, `GameWonScreen`, `OptionsScreen` (with `OptionsTab` subclasses:
`AudioOptionTab`, `VideoOptionsTab`, `GameplayOptionsTab`), `InputSelectorScreen`,
`LoadingScreen`, `SplashScreen`, `LeaderboardScene`.

## Multiplayer notes

- The mod's UI clones vanilla sprites/fonts at runtime rather than shipping assets
  (`UiTheme.cs`). The 8-bit HUD font has a 1.6× scaling quirk.
- Winner presentation required both a toast-collision fix and a game-over retitle — the
  `GameOverScreen` is reused for winning.
- Puppets must not hold shop or interaction state: `RemotePuppet.ScrubInteractions` runs on
  death.
- **The ship menu is guarded** (`Patches/ShipMenuGuards.cs`): its input list is pinned to the
  local ship, both open paths name the local player, a re-entrant `Open` may switch tabs but not
  re-run the input contract, a station is deaf to "open the shop" for 350ms after a close, and a
  throwing tab is contained so `Open` still finishes. Why each one exists:
  [`VANILLA_GOTCHAS.md`](VANILLA_GOTCHAS.md#screens--input).
- **The state is checked, not assumed** (`Core/MenuStateWatch.cs`): four invariants every frame,
  `[MenuState] broken …` when one holds for half a second, and — after two seconds — an escape
  through the game's own `Close()`. `menustate` (devcmd) prints the same reading on demand.
- The drop screen had to be made fully controller/keyboard navigable; that is `selectOnOpen`
  plus explicit navigation, not automatic.

## See also

- [`map-and-minimap.md`](map-and-minimap.md) · [`input.md`](input.md) · [`shops-and-economy.md`](shops-and-economy.md)
