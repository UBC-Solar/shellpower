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
IMAGE_PATH = r"C:\Users\Jonah\Documents\UBCSolar\2025\shellpower\optimizer\texture_builder\assets\tmp1.png"

# The image will be scaled to fit within this display size
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


class ImageEditor:
    def __init__(self, root, image_path):
        self.root = root
        self.root.title("Bucket Fill Editor")
        self.root.configure(bg="#1e1e2e")

        # Load image, strip alpha
        src = Image.open(image_path).convert("RGB")
        self.orig_w, self.orig_h = src.size

        # Compute display scale so image fits within DISPLAY_MAX bounds
        scale = min(DISPLAY_MAX_W / self.orig_w, DISPLAY_MAX_H / self.orig_h, 1.0)
        self.scale = scale
        self.disp_w = max(1, int(self.orig_w * scale))
        self.disp_h = max(1, int(self.orig_h * scale))

        # Working pixel buffer at ORIGINAL resolution (flat bytearray for fast access)
        self.pixels = bytearray(src.tobytes())   # length = orig_w * orig_h * 3

        # Undo stack — each entry is (pixels_copy, fill_b, rg_fill_counts_copy)
        self.undo_stack = []

        # State
        self.rg_index       = 0
        self.fill_b         = 0
        self.rg_fill_counts = [0] * len(RG_PAIRS)

        # ── UI layout ─────────────────────────────────────────────────────────
        toolbar = tk.Frame(root, bg="#1e1e2e", pady=6, padx=8)
        toolbar.pack(side=tk.TOP, fill=tk.X)

        btn_style = dict(
            bg="#313244", fg="#cdd6f4",
            activebackground="#45475a", activeforeground="#cdd6f4",
            relief=tk.FLAT, font=("Courier New", 10, "bold"),
            padx=12, pady=4, cursor="hand2"
        )

        tk.Button(toolbar, text="⟳  Reset",        command=self.reset,      **btn_style).pack(side=tk.LEFT, padx=4)
        tk.Button(toolbar, text="⇄  Change RG",    command=self.change_rg,  **btn_style).pack(side=tk.LEFT, padx=4)
        tk.Button(toolbar, text="✕  Reset Blue B", command=self.reset_blue, **btn_style).pack(side=tk.LEFT, padx=4)
        tk.Button(toolbar, text="↩  Undo",         command=self.undo,       **btn_style).pack(side=tk.LEFT, padx=4)
        tk.Button(toolbar, text="💾  Save As",     command=self.save_as,    **btn_style).pack(side=tk.LEFT, padx=4)

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

    # ── Internal helpers ──────────────────────────────────────────────────────

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

    def _update_status(self):
        r, g = RG_PAIRS[self.rg_index]
        self.status_var.set(
            f"Fill RGB: ({r}, {g}, {self.fill_b})   "
            f"RG preset: {self.rg_index+1}/{len(RG_PAIRS)}   "
            f"Scale: {self.scale:.2f}×"
        )

    def _update_count_labels(self):
        for i, lbl in enumerate(self.count_labels):
            active = (i == self.rg_index)
            lbl.config(
                text=self._count_label_text(i),
                fg="#a6e3a1" if active else "#cdd6f4",
                bg="#313244" if active else "#181825",
            )

    def _canvas_to_image_coords(self, event):
        cx = int(self.canvas.canvasx(event.x))
        cy = int(self.canvas.canvasy(event.y))
        x  = int(cx / self.scale)
        y  = int(cy / self.scale)
        return x, y

    def _save_undo(self):
        self.undo_stack.append((
            bytearray(self.pixels),
            self.fill_b,
            list(self.rg_fill_counts),
        ))

    # ── Button callbacks ──────────────────────────────────────────────────────

    def reset(self):
        """Turn all non-grayscale pixels to (0, 0, 255) and clear fill counts."""
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
        """Cycle to the next RG pair."""
        self.rg_index = (self.rg_index + 1) % len(RG_PAIRS)
        self._update_status()
        self._update_count_labels()

    def reset_blue(self):
        """Reset the running B counter to 0."""
        self.fill_b = 0
        self._update_status()

    def undo(self):
        """Restore the previous pixel state."""
        if not self.undo_stack:
            return
        pixels_snap, fill_b_snap, counts_snap = self.undo_stack.pop()
        self.pixels         = pixels_snap
        self.fill_b         = fill_b_snap
        self.rg_fill_counts = counts_snap
        self._refresh_display()
        self._update_status()
        self._update_count_labels()

    def save_as(self):
        """Save the current full-resolution image to a user-chosen PNG file."""
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

    # ── Click / fill ──────────────────────────────────────────────────────────

    def on_click(self, event):
        x, y = self._canvas_to_image_coords(event)
        if x < 0 or x >= self.orig_w or y < 0 or y >= self.orig_h:
            return

        idx    = (y * self.orig_w + x) * 3
        target = (self.pixels[idx], self.pixels[idx+1], self.pixels[idx+2])
        r, g   = RG_PAIRS[self.rg_index]
        fill   = (r, g, self.fill_b)

        self._save_undo()
        changed = flood_fill(self.pixels, self.orig_w, self.orig_h, x, y, target, fill)

        if changed:
            self.fill_b += 1
            self.rg_fill_counts[self.rg_index] += 1
            self._refresh_display()
            self._update_status()
            self._update_count_labels()
        else:
            self.undo_stack.pop()   # nothing changed, discard snapshot


def main():
    root = tk.Tk()
    root.geometry("1500x800")
    app = ImageEditor(root, IMAGE_PATH)
    root.mainloop()


if __name__ == "__main__":
    main()
