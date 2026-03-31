from shellpower import ArraySimulator, ArraySimulatorInput
from collections import defaultdict
from pathlib import Path
from tqdm import tqdm
import collections
import logging
import random


logger = logging.getLogger(__name__)


class ArrayHandler:
    """
    Container class for C# ArraySpec object. Provides necessary methods for optimization of array configuration.
    """

    def __init__(self, array_spec: object, max_string_cells: int):
        """
        Instantiate an ArrayHandler object with a given ArraySpec

        :param array_spec: ArraySpec object, initalized with the desired texture and model.
        """

        self.aspec: object = array_spec
        self.max_string_cells: int = max_string_cells

        logger.info("Computing cell positions...")
        self.cell_registry = {}  # pos_key -> cell_object
        self.cell_positions = {} # cell_object_id -> pos_key (for internal lookup)

        for string in self.aspec.Strings:
            for cell in string.Cells:
                pos = self.get_cell_position(cell)
                self.cell_registry[pos] = cell 
                # We use the position as the absolute source of truth

        # Compute adjacency based on the position keys
        logger.info("Computing geometric adjacency...")
        self.geometric_pairs = self.compute_geometric_adjacency()

        self.adj_lookup = defaultdict(list)
        for a_pos, b_pos in self.geometric_pairs:
            self.adj_lookup[a_pos].append(b_pos)
            self.adj_lookup[b_pos].append(a_pos)

        # Persistent cell-string lookup
        logger.info("Initializing cell-to-string lookup...")
        self.pos_to_string = {}
        for string in self.aspec.Strings:
            for cell in string.Cells:
                pos = self.get_cell_position(cell)
                self.pos_to_string[pos] = string

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
        # Uses the registry keys (positions) to find neighbors
        bin_size = 200
        grid = defaultdict(list)
        all_positions = list(self.cell_registry.keys())

        for pos in all_positions:
            grid[(pos[0] // bin_size, pos[1] // bin_size)].append(pos)

        adjacent_pairs = []
        for pos in all_positions:
            gx, gy = pos[0] // bin_size, pos[1] // bin_size
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    for other_pos in grid.get((gx + dx, gy + dy), []):
                        if pos == other_pos: continue
                        # Use tuple comparison for a stable sort to avoid double pairs
                        if pos < other_pos:
                            if self.neighbours(pos, other_pos):
                                adjacent_pairs.append((pos, other_pos))
        return adjacent_pairs

    def is_string_connected_without_cell(self, string_obj, pos_to_remove) -> bool:
        """BFS using stable position keys."""
        # Get positions of all cells in string except the one we are moving
        remaining_pos = [self.get_cell_position(c) for c in string_obj.Cells]
        remaining_pos = [p for p in remaining_pos if p != pos_to_remove]

        if not remaining_pos:
            return True

        remaining_set = set(remaining_pos)
        start_node = remaining_pos[0]
        visited = {start_node}
        queue = collections.deque([start_node])

        while queue:
            curr = queue.popleft()
            for neighbor_pos in self.adj_lookup[curr]:
                if neighbor_pos in remaining_set and neighbor_pos not in visited:
                    visited.add(neighbor_pos)
                    queue.append(neighbor_pos)

        return len(visited) == len(remaining_set)

    # ============================================================
    # ARRAY MANIPULATION
    # ============================================================

    def build_cell_to_string_map(self) -> dict:
        """Returns a mapping of position_tuple -> string_object."""
        mapping = {}
        for string in self.aspec.Strings:
            for cell in string.Cells:
                pos = self.get_cell_position(cell)
                mapping[pos] = string
        return mapping

    def mutate_adjacent(self) -> None:
        """
        Move a random cell to a neighboring string, ensuring the source string
        is not split into two disconnected components.
        """
        logger.debug("Building neighbouring cell pair list...")
        valid_pairs = [
            (a_pos, b_pos)
            for (a_pos, b_pos) in self.geometric_pairs
            if self.pos_to_string[a_pos].Name != self.pos_to_string[b_pos].Name # Compare by Name/ID
        ]

        if not valid_pairs:
            return

        random.shuffle(valid_pairs)

        move_found = False
        for a_pos, b_pos in valid_pairs:
            for from_pos, to_pos in [(a_pos, b_pos), (b_pos, a_pos)]:
                source_string = self.pos_to_string[from_pos]
                target_string = self.pos_to_string[to_pos]

                logger.debug("Ensuring mutation maintains continuity...")
                if not self.is_string_connected_without_cell(source_string, from_pos):
                    logger.debug("Skipped mutation because it would cause a string to lose continuity")
                    continue

                logger.debug("Ensuring mutation doesn't exceed max string size...")
                if len(target_string.Cells) >= self.max_string_cells:
                    logger.debug(f"Skipped mutation because {target_string.Name} already has "
                                 f"the maximum cell count of {self.max_string_cells}")
                    continue

                self._swap_cell_string(from_pos, source_string, target_string)

                move_found = True
                break

            if move_found:
                break

        if not move_found:
            logger.warning("No valid mutation found that preserves string continuity.")

    def _find_cell_swap_pair(self):
        logger.debug("Building neighbouring cell pair list...")
        valid_pairs = [
            (a_pos, b_pos)
            for (a_pos, b_pos) in self.geometric_pairs
            if self.pos_to_string[a_pos].Name != self.pos_to_string[b_pos].Name # Compare by Name/ID
        ]
        random.shuffle(valid_pairs)

        # Choose the string pair for cell swap 1 (cell goes from a to b)
        from_pos_1, to_pos_1 = valid_pairs[0]
        string_a_1 = self.pos_to_string[from_pos_1].Name
        string_b_1 = self.pos_to_string[to_pos_1].Name

        # Look for a second pair with the same two strings
        from_pos_2 = None
        to_pos_2 = None
        for pos_1, pos_2 in valid_pairs[1:]: # Don't duplicate the first pair, which we have already selected
            string_a_2 = self.pos_to_string[pos_1].Name
            string_b_2 = self.pos_to_string[pos_2].Name

            # Make sure the pair for cell swap 2 is from b to a
            if (string_a_2 == string_a_1) and (string_b_2 == string_b_1):
                from_pos_2 = pos_1
                to_pos_2 = pos_2
                break
            if (string_a_2 == string_b_1) and (string_b_2 == string_a_1):
                # a and b are swapped!
                from_pos_2 = pos_2
                to_pos_2 = pos_1
                break

        if None in (from_pos_2, to_pos_2):
            raise ValueError("Failed to find cell swap pair!")

        return from_pos_1, to_pos_1, from_pos_2, to_pos_2

    def dual_mutate_adjacent(self) -> None:
            """
            Choose two neighboring strings A and B. Then move a random cell from string A to string B, and another from string B to string A.
            """

            num_attempts = 10
            for i in range(num_attempts):
                try:
                    from_pos_1, to_pos_1, from_pos_2, to_pos_2 = self._find_cell_swap_pair()
                    break
                except ValueError:
                    logger.warning("Failed to find cell swap pair!")
                    return

            move_found = False
            for a_pos, b_pos in valid_pairs:
                for from_pos, to_pos in [(a_pos, b_pos), (b_pos, a_pos)]:
                    source_string = self.pos_to_string[from_pos]
                    target_string = self.pos_to_string[to_pos]

                    logger.debug("Ensuring mutation maintains continuity...")
                    if not self.is_string_connected_without_cell(source_string, from_pos):
                        logger.debug("Skipped mutation because it would cause a string to lose continuity")
                        continue

                    logger.debug("Ensuring mutation doesn't exceed max string size...")
                    if len(target_string.Cells) >= self.max_string_cells:
                        logger.debug(f"Skipped mutation because {target_string.Name} already has "
                                    f"the maximum cell count of {self.max_string_cells}")
                        continue

                    self._swap_cell_string(from_pos, source_string, target_string)

                    move_found = True
                    break

                if move_found:
                    break

            if not move_found:
                logger.warning("No valid mutation found that preserves string continuity.")

    def undo_mutate(self):
        """
        Undo a previous mutation by restoring a cell to its original string.

        :param remove_from_string: Original string
        :param cell_to_move: Cell to restore
        """
        cell_to_move, original_string = self.last_move
        self._update_cell_membership(cell_to_move, original_string)

    def _swap_cell_string(self, from_pos, source_string, target_string) -> None:
        """
        Move the cell at position `from_pos` from `source_string` to `target_string`.
        """
        # Get reference to cell
        cell_to_move = self.cell_registry[from_pos]
        logger.info(f"Moving cell from string {source_string.Name} to {target_string.Name}...")

        self.last_move = (cell_to_move, source_string)
        self._update_cell_membership(cell_to_move, target_string)

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

    # ============================================================
    # STATE MANAGEMENT (INTERNAL)
    # ============================================================

    def _update_cell_membership(self, cell: object, target_string: object) -> None:
        """
        Centralized method to handle cell movement. 
        Updates the internal cache, the C# ArraySpec, and triggers recoloring.
        """
        # 1. Update internal persistent map
        pos = self.get_cell_position(cell)
        self.pos_to_string[pos] = target_string

        # 2. Update the C# API
        self.aspec.AddCellToCellString(cell, target_string)
        self.aspec.Recolor()
