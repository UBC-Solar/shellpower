"""
Configuration for array optimization conditions

This defines the environment in which the arrays will be simulated
"""
import datetime

ncm_motorsports_park_config = {
    "Latitude": -23.7,
    "Longitude": 133.8,
    "HeadingRadians": 3.141592653589793,
    "Utc": datetime.datetime(2019, 10, 16, 13, 0, 0,
                             tzinfo=datetime.timezone.utc),
    "TimezoneOffsetHours": 9.5,
    "TemperatureC": 25.0,
    "DirectIrradianceWm2": 1050.0,
    "DiffuseIrradianceWm2": 70.0
}