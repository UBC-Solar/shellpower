using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using Color = System.Drawing.Color;
using Point = Avalonia.Point;
using System.Runtime.InteropServices;

namespace SSCP.ShellPower;

/// <summary>
/// Avalonia control that replaces the old WinForms + System.Drawing ArrayLayoutControl.
/// Draws the array layout image, lets the user pick cells and bypass diodes, and animates selection.
/// Assumptions:
/// - ArraySpec.LayoutTexture is an ImageSharp <see cref="Image{Rgba32}"/>.
/// - ArraySpec, ArraySpec.CellString, ArraySpec.Cell, ArraySpec.BypassDiode, Pair<T> exist as in the legacy code.
/// </summary>
public partial class ArrayLayoutControl : Control
{
    private const double JUNCTION_RADIUS = 4.0; // px in control space (after scaling)
    private const double JUNCTION_RADIUS_CLICK = 15.0; // px in control space

    // array model, currently selected string
    private ArraySpec? _array;
    private ArraySpec.CellString? _cellStr;

    // source image (ImageSharp) and cached Avalonia bitmap for drawing
    private Image<Rgba32>? _layoutImageSharp;        // provided by model
    private WriteableBitmap? _layoutBitmapCached;    // converted for fast drawing

    // cached texels (ARGB in uint) from ImageSharp for picking/flood fill
    private uint[,]? _pixels;

    private Point[] _cellPoints = System.Array.Empty<Point>();
    private Point[] _junctionPoints = System.Array.Empty<Point>();

    // selection rendering
    private WriteableBitmap? _texSelected; // overlay (animated hatch)
    private readonly List<int> _bypassJunctions = new();

    // mouseover hints
    private int _mouseoverJunction = -1;
    private ArraySpec.Cell? _mouseoverCell;

    // animation
    private readonly DispatcherTimer _timer;

    public ArrayLayoutControl()
    {
        Editable = true;
        EditBypassDiodes = false;
        AnimatedSelection = false;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) => { if (AnimatedSelection) InvalidateVisual(); };
        _timer.Start();

