from shellpower import ArraySimulator, ArraySimulatorInput
from shellpower.optimizer.config import brainerd_afternoon
from shellpower.simulator.Simulation import Simulation
from array_handler import ArrayHandler
from simulated_annealing import SimulatedAnnealing
import matplotlib.pyplot as plt
from typing import Callable
from pathlib import Path
from tqdm import tqdm
import datetime
import logging
import random
import math
import csv
import re


logger = logging.getLogger(__name__)


def setup_logging():
    logging.basicConfig(
        level=logging.INFO,
        # format="%(asctime)s - %(levelname)s - %(name)s - %(message)s"
    )


def find_free_texture_index(directory: Path) -> int:
    pattern = re.compile(r"^texture_(\d+)$")
    used = set()

    for path in directory.iterdir():
        match = pattern.match(path.name)
        if match:
            used.add(int(match.group(1)))

    i = 0
    while i in used:
        i += 1

    return i


def run_optimization():

    # Optimization configuration
    seed: int = 0
    num_iters: int = 200

    """
    Higher tempertures increase the probability that a worse mutation will be kept. Temperature T decays over time throughout the simulation.
            Note that physically, temperature has the same dimension as the objective function:
              - If a mutation worsens the objective by 0.5T, there is a 61% chance it will be kept.
              - If a mutation worsens the objective by T, there is a 37% chance it will be kept.
              - If a mutation worsens the objective by 2T, there is a 14% chance it will be kept.
    """
    init_temp: float = 0.8 # W

    random.seed(0)
    logger.info(f"Starting optimization with {num_iters} iterations!")
    logger.info(f"Seed: {seed}")
    logger.info(f"Initial temperature: {init_temp} W")

    PROJECT_ROOT = Path(__file__).parent.parent.parent
    BASE_TEXTURE_PATH = PROJECT_ROOT / "arrays" / "v4" / "cascadia_v1_y160x90.png"
    TOP_SHELL_MODEL = PROJECT_ROOT / "arrays" / "v4" / "v4-blender-guillotined.stl"
    BYPASS_DIODES_JSON = PROJECT_ROOT / "shellpower" / "bypass_diodes.json"

    logger.info("Initializing ArraySpec with parameters:")
    logger.info(f"    {PROJECT_ROOT=}")
    logger.info(f"    {BASE_TEXTURE_PATH=}")
    logger.info(f"    {TOP_SHELL_MODEL=}")
    logger.info(f"    {BYPASS_DIODES_JSON=}")

    timestamp = datetime.datetime.now().strftime("%Y-%m-%d_%Hh%Mm%Ss")
    simulation_dir = PROJECT_ROOT / "shellpower" / "outputs" / timestamp
    simulation_dir.mkdir(parents=True, exist_ok=True)
    logger.info(f"Created output directory at {simulation_dir}")

    # Load ArraySpec
    aspec: object = Simulation.ArraySpec(
        str(BASE_TEXTURE_PATH),
        str(TOP_SHELL_MODEL),
        str(BYPASS_DIODES_JSON),
    )
    handler: ArrayHandler = ArrayHandler(aspec)

    texture_number = 0

    def objective_function() -> float:
        """Returns the value to be minimized by simulated annealing"""

        # 1. Save the current ArraySpec
        nonlocal texture_number
        texture_path = simulation_dir / f"texture_{texture_number}.png"
        handler.save_texture(texture_path)
        texture_number += 1

        # 2. Define the irradiance conditions
        simulator_input = ArraySimulatorInput(
            **brainerd_afternoon,
            LayoutTexturePath=texture_path,
            MeshPath=str(TOP_SHELL_MODEL),
        )

        # 3. Compute the value to be minimized
        logger.info(f"Evaluating config texture at {texture_path}...")
        power = handler.get_watts(simulator_input)
        logger.info(f"Simulator output: {power} W")

        to_minimize = -power

        return to_minimize

    sa_optimizer: SimulatedAnnealing = SimulatedAnnealing(
        objective_function,
        handler.mutate_adjacent,
        handler.undo_mutate,
        init_temp,
        num_iters,
    )

    sa_optimizer.simulate()

    logger.info("Simulated annealing complete!")

    plt.plot(sa_optimizer.scores)
    plt.title("Simulated Annealing Score vs. Iteration Number")
    plt.xlabel("Iteration number")
    plt.ylabel("Score (lower is better)")
    plt.savefig(str(simulation_dir / "progress_plot.png"))
    plt.show()


# ============================================================
# OPTIMIZATION ENTRY POINT
# ============================================================

if __name__ == "__main__":
    setup_logging()
    run_optimization()
