using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SSCP.ShellPower {
    public partial class BypassDiodesWindow : Window {
        private double fwdDrop;

        // Backing field for the model
        private DiodeSpec? spec;
        public DiodeSpec? Spec {
            get => spec;
            set { spec = value; UpdateView(); }
        }
        
        public BypassDiodesWindow() {
            InitializeComponent();
            // If the caller sets Spec after construction, UpdateView() will run then.
            // If Spec was already set, ensure UI reflects it on open as well:
            this.Opened += (_, __) => UpdateView();
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);

            // Resolve x:Name'd controls
            textBoxFwdDrop = this.FindControl<TextBox>("textBoxFwdDrop");
            labelStatus    = this.FindControl<TextBlock>("labelStatus");
            buttonOK       = this.FindControl<Button>("buttonOK");
            buttonCancel   = this.FindControl<Button>("buttonCancel");
        }

        private bool ControlsReady =>
            textBoxFwdDrop is not null &&
            labelStatus    is not null &&
            buttonOK       is not null &&
            buttonCancel   is not null;

        private void UpdateView() {
            if (!ControlsReady) return;
            if (spec is null) {
                // No model yet — clear UI / disable OK if you like
                textBoxFwdDrop!.Text = string.Empty;
                labelStatus!.Text = "No diode spec provided.";
                return;
            }

            textBoxFwdDrop!.Text = spec.VoltageDrop.ToString(CultureInfo.InvariantCulture);
            labelStatus!.Text = string.Empty;
        }

        private bool ValidateEntries() {
            if (!ControlsReady) return false;

            bool valid = true;
            valid &= ViewUtil.ValidateEntry(textBoxFwdDrop!, out fwdDrop, 0.0, 10.0);
            labelStatus!.Text = valid ? string.Empty :
                "Some entries look off. Please correct the highlighted fields.";
            return valid;
        }

        // Events (wired in XAML)
        private void TextBoxFwdDrop_TextChanged(object? sender, TextChangedEventArgs e) {
            ValidateEntries();
        }

        private void ButtonOK_Click(object? sender, RoutedEventArgs e) {
            if (!ValidateEntries()) {
                labelStatus!.Text = "Some of those entries don't look right. Try again.";
                return;
            }

            if (spec is null) {
                // If caller didn’t provide one, create it so they can read Spec back after Close()
                spec = new DiodeSpec();
            }

            // IMPORTANT: if DiodeSpec is a struct, mutating a copy won’t persist.
            // Make a local, set the field, then assign back to Spec.
            var s = spec;
            s!.VoltageDrop = fwdDrop;
            Spec = s;

            Close(true);
        }

        private void ButtonCancel_Click(object? sender, RoutedEventArgs e) {
            Close(false);
        }
    }
}