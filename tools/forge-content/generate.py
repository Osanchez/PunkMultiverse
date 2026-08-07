"""Generate custom weapon art + audio for the WeaponForge sync test.

Sprites are animated multi-frame strips, because a single frozen frame would not
exercise the thing actually under test: whether a remote client drives the same
flipbook, at the same rate, on a projectile it did not fire.

Audio is synthesized PCM WAV (16-bit mono 44.1k) — WeaponForge decodes .wav
directly and synchronously, so there is no background-decode race to confuse a
first test with.
"""
import json, math, os, struct, wave, random
from PIL import Image, ImageDraw

OUT = os.path.dirname(os.path.abspath(__file__))
SPRITES = os.path.join(OUT, "content", "sprites")
SOUNDS = os.path.join(OUT, "content", "sounds")
WEAPONS = os.path.join(OUT, "content", "weapons")
for d in (SPRITES, SOUNDS, WEAPONS):
    os.makedirs(d, exist_ok=True)

# ---------------------------------------------------------------- sprites

def radial(dr, cx, cy, r, inner, outer):
    """Soft glowing disc: cheap, but reads correctly at 16px where a hard edge does not."""
    steps = max(2, int(r))
    for i in range(steps, 0, -1):
        t = i / steps
        col = tuple(int(inner[c] + (outer[c] - inner[c]) * t) for c in range(4))
        rr = r * t
        dr.ellipse([cx - rr, cy - rr, cx + rr, cy + rr], fill=col)


def plasma_sheet(path, size=16, frames=6):
    """Spinning plasma orb — the core rotates so motion is unmistakable frame to frame."""
    sheet = Image.new("RGBA", (size * frames, size), (0, 0, 0, 0))
    for f in range(frames):
        cell = Image.new("RGBA", (size * 4, size * 4), (0, 0, 0, 0))
        d = ImageDraw.Draw(cell)
        c = size * 2
        radial(d, c, c, size * 1.75, (200, 245, 255, 255), (40, 120, 255, 0))
        ang = (f / frames) * math.tau
        for arm in range(3):
            a = ang + arm * (math.tau / 3)
            x = c + math.cos(a) * size * 0.95
            y = c + math.sin(a) * size * 0.95
            radial(d, x, y, size * 0.5, (255, 255, 255, 255), (120, 220, 255, 0))
        radial(d, c, c, size * 0.65, (255, 255, 255, 255), (180, 240, 255, 90))
        sheet.paste(cell.resize((size, size), Image.LANCZOS), (f * size, 0))
    sheet.save(path)
    return frames


def shard_sheet(path, size=14, frames=4):
    """Tumbling crystal shard — an angular silhouette, so rotation is legible."""
    sheet = Image.new("RGBA", (size * frames, size), (0, 0, 0, 0))
    for f in range(frames):
        cell = Image.new("RGBA", (size * 4, size * 4), (0, 0, 0, 0))
        d = ImageDraw.Draw(cell)
        c = size * 2
        ang = (f / frames) * math.pi
        pts = []
        for i, (rad, off) in enumerate([(1.8, 0), (0.7, 0.5), (1.5, 1.0), (0.7, 1.5)]):
            a = ang + off * (math.tau / 4) * 2
            pts.append((c + math.cos(a) * size * rad, c + math.sin(a) * size * rad))
        d.polygon(pts, fill=(255, 170, 90, 255), outline=(255, 240, 200, 255))
        radial(d, c, c, size * 0.8, (255, 250, 230, 220), (255, 150, 60, 0))
        sheet.paste(cell.resize((size, size), Image.LANCZOS), (f * size, 0))
    sheet.save(path)
    return frames


def sheet_json(path, sheet_name, base, size, frames, fps, ppu=20, randomstart=True):
    sprites = [{"name": f"{base}{i}", "x": i * size, "y": 0, "w": size, "h": size}
               for i in range(frames)]
    doc = {
        "sheet": sheet_name,
        "pixelsPerUnit": ppu,
        "sprites": sprites,
        "animations": [{
            "name": f"{base}Anim",
            "fps": fps,
            "loop": "loop",
            "randomStart": randomstart,
            "frames": [s["name"] for s in sprites],
        }],
    }
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(doc, fh, indent=2)


