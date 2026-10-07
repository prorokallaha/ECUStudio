using ECUStudio.Binary;
using ECUStudio.Calibration.Model;
using ECUStudio.Core;

namespace ECUStudio.Calibration.Scanning;

/// <summary>
/// Finds self-describing Bosch maps laid out as
/// <c>u16 nx, u16 ny, u16 x[nx], u16 y[ny], u16 z[nx*ny]</c> (row-major over Y).
/// This layout is common in EDC15/EDC16 calibrations; it is an assumption of this scanner,
/// so every result is a candidate with plausibility-derived confidence, not a fact.
/// </summary>
public static class BoschMapScanner
{
    public sealed record Options
    {
        public int MinAxis { get; init; } = 3;
        public int MaxAxis { get; init; } = 32;
        public int Start { get; init; }
        public int? End { get; init; }
        public Endianness Endian { get; init; } = Endianness.Big;
    }

    public sealed record RawMap(int HeaderAddress, int DataAddress, int Cols, int Rows, double[] X, double[] Y, double[] Z);

    public static List<RawMap> Scan(ReadOnlySpan<byte> data, Options? options = null)
    {
        options ??= new Options();
        var results = new List<RawMap>();
        var end = Math.Min(options.End ?? data.Length, data.Length);
        var e = options.Endian;
        var offset = options.Start & ~1;
        Span<double> xs = stackalloc double[options.MaxAxis];
        Span<double> ys = stackalloc double[options.MaxAxis];

        while (offset + 8 <= end)
        {
            var nx = (int)ValueReader.ReadRaw(data, offset, DataType.UInt16, e);
            var ny = (int)ValueReader.ReadRaw(data, offset + 2, DataType.UInt16, e);
            if (nx < options.MinAxis || ny < 2 || nx > options.MaxAxis || ny > options.MaxAxis)
            {
                offset += 2;
                continue;
            }
            var total = 4 + 2 * (nx + ny + nx * ny);
            if (offset + total > end) { offset += 2; continue; }

            ValueReader.ReadRawArray(data, offset + 4, nx, DataType.UInt16, e, xs);
            ValueReader.ReadRawArray(data, offset + 4 + 2 * nx, ny, DataType.UInt16, e, ys);
            if (!IsPlausibleAxis(xs[..nx]) || !IsPlausibleAxis(ys[..ny]))
            {
                offset += 2;
                continue;
            }

            var z = new double[nx * ny];
            var dataAddress = offset + 4 + 2 * (nx + ny);
            ValueReader.ReadRawArray(data, dataAddress, nx * ny, DataType.UInt16, e, z);
            if (!IsPlausibleData(z))
            {
                offset += 2;
                continue;
            }

            results.Add(new RawMap(offset, dataAddress, nx, ny, xs[..nx].ToArray(), ys[..ny].ToArray(), z));
            offset += total;
        }
        return results;
    }

    /// <summary>Strictly increasing, not a trivial 0,1,2,... counter, not fill bytes.</summary>
    private static bool IsPlausibleAxis(ReadOnlySpan<double> axis)
    {
        var trivial = true;
        for (var i = 1; i < axis.Length; i++)
        {
            if (axis[i] <= axis[i - 1]) return false;
            if (axis[i] - axis[i - 1] != 1) trivial = false;
        }
        if (trivial && axis[0] <= 1) return false;
        return axis[^1] < 0xFF00;
    }

    private static bool IsPlausibleData(double[] z)
    {
        double min = double.MaxValue, max = double.MinValue;
        var fill = 0;
        foreach (var v in z)
        {
            if (v < min) min = v;
            if (v > max) max = v;
            if (v == 0xFFFF || v == 0) fill++;
        }
        if (fill == z.Length) return false;
        return max < 0xFFFF || min != max;
    }
}

/// <summary>Classifies raw axes/maps by their physical signature into role hypotheses.</summary>
public static class SignatureClassifier
{
    /// <summary>Raw-value heuristics for EDC16-style scaling. Each guess carries a likelihood, not a verdict.</summary>
    public static List<AxisGuess> ClassifyAxis(double[] raw)
    {
        var guesses = new List<AxisGuess>();
        var first = raw[0];
        var last = raw[^1];
        var n = raw.Length;

        if (n >= 4 && first < 2000 && last is >= 2500 and <= 6500)
            guesses.Add(new AxisGuess(AxisQuantity.EngineSpeed, 0.85, 1, "rpm"));
        // Pedal 0…100 % at 0.01 %; real EDC16 axes often start at a small dead-band value (e.g. 1 %) instead of 0.
        if (first <= 500 && last is >= 8000 and <= 10000)
            guesses.Add(new AxisGuess(AxisQuantity.PedalPosition, first == 0 ? 0.7 : 0.6, 0.01, "%"));
        if (first <= 600 && last is >= 2500 and <= 9500)
        {
            guesses.Add(new AxisGuess(AxisQuantity.InjectionQuantity, 0.55, 0.01, "mg/stroke"));
            guesses.Add(new AxisGuess(AxisQuantity.Torque, 0.4, 0.1, "Nm"));
        }
        if (n <= 6 && first >= 500 && last <= 1200)
            guesses.Add(new AxisGuess(AxisQuantity.AtmosphericPressure, 0.8, 1, "mbar"));
        if (first < 450 && last is >= 600 and <= 1600)
            guesses.Add(new AxisGuess(AxisQuantity.AirMass, 0.6, 1, "mg/stroke"));
        if (n <= 8 && first >= 1 && last <= 8 && raw.Zip(raw.Skip(1)).All(p => p.Second - p.First == 1))
            guesses.Add(new AxisGuess(AxisQuantity.Gear, 0.7, 1, "-"));

        guesses.Sort((a, b) => b.Likelihood.CompareTo(a.Likelihood));
        return guesses;
    }

