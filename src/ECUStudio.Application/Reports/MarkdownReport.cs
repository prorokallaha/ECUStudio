using System.Globalization;
using System.Text;
using ECUStudio.Application.Analysis;
using ECUStudio.Core;

namespace ECUStudio.Application.Reports;

/// <summary>Professional text report (CLI output and "Export report"). Every number keeps its range and confidence.</summary>
public static class MarkdownReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Render(AnalysisReport r)
    {
        var sb = new StringBuilder();
        var p = r.Vehicle.Profile;
        sb.AppendLine("# ECUStudio analysis report").AppendLine();
        sb.AppendLine($"> {r.Disclaimer}").AppendLine();
        sb.AppendLine("| | |").AppendLine("|---|---|");
        Row(sb, "Vehicle", $"{P(p.Make)} {P(p.Model)} {P(p.ModelYear)}");
        Row(sb, "VIN", P(p.Vin));
        Row(sb, "Engine", P(p.EngineCode));
        Row(sb, "ECU", $"{r.Ecu.EcuFamily} (detection {r.Detection.Score:0.00})");
        Row(sb, "HW / SW", $"{P(r.Ecu.HardwareNumber)} / {P(r.Ecu.SoftwareNumber)}");
        Row(sb, "Modified file", $"{r.ModifiedName} `{r.ModifiedSha256[..12]}`");
        Row(sb, "Stock file", r.StockName is null ? "not provided" : $"{r.StockName} `{r.StockSha256?[..12]}`");
        Row(sb, "Definitions", r.DefinitionSource);
        Row(sb, "Checksums", $"{r.Checksums.Overall} — {r.Checksums.Note}");
        Row(sb, "Data availability", $"{r.DataAvailability.Level} ({r.DataAvailability.Score:0.00})");
        Row(sb, "Overall risk", $"**{r.Risk.Overall.ToWire()}** (confidence {r.Risk.Confidence:0.00})");
        Row(sb, "Analysis version", r.AnalysisVersion);
        sb.AppendLine();

        sb.AppendLine("## Key metrics").AppendLine();
        sb.AppendLine("| Metric | Stock | Modified | Note |").AppendLine("|---|---|---|---|");
        foreach (var m in r.KeyMetrics)
            sb.AppendLine($"| {m.Label} | {E(m.Stock)} | {E(m.Modified)} | {m.Detail} |");
        sb.AppendLine();

        sb.AppendLine("## Main findings").AppendLine();
        foreach (var f in r.MainFindings) sb.AppendLine($"- **{f.Severity.ToWire()}** {f.Text} `{f.Code}`");
        sb.AppendLine();

        if (r.ModifiedMaps.Count > 0)
        {
            sb.AppendLine("## Modified maps").AppendLine();
            sb.AppendLine("| Map | Changed cells | Mean Δ | Max Δ | Stock max | Mod max |").AppendLine("|---|---|---|---|---|---|");
            foreach (var d in r.ModifiedMaps)
                sb.AppendLine($"| {d.Name} | {d.ChangedCells}/{d.TotalCells} | {d.MeanDeltaPct:+0.0;-0.0}% | {d.MaxDeltaPct:+0.0;-0.0}% | {N(d.StockMax)} {d.Unit} | {N(d.ModMax)} {d.Unit} |");
            if (r.UnmappedChanges.Count > 0) sb.AppendLine().AppendLine($"Changes outside known maps: {r.UnmappedChanges.Count} region(s).");
            sb.AppendLine();
        }

        sb.AppendLine("## Component margins").AppendLine();
        sb.AppendLine("| Component | Metric | Load | Limit | Utilization | Severity | Confidence |").AppendLine("|---|---|---|---|---|---|---|");
        foreach (var c in r.Risk.Components)
        {
            var util = c.ShowExactUtilization ? E(c.Utilization) : $"{c.LoadLevel} (no exact % — limit or load uncertain)";
            sb.AppendLine($"| {c.Label} | {c.Metric} | {E(c.Load)} | {P(c.Limit)} | {util} | {c.Severity.ToWire()} | {c.Confidence:0.00} |");
        }
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(r.Risk.OverallExplanation)) sb.AppendLine(r.Risk.OverallExplanation).AppendLine();

        sb.AppendLine("## Calibration findings").AppendLine();
        foreach (var f in r.CalibrationFindings.Concat(r.Risk.Findings).OrderByDescending(f => f.Severity.Rank()))
        {
            sb.AppendLine($"- **{f.Severity.ToWire()}** `{f.Code}` {f.Text} (confidence {f.Confidence:0.00})");
            if (f.Evidence.Count > 0) sb.AppendLine($"  - evidence: {string.Join("; ", f.Evidence.Select(e => e.Ref))}");
        }
        sb.AppendLine();

        sb.AppendLine("## Simulated full-load curve (modified)").AppendLine();
        sb.AppendLine("| RPM | Torque | Power | Boost | IQ | λ | EGT | Limiter |").AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var w in r.Simulation.ModifiedWot)
            sb.AppendLine($"| {w.Rpm:0} | {E(w.TorqueNm)} | {E(w.PowerHp)} | {E(w.BoostMbar)} | {E(w.IqMg)} | {E(w.Lambda)} | {E(w.EgtC)} | {w.TorqueLimiter}/{w.FuelLimiter} |");
        sb.AppendLine();

        if (r.Simulation.Scenarios.Count > 0)
        {
            sb.AppendLine("## Scenarios").AppendLine();
            sb.AppendLine("| Scenario | Peak power | Peak torque | Max EGT | Min λ |").AppendLine("|---|---|---|---|---|");
            foreach (var s in r.Simulation.Scenarios)
                sb.AppendLine($"| {s.Label} | {E(s.PeakPowerHp)} | {E(s.PeakTorqueNm)} | {E(s.MaxEgtC)} | {E(s.MinLambda)} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Unknowns and what to check").AppendLine();
        foreach (var u in r.Unknowns.Concat(r.Risk.CriticalUnknowns).Distinct()) sb.AppendLine($"- {u}");
        sb.AppendLine();
        sb.AppendLine("## Model assumptions").AppendLine();
        foreach (var a in r.Simulation.Assumptions) sb.AppendLine($"- {a}");
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string k, string v) => sb.AppendLine($"| {k} | {v.Replace("|", "/")} |");

    public static string E(Estimate? e)
    {
        if (e is null) return "—";
        var d = e.Display();
        if (d.Low is null) return d.Value;
        return d.Low.Value.Equals(d.High) ? $"{d.Value} {e.Unit}" : $"{d.Value} {e.Unit} ({N(d.Low.Value)}–{N(d.High!.Value)})";
    }

    public static string P(Param p) =>
        !p.IsKnown ? "UNKNOWN" : p.Text ?? (p.Number is { } n ? $"{N(n)} {(p.Unit == "-" ? "" : p.Unit)}".Trim() : "UNKNOWN");

    private static string N(double v) => v.ToString(Math.Abs(v) >= 100 ? "0" : "0.##", Inv);
}
