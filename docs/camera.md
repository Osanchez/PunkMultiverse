# Camera

The game uses **ProCamera2D** (`ProCamera2D.Runtime.dll`), driven by a set of *camera targets*
rather than by following a transform directly.

Exact signatures: [`api/camera.md`](api/camera.md).

## Targets, not a follow transform

```csharp
public abstract class CameraTargetBase : MonoBehaviour
{
    ShipInput shipInput;
    float maxDistance         = 1f;
    float inertia             = 1f;
    float targetInfluenceH    = 1f;
    float targetInfluenceV    = 1f;
    float duration;
}
```

Concrete targets: `MouseCameraTarget`, `GamepadCameraTarget`, `VirtualJoyCameraTarget`,
`POICameraTarget`. `GameController` collects them with
`GetComponentsInChildren<CameraTargetBase>()`.

Each target contributes a weighted offset — horizontal and vertical influence are separate
(`targetInfluenceH` / `targetInfluenceV`), and `inertia` smooths it. So "the camera leads where
you aim" is a target, not a hard-coded rule, and the input device in use changes which target is
active.

## Control during boot

`GameController.OnLevelGenerated` does this, in order:

```csharp
proCamera.MoveCameraInstantlyToPosition(shipManager.FirstAliveShip.transform.position);
proCamera.enabled = false;                       // <-- camera OFF during the pre-start hold
```

and `StartGame` re-enables it (for a continued run explicitly; otherwise via
`PlayStartSequence`).

**The camera is disabled between level generation and game start.** Anything that tries to move
or read the camera in that window is talking to a disabled component. This is the same window
the drop screen lives in.

Camera sway is a setting:

```csharp
Camera.main.GetComponent<Animator>().enabled = !settings.GameplayOptions.disableCameraSway;
```

— sway is an `Animator` on the main camera, not code. Disabling the setting disables that
animator.

## Shake

`ShipCameraShaker`, `ObjectShaker`, `ShakeOnStart`, `CellShakeAnimation`. Shake is applied on
top of ProCamera2D rather than through it.

## `EnemyTrackingCamera` — not a camera

Despite the name, this is a **station/turret behavior**: a rotating part that watches units
within a vision angle.

```csharp
public Unit  Target      { get; set; }
public float VisionAngle { get; set; }
public void  RefreshVisibleUnits(IEnumerable<Unit> units);
```

It has nothing to do with the player camera. `EnemyTrackingSystem` feeds it.

## Related

- `OrthoSizeFromFiewOfView` (sic — the typo is in the game) converts FOV to orthographic size.
- `FreeMoveCamera` is a debug flycam.
- `CameraExtentions` (also sic) holds helper methods.
- `CameraAudioListenerPosition` decides where audio is heard from — see [`audio.md`](audio.md).

Two of those names are misspelled in the game assembly. Search for the misspelling, not the
correct word.

## Multiplayer notes

The camera is entirely local presentation. Nothing about it is replicated, and each client
frames its own player. Two consequences:

- Anything that reads "what the player can see" from the camera is per-client and cannot be
  used for authority decisions.
- Residency and streaming are driven by the sync layer's own notion of player vicinity, not by
  the camera frustum. See [`ENTITY_SYNC_ARCHITECTURE.md`](ENTITY_SYNC_ARCHITECTURE.md).

## See also

- [`input.md`](input.md) — which camera target is active follows the input device
- [`game-state-flow.md`](game-state-flow.md) — the disabled-camera window
