using ECUStudio.Application.Analysis;
using ECUStudio.Components;
using ECUStudio.Core;
using ECUStudio.Simulation;

namespace ECUStudio.Tests;

public class RiskTests
{
    private static AnalysisReport Stage1(params HardwareOverride[] overrides) =>
        Fixtures.Pipeline().Run(new AnalysisRequest
        {
            Modified = Fixtures.Image(Fixtures.Stage1, "stage1.bin"),
            Stock = Fixtures.Image(Fixtures.Stock, "stock.bin"),
            Vin = Fixtures.GolfVin,
            Overrides = overrides,
            Grid = Fixtures.FastGrid,
        }).Report;

    private static readonly Lazy<AnalysisReport> Default = new(() => Stage1());

    [Fact]
    public void Unknown_limits_are_never_reported_safe()
    {
        foreach (var c in Default.Value.Risk.Components.Where(c => c.Critical && !c.Limit.IsKnownNumber() && c.Load.Kind != ValueKind.NotApplicable))
            Assert.NotEqual(Severity.Safe, c.Severity);
        Assert.NotEqual(Severity.Safe, Default.Value.Risk.Overall);
        Assert.NotEmpty(Default.Value.Risk.CriticalUnknowns);
    }

    [Fact]
    public void Unreliable_utilization_is_not_shown_as_exact_percentage()
    {
        foreach (var c in Default.Value.Risk.Components.Where(c => !c.Limit.IsKnownNumber() && c.Component != "thermal"))
            Assert.False(c.ShowExactUtilization, c.Component);
    }

    [Fact]
    public void Rail_pressure_is_not_applicable_on_pd_engine()
    {
        var fuel = Default.Value.Risk.Components.Single(c => c.Component == "fuel_system");
        Assert.Equal(ValueKind.NotApplicable, fuel.Load.Kind);
        Assert.False(fuel.Critical); // not applicable, so it must not count towards the verdict
        Assert.False(fuel.ShowExactUtilization);
    }

    [Fact]
    public void Dsg_override_makes_transmission_limit_known_and_evaluated()
    {
        var withDsg = Stage1(new HardwareOverride { Kind = ComponentKind.Transmission, CatalogId = "trans_dsg_dq250" });
        var trans = withDsg.Risk.Components.Single(c => c.Component == "transmission");
        Assert.Equal(350, trans.Limit.Number);
        Assert.NotEqual(Severity.Unknown, trans.Severity);
        Assert.True(trans.Utilization.IsKnown);

        var manual = Default.Value.Risk.Components.Single(c => c.Component == "transmission");
        Assert.Null(manual.Limit.Number);
        Assert.Equal(Severity.Unknown, manual.Severity);
    }

    [Fact]
    public void Every_risk_finding_carries_evidence()
    {
        Assert.All(Default.Value.Risk.Findings, f => Assert.NotEmpty(f.Evidence));
    }

    [Fact]
    public void Stage1_power_gain_is_a_range_with_stock_baseline()
    {
        var power = Default.Value.KeyMetrics.Single(k => k.Id == "power");
        Assert.True(power.Modified.IsKnown);
        Assert.True(power.Stock!.IsKnown);
        Assert.True(power.Modified.Value > power.Stock.Value);
        Assert.True(power.Modified.High > power.Modified.Low);
        Assert.InRange(power.Modified.Confidence, 0.05, 0.95);
    }
}

public class EnsembleTests
{
    private static ModelEstimate M(string id, double t, double conf, double halfWidth = 10, bool available = true) =>
        new(id, id, available ? t : null, t - halfWidth, t + halfWidth, conf, available, null);

    [Fact]
    public void Agreeing_models_give_narrow_range_and_higher_confidence()
    {
        var agree = SimulationEngine.Ensemble([M("a", 300, 0.7), M("b", 302, 0.7), M("c", 298, 0.7)], cap: 1);
        var disagree = SimulationEngine.Ensemble([M("a", 260, 0.7), M("b", 300, 0.7), M("c", 340, 0.7)], cap: 1);
        Assert.True(disagree.High - disagree.Low > agree.High - agree.Low);
        Assert.True(disagree.Confidence < agree.Confidence);
    }

    [Fact]
    public void Confidence_respects_cap_and_unavailable_models_are_ignored()
    {
        var e = SimulationEngine.Ensemble([M("a", 300, 0.9), M("b", 0, 0.9, available: false)], cap: 0.3);
        Assert.Equal(300, e.Value!.Value, 6);
        Assert.True(e.Confidence <= 0.3);
    }

    [Fact]
    public void No_usable_model_yields_unknown_not_zero()
    {
        var e = SimulationEngine.Ensemble([M("a", 0, 0.9, available: false)], cap: 1);
        Assert.False(e.IsKnown);
        Assert.Equal(ValueKind.Unknown, e.Kind);
    }
}

internal static class ParamTestExtensions
{
    public static bool IsKnownNumber(this Param p) => p.Number.HasValue && p.Source != SourceType.Unknown;
}
