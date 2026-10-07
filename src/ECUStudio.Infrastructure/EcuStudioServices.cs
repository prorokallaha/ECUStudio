using ECUStudio.AI;
using ECUStudio.Application.Acquisition;
using ECUStudio.Application.Analysis;
using ECUStudio.Application.DevTools;
using ECUStudio.Application.Library;
using ECUStudio.Application.Projects;
using ECUStudio.Calibration.Definitions;
using ECUStudio.Calibration.Plugins;
using ECUStudio.Calibration.Plugins.Edc16U34;
using ECUStudio.Infrastructure.Persistence;
using ECUStudio.Infrastructure.Torrents;
using ECUStudio.Simulation;
using ECUStudio.Vehicle;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ECUStudio.Infrastructure;

public sealed record EcuStudioOptions
{
    /// <summary>"memory" (tests / one-shot CLI), "sqlite" (desktop) or "postgres" (server).</summary>
    public string Storage { get; init; } = "sqlite";
    /// <summary>Npgsql connection string, or SQLite "Data Source=path".</summary>
    public string? ConnectionString { get; init; }
    /// <summary>Directory with *.ecudef.json definition files (Definition DB by SW number).</summary>
    public string? DefinitionsPath { get; init; }
    /// <summary>Registers the definition of the synthetic demo images (SW 1037399999). Harmless for real files.</summary>
    public bool IncludeDemoDefinitions { get; init; } = true;
    public string? AnthropicApiKey { get; init; } = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    public string AIModel { get; init; } = Environment.GetEnvironmentVariable("ECUSTUDIO_CLAUDE_MODEL") ?? "claude-opus-5-5";
    public int AIMaxParallel { get; init; } = 3;
    /// <summary>Directory for the definition library index (metadata only). Default: the application data directory.</summary>
    public string? LibraryPath { get; init; }
    /// <summary>When non-empty, only directories under these paths can be added to the library (server deployments).</summary>
    public IReadOnlyList<string> LibraryAllowedRoots { get; init; } = [];
    /// <summary>
    /// Directory that serves torrent files instead of the BitTorrent network (offline machines, tests):
    /// &lt;dir&gt;/&lt;path in torrent&gt;. Null: MonoTorrent.
    /// </summary>
    public string? TorrentMirrorPath { get; init; } = Environment.GetEnvironmentVariable("ECUSTUDIO_TORRENT_MIRROR");
    /// <summary>Pause per MB served by the mirror (to watch the progress display); 0 by default.</summary>
    public int TorrentMirrorDelayMs { get; init; } = int.TryParse(Environment.GetEnvironmentVariable("ECUSTUDIO_TORRENT_MIRROR_DELAY_MS"), out var ms) ? ms : 0;

    public static string DefaultSqlitePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ECUStudio");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "ecustudio.db");
    }

    public static string DefaultCachePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ECUStudio", "DefinitionCache");

    public static string DefaultLibraryPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ECUStudio", "library");
}

