using System.Text.Json;
using ECUStudio.AI;
using ECUStudio.Application.Analysis;
using ECUStudio.Application.Projects;
using ECUStudio.Core;
using Microsoft.EntityFrameworkCore;

namespace ECUStudio.Infrastructure.Persistence;

/// <summary>
/// Project store over PostgreSQL or SQLite. The project aggregate is a JSON document (it is always loaded and
/// saved as a whole); binaries, analyses and decisions live in their own tables.
/// </summary>
public sealed class EfProjectStore(IDbContextFactory<StudioDbContext> factory) : IProjectStore
{
    public async Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var docs = await db.Projects.AsNoTracking().Select(p => p.Document).ToListAsync(ct);
        // Ordered client-side: SQLite cannot ORDER BY DateTimeOffset.
        return docs.Select(Deserialize).OrderByDescending(p => p.UpdatedAt).ToList();
    }

    public async Task<Project?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var doc = await db.Projects.AsNoTracking().Where(p => p.Id == id).Select(p => p.Document).FirstOrDefaultAsync(ct);
        return doc is null ? null : Deserialize(doc);
    }

    public async Task SaveAsync(Project project, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Projects.FirstOrDefaultAsync(p => p.Id == project.Id, ct);
        if (row is null)
        {
            row = new ProjectRow { Id = project.Id, CreatedAt = project.CreatedAt.ToUniversalTime() };
            db.Projects.Add(row);
        }
        row.Name = project.Name;
        row.Vin = project.Vin;
        row.UpdatedAt = project.UpdatedAt.ToUniversalTime();
        row.Document = JsonSerializer.Serialize(project, Json.Options);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (row is null) return;
        var fileIds = Deserialize(row.Document).Files.Select(f => f.Id).ToList();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.CandidateDecisions.Where(d => d.ProjectId == id).ExecuteDeleteAsync(ct);
        await db.Analyses.Where(a => a.ProjectId == id).ExecuteDeleteAsync(ct);
        if (fileIds.Count > 0) await db.FileContents.Where(f => fileIds.Contains(f.FileId)).ExecuteDeleteAsync(ct);
        db.Projects.Remove(row);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task SaveFileContentAsync(Guid fileId, byte[] content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0) throw new InvalidBinaryException("Empty binary");
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.FileContents.FirstOrDefaultAsync(f => f.FileId == fileId, ct);
        if (row is null) db.FileContents.Add(row = new FileContentRow { FileId = fileId });
        row.Content = content;
        row.Size = content.Length;
        row.Sha256 = Hashing.Sha256Hex(content);
        await db.SaveChangesAsync(ct);
    }

    public async Task<byte[]?> GetFileContentAsync(Guid fileId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.FileContents.AsNoTracking().Where(f => f.FileId == fileId).Select(f => f.Content).FirstOrDefaultAsync(ct);
    }

    public async Task SaveAnalysisAsync(Guid analysisId, Guid projectId, string reportJson, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Analyses.FirstOrDefaultAsync(a => a.Id == analysisId, ct);
        if (row is null) db.Analyses.Add(row = new AnalysisRow { Id = analysisId, ProjectId = projectId, CreatedAt = DateTimeOffset.UtcNow });
        row.AnalysisVersion = AnalysisPipeline.AnalysisVersion;
        row.Report = reportJson;
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> GetAnalysisAsync(Guid analysisId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Analyses.AsNoTracking().Where(a => a.Id == analysisId).Select(a => a.Report).FirstOrDefaultAsync(ct);
    }

    public async Task SaveCandidateDecisionAsync(CandidateDecision d, CancellationToken ct = default)
    {
        if (d.Decision is not ("confirm" or "reject")) throw new EcuStudioException("INVALID_DECISION", "Decision must be 'confirm' or 'reject'", 400);
        await using var db = await factory.CreateDbContextAsync(ct);
        db.CandidateDecisions.Add(new CandidateDecisionRow
        {
            ProjectId = d.ProjectId, BinarySha256 = d.BinarySha256, Address = d.Address, Decision = d.Decision,
            Role = d.Role, Note = d.Note, DecidedAt = d.DecidedAt.ToUniversalTime(),
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<CandidateDecision>> GetCandidateDecisionsAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.CandidateDecisions.AsNoTracking().Where(d => d.ProjectId == projectId).OrderBy(d => d.Id).ToListAsync(ct);
        return rows.Select(r => new CandidateDecision(r.ProjectId, r.BinarySha256, r.Address, r.Decision, r.Role, r.Note, r.DecidedAt)).ToList();
    }

    private static Project Deserialize(string doc) =>
        JsonSerializer.Deserialize<Project>(doc, Json.Options) ?? throw new InvalidOperationException("Corrupt project document");
}

public sealed class EfAICacheStore(IDbContextFactory<StudioDbContext> factory) : IAICacheStore
{
    public async Task<AICacheEntry?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.AICache.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
        return r is null ? null : new AICacheEntry(r.Key, r.Agent, r.Model, r.Response,
            new AIUsage(r.InputTokens, r.OutputTokens, r.CacheReadTokens, r.CacheWriteTokens), r.CreatedAt);
    }

    public async Task PutAsync(AICacheEntry e, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.AICache.FirstOrDefaultAsync(x => x.Key == e.Key, ct);
        if (r is null) db.AICache.Add(r = new AICacheRow { Key = e.Key });
        r.Agent = e.Agent;
        r.Model = e.Model;
        r.Response = e.Json;
        r.InputTokens = e.Usage.InputTokens;
        r.OutputTokens = e.Usage.OutputTokens;
        r.CacheReadTokens = e.Usage.CacheReadTokens;
        r.CacheWriteTokens = e.Usage.CacheWriteTokens;
        r.CreatedAt = e.CreatedAt.ToUniversalTime();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { /* concurrent insert of the same key: the cached value is equivalent */ }
    }
}
