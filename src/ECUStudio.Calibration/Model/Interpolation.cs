namespace ECUStudio.Calibration.Model;

/// <summary>
/// ECU-style lookups: linear interpolation between breakpoints, clamped at the axis ends
/// (Bosch ECUs do not extrapolate). Allocation-free.
/// </summary>
public static class Interpolation
{
    public static double Bilinear(CalibrationMap map, double x, double y)
    {
        var (c0, c1, tx) = Locate(map.XAxis, x);
        if (map.Rows <= 1) return Lerp(map.Values[c0], map.Values[c1], tx);
        var (r0, r1, ty) = Locate(map.YAxis, y);
        var cols = map.Cols;
        var v00 = map.Values[r0 * cols + c0];
        var v01 = map.Values[r0 * cols + c1];
        var v10 = map.Values[r1 * cols + c0];
        var v11 = map.Values[r1 * cols + c1];
        return Lerp(Lerp(v00, v01, tx), Lerp(v10, v11, tx), ty);
    }

    /// <summary>Inverse lookup along Y for a fixed X: finds y such that map(x, y) == target (monotonic maps).</summary>
    public static double InverseY(CalibrationMap map, double x, double target)
    {
        var rows = map.Rows;
        if (rows < 2) return double.NaN;
        var prevV = Bilinear(map, x, map.YAxis[0]);
        if (target <= prevV) return map.YAxis[0];
        for (var r = 1; r < rows; r++)
        {
            var v = Bilinear(map, x, map.YAxis[r]);
            if (target <= v)
            {
                var t = v == prevV ? 0 : (target - prevV) / (v - prevV);
                return map.YAxis[r - 1] + t * (map.YAxis[r] - map.YAxis[r - 1]);
            }
            prevV = v;
        }
        return map.YAxis[rows - 1];
    }

    public static (int I0, int I1, double T) Locate(ReadOnlySpan<double> axis, double v)
    {
        var n = axis.Length;
        if (n <= 1 || v <= axis[0]) return (0, 0, 0);
        if (v >= axis[n - 1]) return (n - 1, n - 1, 0);
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) >> 1;
            if (axis[mid] <= v) lo = mid; else hi = mid;
        }
        var span = axis[hi] - axis[lo];
        return (lo, hi, span <= 0 ? 0 : (v - axis[lo]) / span);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
