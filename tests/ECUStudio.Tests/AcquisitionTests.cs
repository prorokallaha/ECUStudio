using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ECUStudio.Application.Acquisition;
using ECUStudio.Application.Analysis;
using ECUStudio.Application.DevTools;
using ECUStudio.Application.Library;
using ECUStudio.Application.Projects;
using ECUStudio.Calibration.Library;
using ECUStudio.Calibration.Model;
using ECUStudio.Core;
using ECUStudio.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MonoTorrent;
using Xunit;

namespace ECUStudio.Tests;

/// <summary>
/// The automatic definition flow end to end: upload → identify → no local definition → torrent index → selective
/// download (through the <see cref="ITorrentClient"/> abstraction, served by a local mirror) → verification →
/// import → re-analysis with named maps. Nothing in the flow knows a project code in advance.
/// </summary>
public sealed class AcquisitionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ecus-acq-" + Guid.NewGuid().ToString("N"));

    public AcquisitionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>Synthetic EDC16U34 image with a VAG ID block (version 9389) and a Bosch project code.</summary>
    private static byte[] PassatLikeImage(string project = "SYNT", string version = "9389")
    {
        var img = (byte[])SyntheticEdc16U34.Generate(SyntheticVariant.Stock).Image.Clone();
        void Put(int at, string s) => Encoding.ASCII.GetBytes(s).CopyTo(img, at);
        Put(0x40100, $"{SyntheticEdc16U34.SoftwareNumber}P999{project}");
        Put(0x40140, $"03G906021AB\0\0{SyntheticEdc16U34.PartNumber}\0\0{version}");
        return img;
    }

    private static readonly Dictionary<MapRole, string> BoschLabel = new()
    {
        [MapRole.DriverWish] = "AccPed_trqEngHiGear_MAP", [MapRole.TorqueLimiter] = "EngPrt_trqAPSLim_MAP", [MapRole.TorqueToIq] = "FMTC_trq2qBas_MAP",
        [MapRole.SmokeLimiter] = "FlMng_rLmbdSmk_MAP", [MapRole.BoostTarget] = "PCR_pDesBas_MAP", [MapRole.BoostLimiter] = "PCR_pMaxBas_MAP",
        [MapRole.Soi] = "InjCrv_phiMI1Bas1_MAP", [MapRole.VntDuty] = "PCR_rDesBas_MAP", [MapRole.Duration] = "InjCrv_tiMI_MAP", [MapRole.Svbl] = "PCR_pSVBL_MAP",
        [MapRole.RailPressure] = "Rail_pSetPointBase_MAP",
    };

    /// <summary>A2L in EDC16 inline layout ([nx][ny][x][y][z]) for the synthetic maps, optionally at shifted addresses.</summary>
    public static string SyntheticA2l(int shift = 0, string? epk = null, bool scramble = false, bool boschLabels = false)
    {
        var native = SyntheticEdc16U34.Generate(SyntheticVariant.Stock).Definition.ToDefinitions(SourceType.DefinitionDb)
            .Where(m => m.XAxis?.Address is { } xa && m.YAxis?.Address == xa + 2 * m.Cols && m.Address == xa + 2 * (m.Cols + m.Rows)).ToList();
        string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        var sb = new StringBuilder("""
            ASAP2_VERSION 1 61
            /begin PROJECT P "synthetic"
              /begin MODULE M "module"
                /begin MOD_COMMON "" BYTE_ORDER MSB_FIRST /end MOD_COMMON
                /begin RECORD_LAYOUT Kl_Row NO_AXIS_PTS_X 1 UWORD NO_AXIS_PTS_Y 2 UWORD AXIS_PTS_X 3 UWORD INDEX_INCR DIRECT AXIS_PTS_Y 4 UWORD INDEX_INCR DIRECT FNC_VALUES 5 UWORD ROW_DIR DIRECT /end RECORD_LAYOUT
            """).AppendLine();
        if (epk is not null) sb.AppendLine(CultureInfo.InvariantCulture, $"/begin MOD_PAR \"\" EPK \"{epk}\" /end MOD_PAR");
        var index = 0;
        foreach (var m in native)
        {
            ++index;
            var at = shift + (scramble ? 0x777 * index : 0);
            sb.AppendLine(CultureInfo.InvariantCulture, $"/begin COMPU_METHOD cm_{m.Id} \"\" LINEAR \"%8.3\" \"{m.Unit}\" COEFFS_LINEAR {F(m.Factor)} {F(m.Offset)} /end COMPU_METHOD");
            sb.AppendLine(CultureInfo.InvariantCulture, $"/begin COMPU_METHOD cx_{m.Id} \"\" LINEAR \"%8.3\" \"{m.XAxis!.Unit}\" COEFFS_LINEAR {F(m.XAxis.Factor)} 0 /end COMPU_METHOD");
            sb.AppendLine(CultureInfo.InvariantCulture, $"/begin COMPU_METHOD cy_{m.Id} \"\" LINEAR \"%8.3\" \"{m.YAxis!.Unit}\" COEFFS_LINEAR {F(m.YAxis.Factor)} 0 /end COMPU_METHOD");
            var header = 0x8000_0000L + m.XAxis.Address!.Value - 4 + at;
            // Real DAMOS: Bosch labels and a German long name that names no role keyword.
            var label = boschLabels && BoschLabel.TryGetValue(m.Role, out var bl) ? bl : m.Id;
            var longName = boschLabels ? $"Kennfeld {index}" : m.Name;
            sb.AppendLine(CultureInfo.InvariantCulture, $"""
                /begin CHARACTERISTIC {label} "{longName}" MAP 0x{header:X} Kl_Row 0 cm_{m.Id} 0 100000
                  /begin AXIS_DESCR STD_AXIS x cx_{m.Id} {m.Cols} 0 100000 /end AXIS_DESCR
                  /begin AXIS_DESCR STD_AXIS y cy_{m.Id} {m.Rows} 0 100000 /end AXIS_DESCR
                /end CHARACTERISTIC
                """);
        }
        sb.Append("""
              /end MODULE
            /end PROJECT
            """);
        return sb.ToString();
    }

    /// <summary>Builds a torrent of <paramref name="files"/> under "Archive/" and a mirror that serves them.</summary>
    private (byte[] Torrent, string Mirror) Archive(params (string Path, byte[] Content)[] files)
    {
        var mirror = Path.Combine(_dir, "mirror");
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(mirror, "Archive", path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
        }
        var torrent = new TorrentCreator().Create(new TorrentFileSource(Path.Combine(mirror, "Archive"))).Encode();
        return (torrent, mirror);
    }

    private ServiceProvider Services(string mirror, bool autoTestProbable = false)
    {
        var sp = new ServiceCollection()
            .AddEcuStudio(new EcuStudioOptions { Storage = "memory", IncludeDemoDefinitions = false, AnthropicApiKey = null, TorrentMirrorPath = mirror })
            .BuildServiceProvider();
        var acq = sp.GetRequiredService<DefinitionAcquisitionService>();
        acq.UpdateSettings(acq.Settings with { AutoTestProbable = autoTestProbable, CachePath = Path.Combine(_dir, "cache") });
        return sp;
    }

    private static async Task<JobStatus> WaitAsync(JobTracker jobs, Guid jobId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        JobStatus last = JobStatus.Queued;
        await foreach (var e in jobs.Subscribe(jobId, cts.Token)) last = e.Status;
        return last;
    }

    private static async Task<DefinitionAcquisition> WaitForAcquisitionAsync(DefinitionAcquisitionService acq, JobTracker jobs, Guid projectId)
    {
        var st = await acq.StatusAsync(projectId);
        Assert.NotNull(st);
        if (st.JobId is { } id) await WaitAsync(jobs, id);
        return (await acq.StatusAsync(projectId))!;
    }

    [Fact]
    public async Task Upload_alone_finds_downloads_verifies_and_binds_the_definition_from_a_torrent()
    {
        var image = PassatLikeImage();
        var sha = Convert.ToHexStringLower(SHA256.HashData(image));
        var a2l = Encoding.UTF8.GetBytes(SyntheticA2l());
        // The right file sits among decoys of the same family; only the project folder identifies it.
        var (torrent, mirror) = Archive(
            ("DAMOS/EDC16U34/SW/SYNT/Daten/C999SYNT_00_13.a2l", a2l),
            ("DAMOS/EDC16U34/SW/ABCD/Daten/C111ABCD_00_02.a2l", Encoding.UTF8.GetBytes(SyntheticA2l(0x2000))),
            ("DAMOS/MED9/41R138CVBA06.hex", new byte[4096]),
            // Same project folder, but not a definition: data sets and archives not named after this part are not offered.
            ("DAMOS/EDC16U34/SW/SYNT/Daten/SYNT_LSU_pa_060324.DCM", new byte[300]),
            ("DAMOS/EDC16U34/SW/SYNT/Daten/SYNTCHFQ1000.zip", new byte[500]));
        await using var sp = Services(mirror);
        var studio = sp.GetRequiredService<StudioService>();
        var acq = sp.GetRequiredService<DefinitionAcquisitionService>();
        var jobs = sp.GetRequiredService<JobTracker>();
        var root = studio.Library.AddTorrent("archive.torrent", torrent, null);
        studio.Library.Scan(root.Id);
        Assert.Contains(acq.Sources(), s => s.Id == root.Id && s.Indexed && s.FileCount == 5 && s.Downloaded == 0);

        var project = await studio.CreateProjectAsync("passat", null, null);
        var file = await studio.AddFileAsync(project.Id, "ori.bin", image, FileRole.Stock, null, null);
        var analysisJob = await studio.StartAnalysisAsync(project.Id, new AnalysisStartOptions(null, null));
        Assert.Equal(JobStatus.Completed, await WaitAsync(jobs, analysisJob));
        // The first analysis (the search may already have replaced it as the latest one by now).
        var before = jobs.History(analysisJob).Last(e => e.AnalysisId is not null).AnalysisId;

        var result = await WaitForAcquisitionAsync(acq, jobs, project.Id);
        Assert.True(result.State == AcquisitionState.Done, $"{result.State}: {result.Message}\n{string.Join("\n", result.Log)}");
        Assert.Equal("C999SYNT_00_13.a2l", result.Chosen?.FileName);
        Assert.Equal(3, result.Chosen!.Rank); // project match, version not named by the file
        Assert.Equal("9389", result.BinarySoftwareVersion);
        Assert.NotNull(result.Verification);
        Assert.Contains(result.Verification.Status, new[] { DefinitionFit.Exact, DefinitionFit.Compatible });
        Assert.True(result.Verification.Score >= 85, result.Verification.Score.ToString(CultureInfo.InvariantCulture));
        Assert.DoesNotContain(result.Candidates, c => c.FileName.StartsWith("41R", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Candidates, c => c.FileName.EndsWith(".DCM", StringComparison.Ordinal) || c.FileName.EndsWith(".zip", StringComparison.Ordinal));
        // Heuristic candidates of the first analysis are now named maps of the definition.
        Assert.True(result.ReconciledCount > 0);
        Assert.True(result.Reconciled.Any(r => r.MapName == "torque_limiter" && r.CandidateLabel.Contains("Torque Limiter", StringComparison.Ordinal)), string.Join("; ", result.Reconciled.Select(r => $"{r.CandidateLabel}->{r.MapName}")));

        // Only the chosen file was fetched; it is cached and the project uses it.
        var cacheDir = Path.Combine(_dir, "cache");
        var fetched = Directory.EnumerateFiles(cacheDir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".json", StringComparison.Ordinal)).ToList();
        Assert.Single(fetched);
        Assert.EndsWith("C999SYNT_00_13.a2l", fetched[0], StringComparison.Ordinal);
        var bound = await studio.GetProjectAsync(project.Id);
        Assert.Equal(DefinitionOrigin.Acquired, bound.Definition?.Origin);
        Assert.NotEqual(before, bound.LatestAnalysisId);
        Assert.Equal(result.AnalysisId, bound.LatestAnalysisId);

        // The new analysis shows the definition's maps by name; the binary is untouched.
        var report = (await studio.GetReportAsync(bound.LatestAnalysisId!.Value));
        Assert.Contains(report.Maps, m => m.Name == "Torque Limiter" || m.Id.Contains("torque_limiter", StringComparison.Ordinal));
        var (_, content) = await studio.GetFileContentAsync(project.Id, file.Id);
        Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(content)));

        // A second search for the same binary does not run again.
        Assert.Null(await acq.MaybeStartAfterAnalysisAsync(bound, file, sha));
    }

    [Fact]
    public async Task Definition_of_another_version_is_relocated_and_only_fitting_maps_are_used()
    {
        var image = PassatLikeImage();
        // 9351 A2L: the same maps 0x40 bytes lower than in the 9389 binary.
        var (torrent, mirror) = Archive(("SW/SYNT/03G906021ZZ_9351/C999SYNT_00_12.a2l", Encoding.UTF8.GetBytes(SyntheticA2l(-0x40))));
        await using var sp = Services(mirror);
        var studio = sp.GetRequiredService<StudioService>();
        var acq = sp.GetRequiredService<DefinitionAcquisitionService>();
        var jobs = sp.GetRequiredService<JobTracker>();
        var root = studio.Library.AddTorrent("archive.torrent", torrent, null);
        studio.Library.Scan(root.Id);

        var project = await studio.CreateProjectAsync("shift", null, null);
        await studio.AddFileAsync(project.Id, "ori.bin", image, FileRole.Stock, null, null);
        var analysisJob = await studio.StartAnalysisAsync(project.Id, new AnalysisStartOptions(null, null));
        await WaitAsync(jobs, analysisJob);
        var result = await WaitForAcquisitionAsync(acq, jobs, project.Id);

        Assert.True(result.State == AcquisitionState.Done, $"{result.State}: {result.Message}\n{string.Join("\n", result.Log)}");
        Assert.Contains("9351", result.Chosen!.Versions);
        var v = result.Verification!;
        Assert.True(v.RelocatedMaps > 0, string.Join("; ", v.Evidence.Concat(v.Conflicts)));
        Assert.All(v.Relocations, r => Assert.Equal(0x40, r.ResolvedBinaryAddress - r.SourceDefinitionAddress));
        Assert.True(v.Evidence.Concat(v.Conflicts).Any(e => e.Contains("9351", StringComparison.Ordinal)), string.Join("; ", v.Evidence.Concat(v.Conflicts)));
    }

    [Fact]
    public async Task Probable_definitions_wait_for_confirmation_and_download_on_request()
    {
        var image = PassatLikeImage(project: "QQQQ");
        // Only the OEM part number matches (rank 4, medium): never fetched automatically.
        var (torrent, mirror) = Archive(("VW/03G906021ZZ/03G906021ZZ_9351_ME.a2l", Encoding.UTF8.GetBytes(SyntheticA2l())));
        await using var sp = Services(mirror);
        var studio = sp.GetRequiredService<StudioService>();
        var acq = sp.GetRequiredService<DefinitionAcquisitionService>();
        var jobs = sp.GetRequiredService<JobTracker>();
        var root = studio.Library.AddTorrent("archive.torrent", torrent, null);
        studio.Library.Scan(root.Id);

        var project = await studio.CreateProjectAsync("probable", null, null);
        await studio.AddFileAsync(project.Id, "ori.bin", image, FileRole.Stock, null, null);
        await WaitAsync(jobs, await studio.StartAnalysisAsync(project.Id, new AnalysisStartOptions(null, null)));
        var waiting = await WaitForAcquisitionAsync(acq, jobs, project.Id);

        Assert.Equal(AcquisitionState.AwaitingConfirmation, waiting.State);
        var cand = Assert.Single(waiting.Candidates);
        Assert.Equal(MatchConfidence.Medium, cand.Confidence);
        Assert.False(Directory.Exists(Path.Combine(_dir, "cache")) && Directory.EnumerateFiles(Path.Combine(_dir, "cache"), "*.a2l", SearchOption.AllDirectories).Any());
        Assert.Null((await studio.GetProjectAsync(project.Id)).Definition);

        // "Download and check": the user picks the candidate.
        var job = await acq.StartAsync(project.Id, new AcquisitionRequest(null, cand.EntryId));
        await WaitAsync(jobs, job);
        var done = (await acq.StatusAsync(project.Id))!;
        Assert.True(done.State == AcquisitionState.Done, $"{done.State}: {done.Message}\n{string.Join("\n", done.Log)}");
        Assert.Equal(DefinitionOrigin.Acquired, (await studio.GetProjectAsync(project.Id)).Definition?.Origin);
    }

    [Fact]
    public async Task Incompatible_definition_is_rejected_and_heuristics_remain()
    {
        var image = PassatLikeImage();
        // Right name, wrong content: every map points somewhere else, with no common offset to recover.
        var (torrent, mirror) = Archive(("SW/SYNT/C999SYNT_00_13.a2l", Encoding.UTF8.GetBytes(SyntheticA2l(-0x30000, scramble: true))));
        await using var sp = Services(mirror);
        var studio = sp.GetRequiredService<StudioService>();
        var acq = sp.GetRequiredService<DefinitionAcquisitionService>();
        var jobs = sp.GetRequiredService<JobTracker>();
        var root = studio.Library.AddTorrent("archive.torrent", torrent, null);
        studio.Library.Scan(root.Id);

        var project = await studio.CreateProjectAsync("bad", null, null);
        await studio.AddFileAsync(project.Id, "ori.bin", image, FileRole.Stock, null, null);
        await WaitAsync(jobs, await studio.StartAnalysisAsync(project.Id, new AnalysisStartOptions(null, null)));
        var result = await WaitForAcquisitionAsync(acq, jobs, project.Id);

        Assert.Equal(AcquisitionState.NotFound, result.State);
        Assert.Equal(AcquisitionReason.Incompatible, result.Reason);
        Assert.Null((await studio.GetProjectAsync(project.Id)).Definition);
    }

    [Fact]
    public async Task Torrent_added_after_the_analysis_restarts_the_search()
    {
        var image = PassatLikeImage();
        var (torrent, mirror) = Archive(("DAMOS/EDC16U34/SW/SYNT/Daten/C999SYNT_00_13.a2l", Encoding.UTF8.GetBytes(SyntheticA2l())));
        await using var sp = Services(mirror);
        var studio = sp.GetRequiredService<StudioService>();
        var acq = sp.GetRequiredService<DefinitionAcquisitionService>();
        var jobs = sp.GetRequiredService<JobTracker>();

        // Analysed before any source exists: nothing to search.
        var project = await studio.CreateProjectAsync("late torrent", null, null);
        await studio.AddFileAsync(project.Id, "ori.bin", image, FileRole.Stock, null, null);
        await WaitAsync(jobs, await studio.StartAnalysisAsync(project.Id, new AnalysisStartOptions(null, null)));
        Assert.Null(await acq.StatusAsync(project.Id));

        // The torrent arrives later; indexing it resumes the search without a new analysis.
        var root = studio.Library.AddTorrent("archive.torrent", torrent, null);
        studio.Library.Scan(root.Id);
        Assert.Equal(1, await acq.ResumeAfterLibraryChangeAsync());
        var result = await WaitForAcquisitionAsync(acq, jobs, project.Id);
        Assert.True(result.State == AcquisitionState.Done, $"{result.State}: {result.Message}\n{string.Join("\n", result.Log)}");
        // A bound project is left alone afterwards.
        Assert.Equal(0, await acq.ResumeAfterLibraryChangeAsync());
    }

    [Fact]
    public async Task Failed_download_is_reported_as_such_not_as_not_found()
    {
        var image = PassatLikeImage();
        var (torrent, mirror) = Archive(("DAMOS/EDC16U34/SW/SYNT/Daten/C999SYNT_00_13.a2l", Encoding.UTF8.GetBytes(SyntheticA2l())));
        // The torrent lists the file, but nobody can deliver it.
        File.Delete(Path.Combine(mirror, "Archive", "DAMOS", "EDC16U34", "SW", "SYNT", "Daten", "C999SYNT_00_13.a2l"));
        await using var sp = Services(mirror);
        var studio = sp.GetRequiredService<StudioService>();
        var acq = sp.GetRequiredService<DefinitionAcquisitionService>();
        var jobs = sp.GetRequiredService<JobTracker>();
        var root = studio.Library.AddTorrent("archive.torrent", torrent, null);
        studio.Library.Scan(root.Id);

        var project = await studio.CreateProjectAsync("no peers", null, null);
        await studio.AddFileAsync(project.Id, "ori.bin", image, FileRole.Stock, null, null);
        await WaitAsync(jobs, await studio.StartAnalysisAsync(project.Id, new AnalysisStartOptions(null, null)));
        var result = await WaitForAcquisitionAsync(acq, jobs, project.Id);

        Assert.Equal(AcquisitionState.Failed, result.State);
        Assert.Equal(AcquisitionReason.DownloadFailed, result.Reason);
        Assert.Contains("C999SYNT_00_13.a2l", result.Message, StringComparison.Ordinal);
        Assert.Equal("C999SYNT_00_13.a2l", Assert.Single(result.Candidates).FileName);
    }

    [Fact]
    public async Task Bosch_labels_of_a_real_damos_give_roles_and_simulation_numbers()
    {
        var image = PassatLikeImage();
        var (torrent, mirror) = Archive(("DAMOS/EDC16U34/SW/SYNT/Daten/C999SYNT_00_13.a2l", Encoding.UTF8.GetBytes(SyntheticA2l(boschLabels: true))));
        await using var sp = Services(mirror);
        var studio = sp.GetRequiredService<StudioService>();
        var acq = sp.GetRequiredService<DefinitionAcquisitionService>();
        var jobs = sp.GetRequiredService<JobTracker>();
        var root = studio.Library.AddTorrent("archive.torrent", torrent, null);
        studio.Library.Scan(root.Id);
        var project = await studio.CreateProjectAsync("labels", null, null);
        await studio.AddFileAsync(project.Id, "ori.bin", image, FileRole.Stock, null, null);
        await WaitAsync(jobs, await studio.StartAnalysisAsync(project.Id, new AnalysisStartOptions(null, null)));
        var result = await WaitForAcquisitionAsync(acq, jobs, project.Id);
        Assert.True(result.State == AcquisitionState.Done, $"{result.State}: {result.Message}\n{string.Join("\n", result.Log)}");

        var report = await studio.GetReportAsync(result.AnalysisId!.Value);
        var driverWish = Assert.Single(report.Maps, m => m.Name == "AccPed_trqEngHiGear_MAP");
        Assert.Equal(MapRole.DriverWish, driverWish.Role);
        Assert.Equal("AccPed", driverWish.Group);
        Assert.StartsWith("Kennfeld", driverWish.Description, StringComparison.Ordinal);
        Assert.Contains(report.Maps, m => m.Name == "FMTC_trq2qBas_MAP" && m.Role == MapRole.TorqueToIq);
        // With roles from the labels the summary has numbers instead of UNKNOWN.
        var power = Assert.Single(report.KeyMetrics, k => k.Id == "power");
        Assert.NotEqual("Unknown", power.Modified.Kind.ToString());
    }
}
