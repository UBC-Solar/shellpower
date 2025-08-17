using System;
using System.Collections.Generic;
using System.Diagnostics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace SSCP.ShellPower
{
    /// <summary>
    /// Represents a solar array.
    /// </summary>
    public class ArraySpec
    {
        public ArraySpec()
        {
            Strings = new List<CellString>();
            CellSpec = new CellSpec();
            BypassDiodeSpec = new DiodeSpec();
        }

        /// <summary>Shape of the array. Dimensions in meters; +Y is up.</summary>
        public Mesh Mesh { get; set; } = default!;

        /// <summary>Layout image (each cell is a unique flat color).</summary>
        public Image<Rgba32>? LayoutTexture { get; set; }

        /// <summary>
        /// UV alignment for layout texture (top-down ortho projection).
        /// [x0, z0] = top-left (model meters), [x1, z1] = bottom-right.
        /// </summary>
        public BoundsSpec LayoutBounds { get; set; }

        /// <summary>Per-cell properties (area, efficiency, etc.).</summary>
        public CellSpec CellSpec { get; }

        /// <summary>Bypass diode spec (single type for all).</summary>
        public DiodeSpec BypassDiodeSpec { get; }

        /// <summary>Cells grouped into series strings.</summary>
        public List<CellString> Strings { get; }

        /// <summary>Encapsulation loss (e.g., 0.03 = 3%).</summary>
        public double EncapsulationLoss { get; set; }

        // ----------------------- Nested types -----------------------

        public class CellString
        {
            public CellString()
            {
                Cells = new List<Cell>();
                BypassDiodes = new List<BypassDiode>();
                Name = "NewString";
            }

            public List<Cell> Cells { get; }
            public List<BypassDiode> BypassDiodes { get; }
            public string Name { get; set; }

            public override string ToString()
            {
                var s = $"{Name} ({Cells.Count} cells";
                if (BypassDiodes.Count > 0) s += $", {BypassDiodes.Count} diodes";
                return s + ")";
            }
        }

        public class BypassDiode
        {
            /// <summary>Connects these two cells in string order.</summary>
            public Pair<int> CellIxs { get; set; }

            public override int GetHashCode() => CellIxs.GetHashCode();
            public override bool Equals(object? obj) =>
                obj is BypassDiode other && CellIxs.Equals(other.CellIxs);
        }

        public class Cell
        {
            public Rgba32 Color { get; set; }
            /// <summary>Texel coordinates belonging to this cell (x,y), scanline order.</summary>
            public List<Pair<int>> Pixels { get; }

            public Cell()
            {
                Pixels = new List<Pair<int>>();
                Color = new Rgba32(255, 255, 255, 255);
            }

            public override int GetHashCode() => Pixels.Count == 0 ? 0 : Pixels[0].GetHashCode();

            public override bool Equals(object? other)
            {
                if (other is not Cell b) return false;
                var a = this;

                bool equal;
                if (a.Pixels.Count == 0 || b.Pixels.Count == 0)
                    equal = a.Pixels.Count == b.Pixels.Count;
                else
                    equal = a.Pixels[0].Equals(b.Pixels[0]); // lists are sorted

                if (equal)
                {
                    Debug.Assert(a.Pixels.Count == b.Pixels.Count);
                    for (int i = 0; i < a.Pixels.Count; i++)
                        Debug.Assert(a.Pixels[i].Equals(b.Pixels[i]));
                    Debug.Assert(a.Color.Equals(b.Color));
                }
                return equal;
            }
        }

        // ----------------------- Operations -----------------------

        /// <summary>
        /// Recolors the layout so every string uses a distinct (R,G,0) and each cell
        /// in that string is (R,G,B) where B encodes order. Grayscale/other pixels are normalized.
        /// </summary>
        public void Recolor()
        {
            if (LayoutTexture is null)
                throw new InvalidOperationException("No layout texture is loaded.");
            if (Strings.Count >= 256)
                throw new InvalidOperationException("Cannot create a layout texture with more than 255 strings.");

            // 1) Assign unique colors to each cell
            int nstrings = Strings.Count;
            int nsteps = (int)Math.Ceiling(Math.Sqrt(nstrings));
            int colorIx = 0;

            for (int i = 0; i < nstrings; i++)
            {
                var cells = Strings[i].Cells;
                if (cells.Count == 0) continue;

                if ((colorIx / nsteps) == (colorIx % nsteps)) colorIx++; // avoid R==G bands
                byte red   = (byte)(255 * (colorIx / nsteps) / nsteps);
                byte green = (byte)(255 * (colorIx % nsteps) / nsteps);
                colorIx++;

                int ncells = cells.Count;
                for (int j = 0; j < ncells; j++)
                {
                    byte blue = (byte)(255 * j / Math.Max(1, ncells));
                    cells[j].Color = new Rgba32(red, green, blue, 255);
                }
            }

            // 2) Normalize existing pixels: grayscale -> limited gray; colored -> white
            var img = LayoutTexture;
            int texW = img.Width, texH = img.Height;

            img.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < texH; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < texW; x++)
                    {
                        var px = row[x];
                        if (ColorUtils.IsGrayscale(px))
                        {
                            // Cap bright grays to 200 like the legacy code
                            byte g = px.R > 200 ? (byte)200 : px.R;
                            row[x] = new Rgba32(g, g, g, 255);
                        }
                        else
                        {
                            row[x] = new Rgba32(255, 255, 255, 255);
                        }
                    }
                }
            });

            // 3) Paint each cell’s pixels with its assigned color
            img.ProcessPixelRows(accessor =>
            {
                foreach (var cellStr in Strings)
                foreach (var cell in cellStr.Cells)
                foreach (var p in cell.Pixels)
                {
                    int x = p.First, y = p.Second;
                    if ((uint)x < (uint)texW && (uint)y < (uint)texH)
                    {
                        var row = accessor.GetRowSpan(y);
                        row[x] = cell.Color;
                    }
                }
            });
        }

        /// <summary>
        /// Rebuilds Strings and Cells from the layout image’s colors.
        /// Any opaque non-grayscale pixel is considered a cell texel.
        /// Cells are grouped into strings by (R,G,0) key; per-cell key is (R,G,B).
        /// </summary>
        public void ReadStringsFromColors()
        {
            if (LayoutTexture is null)
                throw new InvalidOperationException("No layout texture is loaded.");

            Strings.Clear();
            var cellMap   = new Dictionary<Rgba32, Cell>();
            var stringMap = new Dictionary<Rgba32, CellString>();

            var img = LayoutTexture;
            int texW = img.Width, texH = img.Height;

            img.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < texH; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < texW; x++)
                    {
                        var c = row[x];
                        if (c.A != 255)
                            throw new ArgumentException("Layout texture cannot be transparent.");

                        if (ColorUtils.IsGrayscale(c)) continue;

                        // String key drops blue to 0
                        var stringColor = new Rgba32(c.R, c.G, 0, 255);

                        if (!stringMap.TryGetValue(stringColor, out var cellStr))
                        {
                            cellStr = new CellString { Name = $"String {Strings.Count}" };
                            Strings.Add(cellStr);
                            stringMap[stringColor] = cellStr;
                        }

                        if (!cellMap.TryGetValue(c, out var cell))
                        {
                            cell = new Cell { Color = c };
                            cellMap[c] = cell;
                            cellStr.Cells.Add(cell);
                        }

                        cell.Pixels.Add(new Pair<int>(x, y));
                    }
                }
            });

            // Sort cells in each string by blue channel (legacy wiring order)
            foreach (var cellStr in Strings)
            {
                cellStr.Cells.Sort((a, b) => a.Color.B.CompareTo(b.Color.B));
            }
        }
    }
}
