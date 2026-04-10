"""
Configuration for array optimization conditions

This defines the environment in which the arrays will be simulated
"""

import datetime
from math import pi

brainerd_10am_north = {
    "Latitude": 46.4136132,
    "Longitude": -94.2774524,
    "HeadingRadians": 0,
    "Utc": datetime.datetime(
        2026, 7, 9, 23, 0, 0, tzinfo=datetime.timezone.utc
    ),  # CDT is UTC-6
    "TimezoneOffsetHours": 0,  # unused?
    "TemperatureC": 25.0,
    "DirectIrradianceWm2": 1050.0,
    "DiffuseIrradianceWm2": 70.0,
}

brainerd_1pm_west = {
    "Latitude": 46.4136132,
    "Longitude": -94.2774524,
    "HeadingRadians": pi / 2,
    "Utc": datetime.datetime(
        2026, 7, 9, 2, 0, 0, tzinfo=datetime.timezone.utc
    ),  # CDT is UTC-6
    "TimezoneOffsetHours": 0,  # unused?
    "TemperatureC": 25.0,
    "DirectIrradianceWm2": 1050.0,
    "DiffuseIrradianceWm2": 70.0,
}

brainerd_3pm_east = {
    "Latitude": 46.4136132,
    "Longitude": -94.2774524,
    "HeadingRadians": -pi / 2,
    "Utc": datetime.datetime(
        2026, 7, 9, 4, 0, 0, tzinfo=datetime.timezone.utc
    ),  # CDT is UTC-6
    "TimezoneOffsetHours": 0,  # unused?
    "TemperatureC": 25.0,
    "DirectIrradianceWm2": 1050.0,
    "DiffuseIrradianceWm2": 70.0,
}

brainerd_5pm_south = {
    "Latitude": 46.4136132,
    "Longitude": -94.2774524,
    "HeadingRadians": pi,
    "Utc": datetime.datetime(
        2026, 7, 9, 6, 0, 0, tzinfo=datetime.timezone.utc
    ),  # CDT is UTC-6
    "TimezoneOffsetHours": 0,  # unused?
    "TemperatureC": 25.0,
    "DirectIrradianceWm2": 1050.0,
    "DiffuseIrradianceWm2": 70.0,
}

test_cases = [brainerd_10am_north, brainerd_1pm_west, brainerd_3pm_east, brainerd_5pm_south]
test_case_names = ["brainerd_10am_north", "brainerd_1pm_west", "brainerd_3pm_east", "brainerd_5pm_south"]

if __name__ == "__main__":

    from pathlib import Path
    from shellpower.simulator.Simulation import Simulation
    from shellpower import ArraySimulator, ArraySimulatorInput, ArraySimulatorOutput
    import datetime

    PROJECT_ROOT = Path(__file__).parent.parent.parent
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

        """
        hour: 0, power: 1352.494454 <-- 11AM
        hour: 1, power: 1453.749220 <-- 12PM
        hour: 2, power: 1436.234421 <-- NOON is 1:22 on this location/date (so this is about 1pm)
        hour: 3, power: 1303.853989 <-- 2pm
        hour: 4, power: 1078.283607 <-- 3pm
        hour: 5, power: 802.0301091 <-- 4pm
        hour: 6, power: 527.8844553 <-- 5
        hour: 7, power: 304.6178147 <-- 6
        hour: 8, power: 163.5031240 <-- 7
        hour: 9, power: 113.0401403 <-- 8
        hour: 10, power: 102.574004 <-- 9
        hour: 11, power: 101.933301 <-- 
        hour: 12, power: 101.558823 <-- 
        hour: 13, power: 101.441727 <-- 
        hour: 14, power: 101.438125 <-- 
        hour: 15, power: 101.438128 <-- 
        hour: 16, power: 103.364528 <-- 
        hour: 17, power: 108.114985 <-- 
        hour: 18, power: 116.955450 <-- 5
        hour: 19, power: 176.385070 <-- 6
        hour: 20, power: 328.707211 <-- 7
        hour: 21, power: 568.590759 <-- 8
        hour: 22, power: 855.582571 <-- 9
        hour: 23, power: 1135.14076 <-- 10am
        """