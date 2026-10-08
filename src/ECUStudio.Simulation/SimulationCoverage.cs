using System.Text.Json.Serialization;
using ECUStudio.Calibration.Model;
using ECUStudio.Components;

namespace ECUStudio.Simulation;

[JsonConverter(typeof(JsonStringEnumConverter<CoverageStatus>))]
public enum CoverageStatus
{
    /// <summary>Every prerequisite is present.</summary>
    Computed,
    /// <summary>Computed from a subset of the possible sources (e.g. torque from one model of four).</summary>
    Partial,
    /// <summary>A prerequisite is missing: the output is UNKNOWN.</summary>
    Unknown,
    NotApplicable,
}

[JsonConverter(typeof(JsonStringEnumConverter<PrerequisiteKind>))]
public enum PrerequisiteKind { Map, Hardware, StockFile }

/// <summary>A missing input. <see cref="Id"/> is a map role, a hardware parameter or "stock_file".</summary>
public sealed record Prerequisite(PrerequisiteKind Kind, string Id, string Label);

/// <summary>
/// One simulation output, whether it can be computed, from what, and which inputs are missing.
/// Ids match <see cref="PointResult"/> property names in camelCase.
/// </summary>
public sealed record OutputCoverage(string Output, CoverageStatus Status, IReadOnlyList<string> DependsOn, IReadOnlyList<Prerequisite> Missing, string? Note = null);

/// <summary>
/// Dependency-aware coverage of the simulation for one input: a missing map or hardware value makes only the
/// outputs that depend on it UNKNOWN, and lists exactly what is missing. Mirrors the data flow of
/// <see cref="SimulationEngine.Evaluate"/>.
/// </summary>
public static class SimulationCoverage
{
    public static IReadOnlyList<OutputCoverage> Evaluate(SimulationInput input)
    {
        var cal = input.Calibration;
        var hw = input.Hardware;
        Prerequisite? MapNeed(MapRole r) => cal.ByRole(r) is null ? new(PrerequisiteKind.Map, r.ToString(), r.DisplayName()) : null;
        var engine = new List<Prerequisite>();
        if (hw.Engine.Number(P.DisplacementCc) is not > 0) engine.Add(new(PrerequisiteKind.Hardware, P.DisplacementCc, "Engine displacement"));
        if (hw.Engine.Number(P.Cylinders) is not > 0) engine.Add(new(PrerequisiteKind.Hardware, P.Cylinders, "Cylinder count"));

        var results = new Dictionary<string, OutputCoverage>(StringComparer.Ordinal);
        OutputCoverage Add(string output, IReadOnlyList<string> deps, IEnumerable<Prerequisite?> own, string? note = null, CoverageStatus? force = null)
        {
            var missing = own.OfType<Prerequisite>().ToList();
            foreach (var d in deps) missing.AddRange(results[d].Missing);
            missing = missing.DistinctBy(m => (m.Kind, m.Id)).ToList();
            var status = force ?? (missing.Count == 0 ? CoverageStatus.Computed : CoverageStatus.Unknown);
            return results[output] = new OutputCoverage(output, status, deps, missing, note);
        }

        Add("requestedTorque", [], [MapNeed(MapRole.DriverWish)]);
        // Permitted torque needs at least one of driver wish / torque limiter.
        var permittedMissing = cal.ByRole(MapRole.DriverWish) is null && cal.ByRole(MapRole.TorqueLimiter) is null
            ? new[] { MapNeed(MapRole.DriverWish), MapNeed(MapRole.TorqueLimiter) } : [];
        Add("permittedTorque", [], permittedMissing);
        Add("iqRequested", ["permittedTorque"], [MapNeed(MapRole.TorqueToIq)]);
        Add("iq", ["iqRequested"], [], cal.ByRole(MapRole.SmokeLimiter) is null ? "Smoke limiter not identified: IQ is not limited by air mass" : null);
        Add("boostTarget", ["iqRequested"], [MapNeed(MapRole.BoostTarget)]);
        Add("map", ["boostTarget"], [], cal.ByRole(MapRole.BoostLimiter) is null ? "Boost limiter not identified" : null);
        foreach (var o in new[] { "pressureRatio", "compressorOutletTemp", "intakeManifoldTemp", "airMass", "airFlow", "correctedAirFlow" })
            Add(o, ["map"], engine);
        Add("lambda", ["iq", "airMass"], []);
        Add("soi", ["iq"], [MapNeed(MapRole.Soi)]);
        Add("duration", ["iq"], [MapNeed(MapRole.Duration)]);
        if (input.HasCommonRail) Add("railPressure", ["iq"], [MapNeed(MapRole.RailPressure)]);
        else Add("railPressure", [], [], "Unit-injector engine: no common rail", CoverageStatus.NotApplicable);

        // Torque: ensemble of models A–D; available when at least one is.
        var iqOk = results["iq"].Missing.Count == 0;
        var models = new List<(string Name, bool Ok, List<Prerequisite> Missing)>
        {
            ("A", iqOk && cal.ByRole(MapRole.TorqueToIq) is not null, [.. results["iq"].Missing]),
            ("B", iqOk && engine.Count == 0, [.. results["iq"].Missing, .. engine]),
            ("C", results["airMass"].Missing.Count == 0, [.. results["airMass"].Missing]),
        };
        var dMissing = new List<Prerequisite>(results["iq"].Missing);
        if (hw.Engine.Number(P.RatedTorqueNm) is null) dMissing.Add(new(PrerequisiteKind.Hardware, P.RatedTorqueNm, "Rated torque"));
        if (hw.Engine.Number(P.RatedTorqueRpm) is null) dMissing.Add(new(PrerequisiteKind.Hardware, P.RatedTorqueRpm, "Rated torque rpm"));
        if (input.StockReference is null) dMissing.Add(new(PrerequisiteKind.StockFile, "stock_file", "Stock file of the same software"));
        dMissing.AddRange(engine);
        models.Add(("D", dMissing.Count == 0, dMissing));
        var available = models.Where(m => m.Ok).Select(m => m.Name).ToList();
        var torqueStatus = available.Count == 0 ? CoverageStatus.Unknown : available.Count < models.Count ? CoverageStatus.Partial : CoverageStatus.Computed;
        var torqueMissing = available.Count == 0 ? models.SelectMany(m => m.Missing).DistinctBy(m => (m.Kind, m.Id)).ToList()
            : models.Where(m => !m.Ok).SelectMany(m => m.Missing).DistinctBy(m => (m.Kind, m.Id)).ToList();
        results["torque"] = new OutputCoverage("torque", torqueStatus, ["iq", "airMass"], torqueMissing,
            available.Count == 0 ? "No torque model has its inputs" : $"Models available: {string.Join(", ", available)} of A–D");
        foreach (var o in new[] { "powerKw", "powerHp" })
            results[o] = results["torque"] with { Output = o, DependsOn = ["torque"], Note = null };
        Add("egt", ["lambda", "intakeManifoldTemp"], []);
        return results.Values.ToList();
    }
}
