import os
import re
from pathlib import Path
from PIL import Image
from tqdm import tqdm


def natural_sort_key(filename):
    return [
        int(text) if text.isdigit() else text.lower()
        for text in re.split(r"(\d+)", filename)
    ]


def create_gif(
    folder_path, output_name="texture_animation.gif", fps=10, max_width=None
):
    base_path = Path(folder_path)
    frame_duration = int(1000 / fps)
    pattern = re.compile(r"^texture_\d+\.(png|jpg|jpeg|bmp)$", re.IGNORECASE)

    files = [f for f in os.listdir(base_path) if pattern.match(f)]
    files.sort(key=natural_sort_key)

    if not files:
        print(f"No files found in {base_path}")
        return

    images = []

    # --- PHASE 1: LOADING & RESIZING ---
    for filename in tqdm(files, desc="Loading/Resizing", unit="frame"):
        img = Image.open(base_path / filename).convert("RGBA")

        # Downscale if max_width is set and image is wider than that
        if max_width and img.width > max_width:
            # Calculate height to maintain aspect ratio
            ratio = max_width / float(img.width)
            new_height = int(float(img.height) * ratio)
            img = img.resize((max_width, new_height), Image.Resampling.LANCZOS)

        images.append(img)

    # --- PHASE 2: STITCHING & SAVING ---
    # We use a progress bar here to monitor the save process
    print(f"Stitching {len(images)} frames...")

    # Note: 'optimize=False' speeds up the save time significantly
    images[0].save(
        output_name,
        save_all=True,
        append_images=images[1:],
        duration=frame_duration,
        loop=0,
        optimize=False,
    )
    print(f"\nSuccess! GIF saved: {output_name}")


if __name__ == "__main__":
    # CONFIGURATION
    FOLDER = r"C:\Users\Jonah\Documents\UBCSolar\2025\shellpower\shellpower\outputs\2026-02-19_23h19m24s"
    FPS = 50 # Max allowed: 50FPS
    MAX_W = 512  # Set to None to keep original size

    create_gif(FOLDER, fps=FPS, max_width=MAX_W)
