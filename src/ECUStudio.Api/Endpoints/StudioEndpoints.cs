using System.Text.Json;
using ECUStudio.Application.Analysis;
using ECUStudio.Application.Library;
using ECUStudio.Calibration.Library;
using ECUStudio.Application.Projects;
using ECUStudio.Binary;
using ECUStudio.Components;
using ECUStudio.Core;
using ECUStudio.Simulation;
using ECUStudio.Vehicle;
using Microsoft.AspNetCore.Mvc;

namespace ECUStudio.Api;

public sealed record CreateProjectBody(string Name, string? Vin, string? Notes);
public sealed record UpdateProjectBody(string? Name, string? Vin, string? Notes, string? PreferredVariantId, string? TransmissionId);
public sealed record FileRoleBody(FileRole Role);
public sealed record HardwareBody(IReadOnlyList<HardwareOverride> Overrides);
public sealed record StartAnalysisBody(Guid? ModifiedFileId, Guid? StockFileId);
public sealed record JobStarted(Guid JobId);
public sealed record WorkingHexDto(int Offset, int Length, int FileSize, string Working, string Original, IReadOnlyList<ByteRange> Changed);
public sealed record InvestigateBody(string? MapId, string? CandidateId, string? Language);
public sealed record AddLibraryRootBody(string Path, string? Name);
public sealed record AddTorrentPathBody(string Path, string? DownloadPath);
public sealed record UpdateLibraryRootBody(string? Name, string? DownloadPath);
public sealed record LibraryEntryBody(string EntryId, bool Force = false);
public sealed record InspectBody(double Rpm, double PedalPct, int Gear = 4, double AmbientTempC = 20, double AltitudeM = 0, double? AtmosphericPressureMbar = null, CoolantState Coolant = CoolantState.Normal);
public sealed record DecisionBody(string Decision, string? Role, string? Note);
public sealed record AskBody(string Question, JsonElement? Selection);
public sealed record PluginInfo(string Id, string Name, IReadOnlyList<string> Families, bool CommonRail);
public sealed record SystemInfo(string Version, bool AiConfigured, IReadOnlyList<PluginInfo> Plugins);
public sealed record JobState(Guid Id, JobStatus Status, IReadOnlyList<JobEvent> Events);
public sealed record HexPageDto(int Offset, int Length, int FileSize, string Modified, string? Stock, IReadOnlyList<HexRegion> Regions, IReadOnlyList<ByteRange> Changed);

/// <summary>
/// REST contract (prefix /api/v1). Hosts contain no business logic: every endpoint delegates to <see cref="StudioService"/>.
/// Errors are returned uniformly as {"error":{"code","message","details"}}.
/// </summary>
public static class StudioEndpoints
{
    public static RouteGroupBuilder MapStudioEndpoints(this RouteGroupBuilder api)
    {
        // ---- system / reference data ----
        api.MapGet("/info", (StudioService s) => new SystemInfo(AnalysisPipeline.AnalysisVersion, s.AIConfigured,
            s.Plugins.Plugins.Select(p => new PluginInfo(p.PluginId, p.DisplayName, p.EngineFamilies, p.HasCommonRail)).ToList())).WithTags("system");
        api.MapGet("/vin/{vin}", (string vin) => VinDecoder.Decode(vin)).WithTags("vehicle");
        api.MapGet("/components", (StudioService s, ComponentKind? kind) =>
            kind is { } k ? s.KnowledgeBase.Catalog.OfKind(k) : s.KnowledgeBase.Catalog.Items).WithTags("vehicle");
        api.MapGet("/variants", (StudioService s) => s.KnowledgeBase.Variants).WithTags("vehicle");

        // ---- projects ----
        var projects = api.MapGroup("/projects").WithTags("projects");
        projects.MapGet("/", (StudioService s, CancellationToken ct) => s.ListProjectsAsync(ct));
        projects.MapPost("/", async (CreateProjectBody b, StudioService s, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(b.Name)) throw new EcuStudioException("INVALID_NAME", "Project name is required");
            var p = await s.CreateProjectAsync(b.Name.Trim(), b.Vin, b.Notes, ct);
            return Results.Created($"/api/v1/projects/{p.Id}", p);
        });
        projects.MapPost("/demo", (StudioService s, CancellationToken ct) => s.CreateDemoProjectAsync(ct));
        projects.MapGet("/{id:guid}", (Guid id, StudioService s, CancellationToken ct) => s.GetProjectAsync(id, ct));
        projects.MapPatch("/{id:guid}", (Guid id, UpdateProjectBody b, StudioService s, CancellationToken ct) =>
            s.UpdateProjectAsync(id, b.Name, b.Vin, b.Notes, b.PreferredVariantId, b.TransmissionId, ct));
        projects.MapDelete("/{id:guid}", async (Guid id, StudioService s, CancellationToken ct) => { await s.DeleteProjectAsync(id, ct); return Results.NoContent(); });

