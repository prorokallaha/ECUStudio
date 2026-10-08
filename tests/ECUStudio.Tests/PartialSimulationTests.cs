using ECUStudio.Application.Analysis;
using ECUStudio.Calibration.Model;
using ECUStudio.Simulation;

namespace ECUStudio.Tests;

public class PartialSimulationTests
{
    private static readonly Lazy<AnalysisSession> Session = new(() => Fixtures.Pipeline().Run(new AnalysisRequest
    {
        Modified = Fixtures.Image(Fixtures.Stage1, "stage1.bin"),
        Stock = Fixtures.Image(Fixtures.Stock, "stock.bin"),
        Vin = Fixtures.GolfVin,
        Grid = Fixtures.FastGrid,
    }));

    private static SimulationInput Without(MapRole role)
    {
        var s = Session.Value;
        var cal = s.ModInput.Calibration;
        return s.ModInput with { Calibration = new CalibrationSet(cal.BinarySha256, cal.PluginId, cal.Maps.Where(m => m.Definition.Role != role)) };
    }

    [Fact]
    public void Full_input_computes_every_output()
    {
        var coverage = Session.Value.Report.Simulation.Coverage.ToDictionary(c => c.Output);
        Assert.Equal(CoverageStatus.Computed, coverage["boostTarget"].Status);
        Assert.Equal(CoverageStatus.NotApplicable, coverage["railPressure"].Status);
        Assert.NotEqual(CoverageStatus.Unknown, coverage["torque"].Status);
    }

    [Fact]
    public void Missing_boost_map_makes_only_the_air_path_unknown_and_names_the_missing_map()
    {
        var input = Without(MapRole.BoostTarget);
        var coverage = SimulationCoverage.Evaluate(input).ToDictionary(c => c.Output);

        foreach (var o in new[] { "boostTarget", "map", "airMass", "lambda", "egt" })
        {
            Assert.Equal(CoverageStatus.Unknown, coverage[o].Status);
            Assert.Contains(coverage[o].Missing, m => m.Kind == PrerequisiteKind.Map && m.Id == nameof(MapRole.BoostTarget));
        }
        foreach (var o in new[] { "requestedTorque", "permittedTorque", "iqRequested", "iq", "soi" })
            Assert.Equal(CoverageStatus.Computed, coverage[o].Status);
        Assert.Equal(CoverageStatus.Partial, coverage["torque"].Status); // models A/B/D still work, C (airflow) does not

        // The engine agrees: torque is estimated, the air path is UNKNOWN.
        var point = new SimulationEngine().Evaluate(input, new OperatingPoint(2500, 100), EnvironmentConditions.Reference);
        Assert.False(point.BoostTarget.IsKnown);
        Assert.False(point.AirMass.IsKnown);
        Assert.False(point.Egt.IsKnown);
        Assert.True(point.Torque.IsKnown);
        Assert.True(point.Iq.IsKnown);
    }

    [Fact]
    public void Missing_torque_to_iq_map_propagates_to_everything_downstream()
    {
        var coverage = SimulationCoverage.Evaluate(Without(MapRole.TorqueToIq)).ToDictionary(c => c.Output);
        Assert.Equal(CoverageStatus.Computed, coverage["requestedTorque"].Status);
        foreach (var o in new[] { "iqRequested", "iq", "boostTarget", "lambda", "torque", "powerHp", "egt" })
        {
            Assert.Equal(CoverageStatus.Unknown, coverage[o].Status);
            Assert.Contains(coverage[o].Missing, m => m.Id == nameof(MapRole.TorqueToIq));
        }
    }
}
