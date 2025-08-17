using System.Globalization;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SSCP.ShellPower {
    public partial class IVTraceWindow : Window {
        private IVTrace? _trace;

        public IVTraceWindow() {
            InitializeComponent();
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }

        // Preserve your API
        public string Label {
            get => labelName.Text ?? string.Empty;
            set => labelName.Text = value;
        }

        public IVTrace? IVTrace {
            get => _trace;
            set { _trace = value; UpdateView(); }
        }

        private void UpdateView() {
            if (_trace is null) {
                labelMaxPower.Text = string.Empty;
                labelFillFactor.Text = string.Empty;
                simpleGraphIV.X = null;
                simpleGraphIV.Y = null;
                return;
            }

            var t = _trace;

            labelMaxPower.Text =
                $"Maximum power: {t.Imp.ToString("0.000", CultureInfo.InvariantCulture)}A * " +
                $"{t.Vmp.ToString("0.000", CultureInfo.InvariantCulture)}V = " +
                $"{t.Pmp.ToString("0.000", CultureInfo.InvariantCulture)}W";

            labelFillFactor.Text =
                $"Isc={t.Isc.ToString("0.000", CultureInfo.InvariantCulture)}A, " +
                $"Voc={t.Voc.ToString("0.000", CultureInfo.InvariantCulture)}V, " +
                $"Fill factor={(t.FillFactor * 100).ToString("0.0", CultureInfo.InvariantCulture)}%";

            // Plot I-V curve: X=V, Y=I
            simpleGraphIV.X = t.V;
            simpleGraphIV.Y = t.I;
        }
    }

    // Assuming you already have this model; shown here for reference only.
    // public class IVTrace {
    //     public double[] V { get; set; } = Array.Empty<double>();
    //     public double[] I { get; set; } = Array.Empty<double>();
    //     public double Imp { get; set; }
    //     public double Vmp { get; set; }
    //     public double Pmp { get; set; }
    //     public double Isc { get; set; }
    //     public double Voc { get; set; }
    //     public double FillFactor { get; set; } // 0..1
    // }
}
