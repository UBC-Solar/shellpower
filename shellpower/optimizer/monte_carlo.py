from shellpower import ArraySimulator, ArraySimulatorInput
from shellpower.optimizer.config import brainerd_afternoon
from shellpower.simulator.Simulation import Simulation
from utils import compute_cell_positions, compute_geometric_adjacency, mutate_adjacent, score_output
from pathlib import Path
import datetime
import random
import csv

# ============================================================
# OPTIMIZATION ENTRY POINT
# ============================================================

if __name__ == "__main__":

    random.seed(0)

    PROJECT_ROOT = Path(__file__).parent.parent.parent
    BASE_TEXTURE_PATH = PROJECT_ROOT / "arrays" / "v4" / "cascadia_v1_y160x90.png"
    TOP_SHELL_MODEL = PROJECT_ROOT / "arrays" / "v4" / "v4-blender-guillotined.stl"
    BYPASS_DIODES_JSON = PROJECT_ROOT / "shellpower" / "bypass_diodes.json"

    # Load ArraySpec
    aspec = Simulation.ArraySpec(
        str(BASE_TEXTURE_PATH),
        str(TOP_SHELL_MODEL),
        str(BYPASS_DIODES_JSON),
    )

    # Precompute geometry
    print("Computing cell positions...")
    cell_positions = compute_cell_positions(aspec)

    print("Computing geometric adjacency...")
    geometric_pairs = compute_geometric_adjacency(cell_positions)
    print(f"Found {len(geometric_pairs)} adjacent geometric pairs.")

    # Prepare output directory
    timestamp = datetime.datetime.now().strftime("%Y-%m-%d_%Hh%Mm%Ss")
    simulation_dir = PROJECT_ROOT / "shellpower" / "outputs" / timestamp
    simulation_dir.mkdir(parents=True, exist_ok=True)

    results_csv_path = simulation_dir / "results.csv"
    results_cols = ["Model", "Texture Path", "Simulated Power[W]", "Score"]

    with open(results_csv_path, "w", newline="") as results_csv:
        writer = csv.DictWriter(results_csv, fieldnames=results_cols)
        writer.writeheader()

    # Initial simulation
    texture_path = simulation_dir / "texture_0.png"
    aspec.SaveArrayTexture(str(texture_path))

    print("Running initial simulation...")
    simulator = ArraySimulator()
    simulator_input = ArraySimulatorInput(
        **brainerd_afternoon,
        LayoutTexturePath=texture_path,
        MeshPath=str(TOP_SHELL_MODEL),
    )

    output = simulator.simulate(simulator_input)
    current_score = score_output(output)

    print(f"Initial power: {current_score} W")

    with open(results_csv_path, "a", newline="") as results_csv:
        writer = csv.DictWriter(results_csv, fieldnames=results_cols)
        writer.writerow({
            "Model": TOP_SHELL_MODEL,
            "Texture Path": str(texture_path),
            "Simulated Power[W]": current_score,
            "Score": current_score,
        })

    # Optimization loop
    num_iters = 5

    for i in range(num_iters):
        print(f"\nIteration {i + 1}")

        remove_from_string, add_to_string, moved_cell = mutate_adjacent(
            aspec,
            geometric_pairs,
        )

        print(f"Moved cell from {remove_from_string.Name} to {add_to_string.Name}")

        texture_path = simulation_dir / f"texture_{i + 1}.png"
        aspec.SaveArrayTexture(str(texture_path))

        simulator_input = ArraySimulatorInput(
            **brainerd_afternoon,
            LayoutTexturePath=texture_path,
            MeshPath=str(TOP_SHELL_MODEL),
        )

        output = simulator.simulate(simulator_input)
        current_score = score_output(output)

        print(f"Simulated power: {current_score} W")

        with open(results_csv_path, "a", newline="") as results_csv:
            writer = csv.DictWriter(results_csv, fieldnames=results_cols)
            writer.writerow({
                "Model": TOP_SHELL_MODEL,
                "Texture Path": str(texture_path),
                "Simulated Power[W]": current_score,
                "Score": current_score,
            })
