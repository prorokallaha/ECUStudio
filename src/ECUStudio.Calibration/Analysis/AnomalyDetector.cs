using ECUStudio.Binary;
using ECUStudio.Calibration.Model;
using ECUStudio.Core;

namespace ECUStudio.Calibration.Analysis;

/// <summary>Pattern-level anomalies in a stock/mod comparison. Pure rules, every finding carries evidence.</summary>
public static class AnomalyDetector
{
    private static readonly MapRole[] LimiterRoles =
        [MapRole.TorqueLimiter, MapRole.GearTorqueLimiter, MapRole.SmokeLimiter, MapRole.BoostLimiter, MapRole.Svbl,
         MapRole.EgtProtection, MapRole.TemperatureProtection, MapRole.RpmLimiter, MapRole.GearboxTorqueMonitor];

    public static List<Finding> Detect(DiffResult diff, CalibrationSet modSet)
    {
        var findings = new List<Finding>();
        var modified = diff.Modified.ToList();

        foreach (var d in modified)
        {
            var mapRef = new Evidence(EvidenceType.Diff, $"diff:{d.MapId}", $"{d.ChangedCells}/{d.TotalCells} cells changed, mean {d.MeanDeltaPct:+0.0;-0.0}%");
            var isLimiter = LimiterRoles.Contains(d.Role);

            // 1. Primitive percentage tuning: (almost) every cell scaled by the same factor.
            if (d.TotalCells >= 4 && d.MeanRatio is { } ratio && d.RatioStdDev is { } std && std < 0.006 && Math.Abs(ratio - 1) >= 0.03)
            {
                var whole = d.ChangedFraction >= 0.85;
                findings.Add(new Finding
                {
                    Code = whole ? "PERCENTAGE_TUNING" : "REGION_SCALING",
                    Text = whole
                        ? $"{d.Name}: whole map scaled uniformly by {(ratio - 1) * 100:+0.#;-0.#}% (primitive percentage tuning)"
                        : $"{d.Name}: {d.ChangedFraction:P0} of the map scaled uniformly by {(ratio - 1) * 100:+0.#;-0.#}%",
                    Severity = isLimiter || d.Role is MapRole.TorqueToIq ? Severity.Warning : Severity.Review,
                    Confidence = 0.9,
                    Evidence = [mapRef, new Evidence(EvidenceType.Rule, "rule:uniform_ratio", $"mod/stock ratio {ratio:0.000} ± {std:0.0000}")],
                    Assumptions = ["Uniform scaling ignores where the engine actually has air, thermal and mechanical margin"],
                    RelatedMaps = [d.MapId],
                    Source = FindingSource.Rule,
                });
            }

            // 2. Maxed limiter / disabled protection.
            if (isLimiter && modSet.Get(d.MapId) is { } map)
            {
                var maxRaw = map.Definition.DataType.MaxRaw();
                var atCeiling = map.Raw.Count(r => r >= maxRaw * 0.98);
                if (atCeiling > 0)
                {
                    findings.Add(new Finding
                    {
                        Code = "LIMITER_MAXED",
                        Text = $"{d.Name}: {atCeiling} cell(s) set to the datatype ceiling; the limit/protection is effectively disabled there",
                        Severity = Severity.Warning,
                        Confidence = 0.85,
                        Evidence = [mapRef, new Evidence(EvidenceType.Map, $"map:{d.MapId}", $"raw ≥ {maxRaw * 0.98:0} in {atCeiling} cells")],
                        AffectedComponents = AffectedBy(d.Role),
                        RelatedMaps = [d.MapId],
                    });
                }
            }

            // 3. Clipping / plateau introduced by the modification.
            var stockPlateau = PlateauCount(d.Stock);
            var modPlateau = PlateauCount(d.Modified);
            if (modPlateau >= Math.Max(3, d.TotalCells / 4) && modPlateau > stockPlateau * 2)
            {
                findings.Add(new Finding
                {
                    Code = "CLIPPING",
                    Text = $"{d.Name}: {modPlateau} cells sit on the same maximum value (clipped plateau); stock had {stockPlateau}",
                    Severity = Severity.Review,
                    Confidence = 0.75,
                    Evidence = [mapRef, new Evidence(EvidenceType.Map, $"map:{d.MapId}", $"max {d.ModMax:0.##} {d.Unit} repeated {modPlateau}×")],
                    RelatedMaps = [d.MapId],
                });
            }

            // 4. Flattened map.
            if (StdDev(d.Modified) < 1e-6 && StdDev(d.Stock) > 1e-6)
            {
                findings.Add(new Finding
                {
                    Code = "FLAT_MAP",
                    Text = $"{d.Name}: map flattened to a single value {d.Modified[0]:0.##} {d.Unit}",
                    Severity = isLimiter ? Severity.Warning : Severity.Review,
                    Confidence = 0.85,
                    Evidence = [mapRef],
                    AffectedComponents = AffectedBy(d.Role),
                    RelatedMaps = [d.MapId],
                });
            }

            // 5. Discontinuities (abrupt steps) that were not in stock.
            var stockStep = MaxSecondDifference(d.Stock, d.Rows, d.Cols);
            var modStep = MaxSecondDifference(d.Modified, d.Rows, d.Cols);
            var range = Math.Max(1e-9, d.Modified.Max() - d.Modified.Min());
            if (modStep > 4 * Math.Max(stockStep, 1e-9) && modStep / range > 0.25)
            {
                findings.Add(new Finding
                {
                    Code = "DISCONTINUITY",
                    Text = $"{d.Name}: abrupt step introduced (max 2nd difference {modStep:0.##} vs stock {stockStep:0.##} {d.Unit})",
                    Severity = Severity.Review,
                    Confidence = 0.7,
                    Evidence = [mapRef],
                    Assumptions = ["Steps cause torque/boost oscillation when the operating point crosses the breakpoint"],
                    RelatedMaps = [d.MapId],
                });
            }

            // 6. Axis rescaling changes interpolation everywhere.
            if (d.XAxisChanged || d.YAxisChanged)
            {
                findings.Add(new Finding
                {
                    Code = "AXIS_CHANGED",
                    Text = $"{d.Name}: {(d.XAxisChanged ? "X" : "")}{(d.XAxisChanged && d.YAxisChanged ? "/" : "")}{(d.YAxisChanged ? "Y" : "")} axis breakpoints changed; cell-by-cell comparison is only approximate",
                    Severity = Severity.Review,
                    Confidence = 0.9,
                    Evidence = [mapRef],
                    RelatedMaps = [d.MapId],
                });
            }
        }

        // 7. One map changed much more than the rest.
        if (modified.Count >= 3)
        {
            var deltas = modified.Select(m => Math.Abs(m.MeanDeltaPct)).ToArray();
            var mean = deltas.Average();
            var std = StdDev(deltas);
            foreach (var m in modified.Where(m => std > 0 && (Math.Abs(m.MeanDeltaPct) - mean) / std > 1.8 && Math.Abs(m.MeanDeltaPct) > 15))
            {
                findings.Add(new Finding
                {
                    Code = "OUTLIER_CHANGE",
                    Text = $"{m.Name} changed far more than other maps ({m.MeanDeltaPct:+0.#;-0.#}% vs average {mean:0.#}%)",
                    Severity = Severity.Review,
                    Confidence = 0.7,
                    Evidence = [new Evidence(EvidenceType.Diff, $"diff:{m.MapId}", $"mean change {m.MeanDeltaPct:+0.#;-0.#}%")],
                    RelatedMaps = [m.MapId],
                });
            }
        }

        // 8. Changes outside any known map: code patches, unidentified maps, DTC/limiter switches.
        foreach (var group in diff.UnmappedChanges.GroupBy(u => u.Section))
        {
            var list = group.ToList();
            var bytes = list.Sum(u => u.Length);
            var code = group.Key == SectionKind.Code;
            findings.Add(new Finding
            {
                Code = code ? "CODE_SECTION_CHANGED" : "UNMAPPED_CHANGES",
                Text = code
                    ? $"{list.Count} change(s), {bytes} bytes, in the estimated code area (possible patch: switch, DTC-off or limiter bypass)"
                    : $"{list.Count} change(s), {bytes} bytes, outside known maps in {group.Key} area",
                Severity = code ? Severity.Warning : Severity.Review,
                Confidence = code ? 0.5 : 0.6,
                Evidence = list.Take(5).Select(u => new Evidence(EvidenceType.Binary, $"0x{u.Start:X6}", $"{u.Length} bytes changed")).ToList(),
                Unknowns = ["Purpose of the changed bytes", code ? "Exact code/calibration boundary (estimated from content)" : "Whether these bytes belong to an unidentified map"],
            });
        }

        return findings;
    }

