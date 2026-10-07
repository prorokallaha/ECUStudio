using System.Text.Json;
using ECUStudio.Application.Analysis;
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

        // ---- AI ----
        a.MapPost("/ai", async (Guid analysisId, StudioService s, CancellationToken ct) => Results.Accepted(value: new JobStarted(await s.StartAIAnalysisAsync(analysisId, ct)))).WithTags("ai");
        a.MapGet("/ai", (Guid analysisId, StudioService s) => s.GetAIResult(analysisId) ?? throw new NotFoundException("No AI analysis for this analysis yet")).WithTags("ai");
        a.MapPost("/ai/ask", (Guid analysisId, AskBody b, StudioService s, CancellationToken ct) => s.AskAsync(analysisId, b.Question, b.Selection, ct)).WithTags("ai");
        a.MapPost("/candidates/{candidateId}/hypotheses", (Guid analysisId, string candidateId, StudioService s, CancellationToken ct) => s.CandidateHypothesesAsync(analysisId, candidateId, ct)).WithTags("ai");
        return api;
    }
}
