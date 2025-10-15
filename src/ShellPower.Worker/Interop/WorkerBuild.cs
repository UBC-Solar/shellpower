using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace SSCP.ShellPower;

public static class SimulationBuilder
{
    public static Mesh LoadMesh(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        IMeshParser parser = ext switch
        {
            "stl"   => new MeshParserStl(),
            "3dxml" => new MeshParser3DXml(),
            _       => throw new ArgumentException($"Unsupported mesh type: .{ext}")
        };
        parser.Parse(path);
        var mesh = parser.GetMesh();
        var size = mesh.BoundingBox.Max - mesh.BoundingBox.Min;
        if (size.Length() > 1000) mesh = MeshUtils.Scale(mesh, 0.001f);
        return mesh;
    }

    public static ArraySimulationStepInput BuildInput(SimulationRequest req)
    {
        var mesh   = LoadMesh(req.MeshPath);
        Image<Rgba32> layout = Image.Load<Rgba32>(req.LayoutTexturePath);

        var array = new ArraySpec
        {
            Mesh = mesh,
            LayoutTexture = layout,
            LayoutBounds = req.LayoutBounds ?? new BoundsSpec { MinX=-0.115, MaxX=2.035, MinZ=-0.23, MaxZ=4.59 },
            EncapsulationLoss = req.EncapsulationLoss ?? 0.025
        };

        if (req.Cell is not null) array.CellSpec = req.Cell;
        else { array.CellSpec.IscStc=6.27; array.CellSpec.VocStc=0.686; array.CellSpec.DIscDT=-0.0020; array.CellSpec.DVocDT=-0.0018; array.CellSpec.Area=0.015555; array.CellSpec.NIdeal=1.26; array.CellSpec.SeriesR=0.003; }

        if (req.BypassDiode is not null) array.BypassDiodeSpec = req.BypassDiode;
        else array.BypassDiodeSpec.VoltageDrop = 0.35;

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