#!/usr/bin/env python3
"""Generate the test weapons' sounds.

These are TEST assets for the multiverse content/audio path, not shipped art. Generating them
rather than committing opaque binaries means the set is reproducible, reviewable as a diff, and
regenerable at a different length or level when a test needs it.

Why each one exists:

  mv_arc_loop      the held beam's continousShootSfx. THE sound the whole HeldFireSync audio path
                   exists to deliver to other players, so it has to be instantly recognisable and
                   obviously continuous -- if a remote beam is silent, or keeps playing after the
                   shooter lets go, you must hear that without straining.
  mv_arc_release   NEW. Vanilla's StopContinousSound plays ReleaseSfx as it stops the loop, and
                   HeldFireSync does the same on the peer. Without a release sound that code path
                   is untestable by ear: a correct stop and a stop-that-never-fires both just go
                   quiet. A distinct power-down turns "did it stop?" into a yes/no you can hear.
  mv_plasma_shot   one-shot, the ordinary per-shot path that already replicated.
  mv_shard_shot    one-shot for the lobbed weapon, deliberately unlike the plasma one so two
                   players firing different weapons are distinguishable in one recording.

SEAMLESS LOOPING is the one hard constraint. A looping AudioSource jumps from the last sample
back to the first, so any step across that join is a click once per loop -- on a beam held for
several seconds that reads as a broken sound, and it would be easy to blame the netcode for it.
Every partial here is at an integer multiple of 1/duration and the noise is synthesised by
inverse FFT of a random-phase spectrum, which is inherently periodic over the buffer. The loop is
therefore seamless by construction, not by crossfading the ends and hoping. Verified at the end.

Usage:  python tools/make-weapon-sounds.py [--out DIR]
Defaults to tools/forge-content/sounds (the fixture the host serves).
"""
import argparse
import json
import os
import wave

import numpy as np

SR = 44100
RNG = np.random.default_rng(0xB3A11)         # fixed: same bytes on every run, so a diff means a real change


def periodic_noise(n, lo, hi, rng):
    """Band-limited noise that is exactly periodic over n samples.

    Built in the frequency domain so every component completes a whole number of cycles across
    the buffer. Ordinary white noise would not, and its loop join would click.
    """
    spec = np.zeros(n // 2 + 1, dtype=complex)
    freqs = np.fft.rfftfreq(n, 1.0 / SR)
    band = (freqs >= lo) & (freqs <= hi)
    phase = rng.uniform(0, 2 * np.pi, band.sum())
    spec[band] = np.exp(1j * phase)
    out = np.fft.irfft(spec, n)
    peak = np.max(np.abs(out))
    return out / peak if peak > 0 else out


def harmonic(t, base_hz, partials, dur):
    """Sum partials, snapping each to an exact multiple of the loop rate so the buffer is periodic."""
    loop_hz = 1.0 / dur
    out = np.zeros_like(t)
    for mult, amp, phase in partials:
        f = round(base_hz * mult / loop_hz) * loop_hz     # the snap that makes it loop-safe
        out += amp * np.sin(2 * np.pi * f * t + phase)
    return out


def norm(x, peak):
    m = np.max(np.abs(x))
    return x * (peak / m) if m > 0 else x


def write_wav(path, x):
    """16-bit mono PCM. Dithered, because quantising a quiet synthetic tail without it produces
    audible stair-stepping rather than a smooth fade."""
    x = np.clip(x, -1.0, 1.0)
    dither = RNG.uniform(-0.5, 0.5, len(x)) / 32768.0
    pcm = np.clip(np.round((x + dither) * 32767.0), -32768, 32767).astype(np.int16)
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())
    return len(pcm)


# ---------------------------------------------------------------------------- the sounds

def arc_loop(dur=1.0):
    """A held electric beam: low buzz, a moving mid formant, and crackle on top."""
    n = int(SR * dur)
    t = np.arange(n) / SR
    loop_hz = 1.0 / dur

    # Odd harmonics -> a hollow, electric buzz rather than a musical tone.
    body = harmonic(t, 116.0, [(1, 1.00, 0), (3, 0.55, 1.1), (5, 0.32, 2.3),
                               (7, 0.20, 0.4), (9, 0.13, 1.9), (11, 0.08, 2.8)], dur)

    # Formant sweep. Its rate is snapped to the loop rate too, or the sweep itself would jump.
    sweep_hz = round(3.0 / loop_hz) * loop_hz
    formant = np.sin(2 * np.pi * 720.0 * t) * (0.5 + 0.5 * np.sin(2 * np.pi * sweep_hz * t))
    body += 0.22 * formant

    # Crackle: two noise bands, the upper one gated hard so it spits rather than hisses.
    fizz = 0.30 * periodic_noise(n, 1800, 6500, RNG)
    spit = periodic_noise(n, 6500, 15000, RNG)
    gate = periodic_noise(n, 4, 40, RNG)
    spit *= np.clip(gate * 3.0, 0, 1) ** 2
    body += 0.18 * spit + fizz

    # Amplitude wobble at integer rates, so the modulation is periodic as well.
    am = 1.0
    for rate, depth in ((7.0, 0.10), (13.0, 0.06), (31.0, 0.035)):
        am = am * (1.0 - depth + depth * np.sin(2 * np.pi * (round(rate / loop_hz) * loop_hz) * t))
    body *= am

    body = np.tanh(body * 1.5)                 # soft clip: grit, and a tighter peak
    return norm(body, 0.80)


