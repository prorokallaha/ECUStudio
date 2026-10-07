using System.Text.Json.Serialization;
using ECUStudio.Calibration.Analysis;
using ECUStudio.Calibration.Model;
using ECUStudio.Components;
using ECUStudio.Core;
using ECUStudio.Simulation;

namespace ECUStudio.Risk;

[JsonConverter(typeof(JsonStringEnumConverter<LoadLevel>))]
public enum LoadLevel { Unknown, Low, Moderate, High, VeryHigh }

public sealed record ComponentMargin
{
    public required string Component { get; init; }
    public required string Label { get; init; }
    public required string Metric { get; init; }
    public required Estimate Load { get; init; }
    public Param Limit { get; init; } = Param.Unknown();
    public Estimate Utilization { get; init; } = Estimate.Unknown("%");
    public Estimate Margin { get; init; } = Estimate.Unknown("%");
    public double? ChangeVsStockPct { get; init; }
    public LoadLevel LoadLevel { get; init; }
    public Severity Severity { get; init; }
    public double Confidence { get; init; }
    /// <summary>False when the limit or load is too uncertain to show an exact percentage in the UI.</summary>
    public bool ShowExactUtilization { get; init; }
    public bool Critical { get; init; } = true;
    public string Explanation { get; init; } = "";
    public double[]? AffectedRpm { get; init; }
    public IReadOnlyList<string> RelatedMaps { get; init; } = [];
    public IReadOnlyList<string> MissingData { get; init; } = [];
    public IReadOnlyList<Evidence> Evidence { get; init; } = [];
}

