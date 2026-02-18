from shellpower import ArraySimulator, ArraySimulatorInput, ArraySimulatorOutput
import datetime

if __name__ == "__main__":
    from pathlib import Path
    from shellpower.simulator.Simulation import Simulation
    from shellpower import ArraySimulator, ArraySimulatorInput
    import datetime

    PROJECT_ROOT = Path(__file__).parent.parent
    BASE_TEXTURE_PATH = PROJECT_ROOT / "arrays" / "v4" / "cascadia_v1_y160x90.png"
    TOP_SHELL_MODEL = PROJECT_ROOT / "arrays" / "v4" / "v4-blender-guillotined.stl"
    BYPASS_DIODES_JSON = PROJECT_ROOT / "shellpower" / "bypass_diodes.json"

    # Load ArraySpec
    aspec = Simulation.ArraySpec(
        str(BASE_TEXTURE_PATH),
        str(TOP_SHELL_MODEL),
        str(BYPASS_DIODES_JSON),
    )

    for hour in range(24):
        test_config = {
            "Latitude": 46.4136132,
            "Longitude": -94.2774524,
            "HeadingRadians": 0,
            "Utc": datetime.datetime(2026, 7, 9, hour, 0, 0,
                                     tzinfo=datetime.timezone.utc),
            "TimezoneOffsetHours": 0,  # unused?
            "TemperatureC": 25.0,
            "DirectIrradianceWm2": 1050.0,
            "DiffuseIrradianceWm2": 70.0
        }

        simulator = ArraySimulator()

        simulator_input = ArraySimulatorInput(
            **test_config,
            LayoutTexturePath=str(BASE_TEXTURE_PATH),
            MeshPath=str(TOP_SHELL_MODEL),
        )

        output: ArraySimulatorOutput = simulator.simulate(simulator_input)

        print(f"hour: {hour}, power: {output.WattsOutputByCell}")


