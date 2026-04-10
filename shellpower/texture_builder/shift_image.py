from PIL import Image, ImageOps
from pathlib import Path

"""
Helper script to pad the images from cascadia_texture_building_(V2) to help align them with the
3d model as loaded in Shellpower.
"""

if __name__ == "__main__":

    # Configuration
    input_img_path = Path(
        r"C:\Users\Jonah\Documents\UBCSolar\2025\shellpower\arrays\v4\cascadia_final_with_strings_fixed.png"
    )

    # Pixel shifts (edit these)
    pad_top: int = 10
    pad_left: int = 5

    out_name: str = f'padded_{input_img_path.name}'
    out_dir: Path = Path(__file__).parent / 'assets'
    out_dir.mkdir(parents=True, exist_ok=True)
    out_path = out_dir / out_name

    # Processing
    try:
        with Image.open(input_img_path) as img:
            border = (pad_left, pad_top, 0, 0)
            new_img = ImageOps.expand(img, border=border, fill=(255, 255, 255)) # 0 is black/transparent

            new_img.save(out_path)
            print(f"Success! Image saved to: {out_path}")
            print(f"New dimensions: {new_img.size}")

    except FileNotFoundError:
        print(f"Error: Could not find the image at {input_img_path}")
    except Exception as e:
        print(f"An error occurred: {e}")