using System.Text.Json.Serialization;
using ECUStudio.Components;
using ECUStudio.Core;

namespace ECUStudio.Simulation.Logs;

[JsonConverter(typeof(JsonStringEnumConverter<LogAgreement>))]
public enum LogAgreement { Agrees, Deviates, Insufficient }

/// <summary>One RPM bin: median of logged WOT samples vs. the model at the bin centre.</summary>
public sealed record LogBin(double Rpm, int Samples, double Measured, double? Model, double? ModelLow, double? ModelHigh);

public sealed record ChannelValidation
{
    public required LogChannel Channel { get; init; }
    public required string Label { get; init; }
    public required string Unit { get; init; }
    public required IReadOnlyList<LogBin> Bins { get; init; }
    /// <summary>Median relative deviation (measured − model) / model over bins, %.</summary>
    public double? BiasPct { get; init; }
    /// <summary>Share of bins whose measured median lies inside the model range, %.</summary>
    public double? WithinRangePct { get; init; }
    public double TolerancePct { get; init; }
    public LogAgreement Status { get; init; }
    public string Note { get; init; } = "";
}

public sealed record LogPeaks(double? BoostMbar, double? MafMg, double? IqMg, double? EgtC, double? IntakeTempC);

public sealed record LogValidation
{
    public required string LogId { get; init; }
    public required string Name { get; init; }
    public required string Format { get; init; }
    public required bool AgainstStock { get; init; }
    public required int Samples { get; init; }
    public required int WotSamples { get; init; }
    public required string WotCriterion { get; init; }
    public required IReadOnlyList<ChannelValidation> Channels { get; init; }
    public required LogPeaks Peaks { get; init; }
    public required LogAgreement Status { get; init; }
    public double? AtmosphericPressureMbar { get; init; }
    public IReadOnlyList<LogColumn> Mapping { get; init; } = [];
    public IReadOnlyList<string> UnmappedHeaders { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string Explanation { get; init; } = "";
}

/// <summary>
/// Compares logged full-load data with the steady-state model. A log counts as supporting evidence
/// (and raises data availability) only when every comparable channel agrees within tolerance; a
/// deviation is reported, never averaged away. Transient samples are reduced by RPM-binned medians,
/// not removed: the model is steady-state, so spool-up lag shows as negative boost bias at low RPM.
/// </summary>
public static class LogValidator
{
    public const int MinWotSamples = 20;
    public const double BinWidthRpm = 250;

    private static readonly (LogChannel Channel, string Label, double TolPct, Func<PointResult, Estimate> Model)[] Comparable =
    [
        (LogChannel.BoostActual, "Boost pressure (actual)", 8, p => p.Map),
        (LogChannel.BoostSpecified, "Boost pressure (specified)", 5, p => p.BoostTarget),
        (LogChannel.MafActual, "Air mass (actual)", 10, p => p.AirMass),
        (LogChannel.IqActual, "Injection quantity", 10, p => p.Iq),
        (LogChannel.IqRequested, "Injection quantity (requested)", 10, p => p.IqRequested),
        (LogChannel.Egt, "EGT", 12, p => p.Egt),
    ];

