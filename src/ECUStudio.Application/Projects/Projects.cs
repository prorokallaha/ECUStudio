using System.Text.Json.Serialization;
using ECUStudio.Application.Library;
using ECUStudio.Components;
using ECUStudio.Core;

namespace ECUStudio.Application.Projects;

[JsonConverter(typeof(JsonStringEnumConverter<FileRole>))]
public enum FileRole { Stock, Modified, Version }

public sealed record ProjectFile
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required FileRole Role { get; init; }
    public required string Sha256 { get; init; }
    public required int Size { get; init; }
    public DateTimeOffset UploadedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? Notes { get; init; }
    /// <summary>Filled after an analysis against the project stock file.</summary>
    public VersionSummary? Summary { get; init; }
}

/// <summary>A diagnostic log stored with the project. <see cref="FileId"/> names the binary that was flashed while logging.</summary>
public sealed record ProjectLog
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Sha256 { get; init; }
    public required int Size { get; init; }
    public required string Format { get; init; }
    public required int Samples { get; init; }
    public required IReadOnlyList<string> Channels { get; init; }
    public Guid? FileId { get; init; }
    public DateTimeOffset UploadedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record VersionSummary(int ChangedMaps, Estimate? PeakPowerHp, Estimate? PeakTorqueNm, Severity Risk, double Confidence, Guid AnalysisId);

public sealed record Project
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Vin { get; init; }
    public string? PreferredVariantId { get; init; }
    public string? TransmissionId { get; init; }
    public IReadOnlyList<HardwareOverride> HardwareOverrides { get; init; } = [];
    public IReadOnlyList<ProjectFile> Files { get; init; } = [];
    public string? Notes { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Denormalised summary for the projects list.</summary>
    public ProjectHeadline? Headline { get; init; }
    public IReadOnlyList<ProjectLog> Logs { get; init; } = [];
    public int LogCount => Logs.Count;
    public int SimulationCount { get; init; }
    public Guid? LatestAnalysisId { get; init; }
    /// <summary>External definition bound by the user (uploaded or chosen from the library).</summary>
    public ProjectDefinition? Definition { get; init; }
    /// <summary>Patch histories of edited files (the stored files are never modified).</summary>
    public IReadOnlyList<Analysis.FileEdits> Edits { get; init; } = [];
    /// <summary>Last automatic definition search for this project.</summary>
    public Acquisition.DefinitionAcquisition? Acquisition { get; init; }

    [JsonIgnore] public ProjectFile? Stock => Files.FirstOrDefault(f => f.Role == FileRole.Stock);
}

public sealed record ProjectHeadline(string? Vehicle, string? EngineCode, string? Ecu, Severity? Risk);

/// <summary>Persistence port. Implemented by EF Core (PostgreSQL / SQLite) in Infrastructure, in-memory for tests.</summary>
public interface IProjectStore
{
    Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default);
    Task<Project?> GetAsync(Guid id, CancellationToken ct = default);
    Task SaveAsync(Project project, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task SaveFileContentAsync(Guid fileId, byte[] content, CancellationToken ct = default);
    Task<byte[]?> GetFileContentAsync(Guid fileId, CancellationToken ct = default);
    Task DeleteFileContentAsync(Guid fileId, CancellationToken ct = default);
    Task SaveAnalysisAsync(Guid analysisId, Guid projectId, string reportJson, CancellationToken ct = default);
    Task<string?> GetAnalysisAsync(Guid analysisId, CancellationToken ct = default);
    Task SaveCandidateDecisionAsync(CandidateDecision decision, CancellationToken ct = default);
    Task<IReadOnlyList<CandidateDecision>> GetCandidateDecisionsAsync(Guid projectId, CancellationToken ct = default);
}

public sealed record CandidateDecision(Guid ProjectId, string BinarySha256, int Address, string Decision, string? Role, string? Note, DateTimeOffset DecidedAt);

public sealed class InMemoryProjectStore : IProjectStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Project> _projects = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte[]> _files = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _analyses = new();
    private readonly System.Collections.Concurrent.ConcurrentBag<CandidateDecision> _decisions = new();

    public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Project>>(_projects.Values.OrderByDescending(p => p.UpdatedAt).ToList());
    public Task<Project?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_projects.GetValueOrDefault(id));
    public Task SaveAsync(Project project, CancellationToken ct = default) { _projects[project.Id] = project; return Task.CompletedTask; }
    public Task DeleteAsync(Guid id, CancellationToken ct = default) { _projects.TryRemove(id, out _); return Task.CompletedTask; }
    public Task SaveFileContentAsync(Guid fileId, byte[] content, CancellationToken ct = default) { _files[fileId] = content; return Task.CompletedTask; }
    public Task<byte[]?> GetFileContentAsync(Guid fileId, CancellationToken ct = default) => Task.FromResult(_files.GetValueOrDefault(fileId));
    public Task DeleteFileContentAsync(Guid fileId, CancellationToken ct = default) { _files.TryRemove(fileId, out _); return Task.CompletedTask; }
    public Task SaveAnalysisAsync(Guid analysisId, Guid projectId, string reportJson, CancellationToken ct = default) { _analyses[analysisId] = reportJson; return Task.CompletedTask; }
    public Task<string?> GetAnalysisAsync(Guid analysisId, CancellationToken ct = default) => Task.FromResult(_analyses.GetValueOrDefault(analysisId));
    public Task SaveCandidateDecisionAsync(CandidateDecision decision, CancellationToken ct = default) { _decisions.Add(decision); return Task.CompletedTask; }
    public Task<IReadOnlyList<CandidateDecision>> GetCandidateDecisionsAsync(Guid projectId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CandidateDecision>>(_decisions.Where(d => d.ProjectId == projectId).OrderBy(d => d.DecidedAt).ToList());
}
