#!/usr/bin/env python3
"""
pixel_flood_swap.py

A small Tkinter + Pillow tool: click two pixels in a PNG, and the
flood-filled regions touching those two pixels (like the "paint bucket"
tool) swap colors with each other.

Usage:
    python pixel_flood_swap.py [path/to/image.png]

If no path is given, a file picker opens on startup.

Controls:
    - Click a pixel        -> selects the first region (marked with a red circle)
    - Click another pixel  -> selects the second region and swaps the two colors
    - Tolerance slider      -> how strictly colors must match to be flood-filled
                                (0 = exact match only, useful for anti-aliased edges)
    - Undo / Reset / Save As buttons in the toolbar

Requires:
    pip install pillow numpy
"""

from __future__ import annotations

import sys
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

import numpy as np
from PIL import Image, ImageDraw, ImageTk

DISPLAY_MAX = 900  # largest dimension shown on screen, in pixels
DISPLAY_MIN = 400  # smallest dimension shown on screen (small images get upscaled)


def get_flood_mask(image: Image.Image, seed_xy, thresh: int = 0):
    """
    Compute the flood-fill region touching `seed_xy`, without modifying `image`.

    Returns (mask, color):
        mask  - boolean numpy array, shape (H, W), True for every pixel the
                flood fill would touch
        color - the original color tuple at seed_xy

    Implementation note: we run Pillow's flood fill on a scratch copy using a
    marker color, then diff the scratch copy against the original to recover
    which pixels changed. This means the real image is never touched until
    both regions (for both click points) are known, so the two flood fills
    can never bleed into each other or interfere with the swap.
    """
    color = image.getpixel(seed_xy)

    # Pick a marker color guaranteed not to equal the seed color, so the
    # flood fill is guaranteed to actually change something.
    if image.mode == "RGBA":
        marker = (255, 0, 255, 255) if color != (255, 0, 255, 255) else (0, 255, 255, 255)
    else:
        marker = (255, 0, 255) if color != (255, 0, 255) else (0, 255, 255)

    scratch = image.copy()
    ImageDraw.floodfill(scratch, seed_xy, marker, thresh=thresh)

    before = np.array(image)
    after = np.array(scratch)
    mask = np.any(before != after, axis=-1)
    return mask, color


def swap_flood_regions(image: Image.Image, p1, p2, thresh: int = 0) -> Image.Image:
    """Return a new image with the flood-fill regions at p1 and p2 swapped in color."""
    mask1, color1 = get_flood_mask(image, p1, thresh)
    mask2, color2 = get_flood_mask(image, p2, thresh)

    arr = np.array(image)
    result = arr.copy()
    result[mask1] = color2
    result[mask2] = color1
    return Image.fromarray(result, mode=image.mode)


