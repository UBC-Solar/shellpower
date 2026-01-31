using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using System.Numerics;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace SSCP.ShellPower {
    public partial class MainWindow : Window {
        /* model */
        private ArraySimulationStepInput simInput = new ArraySimulationStepInput();
        private Shadow? shadow;
        private string? meshFilename;

        /* simulator */
        private ArraySimulator? simulator;

        // Sub-windows (created on demand)
        private ArrayLayoutForm? arrayLayoutWindow;
        private CellParamsWindow? cellParamsWindow;
        private ArrayDimensionsWindow? arrayDimsWindow;

        public MainWindow() {
            InitializeComponent();

            // init model
            simInput.Array = new ArraySpec();
            InitTimeAndPlace();
            InitializeArraySpec();
            InitializeConditions();
            CalculateSimStepGui();

            // init subviews
            InitInputView();
            InitOutputView();

            // initialize time-avg defaults
            var localNow = DateTime.Now;
            StartDate.SelectedDate = localNow.Date;
            StartTime.SelectedTime = new TimeSpan(localNow.Hour, 0, 0);
            EndDate.SelectedDate = localNow.Date;
            EndTime.SelectedTime = new TimeSpan(Math.Min(23, localNow.Hour + 1), 0, 0);

            StatusText.Text = "Ready";
        }

        private void InitInputView() {
            SimInputControl.SimInput = simInput;
            GLView.Array = simInput.Array;
        }

        private void InitOutputView() {
            OutputArrayLayout.Editable = false;
            OutputArrayLayout.Array = simInput.Array;
        }

        private void InitTimeAndPlace() {
            // Alice Springs, heading due south
            simInput.Longitude = 133.8;
            simInput.Latitude = -23.7;
            simInput.Heading = Math.PI;

            // Middle of WSC 2019 (Darwin is UTC+9:30)
            simInput.Utc = new DateTime(2019, 10, 16, 8, 0, 0).AddHours(-9.5);
            simInput.TimezoneOffsetHours = 9.5; // Darwin, NT time
        }

        /// <summary>
        /// Hack to make debugging faster.
        /// </summary>
        private void InitializeArraySpec() {
            var array = simInput.Array;
            array.LayoutBounds = new BoundsSpec() {
                MinX = -0.115,
                MaxX = 2.035,
                MinZ = -0.23,
                MaxZ = 4.59
            };
            array.LayoutTexture = ArrayModelControl.DEFAULT_TEX; // Avalonia Bitmap equivalent expected in your control
            //LoadModel(meshFilename);
            array.EncapsulationLoss = 0.025; // 2.5 %

            // Sunpower C60 Bin I
            CellSpec cellSpec = simInput.Array.CellSpec;
            cellSpec.IscStc = 6.27;
            cellSpec.VocStc = 0.686;
            cellSpec.DIscDT = -0.0020; // approx, computed
            cellSpec.DVocDT = -0.0018;
            cellSpec.Area = 0.015555; // m^2
            cellSpec.NIdeal = 1.26; // fudge
            cellSpec.SeriesR = 0.003; // ohms

            // Average bypass diode
            DiodeSpec diodeSpec = simInput.Array.BypassDiodeSpec;
            diodeSpec.VoltageDrop = 0.35;
        }

        private void InitializeConditions() {
            simInput.Temperature = 25; // STC, 25 C
            simInput.Irradiance = 1050; // not STC
            simInput.IndirectIrradiance = 70; // not STC
        }

        private void InitSimulator() {
            simulator ??= new ArraySimulator();
        }

        private async Task LoadModel(string filename) {
            var mesh = await Task.Run(() => LoadMesh(filename));
            SetModel(mesh);
        }

        private Mesh LoadMesh(string filename) {
            string extension = filename.Split('.').Last().ToLower();
            IMeshParser parser = extension switch {
                "3dxml" => new MeshParser3DXml(),
                "stl" => new MeshParserStl(),
                _ => throw new ArgumentException("Unsupported file type: " + extension)
            };
            parser.Parse(filename);
            Mesh mesh = parser.GetMesh();
            var size = mesh.BoundingBox.Max - mesh.BoundingBox.Min;
            if (size.Length() > 1000) {
                mesh = MeshUtils.Scale(mesh, 0.001f);
            }
            // StatusText.Text = $"Loaded model {Path.GetFileName(filename)}, {mesh.triangles.Length} triangles, {size.X:0.00}x{size.Y:0.00}x{size.Z:0.00}m";
            return mesh;
        }

        private Image<Rgba32> LoadTexture(string filename)
        {
            // ImageSharp auto-detects the format and decodes into RGBA8
            return SixLabors.ImageSharp.Image.Load<Rgba32>(filename);
        }

        /// <summary>
        /// Uses the given mesh for rendering and calculation. Computes shadow volumes for rendering.
        /// </summary>
        private void SetModel(Mesh mesh) {
            Logger.info("computing shadows...");
            var newShadow = new Shadow(mesh);
            newShadow.Initialize();
            shadow = newShadow;

            var shadowSprite = new ShadowMeshSprite(shadow);
            var center = (mesh.BoundingBox.Max + mesh.BoundingBox.Min) / 2;
            shadowSprite.Position = new OpenTK.Mathematics.Vector3(-center.X, -center.Y, -center.Z);
            GLView.Sprite = shadowSprite;
            simInput.Array.Mesh = mesh;
            Debug.WriteLine($"[SetModel] Sprite set. tris={mesh.triangles.Length}, bounds={mesh.BoundingBox.Min}..{mesh.BoundingBox.Max}");
            CalculateSimStepGui();
        }

        /// <summary>
        /// Responds to simulation input change. Fast, interactive update.
        /// </summary>
        private void CalculateSimStepGui() {
            if (shadow != null) {
                UpdateShadowView();
            }
        }

        /// <summary>
        /// Finds the position of the sun, or returns (0,0,0) if it's below the horizon.
        /// </summary>
        private Vector3 CalculateSunDir() {
            var lightDir = ArraySimulator.GetSunDir(simInput);
            if (lightDir.Y < 0) lightDir = Vector3.Zero;
            return lightDir;
        }

        /// <summary>
        /// Updates 3D rendering (view) from sim inputs (model).
        /// </summary>
        private void UpdateShadowView() {
            var lightDir = CalculateSunDir();
            if (shadow == null) return;
            shadow.Light = new Vector4(lightDir, 0);
            shadow.ComputeShadows();
            GLView.RequestNextFrameRendering();
        }

        // -------------------- UI Event Handlers --------------------

        private async void OpenModel_Click(object? sender, RoutedEventArgs e)
        {
            var files = await this.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    AllowMultiple = false,
                    Title = "Open 3D Model",
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("3D Models")
                        {
                            Patterns = new[] { "*.stl", "*.3dxml" }
                        },
                        FilePickerFileTypes.All
                    }
                });

            if (files is null || files.Count == 0) 
                return;

            var path = files[0].TryGetLocalPath();
            if (path is null)
            {
                await ShowErrorAsync("Unsupported file source", 
                    "This file cannot be accessed via a local path. " +
                    "Please copy it to a local drive first.");
                return;
            }

            try
            {
                await LoadModel(path);
                meshFilename = path;
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("Error loading model", $"{ex.Message}\n\n{ex.StackTrace}");
            }

            CalculateSimStepGui();
        }

        private async void OpenLayoutTexture_Click(object? sender, RoutedEventArgs e) {
            var dlg = new OpenFileDialog { AllowMultiple = false, Filters = {
                new FileDialogFilter { Name = "Images", Extensions = { "png","jpg","bmp","gif" } }
            }};
            var result = await dlg.ShowAsync(this);
            if (result == null || result.Length == 0) return;

            var array = simInput.Array;
            var origTexture = array.LayoutTexture;
            try {
                array.LayoutTexture = LoadTexture(result[0]);
                array.ReadStringsFromColors();
            } catch (Exception ex) {
                await ShowErrorAsync("Error loading layout texture", ex.Message);
                array.LayoutTexture = origTexture;
            }
            
            CalculateSimStepGui();
        }

        private async void SaveLayoutTexture_Click(object? sender, RoutedEventArgs e) {
            if (simInput.Array.LayoutTexture == null) {
                await ShowInfoAsync("Nothing to save. Try opening and editing a layout first.");
                return;
            }
            var dlg = new SaveFileDialog {
                Filters = {
                    new FileDialogFilter { Name = "PNG Images", Extensions = { "png" } },
                    new FileDialogFilter { Name = "GIF Images", Extensions = { "gif" } },
                    new FileDialogFilter { Name = "Bitmap images", Extensions = { "bmp" } },
                },
                InitialFileName = "layout.png"
            };
            var filename = await dlg.ShowAsync(this);
            if (string.IsNullOrWhiteSpace(filename)) return;

            ImageExtensions.Save(simInput.Array.LayoutTexture, filename);
        }

        private async void OpenParameters_Click(object? sender, RoutedEventArgs e) {
            var dlg = new OpenFileDialog { AllowMultiple = false, Filters = {
                new FileDialogFilter { Name = "JSON files", Extensions = { "json" } }
            }};
            var result = await dlg.ShowAsync(this);
            if (result == null || result.Length == 0) return;
            try {
                string filename = result[0];
                string dir = Path.GetDirectoryName(filename)!;
                JsonSpec spec = JsonSpecConverter.Read(filename);
                string meshFname = Path.Combine(dir, spec.Array.MeshFilename);
                var mesh = LoadMesh(meshFname);
                var texture = LoadTexture(Path.Combine(dir, spec.Array.LayoutFilename));
                simInput = JsonSpecConverter.FromJson(spec, mesh, texture);

                SetModel(simInput.Array.Mesh);
                meshFilename = meshFname;

                // refresh views
                InitInputView();
                InitOutputView();

                StatusText.Text = $"Read spec {filename}";
            } catch (Exception ex) {
                await ShowErrorAsync("Error loading model", ex.Message);
            }
            CalculateSimStepGui();
        }

        private async void SaveParameters_Click(object? sender, RoutedEventArgs e) {
            var dlg = new SaveFileDialog {
                Filters = { new FileDialogFilter { Name = "JSON files", Extensions = { "json" } } },
                InitialFileName = "params.json"
            };
            var filename = await dlg.ShowAsync(this);
            if (string.IsNullOrWhiteSpace(filename)) return;

            // prompt to save layout texture alongside
            var dlgLayout = new SaveFileDialog {
                Filters = {
                    new FileDialogFilter { Name = "PNG Images", Extensions = { "png" } },
                    new FileDialogFilter { Name = "GIF Images", Extensions = { "gif" } },
                    new FileDialogFilter { Name = "Bitmap images", Extensions = { "bmp" } },
                },
                InitialFileName = "layout.png"
            };
            var layoutFile = await dlgLayout.ShowAsync(this);
            if (string.IsNullOrWhiteSpace(layoutFile)) return;
            ImageExtensions.Save(simInput.Array.LayoutTexture, layoutFile);
            var spec = JsonSpecConverter.ToJson(simInput, Path.GetFileName(layoutFile), meshFilename ?? string.Empty, Path.GetDirectoryName(filename)!);
            JsonSpecConverter.Write(spec, filename);
        }

        // private async void Layout_Click(object? sender, RoutedEventArgs e) {
        //     arrayLayoutWindow ??= new ArrayLayoutForm(simInput.Array) { Title = "Layout" };
        //     await arrayLayoutWindow.ShowDialog(this);
        // }
        
        private async void Layout_Click(object? sender, RoutedEventArgs e)
        {
            var dlg = new ArrayLayoutForm(simInput.Array) { Title = "Layout" };

            // IMPORTANT: await ShowDialog to run it modally and avoid reentrancy
            await dlg.ShowDialog(this);
            // dlg is closed and disposable now; don't try to reuse it
        }

        private async void CellParameters_Click(object? sender, RoutedEventArgs e) {
            cellParamsWindow ??= new CellParamsWindow(simInput) { Title = "Cell Parameters" };
            await cellParamsWindow.ShowDialog(this);
        }

        private void LayoutTextureDimensions_Click(object? sender, RoutedEventArgs e) {
            if (arrayDimsWindow != null && arrayDimsWindow.IsVisible) {
                arrayDimsWindow.Activate();
            } else {
                arrayDimsWindow = new ArrayDimensionsWindow { Array = simInput.Array };
                arrayDimsWindow.Show(this);
            }
        }

        private void SimInputs_Change(object? sender, EventArgs e) {
            CalculateSimStepGui();
        }

        private async void Simulate_Click(object? sender, RoutedEventArgs e) {
            try
            {
                // Build/refresh any non-GL state here (loading meshes, textures into ImageSharp, etc.)
                // Do NOT touch simulator.EnsureGlResources() here; that runs inside the GL callbacks.

                // “Noon” pass with explicit sun dir:
                var noon = await SimSurface.RunOnceExplicitAsync(
                    simInput.Array!,
                    new System.Numerics.Vector3(0.1f, 0.995f, 0.0f),   // noon
                    simInput.Irradiance,
                    simInput.IndirectIrradiance,
                    simInput.Temperature);

                // Actual pass using ephemerides from simInput (GetSunDir inside ArraySimulator)

                Debug.WriteLine("Irradiance Is:");
                Debug.WriteLine(simInput.Irradiance);
                var simOutput = await SimSurface.RunOnceAsync(simInput);

                double distortion = Math.Abs(noon.ArrayLitArea - simOutput.ArrayArea) / simOutput.ArrayArea;

                // UI text
                string boldLine   = $"{simOutput.WattsOutput:0}W over {simOutput.ArrayArea:0.00}m² cell area";
                string firstLine  = $", {noon.ArrayLitArea:0.00}m² lit cells{(distortion > 0.01 ? " (MISMATCH)" : "")}, {noon.ArrayLitArea - simOutput.ArrayLitArea:0.00}m² shaded";
                string secondLine =
                    $"(Power breakdown: {simOutput.WattsInsolation:0}W in, " +
                    $"{simOutput.WattsOutputByCell:0}W cell-MPPT ({100*simOutput.WattsOutputByCell/simOutput.WattsInsolation:0}%), " +
                    $"{simOutput.WattsOutput:0}W delivered ({100*simOutput.WattsOutput/simOutput.WattsInsolation:0}%))";
                ArrayPowerText.Text = boldLine;
                ArrayPowerDetails.Text = secondLine;
                OutputStringsList.ItemsSource = simOutput.Strings; // your array is fine for IList
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("Simulation error", ex.Message);
            }
        }

        private async void SaveRender_Click(object? sender, RoutedEventArgs e) {
            var bmp = GLView.GrabScreenshot();
            var dlg = new SaveFileDialog {
                Filters = {
                    new FileDialogFilter { Name = "PNG Images", Extensions = { "png" } },
                    new FileDialogFilter { Name = "GIF Images", Extensions = { "gif" } },
                    new FileDialogFilter { Name = "Bitmap images", Extensions = { "bmp" } },
                },
                InitialFileName = "render.png"
            };
            var filename = await dlg.ShowAsync(this);
            if (string.IsNullOrWhiteSpace(filename)) return;
            using var fs = File.Open(filename, FileMode.Create, FileAccess.Write);
            bmp.Save(fs);
        }

        private void OutputStringsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (OutputStringsList.SelectedItem is not ArraySimStringOutput output) return;

            static double SafePct(double num, double den) => den > 0 ? 100.0 * num / den : 0.0;

            double pin = output.WattsIn;                       // (1)
            double pCell = output.WattsOutputByCell;           // (2)
            double pStringElec = output.WattsStringMppElectrical; // (3a)
            double eta = output.MpptEta;                       // converter η at (Vmp,Imp)
            double pOut = output.WattsOutput;                  // (3b) delivered after converter
            double pMaxIn = output.WattsInMaxDirect;           // (5) direct*area

            // Requested efficiencies
            double eff31 = SafePct(pOut, pin);     // (3)/(1)
            double eff21 = SafePct(pCell, pin);    // (2)/(1)
            double capture = SafePct(pin, pMaxIn); // (1)/(5)  (the meaningful direction)

            // Useful splits
            double mismatch = SafePct(pStringElec, pCell);     // string coupling loss
            double etaPct = 100.0 * eta;

            OutputStringName.Text = output.String.ToString();

            OutputStringInsolation.Text = $"{pin:0.0} W";
            OutputStringPower.Text      = $"{pOut:0.0} W ({eff31:0.0} % of in)";
            OutputStringMPPT.Text       = $"{pCell:0.0} W ({eff21:0.0} % of in)";
            OutputStringFlattened.Text  = $"{output.WattsOutputIdeal:0.0} W";

            OutputStringArea.Text   = $"{output.Area:0.000} m^2";
            OutputStringShaded.Text = $"{output.AreaShaded:0.000} m^2 ({SafePct(output.AreaShaded, output.Area):0.0} %)";

            // New diagnostics
            OutputStringStringMpp.Text   = $"{pStringElec:0.0} W";
            OutputStringMpptEta.Text     = $"{etaPct:0.0} %";
            OutputStringMismatch.Text    = $"{mismatch:0.0} %";
            OutputStringMaxInDirect.Text = $"{pMaxIn:0.0} W";
            OutputStringCapture.Text     = $"{capture:0.0} %";

            OutputArrayLayout.CellString = output.String;
        }
        
        private async void ShowIVTrace_Click(object? sender, RoutedEventArgs e) {
            if (OutputStringsList.SelectedItem is not ArraySimStringOutput output) {
                await ShowInfoAsync("No string selected.");
                return;
            }
            var win = new IVTraceWindow { Label = output.String.ToString(), IVTrace = output.IVTrace };
            await win.ShowDialog(this);
        }

        private async void BypassDiodeParameters_Click(object? sender, RoutedEventArgs e) {
            var win = new BypassDiodesWindow { Spec = simInput.Array.BypassDiodeSpec };
            await win.ShowDialog(this);
        }

        private async void RunTimeAveraged_Click(object? sender, RoutedEventArgs e) {
            var start = CombineLocalDateTime(StartDate.SelectedDate, StartTime.SelectedTime);
            var end = CombineLocalDateTime(EndDate.SelectedDate, EndTime.SelectedTime);
            if (start == null || end == null) { await ShowInfoAsync("Please choose start and end times."); return; }
            if (end <= start) { await ShowInfoAsync("End must be after start."); return; }

            // convert to UTC using simInput.TimezoneOffsetHours
            DateTime utcStart = start.Value.AddHours(-simInput.TimezoneOffsetHours);
            DateTime utcEnd = end.Value.AddHours(-simInput.TimezoneOffsetHours);

            using var csv = new StreamWriter(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "output.csv"));
            csv.WriteLine("time_utc,insolation_w,output_w");

            var simAvg = new ArraySimulationStepOutput();

            InitSimulator();
            int nsim = 0;
            for (DateTime t = utcStart; t <= utcEnd; t = t.AddMinutes(10), nsim++) {
                simInput.Utc = t;
                SimInputControl.UpdateView();
                var simOutput = simulator!.Simulate(simInput);

                if (nsim > 0) Debug.Assert(simAvg.ArrayArea == simOutput.ArrayArea);
                simAvg.ArrayArea = simOutput.ArrayArea;
                simAvg.ArrayLitArea += simOutput.ArrayLitArea;
                simAvg.WattsInsolation += simOutput.WattsInsolation;
                simAvg.WattsOutputByCell += simOutput.WattsOutputByCell;
                simAvg.WattsOutput += simOutput.WattsOutput;

                csv.WriteLine($"{t:o},{simOutput.WattsInsolation},{simOutput.WattsOutput}");
            }

            // averages
            simAvg.ArrayLitArea /= nsim;
            simAvg.WattsInsolation /= nsim;
            simAvg.WattsOutputByCell /= nsim;
            simAvg.WattsOutput /= nsim;

            LabelSimAvgPower.Text = $"{simAvg.WattsOutput:0.0} W";
            // If you have an efficiency metric, compute here; placeholder:
            LabelSimAvgEfficiency.Text = simAvg.WattsInsolation > 0 ? $"{100.0 * simAvg.WattsOutput / simAvg.WattsInsolation:0.0}%" : "-";
            LabelSimTotalEnergy.Text = $"{simAvg.WattsOutput * (10.0/60.0) * nsim:0.0} Wh"; // rough: avg power * hours
        }

        private static DateTime? CombineLocalDateTime(DateTimeOffset? date, TimeSpan? time) {
            if (date == null || time == null) return null;
            var local = date.Value.Date + time.Value;
            return local;
        }

        // -------------------- Minimal modal helpers (no extra deps) --------------------
        private async Task ShowErrorAsync(string title, string message)
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var dlg = new Window
                {
                    Title = title,
                    Width = 420,
                    Height = 180,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = new StackPanel
                    {
                        Margin = new Thickness(12),
                        Children =
                        {
                            new TextBlock
                            {
                                Text = message,
                                TextWrapping = TextWrapping.Wrap,
                                Margin = new Thickness(0,0,0,12)
                            },
                            new Button
                            {
                                Content = "OK",
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                                Width = 80,
                                IsDefault = true
                            }
                        }
                    }
                };

                var ok = ((StackPanel)dlg.Content!).Children.OfType<Button>().First();
                ok.Click += (_, __) => dlg.Close();

                await dlg.ShowDialog(this);
            });
        }        
        private Task ShowInfoAsync(string message) => ShowErrorAsync("Info", message);
    }
}
