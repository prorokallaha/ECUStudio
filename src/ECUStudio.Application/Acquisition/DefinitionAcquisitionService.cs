using ECUStudio.Binary.Editing;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using ECUStudio.Application.Analysis;
using ECUStudio.Application.Library;
using ECUStudio.Application.Projects;
using ECUStudio.Binary;
using ECUStudio.Calibration.Library;
using ECUStudio.Calibration.Plugins;
using ECUStudio.Core;

namespace ECUStudio.Application.Acquisition;

/// <summary>
/// Finds the definition for a project's binary without manual steps: identify → local library → torrent sources
/// (metadata for magnet links, selective download of only the chosen file) → verification against the binary →
/// binding to the project → re-analysis that replaces matching heuristic candidates. Definitions are metadata over
/// the binary: nothing here writes to a binary file.
/// </summary>
public sealed class DefinitionAcquisitionService(
    IProjectStore store,
    DefinitionService definitions,
    ITorrentClient torrents,
    DefinitionCache cache,
    IAcquisitionStore settingsStore,
    JobTracker jobs,
    PluginRegistry plugins)
{
    private const int MaxAttempts = 4;
    private const int MaxShown = 8;
    private readonly ConcurrentDictionary<Guid, DefinitionAcquisition> _live = new();
    private readonly ConcurrentDictionary<Guid, byte> _running = new();
    private SemaphoreSlim _downloads = new(Math.Max(1, settingsStore.LoadSettings().MaxConcurrentDownloads));
    private AcquisitionSettings _settings = settingsStore.LoadSettings();

    /// <summary>Runs an analysis of the project and returns its id (wired by the composition root).</summary>
    public Func<Guid, CancellationToken, Task<Guid?>>? Reanalyze { get; set; }

    public string ClientName => torrents.Name;
    public AcquisitionSettings Settings => _settings;

    public AcquisitionSettings UpdateSettings(AcquisitionSettings settings)
    {
        settings = settings with { MaxConcurrentDownloads = Math.Clamp(settings.MaxConcurrentDownloads, 1, 8) };
        if (settings.CachePath is { Length: > 0 } p && !Path.IsPathFullyQualified(p)) throw new EcuStudioException("LIBRARY_PATH", "Cache path must be absolute");
        if (settings.MaxConcurrentDownloads != _settings.MaxConcurrentDownloads) _downloads = new SemaphoreSlim(settings.MaxConcurrentDownloads);
        _settings = settings;
        settingsStore.SaveSettings(settings);
        if (settings.CachePath is { Length: > 0 } cp) definitions.Library.CacheDirectory = cp;
        return settings;
    }

    /// <summary>Current state: the running job's live state, else the last outcome stored with the project.</summary>
    public async Task<DefinitionAcquisition?> StatusAsync(Guid projectId, CancellationToken ct = default) =>
        _live.TryGetValue(projectId, out var live) ? live : (await store.GetAsync(projectId, ct))?.Acquisition;

    public IReadOnlyList<TorrentSource> Sources()
    {
        var lib = definitions.Library;
        return lib.Roots().Where(r => r.Root.Kind == LibraryRootKind.Torrent).Select(r => new TorrentSource(
            r.Root.Id, r.Root.Name ?? r.Root.Path, r.Root.InfoHash, r.Root.MagnetUri, r.Root.FileCount, r.Root.TotalBytes, r.Root.LastScanAt is not null,
            r.Root.MagnetUri is not null && lib.TorrentBytes(r.Root.Id) is null, lib.DownloadDirectory(r.Root), r.Root.Enabled, r.Root.Priority,
            r.Root.FileCount - r.Unavailable, r.Root.LastError)).OrderBy(s => s.Priority).ThenBy(s => s.Name).ToList();
    }

    /// <summary>
    /// Starts a search after an analysis found no definition, once per binary, when torrent or library sources exist.
    /// Returns the job id, or null when nothing is started.
    /// </summary>
    public async Task<Guid?> MaybeStartAfterAnalysisAsync(Project project, ProjectFile file, string sha256, CancellationToken ct = default)
    {
        if (!_settings.AutoAcquire || project.Definition is not null || _running.ContainsKey(project.Id)) return null;
        // Searched for this binary already: again only after a failed search when a source was added or re-indexed since.
        if (project.Acquisition is { } last && last.FileSha256 == sha256
            && (last.State is not (AcquisitionState.NotFound or AcquisitionState.Failed)
                || definitions.Library.Roots().All(r => (r.Root.LastScanAt ?? r.Root.AddedAt) < last.UpdatedAt))) return null;
        if (definitions.Library.Roots().Count == 0) return null;
        return await StartAsync(project.Id, new AcquisitionRequest(file.Id), ct);
    }

    /// <summary>
    /// A source was added or re-indexed: repeats the search for analysed projects that have no definition yet and whose
    /// last search did not find one (or never ran). Returns how many searches started.
    /// </summary>
    public async Task<int> ResumeAfterLibraryChangeAsync(CancellationToken ct = default)
    {
        if (!_settings.AutoAcquire || definitions.Library.Roots().Count == 0) return 0;
        var started = 0;
        foreach (var p in await store.ListAsync(ct))
        {
            if (p.Definition is not null || p.LatestAnalysisId is null || _running.ContainsKey(p.Id) || p.Files.Count == 0) continue;
            if (p.Acquisition is { State: not (AcquisitionState.NotFound or AcquisitionState.Failed) }) continue;
            try
            {
                await StartAsync(p.Id, new AcquisitionRequest(), ct);
                started++;
            }
            catch (EcuStudioException) { }
        }
        return started;
    }

    public async Task<Guid> StartAsync(Guid projectId, AcquisitionRequest request, CancellationToken ct = default)
    {
        var project = await store.GetAsync(projectId, ct) ?? throw new NotFoundException($"Project {projectId} not found");
        var file = (request.FileId is { } fid ? project.Files.FirstOrDefault(f => f.Id == fid) : null)
            ?? project.Files.LastOrDefault(f => f.Role == FileRole.Modified) ?? project.Files.LastOrDefault(f => f.Role != FileRole.Stock) ?? project.Stock
            ?? throw new EcuStudioException("NO_FILES", "Project has no binaries");
        if (!_running.TryAdd(projectId, 0)) throw new EcuStudioException("ACQUISITION_BUSY", "A definition search is already running for this project", 409);
        var jobId = jobs.Create();
        _live[projectId] = new DefinitionAcquisition { JobId = jobId, State = AcquisitionState.Identify, FileSha256 = file.Sha256 };
        _ = Task.Run(async () =>
        {
            try { await RunAsync(jobId, project, file, request.EntryId, CancellationToken.None); }
            catch (Exception ex)
            {
                await FinishAsync(projectId, Live(projectId) with { State = AcquisitionState.Failed, Message = ex is EcuStudioException ee ? ee.Message : ex.Message });
                jobs.Publish(new JobEvent(jobId, "failed", null, JobStatus.Failed, ex is EcuStudioException e2 ? $"{e2.Code}: {e2.Message}" : ex.Message));
            }
            finally { _running.TryRemove(projectId, out _); }
        }, CancellationToken.None);
        return jobId;
    }

    private DefinitionAcquisition Live(Guid projectId) => _live.GetValueOrDefault(projectId) ?? new DefinitionAcquisition { State = AcquisitionState.Identify };

    private async Task RunAsync(Guid jobId, Project project, ProjectFile file, string? explicitEntry, CancellationToken ct)
    {
        var log = new List<string>();
        void Set(Func<DefinitionAcquisition, DefinitionAcquisition> f) => _live[project.Id] = f(Live(project.Id)) with { Log = log.ToList(), UpdatedAt = DateTimeOffset.UtcNow };
        void Step(string id, string label, StepState state, string? message = null, double? fraction = null, TransferInfo? transfer = null) =>
            jobs.Publish(new JobEvent(jobId, "step", new StepProgress(id, label, state, fraction, message), JobStatus.Running, Transfer: transfer));

        // IDENTIFY
        Step("identify", "Identifying ECU", StepState.Running);
        var bytes = await store.GetFileContentAsync(file.Id, ct) ?? throw new NotFoundException($"Content for {file.Name} missing");
        using var image = BinaryImage.FromBytes(bytes, file.Name);
        var key = definitions.KeyOf(image);
        var (plugin, _) = plugins.Detect(image);
        var ident = plugin.Identify(image);
        Set(a => a with { State = AcquisitionState.LocalSearch, BinarySoftwareVersion = key.SoftwareVersion });
        Step("identify", "Identifying ECU", StepState.Done,
            string.Join(" · ", new[] { ident.EcuFamily, key.OemNumber, key.SoftwareNumber, key.ProjectCode, key.SoftwareVersion }.Where(s => !string.IsNullOrEmpty(s))));

        // WAITING_METADATA: magnet sources need their file list first.
        await FetchPendingMetadataAsync(log, (s, m) => Step("waiting_metadata", "Fetching torrent metadata", s, m), ct);

        var library = definitions.Library;
        var roots = library.Roots().ToDictionary(r => r.Root.Id, r => r.Root);
        var matches = library.Match(key, 400)
            .Where(m => IsWanted(m, key) && (explicitEntry is null || m.Entry.Id == explicitEntry))
            .OrderBy(m => m.Rank).ThenBy(m => FormatOrder(m.Entry)).ThenBy(m => SoftwareDistance(m.Entry, key))
            .ThenBy(m => roots.GetValueOrDefault(m.Entry.RootId)?.Priority ?? 0).ThenByDescending(m => m.Score)
            .ToList();
        if (explicitEntry is not null && matches.Count == 0)
        {
            // The user picked a file: allow it even below the automatic ranks, but never another ECU family.
            var e = library.Entry(explicitEntry);
            matches = library.Match(key, 2000).Where(m => m.Entry.Id == explicitEntry).ToList();
            if (matches.Count == 0) throw new EcuStudioException("ACQUISITION_MISMATCH", $"{e.RelativePath} does not match this binary");
        }
        var candidates = matches.Take(MaxShown).Select(m => Candidate(m, roots, key)).ToList();
        Set(a => a with { Candidates = candidates });

        // LOCAL_SEARCH: files already on disk (directories, earlier downloads).
        Step("local_search", "Searching local definitions", StepState.Running);
        var local = matches.Where(m => m.Entry.Available).Take(MaxAttempts).ToList();
        Step("local_search", "Searching local definitions", StepState.Done, local.Count == 0 ? "none on disk" : $"{local.Count} candidate(s) on disk");
        (AcquisitionCandidate Cand, DefinitionSource Source, VerifiedDefinition Verified)? best = null;
        foreach (var m in local)
        {
            var outcome = TryVerify(m, image, roots, key, log, (s, msg) => Step("verifying", "Checking compatibility", s, msg));
            if (outcome is null) continue;
            if (Better(outcome.Value.Verified.Result, best?.Verified.Result)) best = outcome;
            if (outcome.Value.Verified.Result.Status is DefinitionFit.Exact or DefinitionFit.Compatible) break;
        }

        var downloadsTried = 0;
        var downloadsFailed = 0;
        string? lastDownloadError = null;
        // TORRENT_SEARCH: only when nothing local is good enough.
        if (best is null || best.Value.Verified.Result.Status is not (DefinitionFit.Exact or DefinitionFit.Compatible))
        {
            Set(a => a with { State = AcquisitionState.TorrentSearch });
            Step("torrent_search", "Searching torrent sources", StepState.Running);
            var remote = matches.Where(m => !m.Entry.Available && roots.GetValueOrDefault(m.Entry.RootId)?.Kind == LibraryRootKind.Torrent).ToList();
            var allowed = remote.Where(m => explicitEntry is not null || Allowed(m.Confidence)).Take(MaxAttempts).ToList();
            Step("torrent_search", "Searching torrent sources", StepState.Done,
                remote.Count == 0 ? "no matching files in torrent sources" : $"{remote.Count} candidate(s), best: {Path.GetFileName(remote[0].Entry.RelativePath)} ({remote[0].Confidence})");

            var deadRoots = new HashSet<Guid>();
            foreach (var m in allowed)
            {
                // A torrent that delivered nothing to one file will not deliver the next one either.
                if (deadRoots.Contains(m.Entry.RootId)) continue;
                var cand = Candidate(m, roots, key);
                Set(a => a with { State = AcquisitionState.Downloading, Chosen = cand });
                downloadsTried++;
                var error = await DownloadAsync(m, roots[m.Entry.RootId], cand, log, Step, transfer => Set(a => a with { Transfer = transfer }), ct);
                if (error is not null)
                {
                    downloadsFailed++;
                    lastDownloadError = error;
                    if (error.Contains("no data for", StringComparison.Ordinal)) deadRoots.Add(m.Entry.RootId);
                    continue;
                }
                Set(a => a with { State = AcquisitionState.Verifying });
                var refreshed = library.Match(key, 2000).FirstOrDefault(x => x.Entry.Id == m.Entry.Id) ?? m;
                var outcome = TryVerify(refreshed, image, roots, key, log, (s, msg) => Step("verifying", "Checking compatibility", s, msg));
                if (outcome is null) continue;
                if (Better(outcome.Value.Verified.Result, best?.Verified.Result)) best = outcome;
                if (outcome.Value.Verified.Result.Status is DefinitionFit.Exact or DefinitionFit.Compatible) break;
            }

            if (best is null && allowed.Count == 0 && remote.Count > 0)
            {
                var waiting = remote.Where(m => m.Confidence <= MatchConfidence.Medium).Take(5).Select(m => Candidate(m, roots, key)).ToList();
                if (waiting.Count > 0)
                {
                    log.Add("Only probable definitions found: download needs your confirmation (or enable automatic testing of probable definitions)");
                    await FinishAsync(project.Id, Live(project.Id) with { State = AcquisitionState.AwaitingConfirmation, Candidates = waiting, Log = log.ToList(), Message = "Probable definition found" });
                    jobs.Publish(new JobEvent(jobId, "completed", null, JobStatus.Completed));
                    return;
                }
            }
        }

        if (best is null && downloadsTried > 0 && downloadsFailed == downloadsTried)
        {
            // The files exist in the torrent but could not be fetched: that is not "not found".
            log.Add("Definition files were found in the torrent but none could be downloaded");
            await FinishAsync(project.Id, Live(project.Id) with
            {
                State = AcquisitionState.Failed, Reason = AcquisitionReason.DownloadFailed, Message = lastDownloadError ?? "Download failed",
                Log = log.ToList(), Transfer = null,
            });
            jobs.Publish(new JobEvent(jobId, "completed", null, JobStatus.Completed));
            return;
        }
        if (best is not { } chosen || chosen.Verified.Result.Status is DefinitionFit.Incompatible or DefinitionFit.Unknown || chosen.Verified.Result.Score < 50)
        {
            log.Add("No suitable definition: maps are discovered heuristically and stay candidates");
            var reason = best is not null ? AcquisitionReason.Incompatible
                : roots.Count == 0 ? AcquisitionReason.NoSources
                : roots.Values.Any(r => r.Enabled && r.LastScanAt is null) ? AcquisitionReason.NotIndexed
                : AcquisitionReason.NoCandidates;
            await FinishAsync(project.Id, Live(project.Id) with
            {
                State = AcquisitionState.NotFound, Reason = reason, Message = "Not found: heuristic analysis", Log = log.ToList(),
                Verification = best?.Verified.Result, Transfer = null,
            });
            jobs.Publish(new JobEvent(jobId, "completed", null, JobStatus.Completed));
            return;
        }

        // IMPORTING: bind to the project (metadata only).
        Set(a => a with { State = AcquisitionState.Importing, Chosen = chosen.Cand, Verification = chosen.Verified.Result });
        Step("importing", "Importing definition", StepState.Running, chosen.Source.Name);
        var current = await store.GetAsync(project.Id, ct) ?? project;
        var root = roots[library.Entry(chosen.Cand.EntryId).RootId];
        var definition = new ProjectDefinition
        {
            Id = Guid.NewGuid(), Name = chosen.Source.Name, Format = chosen.Source.Format, Origin = DefinitionOrigin.Acquired,
            LibraryEntryId = chosen.Cand.EntryId, ArchivePath = chosen.Source.ArchivePath, Sha256 = Hashing.Sha256Hex(chosen.Source.Content),
            Size = chosen.Source.Content.Length, MapCount = chosen.Verified.Definition.Maps.Count, Identifiers = library.Entry(chosen.Cand.EntryId).Identifiers,
            Compatibility = chosen.Verified.Result.ToReport(), Verification = chosen.Verified.Result, CheckedAgainst = file.Name,
            MatchConfidence = chosen.Cand.Confidence, TorrentHash = root.Kind == LibraryRootKind.Torrent ? root.InfoHash : null,
        };
        await store.SaveAsync(current with { Definition = definition, UpdatedAt = DateTimeOffset.UtcNow }, ct);
        if (definition.TorrentHash is { } th) cache.LinkDefinition(th, library.Entry(chosen.Cand.EntryId).RelativePath, definition.Id);
        var r = chosen.Verified.Result;
        log.Add($"Definition {chosen.Source.Name} connected: {r.Status} {r.Score} %, {r.Tables} tables, {r.Axes} axes, {r.Parameters} parameters");
        Step("importing", "Importing definition", StepState.Done, $"{r.Tables} tables, {r.Axes} axes, {r.Parameters} parameters");

        // MATCHING_MAPS: re-analyse; heuristic candidates covered by the definition become its maps.
        Set(a => a with { State = AcquisitionState.MatchingMaps });
        Step("matching_maps", "Matching maps", StepState.Running);
        var previousReport = current.LatestAnalysisId is { } prev ? await TryReportAsync(prev, ct) : null;
        Guid? analysisId = Reanalyze is null ? null : await Reanalyze(project.Id, ct);
        var reconciled = analysisId is { } aid && previousReport is not null && await TryReportAsync(aid, ct) is { } newReport
            ? Reconcile(previousReport, newReport) : [];
        await RelabelEditsAsync(project.Id, reconciled, ct);
        Step("matching_maps", "Matching maps", StepState.Done, $"{reconciled.Count} candidate(s) now identified by the definition");

        await FinishAsync(project.Id, Live(project.Id) with
        {
            State = AcquisitionState.Done, Message = "Definition connected", Log = log.ToList(), Transfer = null, AnalysisId = analysisId,
            ReconciledCount = reconciled.Count, Reconciled = reconciled.Take(100).ToList(),
        });
        jobs.Publish(new JobEvent(jobId, "completed", null, JobStatus.Completed, null, analysisId));
    }

    private bool Allowed(MatchConfidence c) => c switch
    {
        MatchConfidence.Exact or MatchConfidence.VeryHigh or MatchConfidence.High => _settings.AutoDownloadExact,
        MatchConfidence.Medium => _settings.AutoTestProbable,
        _ => false,
    };

    private static bool Better(DefinitionCompatibilityResult a, DefinitionCompatibilityResult? b) =>
        b is null || Order(a.Status) < Order(b.Status) || Order(a.Status) == Order(b.Status) && a.Score > b.Score;

    private static int Order(DefinitionFit f) => f switch
    {
        DefinitionFit.Exact => 0, DefinitionFit.Compatible => 1, DefinitionFit.Partial => 2, DefinitionFit.Unknown => 3, _ => 4,
    };

    /// <summary>
    /// Only what can define this binary's maps: A2L/XDF/ECU definitions of the same software (or project) and zip
    /// archives named after its OEM part or SW number. Data sets (DCM), notes, other ECUs and other parts are not offered.
    /// </summary>
    public static bool IsWanted(DefinitionMatch m, BinaryKey key)
    {
        if (m.Rank > 4) return false;
        if (DefinitionService.IsImportable(m.Entry.Format)) return true;
        if (!DefinitionService.IsAcquirable(m.Entry)) return false;
        var ids = m.Entry.Identifiers;
        var oem = key.OemNumber is { Length: > 0 } o ? LibraryScanner.NormalizeOem(o) : null;
        var shortSw = DefinitionMatcher.ShortSoftware(key.SoftwareNumber);
        return oem is not null && ids.OemNumbers.Contains(oem)
            || key.SoftwareNumber is { } sw && ids.SoftwareNumbers.Contains(sw)
            || shortSw is not null && DefinitionMatcher.ShortSoftwareIn(m.Entry.RelativePath)?.ToString(System.Globalization.CultureInfo.InvariantCulture) == shortSw;
    }

    /// <summary>Distance between the binary's SW number and the one in the file name (0 when equal, large when unknown).</summary>
    private static int SoftwareDistance(LibraryEntry e, BinaryKey key)
    {
        if (DefinitionMatcher.ShortSoftware(key.SoftwareNumber) is not { } s || DefinitionMatcher.ShortSoftwareIn(e.RelativePath) is not { } n) return 500_000;
        return Math.Abs(n - int.Parse(s, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static int FormatOrder(LibraryEntry e) => e.Format switch
    {
        LibraryFormat.A2L => 0, LibraryFormat.EcuDef => 1, LibraryFormat.Xdf => 2, _ => 3,
    };

    private static AcquisitionCandidate Candidate(DefinitionMatch m, IReadOnlyDictionary<Guid, LibraryRoot> roots, BinaryKey key) => new()
    {
        EntryId = m.Entry.Id, Path = m.Entry.RelativePath, FileName = Path.GetFileName(m.Entry.RelativePath), Format = m.Entry.Format, Size = m.Entry.Size,
        Available = m.Entry.Available, Source = roots.GetValueOrDefault(m.Entry.RootId)?.Name ?? "?", SourceId = m.Entry.RootId,
        Rank = m.Rank, Confidence = m.Confidence, Level = m.Level, Reasons = m.Reasons,
        Versions = m.Entry.Identifiers.SoftwareVersions,
    };

    private (AcquisitionCandidate Cand, DefinitionSource Source, VerifiedDefinition Verified)? TryVerify(DefinitionMatch m, BinaryImage image, IReadOnlyDictionary<Guid, LibraryRoot> roots,
        BinaryKey key, List<string> log, Action<StepState, string?> step)
    {
        var cand = Candidate(m, roots, key);
        step(StepState.Running, cand.FileName);
        try
        {
            var source = definitions.ReadDefinition(m.Entry.Id);
            step(StepState.Running, $"Importing symbols from {source.Name}");
            var def = definitions.Import(source.Name, source.Content);
            var ids = LibraryScanner.Merge(m.Entry.Identifiers, LibraryScanner.Identify(source.Name));
            var verified = definitions.Verify(def, image, ids);
            var res = verified.Result;
            log.Add($"{source.Name}: {res.Status} {res.Score} % ({res.MatchedMaps}/{res.TotalMaps} objects fit{(res.RelocatedMaps > 0 ? $", {res.RelocatedMaps} relocated" : "")})");
            step(StepState.Done, $"{source.Name}: {res.Status} {res.Score} %");
            return (cand, source, verified);
        }
        catch (Exception ex) when (ex is EcuStudioException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            log.Add($"{cand.FileName}: {ex.Message}");
            step(StepState.Failed, $"{cand.FileName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Fetches one file; returns null when it is on disk, else why it is not.</summary>
    private async Task<string?> DownloadAsync(DefinitionMatch m, LibraryRoot root, AcquisitionCandidate cand, List<string> log,
        Action<string, string, StepState, string?, double?, TransferInfo?> step, Action<TransferInfo> live, CancellationToken ct)
    {
        var library = definitions.Library;
        const string label = "Downloading";
        var torrent = library.TorrentBytes(root.Id);
        var dir = library.DownloadDirectory(root);
        if (torrent is null || dir is null || root.InfoHash is null)
        {
            log.Add($"{cand.FileName}: torrent source {root.Name} has no metadata or download directory");
            return $"{root.Name}: no torrent metadata or download directory";
        }
        var target = Path.Combine(dir, m.Entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (cache.Find(root.InfoHash, m.Entry.RelativePath) is not null || File.Exists(target) && new FileInfo(target).Length == m.Entry.Size)
        {
            var cached = new TransferInfo(cand.FileName, m.Entry.Size, m.Entry.Size, 0, 0, 0, 0, FromCache: true);
            step("downloading", label, StepState.Done, $"{cand.FileName} from cache", 1, cached);
            library.MarkDownloaded(root.Id, [m.Entry.RelativePath]);
            log.Add($"{cand.FileName}: taken from the definition cache");
            return null;
        }

        step("downloading", label, StepState.Running, $"Connecting to peers for {cand.FileName}", 0, null);
        var lastPublish = DateTime.MinValue;
        var progress = new SyncProgress<TorrentTransferProgress>(p =>
        {
            var info = new TransferInfo(cand.FileName, p.BytesDone, p.BytesTotal, p.BytesPerSecond, p.Peers, p.Seeds, p.Remaining?.TotalSeconds);
            live(info);
            if (p.Phase != TransferPhase.Done && (DateTime.UtcNow - lastPublish).TotalMilliseconds < 250) return;
            lastPublish = DateTime.UtcNow;
            var msg = p.Phase switch
            {
                TransferPhase.Metadata => "Fetching torrent metadata",
                TransferPhase.Connecting => $"Connecting to peers ({p.Peers})",
                _ => $"{cand.FileName} {p.BytesDone / 1048576.0:0.0} / {p.BytesTotal / 1048576.0:0.0} MB",
            };
            step("downloading", label, StepState.Running, msg, p.Fraction, info);
        });
        await _downloads.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(dir);
            await torrents.DownloadAsync(new TorrentDownloadRequest(torrent, dir, [m.Entry.RelativePath]), progress, ct);
        }
        catch (Exception ex) when (ex is EcuStudioException or IOException or TimeoutException or InvalidOperationException)
        {
            log.Add($"{cand.FileName}: download failed: {ex.Message}");
            step("downloading", label, StepState.Failed, $"{cand.FileName}: {ex.Message}", null, null);
            return $"{cand.FileName}: {ex.Message}";
        }
        finally { _downloads.Release(); }

        if (!File.Exists(target) || new FileInfo(target).Length != m.Entry.Size)
        {
            log.Add($"{cand.FileName}: downloaded file is missing or incomplete");
            step("downloading", label, StepState.Failed, $"{cand.FileName}: incomplete", null, null);
            return $"{cand.FileName}: downloaded file is missing or incomplete";
        }
        string sha;
        await using (var fs = File.OpenRead(target)) sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
        cache.Add(new CachedDefinitionFile { TorrentHash = root.InfoHash, FilePath = m.Entry.RelativePath, FileSize = m.Entry.Size, FileHash = sha, LocalPath = target });
        library.MarkDownloaded(root.Id, [m.Entry.RelativePath]);
        log.Add($"{cand.FileName}: downloaded ({m.Entry.Size / 1024} KB) from {root.Name}");
        step("downloading", label, StepState.Done, $"{cand.FileName} downloaded", 1, new TransferInfo(cand.FileName, m.Entry.Size, m.Entry.Size, 0, 0, 0, 0));
        return null;
    }

    private async Task FetchPendingMetadataAsync(List<string> log, Action<StepState, string?> step, CancellationToken ct)
    {
        var library = definitions.Library;
        var pending = library.Roots().Select(r => r.Root).Where(r => r.Enabled && r.MagnetUri is not null && library.TorrentBytes(r.Id) is null).ToList();
        foreach (var root in pending)
        {
            step(StepState.Running, $"Fetching torrent metadata for {root.Name}");
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                var meta = await torrents.FetchMetadataAsync(root.MagnetUri!, null, timeout.Token);
                library.SetTorrentMetadata(root.Id, meta);
                library.Scan(root.Id, ct);
                log.Add($"Metadata of {root.Name} fetched and indexed");
                step(StepState.Done, root.Name);
            }
            catch (Exception ex) when (ex is EcuStudioException or OperationCanceledException or IOException or InvalidOperationException)
            {
                log.Add($"Metadata of {root.Name} not available: {ex.Message}");
                step(StepState.Failed, $"{root.Name}: {ex.Message}");
            }
        }
    }

    private async Task<AnalysisReport?> TryReportAsync(Guid analysisId, CancellationToken ct)
    {
        var json = await store.GetAnalysisAsync(analysisId, ct);
        if (json is null) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<AnalysisReport>(json, Json.Options); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Candidates of the earlier heuristic analysis that are covered by a map of the definition now.</summary>
    public static List<ReconciledCandidate> Reconcile(AnalysisReport before, AnalysisReport after)
    {
        var list = new List<ReconciledCandidate>();
        foreach (var c in before.Candidates)
        {
            var map = after.Maps.FirstOrDefault(m => c.Address >= m.Address && c.Address < m.Address + Math.Max(2, m.Rows * m.Cols * 2));
            if (map is null) continue;
            list.Add(new ReconciledCandidate(c.Address, c.DisplayName, map.Id, map.Name, c.DecisionNote));
        }
        return list;
    }

    /// <summary>Edit history entries that referred to a candidate now point at the definition's map (bytes are unchanged).</summary>
    private async Task RelabelEditsAsync(Guid projectId, IReadOnlyList<ReconciledCandidate> reconciled, CancellationToken ct)
    {
        if (reconciled.Count == 0) return;
        var project = await store.GetAsync(projectId, ct);
        if (project is null || project.Edits.Count == 0) return;
        var changed = false;
        EditOperation Relabel(EditOperation op)
        {
            if (op.Patches.Count == 0) return op;
            var at = op.Patches[0].Address;
            var r = reconciled.FirstOrDefault(x => Math.Abs(x.Address - at) < 0x400 && op.MapId is { } id && (id.StartsWith("cand_", StringComparison.Ordinal) || id.StartsWith("scan_", StringComparison.Ordinal)));
            if (r is null) return op;
            changed = true;
            return op with { MapId = r.MapId, Description = $"{op.Description} (now {r.MapName})" };
        }
        var edits = project.Edits.Select(e => e with { History = e.History.Select(Relabel).ToList(), Redo = e.Redo.Select(Relabel).ToList() }).ToList();
        if (changed) await store.SaveAsync(project with { Edits = edits }, ct);
    }

    private async Task FinishAsync(Guid projectId, DefinitionAcquisition final)
    {
        final = final with { UpdatedAt = DateTimeOffset.UtcNow };
        // Live state first carries the outcome, so a status request never falls back to the previous stored one.
        _live[projectId] = final;
        if (await store.GetAsync(projectId) is { } p) await store.SaveAsync(p with { Acquisition = final });
        _live.TryRemove(projectId, out _);
    }

    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