class FloodSwapApp(tk.Frame):
    def __init__(self, master: tk.Tk, image_path: str | None = None):
        super().__init__(master)
        self.master = master
        self.pack(fill="both", expand=True)

        self.original_image: Image.Image | None = None  # untouched, full resolution
        self.current_image: Image.Image | None = None  # full resolution, edited
        self.display_scale = 1.0
        self.tk_image = None
        self.first_point = None  # (x, y) in image space, or None
        self.undo_stack: list[Image.Image] = []

        self._build_ui()

        if image_path:
            self.load_image(image_path)
        else:
            self.after(100, self.open_image)

    # ---------------------------------------------------------- UI setup --

    def _build_ui(self):
        toolbar = tk.Frame(self)
        toolbar.pack(side="top", fill="x")

        tk.Button(toolbar, text="Open...", command=self.open_image).pack(side="left", padx=4, pady=4)
        tk.Button(toolbar, text="Save As...", command=self.save_as).pack(side="left", padx=4, pady=4)
        tk.Button(toolbar, text="Undo", command=self.undo).pack(side="left", padx=4, pady=4)
        tk.Button(toolbar, text="Reset", command=self.reset).pack(side="left", padx=4, pady=4)

        tk.Label(toolbar, text="Tolerance:").pack(side="left", padx=(16, 2))
        self.tolerance_var = tk.IntVar(value=0)
        ttk.Scale(toolbar, from_=0, to=100, orient="horizontal",
                  variable=self.tolerance_var, length=120).pack(side="left")

        self.coord_var = tk.StringVar(value="")
        tk.Label(toolbar, textvariable=self.coord_var, anchor="e").pack(side="right", padx=8)

        self.canvas = tk.Canvas(self, bg="#444444", cursor="crosshair")
        self.canvas.pack(side="top", fill="both", expand=True)
        self.canvas.bind("<Button-1>", self.on_canvas_click)
        self.canvas.bind("<Motion>", self.on_canvas_motion)

        self.status_var = tk.StringVar(value="Open an image to begin.")
        tk.Label(self, textvariable=self.status_var, anchor="w").pack(side="bottom", fill="x", padx=4, pady=2)

    # ----------------------------------------------------- load / save ----

    def open_image(self):
        path = filedialog.askopenfilename(
            title="Open image",
            filetypes=[("PNG images", "*.png"), ("All images", "*.png *.gif *.bmp *.jpg *.jpeg"), ("All files", "*.*")],
        )
        if not path:
            if self.original_image is None:
                self.status_var.set("No image loaded. Click 'Open...' to choose a PNG.")
            return
        self.load_image(path)

    def load_image(self, path: str):
        try:
            img = Image.open(path).convert("RGBA")
        except Exception as exc:
            messagebox.showerror("Could not open image", str(exc))
            return

        self.original_image = img
        self.current_image = img.copy()
        self.undo_stack.clear()
        self.first_point = None
        self.master.title(f"Pixel Flood Swap - {path}")
        self._compute_display_scale()
        self.render()
        self.status_var.set("Click a pixel to select the first region.")

    def save_as(self):
        if self.current_image is None:
            return
        path = filedialog.asksaveasfilename(defaultextension=".png", filetypes=[("PNG image", "*.png")])
        if not path:
            return
        self.current_image.save(path)
        self.status_var.set(f"Saved to {path}")

    # -------------------------------------------------------- history -----

    def undo(self):
        if not self.undo_stack:
            self.status_var.set("Nothing to undo.")
            return
        self.current_image = self.undo_stack.pop()
        self.first_point = None
        self.render()
        self.status_var.set("Undid last swap. Click a pixel to select the first region.")

    def reset(self):
        if self.original_image is None:
            return
        self.undo_stack.clear()
        self.current_image = self.original_image.copy()
        self.first_point = None
        self.render()
        self.status_var.set("Reset to original image.")

    # -------------------------------------------------------- display -----

    def _compute_display_scale(self):
        w, h = self.current_image.size
        longest = max(w, h)
        scale = 1.0
        if longest > DISPLAY_MAX:
            scale = DISPLAY_MAX / longest
        elif longest < DISPLAY_MIN:
            scale = DISPLAY_MIN / longest
        self.display_scale = scale

    def render(self):
        w, h = self.current_image.size
        disp_w = max(1, round(w * self.display_scale))
        disp_h = max(1, round(h * self.display_scale))
        # NEAREST keeps pixel edges crisp so clicks map to exact pixels,
        # whether the image is being scaled up or down for display.
        display_img = self.current_image.resize((disp_w, disp_h), Image.NEAREST)
        self.tk_image = ImageTk.PhotoImage(display_img)

        self.canvas.delete("all")
        self.canvas.config(width=disp_w, height=disp_h)
        self.canvas.create_image(0, 0, anchor="nw", image=self.tk_image, tags="bg")
        if self.first_point is not None:
            self._draw_marker(self.first_point)

    def _draw_marker(self, image_xy):
        x, y = image_xy
        cx = (x + 0.5) * self.display_scale
        cy = (y + 0.5) * self.display_scale
        r = 5
        self.canvas.create_oval(cx - r, cy - r, cx + r, cy + r, outline="red", width=2)

    def _canvas_to_image_xy(self, event):
        x = int(event.x / self.display_scale)
        y = int(event.y / self.display_scale)
        w, h = self.current_image.size
        x = max(0, min(w - 1, x))
        y = max(0, min(h - 1, y))
        return x, y

    # ----------------------------------------------------- interaction ----

    def on_canvas_motion(self, event):
        if self.current_image is None:
            return
        x, y = self._canvas_to_image_xy(event)
        color = self.current_image.getpixel((x, y))
        self.coord_var.set(f"({x}, {y})  {color}")

    def on_canvas_click(self, event):
        if self.current_image is None:
            return
        xy = self._canvas_to_image_xy(event)

        if self.first_point is None:
            self.first_point = xy
            self.render()
            self.status_var.set(f"First region: pixel {xy}. Now click a pixel in the second region.")
            return

        first_point, second_point = self.first_point, xy
        self.first_point = None

        if first_point == second_point:
            self.render()
            self.status_var.set("Same pixel clicked twice - pick two different pixels.")
            return

        self.undo_stack.append(self.current_image.copy())
        thresh = self.tolerance_var.get()
        try:
            self.current_image = swap_flood_regions(self.current_image, first_point, second_point, thresh)
        except Exception as exc:
            self.undo_stack.pop()
            messagebox.showerror("Swap failed", str(exc))
            self.render()
            return

        self.render()
        self.status_var.set(
            f"Swapped colors of regions at {first_point} and {second_point}. Click to select a new pair."
        )


def main():
    image_path = sys.argv[1] if len(sys.argv) > 1 else None
    root = tk.Tk()
    root.title("Pixel Flood Swap")
    FloodSwapApp(root, image_path)
    root.mainloop()


if __name__ == "__main__":
    main()