def arc_release(dur=0.42):
    """Power-down: the buzz drops away and the field collapses. Deliberately unlike the loop, so
    'stopped' and 'still going' are never confusable."""
    n = int(SR * dur)
    t = np.arange(n) / SR
    k = t / dur

    f = 116.0 * (2.0 ** (-2.4 * k))            # about two and a half octaves down
    ph = 2 * np.pi * np.cumsum(f) / SR
    x = np.sin(ph) + 0.5 * np.sin(3 * ph) + 0.25 * np.sin(5 * ph)

    x += 1.2 * periodic_noise(n, 300, 9000, RNG) * np.exp(-28.0 * k)   # collapse snap
    x *= np.exp(-4.5 * k)
    x[: int(SR * 0.004)] *= np.linspace(0, 1, int(SR * 0.004))         # no click on entry
    return norm(np.tanh(x * 1.2), 0.85)


def plasma_shot(dur=0.30):
    """Bright discharge: noise crack, then a fast downward sweep with body."""
    n = int(SR * dur)
    t = np.arange(n) / SR
    k = t / dur

    f = 1900.0 * np.exp(-5.0 * k) + 240.0
    ph = 2 * np.pi * np.cumsum(f) / SR
    x = np.sin(ph) * np.exp(-7.0 * k)
    x += 0.45 * np.sin(2 * ph) * np.exp(-12.0 * k)
    x += 1.4 * periodic_noise(n, 900, 12000, RNG) * np.exp(-40.0 * k)  # the crack
    x += 0.5 * np.sin(2 * np.pi * 70 * t) * np.exp(-16.0 * k)          # thump
    x[: int(SR * 0.002)] *= np.linspace(0, 1, int(SR * 0.002))
    return norm(np.tanh(x * 1.6), 0.90)


def shard_shot(dur=0.34):
    """Lobbed shard: an inharmonic metallic clank plus air. Nothing like the plasma crack."""
    n = int(SR * dur)
    t = np.arange(n) / SR
    k = t / dur

    x = np.zeros(n)
    for ratio, amp, decay in ((1.00, 1.00, 11.0), (2.41, 0.62, 15.0),
                              (3.83, 0.40, 19.0), (5.21, 0.26, 24.0), (7.13, 0.15, 30.0)):
        x += amp * np.sin(2 * np.pi * 340.0 * ratio * t) * np.exp(-decay * k)

    air = periodic_noise(n, 500, 7000, RNG) * (k ** 0.6) * np.exp(-6.0 * k)   # whoosh after the clank
    x = x + 0.55 * air
    x += 0.7 * periodic_noise(n, 2000, 11000, RNG) * np.exp(-55.0 * k)        # strike transient
    x[: int(SR * 0.0015)] *= np.linspace(0, 1, int(SR * 0.0015))
    return norm(np.tanh(x * 1.3), 0.88)


# Sidecars: WeaponForge reads these next to the wav. Levels are set so the LOOP sits under the
# one-shots -- a beam you hold for seconds is fatiguing at shot volume.
SIDECARS = {
    "mv_arc_loop":    {"volume": 0.45, "looping": True},
    "mv_arc_release": {"volume": 0.55},
    "mv_plasma_shot": {"volume": 0.50, "repeatMinDelay": 0.05},
    "mv_shard_shot":  {"volume": 0.55, "repeatMinDelay": 0.08},
}

BUILDERS = {
    "mv_arc_loop": arc_loop,
    "mv_arc_release": arc_release,
    "mv_plasma_shot": plasma_shot,
    "mv_shard_shot": shard_shot,
}


def main():
    ap = argparse.ArgumentParser()
    here = os.path.dirname(os.path.abspath(__file__))
    ap.add_argument("--out", default=os.path.join(here, "forge-content", "sounds"))
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    print(f"{'sound':16} {'dur':>7} {'peak':>6} {'bytes':>8}  loop seam")
    ok = True
    for name, build in BUILDERS.items():
        x = build()
        path = os.path.join(args.out, name + ".wav")
        write_wav(path, x)

        seam = ""
        if SIDECARS[name].get("looping"):
            # The claim this file makes about itself, checked rather than asserted.
            step = abs(x[0] - x[-1])
            typical = float(np.mean(np.abs(np.diff(x))))
            ratio = step / typical if typical else 0.0
            good = ratio < 3.0
            ok = ok and good
            seam = f"{ratio:.2f}x typical step -> {'SEAMLESS' if good else 'CLICKS - FIX'}"

        with open(os.path.join(args.out, name + ".json"), "w") as f:
            json.dump(SIDECARS[name], f, indent=2)
            f.write("\n")

        print(f"{name:16} {len(x)/SR:6.3f}s {np.max(np.abs(x)):6.3f} "
              f"{os.path.getsize(path):8}  {seam}")

    print(f"\nwrote {len(BUILDERS)} sound(s) + sidecars to {args.out}")
    if not ok:
        raise SystemExit("a looping sound would click at its join")


if __name__ == "__main__":
    main()
