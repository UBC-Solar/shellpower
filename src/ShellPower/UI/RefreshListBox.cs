using Avalonia.Controls;

namespace SSCP.ShellPower {
    public class RefreshListBox : ListBox {
        // In Avalonia, ListBox redraws automatically when Items changes.
        // But if you really need to force a redraw, you can call InvalidateVisual().

        public void RefreshItem(int index) {
            // No per-item refresh API — force whole control to redraw.
            InvalidateVisual();
        }

        public void RefreshItems() {
            // Same as above.
            InvalidateVisual();
        }
    }
}