using System.Globalization;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SSCP.ShellPower
{
    public partial class IVTraceWindow : Window
    {
        // Cache the controls we reference
        private TextBlock? _labelName;
        private TextBlock? _labelMaxPower;
        private TextBlock? _labelFillFactor;
        private SimpleGraph? _simpleGraphIV;

        private IVTrace? _trace;

        public IVTraceWindow()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);

            // Find controls by x:Name from the XAML
            _labelName       = this.FindControl<TextBlock>("labelName");
            _labelMaxPower   = this.FindControl<TextBlock>("labelMaxPower");
            _labelFillFactor = this.FindControl<TextBlock>("labelFillFactor");
            _simpleGraphIV   = this.FindControl<SimpleGraph>("simpleGraphIV");
        }

        // Preserve your API
        public string Label
        {
            get => _labelName?.Text ?? string.Empty;
            set { if (_labelName != null) _labelName.Text = value; }
        }

        public IVTrace? IVTrace
        {
            get => _trace;
            set { _trace = value; UpdateView(); }
        }

        private void UpdateView()
        {
            if (_labelMaxPower == null || _labelFillFactor == null || _simpleGraphIV == null)
                return; // window not initialized yet

            if (_trace is null)
            {
                _labelMaxPower.Text = string.Empty;
                _labelFillFactor.Text = string.Empty;
                _simpleGraphIV.X = null;
                _simpleGraphIV.Y = null;
                return;
            }

            var t = _trace;

            _labelMaxPower.Text =
                $"Maximum power: {t.Imp.ToString("0.000", CultureInfo.InvariantCulture)}A * " +
                $"{t.Vmp.ToString("0.000", CultureInfo.InvariantCulture)}V = " +
                $"{t.Pmp.ToString("0.000", CultureInfo.InvariantCulture)}W";

            _labelFillFactor.Text =
                $"Isc={t.Isc.ToString("0.000", CultureInfo.InvariantCulture)}A, " +
                $"Voc={t.Voc.ToString("0.000", CultureInfo.InvariantCulture)}V, " +
                $"Fill factor={(t.FillFactor * 100).ToString("0.0", CultureInfo.InvariantCulture)}%";

            // Plot I-V curve: X=V, Y=I
            _simpleGraphIV.X = t.V;
            _simpleGraphIV.Y = t.I;
        }
    }
}