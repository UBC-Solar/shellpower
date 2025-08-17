using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;           // replaces OpenTK.Vector3/4
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;     // Avalonia bitmap
using Avalonia.Platform.Storage;  // IStorageProvider for file dialogs
using Avalonia.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Image = Avalonia.Controls.Image;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering;
using Avalonia.Threading;

namespace SSCP.ShellPower;

public partial class MainWindow : Window
{
    // --- model ---
    private ArraySimulationStepInput simInput = new();
    private Shadow? shadow;
    private string? meshFilename;

    // --- simulator ---
    private ArraySimulator? simulator;

    // --- UI elements (defined in XAML) ---
    private GLControl glControl = null!;              // OpenGL drawing surface
    private TextBlock labelArrPower = null!;          // replaces RTF label
    private ListBox outputStringsListBox = null!;
    private Image<Rgba32> outputArrayLayoutImage = null!;     // shows layout texture
    private DatePicker dtStartDate = null!;
    private TimePicker dtStartTime = null!;
    private DatePicker dtEndDate = null!;
    private TimePicker dtEndTime = null!;
    private Button btnSimulate = null!;
    private Button btnSaveRender = null!;

    public MainWindow()
    {
        InitializeComponent();
        AttachNamedControls();

        // init model
        simInput.Array = new ArraySpec();
        InitTimeAndPlace();
        InitializeArraySpec();
        InitializeConditions();
        CalculateSimStepGui();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void AttachNamedControls()
    {
        glControl = this.FindControl<GLControl>("GlView");
        labelArrPower = this.FindControl<TextBlock>("LabelArrPower");
        outputStringsListBox = this.FindControl<ListBox>("OutputStringsList");
        // outputArrayLayoutImage = this.FindControl<Image>("OutputArrayLayoutImage");
        dtStartDate = this.FindControl<DatePicker>("StartDate");
        dtStartTime = this.FindControl<TimePicker>("StartTime");
        dtEndDate = this.FindControl<DatePicker>("EndDate");
        dtEndTime = this.FindControl<TimePicker>("EndTime");
        btnSimulate = this.FindControl<Button>("BtnSimulate");
        btnSaveRender = this.FindControl<Button>("BtnSaveRender");

        btnSimulate.Click += (_, __) => _ = RunSingleSimAsync();
        btnSaveRender.Click += (_, __) => _ = SaveRenderAsync();
        outputStringsListBox.SelectionChanged += OutputStringsListBox_SelectionChanged;

        // Menu items are wired in XAML to these handlers
    }

    #region Init helpers
    private void InitTimeAndPlace()
    {
        // Alice Springs, heading due south; times in local timezone with offset
        simInput.Longitude = 133.8;
        simInput.Latitude = -23.7;
        simInput.Heading = MathF.PI;

        // Middle of WSC 2019
        simInput.Utc = new DateTime(2019, 10, 16, 8, 0, 0, DateTimeKind.Utc).AddHours(-9.5);
        simInput.TimezoneOffsetHours = 9.5; // Darwin, NT time

        // initialize pickers
        var localStart = simInput.Utc.AddHours(simInput.TimezoneOffsetHours);
        dtStartDate.SelectedDate = localStart.Date;
        dtStartTime.SelectedTime = localStart.TimeOfDay;
        dtEndDate.SelectedDate = localStart.Date;
        dtEndTime.SelectedTime = localStart.TimeOfDay + TimeSpan.FromHours(1);
    }

    private void InitializeArraySpec()
    {
        var array = simInput.Array;
        array.LayoutBounds = new BoundsSpec
        {
            MinX = -0.115,
            MaxX = 2.035,
            MinZ = -0.23,
            MaxZ = 4.59
        };
        // array.LayoutTexture = ArrayModelControl.DEFAULT_TEX; // TODO: provide Avalonia Bitmap equivalent
        array.EncapsulationLoss = 0.025; // 2.5 %

        // Sunpower C60 Bin I
        var cellSpec = simInput.Array.CellSpec;
        cellSpec.IscStc = 6.27;
        cellSpec.VocStc = 0.686;
        cellSpec.DIscDT = -0.0020; // approx, computed
        cellSpec.DVocDT = -0.0018;
        cellSpec.Area = 0.015555; // m^2
        cellSpec.NIdeal = 1.26; // fudge
        cellSpec.SeriesR = 0.003; // ohms

        var diodeSpec = simInput.Array.BypassDiodeSpec;
        diodeSpec.VoltageDrop = 0.35;

        // // If you want to preview the layout texture in UI
        // if (array.LayoutTexture is Image<Rgba32> avaloniaBmp)
        //     outputArrayLayoutImage = avaloniaBmp;
    }

    private void InitializeConditions()
    {
        simInput.Temperature = 25;        // STC, 25 °C
        simInput.Irradiance = 1050;       // not STC
        simInput.IndirectIrradiance = 70; // not STC
    }

    private void InitSimulator()
    {
        simulator ??= new ArraySimulator();
    }
    #endregion

    #region File I/O (cross‑platform dialogs)
    private async Task<string?> PickOpenFileAsync(string title, params string[] extensions)
    {
        var provider = this.StorageProvider;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = extensions.Length == 0 ? null :
                new[] { new FilePickerFileType(title) { Patterns = extensions.Select(e => "*." + e).ToArray() } }
        });
        return files?.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickSaveFileAsync(string title, string suggestedName, params string[] extensions)
    {
        var provider = this.StorageProvider;
        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = extensions.Length == 0 ? null :
                new[] { new FilePickerFileType(title) { Patterns = extensions.Select(e => "*." + e).ToArray() } }
        });
        return file?.TryGetLocalPath();
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        await new ContentDialog { Title = title, Message = message }.ShowAsync(this);
    }
    #endregion

    private async Task LoadModelAsync()
    {
        var file = await PickOpenFileAsync("Open Model", "stl", "3dxml");
        if (file == null) return;
        try
        {
            var mesh = LoadMesh(file);
            SetModel(mesh);
            meshFilename = file;
        }
        catch (Exception e)
        {
            await ShowErrorAsync("Error loading model", e.Message);
        }
        CalculateSimStepGui();
    }

    private Mesh LoadMesh(string filename)
    {
        string extension = Path.GetExtension(filename).Trim('.').ToLowerInvariant();
        IMeshParser parser = extension switch
        {
            "3dxml" => new MeshParser3DXml(),
            "stl" => new MeshParserStl(),
            _ => throw new ArgumentException("Unsupported file type: " + extension)
        };
        parser.Parse(filename);
        Mesh mesh = parser.GetMesh();
        Vector3 size = mesh.BoundingBox.Max - mesh.BoundingBox.Min;
        if (size.Length() > 1000)
            mesh = MeshUtils.Scale(mesh, 0.001f);

        Debug.WriteLine($"Loaded model {Path.GetFileName(filename)}, {mesh.triangles.Length} triangles, {size.X:0.00}x{size.Y:0.00}x{size.Z:0.00}m");
        return mesh;
    }

    private static Image<Rgba32> LoadTexture(string filename)
    {
        return SixLabors.ImageSharp.Image.Load<Rgba32>(filename);
    }

    private void SetModel(Mesh mesh)
    {
        Debug.WriteLine("computing shadows...");
        var newShadow = new Shadow(mesh);
        newShadow.Initialize(); // ensure it works before setting
        shadow = newShadow;

        // Hook into GL control to render a ShadowMeshSprite equivalent
        var center = Vector3.Divide(mesh.BoundingBox.Max + mesh.BoundingBox.Min, 2);
        glControl.Sprite = new ShadowMeshSprite(shadow);
        // {
        //     Position = new Vector4(-center, 1);
        // };

        simInput.Array.Mesh = mesh;
    }

    #region Simulation step + rendering
    private void CalculateSimStepGui()
    {
        if (shadow != null)
            UpdateShadowView();
    }

    private Vector3 CalculateSunDir()
    {
        var lightDir = ArraySimulator.GetSunDir(simInput);
        if (lightDir.Y < 0) // below horizon
            lightDir = Vector3.Zero;
        return lightDir;
    }

    private void UpdateShadowView()
    {
        if (shadow == null) return;
        Vector3 lightDir = CalculateSunDir();
        shadow.Light = new Vector4(lightDir, 0);
        shadow.ComputeShadows();
        glControl.InvalidateVisual(); // triggers render
    }
    #endregion

    #region UI handlers (wired from XAML menu)
    private async void OnOpenModel(object? sender, RoutedEventArgs e) => await LoadModelAsync();

    private async void OnOpenLayout(object? sender, RoutedEventArgs e)
    {
        // prompt user for a texture image; replace ArrayLayoutForm prompt with dialog
        var fname = await PickOpenFileAsync("Open Layout Texture", "png", "jpg", "jpeg", "bmp");
        if (fname == null) return;

        var orig = simInput.Array.LayoutTexture;
        try
        {
            var bmp = LoadTexture(fname);
            simInput.Array.LayoutTexture = bmp;
            simInput.Array.ReadStringsFromColors();
            // outputArrayLayoutImage = bmp;
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Error loading layout texture", ex.Message);
            simInput.Array.LayoutTexture = orig;
        }
        CalculateSimStepGui();
    }

    private async void OnOpenParameters(object? sender, RoutedEventArgs e)
    {
        var fname = await PickOpenFileAsync("Open Parameters", "json");
        if (fname == null) return;
        try
        {
            string dir = Path.GetDirectoryName(fname)!;
            JsonSpec spec = JsonSpecConverter.Read(fname);
            string meshFname = Path.Combine(dir, spec.Array.MeshFilename);
            Mesh mesh = LoadMesh(meshFname);
            var texture = LoadTexture(Path.Combine(dir, spec.Array.LayoutFilename));
            simInput = JsonSpecConverter.FromJson(spec, mesh, texture);

            SetModel(simInput.Array.Mesh);
            meshFilename = meshFname;
            Debug.WriteLine("Read spec " + spec);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Error loading model", ex.Message);
        }
        CalculateSimStepGui();
    }

    private async void OnSaveParameters(object? sender, RoutedEventArgs e)
    {
        var fname = await PickSaveFileAsync("Save Parameters", "parameters.json", "json");
        if (fname == null) return;

        var layoutFile = await PickSaveFileAsync("Save Layout Texture", "layout.png", "png");
        if (layoutFile == null) return;

        if (simInput.Array.LayoutTexture is Image<Rgba32> img)
        {
            await using var fs = File.Create(layoutFile);
            await img.SaveAsPngAsync(fs);
        }

        JsonSpec spec = JsonSpecConverter.ToJson(simInput, layoutFile, meshFilename ?? string.Empty, Path.GetDirectoryName(fname)!);
        JsonSpecConverter.Write(spec, fname);
    }

    private async Task RunSingleSimAsync()
    {
        try
        {
            InitSimulator();
            // Noon reference direction
            var simOutputNoon = simulator!.Simulate(
                simInput.Array, new Vector3(0.1f, 0.995f, 0.0f),
                simInput.Irradiance, simInput.IndirectIrradiance, simInput.Temperature);
            var simOutput = simulator.Simulate(simInput);
            double arrayAreaDistortion = Math.Abs(simOutputNoon.ArrayLitArea - simOutput.ArrayArea) / simOutput.ArrayArea;

            string bold = $"{simOutput.WattsOutput:0}W over {simOutput.ArrayArea:0.00}m² cell area";
            string line1 = $", {simOutputNoon.ArrayLitArea:0.00}m² lit cells{(arrayAreaDistortion > 0.01 ? " (MISMATCH)" : "")}, {simOutputNoon.ArrayLitArea - simOutput.ArrayLitArea:0.00}m² shaded";
            string line2 = $"(Power breakdown: {simOutput.WattsInsolation:0}W {simOutput.WattsInsolation / simOutputNoon.WattsInsolation * 100:0}% in, {simOutput.WattsOutputByCell:0}W {simOutput.WattsOutputByCell / simOutputNoon.WattsOutputByCell * 100:0}% ideal mppt, {simOutput.WattsOutput:0}W {simOutput.WattsOutput / simOutputNoon.WattsOutputByCell * 100:0}% output)";
            labelArrPower.Text = bold + "\n" + line1 + "\n" + line2;

            outputStringsListBox.ItemsSource = simOutput.Strings;

        }
        catch (Exception exc)
        {
            await ShowErrorAsync("Simulation error", exc.Message);
        }
    }

    private async void OnSaveLayoutTexture(object? sender, RoutedEventArgs e)
    {
        if (simInput.Array.LayoutTexture is not Image<Rgba32> img)
        {
            await ShowErrorAsync("Nothing to save", "Open and edit a layout first.");
            return;
        }

        var fname = await PickSaveFileAsync("Save Layout Texture", "layout.png", "png");
        if (fname == null) return;

        await using var fs = File.Create(fname);
        await img.SaveAsPngAsync(fs);  // async ImageSharp save
    }


    private void OutputStringsListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (outputStringsListBox.SelectedItem is not ArraySimStringOutput output) return;

        // Show details — in Avalonia you’d bind these to text blocks; here we just log
        Debug.WriteLine($"String {output.String}: {output.WattsOutput:0.0} W ({100 * output.WattsOutput / output.WattsOutputIdeal:0.0} %)");

        // Update the layout view to highlight the string (requires your control to support it)
        // For this skeleton, ensure your ArrayModelControl in Avalonia exposes a similar API.
    }

    private async Task SaveRenderAsync()
    {
        // Basic stub: ask GL control for a snapshot. Implement GLControl.GrabScreenshotAsync().
        var fname = await PickSaveFileAsync("Save Render", "render.png", "png");
        if (fname == null) return;
        var bmp = await glControl.GrabScreenshotAsync();
        await using var fs = File.Create(fname);
        bmp.Save(fs);
    }

    private async void OnRunTimeAverage(object? sender, RoutedEventArgs e) => await TimeAveragedSimAsync();

    private async Task TimeAveragedSimAsync()
    {
        // input time range; all other inputs come from simInput
        if (dtStartDate.SelectedDate is null || dtEndDate.SelectedDate is null ||
            dtStartTime.SelectedTime is null || dtEndTime.SelectedTime is null)
        {
            await ShowErrorAsync("Invalid time range", "Please select start/end date and time.");
            return;
        }

        var startLocal = dtStartDate.SelectedDate.Value + dtStartTime.SelectedTime.Value;
        var endLocal = dtEndDate.SelectedDate.Value + dtEndTime.SelectedTime.Value;
        if (endLocal <= startLocal) endLocal = startLocal + TimeSpan.FromHours(1);

        DateTimeOffset utcStart = startLocal.AddHours(-simInput.TimezoneOffsetHours);
        DateTimeOffset utcEnd   = endLocal.AddHours(-simInput.TimezoneOffsetHours);

        await using var csv = new StreamWriter("output.csv");
        await csv.WriteLineAsync("time_utc,insolation_w,output_w");

        var simAvg = new ArraySimulationStepOutput();

        InitSimulator();
        int nsim = 0;
        for (DateTimeOffset t = utcStart; t <= utcEnd; t = t.AddMinutes(10), nsim++)        {
            simInput.Utc = t;
            var simOutput = simulator!.Simulate(simInput);
            if (nsim > 0) Debug.Assert(simAvg.ArrayArea == simOutput.ArrayArea);

            simAvg.ArrayArea = simOutput.ArrayArea;
            simAvg.ArrayLitArea += simOutput.ArrayLitArea;
            simAvg.WattsInsolation += simOutput.WattsInsolation;
            simAvg.WattsOutputByCell += simOutput.WattsOutputByCell;
            simAvg.WattsOutput += simOutput.WattsOutput;

            await csv.WriteLineAsync($"{t:o},{simOutput.WattsInsolation},{simOutput.WattsOutput}");
        }

        simAvg.ArrayLitArea /= nsim;
        simAvg.WattsInsolation /= nsim;
        simAvg.WattsOutputByCell /= nsim;
        simAvg.WattsOutput /= nsim;

        Debug.WriteLine("Array time-averaged simulation output");
        Debug.WriteLine($"   ... {simAvg.ArrayArea} m^2 total cell area");
        Debug.WriteLine($"   ... {simAvg.ArrayLitArea} m^2 exposed to sunlight");
        Debug.WriteLine($"   ... {simAvg.WattsInsolation} W insolation");
        Debug.WriteLine($"   ... {simAvg.WattsOutputByCell} W output (assuming mppt per cell)");
        Debug.WriteLine($"   ... {simAvg.WattsOutput} W output");
    }
    #endregion
}
// ---------------------------------------------------------------------------------
// Minimal content dialog helper (since Avalonia has no MessageBox by default)
// ---------------------------------------------------------------------------------
public sealed class ContentDialog : Window
{
    public string Message { get; set; } = string.Empty;

