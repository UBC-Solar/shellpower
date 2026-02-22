from shellpower.simulator import ArraySimulatorOutput, ArraySimulatorInput
from shellpower.simulator.persistent import ShellPowerPersistentWorker
from shellpower.internal import ShellPowerWorker
import datetime


class ArraySimulator:
    def __init__(self):
        self._executable = ShellPowerWorker()
        self._worker = ShellPowerPersistentWorker(str(self._executable.path))

    def simulate(self, simulator_input: ArraySimulatorInput) -> ArraySimulatorOutput:
        resp = self._worker.call(simulator_input.model_dump())
        return ArraySimulatorOutput(**resp)


if __name__ == "__main__":
    simulator = ArraySimulator()

    sim_input = ArraySimulatorInput(**{"MeshPath": "./../arrays/luminos/luminos.stl",
         "LayoutTexturePath": "./../arrays/luminos/luminos-splines-6-string-no-bypass-rot.png",
         "Latitude": -23.7,
         "Longitude": 133.8,
         "HeadingRadians": 3.141592653589793,
         "Utc": datetime.datetime(2019, 10, 16, 13, 0, 0, tzinfo=datetime.timezone.utc),
         "TimezoneOffsetHours": 9.5,
         "TemperatureC": 25.0,
         "DirectIrradianceWm2": 1050.0,
         "DiffuseIrradianceWm2": 70.0
    })

    output = simulator.simulate(sim_input)

    print(output.WattsOutputByCell)
