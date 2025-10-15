from pydantic import BaseModel, ConfigDict, AfterValidator
from datetime import timezone, datetime
from typing import Annotated
import pathlib


def _check_path_exists(v):
    path = pathlib.Path(v)
    if not path.exists():
        raise ValueError(f"Path does not exist: {path}")

    return path


def _ensure_timezone_and_convert_utc(v: datetime) -> datetime:
    if v.tzinfo is None:
        raise ValueError("Datetime must be timezone-aware")
    return v.astimezone(timezone.utc)


class ArraySimulatorInput(BaseModel):
    model_config = ConfigDict(frozen=True, arbitrary_types_allowed=True)

    MeshPath: Annotated[pathlib.Path | str, AfterValidator(_check_path_exists)]
    LayoutTexturePath: Annotated[pathlib.Path | str, AfterValidator(_check_path_exists)]
    Latitude: float
    Longitude: float
    HeadingRadians: float
    Utc: Annotated[datetime, _ensure_timezone_and_convert_utc]
    TimezoneOffsetHours: float
    TemperatureC: float
    DirectIrradianceWm2: float
    DiffuseIrradianceWm2: float

    def model_dump(self, **kwargs) -> dict:
        json_dumped = super().model_dump(**kwargs)

        json_dumped["Utc"] = json_dumped["Utc"].isoformat()     # datetime is not JSON serializable, so we encode as str

        # Path is not JSON serializable, so we encode as str
        json_dumped["MeshPath"] = str(json_dumped["MeshPath"].absolute().resolve())
        json_dumped["LayoutTexturePath"] = str(json_dumped["LayoutTexturePath"].absolute().resolve())

        return json_dumped
