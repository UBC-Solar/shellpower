// ImageSharp-based replacements for legacy System.Drawing ColorUtils
// Packages: SixLabors.ImageSharp
// This file removes all System.Drawing dependencies and unsafe code.

using System;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace SSCP.ShellPower
{
    public static class ColorUtils
    {
        /// <summary>
        /// Returns true if the color is grayscale (R≈G≈B).
        /// Optional <paramref name="tolerance"/> lets you allow small differences
        /// due to encoding or filtering (default 0 → exact equality).
        /// </summary>
        public static bool IsGrayscale(Rgba32 c, byte tolerance = 0)
        {
            // Compare in byte space; fast and allocation-free
            return Math.Abs(c.R - c.G) <= tolerance && Math.Abs(c.G - c.B) <= tolerance;
        }

        /// <summary>
        /// Returns a new color with the same RGB and alpha forced to 255 (opaque).
        /// </summary>
        public static Rgba32 Opaque(Rgba32 c) => new Rgba32(c.R, c.G, c.B, 255);

        /// <summary>
        /// In-place: sets every pixel's alpha to 255 (opaque).
        /// </summary>
        public static void RemoveAlpha(Image<Rgba32> img)
        {
            if (img is null) throw new ArgumentNullException(nameof(img));

            img.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        var px = row[x];
                        if (px.A != 255)
                        {
                            px.A = 255;
                            row[x] = px;
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Non-destructive: returns a copy with alpha removed (all pixels opaque).
        /// </summary>
        public static Image<Rgba32> WithoutAlpha(Image<Rgba32> img)
        {
            if (img is null) throw new ArgumentNullException(nameof(img));
            var clone = img.Clone();
            RemoveAlpha(clone);
            return clone;
        }

        /// <summary>
        /// In-place grayscale test on an entire row span. Useful for tight loops.
        /// Returns the count of non-grayscale pixels encountered.
        /// </summary>
        public static int CountNonGrayscale(Span<Rgba32> row, byte tolerance = 0)
        {
            int count = 0;
            for (int i = 0; i < row.Length; i++)
            {
                var p = row[i];
                if (!IsGrayscale(p, tolerance)) count++;
            }
            return count;
        }
    }
}
