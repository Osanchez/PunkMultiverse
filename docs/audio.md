# Audio & music

Exact signatures: [`api/audio.md`](api/audio.md).

## Sound effects — handle-based, called statically

```csharp
public class AudioManager : MonoBehaviour, IGameService
{
    public static int  PlaySfx(string sfx);
    public static int  PlaySfx(string sfx, Vector2 position);
    public static int  PlaySfx(string sfx, Transform transformToFollow);
    public static void Stop(int handle);

    public void UpdatePosition(int handle, Vector2 position);

    public Vector2       ListenerPosition { get; }
    public AudioListener AudioListener    { get; }
}
```

Sounds are named by **string**, and `PlaySfx` returns an `int` handle. Two things follow:

1. A misspelled sfx name fails silently — there is no compile-time check. Copy names from
   existing call sites or from the `Sfx` assets.
2. Anything looping or following must keep its handle. `Stop(handle)` and
   `UpdatePosition(handle, …)` are the only way back to a playing sound.

The three overloads are the whole spatialisation story: no position (2D), a fixed position, or
a transform to follow.

## `Sfx` — the authored asset

```csharp
public class Sfx : IAudioDatabaseItem
{
    public string guid, name;
    public AudioClipDistribution audioClips;   // weighted random selection
    public float volume;                       // [0..1]
    public int   priority;                     // [0..256], default 128
    public bool  is3d;                         // default true
    public AudioMixerGroup mixerGroup;
    public bool  looping;
    public float repeatMinDelay;               // default 0.01 — retrigger guard
    public bool  cancelPrevious;
    public bool  ignoreValidation;
}
```

`audioClips` is an `AudioClipDistribution`, so one named sfx is usually several clips chosen by
weight — variation is authored, not coded.

`repeatMinDelay` and `cancelPrevious` are the two anti-spam controls. A sound that machine-guns
when an event fires rapidly is almost always a `repeatMinDelay` of near-zero rather than a bug
in the caller.

Lookup is through `AudioDatabase` (`IAudioDatabaseItem`, keyed by `Guid` and `Name`).

## Music

```csharp
public class MusicManager : MonoBehaviour
{
    public class PlayedMusic { ... }
    public event Action<PlayedMusic> MusicRecycled;

    public PlayedMusic Play(MusicTrack musicTrack, float fadeInDuration = 0.001f);
    public void Stop(MusicTrack musicTrack, bool useFadeOut = true);
    public bool IsPlaying(MusicTrack musicTrack);
    public void StopAll();
}
```

Music is pooled — `MusicRecycled` fires when a `PlayedMusic` is reused, so anything holding one
must not assume it still refers to the same track. Default fade-in is effectively zero
(`0.001f`); pass a real duration if you want a fade.

`MusicTrackActivator` and `InGameMusicController` drive track selection from game state.
`AmbientSoundManager` handles ambience separately.

## Mixing and settings

`AudioManager.ApplySettings(OptionsData.AudioOptions)`, `SetMasterVolume`, `SetMusicVolume`.
Individual sounds route through `Sfx.mixerGroup` into an `AudioMixer`.

`TimeManager` also holds an `AudioMixer` reference and captures `effectsVolume` — time-scale
changes duck effects. If audio pitch or volume behaves oddly during a slow-motion effect, that
is where to look.

## Listener position

`CameraAudioListenerPosition` sets where the listener sits, and `AudioManager.ListenerPosition`
exposes it as a `Vector2`. In multiplayer the listener follows the local player's camera, so
positional audio is inherently per-client — nothing about sound needs replicating, only the
events that trigger it.

## Multiplayer notes

Sound is a **presentation** concern: replicate the event, let each client play its own audio.
Never send audio state over the wire. The mod calls `AudioManager.PlaySfx` directly from its own
UI and feedback code (for example `Ship.damageSfx` for hit markers).

## See also

- [`ui-and-screens.md`](ui-and-screens.md) · [`game-state-flow.md`](game-state-flow.md)
