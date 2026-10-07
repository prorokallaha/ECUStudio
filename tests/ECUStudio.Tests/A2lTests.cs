using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using ECUStudio.Binary;
using ECUStudio.Calibration.Definitions;
using ECUStudio.Calibration.Model;
using ECUStudio.Calibration.Plugins;
using ECUStudio.Core;

namespace ECUStudio.Tests;

public class A2lImporterTests
{
    private const string Header = """
        ASAP2_VERSION 1 61
        /* comment with /begin inside is ignored */
        /begin PROJECT P "test"
          /begin MODULE M "module"
            /begin MOD_COMMON "" BYTE_ORDER MSB_FIRST /end MOD_COMMON
            /begin RECORD_LAYOUT Kl_Xu16_Yu16_Wu16_Col
              NO_AXIS_PTS_X 1 UWORD
              NO_AXIS_PTS_Y 2 UWORD
              AXIS_PTS_X 3 UWORD INDEX_INCR DIRECT
              AXIS_PTS_Y 4 UWORD INDEX_INCR DIRECT
              FNC_VALUES 5 UWORD COLUMN_DIR DIRECT
            /end RECORD_LAYOUT
            /begin RECORD_LAYOUT Val_Wu16 FNC_VALUES 1 UWORD ROW_DIR DIRECT /end RECORD_LAYOUT
            /begin RECORD_LAYOUT Val_Ws16 FNC_VALUES 1 SWORD ROW_DIR DIRECT /end RECORD_LAYOUT
            /begin RECORD_LAYOUT Axis_u16 NO_AXIS_PTS_X 1 UWORD AXIS_PTS_X 2 UWORD INDEX_INCR DIRECT /end RECORD_LAYOUT
            /begin COMPU_METHOD Rpm "" IDENTICAL "%5.0" "rpm" /end COMPU_METHOD
            /begin COMPU_METHOD Mg "" LINEAR "%5.2" "mg/stroke" COEFFS_LINEAR 0.01 0 /end COMPU_METHOD
            /begin COMPU_METHOD Temp "" RAT_FUNC "%5.1" "degC" COEFFS 0 10 400 0 0 1 /end COMPU_METHOD
            /begin COMPU_METHOD Quad "" RAT_FUNC "%5.1" "-" COEFFS 1 0 0 0 0 1 /end COMPU_METHOD
            /begin COMPU_METHOD Verbal "" TAB_VERB "%5.0" "" COMPU_TAB_REF T /end COMPU_METHOD
        """;

    private const string Footer = """
          /end MODULE
        /end PROJECT
        """;

    /// <summary>Image: STD_AXIS map 3×2 (column-major) at 0x80000100, COM_AXIS curve, FIX_AXIS curve, scalar.</summary>
    private static (string A2l, byte[] Image) Sample()
    {
        var img = new byte[0x400];
        void W(int a, int v) => BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(a), (ushort)v);
        // Map: X = rpm (3), Y = rpm-like (2). Stored column-major: for each X, all Y.
        W(0x100, 3); W(0x102, 2);
        W(0x104, 1000); W(0x106, 2000); W(0x108, 3000);
        W(0x10A, 10); W(0x10C, 20);
        int[,] z = { { 1000, 2000, 3000 }, { 1100, 2100, 3100 } }; // [row(Y), col(X)] raw
        var a = 0x10E;
        for (var col = 0; col < 3; col++) for (var row = 0; row < 2; row++) { W(a, z[row, col]); a += 2; }
        // Shared axis (AXIS_PTS) at 0x200: count + 4 points.
        W(0x200, 4); W(0x202, 1500); W(0x204, 2500); W(0x206, 3500); W(0x208, 4500);
        // COM_AXIS curve values at 0x220 (temperature via RAT_FUNC: phys = (int - 400) / 10).
        W(0x220, 400); W(0x222, 900); W(0x224, 1400); W(0x226, 1900);
        // FIX_AXIS curve at 0x240.
        W(0x240, 5000); W(0x242, 6000); W(0x244, 7000);
        // Scalar.
        W(0x260, 4242);

