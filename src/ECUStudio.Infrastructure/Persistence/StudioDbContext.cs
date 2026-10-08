using Microsoft.EntityFrameworkCore;

namespace ECUStudio.Infrastructure.Persistence;

public enum DatabaseProvider { Sqlite, PostgreSql }

public sealed class ProjectRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Vin { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string Document { get; set; } = "{}";
}

public sealed class FileContentRow
{
    public Guid FileId { get; set; }
    public string Sha256 { get; set; } = "";
    public int Size { get; set; }
    public byte[] Content { get; set; } = [];
}

public sealed class AnalysisRow
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string AnalysisVersion { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Report { get; set; } = "{}";
}

public sealed class CandidateDecisionRow
{
    public long Id { get; set; }
    public Guid ProjectId { get; set; }
    public string BinarySha256 { get; set; } = "";
    public int Address { get; set; }
    public string Decision { get; set; } = "";
    public string? Role { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
}

public sealed class AICacheRow
{
    public string Key { get; set; } = "";
    public string Agent { get; set; } = "";
    public string Model { get; set; } = "";
    public string Response { get; set; } = "{}";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// EF Core model over the schema created by <see cref="SchemaMigrator"/> (SQL scripts are the source of truth,
/// so PostgreSQL and SQLite share one model and one set of versioned migrations per provider).
/// </summary>
public sealed class StudioDbContext(DbContextOptions<StudioDbContext> options) : DbContext(options)
{
    public DbSet<ProjectRow> Projects => Set<ProjectRow>();
    public DbSet<FileContentRow> FileContents => Set<FileContentRow>();
    public DbSet<AnalysisRow> Analyses => Set<AnalysisRow>();
    public DbSet<CandidateDecisionRow> CandidateDecisions => Set<CandidateDecisionRow>();
    public DbSet<AICacheRow> AICache => Set<AICacheRow>();

    public bool IsPostgres => Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;

    protected override void OnModelCreating(ModelBuilder b)
    {
        var json = IsPostgres ? "jsonb" : "TEXT";

        b.Entity<ProjectRow>(e =>
        {
            e.ToTable("projects");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Vin).HasColumnName("vin").HasMaxLength(17);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Document).HasColumnName("document").HasColumnType(json);
        });
        b.Entity<FileContentRow>(e =>
        {
            e.ToTable("file_contents");
            e.HasKey(x => x.FileId);
            e.Property(x => x.FileId).HasColumnName("file_id");
            e.Property(x => x.Sha256).HasColumnName("sha256");
            e.Property(x => x.Size).HasColumnName("size");
            e.Property(x => x.Content).HasColumnName("content");
        });
        b.Entity<AnalysisRow>(e =>
        {
            e.ToTable("analyses");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.AnalysisVersion).HasColumnName("analysis_version");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.Report).HasColumnName("report").HasColumnType(json);
        });
        b.Entity<CandidateDecisionRow>(e =>
        {
            e.ToTable("candidate_decisions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.BinarySha256).HasColumnName("binary_sha256");
            e.Property(x => x.Address).HasColumnName("address");
            e.Property(x => x.Decision).HasColumnName("decision");
            e.Property(x => x.Role).HasColumnName("role");
            e.Property(x => x.Note).HasColumnName("note");
            e.Property(x => x.DecidedAt).HasColumnName("decided_at");
        });
        b.Entity<AICacheRow>(e =>
        {
            e.ToTable("ai_cache");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasColumnName("key");
            e.Property(x => x.Agent).HasColumnName("agent");
            e.Property(x => x.Model).HasColumnName("model");
            e.Property(x => x.Response).HasColumnName("response").HasColumnType(json);
            e.Property(x => x.InputTokens).HasColumnName("input_tokens");
            e.Property(x => x.OutputTokens).HasColumnName("output_tokens");
            e.Property(x => x.CacheReadTokens).HasColumnName("cache_read_tokens");
            e.Property(x => x.CacheWriteTokens).HasColumnName("cache_write_tokens");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });
    }
}