public sealed record RiskReport
{
    public required IReadOnlyList<ComponentMargin> Components { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
    public required Severity Overall { get; init; }
    public required double Confidence { get; init; }
    public IReadOnlyList<string> CriticalUnknowns { get; init; } = [];
    public string OverallExplanation { get; init; } = "";
}

public sealed record RiskInput
{
    public required HardwareProfile Hardware { get; init; }
    public required GridResult Modified { get; init; }
    public GridResult? Stock { get; init; }
    public CalibrationSet? ModCalibration { get; init; }
    public IReadOnlyList<Finding> CalibrationFindings { get; init; } = [];
    public bool HasCommonRail { get; init; }
    public double ConfidenceCap { get; init; } = 1;
}

/// <summary>
/// Component margins and overall verdict. Limits come only from the hardware profile with their
/// source/confidence; an unknown limit yields UNKNOWN severity (never SAFE) plus a relative
/// load level against stock as supporting evidence.
/// </summary>
public static class RiskEngine
{
    public static RiskReport Evaluate(RiskInput input)
    {
        var hw = input.Hardware;
        var mod = AllWot(input.Modified);
        var stock = input.Stock is null ? [] : AllWot(input.Stock);
        var cal = input.ModCalibration;
        string[] Maps(params MapRole[] roles) => cal is null ? [] : roles.Select(r => cal.ByRole(r)?.Id).OfType<string>().ToArray();

        var components = new List<ComponentMargin>();

        // Drivetrain: peak crank torque against gearbox / clutch ratings.
        var torque = Peak(mod, p => p.Torque);
        var stockTorque = Peak(stock, p => p.Torque);
        var drivetrainMaps = Maps(MapRole.TorqueLimiter, MapRole.GearTorqueLimiter, MapRole.DriverWish, MapRole.TorqueToIq);
        components.Add(AgainstLimit("transmission", $"Transmission ({hw.Transmission.Name})", "Peak engine torque", torque, stockTorque,
            hw.Transmission.Get(P.RatedTorqueInputNm), drivetrainMaps, "Gearbox rated input torque"));
        if (!hw.Clutch.Id.Contains("dsg", StringComparison.OrdinalIgnoreCase))
            components.Add(AgainstLimit("clutch", $"Clutch ({hw.Clutch.Name})", "Peak engine torque", torque, stockTorque,
                hw.Clutch.Get(P.RatedTorqueInputNm), drivetrainMaps, "Clutch / DMF torque capacity"));

        // Engine internals: design limit is typically unpublished → relative to OEM rating.
        var engine = AgainstLimit("engine", $"Engine ({hw.Engine.Name})", "Peak engine torque", torque, stockTorque,
            hw.Engine.Get(P.MaxDesignTorqueNm), drivetrainMaps, "Engine design torque limit (pistons, rods, head gasket)");
        if (hw.Engine.Number(P.RatedTorqueNm) is { } rated && torque.load.Value is { } tv)
        {
            var over = (tv / rated - 1) * 100;
            engine = engine with
            {
                Explanation = engine.Explanation + $" Estimated peak torque is {over:+0;-0}% vs OEM rated {rated:0} Nm.",
                Evidence = [.. engine.Evidence, new Evidence(EvidenceType.Component, "component:engine.rated_torque_nm", $"OEM rated {rated:0} Nm")],
                ChangeVsStockPct = engine.ChangeVsStockPct ?? over,
                LoadLevel = engine.LoadLevel == LoadLevel.Unknown ? Level(over) : engine.LoadLevel,
            };
        }
        components.Add(engine);

        // Turbo: pressure ratio, corrected flow, turbine inlet temperature (altitude scenarios included).
        var pr = Peak(mod, p => p.PressureRatio);
        var flow = Peak(mod, p => p.CorrectedAirFlow);
        var egt = Peak(mod, p => p.Egt);
        var turboMaps = Maps(MapRole.BoostTarget, MapRole.BoostLimiter, MapRole.Svbl, MapRole.VntDuty, MapRole.SmokeLimiter);
        var turboPr = AgainstLimit("turbo", $"Turbo ({hw.Turbo.Name})", "Peak compressor pressure ratio", pr, Peak(stock, p => p.PressureRatio),
            hw.Turbo.Get(P.MaxPressureRatio), turboMaps, "Compressor map (surge/choke/overspeed lines)");
        var flowChange = Change(flow, Peak(stock, p => p.CorrectedAirFlow));
        var egtTurbine = hw.Turbo.Get(P.MaxTurbineInletTempC);
        turboPr = turboPr with
        {
            Explanation = turboPr.Explanation + (flowChange is { } fc ? $" Corrected airflow {fc:+0;-0}% vs stock." : "")
                + (egt.load.Value is { } e ? $" Estimated turbine inlet temperature up to ~{EngineeringRounding.Round(e, "°C")} °C (limit {(egtTurbine.IsKnown ? egtTurbine.Number + " °C" : "UNKNOWN")})." : ""),
            MissingData = [.. turboPr.MissingData, "Turbo shaft speed (no speed sensor/compressor map)"],
        };
        components.Add(turboPr);

        // Injectors: delivery and duration.
        var iq = Peak(mod, p => p.Iq);
        var injMaps = Maps(MapRole.TorqueToIq, MapRole.SmokeLimiter, MapRole.Duration, MapRole.Soi);
        var inj = AgainstLimit("injectors", $"Injectors ({hw.Injectors.Name})", "Peak injected quantity", iq, Peak(stock, p => p.Iq),
            hw.Injectors.Get(P.MaxDeliveryMg), injMaps, "Nozzle flow rating / maximum delivery");
        var dur = Peak(mod, p => p.Duration);
        if (dur.load.Value is { } dv)
            inj = inj with { Explanation = inj.Explanation + $" Peak duration {dv:0.#} {dur.load.Unit} (limit {(hw.Injectors.Get(P.MaxInjectionDurationDegCa).IsKnown ? hw.Injectors.Number(P.MaxInjectionDurationDegCa) + " °CA" : "UNKNOWN")})." };
        components.Add(inj);

        // Fuel system: rail or (PD) not applicable.
        if (input.HasCommonRail)
            components.Add(AgainstLimit("fuel_system", $"Fuel system ({hw.FuelSystem.Name})", "Peak rail pressure", Peak(mod, p => p.RailPressure), Peak(stock, p => p.RailPressure),
                hw.FuelSystem.Get(P.MaxRailPressureBar), Maps(MapRole.RailPressure), "HPFP / rail pressure rating"));
        else
            components.Add(new ComponentMargin
            {
                Component = "fuel_system", Label = $"Fuel system ({hw.FuelSystem.Name})", Metric = "Rail pressure",
                Load = Estimate.NotApplicable("bar"), Severity = Severity.Safe, Confidence = 0.9, Critical = false, LoadLevel = LoadLevel.Unknown,
                Explanation = "Unit-injector system: no rail/HPFP. Injection pressure is cam-driven; injector load is assessed separately.",
            });

        // Thermal: EGT and lambda (physics thresholds, not component ratings).
        components.Add(Thermal(mod, stock, hw, Maps(MapRole.SmokeLimiter, MapRole.Soi, MapRole.Duration, MapRole.BoostTarget), input.ConfidenceCap));

        // Findings from component analysis
        var findings = new List<Finding>(input.CalibrationFindings);
        foreach (var c in components.Where(c => c.Severity is Severity.Warning or Severity.Danger or Severity.Review && c.Load.IsKnown))
        {
            findings.Add(new Finding
            {
                Code = $"MARGIN_{c.Component.ToUpperInvariant()}",
                Text = $"{c.Label}: {c.Explanation}".Trim(),
                Severity = c.Severity,
                Confidence = c.Confidence,
                Evidence = c.Evidence,
                AffectedComponents = [c.Component],
                RelatedMaps = c.RelatedMaps,
                Unknowns = c.MissingData,
                Source = FindingSource.Risk,
                RpmRange = c.AffectedRpm,
            });
        }

        var critical = components.Where(c => c.Critical).ToList();
        var criticalUnknowns = critical.Where(c => c.Severity == Severity.Unknown).Select(c => $"{c.Label}: {string.Join("; ", c.MissingData.DefaultIfEmpty("limit unknown"))}").ToList();
        var overall = SeverityExtensions.Worst(components.Select(c => c.Severity).Concat(findings.Select(f => f.Severity)));
        // SAFE is impossible while a critical component is UNKNOWN.
        if (overall == Severity.Safe && criticalUnknowns.Count > 0) overall = Severity.Unknown;
        var conf = critical.Count == 0 ? 0 : Math.Min(input.ConfidenceCap, critical.Average(c => c.Confidence));

        var explanation = overall switch
        {
            Severity.Danger or Severity.Warning => $"Worst: {string.Join(", ", components.Where(c => c.Severity == overall).Select(c => c.Label).Concat(findings.Where(f => f.Severity == overall && f.Source != FindingSource.Risk).Select(f => f.Code)).Distinct())}.",
            Severity.Unknown => $"{criticalUnknowns.Count} critical component(s) without a reliable limit; risk cannot be reliably determined for them.",
            _ => "All critical components within known limits.",
        };

        return new RiskReport { Components = components, Findings = findings, Overall = overall, Confidence = Math.Round(conf, 2), CriticalUnknowns = criticalUnknowns, OverallExplanation = explanation };
    }

