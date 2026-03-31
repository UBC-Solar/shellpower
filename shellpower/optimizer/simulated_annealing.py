from typing import Callable
import logging
import random
import math


logger = logging.getLogger(__name__)


class SimulatedAnnealing:
    def __init__(
        self,
        minimize_func: Callable[[], float],
        mutate_func: Callable[[], None],
        undo_mutate_func: Callable,
        init_temp: float,
        num_iters: int,
    ):
        """
        Instantiate a SimulatedAnnealing optimizer

        :param minimize_func: Objective function to minimize
        :param mutate_func: Function which traverses one node on the search space
        :param undo_mutate_func: Function to revert the last mutation/traversal. Returns the number of cell swaps conducted.
        :param init_temp: Initial temperature.
            Higher tempertures increase the probability that a worse mutation will be kept. Temperature T decays over time throughout the simulation.
            Note that physically, temperature has the same dimension as the objective function:
              - If a mutation worsens the objective by 0.5T, there is a 61% chance it will be kept.
              - If a mutation worsens the objective by T, there is a 37% chance it will be kept.
              - If a mutation worsens the objective by 2T, there is a 14% chance it will be kept.
        :param num_iters: Description
        """
        self._minimize_func = minimize_func
        self._mutate_func = mutate_func
        self._undo_mutate_func = undo_mutate_func
        self._init_temp = init_temp
        self._num_iters = num_iters

        self.scores: list[float] = []

    def temperature_linear(self, r: float) -> float:
        """
        Calculate the temperature for the search as a function of progress

        :param r: Fraction of the time budged elapsed so far
        :return: Description
        """
        # linear decay
        t = self._init_temp * (1 - r)

        assert t >= 0, f"Cannot have negative temperature! {r=}"
        return t

    def simulate(self):

        logger.info("Evaluating base config!")
        prev_score = self._minimize_func()
        self.scores.append(prev_score)

        for i in range(self._num_iters):
            logger.info(f"Starting simulated annealing iteration {i}!")

            # Mutate & evaluate
            num_changes = self._mutate_func()
            updated_score = self._minimize_func()

            progress_ratio = i / self._num_iters
            temp: float = self.temperature_linear(progress_ratio)

            if updated_score < prev_score:
                acceptance_probability = 1
                logger.info("New config is superior! Keeping changes...")
                self.scores.append(updated_score)
                prev_score = updated_score

            else:
                if temp == 0:
                    logger.warning(
                        f"Temperature is zero on iteration {i} of {self._num_iters}. Setting acceptance probability to zero."
                    )
                    acceptance_probability = 0
                else:
                    acceptance_probability = math.exp(
                        -(updated_score - prev_score) / temp
                    )
                    assert acceptance_probability <= 1, (
                        f"Acceptance probability > 1! {prev_score=}, {updated_score=}, {temp=}"
                    )

                logger.info(
                    f"New config is worse by {updated_score - prev_score:.4f}. "
                    f"Acceptance probability is {acceptance_probability * 100:.2f}%"
                )

                if acceptance_probability >= random.random():
                    logger.info("Accepted! Keeping changes...")
                    self.scores.append(updated_score)

                    # Only update prev_score when the change is kept!
                    prev_score = updated_score
                    pass
                else:
                    logger.info("Rejected! Undoing changes...")
                    self.scores.append(prev_score)
                    for i in range(num_changes):
                        self._undo_mutate_func()
