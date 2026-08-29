"""
Cell String Colorizer
----------------------
Finds contiguous, single-color, non-grayscale "cells" in an RGB PNG,
splits them into 4 "strings" (as evenly as possible), and recolors every
cell so that:
  - all cells in the same string share the same R and G channel value
    (taken from RG_PAIRS below)
  - every cell within a string gets a unique, sequential B value

Requires: pillow, numpy, scipy
    pip install pillow numpy scipy
"""

import numpy as np
from PIL import Image
from scipy import ndimage

# ============================== CONFIG ==============================

INPUT_PATH = r"C:\Users\Jonah\Documents\UBCSolar\2025\shellpower\optimizer\texture_builder\assets\split_2_2026-08-27.png"
OUTPUT_PATH = r"C:\Users\Jonah\Documents\UBCSolar\2025\shellpower\optimizer\texture_builder\assets\split_2_2026-08-27_autocolored.png"

# (R, G) pair used for each string, in string order. Must have at least
# as many entries as the number of strings you're splitting cells into
# (4, matching the "divide into 4 integers" step).
RG_PAIRS = [(255, 127), (127, 0), (127, 255), (63, 0)]

# --- Ordering used to GROUP cells into strings -----------------------
# Cells are globally sorted by (PRIMARY axis, SECONDARY axis as tiebreak),
# then split into consecutive chunks; chunk 0 becomes string 0, etc.
# AXIS: 'x' (column) or 'y' (row) of the cell's upper-left bounding-box corner.
# ASCENDING: True = smaller values first (e.g. left-to-right / top-to-bottom).
#            False = larger values first (e.g. right-to-left / bottom-to-top).
# For a normal image (origin top-left), left-to-right = x ascending.
# If your image is rotated/flipped, change these two pairs accordingly, e.g.:
#   right-to-left -> GROUP_PRIMARY_AXIS='x', GROUP_PRIMARY_ASCENDING=False
#   top-to-bottom -> GROUP_PRIMARY_AXIS='y', GROUP_PRIMARY_ASCENDING=True
GROUP_PRIMARY_AXIS = 'x'
GROUP_PRIMARY_ASCENDING = True
GROUP_SECONDARY_AXIS = 'y'
GROUP_SECONDARY_ASCENDING = True

# --- Ordering used to assign B VALUES within a string -----------------
# Independent of the grouping order above, so you could e.g. group cells
# left-to-right into strings, but still assign B values top-to-bottom
# within each string. Defaults to "left-to-right then top-to-bottom".
BVALUE_PRIMARY_AXIS = 'x'
BVALUE_PRIMARY_ASCENDING = True
BVALUE_SECONDARY_AXIS = 'y'
BVALUE_SECONDARY_ASCENDING = True

# =====================================================================


class Cell:
    __slots__ = ("pixels", "min_x", "min_y", "color")

    def __init__(self, pixels, color):
        # pixels: list of (row, col) tuples belonging to this cell
        self.pixels = pixels
        self.color = color
        rows = [p[0] for p in pixels]
        cols = [p[1] for p in pixels]
        # "upper left corner" proxy = bounding box top-left
        self.min_y = min(rows)
        self.min_x = min(cols)

    def sort_key(self, axis, ascending):
        val = self.min_x if axis == 'x' else self.min_y
        return val if ascending else -val


def find_cells(rgb_array):
    """Find all contiguous, same-exact-color, non-grayscale cells (4-connectivity)."""
    r = rgb_array[:, :, 0]
    g = rgb_array[:, :, 1]
    b = rgb_array[:, :, 2]

    grayscale_mask = (r == g) & (g == b)
    non_gray_mask = ~grayscale_mask

    # Pack each pixel's color into a single int for fast unique-color lookup
    packed = (r.astype(np.int64) << 16) | (g.astype(np.int64) << 8) | b.astype(np.int64)

    unique_colors = np.unique(packed[non_gray_mask])

    structure = np.array([[0, 1, 0],
                           [1, 1, 1],
                           [0, 1, 0]])  # 4-connectivity only (no diagonals)

    cells = []
    for color_val in unique_colors:
        color_val = int(color_val)
        color_mask = (packed == color_val) & non_gray_mask
        labeled, num_features = ndimage.label(color_mask, structure=structure)
        if num_features == 0:
            continue

        rr = (color_val >> 16) & 0xFF
        gg = (color_val >> 8) & 0xFF
        bb = color_val & 0xFF

        for label_id in range(1, num_features + 1):
            coords = np.argwhere(labeled == label_id)
            pixels = [(int(row), int(col)) for row, col in coords]
            cells.append(Cell(pixels, (rr, gg, bb)))

    return cells


def split_counts(total, n):
    """Split `total` into `n` integers as evenly as possible.
    Any remainder is added one-at-a-time to the LAST entries,
    e.g. split_counts(386, 4) -> [96, 96, 97, 97]."""
    base = total // n
    remainder = total % n
    sizes = [base] * n
    for i in range(n - remainder, n):
        sizes[i] += 1
    return sizes


def group_into_strings(cells, sizes):
    ordered = sorted(
        cells,
        key=lambda c: (
            c.sort_key(GROUP_PRIMARY_AXIS, GROUP_PRIMARY_ASCENDING),
            c.sort_key(GROUP_SECONDARY_AXIS, GROUP_SECONDARY_ASCENDING),
        ),
    )
    strings = []
    idx = 0
    for size in sizes:
        strings.append(ordered[idx: idx + size])
        idx += size
    return strings


def colorize(rgb_array, strings):
    if len(strings) > len(RG_PAIRS):
        raise ValueError(
            f"{len(strings)} strings but only {len(RG_PAIRS)} RG_PAIRS provided."
        )

    for string_idx, cells in enumerate(strings):
        r_val, g_val = RG_PAIRS[string_idx]

        ordered_cells = sorted(
            cells,
            key=lambda c: (
                c.sort_key(BVALUE_PRIMARY_AXIS, BVALUE_PRIMARY_ASCENDING),
                c.sort_key(BVALUE_SECONDARY_AXIS, BVALUE_SECONDARY_ASCENDING),
            ),
        )

        if len(ordered_cells) > 256:
            raise ValueError(
                f"String {string_idx} has {len(ordered_cells)} cells, which "
                "exceeds the max of 256 (B value must fit in a single byte)."
            )

        for b_val, cell in enumerate(ordered_cells):
            for row, col in cell.pixels:
                rgb_array[row, col, 0] = r_val
                rgb_array[row, col, 1] = g_val
                rgb_array[row, col, 2] = b_val


def main():
    img = Image.open(INPUT_PATH).convert("RGB")
    rgb_array = np.array(img)

    cells = find_cells(rgb_array)
    total_cells = len(cells)
    print(f"Found {total_cells} cells")

    sizes = split_counts(total_cells, len(RG_PAIRS))
    print(f"String sizes ({len(sizes)} strings): {sizes}")

    strings = group_into_strings(cells, sizes)

    colorize(rgb_array, strings)

    out_img = Image.fromarray(rgb_array, mode="RGB")
    out_img.save(OUTPUT_PATH)
    print(f"Saved output to {OUTPUT_PATH}")


if __name__ == "__main__":
    main()