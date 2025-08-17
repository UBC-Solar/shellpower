using System.Globalization;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SSCP.ShellPower {
    public partial class ArrayDimensionsWindow : Window {
        private BoundsSpec originalLayoutBounds;
        private bool updatingView = false;
        private ArraySpec? array;

        public ArraySpec? Array {
            get => array;
            set {
                array = value;
                if (array is not null) originalLayoutBounds = array.LayoutBounds;
                UpdateView();
            }
        }

        public ArrayDimensionsWindow() {
            InitializeComponent();
            UpdateView();
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }

        private void SetEnabled(bool enabled) {
            numX0.IsEnabled = numX1.IsEnabled = numZ0.IsEnabled = numZ1.IsEnabled = buttonOK.IsEnabled = enabled;
        }

        private void UpdateView() {
            if (Array is null) {
                SetEnabled(false);
                return;
            }
            updatingView = true;
            try {
                SetEnabled(true);
                numX0.Text = Array.LayoutBounds.MinX.ToString(CultureInfo.InvariantCulture);
                numX1.Text = Array.LayoutBounds.MaxX.ToString(CultureInfo.InvariantCulture);
                numZ0.Text = Array.LayoutBounds.MinZ.ToString(CultureInfo.InvariantCulture);
                numZ1.Text = Array.LayoutBounds.MaxZ.ToString(CultureInfo.InvariantCulture);
                labelStatus.Text = string.Empty;
            } finally {
                updatingView = false;
            }
        }

        private bool TryParseBox(TextBox tb, out double v)
            => double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        private bool ValidateAll(out double minX, out double maxX, out double minZ, out double maxZ) {
            bool ok = true;
            ok &= ViewUtil.ValidateEntry(numX0, out minX, double.NegativeInfinity, double.PositiveInfinity);
            ok &= ViewUtil.ValidateEntry(numX1, out maxX, double.NegativeInfinity, double.PositiveInfinity);
            ok &= ViewUtil.ValidateEntry(numZ0, out minZ, double.NegativeInfinity, double.PositiveInfinity);
            ok &= ViewUtil.ValidateEntry(numZ1, out maxZ, double.NegativeInfinity, double.PositiveInfinity);

            if (!ok) { labelStatus.Text = "Please correct highlighted values."; return false; }

            if (maxX < minX) { labelStatus.Text = "Max X must be ≥ Min X."; ok = false; }
            else if (maxZ < minZ) { labelStatus.Text = "Max Z must be ≥ Min Z."; ok = false; }
            else labelStatus.Text = string.Empty;

            return ok;
        }

        private void UpdateModel() {
            if (Array is null || updatingView) return;

            if (ValidateAll(out var minX, out var maxX, out var minZ, out var maxZ)) {
                Array.LayoutBounds.MinX = minX;
                Array.LayoutBounds.MaxX = maxX;
                Array.LayoutBounds.MinZ = minZ;
                Array.LayoutBounds.MaxZ = maxZ;
            }
        }

        // Events
        private void Num_ValueChanged(object? sender, TextChangedEventArgs e) {
            if (updatingView) return;
            UpdateModel();
        }

        private void ButtonOK_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
            if (Array is null) { Close(); return; }
            if (ValidateAll(out var minX, out var maxX, out var minZ, out var maxZ)) {
                Array.LayoutBounds.MinX = minX;
                Array.LayoutBounds.MaxX = maxX;
                Array.LayoutBounds.MinZ = minZ;
                Array.LayoutBounds.MaxZ = maxZ;
                Close();
            }
        }

        private void ButtonCancel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
            if (Array is not null) {
                Array.LayoutBounds = originalLayoutBounds;
            }
            Close();
        }
    }
}