public static class EcuStudioServices
{
    /// <summary>Single composition root shared by API server, desktop host and CLI.</summary>
    public static IServiceCollection AddEcuStudio(this IServiceCollection services, EcuStudioOptions options)
    {
        services.AddSingleton(options);

        var definitions = new List<DefinitionFile>(new DefinitionDatabase(options.DefinitionsPath).Files);
        if (options.IncludeDemoDefinitions) definitions.Add(SyntheticEdc16U34.Generate(SyntheticVariant.Stock).Definition);
        var db = new DefinitionDatabase(definitions);
        services.AddSingleton(db);
        services.AddSingleton(new PluginRegistry([new Edc16U34Plugin(db)]));
        services.AddSingleton(_ => VehicleKnowledgeBase.LoadEmbedded());
        services.AddSingleton<ISimulationEngine, SimulationEngine>();
        services.AddSingleton<AnalysisPipeline>();
        services.AddSingleton<JobTracker>();

        IAIProvider provider = string.IsNullOrWhiteSpace(options.AnthropicApiKey)
            ? new UnconfiguredAIProvider()
            : new ClaudeProvider(new ClaudeOptions { ApiKey = options.AnthropicApiKey, Model = options.AIModel });
        services.AddSingleton(provider);

        switch (options.Storage.ToLowerInvariant())
        {
            case "memory":
                services.AddSingleton<IProjectStore, InMemoryProjectStore>();
                services.AddSingleton<IAICacheStore, InMemoryAICacheStore>();
                break;
            case "sqlite":
                var cs = options.ConnectionString ?? $"Data Source={EcuStudioOptions.DefaultSqlitePath()}";
                services.AddDbContextFactory<StudioDbContext>(o => o.UseSqlite(cs));
                services.AddSingleton<IProjectStore, EfProjectStore>();
                services.AddSingleton<IAICacheStore, EfAICacheStore>();
                break;
            case "postgres":
            case "postgresql":
                if (string.IsNullOrWhiteSpace(options.ConnectionString))
                    throw new InvalidOperationException("Storage 'postgres' requires a connection string (ConnectionStrings:Postgres or ECUSTUDIO_CONNECTION).");
                services.AddDbContextFactory<StudioDbContext>(o => o.UseNpgsql(options.ConnectionString));
                services.AddSingleton<IProjectStore, EfProjectStore>();
                services.AddSingleton<IAICacheStore, EfAICacheStore>();
                break;
            default:
                throw new InvalidOperationException($"Unknown storage '{options.Storage}'. Use memory, sqlite or postgres.");
        }

        services.AddSingleton(sp => new AIOrchestrator(sp.GetRequiredService<IAIProvider>(), sp.GetRequiredService<IAICacheStore>(), options.AIModel, options.AIMaxParallel));
        services.AddSingleton<IDefinitionLibraryStore>(options.Storage.Equals("memory", StringComparison.OrdinalIgnoreCase) && options.LibraryPath is null
            ? new InMemoryLibraryStore()
            : new JsonFileLibraryStore(options.LibraryPath ?? EcuStudioOptions.DefaultLibraryPath()));
        services.AddSingleton<IMapKnowledgeStore>(options.Storage.Equals("memory", StringComparison.OrdinalIgnoreCase) && options.LibraryPath is null
            ? new InMemoryMapKnowledgeStore()
            : new JsonFileMapKnowledgeStore(options.LibraryPath ?? EcuStudioOptions.DefaultLibraryPath()));
        services.AddSingleton(new LibraryPolicy(options.LibraryAllowedRoots));
        var inMemoryLibrary = options.Storage.Equals("memory", StringComparison.OrdinalIgnoreCase) && options.LibraryPath is null;
        IAcquisitionStore acquisitionStore = inMemoryLibrary ? new InMemoryAcquisitionStore() : new JsonFileAcquisitionStore(options.LibraryPath ?? EcuStudioOptions.DefaultLibraryPath());
        services.AddSingleton(acquisitionStore);
        var cachePath = acquisitionStore.LoadSettings().CachePath is { Length: > 0 } configured ? configured
            : inMemoryLibrary ? Path.Combine(Path.GetTempPath(), "ecustudio-definition-cache", Guid.NewGuid().ToString("N"))
            : options.LibraryPath is { } lp ? Path.Combine(lp, "DefinitionCache") : EcuStudioOptions.DefaultCachePath();
        services.AddSingleton(new LibraryCacheLocation(cachePath));
        services.AddSingleton<DefinitionLibrary>();
        services.AddSingleton<DefinitionService>();
        services.AddSingleton<DefinitionCache>();
        if (!string.IsNullOrWhiteSpace(options.TorrentMirrorPath))
            services.AddSingleton<ITorrentClient>(new LocalMirrorTorrentClient(options.TorrentMirrorPath, chunkDelay: TimeSpan.FromMilliseconds(options.TorrentMirrorDelayMs)));
        else
            services.AddSingleton<ITorrentClient>(_ => new MonoTorrentClient(new MonoTorrentOptions
            {
                StateDirectory = Path.Combine(options.LibraryPath ?? EcuStudioOptions.DefaultLibraryPath(), "torrent-state"),
            }));
        services.AddSingleton(sp =>
        {
            var studio = ActivatorUtilities.CreateInstance<StudioService>(sp);
            // A finished analysis without definition maps starts the automatic search (the service decides whether it may).
            studio.AnalysisCompleted = async (projectId, file, session) =>
            {
                if (session.Report.DefinitionSource != ECUStudio.Calibration.Plugins.DefinitionResolution.ScanOnly) return;
                var acquisition = sp.GetRequiredService<DefinitionAcquisitionService>();
                if (await sp.GetRequiredService<IProjectStore>().GetAsync(projectId) is { } project)
                    await acquisition.MaybeStartAfterAnalysisAsync(project, file, file.Sha256);
            };
            return studio;
        });
        services.AddSingleton(sp =>
        {
            var acquisition = ActivatorUtilities.CreateInstance<DefinitionAcquisitionService>(sp);
            var studio = sp.GetRequiredService<StudioService>();
            acquisition.Reanalyze = async (projectId, ct) => await studio.ReanalyzeAsync(projectId, ct);
            return acquisition;
        });
        return services;
    }

    /// <summary>Applies pending schema migrations (no-op for in-memory storage).</summary>
    public static async Task InitializeEcuStudioAsync(this IServiceProvider sp, CancellationToken ct = default)
    {
        var factory = sp.GetService<IDbContextFactory<StudioDbContext>>();
        if (factory is null) return;
        await using var db = await factory.CreateDbContextAsync(ct);
        await SchemaMigrator.MigrateAsync(db, ct);
    }
}