    public static LogValidation Validate(string logId, DiagnosticLog log, SimulationInput input, ISimulationEngine engine, bool againstStock, int? cylinders)
    {
        var rpm = log[LogChannel.Rpm]!;
        var warnings = new List<string>(log.Warnings);
        var (wot, criterion) = SelectWot(log);
        var atm = log[LogChannel.AtmosphericPressure] is { } a ? LogParser.Median(wot.Select(i => a[i])) : double.NaN;
        var iat = log[LogChannel.IntakeTemp] is { } t ? LogParser.Median(Enumerable.Range(0, log.SampleCount).Select(i => t[i])) : double.NaN;
        var env = new EnvironmentConditions
        {
            AtmosphericPressureMbar = double.IsFinite(atm) && atm is > 600 and < 1100 ? atm : null,
            AmbientTempC = double.IsFinite(iat) && iat is > -30 and < 50 ? iat : 20,
        };
        if (!env.AtmosphericPressureMbar.HasValue) warnings.Add("No atmospheric pressure in log: model uses sea level (1013 mbar)");

        // Model once per RPM bin.
        var bins = wot.GroupBy(i => Math.Round(rpm[i] / BinWidthRpm) * BinWidthRpm).Where(g => g.Count() >= 2).OrderBy(g => g.Key).ToList();
        var model = bins.ToDictionary(g => g.Key, g => engine.Evaluate(input, new OperatingPoint(g.Key, 100), env));

        var channels = new List<ChannelValidation>();
        foreach (var (ch, label, tol, pick) in Comparable)
        {
            if (log[ch] is not { } values) continue;
            var column = log.Mapping.First(m => m.Channel == ch);
            values = ToMgPerStroke(values, column.Unit, rpm, cylinders, label, warnings);
            if (values is null) continue;
            var list = new List<LogBin>();
            foreach (var g in bins)
            {
                var measured = LogParser.Median(g.Select(i => values[i]));
                if (!double.IsFinite(measured)) continue;
                var e = pick(model[g.Key]);
                list.Add(new LogBin(g.Key, g.Count(), Math.Round(measured, 2), e.IsKnown ? Math.Round(e.Value!.Value, 2) : null,
                    e.IsKnown ? Math.Round(e.Low!.Value, 2) : null, e.IsKnown ? Math.Round(e.High!.Value, 2) : null));
            }
            channels.Add(Judge(ch, label, ch == LogChannel.Egt ? "°C" : column.Unit is "g/s" or "kg/h" ? "mg/stroke" : column.Unit, list, tol, wot.Count));
        }

        var comparable = channels.Where(c => c.Status != LogAgreement.Insufficient).ToList();
        var status = wot.Count < MinWotSamples || comparable.Count == 0 ? LogAgreement.Insufficient
            : comparable.Any(c => c.Status == LogAgreement.Deviates) ? LogAgreement.Deviates : LogAgreement.Agrees;
        var explanation = status switch
        {
            LogAgreement.Agrees => $"{comparable.Count} channel(s) agree with the model within tolerance at full load: the log is used as supporting evidence and raises data availability.",
            LogAgreement.Deviates => $"Model and log disagree on {string.Join(", ", comparable.Where(c => c.Status == LogAgreement.Deviates).Select(c => c.Label))}: data availability is NOT raised; estimates derived from those quantities are less reliable than their ranges suggest.",
            _ => wot.Count < MinWotSamples
                ? $"Only {wot.Count} full-load samples (need {MinWotSamples}); log a 3rd/4th gear pull from ~1500 rpm to the limiter."
                : "No channel in the log can be compared with the model.",
        };

        return new LogValidation
        {
            LogId = logId, Name = log.Name, Format = log.Format, AgainstStock = againstStock, Samples = log.SampleCount, WotSamples = wot.Count,
            WotCriterion = criterion, Channels = channels, Status = status, AtmosphericPressureMbar = env.AtmosphericPressureMbar,
            Peaks = Peaks(log, rpm, cylinders), Mapping = log.Mapping, UnmappedHeaders = log.UnmappedHeaders, Warnings = warnings, Explanation = explanation,
        };
    }

