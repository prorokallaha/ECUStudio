using ECUStudio.Binary;
using ECUStudio.Calibration.Model;
using ECUStudio.Core;

namespace ECUStudio.Calibration.Analysis;

public sealed record MapDiff
{
    public required string MapId { get; init; }
    public required string Name { get; init; }
    public MapRole Role { get; init; }
    public MapCategory Category { get; init; }
    public string Unit { get; init; } = "-";
    public int Rows { get; init; }
    public int Cols { get; init; }
    public int ChangedCells { get; init; }
    public int TotalCells { get; init; }
    public double ChangedFraction => TotalCells == 0 ? 0 : (double)ChangedCells / TotalCells;
    public double MeanDeltaPct { get; init; }
    public double MaxDeltaPct { get; init; }
    public double MinDeltaPct { get; init; }
    public double MaxAbsDelta { get; init; }
    public double StockMax { get; init; }
    public double ModMax { get; init; }
    public double MaxIncreasePct => StockMax == 0 ? 0 : (ModMax - StockMax) / Math.Abs(StockMax) * 100;
    public bool XAxisChanged { get; init; }
    public bool YAxisChanged { get; init; }
    /// <summary>Mean mod/stock ratio over changed cells (null if none changed or stock has zeros).</summary>
    public double? MeanRatio { get; init; }
    public double? RatioStdDev { get; init; }
    public required double[] Stock { get; init; }
    public required double[] Modified { get; init; }
    public required double[] XAxis { get; init; }
    public required double[] YAxis { get; init; }
    public required double[] StockXAxis { get; init; }
    public required double[] StockYAxis { get; init; }
    public bool IsModified => ChangedCells > 0 || XAxisChanged || YAxisChanged;
}

public sealed record UnmappedChange(int Start, int Length, SectionKind Section, string? NearestMapId);

public sealed record DiffResult
{
    public required IReadOnlyList<MapDiff> Maps { get; init; }
    public required IReadOnlyList<ByteRange> ChangedRanges { get; init; }
    public required IReadOnlyList<UnmappedChange> UnmappedChanges { get; init; }
    public int ChangedBytes { get; init; }
    public IEnumerable<MapDiff> Modified => Maps.Where(m => m.IsModified);
}

public static class MapDiffer
{
    private const double Epsilon = 1e-9;

    public static DiffResult Compare(BinaryImage stock, BinaryImage mod, CalibrationSet stockSet, CalibrationSet modSet,
        IReadOnlyList<MemorySection> sections, IEnumerable<ByteRange>? knownExtraRegions = null)
    {
        if (stock.Length != mod.Length)
            throw new IncompatibleBinariesException($"Stock ({stock.Length} bytes) and modified ({mod.Length} bytes) images differ in size");

        var maps = new List<MapDiff>();
        foreach (var m in modSet.Maps)
        {
            if (stockSet.Get(m.Id) is not { } s) continue;
            if (s.Values.Length != m.Values.Length) continue;
            maps.Add(CompareMap(s, m));
        }

        var ranges = BinaryDiff.ChangedRanges(stock.Span, mod.Span);
        var known = modSet.Maps.SelectMany(KnownRanges).Concat(knownExtraRegions ?? []).ToList();
        var unmapped = new List<UnmappedChange>();
        foreach (var r in ranges)
        {
            if (known.Any(k => k.Overlaps(r.Start, r.Length))) continue;
            var section = sections.FirstOrDefault(s => r.Start >= s.Start && r.Start < s.End)?.Kind ?? SectionKind.Unknown;
            var nearest = modSet.Maps.OrderBy(mp => Math.Abs(mp.Definition.Address - r.Start)).FirstOrDefault()?.Id;
            unmapped.Add(new UnmappedChange(r.Start, r.Length, section, nearest));
        }

        return new DiffResult
        {
            Maps = maps.OrderByDescending(d => d.IsModified).ThenByDescending(d => Math.Abs(d.MeanDeltaPct)).ToList(),
            ChangedRanges = ranges,
            UnmappedChanges = unmapped,
            ChangedBytes = BinaryDiff.CountChangedBytes(stock.Span, mod.Span),
        };
    }

    public static IEnumerable<ByteRange> KnownRanges(CalibrationMap m)
    {
        var d = m.Definition;
        yield return new ByteRange(d.Address, d.ByteLength);
        if (d.XAxis?.Address is { } xa) yield return new ByteRange(xa, d.Cols * d.XAxis.DataType.Size());
        if (d.YAxis?.Address is { } ya) yield return new ByteRange(ya, d.Rows * d.YAxis.DataType.Size());
    }

    public static MapDiff CompareMap(CalibrationMap stock, CalibrationMap mod)
    {
        var n = mod.Values.Length;
        var changed = 0;
        double sumPct = 0, maxPct = double.MinValue, minPct = double.MaxValue, maxAbs = 0;
        double ratioSum = 0, ratioSq = 0;
        var ratioCount = 0;
        for (var i = 0; i < n; i++)
        {
            var a = stock.Values[i];
            var b = mod.Values[i];
            var d = b - a;
            if (Math.Abs(stock.Raw[i] - mod.Raw[i]) < Epsilon) continue;
            changed++;
            maxAbs = Math.Max(maxAbs, Math.Abs(d));
            if (Math.Abs(a) > Epsilon)
            {
                var pct = d / Math.Abs(a) * 100;
                sumPct += pct;
                maxPct = Math.Max(maxPct, pct);
                minPct = Math.Min(minPct, pct);
                var ratio = b / a;
                ratioSum += ratio;
                ratioSq += ratio * ratio;
                ratioCount++;
            }
        }
        double? meanRatio = ratioCount > 0 ? ratioSum / ratioCount : null;
        double? ratioStd = ratioCount > 1 ? Math.Sqrt(Math.Max(0, ratioSq / ratioCount - meanRatio!.Value * meanRatio.Value)) : ratioCount == 1 ? 0 : null;

        return new MapDiff
        {
            MapId = mod.Id,
            Name = mod.Definition.Name,
            Role = mod.Definition.Role,
            Category = mod.Definition.Category,
            Unit = mod.Definition.Unit,
            Rows = mod.Rows,
            Cols = mod.Cols,
            ChangedCells = changed,
            TotalCells = n,
            MeanDeltaPct = changed == 0 || ratioCount == 0 ? 0 : sumPct / ratioCount,
            MaxDeltaPct = changed == 0 || ratioCount == 0 ? 0 : maxPct,
            MinDeltaPct = changed == 0 || ratioCount == 0 ? 0 : minPct,
            MaxAbsDelta = maxAbs,
            StockMax = stock.Max,
            ModMax = mod.Max,
            XAxisChanged = !stock.XAxis.AsSpan().SequenceEqual(mod.XAxis),
            YAxisChanged = !stock.YAxis.AsSpan().SequenceEqual(mod.YAxis),
            MeanRatio = meanRatio,
            RatioStdDev = ratioStd,
            Stock = stock.Values,
            Modified = mod.Values,
            XAxis = mod.XAxis,
            YAxis = mod.YAxis,
            StockXAxis = stock.XAxis,
            StockYAxis = stock.YAxis,
        };
    }
}
