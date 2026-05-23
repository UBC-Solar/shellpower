"""
Configuration for array optimization conditions

This defines the environment in which the arrays will be simulated
"""

from datetime import datetime, timedelta, timezone
from math import pi
from pprint import pprint

def create_test_case_brainerd(
        year: int,
        month: int,
        day: int,
        hour: int,
        heading_deg: float,
        timezone_offset_hours: float = 0.0,
        temperature_c: float = 25.0,
        direct_irradiance_wm2: float = 1050.0,
        diffuse_irradiance_wm2: float = 70.0,
    ) -> dict:
    """
    Create a test case for the python shelllpower simulator at Brainerd International Raceway

    :param datetime time_utc:
    :param float heading_deg:
    :param float timezone_offset_hours:
    :param float temperature_c:
    :param float direct_irradiance_wm2:
    :param float diffuse_irradiance_wm2:
    """

    hour_offset: int = 13
    sim_datetime = datetime(year, month, day, (hour + hour_offset) % 24, 0, 0, tzinfo=timezone.utc)
    # NO IDEA why the time needs this offset, but by experimentation
    # we find at 2025-07-09 that the peak irradiance is at hour=2 (am), but since the
    # actual peak irradiance was 1:22PM we say that this was probably actually 1:22PM~1PM.
    # So to get 12AM brainerd time we need to input hour=13 to shellpower.

    output: dict = {
        "Latitude": 46.4136132,
        "Longitude": -94.2774524,
        "HeadingRadians": pi / 180 * heading_deg,
        "Utc": sim_datetime,
        "TimezoneOffsetHours": timezone_offset_hours,  # unused?
        "TemperatureC": temperature_c,
        "DirectIrradianceWm2": direct_irradiance_wm2,
        "DiffuseIrradianceWm2": diffuse_irradiance_wm2,
    }

    return output

# FSGP 2026 is on July 21-23
year = 2026
month = 7
day = 22

# ASC Regs 2026 Rev-B: 14.9.E Track Hours
# The track will be open for driving from 10:00 am – 6:00 pm local time (Day 1) and 9:00 am – 5:00 pm
# local time for Days 2 and 3.
hours = [
    9,
    11,
    13,
    15,
    17,
]

headings_deg = [
    0,
    45,
    90,
    135,
    180,
    -45,
    -90,
    -135,
]

BRAINERD_TEST_CASES = []
BRAINERD_TEST_CASE_NAMES = []

# Generate 20 test cases
for hour in hours:
    for heading_deg in headings_deg:
        test_case = create_test_case_brainerd(
            year=year,
            month=month,
            day=day,
            hour=hour,
            heading_deg=heading_deg
        )
        BRAINERD_TEST_CASES.append(
            test_case
        )
        BRAINERD_TEST_CASE_NAMES.append(
            f"brainerd_{hour}:00_{heading_deg}deg"
        )


# Manually created

# brainerd_10am_north = {
#     "Latitude": 46.4136132,
#     "Longitude": -94.2774524,
#     "HeadingRadians": 0,
#     "Utc": datetime(
#         2026, 7, 9, 23, 0, 0, tzinfo=timezone.utc
#     ),  # CDT is UTC-6
#     "TimezoneOffsetHours": 0,  # unused?
#     "TemperatureC": 25.0,
#     "DirectIrradianceWm2": 1050.0,
#     "DiffuseIrradianceWm2": 70.0,
# }

# brainerd_1pm_west = {
#     "Latitude": 46.4136132,
#     "Longitude": -94.2774524,
#     "HeadingRadians": pi / 2,
#     "Utc": datetime(
#         2026, 7, 9, 2, 0, 0, tzinfo=timezone.utc
#     ),  # CDT is UTC-6
#     "TimezoneOffsetHours": 0,  # unused?
#     "TemperatureC": 25.0,
#     "DirectIrradianceWm2": 1050.0,
#     "DiffuseIrradianceWm2": 70.0,
# }

