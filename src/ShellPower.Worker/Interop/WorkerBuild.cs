using System.Text.Json;
using System.Text.Json.Serialization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace SSCP.ShellPower;

public static class SimulationBuilder
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    
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
        if (!string.IsNullOrWhiteSpace(req.BypassDiodesPath))
        {
            var text = File.ReadAllText(req.BypassDiodesPath);
            var fileModel = JsonSerializer.Deserialize<BypassLayoutFile>(text, _jsonOpts);
            if (fileModel is null) throw new InvalidOperationException("Empty or invalid JSON.");

            array.ImportBypassDiodes(fileModel);
        };

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
            WattsOutput = s.WattsOutput
        };
}