    private sealed record Signature(MapRole Role, AxisQuantity X, AxisQuantity Y, double ZMinRaw, double ZMaxRaw, string Description);

    // z ranges are raw u16 values under the plugin's assumed EDC16 scaling.
    private static readonly Signature[] Signatures =
    [
        new(MapRole.DriverWish, AxisQuantity.EngineSpeed, AxisQuantity.PedalPosition, 0, 6000, "RPM × pedal → torque (0.1 Nm)"),
        new(MapRole.TorqueLimiter, AxisQuantity.EngineSpeed, AxisQuantity.AtmosphericPressure, 500, 6000, "RPM × atmospheric pressure → torque (0.1 Nm)"),
        new(MapRole.BoostLimiter, AxisQuantity.EngineSpeed, AxisQuantity.AtmosphericPressure, 900, 3500, "RPM × atmospheric pressure → boost (mbar abs)"),
        new(MapRole.TorqueToIq, AxisQuantity.EngineSpeed, AxisQuantity.Torque, 0, 9500, "RPM × torque → IQ (0.01 mg)"),
        new(MapRole.SmokeLimiter, AxisQuantity.EngineSpeed, AxisQuantity.AirMass, 0, 9500, "RPM × air mass → IQ (0.01 mg)"),
        new(MapRole.BoostTarget, AxisQuantity.EngineSpeed, AxisQuantity.InjectionQuantity, 900, 3500, "RPM × IQ → boost (mbar abs)"),
        new(MapRole.Soi, AxisQuantity.EngineSpeed, AxisQuantity.InjectionQuantity, 0, 1400, "RPM × IQ → SOI"),
        new(MapRole.Duration, AxisQuantity.EngineSpeed, AxisQuantity.InjectionQuantity, 0, 2000, "RPM × IQ → duration"),
        new(MapRole.VntDuty, AxisQuantity.EngineSpeed, AxisQuantity.InjectionQuantity, 1000, 9500, "RPM × IQ → VNT duty (0.01 %)"),
        new(MapRole.GearTorqueLimiter, AxisQuantity.EngineSpeed, AxisQuantity.Gear, 500, 6000, "RPM × gear → torque (0.1 Nm)"),
    ];

    public static List<RoleHypothesis> Hypotheses(BoschMapScanner.RawMap raw, IReadOnlyList<AxisGuess> xGuesses, IReadOnlyList<AxisGuess> yGuesses)
    {
        var zMin = raw.Z.Min();
        var zMax = raw.Z.Max();
        var scored = new List<(Signature Sig, double Score, string Why)>();
        foreach (var sig in Signatures)
        {
            var px = xGuesses.FirstOrDefault(g => g.Quantity == sig.X)?.Likelihood ?? 0;
            var py = yGuesses.FirstOrDefault(g => g.Quantity == sig.Y)?.Likelihood ?? 0;
            if (px == 0 || py == 0) continue;
            var pz = zMin >= sig.ZMinRaw && zMax <= sig.ZMaxRaw ? 1.0 : 0.15;
            var shape = ShapeScore(sig.Role, raw);
            var score = px * py * pz * shape;
            if (score > 0.01) scored.Add((sig, score, $"{sig.Description}; X≈{sig.X} ({px:0.00}), Y≈{sig.Y} ({py:0.00}), values {zMin}–{zMax} raw"));
        }

        // Residual mass for "unknown purpose" so that confidences never sum to certainty.
        var total = scored.Sum(s => s.Score) + 0.25;
        var evidence = new List<Evidence> { new(EvidenceType.Binary, $"0x{raw.HeaderAddress:X6}", $"Bosch header nx={raw.Cols}, ny={raw.Rows}") };
        var result = scored
            .OrderByDescending(s => s.Score)
            .Select(s => new RoleHypothesis(s.Sig.Role, Math.Round(s.Score / total, 3), s.Why, SourceType.SignatureScan, evidence))
            .ToList();
        result.Add(new RoleHypothesis(MapRole.Unknown, Math.Round(0.25 / total, 3), "Residual: purpose not established by signature", SourceType.SignatureScan, evidence));
        return result;
    }

    /// <summary>Shape cues that separate roles with identical axes (e.g. SOI vs duration).</summary>
    private static double ShapeScore(MapRole role, BoschMapScanner.RawMap raw)
    {
        // average change along Y (IQ / torque / pedal direction)
        double dy = 0;
        for (var r = 1; r < raw.Rows; r++)
            for (var c = 0; c < raw.Cols; c++)
                dy += raw.Z[r * raw.Cols + c] - raw.Z[(r - 1) * raw.Cols + c];
        var increasingInY = dy > 0;
        return role switch
        {
            MapRole.Duration or MapRole.TorqueToIq or MapRole.SmokeLimiter or MapRole.DriverWish => increasingInY ? 1.0 : 0.3,
            MapRole.Soi => 0.8,
            _ => 1.0,
        };
    }
}
