# Input & devices

Unity's Input System package, driven through named action maps. Most input problems on this
project have turned out to be *which action map is active*, not the bindings.

Exact signatures: [`api/input.md`](api/input.md).

## Action maps are the state

`GameController` switches every `PlayerInput` wholesale at two points:

| When | Map |
|---|---|
| `OnLevelGenerated` (loading) | `"Menu"` |
| `StartGame` | `"ShipControl"` |

```csharp
foreach (var input in FindObjectsOfType<PlayerInput>())
    input.SwitchCurrentActionMap("ShipControl");
```

Additionally, `UIScreen` carries an `inputManuActionMapName` field (spelled `Manu` in the game)
and switches to it while the screen is open.

**If input is not reaching the ship, check the active action map first.** A screen that failed
to close, or a screen with the wrong map name, produces exactly the symptom of "controls stopped
working".

The named maps have wrapper classes: `ShipActionMap` (abstract, with `Enable()`/`Disable()`/
`Enabled`), `ShipControlActionMap`, `UIActionMap`, `ItemWheelActionMap`,
`InstrumentMenuActionMap`.

## `ShipInput`

The bridge from actions to ship systems. Its serialized references are the full list of what
input can drive:

```
ShipMovement ship
Aimer        aimer
Shooter      primaryShooter, secondaryShooter
ModuleActivator moduleActivator1, moduleActivator2, ...
```

Note **`Aimer`** — aim direction goes through it. Writing to a barrel transform directly loses
to the `Aimer`, which overwrites it. That cost a round of confusion in the PvP test harness:
`fire player N` was driving the ship's auto-turret `Shooter` rather than the intended target,
and barrel writes were being clobbered.

The game's single source of truth for aim direction is the `BarrelTransform`, but only as the
`Aimer` leaves it.

## Device tracking drives the UI

```csharp
public class LastUsedDeviceTracker : IInitializable, IDisposable
{
    public bool GamepadLastUsed => lastDevice is Gamepad;
    private void OnActionChange(object obj, InputActionChange change);
}
```

It subscribes to `InputActionChange` and remembers the last device that produced input.
`ShipHud` then picks between `pcSpriteSet`, `xboxSpriteSet` and `psSpriteSet`
(`PlatformSpriteSet`) based on it.

**Button prompts therefore change mid-session**, on the platform you are already on, the moment
someone touches a different device. That is intended. `AdaptiveInputHint`, `ButtonHint` and
`InputSelectorScreen` / `InputSelectorPopup` / `InputSelectorDeviceRow` are the rest of that
surface.

## Touch / virtual controls

`ShipVirtualJoyInput` and `VirtualJoyCameraTarget` exist, and `GameController` holds a
`virtualJoysRoot`. Gamepad rumble is `ShipGamepadRumble` + `RumblePreset`.

`CursorController` owns cursor visibility; `UIScreen.showCursor` feeds it.

## Making a screen controller-navigable

Two things are required, and neither is automatic:

1. `UIScreen.selectOnOpen` — the initial selection.
2. `UiManager.Push/Pop` of a `DefaultSelector` for nested UI, so focus restores on close.

A new screen without these is mouse-only. The battle-royale drop screen had to be retrofitted
with both.

## Multiplayer notes

Input is local and never replicated — the mod sends *intent and results*, not keystrokes. Two
input-adjacent notes from the history:

- The locator showcase input-lock is removed in net runs.
- Bots cannot click a screen, so `br-test.ps1` sets `BrChooseSpawn=false`; the drop screen is
  manual-test only. With it enabled, every automated probe false-fails.

## See also

- [`players-and-projectiles.md`](players-and-projectiles.md) — what `ShipInput` drives
- [`ui-and-screens.md`](ui-and-screens.md) · [`game-state-flow.md`](game-state-flow.md)
