using ECUStudio.Calibration.Analysis;
using ECUStudio.Calibration.Model;
using ECUStudio.Core;
using ECUStudio.Simulation;

namespace ECUStudio.Risk;

public sealed record MapExplanation
{
    public required string MapId { get; init; }
    public required string Name { get; init; }
    public required string WhatChanged { get; init; }
    public required string Purpose { get; init; }
    public required IReadOnlyList<string> LinkedTo { get; init; }
    public required string PhysicalEffect { get; init; }
    public required IReadOnlyList<string> AffectedComponents { get; init; }
    public required string Assessment { get; init; }
    public required Severity Severity { get; init; }
    public required IReadOnlyList<string> DataToIncreaseConfidence { get; init; }
}

/// <summary>Explain mode: one human-readable explanation per modified map, built from computed facts only.</summary>
public static class Explainer
{
    public static List<MapExplanation> Explain(DiffResult diff, DependencyGraph graph, RiskReport risk, GridResult mod, GridResult? stock)
    {
        var result = new List<MapExplanation>();
        foreach (var d in diff.Modified)
        {
            var node = graph.Nodes.FirstOrDefault(n => n.MapId == d.MapId);
            var linked = node is null ? new List<string>() : graph.Edges
                .Where(e => e.From == node.Id || e.To == node.Id)
                .Select(e => e.From == node.Id ? e.To : e.From)
                .Select(id => graph.Nodes.First(n => n.Id == id).Label).Distinct().ToList();
            var comps = risk.Components.Where(c => c.RelatedMaps.Contains(d.MapId)).ToList();
            var severity = SeverityExtensions.Worst(comps.Select(c => c.Severity).Concat(risk.Findings.Where(f => f.RelatedMaps.Contains(d.MapId)).Select(f => f.Severity)));
            var data = new List<string>();
            if (d.Role == MapRole.Unknown) data.Add("Map definition (DAMOS/A2L/XDF) to confirm purpose");
            data.AddRange(comps.SelectMany(c => c.MissingData));
            data.Add("Diagnostic log (MAF, boost actual vs target, IQ) at WOT");

            result.Add(new MapExplanation
            {
                MapId = d.MapId,
                Name = d.Name,
                WhatChanged = $"{d.ChangedCells}/{d.TotalCells} cells; max {d.StockMax:0.##}→{d.ModMax:0.##} {d.Unit} ({d.MaxIncreasePct:+0.#;-0.#}%), mean {d.MeanDeltaPct:+0.#;-0.#}%",
                Purpose = d.Role.WhyItMatters(),
                LinkedTo = linked,
                PhysicalEffect = Effect(d.Role, mod, stock),
                AffectedComponents = comps.Select(c => c.Label).ToList(),
                Assessment = comps.Count == 0
                    ? "No component load directly attributed."
                    : string.Join(" ", comps.Select(c => $"{c.Label}: {c.Severity.ToWire()}{(c.Severity == Severity.Unknown ? " (limit unknown)" : "")}.")),
                Severity = severity,
                DataToIncreaseConfidence = data.Distinct().ToList(),
            });
        }
        return result;
    }

    private static string Effect(MapRole role, GridResult mod, GridResult? stock)
    {
        static PointResult? At(GridResult? g, double rpm) => g?.ReferencePoints.Where(p => p.Point.PedalPct >= 99).MinBy(p => Math.Abs(p.Point.Rpm - rpm));
        string Delta(Func<PointResult, Estimate> f, string unit, double rpm)
        {
            var m = At(mod, rpm);
            var s = At(stock, rpm);
            if (m is null || f(m).Value is not { } mv) return "n/a";
            return s is not null && f(s).Value is { } sv ? $"{sv:0.#}→{mv:0.#} {unit} @ {rpm:0} rpm" : $"{mv:0.#} {unit} @ {rpm:0} rpm";
        }
        return role switch
        {
            MapRole.BoostTarget or MapRole.BoostLimiter or MapRole.Svbl => $"Boost {Delta(p => p.Map, "mbar", 2500)}; air mass {Delta(p => p.AirMass, "mg", 2500)}.",
            MapRole.SmokeLimiter or MapRole.TorqueToIq or MapRole.Duration => $"IQ {Delta(p => p.Iq, "mg", 2500)}; λ {Delta(p => p.Lambda, "", 2500)}; EGT {Delta(p => p.Egt, "°C", 3500)}.",
            MapRole.TorqueLimiter or MapRole.DriverWish or MapRole.GearTorqueLimiter => $"Torque {Delta(p => p.Torque, "Nm", 2000)}.",
            MapRole.Soi => $"SOI {Delta(p => p.Soi, "°", 3000)}; EGT {Delta(p => p.Egt, "°C", 3000)}.",
            _ => "Effect not modelled for this map type.",
        };
    }
}
