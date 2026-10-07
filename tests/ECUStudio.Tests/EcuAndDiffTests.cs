using System.Buffers.Binary;
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
    public void Checksums_without_described_blocks_are_not_implemented_never_valid()
    {
        var img = Fixtures.Image(Fixtures.Stock, "stock.bin");
        var report = Fixtures.Plugin().VerifyChecksums(img, []);
        Assert.Equal(ChecksumStatus.NotImplemented, report.Overall);
        Assert.Empty(report.Blocks);
    }

    [Fact]
    public void Checksum_blocks_from_the_definition_are_verified()
    {
        var plugin = Fixtures.Plugin();
        ChecksumReport Verify(Lazy<ECUStudio.Application.DevTools.SyntheticEdc16U34.Result> r)
        {
            var img = Fixtures.Image(r, "x.bin");
            return plugin.VerifyChecksums(img, plugin.ResolveDefinitions(img, plugin.Identify(img), null).Checksums);
        }

        var stock = Verify(Fixtures.Stock);
        Assert.Equal(ChecksumStatus.Valid, stock.Overall);
        Assert.Equal(3, stock.Blocks.Count);
        Assert.Contains("not a flash-readiness check", stock.Note, StringComparison.Ordinal);
        Assert.Equal(ChecksumStatus.Valid, Verify(Fixtures.Stage1).Overall); // corrected like a tuning tool would

        var aggressive = Verify(Fixtures.Aggressive); // code patched after correction
        Assert.Equal(ChecksumStatus.Invalid, aggressive.Overall);
        Assert.Equal(ChecksumStatus.Invalid, aggressive.Blocks.Single(b => b.Name == "Code CRC32").Status);
        Assert.All(aggressive.Blocks.Where(b => b.Name != "Code CRC32"), b => Assert.Equal(ChecksumStatus.Valid, b.Status));
    }
}

public class ChecksumVerifierTests
{
    private static byte[] Image()
    {
        var img = new byte[64];
        for (var i = 0; i < 32; i++) img[i] = (byte)(i + 1);
        return img;
    }

    private static ChecksumSpec Spec(ChecksumAlgorithm a, int storeSize = 4, Endianness e = Endianness.Big, bool complement = false) =>
        new() { Name = "b", Start = 0, End = 32, Algorithm = a, StoredAt = 40, StoreSize = storeSize, Endian = e, Complement = complement };

    [Theory]
    [InlineData(ChecksumAlgorithm.Add8, 4, Endianness.Big, 528u)]            // 1 + … + 32
    [InlineData(ChecksumAlgorithm.Add8, 1, Endianness.Big, 528u & 0xFF)]     // truncated to the stored width
    [InlineData(ChecksumAlgorithm.Add16, 4, Endianness.Big, 0x10110u)]       // Σ (2k+1)·256 + (2k+2), k = 0..15
    [InlineData(ChecksumAlgorithm.Add16, 4, Endianness.Little, 0x11100u)]
    public void Sums_follow_word_size_endianness_and_width(ChecksumAlgorithm a, int size, Endianness e, uint expected) =>
        Assert.Equal(expected, ChecksumVerifier.Compute(Image(), Spec(a, size, e)));

    [Fact]
    public void Checksum_blocks_parse_from_definition_json()
    {
        var file = ECUStudio.Calibration.Definitions.DefinitionFile.Parse("""
            { "plugin": "edc16u34", "checksums": [
              { "name": "Cal", "start": "0x050000", "end": "0x070000", "algorithm": "Add16", "storedAt": "0x07FFF4", "storeSize": 2, "endian": "Little", "complement": true } ] }
            """);
        var spec = Assert.Single(file.ToChecksumSpecs());
        Assert.Equal((0x50000, 0x70000, 0x7FFF4, 2), (spec.Start, spec.End, spec.StoredAt, spec.StoreSize));
        Assert.Equal((ChecksumAlgorithm.Add16, Endianness.Little, true), (spec.Algorithm, spec.Endian, spec.Complement));
    }

    [Fact]
    public void Crc_variants_match_reference_check_values()
    {
        Assert.Equal(0x29B1, ChecksumVerifier.Crc16Ccitt("123456789"u8));
        var img = "123456789"u8.ToArray().Concat(new byte[8]).ToArray();
        var spec = new ChecksumSpec { Name = "c", Start = 0, End = 9, Algorithm = ChecksumAlgorithm.Crc32, StoredAt = 12 };
        Assert.Equal(0xCBF43926u, ChecksumVerifier.Compute(img, spec));
    }

    [Fact]
    public void Stored_value_and_complement_are_compared()
    {
        var img = Image();
        var sum = ChecksumVerifier.Compute(img, Spec(ChecksumAlgorithm.Add16));
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(40), ~sum);
        Assert.Equal(ChecksumStatus.Valid, ChecksumVerifier.Verify(img, [Spec(ChecksumAlgorithm.Add16, complement: true)], "t").Overall);
        var bad = ChecksumVerifier.Verify(img, [Spec(ChecksumAlgorithm.Add16)], "t");
        Assert.Equal(ChecksumStatus.Invalid, bad.Overall);
        Assert.Equal("0x00010110", bad.Blocks[0].Computed);
    }

    [Theory]
    [InlineData(0, 100, 40, "outside")]
    [InlineData(0, 31, 40, "multiple of 2")]
    [InlineData(0, 32, 30, "inside the covered range")]
    [InlineData(0, 32, 62, "outside the image")]
    public void Inconsistent_definitions_yield_unknown_not_valid(int start, int end, int storedAt, string message)
    {
        var spec = new ChecksumSpec { Name = "b", Start = start, End = end, Algorithm = ChecksumAlgorithm.Add16, StoredAt = storedAt };
        var r = ChecksumVerifier.Verify(Image(), [spec], "t");
        Assert.Equal(ChecksumStatus.Unknown, r.Overall);
        Assert.Contains(message, r.Blocks[0].Computed, StringComparison.Ordinal);
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
