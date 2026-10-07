using System.Text;
using System.Text.Json;
using ECUStudio.Application.Analysis;
using ECUStudio.Application.Library;
using ECUStudio.Application.Projects;
using ECUStudio.Binary;
using ECUStudio.Calibration.Definitions;
using ECUStudio.Calibration.Library;
using ECUStudio.Calibration.Plugins;
using ECUStudio.Core;
using ECUStudio.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ECUStudio.Tests;

public sealed class DefinitionLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ecu-lib-" + Guid.NewGuid().ToString("N"));

    public DefinitionLibraryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    private string Put(string relative, string content) => Put(relative, Encoding.UTF8.GetBytes(content));
    private string Put(string relative, byte[] content)
    {
        var path = Path.Combine(_dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string DemoDefinitionJson() => JsonSerializer.Serialize(Fixtures.Stock.Value.Definition, DefinitionFile.JsonOptions);

    [Fact]
    public void Identifiers_are_read_from_names_without_inventing_any()
    {
        var ids = LibraryScanner.Identify("VW/Passat B6 2,0 TDI BMP/03G906021AB_0281012345_1037370000 EDC16U34.a2l");
        Assert.Equal(["1037370000"], ids.SoftwareNumbers);
        Assert.Equal(["0281012345"], ids.HardwareNumbers);
        Assert.Equal(["03G906021AB"], ids.OemNumbers);
        Assert.Equal(["EDC16U34"], ids.EcuFamilies);
        Assert.Equal(["2.0 TDI"], ids.EngineHints);
        Assert.True(LibraryScanner.Identify("readme.txt").IsEmpty);
    }

    [Fact]
    public void Directory_scan_indexes_metadata_in_place_and_rescans_incrementally()
    {
        Put("a2l/EDC16U34_1037370000.a2l", "ASAP2_VERSION 1 60\n/begin PROJECT P1 \"Passat\"\n/begin MODULE M1 \"\"\n/begin CHARACTERISTIC A \"\" MAP 0x1 R 0 C 0 1\n/end CHARACTERISTIC\n/begin CHARACTERISTIC B \"\" VALUE 0x2 R 0 C 0 1\n/end CHARACTERISTIC\n/end MODULE\n/end PROJECT\n");
        Put("xdf/golf.xdf", "<XDFFORMAT><XDFHEADER><deftitle>Golf 1037369999</deftitle></XDFHEADER><XDFTABLE/><XDFTABLE/><XDFTABLE/></XDFFORMAT>");
        var dump = new byte[4096];
        Encoding.ASCII.GetBytes("  0281011111 1037365555 03G906016XX ").CopyTo(dump, 100);
        Put("dumps/unknown.bin", dump);
        Put("docs/notes.txt", "nothing here");
        Put("ols/Passat 03G906021AB.ols", [1, 2, 3]);

        var root = new LibraryRoot { Id = Guid.NewGuid(), Kind = LibraryRootKind.Directory, Path = _dir };
        var scanner = new LibraryScanner();
        var first = scanner.ScanDirectory(root, new Dictionary<string, LibraryEntry>()).ToList();

        Assert.Equal(4, first.Count); // notes.txt carries no identifiers and is skipped
        var a2l = first.Single(e => e.Format == LibraryFormat.A2L);
        Assert.Equal(2, a2l.ObjectCount);
        Assert.Equal("P1 / M1", a2l.Title);
        var xdf = first.Single(e => e.Format == LibraryFormat.Xdf);
        Assert.Equal(3, xdf.ObjectCount);
        Assert.Contains("1037369999", xdf.Identifiers.SoftwareNumbers);
        var bin = first.Single(e => e.Format == LibraryFormat.Binary);
        Assert.True(bin.ContentIdentified);
        Assert.Contains("1037365555", bin.Identifiers.SoftwareNumbers);
        Assert.NotNull(bin.Sha256);
        Assert.Contains("03G906021AB", first.Single(e => e.Format == LibraryFormat.Ols).Identifiers.OemNumbers);

        var second = scanner.ScanDirectory(root, first.ToDictionary(e => e.RelativePath)).ToList();
        Assert.All(second, e => Assert.Same(first.Single(f => f.RelativePath == e.RelativePath), e)); // unchanged files are not re-read
    }

    private static byte[] Torrent(string name, params (string Path, long Length)[] files)
    {
        static string S(string s) => $"{Encoding.UTF8.GetByteCount(s)}:{s}";
        var list = string.Concat(files.Select(f => $"d6:lengthi{f.Length}e4:pathl{string.Concat(f.Path.Split('/').Select(S))}ee"));
        return Encoding.UTF8.GetBytes($"d8:announce{S("http://tracker.invalid/a")}4:infod5:filesl{list}e4:name{S(name)}12:piece lengthi16384e6:pieces0:ee");
    }

    [Fact]
    public void Torrent_metadata_lists_files_and_marks_only_downloaded_ones_available()
    {
        var bytes = Torrent("DAMOS pack", ("EDC16/1037370000.a2l", 120), ("EDC16/1037371111.dam", 50), ("readme.nfo", 10));
        var meta = TorrentMetadata.Parse(bytes);
        Assert.Equal("DAMOS pack", meta.Name);
        Assert.Equal(3, meta.Files.Count);
        Assert.Equal(180, meta.TotalLength);

        Put("DAMOS pack/EDC16/1037370000.a2l", new string('x', 120));
        var root = new LibraryRoot { Id = Guid.NewGuid(), Kind = LibraryRootKind.Torrent, Path = "pack.torrent", DownloadPath = _dir };
        var entries = new LibraryScanner().ScanTorrent(root, meta, new Dictionary<string, LibraryEntry>()).ToList();

        Assert.Equal(2, entries.Count); // the .nfo has no identifiers
        Assert.True(entries.Single(e => e.Format == LibraryFormat.A2L).Available);
        var dam = entries.Single(e => e.Format == LibraryFormat.Damos);
        Assert.False(dam.Available);
        Assert.Contains("1037371111", dam.Identifiers.SoftwareNumbers);
    }

    [Theory]
    [InlineData("../evil.a2l")]
    [InlineData("a/../../b.a2l")]
    public void Torrent_with_unsafe_paths_is_rejected(string path) =>
        Assert.Throws<DefinitionException>(() => TorrentMetadata.Parse(Torrent("x", (path, 1))));

    private static LibraryEntry Entry(string name, string? sha = null, bool content = false) => new()
    {
        Id = name, RootId = Guid.Empty, RelativePath = name, Format = LibraryScanner.FormatOf(name), Identifiers = LibraryScanner.Identify(name), Sha256 = sha, ContentIdentified = content,
    };

    [Fact]
    public void Matcher_levels_follow_the_evidence_and_explain_themselves()
    {
        var key = new BinaryKey("1037370000", "0281012345", "03G906021AB", "Bosch EDC16U34", "abc", "2.0");
        var entries = new[]
        {
            Entry("exact/0281012345_1037370000.a2l"),
            Entry("strong/1037370000.a2l"),
            Entry("otherhw/0281099999_1037370000.a2l"),
            Entry("oem/03G906021AB_1037371234.dam"),
            Entry("family/EDC16U34 2.0 TDI.xdf"),
            Entry("otherfamily/EDC17C46_1037370000.a2l"),
            Entry("dump/stock.bin", sha: "ABC"),
            Entry("unrelated/ME7.5 1.8T.a2l"),
        };
        var m = DefinitionMatcher.Match(key, entries).ToDictionary(x => x.Entry.Id);

        Assert.Equal(MatchLevel.Exact, m["exact/0281012345_1037370000.a2l"].Level);
        Assert.Equal(MatchLevel.Exact, m["dump/stock.bin"].Level);
        Assert.Contains("identical file (SHA-256)", m["dump/stock.bin"].Reasons);
        Assert.Equal(MatchLevel.Strong, m["strong/1037370000.a2l"].Level);
        Assert.Equal(MatchLevel.Probable, m["otherhw/0281099999_1037370000.a2l"].Level);
        Assert.Contains(m["otherhw/0281099999_1037370000.a2l"].Reasons, r => r.StartsWith("HW differs", StringComparison.Ordinal));
        Assert.Equal(MatchLevel.Probable, m["oem/03G906021AB_1037371234.dam"].Level);
        Assert.Contains(m["oem/03G906021AB_1037371234.dam"].Reasons, r => r.StartsWith("different SW", StringComparison.Ordinal));
        Assert.Equal(MatchLevel.Weak, m["family/EDC16U34 2.0 TDI.xdf"].Level);
        Assert.Contains("engine 2.0", m["family/EDC16U34 2.0 TDI.xdf"].Reasons);
        Assert.False(m.ContainsKey("otherfamily/EDC17C46_1037370000.a2l")); // another ECU family is never offered
        Assert.False(m.ContainsKey("unrelated/ME7.5 1.8T.a2l"));
        var ranked = DefinitionMatcher.Match(key, entries);
        Assert.Equal(ranked.OrderBy(x => x.Level).Select(x => x.Entry.Id), ranked.Select(x => x.Entry.Id));
        Assert.Equal(MatchLevel.Exact, ranked[0].Level);
    }

    [Fact]
    public void Compatibility_check_rejects_a_definition_that_lands_on_other_data()
    {
        var image = Fixtures.Image(Fixtures.Stock, "stock.bin");
        var ident = Fixtures.Plugin().Identify(image);
        var file = Fixtures.Stock.Value.Definition;
        var good = new ExternalDefinition(SourceType.User, file.ToDefinitions(SourceType.User), "demo_1037399999.ecudef.json");
        var ok = DefinitionCompatibility.Check(good, image, ident);
        Assert.Equal(CompatibilityStatus.Compatible, ok.Status);
        Assert.True(ok.SoftwareMatches);
        Assert.Equal(ok.MapCount, ok.MapsDecoded);

        // Same layout shifted by 2 bytes: axes no longer rise, and the name says another SW.
        var shifted = new ExternalDefinition(SourceType.User, good.Maps.Select(d => d with
        {
            Address = d.Address + 2,
            XAxis = d.XAxis is { Address: { } xa } x ? x with { Address = xa + 2 } : d.XAxis,
            YAxis = d.YAxis is { Address: { } ya } y ? y with { Address = ya + 2 } : d.YAxis,
        }).ToList(), "other_1037311111.a2l");
        var bad = DefinitionCompatibility.Check(shifted, image, ident);
        Assert.Equal(CompatibilityStatus.Incompatible, bad.Status);
        Assert.False(bad.SoftwareMatches);
        Assert.True(bad.AxesNotMonotonic > 0);

        var outside = new ExternalDefinition(SourceType.User, good.Maps.Select(d => d with { Address = image.Length + 16 }).ToList(), "x.a2l");
        var oob = DefinitionCompatibility.Check(outside, image, ident);
        Assert.Equal(CompatibilityStatus.Incompatible, oob.Status);
        Assert.Equal(oob.MapCount, oob.MapsOutOfRange);
    }

    private ServiceProvider Services() => new ServiceCollection()
        .AddEcuStudio(new EcuStudioOptions { Storage = "memory", IncludeDemoDefinitions = false, AnthropicApiKey = null })
        .BuildServiceProvider();

    [Fact]
    public async Task Analysis_picks_a_strong_library_match_and_reports_why()
    {
        Put("archive/EDC16U34/demo_1037399999.ecudef.json", DemoDefinitionJson());
        Put("archive/EDC16U34/other_1037311111.ecudef.json", DemoDefinitionJson().Replace("1037399999", "1037311111", StringComparison.Ordinal));
        await using var sp = Services();
        var studio = sp.GetRequiredService<StudioService>();
        var root = studio.Library.AddDirectory(Path.Combine(_dir, "archive"), null);
        studio.Library.Scan(root.Id);

        var project = await studio.CreateProjectAsync("lib", null, null);
        var file = await studio.AddFileAsync(project.Id, "stock.bin", Fixtures.Stock.Value.Image, FileRole.Stock, null, null);
        var session = await studio.RunAnalysisAsync(await studio.GetProjectAsync(project.Id), file, null, null, CancellationToken.None);

        var binding = session.Report.DefinitionBinding;
        Assert.NotNull(binding);
        Assert.Equal(DefinitionOrigin.AutoLibrary, binding.Origin);
        Assert.True(binding.Applied);
        Assert.Equal(MatchLevel.Strong, binding.Level);
        Assert.Contains(binding.Reasons, r => r.Contains("1037399999", StringComparison.Ordinal));
        Assert.Equal(CompatibilityStatus.Compatible, binding.Compatibility!.Status);
        Assert.StartsWith("User:", session.Report.DefinitionSource, StringComparison.Ordinal);
        Assert.NotEmpty(session.Report.Maps);

        var matches = await studio.LibraryMatchesAsync(session.Report.Id);
        Assert.Equal("demo_1037399999.ecudef.json", Path.GetFileName(matches[0].Entry.RelativePath));
        Assert.Contains(matches, x => x.Entry.RelativePath.EndsWith("other_1037311111.ecudef.json", StringComparison.Ordinal) && x.Level == MatchLevel.Weak);
    }

    [Fact]
    public async Task Importing_a_definition_checks_it_and_an_incompatible_one_needs_force()
    {
        await using var sp = Services();
        var studio = sp.GetRequiredService<StudioService>();
        var project = await studio.CreateProjectAsync("imp", null, null);
        await studio.AddFileAsync(project.Id, "stock.bin", Fixtures.Stock.Value.Image, FileRole.Stock, null, null);

        var json = Encoding.UTF8.GetBytes(DemoDefinitionJson());
        var preview = await studio.PreviewDefinitionAsync(project.Id, "demo.ecudef.json", json);
        Assert.True(preview.Importable);
        Assert.Equal(Fixtures.Stock.Value.Definition.Maps.Count, preview.MapCount);
        Assert.Equal("1037399999", preview.BinarySoftware);
        Assert.Equal(CompatibilityStatus.Compatible, preview.Compatibility!.Status);

        var bound = await studio.BindDefinitionAsync(project.Id, "demo.ecudef.json", json, force: false);
        Assert.Equal(DefinitionOrigin.Upload, bound.Definition!.Origin);
        var session = await studio.RunAnalysisAsync(bound, bound.Stock!, null, null, CancellationToken.None);
        Assert.Equal(DefinitionOrigin.Upload, session.Report.DefinitionBinding!.Origin);
        Assert.True(session.Report.DefinitionBinding.Applied);

        var file = Fixtures.Stock.Value.Definition;
        var shifted = file with { Maps = file.Maps.Select(m => m with { Address = m.Address + 2, XAxis = m.XAxis is { } x ? x with { Address = x.Address + 2 } : null, YAxis = m.YAxis is { } y ? y with { Address = y.Address + 2 } : null }).ToList() };
        var badJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(shifted, DefinitionFile.JsonOptions));
        var ex = await Assert.ThrowsAsync<EcuStudioException>(() => studio.BindDefinitionAsync(project.Id, "shifted.ecudef.json", badJson, force: false));
        Assert.Equal("DEFINITION_INCOMPATIBLE", ex.Code);
        var forced = await studio.BindDefinitionAsync(project.Id, "shifted.ecudef.json", badJson, force: true);
        Assert.Equal(CompatibilityStatus.Incompatible, forced.Definition!.Compatibility!.Status);

        var dam = await studio.PreviewDefinitionAsync(project.Id, "x.dam", [1, 2, 3]);
        Assert.False(dam.Importable);
        Assert.Contains("ASAP2", dam.Error, StringComparison.Ordinal);

        var cleared = await studio.UnbindDefinitionAsync(project.Id);
        Assert.Null(cleared.Definition);
    }

    [Fact]
    public async Task Stock_candidates_come_from_project_files_and_library_dumps_with_the_same_software()
    {
        Put("dumps/EDC16U34_stock.bin", Fixtures.Stock.Value.Image);
        await using var sp = Services();
        var studio = sp.GetRequiredService<StudioService>();
        var root = studio.Library.AddDirectory(Path.Combine(_dir, "dumps"), null);
        studio.Library.Scan(root.Id);
        var project = await studio.CreateProjectAsync("stock", null, null);
        var mod = await studio.AddFileAsync(project.Id, "stage1.bin", Fixtures.Stage1.Value.Image, FileRole.Modified, null, null);
        var session = await studio.RunAnalysisAsync(await studio.GetProjectAsync(project.Id), mod, null, null, CancellationToken.None);

        var fromLibrary = Assert.Single(await studio.StockCandidatesAsync(session.Report.Id));
        Assert.Equal(StockCandidateOrigin.Library, fromLibrary.Origin);
        Assert.True(fromLibrary.SameSoftware);
        Assert.True(fromLibrary.DifferingBytes > 0);

        var added = await studio.AddFileFromLibraryAsync(project.Id, new AddFromLibraryBody(fromLibrary.LibraryEntryId!, FileRole.Version, null));
        var fromProject = Assert.Single(await studio.StockCandidatesAsync(session.Report.Id)); // the library copy is not listed twice
        Assert.Equal(added.Id, fromProject.FileId);
        Assert.Equal(MatchLevel.Exact, fromProject.Level); // SW + HW match: still only a candidate
        Assert.Equal(fromLibrary.DifferingBytes, fromProject.DifferingBytes);
        Assert.Equal("EDC16U34_stock.bin", added.Name);
    }
}
