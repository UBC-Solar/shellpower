from pydantic import BaseModel, ConfigDict


class ArraySimulatorOutput(BaseModel):
    model_config = ConfigDict(frozen=True, arbitrary_types_allowed=True)

    ArrayArea: float
    Strings: list
    WattsInsolation: float
    WattsOutput: float
    WattsOutputByCell: float