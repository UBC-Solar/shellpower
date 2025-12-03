from shellpower import ArraySimulator, ArraySimulatorInput, ArraySimulatorOutput
import datetime


def recursive_dump(json: dict, collapse_threshold: int = 5) -> str:
    out_str = ""

    for key in json.keys():
        val = json[key]
        val_type = type(val)
        out_str += f"{key} {val_type}: "

        if val_type == dict:
            # Get the dump for this dict
            dict_str = recursive_dump(val)

            out_str += "\n"
            # Add the lines, with an indent
            for line in dict_str.splitlines():
                out_str += f"|   {line}\n"

        if type(val) in (list, tuple):
            _iterator = iter(val)

            # val is iterable
            if len(val) >= collapse_threshold:
                out_str += f" [length {len(val)}, showing first element]\n"
                first_element = val[0]
                if type(first_element) == dict:
                    # Get the dump for this dict
                    dict_str = recursive_dump(first_element)
                    # Add the lines, with an indent
                    for line in dict_str.splitlines():
                        out_str += f"|   {line}\n"
                else:
                    out_str += f"    {first_element}\n"

        out_str += f"{str(val)}\n"

    # Wrap dict in brackets
    bracketed_str = "{\n"
    for line in out_str.splitlines():
        bracketed_str += f"|   {line}\n"
    bracketed_str += "}\n"

    return bracketed_str

if __name__ == "__main__":
    simulator = ArraySimulator()

    simulator_input = ArraySimulatorInput(**{
        "MeshPath": "./arrays/luminos/luminos.stl",
        "LayoutTexturePath": "./arrays/luminos/luminos-splines-6-string-no-bypass-rot.png",
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

    out_dump = recursive_dump(output.model_dump())
    breakpoint()

    print(output.WattsOutputByCell)
