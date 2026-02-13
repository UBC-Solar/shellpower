using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace SSCP.ShellPower;

public static class SimulationBuilder
{
    public static ArraySimulationStepInput BuildInput(SimulationRequest req)
    {
        var mesh = ArraySpec.LoadMesh(req.MeshPath);
        Image<Rgba32> layout = Image.Load<Rgba32>(req.LayoutTexturePath);

        var array = new ArraySpec
        {
            Mesh = mesh,
            LayoutTexture = layout,
        };

        // if you have a bypass-diode file format:
        // if (!string.IsNullOrWhiteSpace(req.BypassDiodesPath)) BypassDiodesLoader.Apply(array, req.BypassDiodesPath);

        array.ReadStringsFromColors();

        return new ArraySimulationStepInput
        {
            Array = array,
            Latitude = req.Latitude,
            Longitude = req.Longitude,
            Heading = req.HeadingRadians,
            Utc = req.Utc,
            TimezoneOffsetHours = req.TimezoneOffsetHours,
            Temperature = req.TemperatureC,
            Irradiance = req.DirectIrradianceWm2,
            IndirectIrradiance = req.DiffuseIrradianceWm2
        };
    }

    public static SimulationResponse ToResponse(ArraySimulationStepOutput s) =>
        new() {
            ArrayArea = s.ArrayArea,
            ArrayLitArea = s.ArrayLitArea,
            WattsInsolation = s.WattsInsolation,
            WattsOutputByCell = s.WattsOutputByCell,
            WattsOutput = s.WattsOutput,
            Strings = s.Strings
        };
}