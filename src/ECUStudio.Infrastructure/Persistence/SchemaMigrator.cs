using System.Data;
using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace ECUStudio.Infrastructure.Persistence;

/// <summary>
/// Applies versioned, embedded SQL migrations (Migrations/{provider}/NNNN_name.sql) exactly once,
/// tracked in <c>schema_migrations</c>. Plain SQL keeps the PostgreSQL schema reviewable by a DBA and lets
/// the desktop build use the same mechanism on SQLite. PostgreSQL runs under an advisory lock so that several
/// server instances can start concurrently.
/// </summary>
public static class SchemaMigrator
{
    public sealed record Migration(int Version, string Name, string Sql);

    private const long AdvisoryLockKey = 0x45435553_54554449; // "ECUSTUDI"

    public static IReadOnlyList<Migration> Load(DatabaseProvider provider)
    {
        var folder = provider == DatabaseProvider.PostgreSql ? "postgres" : "sqlite";
        var asm = typeof(SchemaMigrator).Assembly;
        var prefix = $"ECUStudio.Infrastructure.Migrations.{folder}.";
        var list = new List<Migration>();
        foreach (var res in asm.GetManifestResourceNames())
        {
            if (!res.StartsWith(prefix, StringComparison.Ordinal) || !res.EndsWith(".sql", StringComparison.Ordinal)) continue;
            var file = res[prefix.Length..^4];
            var sep = file.IndexOf('_');
            if (sep <= 0 || !int.TryParse(file[..sep], out var version))
                throw new InvalidOperationException($"Migration resource '{res}' must be named NNNN_name.sql");
            list.Add(new Migration(version, file[(sep + 1)..], Read(asm, res)));
        }
        list.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 1; i < list.Count; i++)
            if (list[i].Version == list[i - 1].Version) throw new InvalidOperationException($"Duplicate migration version {list[i].Version}");
        return list;
    }

    /// <returns>Versions applied by this call.</returns>
    public static async Task<IReadOnlyList<int>> MigrateAsync(StudioDbContext db, CancellationToken ct = default)
    {
        var provider = db.IsPostgres ? DatabaseProvider.PostgreSql : DatabaseProvider.Sqlite;
        var migrations = Load(provider);
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != ConnectionState.Open;
        if (opened) await conn.OpenAsync(ct);
        var applied = new List<int>();
        try
        {
            if (provider == DatabaseProvider.PostgreSql) await ExecAsync(conn, null, $"SELECT pg_advisory_lock({AdvisoryLockKey})", ct);
            else await ExecAsync(conn, null, "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;", ct);
            try
            {
                await ExecAsync(conn, null, provider == DatabaseProvider.PostgreSql
                    ? "CREATE TABLE IF NOT EXISTS schema_migrations (version integer PRIMARY KEY, name text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())"
                    : "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP)", ct);

                var existing = new HashSet<int>();
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT version FROM schema_migrations";
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct)) existing.Add(r.GetInt32(0));
                }

                foreach (var m in migrations)
                {
                    if (existing.Contains(m.Version)) continue;
                    await using var tx = await conn.BeginTransactionAsync(ct);
                    await ExecAsync(conn, tx, m.Sql, ct);
                    await using (var ins = conn.CreateCommand())
                    {
                        ins.Transaction = tx;
                        ins.CommandText = "INSERT INTO schema_migrations (version, name) VALUES (@v, @n)";
                        AddParam(ins, "@v", m.Version);
                        AddParam(ins, "@n", m.Name);
                        await ins.ExecuteNonQueryAsync(ct);
                    }
                    await tx.CommitAsync(ct);
                    applied.Add(m.Version);
                }
            }
            finally
            {
                if (provider == DatabaseProvider.PostgreSql) await ExecAsync(conn, null, $"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
            }
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
        return applied;
    }

    private static async Task ExecAsync(DbConnection conn, DbTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static string Read(Assembly asm, string resource)
    {
        using var s = asm.GetManifestResourceStream(resource) ?? throw new InvalidOperationException($"Missing resource {resource}");
        using var reader = new StreamReader(s);
        return reader.ReadToEnd();
    }
}
