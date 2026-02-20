from shellpower import ArraySimulator, ArraySimulatorInput
from collections import defaultdict
from pathlib import Path
from tqdm import tqdm
import logging
import random


logger = logging.getLogger(__name__)


class ArrayHandler:
    """
    Container class for C# ArraySpec object. Provides necessary methods for optimization of array configuration.
    """

    def __init__(self, array_spec: object, _verbose=True):
        """
        Instantiate an ArrayHandler object with a given ArraySpec

        :param array_spec: ArraySpec object, initalized with the desired texture and model.
        """

        self.aspec: object = array_spec
        self.verbose: bool = _verbose

        logger.info("Computing cell positions...")
        self.cell_positions: dict[object, tuple[int, int]] = (
            self.compute_cell_positions()
        )

        logger.info("Computing geometric adjacency...")
        self.geometric_pairs: list[tuple[object, object]] = (
            self.compute_geometric_adjacency()
        )
        logger.info(f"Found {len(self.geometric_pairs)} adjacent geometric pairs.")

        self._simulator = ArraySimulator()

    # ============================================================
    # GEOMETRY
    # ============================================================

    @staticmethod
    def get_cell_position(cell: object) -> tuple[int, int]:
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

    @staticmethod
    def neighbours(a_pos: tuple[int, int], b_pos: tuple[int, int]) -> bool:
        """
        Determine whether two cells are adjacent using Manhattan distance.

        :param a_pos: (x, y) position of first cell
        :param b_pos: (x, y) position of second cell
        :return: True if distance <= threshold
        """
        r_min = 200
        return abs(a_pos[0] - b_pos[0]) + abs(a_pos[1] - b_pos[1]) <= r_min

    def compute_cell_positions(self) -> dict[object, tuple[int, int]]:
        """
        Compute and store the geometric position of every cell in the array.

        This is computed once since geometry does not change during optimization.

        :return: Dict mapping cell -> (x, y) position
        """
        positions = {}

        for string in tqdm(self.aspec.Strings, desc="Determining cell positions"):
            for cell in string.Cells:
                positions[cell] = self.get_cell_position(cell)

        return positions

    def compute_geometric_adjacency(self) -> list[tuple[object, object]]:
        """
        Compute all geometrically adjacent cell pairs.

        This ignores string membership. Adjacency is based solely on position.

        Uses spatial hashing to avoid O(n^2) complexity.

        :return: List of adjacent cell pairs [(cell_a, cell_b), ...]
        """
        bin_size = 200
        grid: dict[tuple, list[object]] = defaultdict(list)

        # Spatial hash
        for cell, (x, y) in self.cell_positions.items():
            grid[(x // bin_size, y // bin_size)].append(cell)

        adjacent_pairs = []

        for cell, (x, y) in self.cell_positions.items():
            gx, gy = x // bin_size, y // bin_size

            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    for other in grid.get((gx + dx, gy + dy), []):
                        if other is cell:
                            continue
                        if id(cell) < id(other):
                            continue
                        if self.neighbours((x, y), self.cell_positions[other]):
                            adjacent_pairs.append((cell, other))

        return adjacent_pairs

    # ============================================================
    # ARRAY MANIPULATION
    # ============================================================

    def build_cell_to_string_map(self) -> dict:
        """
        Build a mapping from each cell to its current string.

        This reflects dynamic string membership and must be rebuilt
        after each mutation.

        :return: Dict mapping cell -> string
        """
        mapping = {}
        for string in self.aspec.Strings:
            for cell in string.Cells:
                mapping[cell] = string
        return mapping

    def mutate_adjacent(self) -> None:
        """
        Move a random cell to a neighboring string.

        Only adjacent cells belonging to different strings are considered.
        Geometric adjacency is precomputed; string membership is checked dynamically.

        :return: (remove_from_string, add_to_string, moved_cell)
        """
        cell_to_string = self.build_cell_to_string_map()

        valid_pairs = [
            (a, b)
            for (a, b) in self.geometric_pairs
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

        self.aspec.AddCellToCellString(cell_to_move, add_to_string)
        self.aspec.Recolor()

        self.last_add_to_string = add_to_string
        self.last_remove_from_string = remove_from_string
        self.last_cell_to_move = cell_to_move

    def undo_mutate(self):
        """
        Undo a previous mutation by restoring a cell to its original string.

        :param remove_from_string: Original string
        :param cell_to_move: Cell to restore
        """
        self.aspec.AddCellToCellString(
            self.last_cell_to_move, self.last_remove_from_string
        )
        self.aspec.Recolor()

    # ============================================================
    # PRODUCE AND EVALUATE TEXTURES
    # ============================================================

    def save_texture(self, out_path: Path | str) -> None:
        """Save the current ArraySpec texture.

        :param out_path: Filepath to save the texture to.
        """
        if isinstance(out_path, Path):
            out_path = str(out_path)
        self.aspec.SaveArrayTexture(out_path)

    def get_watts(self, input: ArraySimulatorInput) -> float:
        """
        Compute the amount of power generated by this config in a given set of input conditions

        :param input: ArraySimulatorInput to describe the conditions, top shell and texture.
        :return: Score (float)
        """
        output = self._simulator.simulate(input)

        return output.WattsOutput
