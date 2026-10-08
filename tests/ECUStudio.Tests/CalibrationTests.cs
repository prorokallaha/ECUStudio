using ECUStudio.Binary;
using ECUStudio.Calibration.Definitions;
using ECUStudio.Calibration.Model;
using ECUStudio.Core;

namespace ECUStudio.Tests;

public class InterpolationTests
{
    private static CalibrationMap Map(double[] x, double[] y, double[] z) => new()
    {
        Definition = new MapDefinition { Id = "t", Name = "t", Rows = y.Length, Cols = x.Length },
        XAxis = x, YAxis = y, Values = z, Raw = z,
    };

    private static readonly CalibrationMap M = Map([1000, 2000, 3000], [0, 100], [10, 20, 30, 50, 60, 70]);

    [Theory]
    [InlineData(1000, 0, 10)]
    [InlineData(3000, 100, 70)]
    [InlineData(1500, 0, 15)]
    [InlineData(2000, 50, 40)]
    [InlineData(2500, 25, 35)]
    public void Bilinear_matches_hand_computed_values(double x, double y, double expected)
    {
        Assert.Equal(expected, Interpolation.Bilinear(M, x, y), 9);
    }

    [Fact]
    public void Lookups_clamp_at_axis_ends_like_bosch_ecus()
    {
        Assert.Equal(10, M.Lookup(0, -50), 9);
        Assert.Equal(70, M.Lookup(9000, 500), 9);
    }

    [Fact]
    public void One_dimensional_map_ignores_y()
    {
        var curve = Map([0, 10], [0], [0, 100]);
        Assert.Equal(25, curve.Lookup(2.5, 999), 9);
    }

    [Fact]
    public void InverseY_inverts_monotonic_column()
    {
        // Column at x=2000 runs 20 → 60 over y 0 → 100.
        Assert.Equal(37.5, Interpolation.InverseY(M, 2000, 35), 9);
        Assert.Equal(0, Interpolation.InverseY(M, 2000, 5), 9);
        Assert.Equal(40, Interpolation.Bilinear(M, 2000, Interpolation.InverseY(M, 2000, 40)), 9);
    }

    [Fact]
    public void Locate_handles_degenerate_axes()
    {
        Assert.Equal((0, 0, 0.0), Interpolation.Locate([5.0], 7));
        Assert.Equal((0, 1, 0.5), Interpolation.Locate([0.0, 2.0], 1));
    }
}

public class XdfImporterTests
{
    private const string Xdf = """
        <XDFFORMAT version="1.70">
          <XDFHEADER>
            <BASEOFFSET offset="0" subtract="0" />
            <DEFAULTS datasizeinbits="16" sigdigits="2" outputtype="1" signed="0" lsbfirst="0" float="0" />
          </XDFHEADER>
          <XDFTABLE uniqueid="0x1">
            <title>Smoke limiter (IQ by MAF)</title>
            <XDFAXIS id="x">
              <EMBEDDEDDATA mmedaddress="0x1C000" mmedelementsizebits="16" mmedcolcount="4" />
              <units>rpm</units>
              <MATH equation="X" />
            </XDFAXIS>
            <XDFAXIS id="y">
              <units>mg/stroke</units>
              <LABEL index="0" value="300" />
              <LABEL index="1" value="600" />
            </XDFAXIS>
            <XDFAXIS id="z">
              <EMBEDDEDDATA mmedaddress="0x1C010" mmedelementsizebits="16" mmedrowcount="2" mmedcolcount="4" mmedtypeflags="0x00" />
              <units>mg/stroke</units>
              <MATH equation="X*0.01" />
            </XDFAXIS>
          </XDFTABLE>
          <XDFTABLE uniqueid="0x2">
            <title>No data table</title>
            <XDFAXIS id="x"><units>-</units></XDFAXIS>
          </XDFTABLE>
        </XDFFORMAT>
        """;

    [Fact]
    public void Imports_tables_with_scaling_axes_and_role_guess()
    {
        var def = DefinitionImporters.Import("tune.xdf", Xdf);
        Assert.Equal(SourceType.Xdf, def.Source);
        var m = Assert.Single(def.Maps);
        Assert.Equal(MapRole.SmokeLimiter, m.Role);
        Assert.Equal(0x1C010, m.Address);
        Assert.Equal((2, 4), (m.Rows, m.Cols));
        Assert.Equal(0.01, m.Factor, 12);
        Assert.Equal(Endianness.Big, m.Endian);
        Assert.Equal(0x1C000, m.XAxis!.Address);
        Assert.Equal([300.0, 600.0], m.YAxis!.FixedValues!);
    }

    [Theory]
    [InlineData("X*0.01", 0.01, 0)]
    [InlineData("X/100", 0.01, 0)]
    [InlineData("0.5*X", 0.5, 0)]
    [InlineData("X*0.1-40", 0.1, -40)]
    [InlineData("X+273", 1, 273)]
    [InlineData("X/0.5-1.5", 2, -1.5)]
    public void Parses_linear_equations(string eq, double factor, double offset)
    {
        var (f, o) = XdfImporter.ParseLinear(eq, "t");
        Assert.Equal(factor, f, 12);
        Assert.Equal(offset, o, 12);
    }

    [Fact]
    public void Non_linear_equation_is_rejected_not_silently_approximated()
    {
        Assert.Throws<DefinitionException>(() => XdfImporter.ParseLinear("X*X", "t"));
        Assert.Throws<DefinitionException>(() => XdfImporter.ParseLinear("LOG(X)", "t"));
        Assert.Throws<DefinitionException>(() => XdfImporter.ParseLinear("X/0", "t"));
    }

    [Fact]
    public void Invalid_or_empty_xdf_is_a_definition_error()
    {
        Assert.Throws<DefinitionException>(() => DefinitionImporters.Import("a.xdf", "<not xml"));
        Assert.Throws<DefinitionException>(() => DefinitionImporters.Import("a.xdf", "<XDFFORMAT />"));
    }
}

public class BinaryPrimitivesTests
{
    [Fact]
    public void ChangedRanges_merges_small_gaps()
    {
        var a = new byte[64];
        var b = (byte[])a.Clone();
        b[10] = 1; b[12] = 1; b[40] = 1;
        var r = BinaryDiff.ChangedRanges(a, b, mergeGap: 4);
        Assert.Equal([new ByteRange(10, 3), new ByteRange(40, 1)], r);
        Assert.Equal(3, BinaryDiff.CountChangedBytes(a, b));
    }

    [Fact]
    public void Hex_pattern_supports_wildcards()
    {
        byte[] data = [0x00, 0x12, 0x34, 0x56, 0x12, 0xFF, 0x56];
        Assert.Equal([1, 4], PatternSearch.FindHex(data, "12 ?? 56"));
        Assert.Empty(PatternSearch.FindHex(data, "AB CD"));
    }

    [Fact]
    public void Crc32_matches_reference_vector()
    {
        Assert.Equal(0xCBF43926u, Checksums.Crc32("123456789"u8));
    }
}
