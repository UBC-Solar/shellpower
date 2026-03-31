from shellpower import ArraySimulatorInput
from shellpower.optimizer.config import brainerd_afternoon
from shellpower.simulator.Simulation import Simulation
from array_handler import ArrayHandler
from simulated_annealing import SimulatedAnnealing
import matplotlib.pyplot as plt
from pathlib import Path
import datetime
import logging
import random
import time


logger = logging.getLogger(__name__)


# autoclicker shenanigans
import win32gui
import win32con
def try_click_window(title: str):
    hwnd = win32gui.FindWindow(None, title)
    if hwnd == 0:
        logger.warning(f"Window '{title}' not found")
    else:
        win32gui.PostMessage(hwnd, win32con.WM_MOUSEMOVE, 0, 0)


def run_optimization():

    # Optimization configuration
    RNG_SEED: int = 0
    NUM_ITERS: int = 1000
    """
    Higher tempertures increase the probability that a worse mutation will be kept.
    Temperature T decays over time throughout the simulation.
    Note that physically, temperature has the same dimension as the objective function:
        - If a mutation worsens the objective by 0.5T, there is a 61% chance it will be kept.
        - If a mutation worsens the objective by T, there is a 37% chance it will be kept.
        - If a mutation worsens the objective by 2T, there is a 14% chance it will be kept.
    """
    INIT_TEMP: float = 0.5  # Watts; the score is -power of the whole array
    MAX_STRING_CELLS: int = 107
    DUAL_MUTATE_PROBABILITY: float = 0.5

    PROJECT_ROOT = Path(__file__).parent.parent.parent
    BASE_TEXTURE_PATH = PROJECT_ROOT / "arrays" / "v4" / "cascadia_v1_y160x90.png"
    TOP_SHELL_MODEL = PROJECT_ROOT / "arrays" / "v4" / "v4-blender-guillotined.stl"
    BYPASS_DIODES_JSON = PROJECT_ROOT / "shellpower" / "bypass_diodes.json"

    # Create output directory
    timestamp = datetime.datetime.now().strftime("%Y-%m-%d_%Hh%Mm%Ss")
    simulation_dir = PROJECT_ROOT / "shellpower" / "outputs" / timestamp
    simulation_dir.mkdir(parents=True, exist_ok=True)

    # Set up logging
    log_file_path: Path = simulation_dir / "optimization_log.txt"
    logging.basicConfig(
        level=logging.DEBUG, format="%(asctime)s - %(levelname)s - %(message)s",
        handlers=[
            logging.FileHandler(str(log_file_path)),
            logging.StreamHandler()
        ]
    )
    # Silence noisy libs
    logging.getLogger("matplotlib").setLevel(logging.WARNING)
    logging.getLogger("PIL").setLevel(logging.WARNING)

    logger.info(f"Created output directory at {simulation_dir}")

    random.seed(RNG_SEED)
    logger.info(f"Starting optimization with {NUM_ITERS} iterations!")
    logger.info(f"Starting texture: {BASE_TEXTURE_PATH}")
    logger.info(f"Top shell model: {TOP_SHELL_MODEL}")
    logger.info(f"Initial temperature: {INIT_TEMP} W")
    logger.info(f"String max size: {MAX_STRING_CELLS} cells")

    start_time = time.perf_counter()  # Performance tracking

    # Inswtantiate ArraySpec & Handler
    aspec: object = Simulation.ArraySpec(
        str(BASE_TEXTURE_PATH),
        str(TOP_SHELL_MODEL),
        str(BYPASS_DIODES_JSON),
    )
    handler: ArrayHandler = ArrayHandler(aspec, MAX_STRING_CELLS)

    # Define function to minimize by simulated annealing
    texture_number = 0
    def objective_function() -> float:
        nonlocal texture_number
        iter_start = time.perf_counter()

        # DIRTY FIX
        # On my desktop PC, it slows down greatly (0.7s -> 9s) per run when the window hasn't been clicked in ~5s
        try_click_window("OpenTK Window")

        # 1. Save the current ArraySpec
        logger.debug("Exporting the current texture...")
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
        logger.debug("Simulating power...")
        power = handler.get_watts(simulator_input)

        iter_duration = time.perf_counter() - iter_start
        logger.info(
            f"[Iter {texture_number}] Power: {power:.4f} W | Eval Time: {iter_duration:.2f}s"
        )

        return -power

    def mutate_function() -> None:
        if random.random() > DUAL_MUTATE_PROBABILITY:
            logger.info("Attempting single cell move!")
            handler.mutate_adjacent()
        else:
            logger.info("Attempting dual cell swap!")
            handler.dual_mutate_adjacent()

    # Run simulated annealing optimization
    sa_optimizer: SimulatedAnnealing = SimulatedAnnealing(
        objective_function,
        mutate_function,
        handler.undo_mutate,
        INIT_TEMP,
        NUM_ITERS,
    )
    sa_optimizer.simulate()

    # Log performance
    total_duration = time.perf_counter() - start_time
    logger.info(f"Simulated annealing complete in {total_duration:.2f} seconds!")

    # Plot objective over time
    plt.plot(sa_optimizer.scores)
    plt.title("Simulated Annealing Score vs. Iteration Number")
    plt.xlabel("Iteration number")
    plt.ylabel("Score (lower is better)")
    plt.savefig(str(simulation_dir / "progress_plot.png"))
    plt.show()


if __name__ == "__main__":
    run_optimization()
