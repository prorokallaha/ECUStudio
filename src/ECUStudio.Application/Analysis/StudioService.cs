using ECUStudio.Simulation.Logs;
using System.Collections.Concurrent;
using System.Text.Json;
using ECUStudio.AI;
using ECUStudio.Application.DevTools;
using ECUStudio.Application.Library;
using ECUStudio.Calibration.Library;
using ECUStudio.Application.Projects;
using ECUStudio.Binary;
using ECUStudio.Calibration.Model;
using ECUStudio.Calibration.Plugins;
using ECUStudio.Calibration.Scanning;
using ECUStudio.Components;
using ECUStudio.Core;
using ECUStudio.Simulation;
using ECUStudio.Vehicle;

namespace ECUStudio.Application.Analysis;

public sealed record AnalysisStartOptions(Guid? ModifiedFileId = null, Guid? StockFileId = null);

public sealed record MapData(string Id, MapSummary Summary, double[] XAxis, double[] YAxis, double[] Values, double[]? StockValues, double[]? StockXAxis, double[]? StockYAxis, int[]? RawBytesAddress);

public sealed record HexRow(int Offset, string Modified, string? Stock);

public sealed record HexRegion(int Start, int End, string Kind, string Label, string? MapId);

public sealed record HexPage(int Offset, int Length, int FileSize, byte[] Modified, byte[]? Stock, IReadOnlyList<HexRegion> Regions, IReadOnlyList<ByteRange> Changed);

public sealed record PointInspection(PointResult Modified, PointResult? Stock);

public sealed record AIJobResult(Guid AnalysisId, AIAnalysisResult Result);