    private static List<PointResult> AllWot(GridResult g) =>
        g.ReferencePoints.Where(p => p.Point.PedalPct >= 99).Concat(g.Scenarios.SelectMany(s => s.WotLine)).Where(p => !p.BeyondCalibratedRange).ToList();

    private static (Estimate load, double rpm, PointResult? at) Peak(List<PointResult> points, Func<PointResult, Estimate> f)
    {
        PointResult? best = null;
        Estimate bestE = Estimate.Unknown();
        foreach (var p in points)
        {
            var e = f(p);
            if (e.Kind == ValueKind.NotApplicable) return (e, 0, null);
            if (!e.IsKnown) continue;
            if (!bestE.IsKnown || e.Value > bestE.Value) { bestE = e; best = p; }
        }
        return (bestE, best?.Point.Rpm ?? 0, best);
    }

    private static double? Change((Estimate load, double rpm, PointResult? at) mod, (Estimate load, double rpm, PointResult? at) stock) =>
        mod.load.Value is { } m && stock.load.Value is { } s && s > 0 ? (m / s - 1) * 100 : null;

    private static LoadLevel Level(double? changePct) => changePct switch
    {
        null => LoadLevel.Unknown,
        <= 5 => LoadLevel.Low,
        <= 15 => LoadLevel.Moderate,
        <= 30 => LoadLevel.High,
        _ => LoadLevel.VeryHigh,
    };

