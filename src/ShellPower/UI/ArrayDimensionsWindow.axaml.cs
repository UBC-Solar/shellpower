using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;

namespace SSCP.ShellPower
{
    public partial class ArrayDimensionsWindow : Window
    {
        private BoundsSpec originalLayoutBounds;
        private bool updatingView = false;
        private ArraySpec? array;

        // Add these fields:
        // private TextBox? numX0, numX1, numZ0, numZ1;
        // private Button? buttonOK, buttonCancel;
        // private TextBlock? labelStatus;

        public ArraySpec? Array
        {
            get => array;
            set
            {
                array = value;
                if (array is not null) originalLayoutBounds = array.LayoutBounds;
                UpdateView();
            }
        }

        public ArrayDimensionsWindow()
        {
            InitializeComponent();
            // Now that controls exist, it's safe to touch them:
            UpdateView();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);

            // Resolve named controls from XAML:
            numX0 = this.FindControl<TextBox>("numX0");
            numX1 = this.FindControl<TextBox>("numX1");
            numZ0 = this.FindControl<TextBox>("numZ0");
            numZ1 = this.FindControl<TextBox>("numZ1");
            buttonOK = this.FindControl<Button>("buttonOK");
            buttonCancel = this.FindControl<Button>("buttonCancel");
            labelStatus = this.FindControl<TextBlock>("labelStatus");

            // (Optional) wire events here if you prefer code-behind wiring:
            // if (numX0 != null) numX0.GetObservable(TextBox.TextProperty).Subscribe(_ => Num_ValueChanged(null!, null!));
            // if (numX1 != null) numX1.GetObservable(TextBox.TextProperty).Subscribe(_ => Num_ValueChanged(null!, null!));
            // if (numZ0 != null) numZ0.GetObservable(TextBox.TextProperty).Subscribe(_ => Num_ValueChanged(null!, null!));
            // if (numZ1 != null) numZ1.GetObservable(TextBox.TextProperty).Subscribe(_ => Num_ValueChanged(null!, null!));
            if (buttonOK != null) buttonOK.Click += ButtonOK_Click;
            if (buttonCancel != null) buttonCancel.Click += ButtonCancel_Click;
        }

        private void SetEnabled(bool enabled)
        {
            // Guard for early lifecycle (if someone calls UpdateView before InitializeComponent)
            if (numX0 is null) return;

            numX0.IsEnabled = enabled;
            numX1!.IsEnabled = enabled;
            numZ0!.IsEnabled = enabled;
            numZ1!.IsEnabled = enabled;
            if (buttonOK != null) buttonOK.IsEnabled = enabled;
        }

        private void UpdateView()
        {
            if (Array is null)
            {
                SetEnabled(false);
                return;
            }

            // From here, controls must be resolved:
            if (numX0 is null) return;

            updatingView = true;
            try
            {
                SetEnabled(true);
                numX0.Text = Array.LayoutBounds.MinX.ToString(System.Globalization.CultureInfo.InvariantCulture);
                numX1!.Text = Array.LayoutBounds.MaxX.ToString(System.Globalization.CultureInfo.InvariantCulture);
                numZ0!.Text = Array.LayoutBounds.MinZ.ToString(System.Globalization.CultureInfo.InvariantCulture);
                numZ1!.Text = Array.LayoutBounds.MaxZ.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (labelStatus != null) labelStatus.Text = string.Empty;
            }
            finally
            {
                updatingView = false;
            }
        }

        private bool ValidateAll(out double minX, out double maxX, out double minZ, out double maxZ)
        {
            // Assume ViewUtil.ValidateEntry sets textbox background etc.
            bool ok = true;
            ok &= ViewUtil.ValidateEntry(numX0!, out minX, double.NegativeInfinity, double.PositiveInfinity);
            ok &= ViewUtil.ValidateEntry(numX1!, out maxX, double.NegativeInfinity, double.PositiveInfinity);
            ok &= ViewUtil.ValidateEntry(numZ0!, out minZ, double.NegativeInfinity, double.PositiveInfinity);
            ok &= ViewUtil.ValidateEntry(numZ1!, out maxZ, double.NegativeInfinity, double.PositiveInfinity);

            if (!ok) { if (labelStatus != null) labelStatus.Text = "Please correct highlighted values."; return false; }

            if (maxX < minX) { if (labelStatus != null) labelStatus.Text = "Max X must be ≥ Min X."; ok = false; }
            else if (maxZ < minZ) { if (labelStatus != null) labelStatus.Text = "Max Z must be ≥ Min Z."; ok = false; }
            else if (labelStatus != null) labelStatus.Text = string.Empty;

            return ok;
        }

        private void UpdateModel()
        {
            if (Array is null || updatingView) return;

            if (ValidateAll(out var minX, out var maxX, out var minZ, out var maxZ))
            {
                // WARNING: if LayoutBounds is a struct returned by value, mutating fields won’t persist.
                // Use a local copy, then assign back:
                var b = Array.LayoutBounds;
                b.MinX = minX;
                b.MaxX = maxX;
                b.MinZ = minZ;
                b.MaxZ = maxZ;
                Array.LayoutBounds = b;
            }
        }

        // Events
        private void Num_ValueChanged(object? sender, TextChangedEventArgs? e)
        {
            if (updatingView) return;
            UpdateModel();
        }

        private void ButtonOK_Click(object? sender, RoutedEventArgs e)
        {
            if (Array is null) { Close(); return; }
            if (ValidateAll(out var minX, out var maxX, out var minZ, out var maxZ))
            {
                var b = Array.LayoutBounds;
                b.MinX = minX; b.MaxX = maxX; b.MinZ = minZ; b.MaxZ = maxZ;
                Array.LayoutBounds = b;
                Close();
            }
        }

        private void ButtonCancel_Click(object? sender, RoutedEventArgs e)
        {
            if (Array is not null)
            {
                Array.LayoutBounds = originalLayoutBounds; // restores snapshot
            }
            Close();
        }
    }
}