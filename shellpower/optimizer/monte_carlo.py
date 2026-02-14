from shellpower import ArraySimulator, ArraySimulatorInput, ArraySimulatorOutput
from shellpower.optimizer.config import ncm_motorsports_park_config
from shellpower.simulator.Simulation import Simulation
from pathlib import Path
from tqdm import tqdm
import datetime
import random
import csv


# ======== FUNCTION DEFINITIONS ========

def get_cell_position(cell) -> tuple[int, int]:
    """Determine the coordinates of a cell

    A cell's coordinates are defined by the leftmost pixel of the topmost row in that cell.

    :param cell: The cell to determine the coordinates for
    :return: (x, y) coordinates of the topmost pixel coordinate. Origin is in the top left corner.
    """

    # If you're using a pixel with a million pixels in any direction, you've got bigger problems
    min_x: int = cell.Pixels[0].First
    min_y: int = cell.Pixels[0].Second

    for px in cell.Pixels:
        x: int = px.First
        y: int = px.Second
        if y < min_y:
            # Found a new topmost row
            min_x = x
            min_y = y
        if y == min_y and x < min_x:
            # Tied for top row, but more to the left
            min_x = x
            min_y = y

    return min_x, min_y

def neighbours(a_pos, b_pos) -> bool:
    """Determine if two cells are neighbours of each other

    Distance is measured by taxicab distance to select side-to-side neighbours.
    """

    # min distance between cells in pixels
    r_min = 200

    return abs(a_pos[0] - b_pos[0]) + abs(a_pos[1] - b_pos[1]) <= r_min

def get_adjacent_cells(cell_properties: dict[object, dict]) -> list[tuple]:
    """Determine a list of cells on different strings which are adjacent

    :param cell_properties: Dictionary of cell properties
    :return: List of tuples of unique adjacent cell object pairs"""

    adjacent_cells: list[tuple] = []

    for i_a, cell_a_key_val in enumerate(cell_properties.items()):
        for i_b, cell_b_key_val in enumerate(cell_properties.items()):
            cell_a, a_properties = cell_a_key_val
            cell_b, b_properties = cell_b_key_val

            if a_properties["string"] == b_properties["string"]:
                # Only consider cells on different strings
                continue

            if i_a <= i_b:
                # Only consider unique pairs of cells
                continue

            if neighbours(a_properties["pos"], b_properties["pos"]):
                adjacent_cells.append(
                    (cell_a, cell_b, a_properties["string"], b_properties["string"])
                )

    return adjacent_cells

def mutate_random(array_spec) -> tuple:
    """Move a random cell from one string to another. Mutates array_spec.

    :param array_spec: ArraySpec to mutate.
    :return:
        **remove_from_string**: String from which the cell was removed
        **add_to_string**: String to which the cell was added
        **cell_to_move:** Cell which was moved
    """

    strings = list(array_spec.Strings)

    remove_from_string = random.choice(strings)
    cells_on_remove_string = list(remove_from_string.Cells)

    strings.remove(remove_from_string)
    add_to_string = random.choice(strings)
    cell_to_move = random.choice(cells_on_remove_string)

    # Modify and recolor ArraySpec
    array_spec.AddCellToCellString(cell_to_move, add_to_string)
    array_spec.Recolor()

    return remove_from_string, add_to_string, cell_to_move

def undo_mutate(array_spec, remove_from_string, cell_to_move):
    """Undo the action from mutate_random.

    :param array_spec: ArraySpec to mutate.
    :param remove_from_string: String from which the cell was removed
    :param cell_to_move: Cell which was moved
    """

    array_spec.AddCellToCellString(cell_to_move, remove_from_string)
    array_spec.Recolor()

def mutate_adjacent(array_spec) -> tuple:
    """Move a random cell from one string to an adjacent one. Mutates array_spec.

    :param array_spec: ArraySpec to mutate.
    :return:
        **remove_from_string**: String from which the cell was removed
        **add_to_string**: String to which the cell was added
        **cell_to_move:** Cell which was moved
    """
    adjacent_cells: list[tuple] = get_adjacent_cells(cell_properties)

    cell_a, cell_b, string_a, string_b = random.choice(adjacent_cells)

    a_to_b: bool = bool(random.getrandbits(1))

    # Modify and recolor ArraySpec
    if a_to_b:
        cell_to_move = cell_a         # move cell a
        remove_from_string = string_a # from string a
        add_to_string = string_b      # to string b
    else:
        cell_to_move = cell_b         # move cell b
        remove_from_string = string_b # from string b
        add_to_string = string_a      # to string a

    array_spec.AddCellToCellString(cell_to_move, add_to_string)
    array_spec.Recolor()

    return remove_from_string, add_to_string, cell_to_move

def score_output(output: ArraySimulatorOutput) -> float:
    """Score the output of the simulation.

    Currently scoring based on WattsOutputByCell

    :param output: Simulation output
    :return: WattsOutputByCell
    """
    return output.WattsOutputByCell

def get_cell_properties(array_spec) -> dict[object, dict]:
    """Get a dictionary mapping cell objects to their properties:
        "pos": (x, y) position from top left
        "string": string from which the cell was removed
    :param array_spec: ArraySpec object
    :return: dictionary of cell properties
    """
    cell_properties: dict[object, dict] = {}

    for string in tqdm(array_spec.Strings, desc="Determining cell positions"):
        for cell in string.Cells:
            cell_properties[cell] = {"pos": get_cell_position(cell), "string": string}

    return cell_properties

