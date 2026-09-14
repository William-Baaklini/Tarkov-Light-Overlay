"""
Pre-slice huge Tarkov map PNGs into a tile pyramid.

Why: Interchange.png is 12241x8380. Decoded that is ~410 MB of RAM.
Tiling means the viewer only ever decodes the ~20 tiles currently on screen
(~20 MB), no matter how big the source map is.

Output layout:
    tiles/<MapName>/<z>/<x>_<y>.png     z=0 is fully zoomed out (1 tile)
    tiles/maps.json                     metadata the C# viewer reads

Run:  python tools/tile_maps.py
      python tools/tile_maps.py --force     (rebuild everything)
"""

import argparse
import json
import math
import os
import re
import sys
import time
from concurrent.futures import ThreadPoolExecutor

from PIL import Image

# These maps are far larger than Pillow's default decompression-bomb guard.
Image.MAX_IMAGE_PIXELS = None

TILE_SIZE = 512
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC_DIR = os.path.join(ROOT, "maps")
OUT_DIR = os.path.join(ROOT, "tiles")

# Filenames are whatever the user dropped in; tidy them up for the tab strip.
# Keys are matched case- and separator-insensitively, so "groundzero",
# "GroundZero" and "ground_zero" all land on the same display name.
DISPLAY_NAME_FIXES = {
    "shorline": "Shoreline",
    "groundzero": "Ground Zero",
    "thelab": "The Lab",
}


def _cap(word: str) -> str:
    """Capitalise a lowercase word, but leave USEC / TerraGroup alone."""
    return word.capitalize() if word.islower() else word


def display_name(stem: str) -> str:
    key = re.sub(r"[^a-z0-9]", "", stem.lower())
    if key in DISPLAY_NAME_FIXES:
        return DISPLAY_NAME_FIXES[key]
    # CamelCase / snake_case / kebab-case -> spaced words
    spaced = re.sub(r"(?<=[a-z0-9])(?=[A-Z])", " ", stem)
    spaced = spaced.replace("_", " ").replace("-", " ")
    return " ".join(_cap(w) for w in spaced.split())


def tile_one_level(img, out_dir, z, progress):
    """Write every tile for one pyramid level. img is already scaled for z."""
    w, h = img.size
    cols = math.ceil(w / TILE_SIZE)
    rows = math.ceil(h / TILE_SIZE)
    level_dir = os.path.join(out_dir, str(z))
    os.makedirs(level_dir, exist_ok=True)

    def write(xy):
        x, y = xy
        box = (
            x * TILE_SIZE,
            y * TILE_SIZE,
            min((x + 1) * TILE_SIZE, w),
            min((y + 1) * TILE_SIZE, h),
        )
        tile = img.crop(box)
        tile.save(
            os.path.join(level_dir, f"{x}_{y}.png"),
            format="PNG",
            compress_level=6,
        )
        progress[0] += 1

    coords = [(x, y) for y in range(rows) for x in range(cols)]
    # PIL releases the GIL inside encode, so threads genuinely parallelise here.
    with ThreadPoolExecutor(max_workers=min(8, (os.cpu_count() or 4))) as pool:
        list(pool.map(write, coords))

    return cols, rows


def build_map(path, force=False):
    stem = os.path.splitext(os.path.basename(path))[0]
    out_dir = os.path.join(OUT_DIR, stem)
    done_marker = os.path.join(out_dir, ".complete")

    if not force and os.path.exists(done_marker):
        if os.path.getmtime(done_marker) >= os.path.getmtime(path):
            with open(done_marker, "r", encoding="utf-8") as fh:
                meta = json.load(fh)
            print(f"  {stem}: up to date ({meta['tiles']} tiles), skipping")
            return meta

    t0 = time.time()
    print(f"  {stem}: decoding...", flush=True)
    img = Image.open(path)
    # Drop alpha: saves 25% of peak RAM and these maps have no transparency.
    if img.mode != "RGB":
        img = img.convert("RGB")
    full_w, full_h = img.size

    # Deepest level = native resolution. Level 0 must fit in a single tile.
    max_level = max(0, math.ceil(math.log2(max(full_w, full_h) / TILE_SIZE)))

    print(
        f"  {stem}: {full_w}x{full_h}, {max_level + 1} zoom levels",
        flush=True,
    )

    progress = [0]
    levels = {}
    current = img
    # Walk from full res downwards, halving each step. Memory shrinks as we go,
    # so peak usage is just the source image itself.
    for z in range(max_level, -1, -1):
        cols, rows = tile_one_level(current, out_dir, z, progress)
        levels[str(z)] = {
            "width": current.size[0],
            "height": current.size[1],
            "cols": cols,
            "rows": rows,
        }
        if z > 0:
            nw = max(1, current.size[0] // 2)
            nh = max(1, current.size[1] // 2)
            current = current.resize((nw, nh), Image.LANCZOS)

    img.close()

    meta = {
        "id": stem,
        "name": display_name(stem),
        "width": full_w,
        "height": full_h,
        "tileSize": TILE_SIZE,
        "maxLevel": max_level,
        "levels": levels,
        "tiles": progress[0],
    }
    with open(done_marker, "w", encoding="utf-8") as fh:
        json.dump(meta, fh, indent=1)

    print(
        f"  {stem}: {progress[0]} tiles in {time.time() - t0:.1f}s",
        flush=True,
    )
    return meta


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--force", action="store_true", help="rebuild existing tiles")
    args = ap.parse_args()

    if not os.path.isdir(SRC_DIR):
        print(f"No maps folder at {SRC_DIR}", file=sys.stderr)
        return 1

    # Case-insensitive, so a lowercase "factory.png" does not sort after every
    # capitalised map. This order is the tab-strip order in the app.
    sources = sorted(
        (
            os.path.join(SRC_DIR, f)
            for f in os.listdir(SRC_DIR)
            if f.lower().endswith((".png", ".jpg", ".jpeg", ".webp"))
        ),
        key=str.lower,
    )
    if not sources:
        print(f"No images found in {SRC_DIR}", file=sys.stderr)
        return 1

    os.makedirs(OUT_DIR, exist_ok=True)
    print(f"Tiling {len(sources)} map(s) -> {OUT_DIR}")

    maps = [build_map(p, force=args.force) for p in sources]

    with open(os.path.join(OUT_DIR, "maps.json"), "w", encoding="utf-8") as fh:
        json.dump({"tileSize": TILE_SIZE, "maps": maps}, fh, indent=1)

    total = sum(m["tiles"] for m in maps)
    size = sum(
        os.path.getsize(os.path.join(dp, f))
        for dp, _, fs in os.walk(OUT_DIR)
        for f in fs
    )
    print(f"Done. {total} tiles, {size / 1e6:.0f} MB on disk.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