        projects.MapPost("/{id:guid}/files", async (Guid id, IFormFile file, [FromForm] FileRole? role, [FromForm] string? label, [FromForm] string? notes, StudioService s, CancellationToken ct) =>
        {
            if (file.Length == 0) throw new InvalidBinaryException("Empty file");
            if (file.Length > ApiHost.MaxUploadBytes) throw new InvalidBinaryException($"File too large (max {ApiHost.MaxUploadBytes / 1024 / 1024} MB)");
            using var ms = new MemoryStream((int)file.Length);
            await file.CopyToAsync(ms, ct);
            var project = await s.GetProjectAsync(id, ct);
            var effectiveRole = role ?? (project.Stock is null ? FileRole.Stock : FileRole.Modified);
            return Results.Created($"/api/v1/projects/{id}", await s.AddFileAsync(id, Path.GetFileName(file.FileName), ms.ToArray(), effectiveRole, label, notes, ct));
        }).DisableAntiforgery();
        projects.MapPost("/{id:guid}/logs", async (Guid id, IFormFile file, [FromForm] Guid? fileId, StudioService s, CancellationToken ct) =>
        {
            if (file.Length > ApiHost.MaxUploadBytes) throw new EcuStudioException("LOG_FORMAT", $"Log too large (max {ApiHost.MaxUploadBytes / 1024 / 1024} MB)");
            using var ms = new MemoryStream((int)file.Length);
            await file.CopyToAsync(ms, ct);
            return Results.Created($"/api/v1/projects/{id}", await s.AddLogAsync(id, Path.GetFileName(file.FileName), ms.ToArray(), fileId, ct));
        }).DisableAntiforgery().WithTags("logs");
        projects.MapDelete("/{id:guid}/logs/{logId:guid}", (Guid id, Guid logId, StudioService s, CancellationToken ct) => s.DeleteLogAsync(id, logId, ct)).WithTags("logs");
        projects.MapPut("/{id:guid}/files/{fileId:guid}/role", (Guid id, Guid fileId, FileRoleBody b, StudioService s, CancellationToken ct) => s.SetFileRoleAsync(id, fileId, b.Role, ct));
        projects.MapPut("/{id:guid}/hardware", (Guid id, HardwareBody b, StudioService s, CancellationToken ct) => s.SetHardwareAsync(id, b.Overrides, ct));
        projects.MapDelete("/{id:guid}/hardware/{kind}", (Guid id, ComponentKind kind, StudioService s, CancellationToken ct) => s.ResetHardwareAsync(id, kind, ct));
        projects.MapPost("/{id:guid}/analyses", async (Guid id, StartAnalysisBody? b, StudioService s, CancellationToken ct) =>
            Results.Accepted(value: new JobStarted(await s.StartAnalysisAsync(id, new AnalysisStartOptions(b?.ModifiedFileId, b?.StockFileId), ct))));