        var a2l = Header + "\n" + """
            /begin AXIS_PTS RpmAxis "shared" 0x80000200 Rpm Axis_u16 0 Rpm 4 0 6000 /end AXIS_PTS
            /begin CHARACTERISTIC KFRAUCH "Rauchbegrenzung Menge" MAP 0x80000100 Kl_Xu16_Yu16_Wu16_Col 0 Mg 0 100
              /begin AXIS_DESCR STD_AXIS nmot Rpm 3 0 6000 /end AXIS_DESCR
              /begin AXIS_DESCR STD_AXIS other NO_COMPU_METHOD 2 0 100 /end AXIS_DESCR
            /end CHARACTERISTIC
            /begin CHARACTERISTIC KLTEMP "Kühlmittel Korrektur" CURVE 0x80000220 Val_Wu16 0 Temp -40 150
              /begin AXIS_DESCR COM_AXIS nmot Rpm 4 0 6000 AXIS_PTS_REF RpmAxis /end AXIS_DESCR
            /end CHARACTERISTIC
            /begin CHARACTERISTIC KLFIX "fixed" CURVE 0x80000240 Val_Wu16 0 Mg 0 100
              /begin AXIS_DESCR FIX_AXIS nix NO_COMPU_METHOD 3 0 100 FIX_AXIS_PAR_DIST 0 25 3 /end AXIS_DESCR
            /end CHARACTERISTIC
            /begin CHARACTERISTIC CWMAX "scalar" VALUE 0x80000260 Val_Wu16 0 Rpm 0 6000 /end CHARACTERISTIC
            /begin CHARACTERISTIC KLQUAD "non-linear" CURVE 0x80000240 Val_Wu16 0 Quad 0 100
              /begin AXIS_DESCR FIX_AXIS nix NO_COMPU_METHOD 3 0 100 FIX_AXIS_PAR 0 0 3 /end AXIS_DESCR
            /end CHARACTERISTIC
            /begin CHARACTERISTIC KLVERB "verbal" VALUE 0x80000260 Val_Wu16 0 Verbal 0 1 /end CHARACTERISTIC
            /begin CHARACTERISTIC TXT "string" ASCII 0x80000300 Val_Wu16 0 NO_COMPU_METHOD 0 0 NUMBER 8 /end CHARACTERISTIC
            """ + "\n" + Footer;
        return (a2l, img);
    }

    private static CalibrationMap Decode(ExternalDefinition def, byte[] img, string name) =>
        MapDecoder.Decode(BinaryImage.FromBytes(img), def.Maps.Single(m => m.Name == name));

    [Fact]
    public void Imports_std_axis_map_with_column_major_values_and_rebased_addresses()
    {
        var (a2l, img) = Sample();
        var def = DefinitionImporters.Import("ecu.a2l", a2l);
        Assert.Equal(SourceType.A2L, def.Source);
        var m = def.Maps.Single(x => x.Name == "KFRAUCH");
        Assert.Equal(MapRole.SmokeLimiter, m.Role); // German description
        Assert.Equal(0x10E, m.Address);
        Assert.Equal(ValueOrder.ColumnMajor, m.Order);
        Assert.Equal((2, 3), (m.Rows, m.Cols));

        var map = Decode(def, img, "KFRAUCH");
        Assert.Equal([1000.0, 2000.0, 3000.0], map.XAxis);
        Assert.Equal([10.0, 20.0], map.YAxis);
        Assert.Equal([10.0, 20.0, 30.0, 11.0, 21.0, 31.0], map.Values.Select(v => Math.Round(v, 6)));
        Assert.Contains(def.Notes, n => n.Contains("0x80000000", StringComparison.Ordinal));
    }

    [Fact]
    public void Imports_com_axis_fix_axis_scalar_and_linear_rat_func()
    {
        var (a2l, img) = Sample();
        var def = DefinitionImporters.Import("ecu.a2l", a2l);

        var temp = Decode(def, img, "KLTEMP");
        Assert.Equal([1500.0, 2500.0, 3500.0, 4500.0], temp.XAxis);
        Assert.Equal([0.0, 50.0, 100.0, 150.0], temp.Values.Select(v => Math.Round(v, 6)));
        Assert.Equal("degC", temp.Definition.Unit);

        var fix = Decode(def, img, "KLFIX");
        Assert.Equal([0.0, 25.0, 50.0], fix.XAxis);
        Assert.Equal([50.0, 60.0, 70.0], fix.Values.Select(v => Math.Round(v, 6)));

        var scalar = Decode(def, img, "CWMAX");
        Assert.Equal(4242, Assert.Single(scalar.Values));
    }

    [Fact]
    public void Unsupported_conversions_are_skipped_and_reported_not_approximated()
    {
        var (a2l, _) = Sample();
        var def = DefinitionImporters.Import("ecu.a2l", a2l);
        Assert.DoesNotContain(def.Maps, m => m.Name is "KLQUAD" or "KLVERB" or "TXT");
        Assert.Contains(def.Notes, n => n.Contains("KLQUAD", StringComparison.Ordinal) && n.Contains("non-linear", StringComparison.Ordinal));
        Assert.Contains(def.Notes, n => n.Contains("KLVERB", StringComparison.Ordinal) && n.Contains("TAB_VERB", StringComparison.Ordinal));
        Assert.Contains(def.Notes, n => n.StartsWith("A2L: 4 characteristic(s) imported, 2 skipped", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/begin PROJECT P \"\" /begin MODULE M \"\" /end PROJECT", "does not close")]
    [InlineData("/begin PROJECT P \"\" /begin MODULE M \"\" /end MODULE", "never closed")]
    [InlineData("/begin PROJECT P \"unterminated /end PROJECT", "unterminated string")]
    [InlineData("/begin PROJECT P \"\" /end PROJECT", "no MODULE")]
    [InlineData("/begin PROJECT P \"\" /begin MODULE M \"\" /end MODULE /end PROJECT", "no CHARACTERISTIC")]
    public void Malformed_files_fail_with_definition_exception(string a2l, string message)
    {
        var ex = Assert.Throws<DefinitionException>(() => DefinitionImporters.Import("x.a2l", a2l));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A2l_describing_the_synthetic_image_decodes_identically_to_the_native_definition()
    {
        var stock = Fixtures.Stock.Value;
        // Maps written by the synthetic generator in Bosch layout: [nx][ny][X axis][Y axis][values].
        var native = stock.Definition.ToDefinitions(SourceType.DefinitionDb)
            .Where(m => m.XAxis?.Address is { } xa && m.YAxis?.Address == xa + 2 * m.Cols && m.Address == xa + 2 * (m.Cols + m.Rows)).ToList();
        Assert.True(native.Count >= 5);
        var sb = new StringBuilder(Header).AppendLine();
        sb.AppendLine("/begin RECORD_LAYOUT Kl_Row NO_AXIS_PTS_X 1 UWORD NO_AXIS_PTS_Y 2 UWORD AXIS_PTS_X 3 UWORD INDEX_INCR DIRECT AXIS_PTS_Y 4 UWORD INDEX_INCR DIRECT FNC_VALUES 5 UWORD ROW_DIR DIRECT /end RECORD_LAYOUT");
        string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        foreach (var m in native)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"/begin COMPU_METHOD cm_{m.Id} \"\" LINEAR \"%8.3\" \"{m.Unit}\" COEFFS_LINEAR {F(m.Factor)} {F(m.Offset)} /end COMPU_METHOD");
            sb.AppendLine(CultureInfo.InvariantCulture, $"/begin COMPU_METHOD cx_{m.Id} \"\" LINEAR \"%8.3\" \"{m.XAxis!.Unit}\" COEFFS_LINEAR {F(m.XAxis.Factor)} 0 /end COMPU_METHOD");
            sb.AppendLine(CultureInfo.InvariantCulture, $"/begin COMPU_METHOD cy_{m.Id} \"\" LINEAR \"%8.3\" \"{m.YAxis!.Unit}\" COEFFS_LINEAR {F(m.YAxis.Factor)} 0 /end COMPU_METHOD");
            var header = 0x8000_0000L + m.XAxis.Address!.Value - 4;
            sb.AppendLine(CultureInfo.InvariantCulture, $"""
                /begin CHARACTERISTIC {m.Id} "{m.Name}" MAP 0x{header:X} Kl_Row 0 cm_{m.Id} 0 100000
                  /begin AXIS_DESCR STD_AXIS x cx_{m.Id} {m.Cols} 0 100000 /end AXIS_DESCR
                  /begin AXIS_DESCR STD_AXIS y cy_{m.Id} {m.Rows} 0 100000 /end AXIS_DESCR
                /end CHARACTERISTIC
                """);
        }
        sb.Append(Footer);

        var def = new A2lImporter().Import("synthetic.a2l", sb.ToString());
        var image = Fixtures.Image(Fixtures.Stock, "stock.bin");
        Assert.Equal(native.Count, def.Maps.Count);
        foreach (var n in native)
        {
            var a = MapDecoder.Decode(image, n);
            var b = MapDecoder.Decode(image, def.Maps.Single(m => m.Name == n.Id));
            Assert.Equal(a.XAxis, b.XAxis);
            Assert.Equal(a.YAxis, b.YAxis);
            Assert.Equal(a.Values, b.Values);
        }
    }
}
