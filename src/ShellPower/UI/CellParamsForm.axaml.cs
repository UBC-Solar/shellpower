using System.Globalization;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SSCP.ShellPower {
    public partial class CellParamsWindow : Window {
        private readonly ArraySimulationStepInput input;
        
        // parsed values
        private double voc, isc, dvocdt, discdt, nideal, seriesr, area;
        private double tempC, wattsIn;

        public CellParamsWindow(ArraySimulationStepInput input) {
            this.input = input;
            InitializeComponent();
            // Populate once controls exist
            Opened += (_, __) => ResetTextBoxes();
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);

            // Resolve controls by x:Name (must match AXAML)
            textBoxVoc        = this.FindControl<TextBox>("textBoxVoc");
            textBoxIsc        = this.FindControl<TextBox>("textBoxIsc");
            textBoxVocTemp    = this.FindControl<TextBox>("textBoxVocTemp");
            textBoxIscTemp    = this.FindControl<TextBox>("textBoxIscTemp");
            textBoxArea       = this.FindControl<TextBox>("textBoxArea");
            textBoxNIdeal     = this.FindControl<TextBox>("textBoxNIdeal");
            textBoxSeriesR    = this.FindControl<TextBox>("textBoxSeriesR");
            textBoxTemp       = this.FindControl<TextBox>("textBoxTemp");
            textBoxInsolation = this.FindControl<TextBox>("textBoxInsolation");

            labelStatus       = this.FindControl<TextBlock>("labelStatus");
            labelMaxPower     = this.FindControl<TextBlock>("labelMaxPower");
            chartIV           = this.FindControl<SimpleGraph>("chartIV");
        }

        private bool ControlsReady =>
            textBoxVoc is not null &&
            textBoxIsc is not null &&
            textBoxVocTemp is not null &&
            textBoxIscTemp is not null &&
            textBoxArea is not null &&
            textBoxNIdeal is not null &&
            textBoxSeriesR is not null &&
            textBoxTemp is not null &&
            textBoxInsolation is not null &&
            labelStatus is not null &&
            labelMaxPower is not null &&
            chartIV is not null;

        private void ResetTextBoxes() {
            if (!ControlsReady) return;

            var inv = CultureInfo.InvariantCulture;
            var cellSpec = input.Array.CellSpec;

            textBoxVoc!.Text        = cellSpec.VocStc.ToString(inv);
            textBoxIsc!.Text        = cellSpec.IscStc.ToString(inv);
            textBoxVocTemp!.Text    = cellSpec.DVocDT.ToString(inv);
            textBoxIscTemp!.Text    = cellSpec.DIscDT.ToString(inv);
            textBoxArea!.Text       = cellSpec.Area.ToString(inv);
            textBoxNIdeal!.Text     = cellSpec.NIdeal.ToString(inv);
            textBoxSeriesR!.Text    = cellSpec.SeriesR.ToString(inv);

            textBoxTemp!.Text       = input.Temperature.ToString(inv);
            textBoxInsolation!.Text = input.Irradiance.ToString(inv);

            if (ValidateEntries()) {
                Recalculate();
                labelStatus!.Text = string.Empty;
            } else {
                labelStatus!.Text = "Edit fields (invalid entries highlighted).";
            }
        }

        private bool ValidateEntries() {
            if (!ControlsReady) return false;

            bool valid = true;

            valid &= ViewUtil.ValidateEntry(textBoxVoc!,        out voc,     double.Epsilon, 100);
            valid &= ViewUtil.ValidateEntry(textBoxIsc!,        out isc,     double.Epsilon, 100);
            valid &= ViewUtil.ValidateEntry(textBoxVocTemp!,    out dvocdt,  -10, 10);
            valid &= ViewUtil.ValidateEntry(textBoxIscTemp!,    out discdt,  -10, 10);
            valid &= ViewUtil.ValidateEntry(textBoxArea!,       out area,     0.0, 1.0);
            valid &= ViewUtil.ValidateEntry(textBoxNIdeal!,     out nideal,   1.0, 10.0);
            valid &= ViewUtil.ValidateEntry(textBoxSeriesR!,    out seriesr,  0.0, 0.1);
            valid &= ViewUtil.ValidateEntry(textBoxTemp!,       out tempC,   -Constants.C_IN_KELVIN, 1000.0);
            valid &= ViewUtil.ValidateEntry(textBoxInsolation!, out wattsIn,  0, 1600);

            labelStatus!.Text = valid ? string.Empty : "Some entries look off. Please correct the highlighted fields.";
            return valid;
        }

        private void UpdateSpec(CellSpec spec) {
            spec.VocStc  = voc;
            spec.IscStc  = isc;
            spec.DVocDT  = dvocdt;
            spec.DIscDT  = discdt;
            spec.Area    = area;
            spec.NIdeal  = nideal;
            spec.SeriesR = seriesr;
        }

        private void Recalculate() {
            if (!ControlsReady) return;

            var spec = new CellSpec();
            UpdateSpec(spec);

            double i0   = spec.CalcI0(wattsIn, tempC);
            double iscV = spec.CalcIsc(wattsIn, tempC);
            double vocV = spec.CalcVoc(wattsIn, tempC);
            IVTrace sweep = CellSimulator.CalcSweep(spec, wattsIn, tempC);

            labelMaxPower!.Text =
                $"Isc={iscV:0.000}A Voc={vocV:0.000}V @{tempC:0.00}°C\n" +
                $"Imp={sweep.Imp:0.000}A Vmp={sweep.Vmp:0.000}V Pmp={sweep.Pmp:0.000}W\n" +
                $"Rev. sat. current {i0:0.000}A, fill factor {sweep.FillFactor * 100.0:0.0}%";

            chartIV!.X = sweep.V;
            chartIV!.Y = sweep.I;
        }

        // Events (wired in XAML)
        private void TextBox_TextChanged(object? sender, TextChangedEventArgs e) {
            if (ValidateEntries()) Recalculate();
        }

        private void ButtonOK_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
            if (!ValidateEntries()) {
                labelStatus!.Text = "Some of those entries don't look right. Try again.";
                return;
            }
            UpdateSpec(input.Array.CellSpec);
            input.Temperature = tempC;
            input.Irradiance  = wattsIn;
            Close();
        }

        private void ButtonCancel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
            Close();
        }
    }
}