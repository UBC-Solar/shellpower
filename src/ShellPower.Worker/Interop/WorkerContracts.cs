namespace SSCP.ShellPower;

public sealed class SimulationRequest
{
    public required string MeshPath { get; init; }
    public required string LayoutTexturePath { get; init; }
    public string? BypassDiodesPath { get; init; }

    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public required double HeadingRadians { get; init; }
    public required DateTime Utc { get; init; }
    public double TimezoneOffsetHours { get; init; } = 0;

    public double TemperatureC { get; init; } = 25;
    public double DirectIrradianceWm2 { get; init; } = 1000;
    public double DiffuseIrradianceWm2 { get; init; } = 0;

    public BoundsSpec? LayoutBounds { get; init; }
    public double? EncapsulationLoss { get; init; }
    public CellSpec? Cell { get; init; }
    public DiodeSpec? BypassDiode { get; init; }
}

public sealed class SimulationResponse
{
    public required double ArrayArea { get; init; }
    public required double ArrayLitArea { get; init; }
    public required double WattsInsolation { get; init; }
    public required double WattsOutputByCell { get; init; }
    public required double WattsOutput { get; init; }
}