    private static double[]? AffectedBand(List<PointResult> points, Func<PointResult, Estimate> f, double threshold)
    {
        var rpms = points.Where(p => f(p).Value is { } v && v >= threshold).Select(p => p.Point.Rpm).ToList();
        return rpms.Count == 0 ? null : [rpms.Min(), rpms.Max()];
    }

    /// <summary>Operating conditions of a worst-case point, so a peak from an altitude scenario is not read as sea level.</summary>
    internal static string Conditions(PointResult? at, double rpm) => at is null
        ? $"{rpm:0} rpm"
        : $"{at.Point.Rpm:0} rpm, {at.Environment.AltitudeM:0} m, {at.Environment.AmbientTempC:0} °C";

    private static ComponentMargin AgainstLimit(string id, string label, string metric,
        (Estimate load, double rpm, PointResult? at) peak, (Estimate load, double rpm, PointResult? at) stockPeak,
        Param limit, string[] maps, string missingLimitName)
    {
        var load = peak.load;
        var change = Change(peak, stockPeak);
        var evidence = new List<Evidence>();
        if (load.IsKnown) evidence.Add(new Evidence(EvidenceType.Simulation, $"simulation:wot.{id}", $"{metric} {load} at {Conditions(peak.at, peak.rpm)}"));
        if (change is { } c) evidence.Add(new Evidence(EvidenceType.Simulation, $"simulation:stock_vs_mod.{id}", $"{c:+0.#;-0.#}% vs stock"));

        if (!load.IsKnown)
            return new ComponentMargin
            {
                Component = id, Label = label, Metric = metric, Load = load, Limit = limit, Severity = Severity.Unknown, Confidence = 0,
                RelatedMaps = maps, MissingData = ["Load could not be estimated (required maps not identified)"], Explanation = "Load unknown.",
            };

        if (!limit.IsKnown || limit.Number is not { } lim || lim <= 0)
        {
            var level = Level(change);
            return new ComponentMargin
            {
                Component = id, Label = label, Metric = metric, Load = load, Limit = limit, ChangeVsStockPct = change, LoadLevel = level,
                Severity = Severity.Unknown, Confidence = Math.Round(load.Confidence * 0.5, 2), ShowExactUtilization = false,
                Explanation = $"Known limit: UNKNOWN. Estimated {metric.ToLowerInvariant()} {load} (worst case at {Conditions(peak.at, peak.rpm)}).{(change is { } ch ? $" {ch:+0;-0}% vs stock." : "")} Risk cannot be reliably determined.",
                RelatedMaps = maps, MissingData = [missingLimitName], Evidence = evidence,
            };
        }

        var u = load.Value!.Value / lim;
        var uLow = load.Low!.Value / lim;
        var uHigh = load.High!.Value / lim;
        var severity = u switch { <= 0.85 => Severity.Safe, <= 0.95 => Severity.Review, <= 1.05 => Severity.Warning, _ => Severity.Danger };
        if (severity == Severity.Safe && (uHigh > 0.95 || limit.Confidence < 0.6 || load.Confidence < 0.3)) severity = Severity.Review;
        var conf = Math.Round(Math.Min(load.Confidence, limit.Confidence), 2);
        evidence.Add(new Evidence(EvidenceType.Component, $"component:{id}.limit", $"limit {lim:0.##} {limit.Unit} ({limit.Source}, confidence {limit.Confidence:0.00})"));
        var marginAbs = lim - load.Value.Value;
        return new ComponentMargin
        {
            Component = id, Label = label, Metric = metric, Load = load, Limit = limit,
            Utilization = Estimate.Of(u * 100, uLow * 100, uHigh * 100, "%", conf),
            Margin = Estimate.Of((1 - u) * 100, (1 - uHigh) * 100, (1 - uLow) * 100, "%", conf),
            ChangeVsStockPct = change, LoadLevel = Level(change), Severity = severity, Confidence = conf,
            ShowExactUtilization = conf >= 0.4 && limit.Confidence >= 0.6,
            Explanation = $"Estimated {metric.ToLowerInvariant()} {load} (worst case at {Conditions(peak.at, peak.rpm)}) vs known rating ~{lim:0} {limit.Unit}: margin {marginAbs:+0;-0} {limit.Unit} ({(1 - u) * 100:+0;-0}%).",
            AffectedRpm = peak.at is null ? null : [peak.rpm, peak.rpm],
            RelatedMaps = maps, Evidence = evidence,
            MissingData = limit.Confidence < 0.7 ? [$"{missingLimitName}: rating confidence {limit.Confidence:0.00} ({limit.Source})"] : [],
        };
    }

