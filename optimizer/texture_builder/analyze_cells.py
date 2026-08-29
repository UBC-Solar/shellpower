"""
Cell String Analyzer
----------------------
Reads an RGB PNG produced by the "Cell String Colorizer" style (or any image
following the same convention) and prints:
  - the total number of cells
  - the number of strings
  - the number of cells in each string, ordered left-to-right (or whichever
    direction is configured below)
  - the min and max number of pixels found in any single cell

A "cell" is a contiguous (4-connected, no diagonals), single-exact-color,
non-grayscale region. A "string" is the set of all cells that share the same
(R, G) channel values (B is expected to vary within a string and is ignored
when grouping).

Requires: pillow, numpy, scipy
    pip install pillow numpy scipy
"""

import numpy as np
from PIL import Image
from scipy import ndimage

# ============================== CONFIG ==============================

INPUT_PATH = r"C:\Users\Jonah\Documents\UBCSolar\2025\shellpower\optimizer\texture_builder\assets\split_2_2026-08-27_manualcolored.png"

# --- Ordering used to report/order STRINGS and cells within them -----
# A string's position is determined by its "top-left cell" (the cell within
# that string with the smallest sort key below), and strings are then
# ordered by that position.
# AXIS: 'x' (column) or 'y' (row) of a cell's upper-left bounding-box corner.
# ASCENDING: True = smaller values first (e.g. left-to-right / top-to-bottom).
#            False = larger values first (e.g. right-to-left / bottom-to-top).
# Change these if your image is rotated/flipped, matching whatever convention
# was used when the strings were originally built.
PRIMARY_AXIS = 'x'
PRIMARY_ASCENDING = True
SECONDARY_AXIS = 'y'          # tiebreaker
SECONDARY_ASCENDING = True

# =====================================================================


class Cell:
    __slots__ = ("pixels", "min_x", "min_y", "color", "size")

    def __init__(self, pixels, color):
        self.pixels = pixels
        self.color = color
        self.size = len(pixels)
        rows = [p[0] for p in pixels]
        cols = [p[1] for p in pixels]
        self.min_y = min(rows)
        self.min_x = min(cols)

    def sort_key(self, axis, ascending):
        val = self.min_x if axis == 'x' else self.min_y
        return val if ascending else -val

    def order_key(self):
        return (
            self.sort_key(PRIMARY_AXIS, PRIMARY_ASCENDING),
            self.sort_key(SECONDARY_AXIS, SECONDARY_ASCENDING),
        )


def find_cells(rgb_array):
    """Find all contiguous, same-exact-color, non-grayscale cells (4-connectivity)."""
    r = rgb_array[:, :, 0]
    g = rgb_array[:, :, 1]
    b = rgb_array[:, :, 2]

    grayscale_mask = (r == g) & (g == b)
    non_gray_mask = ~grayscale_mask

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


def group_into_strings(cells):
    """Group cells into strings by shared (R, G) value."""
    groups = {}
    for cell in cells:
        r, g, _b = cell.color
        key = (r, g)
        groups.setdefault(key, []).append(cell)
    return groups


def order_strings(groups):
    """Order strings by the position of each string's top-left-most cell."""
    ordered_keys = sorted(
        groups.keys(),
        key=lambda key: min(cell.order_key() for cell in groups[key]),
    )
    return [(key, groups[key]) for key in ordered_keys]


def main():
    img = Image.open(INPUT_PATH).convert("RGB")
    rgb_array = np.array(img)

    cells = find_cells(rgb_array)
    total_cells = len(cells)

    groups = group_into_strings(cells)
    ordered_strings = order_strings(groups)

    sizes = [cell.size for cell in cells]
    min_pixels = min(sizes) if sizes else 0
    max_pixels = max(sizes) if sizes else 0

    print(f"Number of cells: {total_cells}")
    print(f"Number of strings: {len(ordered_strings)}")
    print("Cells per string (left to right):")
    for (r, g), string_cells in ordered_strings:
        print(f"  (R={r}, G={g}): {len(string_cells)} cells")
    print(f"Min pixels in a cell: {min_pixels}")
    print(f"Max pixels in a cell: {max_pixels}")


if __name__ == "__main__":
    main()