"""
Build the icon-matching index the overlay's item scanner uses.

Input is a folder of Tarkov item icons named "<tarkov.dev item id>.png", exactly
as tarkov.dev serves them as `gridImageLink` (and exactly as RatScanner caches
them). Every such icon is 63*n+1 pixels on a side, because that is the game's
inventory cell pitch: at 1080p / 100% scale the game blits these 1:1, so a crop
off the screen is very nearly the reference image.

Output is one compact binary file. We do NOT ship the icons themselves -- 4221
PNGs is 56 MB, while the index is ~2 MB and is all the matcher needs.

Why store premultiplied colour AND alpha, rather than a plain hash:

    The icons are transparent. On screen, whatever shows through is the
    inventory slot background, which the game tints by item rarity. So the
    observed pixel is

        observed = premult + (1 - alpha) * background

    Keeping alpha lets the matcher solve for `background` per candidate at
    query time (closed form, one pass) instead of us having to enumerate
    Tarkov's rarity palette and bake a variant per colour. That makes matching
    invariant to any background the game throws at us, including ones added
    after this was written.

Run:  python tools/build_icon_index.py
      python tools/build_icon_index.py --source <dir> --out <file>
"""

import argparse
import os
import re
import struct
import sys
import time

import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# The game's inventory cell pitch. Icons are CELL*n + 1 px (the +1 is the shared
# 1px grid border), so a 1x1 item is 64x64 and a 2x1 is 127x64.
CELL = 63

# Thumbnail the matcher compares against. 16x16 keeps ~2 MB total while leaving
# enough structure to separate several thousand icons of the same grid size.
THUMB = 16

MAGIC = b"TLOI"
VERSION = 1

ID_RE = re.compile(r"^[0-9a-f]{24}$")

DEFAULT_SOURCES = [
    os.path.join(ROOT, "Examples", "RatScanner", "Data", "icons"),
    os.path.join(ROOT, "data", "icons"),
]


def grid_size(w: int, h: int):
    """Icon pixel size -> (cols, rows) in inventory cells, or None if off-grid."""
    if (w - 1) % CELL or (h - 1) % CELL:
        return None
    gw, gh = (w - 1) // CELL, (h - 1) // CELL
    if not (1 <= gw <= 12 and 1 <= gh <= 12):
        return None
    return gw, gh


def thumbnail(path):
    """
    -> (premultiplied grayscale 16x16 uint8, alpha 16x16 uint8, (cols, rows))

    Premultiplying *before* the resize matters: the icons store dark RGB under
    fully transparent pixels, and averaging that into edge pixels would bleed a
    dark halo into the silhouette.
    """
    with Image.open(path) as im:
        size = im.size
        g = grid_size(*size)
        if g is None:
            return None
        # Drop the outermost pixel on every side. A slot spans grid line to grid
        # line inclusive, so that ring is where the game draws the border -- a
        # colour that is neither the icon nor the slot background, and that would
        # otherwise poison the background solve. The scanner crops to match.
        rgba = im.convert("RGBA").crop((1, 1, size[0] - 1, size[1] - 1))
        arr = np.asarray(rgba, dtype=np.float32)

    rgb, a = arr[:, :, :3], arr[:, :, 3:4] / 255.0
    gray = rgb[:, :, 0] * 0.299 + rgb[:, :, 1] * 0.587 + rgb[:, :, 2] * 0.114
    premult = gray * a[:, :, 0]

    # Area-average down to THUMB x THUMB. Both channels get the same treatment
    # so the "premult + (1-alpha)*bg" relation survives the downsample.
    def shrink(plane):
        img = Image.fromarray(plane.astype(np.float32), mode="F")
        return np.asarray(img.resize((THUMB, THUMB), Image.BOX), dtype=np.float32)

    p = np.clip(shrink(premult), 0, 255).astype(np.uint8)
    al = np.clip(shrink(a[:, :, 0] * 255.0), 0, 255).astype(np.uint8)
    return p, al, g


def build(source, out_path):
    names = sorted(n for n in os.listdir(source) if n.lower().endswith(".png"))
    print(f"Scanning {len(names)} icons in {source}")

    records = []
    skipped_offgrid = 0
    skipped_name = 0
    t0 = time.time()

    for n in names:
        stem = os.path.splitext(n)[0]
        if not ID_RE.match(stem):
            skipped_name += 1
            continue
        r = thumbnail(os.path.join(source, n))
        if r is None:
            # tarkov.dev serves a 61x58 placeholder for items with no real grid
            # image. Nothing to match against, so drop them.
            skipped_offgrid += 1
            continue
        premult, alpha, (gw, gh) = r
        records.append((bytes.fromhex(stem), gw, gh, premult, alpha))

    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    with open(out_path, "wb") as fh:
        fh.write(MAGIC)
        fh.write(struct.pack("<III", VERSION, len(records), THUMB))
        for id_bytes, gw, gh, premult, alpha in records:
            fh.write(id_bytes)
            fh.write(struct.pack("<BB", gw, gh))
            fh.write(premult.tobytes())
            fh.write(alpha.tobytes())

    size = os.path.getsize(out_path)
    print(
        f"  {len(records)} icons indexed, "
        f"{skipped_offgrid} off-grid placeholders and {skipped_name} non-id files skipped"
    )
    print(f"  {out_path}  {size / 1e6:.2f} MB  in {time.time() - t0:.1f}s")

    # Bucket histogram: the matcher only ever compares within one grid size, so
    # the largest bucket is its worst case.
    from collections import Counter

    c = Counter((gw, gh) for _, gw, gh, _, _ in records)
    top = c.most_common(5)
    print("  largest buckets: " + ", ".join(f"{w}x{h}={n}" for (w, h), n in top))
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", help="folder of <itemid>.png icons")
    ap.add_argument(
        "--out",
        default=os.path.join(ROOT, "data", "icons.idx"),
        help="output index path",
    )
    args = ap.parse_args()

    source = args.source
    if not source:
        for cand in DEFAULT_SOURCES:
            if os.path.isdir(cand):
                source = cand
                break
    if not source or not os.path.isdir(source):
        print(
            "No icon folder found. Pass --source, or put icons in data/icons.\n"
            "Icons are '<tarkov.dev item id>.png' as served by gridImageLink.",
            file=sys.stderr,
        )
        return 1

    return build(source, args.out)


if __name__ == "__main__":
    raise SystemExit(main())