        // react to size changes so we can rescale cached textures
        this.GetObservable(BoundsProperty).Subscribe(_ => EnsureSelectionOverlay());
    }

    #region Public API

    public ArraySpec? Array
    {
        get => _array;
        set
        {
            _array = value;
            if (_array != null && !_array.Strings.Contains(_cellStr))
                _cellStr = null;

            _layoutImageSharp = _array?.LayoutTexture; // ImageSharp image
            _layoutBitmapCached = null;                 // will rebuild lazily
            _pixels = null;                             // force re-read on next render
            InvalidateVisual();
        }
    }

    public ArraySpec.CellString? CellString
    {
        get => _cellStr;
        set { _cellStr = value; InvalidateVisual(); }
    }

    public bool Editable { get; set; }
    public bool EditBypassDiodes { get; set; }
    public bool AnimatedSelection { get; set; }

    public event EventHandler? CellStringChanged;

    #endregion

    #region Rendering

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        if (_array == null || _layoutImageSharp == null)
            return;

        // refresh cached bitmap + pixels if source changed
        RecomputeArrayViewModel();

        // draw scaled layout texture top-left aligned (like legacy)
        var scaled = GetScaledArrayRect();
        if (_layoutBitmapCached != null)
            context.DrawImage(_layoutBitmapCached, new Rect(_layoutBitmapCached.Size), scaled);

        // selected cells overlay (animated)
        if (CellString != null)
        {
            RecomputeTexSelected();
            if (_texSelected != null)
                context.DrawImage(_texSelected, new Rect(_texSelected.Size), scaled);
        }

        // draw wiring between cell centers
        if (_cellPoints.Length > 1)
        {
            var blackPen = new Pen(new SolidColorBrush(Avalonia.Media.Color.FromArgb(80, 0, 0, 0)), 3);
            var lightPen = new Pen(Brushes.LightYellow, 1);
            DrawPolyline(context, _cellPoints, blackPen);
            DrawPolyline(context, _cellPoints, lightPen);
        }

        // draw bypass diode arcs
        if (CellString != null)
        {
            foreach (var diode in CellString.BypassDiodes)
            {
                var pA = _junctionPoints[diode.CellIxs.First];
                var pB = _junctionPoints[diode.CellIxs.Second + 1];
                var perp = new Avalonia.Point((pB.Y - pA.Y) * 0.2, (pA.X - pB.X) * 0.2);
                var pMidA = new Avalonia.Point(pA.X * 0.7 + pB.X * 0.3 + perp.X, pA.Y * 0.7 + pB.Y * 0.3 + perp.Y);
                var pMidB = new Avalonia.Point(pA.X * 0.3 + pB.X * 0.7 + perp.X, pA.Y * 0.3 + pB.Y * 0.7 + perp.Y);
                var geom = new PathGeometry
                {
                    Figures =
                    {
                        new PathFigure
                        {
                            StartPoint = pA,
                            Segments = { new BezierSegment { Point1 = pMidA, Point2 = pMidB, Point3 = pB } },
                            IsClosed = false
                        }
                    }
                };
                context.DrawGeometry(null, new Pen(Brushes.Black, 5) { Brush = new SolidColorBrush(Avalonia.Media.Color.FromArgb(200, 0, 0, 0)) }, geom);
                context.DrawGeometry(null, new Pen(Brushes.Red, 3), geom);
            }
        }

        // draw bypass diode endpoints
        foreach (var ix in _bypassJunctions)
        {
            if ((uint)ix < (uint)_junctionPoints.Length)
                DrawJunction(context, _junctionPoints[ix], Brushes.Red);
        }

        // mouseover hint
        if (_mouseoverJunction >= 0 && _mouseoverJunction < _junctionPoints.Length)
        {
            DrawJunction(context, _junctionPoints[_mouseoverJunction], Brushes.White);
        }
    }

    private void DrawPolyline(DrawingContext ctx, Point[] pts, Pen pen)
    {
        if (pts.Length < 2) return;
        var fig = new PathFigure { StartPoint = pts[0] };
        for (int i = 1; i < pts.Length; i++) fig.Segments.Add(new LineSegment { Point = pts[i] });
        var geom = new PathGeometry { Figures = { fig } };
        ctx.DrawGeometry(null, pen, geom);
    }

    private void DrawJunction(DrawingContext ctx, Point p, IBrush brush)
        => ctx.DrawEllipse(brush, null, p, JUNCTION_RADIUS, JUNCTION_RADIUS);

    #endregion

    #region Pointer input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!Editable || CellString == null || _array == null) return;

        RecomputeArrayViewModel();
        var p = e.GetPosition(this);

        if (EditBypassDiodes)
        {
            int j = GetJunctionIxAtPixel(p);
            if (j >= 0) ClickBypassJunction(j);
        }
        else
        {
            var cell = GetCellAtPixel(p);
            if (cell != null) ClickCell(cell);
        }

        CellStringChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_array == null) return;

        RecomputeArrayViewModel();
        var p = e.GetPosition(this);

        if (EditBypassDiodes)
        {
            _mouseoverJunction = GetJunctionIxAtPixel(p);
            _mouseoverCell = null;
        }
        else
        {
            _mouseoverCell = GetCellAtPixel(p);
            _mouseoverJunction = -1;
        }

        Cursor = (_mouseoverJunction == -1 && _mouseoverCell == null)
            ? new Cursor(StandardCursorType.Arrow)
            : new Cursor(StandardCursorType.Hand);

        InvalidateVisual();
    }

    #endregion

    #region Core logic (adapted)

    private void RecomputeArrayViewModel()
    {
        if (_array == null || _array.LayoutTexture == null) return;

        if (!ReferenceEquals(_layoutImageSharp, _array.LayoutTexture))
        {
            _layoutImageSharp = _array.LayoutTexture;
            _layoutBitmapCached = null; // will rebuild on next render
            _pixels = null;             // force reread
        }

        // ensure cached Avalonia bitmap exists
        _layoutBitmapCached ??= ConvertImageSharpToWriteableBitmap(_layoutImageSharp!);

        // read pixels lazily from ImageSharp
        _pixels ??= GetPixels(_layoutImageSharp!);

        // compute cell + junction points
        _cellPoints = ComputeCellCenterpoints(CellString);
        _junctionPoints = ComputeJunctions(_cellPoints);

        EnsureSelectionOverlay();
    }

    private void EnsureSelectionOverlay()
    {
        if (_layoutBitmapCached == null) return;
        var scaled = GetScaledArrayRect();
        var size = new PixelSize(Math.Max((int)Math.Round(scaled.Width), 1), Math.Max((int)Math.Round(scaled.Height), 1));
        if (_texSelected == null || _texSelected.PixelSize != size)
        {
            _texSelected?.Dispose();
            _texSelected = new WriteableBitmap(size, new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888);
        }
    }

    private void RecomputeTexSelected()
    {
        if (_texSelected == null || _layoutBitmapCached == null || CellString == null) return;

        var scaled = GetScaledArrayRect();
        double scaleX = scaled.Width / _layoutBitmapCached.PixelSize.Width;
        double scaleY = scaled.Height / _layoutBitmapCached.PixelSize.Height;
        int selW = _texSelected.PixelSize.Width;
        int selH = _texSelected.PixelSize.Height;

        int animation = (int)((DateTime.UtcNow.Ticks / 1_000_000) % 16);

        using var fb = _texSelected.Lock();

        // fb.RowBytes is the stride in bytes for each row
        int stride = fb.RowBytes;
        int bytes = stride * selH;

        // Build a BGRA buffer in managed memory, then copy it into the WriteableBitmap
        var buffer = new byte[bytes]; // zero-initialized → fully transparent

        foreach (var cell in CellString.Cells)
        {
            foreach (var px in cell.Pixels)
            {
                int i = (int)(px.First * scaleX);
                int j = (int)(px.Second * scaleY);
                if ((uint)i >= (uint)selW || (uint)j >= (uint)selH) continue;

                bool mask = ((i + j + animation) % 16) < 8;
                byte a = mask ? (byte)0x80 : (byte)0x00;

                int off = j * stride + i * 4; // BGRA layout
                buffer[off + 0] = 0xFF; // B
                buffer[off + 1] = 0xFF; // G
                buffer[off + 2] = 0xFF; // R
                buffer[off + 3] = a;    // A
            }
        }

        // Copy managed buffer → bitmap
        Marshal.Copy(buffer, 0, fb.Address, bytes);

    }

    private Point[] ComputeCellCenterpoints(ArraySpec.CellString? cellString)
    {
        if (cellString == null || _layoutBitmapCached == null)
            return System.Array.Empty<Point>();

        var scaled = GetScaledArrayRect();
        double scaleX = scaled.Width / _layoutBitmapCached.PixelSize.Width;
        double scaleY = scaled.Height / _layoutBitmapCached.PixelSize.Height;

        int n = cellString.Cells.Count;
        var points = new Point[n];
        for (int i = 0; i < n; i++)
        {
            var cell = cellString.Cells[i];
            long sx = 0, sy = 0; int m = cell.Pixels.Count;
            foreach (var xy in cell.Pixels) { sx += xy.First; sy += xy.Second; }
            points[i] = new Point((sx / (double)m) * scaleX, (sy / (double)m) * scaleY);
        }
        return points;
    }

    private Point[] ComputeJunctions(Point[] cells)
    {
        if (cells.Length == 0) return System.Array.Empty<Point>();
        if (cells.Length == 1)
            return new[] { new Point(cells[0].X - 10, cells[0].Y), new Point(cells[0].X + 10, cells[0].Y) };

        int n = cells.Length + 1;
        var jx = new Point[n];
        jx[0] = new Point(cells[0].X * 1.5 - cells[1].X * 0.5, cells[0].Y * 1.5 - cells[1].Y * 0.5);
        jx[^1] = new Point(cells[^1].X * 1.5 - cells[^2].X * 0.5, cells[^1].Y * 1.5 - cells[^2].Y * 0.5);
        for (int i = 1; i < n - 1; i++)
            jx[i] = new Point((cells[i - 1].X + cells[i].X) * 0.5, (cells[i - 1].Y + cells[i].Y) * 0.5);
        return jx;
    }

    private Rect GetScaledArrayRect()
    {
        if (_layoutBitmapCached == null)
        {
            return new Rect();
        }

        double texW = _layoutBitmapCached.PixelSize.Width;
        double texH = _layoutBitmapCached.PixelSize.Height;
        double w = Math.Max(Bounds.Width, 1);
        double h = Math.Max(Bounds.Height, 1);
        double scale = Math.Min(w / texW, h / texH);

        return new Rect(0, 0, scale * texW, scale * texH);
    }

    private static WriteableBitmap ConvertImageSharpToWriteableBitmap(Image<Rgba32> src)
    {
        var size = new PixelSize(src.Width, src.Height);
        var wb = new WriteableBitmap(size, new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888);

        using var fb = wb.Lock();

        int stride = fb.RowBytes;
        int w = src.Width;
        int h = src.Height;

        // Build a managed BGRA buffer (zero-initialized)
        var buffer = new byte[stride * h];

        for (int y = 0; y < h; y++)
        {
            var rowSpan = src.DangerousGetPixelRowMemory(y).Span; // or src.GetPixelRowSpan(y)
            int rowOffset = y * stride;

            for (int x = 0; x < w; x++)
            {
                Rgba32 px = rowSpan[x];

                int off = rowOffset + x * 4; // BGRA
                buffer[off + 0] = px.B;                         // B
                buffer[off + 1] = px.G;                         // G
                buffer[off + 2] = px.R;                         // R
                buffer[off + 3] = px.A == 0 ? (byte)255 : px.A; // A (ensure nonzero alpha like legacy)
            }
        }

        // Copy managed buffer → WriteableBitmap
        Marshal.Copy(buffer, 0, fb.Address, buffer.Length);

        return wb;
    }

    private static uint[,] GetPixels(Image<Rgba32> img)
    {
        int w = img.Width, h = img.Height;
        var pixels = new uint[w, h];
        for (int y = 0; y < h; y++)
        {
            var row = img.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < w; x++)
            {
                var p = row[x];
                byte a = p.A == 0 ? (byte)255 : p.A; // legacy assumed opaque
                uint argb = ((uint)a << 24) | ((uint)p.R << 16) | ((uint)p.G << 8) | p.B;
                pixels[x, y] = argb;
            }
        }
        return pixels;
    }

    private ArraySpec.Cell? GetCellAtPixel(Point pt)
    {
        if (_array == null || _layoutImageSharp == null || _pixels == null) return null;
        if (!TryGetTexCoord(pt, out int x, out int y)) return null;
        var argb = _pixels[x, y];
        if (IsGrayscaleArgb(argb)) return null;

        // flood fill in texture space
        int w = _layoutImageSharp.Width, h = _layoutImageSharp.Height;
        var visited = new HashSet<Pair<int>>();
        var q = new Queue<Pair<int>>();
        q.Enqueue(new Pair<int>(x, y));

        while (q.Count > 0)
        {
            var p = q.Dequeue();
            if (!visited.Add(p)) continue;

            for (int x2 = p.First - 1; x2 <= p.First + 1; x2++)
            for (int y2 = p.Second - 1; y2 <= p.Second + 1; y2++)
            {
                if (x2 <= 0 || x2 >= w || y2 <= 0 || y2 >= h) continue;
                if (_pixels[x2, y2] != argb) continue;
                q.Enqueue(new Pair<int>(x2, y2));
            }
        }

        var newCell = new ArraySpec.Cell
        {
            // Store ImageSharp Rgba32 (R,G,B,A order)
            Color = new Rgba32(
                (byte)((argb >> 16) & 0xFF), // R
                (byte)((argb >> 8) & 0xFF),  // G
                (byte)(argb & 0xFF),         // B
                (byte)((argb >> 24) & 0xFF)  // A
            )
        };
        newCell.Pixels.AddRange(visited);
        newCell.Pixels.Sort((a, b) => a.Second != b.Second ? a.Second.CompareTo(b.Second) : a.First.CompareTo(b.First));
        return newCell;
    }

    private int GetJunctionIxAtPixel(Point pt)
    {
        int minIx = -1; double minDD = JUNCTION_RADIUS_CLICK * JUNCTION_RADIUS_CLICK;
        for (int i = 0; i < _junctionPoints.Length; i++)
        {
            double dx = pt.X - _junctionPoints[i].X;
            double dy = pt.Y - _junctionPoints[i].Y;
            double dd = dx * dx + dy * dy;
            if (dd < minDD) { minDD = dd; minIx = i; }
        }
        return minIx;
    }

    private bool TryGetTexCoord(Point pt, out int x, out int y)
    {
        x = y = -1;
        if (_layoutBitmapCached == null)
            return false;

        var scaled = GetScaledArrayRect();
        if (scaled.Width <= 0 || scaled.Height <= 0)
            return false;

        double u = pt.X / scaled.Width; // [0,1]
        double v = pt.Y / scaled.Height;

        int w = _layoutBitmapCached.PixelSize.Width;
        int h = _layoutBitmapCached.PixelSize.Height;

        x = (int)Math.Floor(u * w);
        y = (int)Math.Floor(v * h);

        return !(x < 0 || x >= w || y < 0 || y >= h);
    }


    private void ClickBypassJunction(int junction)
    {
        if (!_bypassJunctions.Remove(junction))
            _bypassJunctions.Add(junction);
        if (_bypassJunctions.Count == 2 && CellString != null)
        {
            int ix0 = Math.Min(_bypassJunctions[0], _bypassJunctions[1]);
            int ix1 = Math.Max(_bypassJunctions[0], _bypassJunctions[1]) - 1;
            var newDiode = new ArraySpec.BypassDiode { CellIxs = new Pair<int>(ix0, ix1) };
            if (!CellString.BypassDiodes.Remove(newDiode))
                CellString.BypassDiodes.Add(newDiode);
            _bypassJunctions.Clear();
        }
    }

    private void ClickCell(ArraySpec.Cell cell)
    {
        if (CellString == null) return;
        if (!CellString.Cells.Remove(cell))
        {
            CellString.Cells.Add(cell);
        }
        else
        {
            // prune bypass diodes
            CellString.BypassDiodes.RemoveAll(d => d.CellIxs.First >= CellString.Cells.Count || d.CellIxs.Second >= CellString.Cells.Count);
        }
    }

    #endregion

    #region Helpers

    private static bool IsGrayscaleArgb(uint argb)
    {
        byte r = (byte)((argb >> 16) & 0xFF);
        byte g = (byte)((argb >> 8) & 0xFF);
        byte b = (byte)(argb & 0xFF);
        return r == g && g == b; // same simple heuristic as legacy
    }

    #endregion
}
