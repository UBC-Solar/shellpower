"""
Image processing script with bucket-fill GUI.
Set IMAGE_PATH below to your PNG file before running.

Requirements:
    pip install pillow
"""

import tkinter as tk
from tkinter import ttk, filedialog
from PIL import Image, ImageTk
from collections import deque

# ── Configuration ─────────────────────────────────────────────────────────────
IMAGE_PATH = r"C:\Users\Jonah\Documents\UBCSolar\2025\shellpower\optimizer\texture_builder\assets\split_2_2026-08-27_autocolored.png"

DISPLAY_MAX_W = 1400
DISPLAY_MAX_H = 600

RG_PAIRS = [
    (255, 127),
    (127,   0),
    (0,   127),
    (127, 255),
]
# ──────────────────────────────────────────────────────────────────────────────


def is_grayscale(r, g, b):
    return r == g == b


def flood_fill(pixels, width, height, sx, sy, target_rgb, fill_rgb):
    """4-connected flood fill on a flat RGB bytearray. Returns True if any pixel changed."""
    if target_rgb == fill_rgb:
        return False
    stack = deque()
    stack.append((sx, sy))
    visited = set()
    changed = False
    while stack:
        x, y = stack.pop()
        if (x, y) in visited:
            continue
        if x < 0 or x >= width or y < 0 or y >= height:
            continue
        idx = (y * width + x) * 3
        if (pixels[idx], pixels[idx+1], pixels[idx+2]) != target_rgb:
            continue
        visited.add((x, y))
        pixels[idx]   = fill_rgb[0]
        pixels[idx+1] = fill_rgb[1]
        pixels[idx+2] = fill_rgb[2]
        changed = True
        stack.append((x+1, y))
        stack.append((x-1, y))
        stack.append((x,   y+1))
        stack.append((x,   y-1))
    return changed


def find_free_b(pixels, target_r, target_g):
    """Return the smallest B value (0-255) not used by any pixel with (target_r, target_g)."""
    used = set()
    for i in range(0, len(pixels), 3):
        if pixels[i] == target_r and pixels[i+1] == target_g:
            used.add(pixels[i+2])
    for b in range(256):
        if b not in used:
            return b
    return None  # all 256 values taken (extremely unlikely)