pf = plasma_sheet(os.path.join(SPRITES, "mv_plasma.png"))
sheet_json(os.path.join(SPRITES, "mv_plasma.json"), "mv_plasma.png", "mvPlasma", 16, pf, 18)

sf = shard_sheet(os.path.join(SPRITES, "mv_shard.png"))
sheet_json(os.path.join(SPRITES, "mv_shard.json"), "mv_shard.png", "mvShard", 14, sf, 14)

# ---------------------------------------------------------------- audio

RATE = 44100

def write_wav(path, samples):
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(b"".join(struct.pack("<h", max(-32767, min(32767, int(s * 32767))))
                               for s in samples))


def zap(dur=0.22, f0=1400, f1=180):
    """Descending sweep + a little grit: reads as an energy weapon at low volume."""
    n = int(RATE * dur)
    out = []
    for i in range(n):
        t = i / RATE
        p = i / n
        f = f0 * (f1 / f0) ** p
        env = (1 - p) ** 2.2
        s = math.sin(math.tau * f * t)
        s += 0.35 * math.sin(math.tau * f * 2.01 * t)
        s += 0.18 * (random.random() * 2 - 1) * (1 - p)
        out.append(s * env * 0.55)
    return out


def thunk(dur=0.28):
    """Lobbed-weapon launch: body thump plus a short airy tail."""
    n = int(RATE * dur)
    out = []
    for i in range(n):
        t = i / RATE
        p = i / n
        env = math.exp(-7 * p)
        s = math.sin(math.tau * (150 * (1 - 0.5 * p)) * t) * 0.9
        s += 0.25 * (random.random() * 2 - 1) * math.exp(-22 * p)
        out.append(s * env * 0.6)
    return out


def beam_loop(dur=1.0, base=220):
    """SEAMLESS loop: whole cycles only, and the two ends are crossfaded, because
    continousShootSfx is force-looped and a discontinuity would tick every second."""
    n = int(RATE * dur)
    out = []
    for i in range(n):
        t = i / RATE
        s = 0.5 * math.sin(math.tau * base * t)
        s += 0.3 * math.sin(math.tau * base * 1.5 * t)
        s += 0.2 * math.sin(math.tau * base * 2.0 * t)
        s += 0.08 * math.sin(math.tau * 3.0 * t) * math.sin(math.tau * base * 4 * t)
        out.append(s * 0.35)
    xf = int(RATE * 0.02)
    for i in range(xf):
        a = i / xf
        out[i] = out[i] * a + out[n - xf + i] * (1 - a)
    return out[:n - xf]


random.seed(7)   # reproducible art+audio, so both machines can be given identical bytes
write_wav(os.path.join(SOUNDS, "mv_plasma_shot.wav"), zap())
write_wav(os.path.join(SOUNDS, "mv_shard_shot.wav"), thunk())
write_wav(os.path.join(SOUNDS, "mv_arc_loop.wav"), beam_loop())

# Per-sound overrides. Synthesized audio is much hotter than the game's stock clips,
# and a fast weapon retriggering an identical sample every 120ms buzzes.
for name, doc in {
    "mv_plasma_shot.json": {"volume": 0.45, "repeatMinDelay": 0.05},
    "mv_shard_shot.json": {"volume": 0.5, "repeatMinDelay": 0.08},
    "mv_arc_loop.json": {"volume": 0.3, "looping": True},
}.items():
    with open(os.path.join(SOUNDS, name), "w", encoding="utf-8") as fh:
        json.dump(doc, fh, indent=2)

print("sprites:", sorted(os.listdir(SPRITES)))
print("sounds :", sorted(os.listdir(SOUNDS)))
for f in sorted(os.listdir(SPRITES)):
    if f.endswith(".png"):
        im = Image.open(os.path.join(SPRITES, f))
        print(f"  {f}: {im.size[0]}x{im.size[1]} {im.mode}")
for f in sorted(os.listdir(SOUNDS)):
    if f.endswith(".wav"):
        with wave.open(os.path.join(SOUNDS, f)) as w:
            print(f"  {f}: {w.getnframes()} frames @ {w.getframerate()}Hz "
                  f"{w.getsampwidth()*8}-bit {w.getnchannels()}ch")