    private static ChannelValidation Judge(LogChannel ch, string label, string unit, List<LogBin> bins, double tol, int wot)
    {
        var withModel = bins.Where(b => b.Model is > 0).ToList();
        if (wot < MinWotSamples || withModel.Count < 3)
            return new ChannelValidation { Channel = ch, Label = label, Unit = unit, Bins = bins, TolerancePct = tol, Status = LogAgreement.Insufficient,
                Note = withModel.Count < 3 ? "Fewer than 3 RPM bins with a model value" : "Too few full-load samples" };
        var bias = LogParser.Median(withModel.Select(b => (b.Measured - b.Model!.Value) / b.Model!.Value * 100));
        // ECU-requested quantities (IQ, boost target) are exact model values: a "range" share would always read 0 %.
        var hasBand = withModel.Any(b => b.ModelHigh - b.ModelLow > 1e-6);
        double? within = hasBand ? withModel.Count(b => b.Measured >= b.ModelLow && b.Measured <= b.ModelHigh) * 100.0 / withModel.Count : null;
        var ok = Math.Abs(bias) <= tol;
        var worst = withModel.MaxBy(b => Math.Abs(b.Measured - b.Model!.Value))!;
        return new ChannelValidation
        {
            Channel = ch, Label = label, Unit = unit, Bins = bins, TolerancePct = tol,
            BiasPct = Math.Round(bias, 1), WithinRangePct = within is { } w ? Math.Round(w, 0) : null,
            Status = ok ? LogAgreement.Agrees : LogAgreement.Deviates,
            Note = $"Largest gap at {worst.Rpm:0} rpm: logged {worst.Measured:0.#} vs model {worst.Model:0.#} {unit}",
        };
    }

    /// <summary>Full load: pedal ≥ 95 % when logged, else IQ ≥ 90 % of the log's maximum. RPM 1200–4800.</summary>
    internal static (List<int> Indices, string Criterion) SelectWot(DiagnosticLog log)
    {
        var rpm = log[LogChannel.Rpm]!;
        bool InRange(int i) => rpm[i] is >= 1200 and <= 4800;
        if (log[LogChannel.Pedal] is { } pedal)
            return (Enumerable.Range(0, log.SampleCount).Where(i => InRange(i) && pedal[i] >= 95).ToList(), "pedal ≥ 95 %");
        var iq = log[LogChannel.IqActual] ?? log[LogChannel.IqRequested];
        if (iq is not null)
        {
            var max = iq.Where(double.IsFinite).DefaultIfEmpty(0).Max();
            return (Enumerable.Range(0, log.SampleCount).Where(i => InRange(i) && iq[i] >= 0.9 * max).ToList(), "IQ ≥ 90 % of logged maximum (no pedal channel)");
        }
        return ([], "no pedal or IQ channel: full load cannot be identified");
    }

    private static double[]? ToMgPerStroke(double[] v, string unit, double[] rpm, int? cylinders, string label, List<string> warnings)
    {
        if (unit is not ("g/s" or "kg/h")) return v;
        if (cylinders is not > 0) { warnings.Add($"{label}: {unit} needs the cylinder count, which is unknown; channel ignored"); return null; }
        var gps = unit == "kg/h" ? v.Select(x => x / 3.6).ToArray() : v;
        // 4-stroke: intake strokes per second = rpm / 60 × cylinders / 2.
        return gps.Select((x, i) => rpm[i] > 0 ? x * 1000 / (rpm[i] / 60.0 * cylinders.Value / 2) : double.NaN).ToArray();
    }

    private static LogPeaks Peaks(DiagnosticLog log, double[] rpm, int? cylinders)
    {
        static double? Max(double[]? a) => a is null ? null : a.Where(double.IsFinite).Select(x => (double?)x).DefaultIfEmpty(null).Max();
        var mafColumn = log.Mapping.FirstOrDefault(m => m.Channel == LogChannel.MafActual);
        var maf = mafColumn is null ? null : ToMgPerStroke(log[LogChannel.MafActual]!, mafColumn.Unit, rpm, cylinders, "MAF", []);
        return new LogPeaks(Max(log[LogChannel.BoostActual]), Max(maf), Max(log[LogChannel.IqActual]), Max(log[LogChannel.Egt]), Max(log[LogChannel.IntakeTemp]));
    }

    public static int? Cylinders(HardwareProfile hw) => hw.Engine.Get(P.Cylinders).Number is { } n && n > 0 ? (int)n : null;
}
