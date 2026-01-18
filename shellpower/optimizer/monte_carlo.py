from shellpower import ArraySimulator, ArraySimulatorInput, ArraySimulatorOutput
from shellpower.optimizer.config import ncm_motorsports_park_config
from shellpower.simulator.Simulation import Simulation
from pathlib import Path
from tqdm import tqdm
import datetime
import random


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
    """Determine if two cells are neighbours of each other"""

    # min distance between cells in pixels
    r_min = 200

    return (a_pos[0] - b_pos[0]) ** 2 + (a_pos[1] - b_pos[1]) ** 2 <= r_min ** 2

def get_adjacent_cells(array_spec) -> list[tuple]:
    """Determine a list of cells on different strings which are adjacent"""

    cell_properties: dict[object, dict] = {}

    for string in tqdm(array_spec.Strings, desc="Determining cell positions"):
        for cell in string.Cells:
            cell_properties[cell] = {"pos": get_cell_position(cell), "string": string}

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

def mutate_adjacent(array_spec, adjacent_cells: list[tuple]) -> tuple:
    """Move a random cell from one string to an adjacent one. Mutates array_spec.

    :param array_spec: ArraySpec to mutate.
    :return:
        **remove_from_string**: String from which the cell was removed
        **add_to_string**: String to which the cell was added
        **cell_to_move:** Cell which was moved
    """

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


if __name__ == "__main__":

    # Load default ArraySpec
    project_root = Path(__file__).parent.parent.parent
    base_texture_path = project_root / "arrays" / "luminos" / "luminos-splines-6-string-no-bypass-rot.png"
    top_shell_model = project_root / "arrays" / "luminos" / "luminos.stl"
    bypass_diodes_json = project_root / "shellpower" / "bypass_diodes.json"

    aspec = Simulation.ArraySpec(
        str(base_texture_path),
        str(top_shell_model),
        str(bypass_diodes_json),
    )

    # Determine adjacent cells
    print("Determining adjacent cells")
    adjacent_cells: list[tuple] = get_adjacent_cells(aspec)
    print(f"Found {len(adjacent_cells)} adjacent cell pairs!")

    # Create a folder for the outputs
    timestamp = datetime.datetime.now().strftime("%Y-%m-%d_%Hh%Mm%Ss")
    simulation_dir = project_root / "shellpower" / "textures" / timestamp
    simulation_dir.mkdir(parents=True, exist_ok=True)

    texture_path = base_texture_path

    for i in range(5):
        print(f"Using texture {texture_path}")

        # Evaluate the string
        print("Simulating performance...")
        simulator = ArraySimulator()
        simulator_input = ArraySimulatorInput(
            **ncm_motorsports_park_config,
            LayoutTexturePath=texture_path,
            MeshPath=str(top_shell_model),
        )
        output: ArraySimulatorOutput = simulator.simulate(simulator_input)

        # Score the output
        print(f"Simulated power: {output.WattsOutputByCell} Watts")

        # Mutate the ArraySpec
        remove_from_string, add_to_string, moved_cell = mutate_adjacent(aspec, adjacent_cells)
        print("\nNew iteration!")
        print(f"Moved cell from {remove_from_string.Name} to {add_to_string.Name}")

        # Export the current texture file
        texture_path = simulation_dir / f"texture_{i}.png"
        aspec.SaveArrayTexture(str(texture_path))