        projects.MapPost("/{id:guid}/definition/preview", async (Guid id, IFormFile file, StudioService s, CancellationToken ct) =>
            await s.PreviewDefinitionAsync(id, Path.GetFileName(file.FileName), await ReadUpload(file, ct), ct)).DisableAntiforgery().WithTags("definitions");
        projects.MapPost("/{id:guid}/definition", async (Guid id, IFormFile file, [FromForm] bool? force, StudioService s, CancellationToken ct) =>
            await s.BindDefinitionAsync(id, Path.GetFileName(file.FileName), await ReadUpload(file, ct), force ?? false, ct)).DisableAntiforgery().WithTags("definitions");
        projects.MapPost("/{id:guid}/definition/library/preview", (Guid id, LibraryEntryBody b, StudioService s, CancellationToken ct) =>
            s.PreviewLibraryDefinitionAsync(id, b.EntryId, ct)).WithTags("definitions");
        projects.MapPost("/{id:guid}/definition/library", (Guid id, LibraryEntryBody b, StudioService s, CancellationToken ct) =>
            s.BindLibraryDefinitionAsync(id, b.EntryId, b.Force, ct)).WithTags("definitions");
        projects.MapPost("/{id:guid}/files/from-library", async (Guid id, AddFromLibraryBody b, StudioService s, CancellationToken ct) =>
            Results.Created($"/api/v1/projects/{id}", await s.AddFileFromLibraryAsync(id, b, ct))).WithTags("library");
        projects.MapDelete("/{id:guid}/definition", (Guid id, StudioService s, CancellationToken ct) => s.UnbindDefinitionAsync(id, ct)).WithTags("definitions");

        // ---- binary editing (patch model; stored files are never modified, saving creates a new file) ----
        var ed = projects.MapGroup("/{id:guid}/files/{fileId:guid}").WithTags("editing");
        ed.MapGet("/content", async (Guid id, Guid fileId, StudioService s, CancellationToken ct) =>
        {
            var (name, content) = await s.GetFileContentAsync(id, fileId, ct);
            return Results.File(content, "application/octet-stream", name);
        });
        ed.MapGet("/edits", (Guid id, Guid fileId, StudioService s, CancellationToken ct) => s.GetEditStateAsync(id, fileId, ct));
        ed.MapGet("/edits/hex", async (Guid id, Guid fileId, int? offset, int? length, StudioService s, CancellationToken ct) =>
        {
            var p = await s.GetWorkingHexAsync(id, fileId, offset ?? 0, length ?? 4096, ct);
            return new WorkingHexDto(p.Offset, p.Length, p.FileSize, Convert.ToBase64String(p.Working), Convert.ToBase64String(p.Original), p.Changed);
        });
        ed.MapPost("/edits/hex", (Guid id, Guid fileId, HexEdit b, StudioService s, CancellationToken ct) => s.ApplyHexEditAsync(id, fileId, b, ct));
        ed.MapPost("/edits/map/preview", (Guid id, Guid fileId, MapEditRequest b, StudioService s, CancellationToken ct) => s.PreviewMapEditAsync(id, fileId, b, ct));
        ed.MapPost("/edits/map", (Guid id, Guid fileId, MapEditRequest b, StudioService s, CancellationToken ct) => s.ApplyMapEditAsync(id, fileId, b, ct));
        ed.MapPost("/edits/undo", (Guid id, Guid fileId, StudioService s, CancellationToken ct) => s.UndoAsync(id, fileId, ct));
        ed.MapPost("/edits/redo", (Guid id, Guid fileId, StudioService s, CancellationToken ct) => s.RedoAsync(id, fileId, ct));
        ed.MapPost("/edits/revert", (Guid id, Guid fileId, RevertRequest b, StudioService s, CancellationToken ct) => s.RevertAsync(id, fileId, b, ct));
        ed.MapPost("/edits/save-check", (Guid id, Guid fileId, SaveRequest? b, StudioService s, CancellationToken ct) => s.CheckSaveAsync(id, fileId, b, ct));
        ed.MapPost("/edits/save", (Guid id, Guid fileId, SaveRequest b, StudioService s, CancellationToken ct) => s.SaveAsNewFileAsync(id, fileId, b, ct));