    internal static string[] AffectedBy(MapRole role) => role switch
    {
        MapRole.BoostLimiter or MapRole.Svbl or MapRole.BoostTarget or MapRole.VntDuty => ["turbo"],
        MapRole.SmokeLimiter or MapRole.EgtProtection => ["thermal", "turbo"],
        MapRole.TorqueLimiter or MapRole.GearTorqueLimiter or MapRole.GearboxTorqueMonitor => ["transmission", "clutch", "engine"],
        MapRole.TemperatureProtection => ["engine", "thermal"],
        MapRole.Duration or MapRole.TorqueToIq => ["injectors", "thermal"],
        _ => [],
    };

    private static int PlateauCount(double[] values)
    {
        var max = values.Max();
        var count = 0;
        foreach (var v in values) if (Math.Abs(v - max) < 1e-9) count++;
        return count;
    }

    private static double StdDev(double[] v)
    {
        if (v.Length < 2) return 0;
        var mean = v.Average();
        double s = 0;
        foreach (var x in v) s += (x - mean) * (x - mean);
        return Math.Sqrt(s / v.Length);
    }

    private static double MaxSecondDifference(double[] v, int rows, int cols)
    {
        double max = 0;
        for (var r = 0; r < rows; r++)
            for (var c = 1; c < cols - 1; c++)
                max = Math.Max(max, Math.Abs(v[r * cols + c + 1] - 2 * v[r * cols + c] + v[r * cols + c - 1]));
        for (var c = 0; c < cols; c++)
            for (var r = 1; r < rows - 1; r++)
                max = Math.Max(max, Math.Abs(v[(r + 1) * cols + c] - 2 * v[r * cols + c] + v[(r - 1) * cols + c]));
        return max;
    }
}
