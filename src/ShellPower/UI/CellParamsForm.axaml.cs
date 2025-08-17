using System.Globalization;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SSCP.ShellPower {
    public partial class CellParamsWindow : Window {
        private ArraySimulationStepInput input = null!;
        private double voc, isc, dvocdt, discdt, nideal, seriesr, area;
        private double tempC, wattsIn;

        public CellParamsWindow(ArraySimulationStepInput input) {
            this.input = input;
            InitializeComponent();

            // When the window opens, populate fields
            Opened += (_, __) => ResetTextBoxes();
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }

        private void ResetTextBoxes() {
            var cellSpec = input.Array.CellSpec;

            textBoxVoc.Text        = cellSpec.VocStc.ToString(CultureInfo.InvariantCulture);
            textBoxIsc.Text        = cellSpec.IscStc.ToString(CultureInfo.InvariantCulture);
            textBoxVocTemp.Text    = cellSpec.DVocDT.ToString(CultureInfo.InvariantCulture);
            textBoxIscTemp.Text    = cellSpec.DIscDT.ToString(CultureInfo.InvariantCulture);
            textBoxArea.Text       = cellSpec.Area.ToString(CultureInfo.InvariantCulture);
            textBoxNIdeal.Text     = cellSpec.NIdeal.ToString(CultureInfo.InvariantCulture);
            textBoxSeriesR.Text    = cellSpec.SeriesR.ToString(CultureInfo.InvariantCulture);

            textBoxTemp.Text       = input.Temperature.ToString(CultureInfo.InvariantCulture);
            textBoxInsolation.Text = input.Irradiance.ToString(CultureInfo.InvariantCulture);

            // Trigger an initial calc if everything is valid
            if (ValidateEntries()) {
                Recalculate();
                labelStatus.Text = string.Empty;
            } else {
                labelStatus.Text = "Edit fields (invalid entries highlighted).";
            }
        }

        private bool ValidateEntries() {
            bool valid = true;

            valid &= ViewUtil.ValidateEntry(textBoxVoc,        out voc,      double.Epsilon, 100);
            valid &= ViewUtil.ValidateEntry(textBoxIsc,        out isc,      double.Epsilon, 100);
            valid &= ViewUtil.ValidateEntry(textBoxVocTemp,    out dvocdt,   -10, 10);
            valid &= ViewUtil.ValidateEntry(textBoxIscTemp,    out discdt,   -10, 10);
            valid &= ViewUtil.ValidateEntry(textBoxArea,       out area,     0.0, 1.0);
            valid &= ViewUtil.ValidateEntry(textBoxNIdeal,     out nideal,   1.0, 10.0);
            valid &= ViewUtil.ValidateEntry(textBoxSeriesR,    out seriesr,  0.0, 0.1);

            valid &= ViewUtil.ValidateEntry(textBoxTemp,       out tempC,    -Constants.C_IN_KELVIN, 1000.0);
            valid &= ViewUtil.ValidateEntry(textBoxInsolation, out wattsIn,  0, 1600);

            labelStatus.Text = valid ? string.Empty : "Some entries look off. Please correct the highlighted fields.";
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
            // Recompute with a temp spec
            var spec = new CellSpec();
            UpdateSpec(spec);

            // Compute sweep + key points
            double i0   = spec.CalcI0(wattsIn, tempC);
            double iscV = spec.CalcIsc(wattsIn, tempC);
            double vocV = spec.CalcVoc(wattsIn, tempC);
            IVTrace sweep = CellSimulator.CalcSweep(spec, wattsIn, tempC);

            labelMaxPower.Text =
                $"Isc={iscV:0.000}A Voc={vocV:0.000}V @{tempC:0.00}°C\n" +
                $"Imp={sweep.Imp:0.000}A Vmp={sweep.Vmp:0.000}V Pmp={sweep.Pmp:0.000}W\n" +
                $"Rev. sat. current {i0:0.000}A, fill factor {sweep.FillFactor * 100.0:0.0}%";

            chartIV.X = sweep.V;
            chartIV.Y = sweep.I;
        }

        // Events
        private void TextBox_TextChanged(object? sender, TextChangedEventArgs e) {
            if (ValidateEntries()) {
                Recalculate();
            }
        }

        private void ButtonOK_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
            if (ValidateEntries()) {
                // Commit changes to the live spec + input
                UpdateSpec(input.Array.CellSpec);
                input.Temperature = tempC;
                input.Irradiance  = wattsIn;
                Close();
            } else {
                labelStatus.Text = "Some of those entries don't look right. Try again.";
            }
        }

        private void ButtonCancel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
            Close();
        }
    }
}