        // ---- definition library (local archive; metadata only, files stay where they are) ----
        var lib = api.MapGroup("/library").WithTags("library");
        lib.MapGet("/roots", (StudioService s) => s.Library.Roots());
        lib.MapPost("/roots", (AddLibraryRootBody b, StudioService s) => Results.Created("/api/v1/library/roots", s.Library.AddDirectory(b.Path, b.Name)));
        lib.MapPost("/torrents", async (IFormFile file, [FromForm] string? downloadPath, StudioService s, CancellationToken ct) =>
        {
            if (file.Length > TorrentMetadata.MaxTorrentBytes) throw new DefinitionException(".torrent file is larger than 256 MB");
            return Results.Created("/api/v1/library/roots", s.Library.AddTorrent(Path.GetFileName(file.FileName), await ReadUpload(file, ct), downloadPath));
        }).DisableAntiforgery();
        lib.MapPost("/torrents/path", (AddTorrentPathBody b, StudioService s) => Results.Created("/api/v1/library/roots", s.Library.AddTorrentFile(b.Path, b.DownloadPath)));
        lib.MapPatch("/roots/{rootId:guid}", (Guid rootId, UpdateLibraryRootBody b, StudioService s) => s.Library.UpdateRoot(rootId, b.Name, b.DownloadPath));
        lib.MapDelete("/roots/{rootId:guid}", (Guid rootId, StudioService s) => { s.Library.RemoveRoot(rootId); return Results.NoContent(); });
        lib.MapPost("/roots/{rootId:guid}/scan", (Guid rootId, StudioService s, ILoggerFactory logs) =>
        {
            if (s.Library.IsScanning(rootId)) throw new EcuStudioException("LIBRARY_BUSY", "This library location is already being scanned", 409);
            var log = logs.CreateLogger("ECUStudio.Library");
            _ = Task.Run(() =>
            {
                try { s.Library.Scan(rootId); }
                catch (Exception ex) { log.LogWarning(ex, "Library scan of {RootId} failed", rootId); }
            });
            return Results.Accepted();
        });
        lib.MapGet("/entries", (StudioService s, string? q, LibraryFormat? format, bool? definitionsOnly, int? offset, int? limit) =>
            s.Library.Search(q, format, definitionsOnly ?? false, offset ?? 0, limit ?? 100));

        // ---- jobs (progress over SSE) ----
        var jobs = api.MapGroup("/jobs").WithTags("jobs");
        jobs.MapGet("/{id:guid}", (Guid id, StudioService s) =>
            s.Jobs.Status(id) is { } st ? new JobState(id, st, s.Jobs.History(id)) : throw new NotFoundException($"Job {id} not found"));
        jobs.MapGet("/{id:guid}/events", (Guid id, StudioService s, CancellationToken ct) =>
        {
            if (s.Jobs.Status(id) is null) throw new NotFoundException($"Job {id} not found");
            return TypedResults.ServerSentEvents(s.Jobs.Subscribe(id, ct), eventType: "job");
        });

