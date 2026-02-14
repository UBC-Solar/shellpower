using System.Text.Json;

namespace SSCP.ShellPower;

public sealed class LookupTable2
{

    private readonly double[] _x;      // sorted ascending
    private readonly double[] _i;      // sorted ascending
    private readonly double[,] _z;     // [x, y]

    public LookupTable2(double[] xGrid, double[] iGrid, double[,] z)
    {
        if (xGrid is null || iGrid is null || z is null) throw new ArgumentNullException();
        if (z.GetLength(0) != xGrid.Length) throw new ArgumentException("eta rows must match vGrid length");
        if (z.GetLength(1) != iGrid.Length) throw new ArgumentException("eta cols must match iGrid length");
        if (xGrid.Length < 2 || iGrid.Length < 2) throw new ArgumentException("Need at least 2 points per axis");

        _x = xGrid;
        _i = iGrid;
        _z = z;
    }

    // Clamp outside range to nearest edge (safe for MPPT)
    public double GetEta(double v, double i)
    {
        int v0 = FindLowerIndex(_x, v);
        int i0 = FindLowerIndex(_i, i);

        int v1 = Math.Min(v0 + 1, _x.Length - 1);
        int i1 = Math.Min(i0 + 1, _i.Length - 1);

        // If exactly on upper edge, collapse to edge cell
        if (v0 == v1) v0 = Math.Max(0, v1 - 1);
        if (i0 == i1) i0 = Math.Max(0, i1 - 1);
        v1 = v0 + 1;
        i1 = i0 + 1;

        double vL = _x[v0], vH = _x[v1];
        double iL = _i[i0], iH = _i[i1];

        double tv = (vH == vL) ? 0.0 : (v - vL) / (vH - vL);
        double ti = (iH == iL) ? 0.0 : (i - iL) / (iH - iL);

        // Clamp interpolation factors
        tv = Math.Clamp(tv, 0.0, 1.0);
        ti = Math.Clamp(ti, 0.0, 1.0);

        double e00 = _z[v0, i0];
        double e10 = _z[v1, i0];
        double e01 = _z[v0, i1];
        double e11 = _z[v1, i1];

        // Bilinear interpolation
        double e0 = e00 + tv * (e10 - e00);
        double e1 = e01 + tv * (e11 - e01);
        return e0 + ti * (e1 - e0);
    }

    // Returns largest idx such that grid[idx] <= x, clamped to [0, n-2]
    private static int FindLowerIndex(double[] grid, double x)
    {
        if (x <= grid[0]) return 0;
        int n = grid.Length;
        if (x >= grid[n - 1]) return n - 2;

        int idx = Array.BinarySearch(grid, x);
        if (idx >= 0) return Math.Min(idx, n - 2);

        idx = ~idx;          // first element > x
        return idx - 1;      // element <= x
    }
    
    private sealed class EfficiencyLutJson
    {
        public double[] Voltage { get; set; } = Array.Empty<double>();
        public double[] Current { get; set; } = Array.Empty<double>();
        public double[][] Efficiency { get; set; } = Array.Empty<double[]>();

        // Optional metadata
        public string? Notes { get; set; }
    }
    
    public static LookupTable2 FromJSON(string jsonPath)
    {
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException("LUT file not found", jsonPath);

        var json = File.ReadAllText(jsonPath);

        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var dto = JsonSerializer.Deserialize<EfficiencyLutJson>(json, opts)
                  ?? throw new InvalidDataException("Failed to deserialize LUT JSON");
        
        int nv = dto.Voltage.Length;
        int ni = dto.Current.Length;

        var eta = new double[nv, ni];
        for (int v = 0; v < nv; v++)
        {
            for (int i = 0; i < ni; i++)
            {
                eta[v, i] = dto.Efficiency[v][i];
            }
        }

        return new LookupTable2(dto.Voltage, dto.Current, eta);

    }
}