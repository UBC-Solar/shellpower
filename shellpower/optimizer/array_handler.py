from shellpower import ArraySimulator, ArraySimulatorInput
from collections import defaultdict
from pathlib import Path
from tqdm import tqdm
import collections
import logging
import random


logger = logging.getLogger(__name__)

point = tuple[int, int]

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
        self.cell_registry: dict[point, object] = self.build_cell_pos_to_obj_map()  # pos_key -> cell_object

        # Compute adjacency based on the position keys
        logger.info("Computing geometric adjacency...")
        self.geometric_pairs: list[tuple[object, object]] = self.compute_geometric_adjacency()

        self.adj_lookup: dict[point, list[point]] = defaultdict(list)
        for a_pos, b_pos in self.geometric_pairs:
            self.adj_lookup[a_pos].append(b_pos)
            self.adj_lookup[b_pos].append(a_pos)

        # Persistent cell-string lookup
        logger.info("Initializing cell-to-string lookup...")
        self.pos_to_string: dict[point, object] = self.build_cell_to_string_map()

        self._simulator = ArraySimulator()

        # List of mutations of the form (cell_pos, string_from, string_to)
        self._mutation_stack: list[tuple[point, object, object]] = []

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

    def build_cell_to_string_map(self) -> dict[point, object]:
        """Returns a mapping of position_tuple -> string_object."""
        mapping = {}
        for string in self.aspec.Strings:
            for cell in string.Cells:
                pos = self.get_cell_position(cell)
                mapping[pos] = string
        return mapping

    def build_cell_pos_to_obj_map(self) -> dict[point, object]:
        """Returns a mapping of position_tuple -> cell_object."""
        mapping = {}
        for string in self.aspec.Strings:
            for cell in string.Cells:
                pos: point = self.get_cell_position(cell)
                mapping[pos] = cell 
                # We use the position as the absolute source of truth
        return mapping

    def mutate_adjacent(self) -> bool:
        """
        Move a random cell to a neighboring string, ensuring the source string
        is not split into two disconnected components.

        Returns true if a mutation was successfully found, and false otherwise
        """

        # string name -> [(cell_pos on this, cell_pos on other), ...]
        cell_pair_map: dict[str, list[tuple[point, point]]] = self.get_string_cell_pair_map()
        strings_with_pairs: list[str] = [string for string in cell_pair_map.keys()]
        random.shuffle(strings_with_pairs)

        for string_a_name in strings_with_pairs:

            # Choose which string to move from
            possible_pairs = cell_pair_map[string_a_name]
            random.shuffle(possible_pairs)

            for a_pos, b_pos in possible_pairs:
                string_b: object = self.pos_to_string[b_pos]
                string_a: object = self.pos_to_string[a_pos]

                try:
                    self._update_cell_membership(a_pos, string_a, string_b)
                except ValueError:
                    # Move breaks string size constraint or continuity
                    continue

                return True

        logger.warning("No valid mutation found that preserves string continuity.")
        return False

    def dual_mutate_adjacent(self) -> bool:
        """
        Choose two neighboring strings A and B. Then move a random cell from string A to string B,
        and another from string B to string A.

        Returns true if a mutation pair was successfully found, and false otherwise
        """

        # string name -> [(cell_pos on this, cell_pos on other), ...]
        cell_pair_map: dict[str, list[tuple[point, point]]] = self.get_string_cell_pair_map()
        strings_with_pairs: list[str] = [string for string in cell_pair_map.keys()]

        chosen_cell_a_pos = None
        chosen_string_a = None
        chosen_string_b = None

        random.shuffle(strings_with_pairs)
        for string_a_name in strings_with_pairs:

            # Choose which string to move from
            possible_pairs = cell_pair_map[string_a_name]

            random.shuffle(possible_pairs)
            for a_pos, b_pos in possible_pairs:
                string_a: object = self.pos_to_string[a_pos]
                string_b: object = self.pos_to_string[b_pos]

                try:
                    self._update_cell_membership(a_pos, string_a, string_b)
                    chosen_cell_a_pos = a_pos
                    chosen_string_a = string_a
                    chosen_string_b = string_b
                    break
                except ValueError:
                    # Move breaks string size constraint or continuity
                    continue

            if chosen_cell_a_pos is not None:
                break

        if chosen_cell_a_pos is None:
            logger.warning("No valid mutation found that preserves string continuity.")
            return False

        # Find other pairs which move a cell from b to a
        # Re-compute after the first move so adjacency reflects current state
        updated_pair_map = self.get_string_cell_pair_map()
        possible_pairs = [
            pair for pair in updated_pair_map[chosen_string_b.Name]
            if self.pos_to_string[pair[1]].Name == chosen_string_a.Name
        ]
        logger.debug(f"Found {len(possible_pairs)} possible b->a swaps!")

        random.shuffle(possible_pairs)
        for b_pos, a_pos in possible_pairs:
            string_b: object = self.pos_to_string[b_pos]
            string_a: object = self.pos_to_string[a_pos]

            try:
                self._update_cell_membership(b_pos, string_b, string_a)
            except ValueError:
                # Move breaks string size constraint or continuity
                continue

            return True

        logger.warning("Found move from string a to b, but not b to a! Reverting...")
        self.undo_mutate()
        return False

    def get_adjacent_pairs(self) -> list[tuple[point, point]]:
        logger.debug("Building neighbouring cell pair list...")
        valid_pairs = [
            (a_pos, b_pos)
            for (a_pos, b_pos) in self.geometric_pairs
            if self.pos_to_string[a_pos].Name != self.pos_to_string[b_pos].Name # Compare by Name/ID
        ]
        return valid_pairs

    def get_string_cell_pair_map(self) -> dict[str, list[tuple[point, point]]]:
        valid_pairs: list[tuple[point, point]] = self.get_adjacent_pairs()

        string_cell_pair_map: dict[str, list[tuple[point, point]]] = defaultdict(list)
        for cell_a, cell_b in valid_pairs:
            string_a = self.pos_to_string[cell_a]
            string_b = self.pos_to_string[cell_b]
            string_cell_pair_map[string_a.Name].append((cell_a, cell_b))
            string_cell_pair_map[string_b.Name].append((cell_b, cell_a))

        return string_cell_pair_map

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

    def _update_cell_membership(self, cell_pos: point, source_string: object, target_string: object) -> None:
        """
        Move the cell at position `from_pos` from `source_string` to `target_string`.

        Centralized method to handle cell movement. 
        Updates the internal cache, movement stack, the C# ArraySpec, and triggers recoloring.

        Raises ValueError if the string configuration is invalid.
        """

        # 0. Validate change
        logger.debug("Ensuring mutation doesn't exceed max string size...")
        if len(target_string.Cells) >= self.max_string_cells:
            msg = f"Skipped mutation because {target_string.Name} already has " \
                f"the maximum cell count of {self.max_string_cells}"
            logger.debug(msg)
            raise ValueError(msg)
        logger.debug("Ensuring mutation maintains continuity...")
        if not self.is_string_connected_without_cell(source_string, cell_pos):
            msg = "Skipped mutation because it would cause a string to lose continuity"
            logger.debug(msg)
            raise ValueError(msg)

        logger.info(f"Moving cell from string {source_string.Name} to {target_string.Name}...")

        # 1. Update the cell movement stack
        self._mutation_stack.append((cell_pos, source_string, target_string))

        # 2. Update internal persistent map
        self.pos_to_string[cell_pos] = target_string

        # 3. Update the C# API
        cell_to_move = self.cell_registry[cell_pos]
        self.aspec.AddCellToCellString(cell_to_move, target_string)
        self.aspec.Recolor()

    def undo_mutate(self):
        """
        Undo a the last mutation by restoring a cell to the string it was previously on.

        The ArrayHandler manages a stack of cell movements, so undo_mutate can be chained.

        :param remove_from_string: Original string
        :param cell_to_move: Cell to restore
        """

        # 1. Update and query from the cell movement stack
        cell_pos, move_source, move_target = self._mutation_stack.pop()

        # 2. Update internal persistent map
        self.pos_to_string[cell_pos] = move_source

        # 3. Update the C# API
        cell_to_move = self.cell_registry[cell_pos]
        self.aspec.AddCellToCellString(cell_to_move, move_source)
        self.aspec.Recolor()

    def assert_state_consistent(self):
        for string in self.aspec.Strings:
            for cell in string.Cells:
                pos = self.get_cell_position(cell)
                assert self.pos_to_string[pos] == string, \
                    f"State desync at {pos}: expected {string.Name}, got {self.pos_to_string[pos].Name}"