/// <summary>
/// Application facade shared by API, CLI and desktop host: projects, files, analyses (with progress),
/// interactive queries (maps, hex, dyno, inspector) and the AI layer. No business logic lives in the hosts.
/// </summary>
public sealed partial class StudioService(
    IProjectStore store,
    AnalysisPipeline pipeline,
    PluginRegistry plugins,
    VehicleKnowledgeBase kb,
    ISimulationEngine engine,
    JobTracker jobs,
    AIOrchestrator ai,
    IAIProvider aiProvider,
    DefinitionService definitions)
{
    private readonly ConcurrentDictionary<Guid, AnalysisSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, AIAnalysisResult> _aiResults = new();
    private readonly ConcurrentQueue<Guid> _sessionOrder = new();
    private const int MaxSessions = 16;

    public JobTracker Jobs => jobs;
    public PluginRegistry Plugins => plugins;
    public VehicleKnowledgeBase KnowledgeBase => kb;
    public bool AIConfigured => aiProvider.IsConfigured;
    public DefinitionLibrary Library => definitions.Library;

    // ---------------- projects -----------------------------------------------------------
    public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct = default) => store.ListAsync(ct);

    public async Task<Project> GetProjectAsync(Guid id, CancellationToken ct = default) =>
        await store.GetAsync(id, ct) ?? throw new NotFoundException($"Project {id} not found");

    public async Task<Project> CreateProjectAsync(string name, string? vin, string? notes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new EcuStudioException("INVALID_PROJECT", "Project name is required");
        if (!string.IsNullOrWhiteSpace(vin)) VinDecoder.Decode(vin);
        var p = new Project { Id = Guid.NewGuid(), Name = name.Trim(), Vin = vin?.Trim().ToUpperInvariant(), Notes = notes };
        await store.SaveAsync(p, ct);
        return p;
    }

    public async Task<Project> UpdateProjectAsync(Guid id, string? name, string? vin, string? notes, string? preferredVariantId, string? transmissionId, CancellationToken ct = default)
    {
        var p = await GetProjectAsync(id, ct);
        if (!string.IsNullOrWhiteSpace(vin)) VinDecoder.Decode(vin);
        p = p with
        {
            Name = string.IsNullOrWhiteSpace(name) ? p.Name : name.Trim(),
            Vin = vin is null ? p.Vin : vin.Trim().ToUpperInvariant(),
            Notes = notes ?? p.Notes,
            PreferredVariantId = preferredVariantId ?? p.PreferredVariantId,
            TransmissionId = transmissionId ?? p.TransmissionId,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await store.SaveAsync(p, ct);
        return p;
    }

    public Task DeleteProjectAsync(Guid id, CancellationToken ct = default) => store.DeleteAsync(id, ct);

    public async Task<ProjectFile> AddFileAsync(Guid projectId, string fileName, byte[] content, FileRole role, string? label, string? notes, CancellationToken ct = default)
    {
        var project = await GetProjectAsync(projectId, ct);
        using var image = BinaryImage.FromBytes(content, fileName);
        var (plugin, detection) = plugins.Detect(image);
        if (project.Files.Any(f => f.Sha256 == image.Sha256)) throw new EcuStudioException("DUPLICATE_FILE", "This exact binary is already in the project");
        if (role == FileRole.Stock && project.Stock is not null) role = FileRole.Version;
        var file = new ProjectFile
        {
            Id = Guid.NewGuid(), Name = fileName, Label = string.IsNullOrWhiteSpace(label) ? Path.GetFileNameWithoutExtension(fileName) : label!,
            Role = role, Sha256 = image.Sha256, Size = image.Length, Notes = notes ?? $"{plugin.DisplayName} (detection {detection.Score:0.00})",
        };
        await store.SaveFileContentAsync(file.Id, content, ct);
        await store.SaveAsync(project with { Files = [.. project.Files, file], UpdatedAt = DateTimeOffset.UtcNow }, ct);
        return file;
    }

    public async Task<ProjectLog> AddLogAsync(Guid projectId, string fileName, byte[] content, Guid? fileId, CancellationToken ct = default)
    {
        var project = await GetProjectAsync(projectId, ct);
        if (content.Length == 0) throw new LogFormatException("Log is empty");
        if (content.Length > LogParser.MaxBytes) throw new LogFormatException($"Log is larger than {LogParser.MaxBytes / 1024 / 1024} MB");
        if (fileId is { } fid && project.Files.All(f => f.Id != fid)) throw new NotFoundException($"File {fid} is not in this project");
        var sha = Hashing.Sha256Hex(content);
        if (project.Logs.Any(l => l.Sha256 == sha)) throw new EcuStudioException("DUPLICATE_FILE", "This exact log is already in the project");
        var parsed = LogParser.Parse(fileName, LogParser.Decode(content)); // validates before anything is stored
        var log = new ProjectLog
        {
            Id = Guid.NewGuid(), Name = fileName, Sha256 = sha, Size = content.Length, Format = parsed.Format, Samples = parsed.SampleCount,
            Channels = parsed.Mapping.Select(m => m.Channel.ToString()).ToList(), FileId = fileId, Warnings = parsed.Warnings,
        };
        await store.SaveFileContentAsync(log.Id, content, ct);
        await store.SaveAsync(project with { Logs = [.. project.Logs, log], UpdatedAt = DateTimeOffset.UtcNow }, ct);
        return log;
    }

    public async Task<Project> DeleteLogAsync(Guid projectId, Guid logId, CancellationToken ct = default)
    {
        var p = await GetProjectAsync(projectId, ct);
        if (p.Logs.All(l => l.Id != logId)) throw new NotFoundException($"Log {logId} not found");
        p = p with { Logs = p.Logs.Where(l => l.Id != logId).ToList(), UpdatedAt = DateTimeOffset.UtcNow };
        await store.SaveAsync(p, ct);
        await store.DeleteFileContentAsync(logId, ct);
        return p;
    }

    private async Task<List<LogInput>> LoadLogsAsync(Project project, ProjectFile modFile, ProjectFile? stockFile, CancellationToken ct)
    {
        var list = new List<LogInput>();
        foreach (var l in project.Logs)
        {
            // A log belongs to the binary that was flashed while logging; unassigned logs are compared with the analysed file.
            var againstStock = l.FileId is { } fid && fid == stockFile?.Id;
            if (l.FileId is { } f && f != modFile.Id && !againstStock) continue;
            var bytes = await store.GetFileContentAsync(l.Id, ct);
            if (bytes is null) continue;
            list.Add(new LogInput(l.Id.ToString(), LogParser.Parse(l.Name, LogParser.Decode(bytes)), againstStock));
        }
        return list;
    }

    public async Task<Project> SetFileRoleAsync(Guid projectId, Guid fileId, FileRole role, CancellationToken ct = default)
    {
        var p = await GetProjectAsync(projectId, ct);
        var files = p.Files.Select(f => f.Id == fileId ? f with { Role = role } : role == FileRole.Stock && f.Role == FileRole.Stock ? f with { Role = FileRole.Version } : f).ToList();
        p = p with { Files = files, UpdatedAt = DateTimeOffset.UtcNow };
        await store.SaveAsync(p, ct);
        return p;
    }

    public async Task<Project> SetHardwareAsync(Guid projectId, IReadOnlyList<HardwareOverride> overrides, CancellationToken ct = default)
    {
        var p = await GetProjectAsync(projectId, ct);
        foreach (var o in overrides)
            if (o.CatalogId is not null && kb.Catalog.Get(o.CatalogId) is null) throw new NotFoundException($"Component '{o.CatalogId}' not in catalog");
        var merged = p.HardwareOverrides.Where(h => overrides.All(o => o.Kind != h.Kind)).Concat(overrides.Where(o => o.CatalogId is not null || o.Custom is not null)).ToList();
        p = p with { HardwareOverrides = merged, UpdatedAt = DateTimeOffset.UtcNow };
        await store.SaveAsync(p, ct);
        return p;
    }

    public async Task<Project> ResetHardwareAsync(Guid projectId, ComponentKind kind, CancellationToken ct = default)
    {
        var p = await GetProjectAsync(projectId, ct);
        p = p with { HardwareOverrides = p.HardwareOverrides.Where(h => h.Kind != kind).ToList(), UpdatedAt = DateTimeOffset.UtcNow };
        await store.SaveAsync(p, ct);
        return p;
    }

    /// <summary>Demo project with SYNTHETIC stock + stage 1 files (clearly labelled).</summary>
    public async Task<Project> CreateDemoProjectAsync(CancellationToken ct = default)
    {
        var p = await CreateProjectAsync("DEMO · VW Golf V 1.9 TDI (synthetic data)", "WVWZZZ1KZ6W123456", "Synthetic EDC16U34 images generated for demonstration. Not a real vehicle calibration.", ct);
        await AddFileAsync(p.Id, "stock_synthetic.bin", SyntheticEdc16U34.Generate(SyntheticVariant.Stock).Image, FileRole.Stock, "Original", null, ct);
        await AddFileAsync(p.Id, "stage1_v1_synthetic.bin", SyntheticEdc16U34.Generate(SyntheticVariant.Stage1).Image, FileRole.Modified, "Stage1_v1", null, ct);
        await AddFileAsync(p.Id, "stage1_aggressive_synthetic.bin", SyntheticEdc16U34.Generate(SyntheticVariant.Stage1Aggressive).Image, FileRole.Version, "Stage1_aggressive", null, ct);

        // A synthetic full-load log "recorded" with Stage1_v1 flashed, so the Logs page has data to show.
        var project = await GetProjectAsync(p.Id, ct);
        var stage1 = project.Files.First(f => f.Label == "Stage1_v1");
        var session = await Task.Run(() => pipeline.Run(new AnalysisRequest
        {
            Modified = BinaryImage.FromBytes(SyntheticEdc16U34.Generate(SyntheticVariant.Stage1).Image, stage1.Name),
            Stock = BinaryImage.FromBytes(SyntheticEdc16U34.Generate(SyntheticVariant.Stock).Image, "stock_synthetic.bin"),
            Vin = project.Vin,
        }, null, ct), ct);
        var csv = SyntheticLog.GenerateVcdsCsv(session.ModInput, engine);
        await AddLogAsync(p.Id, "stage1_pull_3rd_gear_SYNTHETIC.csv", System.Text.Encoding.UTF8.GetBytes(csv), stage1.Id, ct);
        return await GetProjectAsync(p.Id, ct);
    }

    // ---------------- analyses -----------------------------------------------------------
    /// <summary>Starts an analysis job; progress is published on the job tracker. Returns the job id.</summary>
    public async Task<Guid> StartAnalysisAsync(Guid projectId, AnalysisStartOptions options, CancellationToken ct = default)
    {
        var project = await GetProjectAsync(projectId, ct);
        var modFile = (options.ModifiedFileId is { } mid ? project.Files.FirstOrDefault(f => f.Id == mid) : null)
            ?? project.Files.LastOrDefault(f => f.Role == FileRole.Modified)
            ?? project.Files.LastOrDefault(f => f.Role != FileRole.Stock)
            ?? project.Stock
            ?? throw new EcuStudioException("NO_FILES", "Project has no binaries");
        var stockFile = options.StockFileId is { } sid ? project.Files.FirstOrDefault(f => f.Id == sid) : project.Stock;
        if (stockFile?.Id == modFile.Id) stockFile = null;

        var jobId = jobs.Create();
        _ = Task.Run(async () =>
        {
            try
            {
                var session = await RunAnalysisAsync(project, modFile, stockFile, jobs.ProgressFor(jobId), CancellationToken.None);
                jobs.Publish(new JobEvent(jobId, "completed", null, JobStatus.Completed, null, session.Report.Id));
            }
            catch (Exception ex)
            {
                jobs.Publish(new JobEvent(jobId, "failed", null, JobStatus.Failed, ex is EcuStudioException ee ? $"{ee.Code}: {ee.Message}" : ex.Message));
            }
        }, CancellationToken.None);
        return jobId;
    }

    /// <summary>Synchronous analysis (CLI, tests, recompute after override).</summary>
    public async Task<AnalysisSession> RunAnalysisAsync(Project project, ProjectFile modFile, ProjectFile? stockFile, IProgress<StepProgress>? progress, CancellationToken ct)
    {
        var modBytes = await store.GetFileContentAsync(modFile.Id, ct) ?? throw new NotFoundException($"Content for file {modFile.Name} missing");
        var stockBytes = stockFile is null ? null : await store.GetFileContentAsync(stockFile.Id, ct);
        var decisions = await store.GetCandidateDecisionsAsync(project.Id, ct);
        var logs = await LoadLogsAsync(project, modFile, stockFile, ct);
        var modImage = BinaryImage.FromBytes(modBytes, modFile.Name);
        var confirmed = decisions.Where(d => d.BinarySha256 == modImage.Sha256 && d.Decision == "confirm" && Enum.TryParse<MapRole>(d.Role, out _))
            .GroupBy(d => d.Address).Select(g => g.Last()).Select(d => new ConfirmedCandidate(d.Address, Enum.Parse<MapRole>(d.Role!))).ToList();

        var projectDefinitionContent = project.Definition is { Origin: DefinitionOrigin.Upload } pd ? await store.GetFileContentAsync(pd.Id, ct) : null;
        var resolved = definitions.Resolve(project.Definition, _ => projectDefinitionContent, modImage);

        var session = await Task.Run(() => pipeline.Run(new AnalysisRequest
        {
            Modified = modImage,
            Stock = stockBytes is null ? null : BinaryImage.FromBytes(stockBytes, stockFile!.Name),
            Definition = resolved.Definition,
            DefinitionBinding = resolved.Binding,
            DefinitionNotes = resolved.Notes,
            Vin = project.Vin,
            Overrides = project.HardwareOverrides,
            PreferredVariantId = project.PreferredVariantId,
            TransmissionId = project.TransmissionId,
            ConfirmedCandidates = confirmed,
            Logs = logs,
            ProjectId = project.Id,
        }, progress, ct), ct);

        // Apply rejected decisions to the candidate list shown to the user.
        var rejected = decisions.Where(d => d.BinarySha256 == modImage.Sha256 && d.Decision == "reject").Select(d => d.Address).ToHashSet();
        if (rejected.Count > 0)
            session = session with { Report = session.Report with { Candidates = session.Report.Candidates.Select(c => rejected.Contains(c.Address) ? c with { Status = CandidateStatus.Rejected } : c).ToList() } };

        Remember(session);
        await store.SaveAnalysisAsync(session.Report.Id, project.Id, JsonSerializer.Serialize(session.Report, Json.Options), ct);

        var summary = new VersionSummary(session.Report.ModifiedMaps.Count,
            session.Report.KeyMetrics.FirstOrDefault(k => k.Id == "power")?.Modified,
            session.Report.KeyMetrics.FirstOrDefault(k => k.Id == "torque")?.Modified,
            session.Report.Risk.Overall, session.Report.Risk.Confidence, session.Report.Id);
        var fresh = await GetProjectAsync(project.Id, ct);
        await store.SaveAsync(fresh with
        {
            Files = fresh.Files.Select(f => f.Id == modFile.Id ? f with { Summary = summary } : f).ToList(),
            LatestAnalysisId = session.Report.Id,
            SimulationCount = fresh.SimulationCount + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            Headline = new ProjectHeadline(session.Report.Vehicle.Profile.Model.Text, session.Report.Vehicle.Profile.EngineCode.Text, session.Report.Ecu.EcuFamily, session.Report.Risk.Overall),
        }, ct);
        return session;
    }

    // ---------------- definitions ---------------------------------------------------------

    /// <summary>Binary a definition is checked against: the latest analysed file, else modified, else stock.</summary>
    private async Task<BinaryImage?> TargetBinaryAsync(Project project, CancellationToken ct)
    {
        var file = project.Files.FirstOrDefault(f => f.Summary?.AnalysisId == project.LatestAnalysisId && project.LatestAnalysisId is not null)
            ?? project.Files.LastOrDefault(f => f.Role == FileRole.Modified) ?? project.Stock ?? project.Files.LastOrDefault();
        if (file is null) return null;
        var bytes = await store.GetFileContentAsync(file.Id, ct);
        return bytes is null ? null : BinaryImage.FromBytes(bytes, file.Name);
    }

    public async Task<DefinitionPreview> PreviewDefinitionAsync(Guid projectId, string fileName, byte[] content, CancellationToken ct = default)
    {
        var project = await GetProjectAsync(projectId, ct);
        using var target = await TargetBinaryAsync(project, ct);
        return definitions.Preview(fileName, content, target);
    }

    public async Task<DefinitionPreview> PreviewLibraryDefinitionAsync(Guid projectId, string entryId, CancellationToken ct = default)
    {
        var (entry, content) = definitions.Library.Read(entryId);
        var preview = await PreviewDefinitionAsync(projectId, Path.GetFileName(entry.RelativePath), content, ct);
        return preview with { Name = entry.RelativePath };
    }

    /// <summary>Uploads a definition into the project. An incompatible definition needs <paramref name="force"/>.</summary>
    public async Task<Project> BindDefinitionAsync(Guid projectId, string fileName, byte[] content, bool force, CancellationToken ct = default)
    {
        var project = await GetProjectAsync(projectId, ct);
        using var target = await TargetBinaryAsync(project, ct);
        var preview = definitions.Preview(fileName, content, target);
        EnsureBindable(preview, force);
        var def = new ProjectDefinition
        {
            Id = Guid.NewGuid(), Name = fileName, Format = preview.Format, Origin = DefinitionOrigin.Upload,
            Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)), Size = content.Length,
            MapCount = preview.MapCount, Identifiers = preview.Identifiers, Compatibility = preview.Compatibility, CheckedAgainst = preview.Binary,
        };
        await store.SaveFileContentAsync(def.Id, content, ct);
        if (project.Definition is { Origin: DefinitionOrigin.Upload } old) await store.DeleteFileContentAsync(old.Id, ct);
        var updated = project with { Definition = def, UpdatedAt = DateTimeOffset.UtcNow };
        await store.SaveAsync(updated, ct);
        return updated;
    }

    /// <summary>Binds a library file by reference (the archive file is read in place at each analysis, never copied).</summary>
    public async Task<Project> BindLibraryDefinitionAsync(Guid projectId, string entryId, bool force, CancellationToken ct = default)
    {
        var project = await GetProjectAsync(projectId, ct);
        var (entry, content) = definitions.Library.Read(entryId);
        using var target = await TargetBinaryAsync(project, ct);
        var preview = definitions.Preview(Path.GetFileName(entry.RelativePath), content, target);
        EnsureBindable(preview, force);
        var def = new ProjectDefinition
        {
            Id = Guid.NewGuid(), Name = Path.GetFileName(entry.RelativePath), Format = entry.Format, Origin = DefinitionOrigin.Library, LibraryEntryId = entry.Id,
            Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)), Size = content.Length,
            MapCount = preview.MapCount, Identifiers = LibraryScanner.Merge(entry.Identifiers, preview.Identifiers), Compatibility = preview.Compatibility, CheckedAgainst = preview.Binary,
        };
        if (project.Definition is { Origin: DefinitionOrigin.Upload } old) await store.DeleteFileContentAsync(old.Id, ct);
        var updated = project with { Definition = def, UpdatedAt = DateTimeOffset.UtcNow };
        await store.SaveAsync(updated, ct);
        return updated;
    }

    public async Task<Project> UnbindDefinitionAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await GetProjectAsync(projectId, ct);
        if (project.Definition is { Origin: DefinitionOrigin.Upload } old) await store.DeleteFileContentAsync(old.Id, ct);
        var updated = project with { Definition = null, UpdatedAt = DateTimeOffset.UtcNow };
        await store.SaveAsync(updated, ct);
        return updated;
    }

    private static void EnsureBindable(DefinitionPreview preview, bool force)
    {
        if (preview.Error is { } error) throw new DefinitionException(error);
        if (preview.Compatibility is { Status: CompatibilityStatus.Incompatible } c && !force)
            throw new EcuStudioException("DEFINITION_INCOMPATIBLE", $"Definition does not fit the binary: {string.Join("; ", c.Reasons)}", 409,
                new Dictionary<string, object?> { ["compatibility"] = c });
    }

    /// <summary>Library entries matched against the analysed binary, best first, with reasons.</summary>
    public async Task<IReadOnlyList<DefinitionMatch>> LibraryMatchesAsync(Guid analysisId, CancellationToken ct = default)
    {
        var s = await GetSessionAsync(analysisId, ct);
        var ident = s.Report.Ecu;
        var engine = ECUStudio.Vehicle.Resolution.EcuIdentificationProvider.ParseEngineText(ident.EngineCode.Text) is { } e ? e.Litres : null;
        var key = new BinaryKey(ident.SoftwareNumber.IsKnown ? ident.SoftwareNumber.Text : null, ident.HardwareNumber.IsKnown ? ident.HardwareNumber.Text : null,
            ident.OemPartNumber.IsKnown ? ident.OemPartNumber.Text : null, ident.EcuFamily, s.Report.ModifiedSha256, engine);
        return definitions.Library.Match(key);
    }

    public async Task<AnalysisReport> GetReportAsync(Guid analysisId, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(analysisId, out var s)) return s.Report;
        var json = await store.GetAnalysisAsync(analysisId, ct) ?? throw new NotFoundException($"Analysis {analysisId} not found");
        return JsonSerializer.Deserialize<AnalysisReport>(json, Json.Options)!;
    }

    /// <summary>Interactive endpoints need the in-memory session; it is rebuilt from the project when evicted.</summary>
    public async Task<AnalysisSession> GetSessionAsync(Guid analysisId, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(analysisId, out var s)) return s;
        var report = await GetReportAsync(analysisId, ct);
        if (report.ProjectId is not { } pid) throw new NotFoundException("Analysis session expired and has no project to rebuild from");
        var project = await GetProjectAsync(pid, ct);
        var mod = project.Files.FirstOrDefault(f => f.Sha256 == report.ModifiedSha256) ?? throw new NotFoundException("Modified file no longer in project");
        var stock = report.StockSha256 is null ? null : project.Files.FirstOrDefault(f => f.Sha256 == report.StockSha256);
        var rebuilt = await RunAnalysisAsync(project, mod, stock, null, ct);
        _sessions[analysisId] = rebuilt with { Report = rebuilt.Report with { Id = analysisId } };
        return _sessions[analysisId];
    }

    private void Remember(AnalysisSession s)
    {
        _sessions[s.Report.Id] = s;
        _sessionOrder.Enqueue(s.Report.Id);
        while (_sessionOrder.Count > MaxSessions && _sessionOrder.TryDequeue(out var old)) _sessions.TryRemove(old, out _);
    }

    // ---------------- interactive queries --------------------------------------------------
    public async Task<MapData> GetMapAsync(Guid analysisId, string mapId, CancellationToken ct = default)
    {
        var s = await GetSessionAsync(analysisId, ct);
        var m = s.ModCalibration.Get(mapId) ?? throw new NotFoundException($"Map '{mapId}' not found");
        var st = s.StockCalibration?.Get(mapId);
        var summary = s.Report.Maps.First(x => x.Id == mapId);
        return new MapData(mapId, summary, m.XAxis, m.YAxis, m.Values, st?.Values, st?.XAxis, st?.YAxis, null);
    }

    public async Task<HexPage> GetHexAsync(Guid analysisId, int offset, int length, CancellationToken ct = default)
    {
        var s = await GetSessionAsync(analysisId, ct);
        var mod = s.Request.Modified;
        length = Math.Clamp(length, 16, 64 * 1024);
        offset = Math.Clamp(offset & ~0xF, 0, Math.Max(0, mod.Length - 16));
        length = Math.Min(length, mod.Length - offset);
        var modBytes = mod.Slice(offset, length).ToArray();
        var stockBytes = s.Request.Stock is { } st ? st.Slice(offset, length).ToArray() : null;
        var changed = stockBytes is null ? [] : BinaryDiff.ChangedRanges(stockBytes, modBytes, 0).Select(r => new ByteRange(r.Start + offset, r.Length)).ToList();
        var regions = new List<HexRegion>();
        foreach (var sec in s.Report.Ecu.Sections.Where(x => x.Start < offset + length && x.End > offset))
            regions.Add(new HexRegion(sec.Start, sec.End, "section", sec.Name, null));
        foreach (var m in s.ModCalibration.Maps)
            foreach (var r in Calibration.Analysis.MapDiffer.KnownRanges(m).Where(r => r.Start < offset + length && r.End > offset))
                regions.Add(new HexRegion(r.Start, r.End, r.Start == m.Definition.Address ? "map" : "axis", m.Definition.Name, m.Id));
        foreach (var c in s.Report.Candidates.Where(c => c.HeaderAddress < offset + length && c.Address + c.Rows * c.Cols * 2 > offset))
            regions.Add(new HexRegion(c.HeaderAddress, c.Address + c.Rows * c.Cols * 2, "candidate", c.Id, null));
        return new HexPage(offset, length, mod.Length, modBytes, stockBytes, regions, changed);
    }

    public async Task<List<int>> SearchAsync(Guid analysisId, string mode, string query, DataType type, Endianness endian, CancellationToken ct = default)
    {
        var s = await GetSessionAsync(analysisId, ct);
        var data = s.Request.Modified.Span;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return mode switch
        {
            "hex" => PatternSearch.FindHex(data, query, 500),
            "ascii" => PatternSearch.FindAscii(data, query, 500),
            "int" => PatternSearch.FindValue(data, double.Parse(query, inv), type, endian, 0, type.Size() > 1 ? 2 : 1, 500),
            "float" => PatternSearch.FindValue(data, double.Parse(query, inv), DataType.Float32, endian, Math.Abs(double.Parse(query, inv)) * 1e-4, 2, 500),
            _ => throw new EcuStudioException("INVALID_SEARCH", $"Unknown search mode '{mode}'"),
        };
    }

    public async Task<DynoResult> RunDynoAsync(Guid analysisId, DynoRequest request, CancellationToken ct = default)
    {
        var s = await GetSessionAsync(analysisId, ct);
        return new VirtualDyno(engine).Run(request, s.ModInput, s.StockInput);
    }

    public async Task<PointInspection> InspectAsync(Guid analysisId, OperatingPoint point, EnvironmentConditions env, CancellationToken ct = default)
    {
        var s = await GetSessionAsync(analysisId, ct);
        return new PointInspection(engine.Evaluate(s.ModInput, point, env, trace: true), s.StockInput is null ? null : engine.Evaluate(s.StockInput, point, env, trace: true));
    }

    public async Task DecideCandidateAsync(Guid analysisId, string candidateId, string decision, string? role, string? note, CancellationToken ct = default)
    {
        if (decision is not ("confirm" or "reject")) throw new EcuStudioException("INVALID_DECISION", "Decision must be 'confirm' or 'reject'");
        var s = await GetSessionAsync(analysisId, ct);
        var c = s.Report.Candidates.FirstOrDefault(x => x.Id == candidateId) ?? throw new NotFoundException($"Candidate {candidateId} not found");
        if (decision == "confirm" && !Enum.TryParse<MapRole>(role, out var r)) throw new EcuStudioException("INVALID_ROLE", $"Unknown role '{role}'");
        if (s.Report.ProjectId is not { } pid) throw new EcuStudioException("NO_PROJECT", "Candidate decisions are stored per project");
        await store.SaveCandidateDecisionAsync(new CandidateDecision(pid, s.Report.ModifiedSha256, c.Address, decision, role, note, DateTimeOffset.UtcNow), ct);
    }

    // ---------------- AI ---------------------------------------------------------------
    public async Task<Guid> StartAIAnalysisAsync(Guid analysisId, CancellationToken ct = default)
    {
        if (!aiProvider.IsConfigured) throw new AIUnavailableException("No AI provider configured. Set ANTHROPIC_API_KEY.");
        var s = await GetSessionAsync(analysisId, ct);
        var jobId = jobs.Create();
        _ = Task.Run(async () =>
        {
            try
            {
                var context = AIContextBuilder.Build(s);
                var result = await ai.AnalyzeAsync(context, jobs.ProgressFor(jobId));
                _aiResults[analysisId] = result;
                jobs.Publish(new JobEvent(jobId, "completed", null, JobStatus.Completed, null, analysisId));
            }
            catch (Exception ex)
            {
                jobs.Publish(new JobEvent(jobId, "failed", null, JobStatus.Failed, ex.Message));
            }
        }, CancellationToken.None);
        return jobId;
    }

    public AIAnalysisResult? GetAIResult(Guid analysisId) => _aiResults.GetValueOrDefault(analysisId);

    public async Task<AssistantAnswer> AskAsync(Guid analysisId, string question, JsonElement? selection, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new EcuStudioException("INVALID_QUESTION", "Question is empty");
        if (question.Length > 4000) throw new EcuStudioException("INVALID_QUESTION", "Question too long");
        var s = await GetSessionAsync(analysisId, ct);
        var selectionJson = selection?.GetRawText() ?? "{}";
        if (selectionJson.Length > 20000) throw new EcuStudioException("INVALID_SELECTION", "Selection context too large; select fewer cells");
        return await ai.AskAsync(AIContextBuilder.Build(s), question, selectionJson, ct);
    }

    public async Task<List<MapHypothesisResult>> CandidateHypothesesAsync(Guid analysisId, string candidateId, CancellationToken ct = default)
    {
        var s = await GetSessionAsync(analysisId, ct);
        var c = s.Report.Candidates.FirstOrDefault(x => x.Id == candidateId) ?? throw new NotFoundException($"Candidate {candidateId} not found");
        // Small raw excerpt (≤ 512 bytes) of the unknown region is allowed for this workflow.
        var len = Math.Min(512, 4 + 2 * (c.Rows + c.Cols + c.Rows * c.Cols));
        var raw = Convert.ToHexString(s.Request.Modified.Slice(c.HeaderAddress, len));
        var json = JsonSerializer.Serialize(new { c.Id, address = $"0x{c.Address:X6}", c.Rows, c.Cols, c.RawMin, c.RawMax, c.XAxisRaw, c.YAxisRaw, signature_hypotheses = c.Hypotheses, raw_hex_excerpt = raw }, Json.Options);
        return await ai.HypothesesAsync(AIContextBuilder.Build(s), json, Enum.GetNames<MapRole>(), ct);
    }
}
