using System.Collections.Concurrent;
using System.Text.Json;
using ECUStudio.Core;

namespace ECUStudio.Application.Acquisition;

/// <summary>User settings of automatic definition acquisition.</summary>
public sealed record AcquisitionSettings
{
    /// <summary>Search and fetch definitions after an analysis that found none.</summary>
    public bool AutoAcquire { get; init; } = true;
    /// <summary>Download exact / very high / high matches without asking (they are still verified before use).</summary>
    public bool AutoDownloadExact { get; init; } = true;
    /// <summary>Also download and test medium (probable) matches without asking.</summary>
    public bool AutoTestProbable { get; init; }
    public int MaxConcurrentDownloads { get; init; } = 2;
    /// <summary>Where downloaded definition files are kept. Null: the application data directory.</summary>
    public string? CachePath { get; init; }
}

/// <summary>A file fetched from a torrent source. Fetched files are reused, never downloaded twice.</summary>
public sealed record CachedDefinitionFile
{
    public required string TorrentHash { get; init; }
    /// <summary>Path inside the torrent (as listed in its metadata).</summary>
    public required string FilePath { get; init; }
    public long FileSize { get; init; }
    public string? FileHash { get; init; }
    public DateTimeOffset DownloadedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Project definition created from this file, when it was imported.</summary>
    public Guid? DefinitionId { get; init; }
    public string? LocalPath { get; init; }
}

public interface IAcquisitionStore
{
    AcquisitionSettings LoadSettings();
    void SaveSettings(AcquisitionSettings settings);
    IReadOnlyList<CachedDefinitionFile> LoadCache();
    void SaveCache(IReadOnlyList<CachedDefinitionFile> entries);
}

public sealed class InMemoryAcquisitionStore : IAcquisitionStore
{
    private AcquisitionSettings _settings = new();
    private IReadOnlyList<CachedDefinitionFile> _cache = [];
    public AcquisitionSettings LoadSettings() => _settings;
    public void SaveSettings(AcquisitionSettings settings) => _settings = settings;
    public IReadOnlyList<CachedDefinitionFile> LoadCache() => _cache;
    public void SaveCache(IReadOnlyList<CachedDefinitionFile> entries) => _cache = entries;
}

/// <summary>Settings and cache metadata as JSON files in the application data directory.</summary>
public sealed class JsonFileAcquisitionStore(string directory) : IAcquisitionStore
{
    private readonly object _lock = new();
    private string SettingsPath => Path.Combine(directory, "acquisition-settings.json");
    private string CachePath => Path.Combine(directory, "definition-cache.json");

    public AcquisitionSettings LoadSettings() => Read<AcquisitionSettings>(SettingsPath) ?? new();
    public void SaveSettings(AcquisitionSettings settings) => Write(SettingsPath, settings);
    public IReadOnlyList<CachedDefinitionFile> LoadCache() => Read<List<CachedDefinitionFile>>(CachePath) ?? [];
    public void SaveCache(IReadOnlyList<CachedDefinitionFile> entries) => Write(CachePath, entries);

    private T? Read<T>(string path) where T : class
    {
        lock (_lock)
        {
            if (!File.Exists(path)) return null;
            try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json.Options); }
            catch (JsonException) { return null; }
        }
    }

    private void Write<T>(string path, T value)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(directory);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json.Options));
            File.Move(tmp, path, overwrite: true);
        }
    }
}

/// <summary>Index of fetched definition files (keyed by torrent hash + path).</summary>
public sealed class DefinitionCache(IAcquisitionStore store)
{
    private readonly ConcurrentDictionary<(string, string), CachedDefinitionFile> _entries =
        new(store.LoadCache().ToDictionary(e => (e.TorrentHash, e.FilePath)));

    public IReadOnlyList<CachedDefinitionFile> Entries => _entries.Values.OrderByDescending(e => e.DownloadedAt).ToList();

    /// <summary>The cached file if it is still on disk with the recorded size.</summary>
    public CachedDefinitionFile? Find(string torrentHash, string filePath) =>
        _entries.TryGetValue((torrentHash, filePath), out var e) && e.LocalPath is { } p && File.Exists(p) && new FileInfo(p).Length == e.FileSize ? e : null;

    public void Add(CachedDefinitionFile entry)
    {
        _entries[(entry.TorrentHash, entry.FilePath)] = entry;
        store.SaveCache(Entries);
    }

    public void LinkDefinition(string torrentHash, string filePath, Guid definitionId)
    {
        if (!_entries.TryGetValue((torrentHash, filePath), out var e)) return;
        _entries[(torrentHash, filePath)] = e with { DefinitionId = definitionId };
        store.SaveCache(Entries);
    }
}
