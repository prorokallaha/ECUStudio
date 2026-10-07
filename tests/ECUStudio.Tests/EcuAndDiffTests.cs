using ECUStudio.Application.Analysis;
using ECUStudio.Binary;
using ECUStudio.Calibration.Scanning;
using ECUStudio.Core;

namespace ECUStudio.Tests;

public class PluginDetectionTests
{
    [Fact]
    public void Edc16u34_is_detected_and_identified_on_synthetic_image()
    {
        var img = Fixtures.Image(Fixtures.Stock, "stock.bin");
        var (plugin, detection) = Fixtures.Registry().Detect(img);
        Assert.Equal("edc16u34", plugin.PluginId);
        Assert.True(detection.Score >= 0.5, $"score {detection.Score}");
        Assert.False(plugin.HasCommonRail); // PD engine: rail pressure must be N/A, not 0
        var id = plugin.Identify(img);
        Assert.Equal("1037399999", id.SoftwareNumber.Text);
        Assert.NotEmpty(id.Sections);
    }

    [Fact]
    public void Unrelated_binary_is_rejected_with_reasons()
    {
        var rnd = new Random(42);
        var bytes = new byte[512 * 1024];
        rnd.NextBytes(bytes);
        var ex = Assert.Throws<UnsupportedEcuException>(() => Fixtures.Registry().Detect(BinaryImage.FromBytes(bytes, "random.bin")));
        Assert.Contains("edc16u34", ex.Message);
    }

    [Fact]
    public void Checksums_are_reported_honestly_not_as_valid()
    {
        var img = Fixtures.Image(Fixtures.Stock, "stock.bin");
        var report = Fixtures.Plugin().VerifyChecksums(img);
        Assert.NotEqual(ChecksumStatus.Valid, report.Overall);
    }
}

public class ScannerTests
{
    [Fact]
    public void Scanner_finds_bosch_style_maps_without_definitions()
    {
        var maps = BoschMapScanner.Scan(Fixtures.Stock.Value.Image);
        Assert.True(maps.Count >= 5, $"found {maps.Count}");
        Assert.All(maps, m =>
        {
            Assert.Equal(m.Cols, m.X.Length);
            Assert.Equal(m.Rows * m.Cols, m.Z.Length);
            Assert.True(m.X.Zip(m.X.Skip(1)).All(p => p.Second > p.First), "axis must be strictly increasing");
        });
    }

    [Fact]
    public void Rpm_axis_is_classified_as_engine_speed()
    {
        var guesses = SignatureClassifier.ClassifyAxis([750, 1000, 1500, 2000, 2500, 3000, 3500, 4000, 4500]);
        Assert.Equal(ECUStudio.Calibration.Model.AxisQuantity.EngineSpeed, guesses.OrderByDescending(g => g.Likelihood).First().Quantity);
    }

    [Fact]
    public void Without_definitions_maps_become_candidates_not_facts()
    {
        var session = Fixtures.Pipeline(withDb: false).Run(new AnalysisRequest
        {
            Modified = Fixtures.Image(Fixtures.Stock, "stock.bin"),
            Grid = Fixtures.FastGrid,
        });
        Assert.NotEmpty(session.Report.Candidates);
        Assert.All(session.Report.Candidates, c => Assert.True((c.Best?.Confidence ?? 0) < 0.9));
        Assert.NotEqual(Severity.Safe, session.Report.Risk.Overall);
    }
}

public class DiffAndAnomalyTests
{
    private static AnalysisReport Run(Lazy<ECUStudio.Application.DevTools.SyntheticEdc16U34.Result> mod) =>
        Fixtures.Pipeline().Run(new AnalysisRequest
        {
            Modified = Fixtures.Image(mod, "mod.bin"),
            Stock = Fixtures.Image(Fixtures.Stock, "stock.bin"),
            Vin = Fixtures.GolfVin,
            Grid = Fixtures.FastGrid,
        }).Report;

    [Fact]
    public void Aggressive_file_triggers_expected_anomalies()
    {
        var codes = Run(Fixtures.Aggressive).CalibrationFindings.Select(f => f.Code).ToHashSet();
        Assert.Contains("PERCENTAGE_TUNING", codes);
        Assert.Contains("LIMITER_MAXED", codes);
        Assert.Contains("CODE_SECTION_CHANGED", codes);
    }

    [Fact]
    public void Clean_stage1_has_no_code_changes_and_lists_modified_maps()
    {
        var r = Run(Fixtures.Stage1);
        Assert.DoesNotContain(r.CalibrationFindings, f => f.Code == "CODE_SECTION_CHANGED");
        Assert.Contains(r.ModifiedMaps, m => m.Role == ECUStudio.Calibration.Model.MapRole.TorqueLimiter);
        Assert.True(r.ChangedBytes > 0);
    }

    [Fact]
    public void Identical_files_produce_no_diff()
    {
        var r = Fixtures.Pipeline().Run(new AnalysisRequest
        {
            Modified = Fixtures.Image(Fixtures.Stock, "a.bin"),
            Stock = Fixtures.Image(Fixtures.Stock, "b.bin"),
            Grid = Fixtures.FastGrid,
        }).Report;
        Assert.Empty(r.ModifiedMaps);
        Assert.Equal(0, r.ChangedBytes);
    }
}
