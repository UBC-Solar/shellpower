from shellpower.simulator import ArraySimulatorOutput, ArraySimulatorInput
from shellpower.internal import ShellPowerExecutable
import json, subprocess, sys, datetime


class ArraySimulator:
    def __init__(self):
        self._executable = ShellPowerExecutable()

    def simulate(self, simulator_input: ArraySimulatorInput) -> ArraySimulatorOutput:
        proc = subprocess.run(
            [str(self._executable.path)],
            input=json.dumps(simulator_input.model_dump()).encode(),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE
        )

        if proc.returncode != 0:
            print(proc.stderr.decode(), file=sys.stderr)
            sys.exit(proc.returncode)

        resp = json.loads(proc.stdout.decode())

        simulator_output = ArraySimulatorOutput(**resp)

        return simulator_output


if __name__ == "__main__":
    simulator = ArraySimulator()

    simulator_input = ArraySimulatorInput(**{"MeshPath": "./../arrays/luminos/luminos.stl",
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

    output = simulator.simulate(simulator_input)

    print(output.WattsOutputByCell)
