from shellpower import ArraySimulator, ArraySimulatorInput, ArraySimulatorOutput
import datetime

if __name__ == "__main__":
    from shellpower.simulator.Simulation import Simulation

    idk = Simulation.ArraySpec("./../arrays/luminos/luminos-splines-6-string-no-bypass-rot.png", "./../arrays/luminos/luminos.stl", "./bypass_diodes.json")

    print(dir(idk))

    simulator = ArraySimulator()

    simulator_input = ArraySimulatorInput(**{
        "MeshPath": "./../arrays/luminos/luminos.stl",
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

    output: ArraySimulatorOutput = simulator.simulate(simulator_input)

    print(output.WattsOutputByCell)
