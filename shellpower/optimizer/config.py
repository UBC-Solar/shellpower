"""
Configuration for array optimization conditions

This defines the environment in which the arrays will be simulated
"""

import datetime

brainerd_afternoon = {
    "Latitude": 46.4136132,
    "Longitude": -94.2774524,
    "HeadingRadians": 0,
    "Utc": datetime.datetime(
        2026, 7, 9, 0, 0, 0, tzinfo=datetime.timezone.utc
    ),  # CDT is UTC-6
    "TimezoneOffsetHours": 0,  # unused?
    "TemperatureC": 25.0,
    "DirectIrradianceWm2": 1050.0,
    "DiffuseIrradianceWm2": 70.0,
}