# brainerd_3pm_east = {
#     "Latitude": 46.4136132,
#     "Longitude": -94.2774524,
#     "HeadingRadians": -pi / 2,
#     "Utc": datetime(
#         2026, 7, 9, 4, 0, 0, tzinfo=timezone.utc
#     ),  # CDT is UTC-6
#     "TimezoneOffsetHours": 0,  # unused?
#     "TemperatureC": 25.0,
#     "DirectIrradianceWm2": 1050.0,
#     "DiffuseIrradianceWm2": 70.0,
# }

# brainerd_5pm_south = {
#     "Latitude": 46.4136132,
#     "Longitude": -94.2774524,
#     "HeadingRadians": pi,
#     "Utc": datetime(
#         2026, 7, 9, 6, 0, 0, tzinfo=timezone.utc
#     ),  # CDT is UTC-6
#     "TimezoneOffsetHours": 0,  # unused?
#     "TemperatureC": 25.0,
#     "DirectIrradianceWm2": 1050.0,
#     "DiffuseIrradianceWm2": 70.0,
# }

# test_cases = [brainerd_10am_north, brainerd_1pm_west, brainerd_3pm_east, brainerd_5pm_south]
# test_case_names = ["brainerd_10am_north", "brainerd_1pm_west", "brainerd_3pm_east", "brainerd_5pm_south"]

def test_time_shift() -> None:
    from pathlib import Path
    from shellpower.simulator.Simulation import Simulation
    from shellpower import ArraySimulator, ArraySimulatorInput, ArraySimulatorOutput

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
            "Utc": datetime(2026, 7, 9, hour, 0, 0, tzinfo=timezone.utc),
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

        pprint(f"hour: {hour}, power: {output.WattsOutputByCell}")

        """
        hour: 0, power: 1352.494454 <-- 11AM
        hour: 1, power: 1453.749220 <-- 12PM
        hour: 2, power: 1436.234421 <-- SOLAR NOON is 1:22 on this location/date (so this is about 1pm)
        hour: 3, power: 1303.853989 <-- 2pm
        hour: 4, power: 1078.283607 <-- 3pm
        hour: 5, power: 802.0301091 <-- 4pm
        hour: 6, power: 527.8844553 <-- 5
        hour: 7, power: 304.6178147 <-- 6
        hour: 8, power: 163.5031240 <-- 7
        hour: 9, power: 113.0401403 <-- 8
        hour: 10, power: 102.574004 <-- 9pm
        hour: 11, power: 101.933301 <-- 10pm
        hour: 12, power: 101.558823 <-- 11pm
        hour: 13, power: 101.441727 <-- 12am / 0am
        hour: 14, power: 101.438125 <-- 1
        hour: 15, power: 101.438128 <-- 2
        hour: 16, power: 103.364528 <-- 3
        hour: 17, power: 108.114985 <-- 4
        hour: 18, power: 116.955450 <-- 5
        hour: 19, power: 176.385070 <-- 6
        hour: 20, power: 328.707211 <-- 7
        hour: 21, power: 568.590759 <-- 8
        hour: 22, power: 855.582571 <-- 9
        hour: 23, power: 1135.14076 <-- 10am
        """

if __name__ == "__main__":

    # test_time_shift()

    # 10AM north
    pprint(create_test_case_brainerd(
        year=2026, month=7, day=9, hour=10,
        heading_deg=0
    ))

    # 1pm west
    pprint(create_test_case_brainerd(
        year=2026, month=7, day=9, hour=13,
        heading_deg=90
    ))

    # 3pm east
    pprint(create_test_case_brainerd(
        year=2026, month=7, day=9, hour=15,
        heading_deg=-90
    ))

    # 5pm south
    pprint(create_test_case_brainerd(
        year=2026, month=7, day=9, hour=17,
        heading_deg=180
    ))