class ImageEditor:
    def __init__(self, root, image_path):
        self.root = root
        self.root.title("Bucket Fill Editor")
        self.root.configure(bg="#1e1e2e")

        src = Image.open(image_path).convert("RGB")
        self.orig_w, self.orig_h = src.size

        scale = min(DISPLAY_MAX_W / self.orig_w, DISPLAY_MAX_H / self.orig_h, 1.0)
        self.scale = scale
        self.disp_w = max(1, int(self.orig_w * scale))
        self.disp_h = max(1, int(self.orig_h * scale))

        self.pixels = bytearray(src.tobytes())
        self.undo_stack = []

        self.rg_index       = 0
        self.fill_b         = 0
        self.rg_fill_counts = [0] * len(RG_PAIRS)

        # Pick-RG mode state
        self.pick_rg_mode   = False   # waiting for the pick click
        self.picked_rg      = None    # (r, g) picked from image, or None = use preset

        # ── UI ────────────────────────────────────────────────────────────────
        toolbar = tk.Frame(root, bg="#1e1e2e", pady=6, padx=8)
        toolbar.pack(side=tk.TOP, fill=tk.X)

        btn_style = dict(
            bg="#313244", fg="#cdd6f4",
            activebackground="#45475a", activeforeground="#cdd6f4",
            relief=tk.FLAT, font=("Courier New", 10, "bold"),
            padx=12, pady=4, cursor="hand2"
        )
        active_style = {**btn_style, "bg": "#89b4fa", "fg": "#1e1e2e",
                        "activebackground": "#74a8f5", "activeforeground": "#1e1e2e"}

        tk.Button(toolbar, text="⟳  Reset",        command=self.reset,      **btn_style).pack(side=tk.LEFT, padx=4)
        tk.Button(toolbar, text="⇄  Change RG",    command=self.change_rg,  **btn_style).pack(side=tk.LEFT, padx=4)
        tk.Button(toolbar, text="✕  Reset Blue B", command=self.reset_blue, **btn_style).pack(side=tk.LEFT, padx=4)
        tk.Button(toolbar, text="↩  Undo",         command=self.undo,       **btn_style).pack(side=tk.LEFT, padx=4)
        tk.Button(toolbar, text="💾  Save As",     command=self.save_as,    **btn_style).pack(side=tk.LEFT, padx=4)

        # Pick RG button — kept as reference so we can restyle it
        self._btn_style      = btn_style
        self._active_style   = active_style
        self.pick_btn = tk.Button(toolbar, text="🎯  Pick RG",
                                  command=self.toggle_pick_rg, **btn_style)
        self.pick_btn.pack(side=tk.LEFT, padx=4)

        self.status_var = tk.StringVar()
        tk.Label(toolbar, textvariable=self.status_var,
                 bg="#1e1e2e", fg="#a6e3a1",
                 font=("Courier New", 10)).pack(side=tk.RIGHT, padx=8)

        # ── Fill-count table ──────────────────────────────────────────────────
        counts_frame = tk.Frame(root, bg="#181825", pady=5, padx=10)
        counts_frame.pack(side=tk.TOP, fill=tk.X)

        tk.Label(counts_frame, text="Fills since reset →",
                 bg="#181825", fg="#7f849c",
                 font=("Courier New", 9, "bold")).pack(side=tk.LEFT, padx=(0, 10))

        self.count_labels = []
        for i in range(len(RG_PAIRS)):
            lbl = tk.Label(counts_frame,
                           text=self._count_label_text(i),
                           bg="#181825", fg="#cdd6f4",
                           font=("Courier New", 9),
                           padx=8, pady=2)
            lbl.pack(side=tk.LEFT, padx=3)
            self.count_labels.append(lbl)

        # Picked-RG indicator (hidden until a pick is active)
        self.picked_label = tk.Label(counts_frame, text="",
                                     bg="#181825", fg="#f38ba8",
                                     font=("Courier New", 9, "bold"), padx=8, pady=2)
        self.picked_label.pack(side=tk.LEFT, padx=(12, 3))

        # ── Canvas ────────────────────────────────────────────────────────────
        frame = tk.Frame(root, bg="#11111b")
        frame.pack(fill=tk.BOTH, expand=True)

        self.canvas = tk.Canvas(frame, bg="#11111b", cursor="crosshair",
                                highlightthickness=0,
                                width=self.disp_w, height=self.disp_h)
        hbar = ttk.Scrollbar(frame, orient=tk.HORIZONTAL, command=self.canvas.xview)
        vbar = ttk.Scrollbar(frame, orient=tk.VERTICAL,   command=self.canvas.yview)
        self.canvas.configure(xscrollcommand=hbar.set, yscrollcommand=vbar.set)

        hbar.pack(side=tk.BOTTOM, fill=tk.X)
        vbar.pack(side=tk.RIGHT,  fill=tk.Y)
        self.canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)

        self.canvas.configure(scrollregion=(0, 0, self.disp_w, self.disp_h))
        self.canvas_image_id = self.canvas.create_image(0, 0, anchor=tk.NW)

        self.canvas.bind("<Button-1>", self.on_click)
        self.canvas.bind("<MouseWheel>",       lambda e: self.canvas.yview_scroll(-1*(e.delta//120), "units"))
        self.canvas.bind("<Shift-MouseWheel>", lambda e: self.canvas.xview_scroll(-1*(e.delta//120), "units"))

        self._refresh_display()
        self._update_status()
        self._update_count_labels()

    # ── Helpers ───────────────────────────────────────────────────────────────

    def _count_label_text(self, i):
        r, g = RG_PAIRS[i]
        return f"#{i+1} ({r},{g}): {self.rg_fill_counts[i]}"

    def _pixels_to_tkimage(self):
        img = Image.frombytes("RGB", (self.orig_w, self.orig_h), bytes(self.pixels))
        if self.scale < 1.0:
            img = img.resize((self.disp_w, self.disp_h), Image.BILINEAR)
        return ImageTk.PhotoImage(img)

    def _refresh_display(self):
        self._tkimg = self._pixels_to_tkimage()
        self.canvas.itemconfig(self.canvas_image_id, image=self._tkimg)

    def _current_rg(self):
        """Return the (r, g) pair currently in use for fills."""
        if self.picked_rg is not None:
            return self.picked_rg
        return RG_PAIRS[self.rg_index]

    def _update_status(self):
        r, g = self._current_rg()
        if self.pick_rg_mode:
            msg = "🎯 Click a pixel to pick its RG…"
        elif self.picked_rg is not None:
            b = find_free_b(self.pixels, r, g)
            b_str = str(b) if b is not None else "?"
            msg = (f"Picked RG: ({r}, {g})  next B: {b_str}   "
                   f"Scale: {self.scale:.2f}×")
        else:
            msg = (f"Fill RGB: ({r}, {g}, {self.fill_b})   "
                   f"RG preset: {self.rg_index+1}/{len(RG_PAIRS)}   "
                   f"Scale: {self.scale:.2f}×")
        self.status_var.set(msg)

    def _update_count_labels(self):
        for i, lbl in enumerate(self.count_labels):
            active = (i == self.rg_index) and self.picked_rg is None
            lbl.config(
                text=self._count_label_text(i),
                fg="#a6e3a1" if active else "#cdd6f4",
                bg="#313244" if active else "#181825",
            )
        # Picked-RG indicator
        if self.picked_rg is not None:
            r, g = self.picked_rg
            self.picked_label.config(text=f"★ picked ({r},{g})")
        else:
            self.picked_label.config(text="")

    def _canvas_to_image_coords(self, event):
        cx = int(self.canvas.canvasx(event.x))
        cy = int(self.canvas.canvasy(event.y))
        return int(cx / self.scale), int(cy / self.scale)

    def _save_undo(self):
        self.undo_stack.append((
            bytearray(self.pixels),
            self.fill_b,
            list(self.rg_fill_counts),
            self.picked_rg,
        ))

    # ── Button callbacks ──────────────────────────────────────────────────────

    def reset(self):
        self._save_undo()
        p = self.pixels
        for i in range(0, len(p), 3):
            r, g, b = p[i], p[i+1], p[i+2]
            if not is_grayscale(r, g, b):
                p[i]   = 0
                p[i+1] = 0
                p[i+2] = 255
        self.rg_fill_counts = [0] * len(RG_PAIRS)
        self._refresh_display()
        self._update_status()
        self._update_count_labels()

    def change_rg(self):
        self.rg_index = (self.rg_index + 1) % len(RG_PAIRS)
        self.picked_rg = None          # clear any picked RG when switching presets
        self.picked_label.config(text="")
        self._update_pick_btn()
        self._update_status()
        self._update_count_labels()

    def reset_blue(self):
        self.fill_b = 0
        self._update_status()

    def undo(self):
        if not self.undo_stack:
            return
        pixels_snap, fill_b_snap, counts_snap, picked_snap = self.undo_stack.pop()
        self.pixels         = pixels_snap
        self.fill_b         = fill_b_snap
        self.rg_fill_counts = counts_snap
        self.picked_rg      = picked_snap
        self._refresh_display()
        self._update_status()
        self._update_count_labels()

    def save_as(self):
        path = filedialog.asksaveasfilename(
            defaultextension=".png",
            filetypes=[("PNG image", "*.png"), ("All files", "*.*")],
            title="Save image as…",
        )
        if not path:
            return
        img = Image.frombytes("RGB", (self.orig_w, self.orig_h), bytes(self.pixels))
        img.save(path)
        self.status_var.set(f"Saved → {path}")

    def toggle_pick_rg(self):
        """Enter or cancel pick-RG mode."""
        self.pick_rg_mode = not self.pick_rg_mode
        self._update_pick_btn()
        self._update_status()

    def _update_pick_btn(self):
        if self.pick_rg_mode:
            for k, v in self._active_style.items():
                self.pick_btn.config(**{k: v})
        else:
            for k, v in self._btn_style.items():
                self.pick_btn.config(**{k: v})

    # ── Click handler ─────────────────────────────────────────────────────────

    def on_click(self, event):
        x, y = self._canvas_to_image_coords(event)
        if x < 0 or x >= self.orig_w or y < 0 or y >= self.orig_h:
            return

        idx = (y * self.orig_w + x) * 3
        pr, pg, pb = self.pixels[idx], self.pixels[idx+1], self.pixels[idx+2]

        # ── Pick-RG mode: sample the clicked pixel's RG ───────────────────
        if self.pick_rg_mode:
            self.picked_rg    = (pr, pg)
            self.pick_rg_mode = False
            self._update_pick_btn()
            self._update_status()
            self._update_count_labels()
            return

        # ── Normal fill ───────────────────────────────────────────────────
        target = (pr, pg, pb)
        r, g   = self._current_rg()

        if self.picked_rg is not None:
            # Use the smallest free B for these R,G values
            b = find_free_b(self.pixels, r, g)
            if b is None:
                self.status_var.set("All 256 B values are used for this RG — cannot fill.")
                return
            fill = (r, g, b)
        else:
            fill = (r, g, self.fill_b)

        self._save_undo()
        changed = flood_fill(self.pixels, self.orig_w, self.orig_h, x, y, target, fill)

        if changed:
            if self.picked_rg is None:
                self.fill_b += 1
            self.rg_fill_counts[self.rg_index] += 1
            self._refresh_display()
            self._update_status()
            self._update_count_labels()
        else:
            self.undo_stack.pop()


def main():
    root = tk.Tk()
    root.geometry("1500x800")
    app = ImageEditor(root, IMAGE_PATH)
    root.mainloop()


if __name__ == "__main__":
    main()