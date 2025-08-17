using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;

namespace SSCP.ShellPower {
    public static class ViewUtil {
        public static bool ValidateEntry(TextBox textBox, out double val, double min, double max) {
            // Parse with invariant culture; adjust if you prefer current culture
            if (!double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out val)
                || val < min || val > max) {
                // light reddish background (ARGB: FF FF BB AA)
                textBox.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xBB, 0xAA));
                return false;
            } else {
                textBox.Background = Brushes.White;
                return true;
            }
        }
    }
}