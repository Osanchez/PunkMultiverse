"""Turn a folder of animation frames into a WeaponForge sprite sheet + descriptor.

WeaponForge wants one PNG holding every frame side by side, plus a .json that slices it and
declares the flipbook. This does that, and it is deliberately deterministic: the same frames in
the same order always produce byte-identical output, because every machine in a session has to
end up with the same bytes or the module digest diverges and the run is refused.

Usage:
    python assemble_sheet.py <frames_dir> <sheet_name> <base> [--fps 18] [--ppu 80]

<frames_dir> holds frame_00.png, frame_01.png, ... (sorted by name = play order).
"""
import argparse, json, os, sys
from PIL import Image


def assemble(frames_dir, sheet_name, base, fps, ppu, out_dir, drop_first=False):
    names = sorted(f for f in os.listdir(frames_dir) if f.lower().endswith(".png"))
    if not names:
        sys.exit(f"no PNG frames in {frames_dir}")
    # animate_image returns the untouched input as frame 0. Keeping it is usually right for a
    # loop (it IS a real frame), but --drop-first exists for when it does not match the motion.
    if drop_first and len(names) > 1:
        names = names[1:]

    frames = [Image.open(os.path.join(frames_dir, n)).convert("RGBA") for n in names]
    w, h = frames[0].size
    for n, im in zip(names, frames):
        if im.size != (w, h):
            sys.exit(f"frame {n} is {im.size}, expected {(w, h)} - frames must be uniform")

    sheet = Image.new("RGBA", (w * len(frames), h), (0, 0, 0, 0))
    for i, im in enumerate(frames):
        sheet.paste(im, (i * w, 0))

    os.makedirs(out_dir, exist_ok=True)
    png_path = os.path.join(out_dir, sheet_name)
    sheet.save(png_path, optimize=True)

    sprites = [{"name": f"{base}{i}", "x": i * w, "y": 0, "w": w, "h": h} for i in range(len(frames))]
    doc = {
        "sheet": sheet_name,
        "pixelsPerUnit": ppu,
        "sprites": sprites,
        # randomStart so several projectiles in flight do not flip in lock-step and read as one
        # strobe - WeaponForge's own docs call this out for shotguns, and it matters here too.
        "animations": [{
            "name": f"{base}Anim",
            "fps": fps,
            "loop": "loop",
            "randomStart": True,
            "frames": [s["name"] for s in sprites],
        }],
    }
    json_path = os.path.join(out_dir, os.path.splitext(sheet_name)[0] + ".json")
    with open(json_path, "w", encoding="utf-8") as fh:
        json.dump(doc, fh, indent=2)

    print(f"{png_path}: {len(frames)} frames, {w}x{h} each -> sheet {sheet.size[0]}x{sheet.size[1]}")
    print(f"{json_path}: animation '{base}Anim' @ {fps}fps, ppu {ppu}")
    return png_path, json_path


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("frames_dir")
    ap.add_argument("sheet_name")
    ap.add_argument("base")
    ap.add_argument("--fps", type=int, default=18)
    ap.add_argument("--ppu", type=int, default=80)
    ap.add_argument("--out", default=".")
    ap.add_argument("--drop-first", action="store_true")
    a = ap.parse_args()
    assemble(a.frames_dir, a.sheet_name, a.base, a.fps, a.ppu, a.out, a.drop_first)
