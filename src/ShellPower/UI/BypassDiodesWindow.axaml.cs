using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SSCP.ShellPower {
    public partial class BypassDiodesWindow : Window {
        private double fwdDrop;
        private DiodeSpec? spec;

        public BypassDiodesWindow() {
            InitializeComponent();
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }

        public DiodeSpec? Spec {
            get => spec;
            set { spec = value; UpdateView(); }
        }

        private void UpdateView() {
            if (spec is null) return;
            textBoxFwdDrop.Text = spec.VoltageDrop.ToString(System.Globalization.CultureInfo.InvariantCulture);
            labelStatus.Text = string.Empty;
        }

        private bool ValidateEntries() {
            bool valid = true;
            valid &= ViewUtil.ValidateEntry(textBoxFwdDrop, out fwdDrop, 0.0, 10.0);
            labelStatus.Text = valid ? string.Empty : "Some entries look off. Please correct the highlighted fields.";
            return valid;
        }

        // Events
        private void TextBoxFwdDrop_TextChanged(object? sender, TextChangedEventArgs e) {
            ValidateEntries();
        }

        private void ButtonOK_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
            if (ValidateEntries() && spec is not null) {
                spec.VoltageDrop = fwdDrop;
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