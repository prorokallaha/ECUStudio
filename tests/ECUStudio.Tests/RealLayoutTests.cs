using System.Buffers.Binary;
using System.Text;
using ECUStudio.Binary;
using ECUStudio.Calibration.Definitions;
using ECUStudio.Calibration.Library;
using ECUStudio.Calibration.Model;
using ECUStudio.Calibration.Plugins.Edc16U34;
using ECUStudio.Calibration.Scanning;
using ECUStudio.Core;

namespace ECUStudio.Tests;

/// <summary>Layout of real EDC16U34 dumps: 2 MB image, VAG ID block, project code, HW number only in the file name.</summary>
public class RealLayoutTests
{
    private static byte[] Image2Mb()
    {
        var d = new byte[0x200000];
        Array.Fill(d, (byte)0xFF);
        Put(d, 0x1C0028, "1037382425P447HAXE");
        Put(d, 0x1C0CBE, "03G906021AB \0\0\0\0\0\0\0\003G906021MR \09389\0R4 2,0L EDC         ");
        // Inline map: nx=4, ny=3, rpm axis, pedal axis starting at a dead band (1 %), data.
        ushort[] map = [4, 3, 800, 1500, 2500, 4000, 100, 5000, 10000, 0, 0, 0, 0, 500, 900, 1200, 1100, 1500, 2600, 3200, 3000];
        for (var i = 0; i < map.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(0x1C2A74 + 2 * i), map[i]);
        return d;
    }

    private static void Put(byte[] d, int at, string s) => Encoding.ASCII.GetBytes(s).CopyTo(d, at);

    [Fact]
    public void Two_megabyte_dump_is_identified_from_the_vag_id_block_and_the_file_name()
    {
        using var image = BinaryImage.FromBytes(Image2Mb(), "0281013311_03G906021MR_1037382425_ORI.bin");
        var plugin = new Edc16U34Plugin(new DefinitionDatabase((string?)null));
        var ident = plugin.Identify(image);

        Assert.Contains(plugin.Detect(image).Reasons, r => r.Contains("matches EDC16U34 flash", StringComparison.Ordinal));
        Assert.Equal("1037382425", ident.SoftwareNumber.Text);
        Assert.Equal("03G906021MR", ident.OemPartNumber.Text); // software part, not the hardware part listed first
        Assert.Equal("03G906021AB", ident.OemHardwarePartNumber.Text);
        Assert.Equal("9389", ident.SoftwareVersion.Text);
        Assert.Equal("HAXE", ident.ProjectCode.Text);
        // HW only in the file name: used, but as weaker evidence and labelled with its source.
        Assert.Equal("0281013311", ident.HardwareNumber.Text);
        Assert.Equal(SourceType.FileName, ident.HardwareNumber.Source);
        Assert.True(ident.HardwareNumber.Confidence < 0.5);

        using var unnamed = BinaryImage.FromBytes(Image2Mb(), "mod.bin");
        Assert.False(plugin.Identify(unnamed).HardwareNumber.IsKnown);
    }

    [Fact]
    public void Changed_candidate_maps_are_measured_against_stock()
    {
        var stock = Image2Mb();
        var mod = (byte[])stock.Clone();
        var candidate = new Edc16U34Plugin(new DefinitionDatabase((string?)null))
            .ResolveDefinitions(BinaryImage.FromBytes(stock, "s.bin"), new() { PluginId = "edc16u34", EcuFamily = "Bosch EDC16U34" }, null)
            .Candidates.Single(c => c.HeaderAddress == 0x1C2A74);
        Assert.Equal(MapRole.DriverWish, candidate.Best!.Role); // pedal axis 1…100 % is recognised
        BinaryPrimitives.WriteUInt16BigEndian(mod.AsSpan(candidate.Address + 2 * 11), 3300); // 3000 → 3300

        var change = CandidateChange.Compare(candidate, stock, mod);
        Assert.Equal(1, change.ChangedCells);
        Assert.Equal(12, change.TotalCells);
        Assert.Equal(10, change.MaxDeltaPct);
        Assert.False(change.AxesChanged);
        Assert.False(CandidateChange.Compare(candidate, stock, stock).IsModified);
    }

    [Fact]
    public void Archive_folder_of_the_same_bosch_project_matches_as_probable()
    {
        var key = new BinaryKey("1037382425", null, "03G906021MR", "Bosch EDC16U34", null, null, "HAXE");
        LibraryEntry Entry(string path) => new() { Id = path, RootId = Guid.Empty, RelativePath = path, Format = LibraryScanner.FormatOf(path), Identifiers = LibraryScanner.Identify(path) };
        var a2l = Entry("DAMOS/EDC16U34/SW/HAXE/Daten/C447HAXE_00_13.a2l");
        var other = Entry("DAMOS/EDC16U34/SW/HASP/Daten/HASPXX.a2l");
        Assert.Contains("HAXE", a2l.Identifiers.ProjectCodes);
        Assert.Equal(["HAXE"], LibraryScanner.Identify("1037382425P447HAXE").ProjectCodes);

        var matches = DefinitionMatcher.Match(key, [a2l, other]);
        var m = Assert.Single(matches, x => x.Entry == a2l);
        Assert.Equal(MatchLevel.Probable, m.Level);
        Assert.Contains(m.Reasons, r => r.Contains("Bosch project HAXE", StringComparison.Ordinal));
        Assert.Contains(m.Reasons, r => r.Contains("check addresses", StringComparison.Ordinal));
        Assert.Equal(MatchLevel.Weak, matches.Single(x => x.Entry == other).Level);
    }
}