# ======== OPTIMIZATION ENTRY POINT ========

if __name__ == "__main__":

    random.seed(0)  # Fix seed for reproducibility

    """
    Optimization strategy:
        - start with a default array texture
        - mutate it by 'flipping' a cell to a neighboring string
        - evaluate the power produced with the modified texture
            - If it's better, choose the new one
            - If it's worse, keep the old one
            - If they're the same, randomly choose whether to keep or not
            - TODO: detect when we are at a local max, i.e., any flip makes the performance worse
                - In this case, do a bunch of random flips to allow for more improvements
        - save the result in a CSV file which tracks
                - Model
                - Texture Path
                - Simulated Power [W]
                - Commit Hash
            in order to allow optimizations to be interrupted and resumed.

    Outputs:
        - Creates a new timestamped directory in shellpower/shellpower/outputs
        - The output directory contains
            - All texture files evaluated
            - A CSV which tracks the performance of each texture
    """


    # ======== LOAD ARRAYSPEC ========

    # Load default ArraySpec
    PROJECT_ROOT = Path(__file__).parent.parent.parent
    BASE_TEXTURE_PATH = PROJECT_ROOT / "arrays" / "luminos" / "luminos-splines-6-string-no-bypass-rot.png"
    TOP_SHELL_MODEL = PROJECT_ROOT / "arrays" / "luminos" / "luminos.stl"
    BYPASS_DIODES_JSON = PROJECT_ROOT / "shellpower" / "bypass_diodes.json"

    aspec = Simulation.ArraySpec(
        str(BASE_TEXTURE_PATH),
        str(TOP_SHELL_MODEL),
        str(BYPASS_DIODES_JSON),
    )

    # Determine cell positions (centroids)
    # This is needed to calculate adjacency when flipping neighbouring cells.
    print("Calculating cell locations")
    cell_properties: dict[object, dict] = get_cell_properties(aspec)

    # ======== PREPARE OUTPUTS ========

    # Create a folder for the outputs
    timestamp = datetime.datetime.now().strftime("%Y-%m-%d_%Hh%Mm%Ss")
    simulation_dir = PROJECT_ROOT / "shellpower" / "outputs" / timestamp
    simulation_dir.mkdir(parents=True, exist_ok=True)

    # Export the starting texture file (saves a copy)
    texture_path = simulation_dir / "texture_0.png"
    aspec.SaveArrayTexture(str(texture_path))

    # Create a csv file to track optimization progress
    results_cols = [
        "Model",
        "Texture Path",
        "Simulated Power[W]",
        "Score",
    ]
    results_csv_path = simulation_dir / "results.csv"
    with open(results_csv_path, 'w', newline='') as results_csv:
        writer = csv.DictWriter(results_csv, fieldnames=results_cols)
        writer.writeheader()
    print(f"Saving results in {results_csv_path}")

    # ======== INITIAL CONFIGURATION SIMULATION ========

    # Evaluate the string
    print("Simulating performance...")
    simulator = ArraySimulator()
    simulator_input = ArraySimulatorInput(
        **ncm_motorsports_park_config,
        LayoutTexturePath=texture_path,
        MeshPath=str(TOP_SHELL_MODEL),
    )
    output: ArraySimulatorOutput = simulator.simulate(simulator_input)

    # Score the output and save to csv
    print(f"Simulated power: {output.WattsOutputByCell} Watts")
    current_score = score_output(output)
    with open(results_csv_path, 'a', newline='') as results_csv:
        writer = csv.DictWriter(results_csv, fieldnames=results_cols)
        writer.writerow({
            "Model": TOP_SHELL_MODEL,
            "Texture Path": str(texture_path),
            "Simulated Power[W]": output.WattsOutputByCell,
            "Score": current_score,
        })

    num_iters = 5  # Increase to a large value for
    for i in range(num_iters):

        # ======== OPTIMIZATION LOOP ========

        # Mutate the ArraySpec
        remove_from_string, add_to_string, moved_cell = mutate_adjacent(aspec)
        print("\nNew iteration!")
        print(f"Moved cell from {remove_from_string.Name} to {add_to_string.Name}")

        # Export the current texture file
        texture_path = simulation_dir / f"texture_{i + 1}.png"
        aspec.SaveArrayTexture(str(texture_path))

        print(f"Using texture {texture_path}")

        # Evaluate the string
        print("Simulating performance...")
        simulator = ArraySimulator()
        simulator_input = ArraySimulatorInput(
            **ncm_motorsports_park_config,
            LayoutTexturePath=texture_path,
            MeshPath=str(TOP_SHELL_MODEL),
        )
        output: ArraySimulatorOutput = simulator.simulate(simulator_input)

        # Score the output
        print(f"Simulated power: {output.WattsOutputByCell} Watts")
        current_score = score_output(output)
        with open(results_csv_path, 'a', newline='') as results_csv:
            writer = csv.DictWriter(results_csv, fieldnames=results_cols)
            writer.writerow({
                "Model": TOP_SHELL_MODEL,
                "Texture Path": str(texture_path),
                "Simulated Power[W]": output.WattsOutputByCell,
                "Score": current_score,
            })
