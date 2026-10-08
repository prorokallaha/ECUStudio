using System.Text.Json.Serialization;
using ECUStudio.Binary;
using ECUStudio.Calibration.Model;
using ECUStudio.Core;

namespace ECUStudio.Calibration.Scanning;

[JsonConverter(typeof(JsonStringEnumConverter<CandidateStatus>))]
public enum CandidateStatus { Candidate, Confirmed, Rejected }

public sealed record RoleHypothesis(MapRole Role, double Confidence, string Rationale, SourceType Source, IReadOnlyList<Evidence> Evidence);

/// <summary>How a candidate map differs from the stock file at the same address (raw values; scaling is not known).</summary>
public sealed record CandidateChange(int ChangedCells, int TotalCells, double MeanDeltaPct, double MaxDeltaPct, double MinDeltaPct,
    double StockMin, double StockMax, double ModMin, double ModMax, bool AxesChanged)
{
    public bool IsModified => ChangedCells > 0 || AxesChanged;

    /// <summary>Compares the candidate's axes and data in two images of the same layout.</summary>
    public static CandidateChange Compare(MapCandidate c, ReadOnlySpan<byte> stock, ReadOnlySpan<byte> mod)
    {
        var n = c.Rows * c.Cols;
        var s = new double[n];
        var m = new double[n];
        ValueReader.ReadRawArray(stock, c.Address, n, c.DataType, c.Endian, s);
        ValueReader.ReadRawArray(mod, c.Address, n, c.DataType, c.Endian, m);
        var axisBytes = (c.Rows + c.Cols) * c.DataType.Size();
        var axesChanged = !stock.Slice(c.XAxisAddress, axisBytes).SequenceEqual(mod.Slice(c.XAxisAddress, axisBytes));
        int changed = 0, pctCount = 0;
        double sum = 0, max = double.MinValue, min = double.MaxValue;
        for (var i = 0; i < n; i++)
        {
            if (s[i] == m[i]) continue;
            changed++;
            if (s[i] == 0) continue;
            var pct = (m[i] - s[i]) / Math.Abs(s[i]) * 100;
            sum += pct; pctCount++;
            max = Math.Max(max, pct); min = Math.Min(min, pct);
        }
        return new CandidateChange(changed, n, pctCount == 0 ? 0 : Math.Round(sum / pctCount, 1), pctCount == 0 ? 0 : Math.Round(max, 1), pctCount == 0 ? 0 : Math.Round(min, 1),
            s.Min(), s.Max(), m.Min(), m.Max(), axesChanged);
    }
}

public sealed record AxisGuess(AxisQuantity Quantity, double Likelihood, double Factor, string Unit);

/// <summary>
/// A map found by structure scanning whose purpose is not established.
/// It never becomes a confirmed map automatically: only a user decision does that.
/// </summary>
public sealed record MapCandidate
{
    public required string Id { get; init; }
    public int Address { get; init; }
    public int HeaderAddress { get; init; }
    public int Rows { get; init; }
    public int Cols { get; init; }
    public DataType DataType { get; init; } = DataType.UInt16;
    public Endianness Endian { get; init; } = Endianness.Big;
    public required double[] XAxisRaw { get; init; }
    public required double[] YAxisRaw { get; init; }
    public double RawMin { get; init; }
    public double RawMax { get; init; }
    public IReadOnlyList<AxisGuess> XAxisGuesses { get; init; } = [];
    public IReadOnlyList<AxisGuess> YAxisGuesses { get; init; } = [];
    public IReadOnlyList<RoleHypothesis> Hypotheses { get; init; } = [];
    public CandidateStatus Status { get; init; } = CandidateStatus.Candidate;
    public MapRole? ConfirmedRole { get; init; }
    public string? DecisionNote { get; init; }
    /// <summary>Difference from the stock file, when one was analysed.</summary>
    public CandidateChange? Change { get; init; }

    public RoleHypothesis? Best => Hypotheses.Count == 0 ? null : Hypotheses[0];

    /// <summary>
    /// Label shown for the candidate. A hypothesis is never presented as the map's name:
    /// "Unknown map · candidate: Torque to IQ, 18 %" until the user confirms a role.
    /// </summary>
    public string DisplayName => Status == CandidateStatus.Confirmed && ConfirmedRole is { } r
        ? $"{r.DisplayName()} (confirmed by user)"
        : Best is { Role: not MapRole.Unknown } b
            ? $"Unknown map {Rows}×{Cols} @0x{Address:X} · candidate: {b.Role.DisplayName()}, {b.Confidence * 100:0} %"
            : $"Unknown map {Rows}×{Cols} @0x{Address:X}";
    public int XAxisAddress => HeaderAddress + 4;
    public int YAxisAddress => XAxisAddress + Cols * 2;
}