    public ContentDialog()
    {
        Width = 420;
        Height = 160;
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Name = "Msg", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new Button
                {
                    Content = "OK",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    [!Button.CommandProperty] = new Avalonia.Data.Binding("$self.Close")
                }
            }
        };

        Opened += (_, __) =>
        {
            if (Content is StackPanel sp && sp.Children[0] is TextBlock tb)
                tb.Text = Message;
        };
    }

    public Task ShowAsync(Window owner)
    {
        var tcs = new TaskCompletionSource();
        Closed += (_, __) => tcs.SetResult();
        Show(owner);
        return tcs.Task;
    }
}

// ---------------------------------------------------------------------------------
// OpenGL drawing surface for Avalonia (very small stub).
// Implement your ShadowMeshSprite drawing in OnOpenGlRender via GL calls.
// ---------------------------------------------------------------------------------
public class GLControl : Control
{
    public ShadowMeshSprite? Sprite { get; set; }

    public async Task<RenderTargetBitmap> GrabScreenshotAsync()
    {
        // Snapshot the control. Replace with FBO readback if you need raw GL pixels.
        var pixelSize = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
        var rtb = new RenderTargetBitmap(pixelSize);
        await Dispatcher.UIThread.InvokeAsync(() => rtb.Render(this));
        return rtb;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var rect = new Rect(Bounds.Size);
        context.DrawRectangle(Brushes.Black, null, rect);

        var ft = new FormattedText(
            "OpenGL view (stub)",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            14,
            Brushes.White);

        context.DrawText(ft, new Avalonia.Point(10, 10));
    }
}