        // ---- analyses ----
        var a = api.MapGroup("/analyses/{analysisId:guid}").WithTags("analysis");
        a.MapGet("/", (Guid analysisId, StudioService s, CancellationToken ct) => s.GetReportAsync(analysisId, ct));
        a.MapGet("/report.md", async (Guid analysisId, StudioService s, CancellationToken ct) =>
            Results.Text(ECUStudio.Application.Reports.MarkdownReport.Render(await s.GetReportAsync(analysisId, ct)), "text/markdown; charset=utf-8"));
        a.MapGet("/maps/{mapId}", (Guid analysisId, string mapId, StudioService s, CancellationToken ct) => s.GetMapAsync(analysisId, mapId, ct));
        a.MapGet("/hex", async (Guid analysisId, int? offset, int? length, StudioService s, CancellationToken ct) =>
        {
            var p = await s.GetHexAsync(analysisId, offset ?? 0, length ?? 4096, ct);
            return new HexPageDto(p.Offset, p.Length, p.FileSize, Convert.ToBase64String(p.Modified), p.Stock is null ? null : Convert.ToBase64String(p.Stock), p.Regions, p.Changed);
        });
        a.MapGet("/search", (Guid analysisId, string mode, string q, DataType? type, Endianness? endian, StudioService s, CancellationToken ct) =>
            s.SearchAsync(analysisId, mode, q, type ?? DataType.UInt16, endian ?? Endianness.Big, ct));
        a.MapPost("/dyno", (Guid analysisId, DynoRequest b, StudioService s, CancellationToken ct) => { b.Validate(); return s.RunDynoAsync(analysisId, b, ct); });
        a.MapPost("/inspect", (Guid analysisId, InspectBody b, StudioService s, CancellationToken ct) =>
        {
            if (b.Rpm is < 500 or > 7000 || b.PedalPct is < 0 or > 100) throw new EcuStudioException("INVALID_POINT", "RPM must be 500–7000 and pedal 0–100 %");
            var env = new EnvironmentConditions { AmbientTempC = b.AmbientTempC, AltitudeM = b.AltitudeM, AtmosphericPressureMbar = b.AtmosphericPressureMbar, Coolant = b.Coolant };
            return s.InspectAsync(analysisId, new OperatingPoint(b.Rpm, b.PedalPct, b.Gear), env, ct);
        });
        a.MapPost("/candidates/{candidateId}/decision", async (Guid analysisId, string candidateId, DecisionBody b, StudioService s, CancellationToken ct) =>
        {
            await s.DecideCandidateAsync(analysisId, candidateId, b.Decision, b.Role, b.Note, ct);
            return Results.NoContent();
        });

        a.MapGet("/stock-candidates", (Guid analysisId, StudioService s, CancellationToken ct) => s.StockCandidatesAsync(analysisId, ct)).WithTags("library");
        a.MapGet("/definitions", (Guid analysisId, StudioService s, CancellationToken ct) => s.LibraryMatchesAsync(analysisId, ct)).WithTags("definitions");

        // ---- AI ----
        a.MapPost("/ai", async (Guid analysisId, StudioService s, CancellationToken ct) => Results.Accepted(value: new JobStarted(await s.StartAIAnalysisAsync(analysisId, ct)))).WithTags("ai");
        a.MapGet("/ai", (Guid analysisId, StudioService s) => s.GetAIResult(analysisId) ?? throw new NotFoundException("No AI analysis for this analysis yet")).WithTags("ai");
        a.MapPost("/ai/ask", (Guid analysisId, AskBody b, StudioService s, CancellationToken ct) => s.AskAsync(analysisId, b.Question, b.Selection, ct)).WithTags("ai");
        a.MapPost("/ai/investigate", (Guid analysisId, InvestigateBody b, StudioService s, CancellationToken ct) =>
        {
            if ((b.MapId is null) == (b.CandidateId is null)) throw new EcuStudioException("INVALID_TARGET", "Give exactly one of mapId or candidateId");
            return s.InvestigateMapAsync(analysisId, b.MapId, b.CandidateId, b.Language, ct);
        }).WithTags("ai");
        a.MapGet("/candidates/{candidateId}/data", (Guid analysisId, string candidateId, StudioService s, CancellationToken ct) => s.CandidateDataAsync(analysisId, candidateId, ct));
        a.MapPost("/candidates/{candidateId}/hypotheses", (Guid analysisId, string candidateId, StudioService s, CancellationToken ct) => s.CandidateHypothesesAsync(analysisId, candidateId, ct)).WithTags("ai");
        return api;
    }

    private static async Task<byte[]> ReadUpload(IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0) throw new DefinitionException("Empty file");
        if (file.Length > DefinitionLibrary.MaxImportBytes) throw new DefinitionException($"File too large (max {DefinitionLibrary.MaxImportBytes / 1024 / 1024} MB)");
        using var ms = new MemoryStream((int)file.Length);
        await file.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
