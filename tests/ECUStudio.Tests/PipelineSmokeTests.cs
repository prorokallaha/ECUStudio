using ECUStudio.Application.Analysis;
using ECUStudio.Core;
using Xunit.Abstractions;

namespace ECUStudio.Tests;

public class PipelineSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public void Stage1_analysis_produces_estimates_and_findings()
    {
        var session = Fixtures.Pipeline().Run(new AnalysisRequest
        {
            Modified = Fixtures.Image(Fixtures.Stage1, "stage1.bin"),
            Stock = Fixtures.Image(Fixtures.Stock, "stock.bin"),
            Vin = Fixtures.GolfVin,
        });
        var r = session.Report;
        foreach (var k in r.KeyMetrics) output.WriteLine($"{k.Label}: stock {k.Stock} | mod {k.Modified} | {k.Detail} {k.Severity}");
        foreach (var f in r.MainFindings) output.WriteLine($"* [{f.Severity}] {f.Text}");
        foreach (var f in r.CalibrationFindings) output.WriteLine($"- [{f.Severity}] {f.Code}: {f.Text}");
        foreach (var c in r.Risk.Components) output.WriteLine($"# {c.Label}: {c.Severity} {c.Explanation}");
        output.WriteLine($"Overall {r.Risk.Overall}, conf {r.Risk.Confidence}, data {r.DataAvailability.Level}");
        foreach (var w in r.Simulation.ModifiedWot) output.WriteLine($"{w.Rpm}: {w.TorqueNm} {w.PowerHp} boost {w.BoostMbar.Value:0} iq {w.IqMg.Value:0.0} λ {w.Lambda.Value:0.00} egt {w.EgtC.Value:0} {w.TorqueLimiter}/{w.FuelLimiter}/{w.BoostLimiter}");
        foreach (var w in r.Simulation.StockWot!) output.WriteLine($"stock {w.Rpm}: {w.TorqueNm} {w.PowerHp}");

        Assert.Equal("edc16u34", r.Ecu.PluginId);
        Assert.NotEmpty(r.ModifiedMaps);
        Assert.True(r.KeyMetrics.First(k => k.Id == "power").Modified.IsKnown);
        Assert.NotEqual(Severity.Safe, r.Risk.Overall);
    }
}