    private static ComponentMargin Thermal(List<PointResult> mod, List<PointResult> stock, HardwareProfile hw, string[] maps, double cap)
    {
        var egt = Peak(mod, p => p.Egt);
        var minLambda = mod.Where(p => p.Lambda.IsKnown).MinBy(p => p.Lambda.Value!.Value);
        var stockMinLambda = stock.Where(p => p.Lambda.IsKnown).MinBy(p => p.Lambda.Value!.Value);
        var limit = hw.Turbo.Get(P.MaxTurbineInletTempC);
        var evidence = new List<Evidence>();
        var missing = new List<string>();
        if (egt.load.IsKnown) evidence.Add(new Evidence(EvidenceType.Simulation, "simulation:wot.egt", $"EGT {egt.load} at {egt.rpm:0} rpm"));
        if (minLambda is not null) evidence.Add(new Evidence(EvidenceType.Simulation, "simulation:wot.lambda_min", $"λ min {minLambda.Lambda.Value:0.00} at {minLambda.Point.Rpm:0} rpm ({minLambda.Environment.AmbientTempC:0} °C, {minLambda.Environment.AltitudeM:0} m)"));

        // λ thresholds are combustion physics for diesel (smoke/soot rise steeply towards stoichiometric), not part ratings.
        var severity = Severity.Unknown;
        var text = "";
        if (minLambda?.Lambda.Value is { } lam)
        {
            severity = lam < 1.05 ? Severity.Warning : lam < 1.15 ? Severity.Review : Severity.Unknown;
            text = $"Minimum λ ≈ {lam:0.00}{(stockMinLambda?.Lambda.Value is { } sl ? $" (stock {sl:0.00})" : "")}. ";
            if (lam < 1.15) text += "Near-stoichiometric diesel combustion: heavy smoke, high EGT. ";
        }
        if (egt.load.Value is { } e)
        {
            text += $"Estimated pre-turbine EGT up to ~{EngineeringRounding.Round(e, "°C")} °C. ";
            if (limit.IsKnown && limit.Number is { } lim)
            {
                var sev = e > lim ? Severity.Danger : e > lim * 0.95 ? Severity.Warning : Severity.Safe;
                if (sev.Rank() > severity.Rank() || severity == Severity.Unknown) severity = sev;
            }
            else
            {
                missing.Add("Turbine inlet temperature limit");
                text += "EGT limit: UNKNOWN. ";
            }
        }
        missing.Add("Measured EGT (no EGT log)");
        var conf = Math.Min(cap, egt.load.IsKnown ? 0.35 : 0);
        return new ComponentMargin
        {
            Component = "thermal", Label = "Thermal (EGT / lambda)", Metric = "Peak EGT / min λ", Load = egt.load, Limit = limit,
            ChangeVsStockPct = Change(egt, Peak(stock, p => p.Egt)), LoadLevel = Level(Change(egt, Peak(stock, p => p.Egt))),
            Severity = severity, Confidence = Math.Round(conf, 2), ShowExactUtilization = false, Explanation = text.Trim(),
            AffectedRpm = AffectedBand(mod, p => p.Egt, (egt.load.Value ?? double.MaxValue) * 0.95), RelatedMaps = maps, MissingData = missing, Evidence = evidence,
        };
    }
}
