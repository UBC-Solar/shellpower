using System;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SSCP.ShellPower {
    public partial class SimpleGraph : Control {
        // Bindable props
        public static readonly StyledProperty<double[]?> XProperty =
            AvaloniaProperty.Register<SimpleGraph, double[]?>(nameof(X));
        public static readonly StyledProperty<double[]?> YProperty =
            AvaloniaProperty.Register<SimpleGraph, double[]?>(nameof(Y));
        public static readonly StyledProperty<Thickness> PlotMarginsProperty =
            AvaloniaProperty.Register<SimpleGraph, Thickness>(nameof(PlotMargins), new Thickness(left:20, top:10, right:10, bottom:20));

        static SimpleGraph() {
            AffectsRender<SimpleGraph>(XProperty, YProperty, PlotMarginsProperty);
        }

        public double[]? X {
            get => GetValue(XProperty);
            set => SetValue(XProperty, value);
        }

        public double[]? Y {
            get => GetValue(YProperty);
            set => SetValue(YProperty, value);
        }

        // Matches original semantics: left=20, top=10, right=10, bottom=20
        public Thickness PlotMargins {
            get => GetValue(PlotMarginsProperty);
            set => SetValue(PlotMarginsProperty, value);
        }

        private static double[] CalcTicks(double min, double max) {
            Debug.Assert(max > min);
            double span = max - min;
            double tick = Math.Pow(10, (int)Math.Log10(span) - 1);
            while (span / tick > 10) tick *= 10;
            if (span / tick < 2) tick /= 5;
            if (span / tick < 5) tick /= 2;

            long i1 = (long)(min / tick) + 1;
            long i2 = (long)(max / tick);
            if (i2 < i1) return Array.Empty<double>();

            var ticks = new double[(int)((i2 - i1) + 1)];
            for (long i = i1; i <= i2; i++) ticks[i - i1] = i * tick;
            return ticks;
        }

        public override void Render(DrawingContext ctx) {
            base.Render(ctx);

            // Styles
            var bg = Brushes.Black;
            var gridPen = new Pen(Brushes.Gray, 1);
            var dataPen = new Pen(Brushes.Yellow, 1.5);
            var labelBrush = Brushes.LightGray;
            var typeface = new Typeface("Verdana");
            double fontSize = 10;

            // Background
            ctx.FillRectangle(bg, new Rect(Bounds.Size));

            // Data guard
            var x = X; var y = Y;
            if (x is null || y is null || x.Length == 0 || y.Length == 0 || x.Length != y.Length) return;

            // Data extents
            int n = x.Length;
            double xmin = x[0], xmax = x[0], ymin = y[0], ymax = y[0];
            for (int i = 1; i < n; i++) {
                if (x[i] < xmin) xmin = x[i];
                if (x[i] > xmax) xmax = x[i];
                if (y[i] < ymin) ymin = y[i];
                if (y[i] > ymax) ymax = y[i];
            }
            if (xmax <= xmin || ymax <= ymin) return;

            var m = PlotMargins;
            double w = Math.Max(0, Bounds.Width  - (m.Left + m.Right));
            double h = Math.Max(0, Bounds.Height - (m.Top  + m.Bottom));
            if (w <= 1 || h <= 1) return;

            // Ticks
            var xticks = CalcTicks(xmin, xmax);
            var yticks = CalcTicks(ymin, ymax);

            // Grid + labels
            foreach (var xv in xticks) {
                double xt = (xv - xmin) / (xmax - xmin) * w + m.Left;
                ctx.DrawLine(gridPen, new Point(xt, m.Top), new Point(xt, m.Top + h));
                var ft = new FormattedText(
                    xv.ToString("0.000", CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, fontSize, labelBrush);
                ctx.DrawText(ft, new Point(xt, m.Top + h)); // baseline label
            }

            foreach (var yv in yticks) {
                double yt = h - (yv - ymin) / (ymax - ymin) * h + m.Top;
                ctx.DrawLine(gridPen, new Point(m.Left, yt), new Point(m.Left + w, yt));
                var ft = new FormattedText(
                    yv.ToString("0.000", CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, fontSize, labelBrush);
                ctx.DrawText(ft, new Point(m.Left, yt - fontSize)); // nudge up so it doesn’t overlap the line
            }

            // Data polyline
            var geo = new StreamGeometry();
            using (var gctx = geo.Open()) {
                for (int i = 0; i < n; i++) {
                    double px = (x[i] - xmin) / (xmax - xmin) * w + m.Left;
                    double py = h - (y[i] - ymin) / (ymax - ymin) * h + m.Top;
                    if (i == 0) gctx.BeginFigure(new Point(px, py), isFilled: false);
                    else gctx.LineTo(new Point(px, py));
                }
            }
            ctx.DrawGeometry(null, dataPen, geo);
        }
    }
}
