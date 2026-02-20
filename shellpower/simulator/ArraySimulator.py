import subprocess
import datetime
import os
from shellpower.internal import ShellPowerWorker
from shellpower.simulator import ArraySimulatorInput, ArraySimulatorOutput

class ArraySimulator:
    def __init__(self):
        # Finds the path to the ShellPower.Worker binary
        self.worker_exe = ShellPowerWorker().path

    def simulate(self, simulator_input: ArraySimulatorInput) -> ArraySimulatorOutput:
        # 1. Serialize using Pydantic (handles datetime/path automatically)
        request_json = simulator_input.model_dump_json()

        # 2. Setup the Environment
        env = os.environ.copy()
        env["LIBGL_ALWAYS_SOFTWARE"] = "1"
        env["OpenTK_Windowing_GraphicsLibraryFramework_GLFW_PLATFORM"] = "egl"

        # 3. Execute the Worker
        # We use xvfb-run to provide the virtual frame buffer for OpenGL
        cmd = ["xvfb-run", "--auto-servernum", str(self.worker_exe)]
        
        process = subprocess.Popen(
            cmd,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            env=env
        )

        # 4. Pipe JSON in and capture the response
        stdout, stderr = process.communicate(input=request_json)

        if process.returncode != 0:
            print(f"STDOUT: {stdout}") # See if it printed anything before dying
            print(f"STDERR: {stderr}") # This is where driver errors live
            raise RuntimeError(f"C# Worker Error (Exit {process.returncode})")

        # 5. Parse back into Pydantic Output model
        from shellpower import ArraySimulatorOutput
        return ArraySimulatorOutput.model_validate_json(stdout)

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
