from shellpower import ArraySimulatorInput
from shellpower.simulator.Simulation import Simulation

from config import BRAINERD_TEST_CASES, BRAINERD_TEST_CASE_NAMES
from array_handler import ArrayHandler
import matplotlib.pyplot as plt
from pathlib import Path
import numpy as np
import datetime
import logging
import time


logger = logging.getLogger(__name__)


# ----------------------------- Configuration ------------------------------

# Folder containing the texture_*.png files to compare
TEXTURES_DIR: Path = Path(
    r"C:\Users\Jonah\Documents\UBCSolar\2025\shellpower\optimizer\inputs\custom_tests_2"
)

# Texture used only to construct the ArraySpec (layout/geometry reference,
# not evaluated itself). Every file in TEXTURES_DIR is passed directly to
# ArraySimulatorInput instead, so this is just whatever array layout exists.
BASE_TEXTURE_PATH: Path = Path(
    r"C:\Users\Jonah\Documents\UBCSolar\2025\shellpower\optimizer\outputs\2026-06-20_11h48m34s\texture_228.png"
)

MAX_STRING_CELLS: int = 98
MIN_STRING_CELLS: int = 95

PROJECT_ROOT: Path = Path(__file__).parent.parent
INPUTS_DIR: Path = PROJECT_ROOT / "optimizer" / "inputs"
TOP_SHELL_MODEL: Path = INPUTS_DIR / "v4-ep9-guillotined-ascii.stl"
BYPASS_DIODES_JSON: Path = INPUTS_DIR / "bypass_diodes.json"

# ---------------------------------------------------------------------------


# autoclicker shenanigans
import win32gui
import win32con
def try_click_window(title: str):
    hwnd = win32gui.FindWindow(None, title)
    if hwnd == 0:
        logger.warning(f"Window '{title}' not found")
    else:
        win32gui.PostMessage(hwnd, win32con.WM_MOUSEMOVE, 0, 0)


def evaluate_texture(handler: ArrayHandler, texture_path: Path, top_shell_model: Path) -> float:
    """Run all Brainerd test cases against a single texture and return the average power (W)."""
    case_powers = np.zeros(len(BRAINERD_TEST_CASES))

    for i, (case, name) in enumerate(zip(BRAINERD_TEST_CASES, BRAINERD_TEST_CASE_NAMES)):

        # DIRTY FIX
        # On my desktop PC, it slows down greatly (0.7s -> 9s) per run when the window hasn't been clicked in ~5s
        try_click_window("OpenTK Window")

        simulator_input = ArraySimulatorInput(
            **case,
            LayoutTexturePath=texture_path,
            MeshPath=str(top_shell_model),
        )

        logger.debug(f"Simulating test case {name} for {texture_path.name}...")
        power = handler.get_watts(simulator_input)
        logger.debug(f"Estimated {power} W for {name}...")

        case_powers[i] = power

    return case_powers.mean()


def compare_textures(output_dir: Path):

    texture_paths = sorted(p for p in TEXTURES_DIR.glob("*.png") if p.is_file())
    if not texture_paths:
        logger.warning(f"No .png files found in {TEXTURES_DIR}")
        return

    logger.info(f"Found {len(texture_paths)} texture(s) in {TEXTURES_DIR}")

    # The ArraySpec/ArrayHandler just needs to exist so we can call get_watts();
    # its internal array layout is irrelevant here since every texture path is
    # passed directly to ArraySimulatorInput rather than read from the handler.
    aspec: object = Simulation.ArraySpec(
        str(BASE_TEXTURE_PATH),
        str(TOP_SHELL_MODEL),
        str(BYPASS_DIODES_JSON),
    )
    handler: ArrayHandler = ArrayHandler(aspec, MAX_STRING_CELLS, MIN_STRING_CELLS)

    results: list[tuple[str, float]] = []

    for texture_path in texture_paths:
        iter_start = time.perf_counter()
        avg_power = evaluate_texture(handler, texture_path, TOP_SHELL_MODEL)
        iter_duration = time.perf_counter() - iter_start

        logger.info(f"{texture_path.name}: Average Power = {avg_power:.4f} W | Eval Time: {iter_duration:.2f}s")
        results.append((texture_path.name, avg_power))

    # Sort best (highest power) first
    results.sort(key=lambda r: r[1], reverse=True)

    logger.info("=== Ranked results (best first) ===")
    for rank, (name, power) in enumerate(results, start=1):
        logger.info(f"  {rank}. {name}: {power:.4f} W")

    # Save CSV
    csv_path = output_dir / "texture_comparison.csv"
    with open(csv_path, "w") as f:
        f.write("rank,texture,avg_power_watts\n")
        for rank, (name, power) in enumerate(results, start=1):
            f.write(f"{rank},{name},{power:.4f}\n")
    logger.info(f"Saved CSV results to {csv_path}")

    # Save bar chart, ranked best-to-worst
    names = [name for name, _ in results]
    powers = [power for _, power in results]

    plt.figure(figsize=(max(8, len(names) * 0.4), 6))
    bars = plt.bar(range(len(names)), powers, color="steelblue")
    if bars:
        bars[0].set_color("seagreen")  # highlight the best texture
    plt.xticks(range(len(names)), names, rotation=90)
    plt.title("Average Power by Texture (ranked best to worst)")
    plt.xlabel("Texture file")
    plt.ylabel("Average Power (W)")
    plt.tight_layout()

    chart_path = output_dir / "texture_comparison.png"
    plt.savefig(str(chart_path))
    logger.info(f"Saved comparison chart to {chart_path}")
    plt.show()


def main():
    timestamp = datetime.datetime.now().strftime("%Y-%m-%d_%Hh%Mm%Ss")
    output_dir = PROJECT_ROOT / "optimizer" / "outputs" / f"comparison_{timestamp}"
    output_dir.mkdir(parents=True, exist_ok=True)

    log_file_path: Path = output_dir / "comparison_log.txt"
    logging.basicConfig(
        level=logging.DEBUG, format="%(asctime)s - %(levelname)s - %(message)s",
        handlers=[
            logging.FileHandler(str(log_file_path)),
            logging.StreamHandler()
        ]
    )
    logging.getLogger("matplotlib").setLevel(logging.WARNING)
    logging.getLogger("PIL").setLevel(logging.WARNING)

    logger.info(f"Comparing all textures in {TEXTURES_DIR}")
    logger.info(f"Output directory: {output_dir}")

    compare_textures(output_dir)


if __name__ == "__main__":
    main()