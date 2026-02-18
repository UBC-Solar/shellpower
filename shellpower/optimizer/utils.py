from shellpower import ArraySimulatorOutput
from tqdm import tqdm
from collections import defaultdict
import random

# ============================================================
# GEOMETRY UTILITIES
# ============================================================

def get_cell_position(cell) -> tuple[int, int]:
    """
    Determine the (x, y) coordinate of a cell.

    The coordinate is defined as the leftmost pixel of the topmost row
    in the cell. Origin is the top-left corner of the texture.

    :param cell: Cell object
    :return: (x, y) pixel coordinate
    """
    min_x = cell.Pixels[0].First
    min_y = cell.Pixels[0].Second

    for px in cell.Pixels:
        x = px.First
        y = px.Second

        if y < min_y:
            # found a new top row
            min_x, min_y = x, y
        elif y == min_y and x < min_x:
            # in same top row, but more to the left
            min_x, min_y = x, y

    return min_x, min_y


def neighbours(a_pos: tuple[int, int], b_pos: tuple[int, int]) -> bool:
    """
    Determine whether two cells are adjacent using Manhattan distance.

    :param a_pos: (x, y) position of first cell
    :param b_pos: (x, y) position of second cell
    :return: True if distance <= threshold
    """
    r_min = 200
    return abs(a_pos[0] - b_pos[0]) + abs(a_pos[1] - b_pos[1]) <= r_min


def compute_cell_positions(array_spec) -> dict:
    """
    Compute and store the geometric position of every cell in the array.

    This is computed once since geometry does not change during optimization.

    :param array_spec: ArraySpec instance
    :return: Dict mapping cell -> (x, y) position
    """
    positions = {}

    for string in tqdm(array_spec.Strings, desc="Determining cell positions"):
        for cell in string.Cells:
            positions[cell] = get_cell_position(cell)

    return positions


def compute_geometric_adjacency(cell_positions: dict) -> list[tuple]:
    """
    Compute all geometrically adjacent cell pairs.

    This ignores string membership. Adjacency is based solely on position.

    Uses spatial hashing to avoid O(n^2) complexity.

    :param cell_positions: Dict mapping cell -> (x, y)
    :return: List of adjacent cell pairs [(cell_a, cell_b), ...]
    """
    bin_size = 200
    grid: dict[tuple, list[object]] = defaultdict(list)

    # Spatial hash
    for cell, (x, y) in cell_positions.items():
        grid[(x // bin_size, y // bin_size)].append(cell)

    adjacent_pairs = []

    for cell, (x, y) in cell_positions.items():
        gx, gy = x // bin_size, y // bin_size

        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for other in grid.get((gx + dx, gy + dy), []):
                    if other is cell:
                        continue
                    if id(cell) < id(other):
                        continue
                    if neighbours((x, y), cell_positions[other]):
                        adjacent_pairs.append((cell, other))

    return adjacent_pairs


def build_cell_to_string_map(array_spec) -> dict:
    """
    Build a mapping from each cell to its current string.

    This reflects dynamic string membership and must be rebuilt
    after each mutation.

    :param array_spec: ArraySpec instance
    :return: Dict mapping cell -> string
    """
    mapping = {}
    for string in array_spec.Strings:
        for cell in string.Cells:
            mapping[cell] = string
    return mapping


# ============================================================
# MUTATION LOGIC
# ============================================================

def mutate_adjacent(array_spec, geometric_pairs: list[tuple]) -> tuple:
    """
    Move a random cell to a neighboring string.

    Only adjacent cells belonging to different strings are considered.
    Geometric adjacency is precomputed; string membership is checked dynamically.

    :param array_spec: ArraySpec to mutate
    :param geometric_pairs: List of geometrically adjacent cell pairs
    :return: (remove_from_string, add_to_string, moved_cell)
    """
    cell_to_string = build_cell_to_string_map(array_spec)

    valid_pairs = [
        (a, b)
        for (a, b) in geometric_pairs
        if cell_to_string[a] != cell_to_string[b]
    ]

    if not valid_pairs:
        raise RuntimeError("No adjacent cross-string cell pair found.")

    cell_a, cell_b = random.choice(valid_pairs)

    if random.getrandbits(1):
        cell_to_move = cell_a
        remove_from_string = cell_to_string[cell_a]
        add_to_string = cell_to_string[cell_b]
    else:
        cell_to_move = cell_b
        remove_from_string = cell_to_string[cell_b]
        add_to_string = cell_to_string[cell_a]

    array_spec.AddCellToCellString(cell_to_move, add_to_string)
    array_spec.Recolor()

    return remove_from_string, add_to_string, cell_to_move


def undo_mutate(array_spec, remove_from_string, cell_to_move):
    """
    Undo a previous mutation by restoring a cell to its original string.

    :param array_spec: ArraySpec to modify
    :param remove_from_string: Original string
    :param cell_to_move: Cell to restore
    """
    array_spec.AddCellToCellString(cell_to_move, remove_from_string)
    array_spec.Recolor()


def score_output(output: ArraySimulatorOutput) -> float:
    """
    Compute a scalar score for a simulation result.

    Currently uses WattsOutputByCell.

    :param output: Simulation output object
    :return: Score (float)
    """
    return output.WattsOutputByCell
