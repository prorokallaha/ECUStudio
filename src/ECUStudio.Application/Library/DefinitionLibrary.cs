using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using ECUStudio.Calibration.Library;
using ECUStudio.Core;

namespace ECUStudio.Application.Library;

/// <summary>Persisted index: registered roots and the metadata of their files. Never the files themselves.</summary>
public sealed record LibraryIndex(IReadOnlyList<LibraryRoot> Roots, IReadOnlyList<LibraryEntry> Entries)
{
    public static readonly LibraryIndex Empty = new([], []);
}

public interface IDefinitionLibraryStore
{
    LibraryIndex Load();
    void Save(LibraryIndex index);
    /// <summary>Keeps a copy of a .torrent metainfo file (kilobytes; the payload is never stored).</summary>
    void SaveTorrent(Guid rootId, byte[] torrent);
    byte[]? LoadTorrent(Guid rootId);
    void DeleteTorrent(Guid rootId);
}

public sealed class InMemoryLibraryStore : IDefinitionLibraryStore
{
    private LibraryIndex _index = LibraryIndex.Empty;
    private readonly ConcurrentDictionary<Guid, byte[]> _torrents = new();
    public LibraryIndex Load() => _index;
    public void Save(LibraryIndex index) => _index = index;
    public void SaveTorrent(Guid rootId, byte[] torrent) => _torrents[rootId] = torrent;
    public byte[]? LoadTorrent(Guid rootId) => _torrents.GetValueOrDefault(rootId);
    public void DeleteTorrent(Guid rootId) => _torrents.TryRemove(rootId, out _);
}

/// <summary>Index as one JSON file in the application data directory (written atomically via a temp file).</summary>
public sealed class JsonFileLibraryStore(string directory) : IDefinitionLibraryStore
{
    private string IndexPath => Path.Combine(directory, "library-index.json");
    private string TorrentPath(Guid id) => Path.Combine(directory, "torrents", $"{id:N}.torrent");

    public LibraryIndex Load()
    {
        if (!File.Exists(IndexPath)) return LibraryIndex.Empty;
        try { return JsonSerializer.Deserialize<LibraryIndex>(File.ReadAllText(IndexPath), Json.Options) ?? LibraryIndex.Empty; }
        catch (JsonException) { return LibraryIndex.Empty; } // a corrupt index is rebuilt by rescanning, the archive is untouched
    }

    public void Save(LibraryIndex index)
    {
        Directory.CreateDirectory(directory);
        var tmp = IndexPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(index, Json.Options));
        File.Move(tmp, IndexPath, overwrite: true);
    }

    public void SaveTorrent(Guid rootId, byte[] torrent)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TorrentPath(rootId))!);
        File.WriteAllBytes(TorrentPath(rootId), torrent);
    }

    public byte[]? LoadTorrent(Guid rootId) => File.Exists(TorrentPath(rootId)) ? File.ReadAllBytes(TorrentPath(rootId)) : null;
    public void DeleteTorrent(Guid rootId) { if (File.Exists(TorrentPath(rootId))) File.Delete(TorrentPath(rootId)); }
}

[JsonConverter(typeof(JsonStringEnumConverter<ScanState>))]
public enum ScanState { Idle, Scanning, Failed }

public sealed record LibraryRootStatus(LibraryRoot Root, ScanState State, int Definitions, int Binaries, int Unavailable);

/// <summary>Server deployments can restrict which directories may be registered (desktop: unrestricted).</summary>
public sealed record LibraryPolicy(IReadOnlyList<string> AllowedRoots)
{
    public static readonly LibraryPolicy Unrestricted = new([]);

    public void EnsureAllowed(string fullPath)
    {
        if (AllowedRoots.Count == 0) return;
        var ok = AllowedRoots.Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .Any(r => (fullPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).StartsWith(r, StringComparison.Ordinal));
        if (!ok) throw new EcuStudioException("LIBRARY_PATH_NOT_ALLOWED", "This directory is outside the library locations allowed by the server configuration", 403);
    }
}

/// <summary>Directory of the definition cache (files fetched from torrent sources).</summary>
public sealed record LibraryCacheLocation(string? Path);

public sealed record LibrarySearchResult(int Total, IReadOnlyList<LibraryEntry> Entries);

/// <summary>
/// Local definition archive (DAMOS / A2L / XDF / dumps). Directories are indexed in place; .torrent files contribute
/// their file list. Only metadata is stored; files are read from their location when a definition is used.
/// The library never uploads, copies or redistributes archive files.
/// </summary>
public sealed class DefinitionLibrary
{
    /// <summary>Largest definition file read into memory for import.</summary>
    public const long MaxImportBytes = 128L * 1024 * 1024;

    private readonly IDefinitionLibraryStore _store;
    private readonly LibraryPolicy _policy;
    private readonly LibraryScanner _scanner = new();
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<Guid, ScanState> _state = new();
    private LibraryIndex _index;

    public DefinitionLibrary(IDefinitionLibraryStore store, LibraryPolicy? policy = null, LibraryCacheLocation? cache = null)
    {
        _store = store;
        _policy = policy ?? LibraryPolicy.Unrestricted;
        _index = store.Load();
        CacheDirectory = cache?.Path;
    }

    /// <summary>Where files fetched from torrent sources without their own download directory are stored.</summary>
    public string? CacheDirectory { get; set; }

    /// <summary>Directory holding the payload of a torrent source: its own download directory, else the definition cache.</summary>
    public string? DownloadDirectory(LibraryRoot root) => root.Kind != LibraryRootKind.Torrent ? root.Path
        : root.DownloadPath ?? (CacheDirectory is { } c ? Path.Combine(c, root.InfoHash ?? root.Id.ToString("N")) : null);

    public LibraryRoot RootById(Guid id) => Root(id);

    public byte[]? TorrentBytes(Guid rootId) => _store.LoadTorrent(rootId);

    public IReadOnlyList<LibraryRootStatus> Roots()
    {
        var index = _index;
        return index.Roots.Select(r =>
        {
            var entries = index.Entries.Where(e => e.RootId == r.Id).ToList();
            return new LibraryRootStatus(r, _state.GetValueOrDefault(r.Id, r.LastError is null ? ScanState.Idle : ScanState.Failed),
                entries.Count(e => new DefinitionMatch(e, MatchLevel.Unknown, 0, []).IsDefinition),
                entries.Count(e => e.Format is LibraryFormat.Binary or LibraryFormat.Hex),
                entries.Count(e => !e.Available));
        }).ToList();
    }

    public LibraryRoot AddDirectory(string path, string? name)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new EcuStudioException("LIBRARY_PATH", "Give an absolute directory path");
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new EcuStudioException("LIBRARY_PATH", $"Directory not found: {full}");
        _policy.EnsureAllowed(full);
        lock (_lock)
        {
            if (_index.Roots.Any(r => r.Kind == LibraryRootKind.Directory && string.Equals(r.Path, full, StringComparison.Ordinal)))
                throw new EcuStudioException("LIBRARY_DUPLICATE", "This directory is already in the library");
            var root = new LibraryRoot { Id = Guid.NewGuid(), Kind = LibraryRootKind.Directory, Path = full, Name = name ?? Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)) };
            Commit(_index with { Roots = [.. _index.Roots, root] });
            return root;
        }
    }

    public LibraryRoot AddTorrent(string fileName, byte[] torrent, string? downloadPath)
    {
        var meta = TorrentMetadata.Parse(torrent);
        string? download = null;
        if (!string.IsNullOrWhiteSpace(downloadPath))
        {
            if (!Path.IsPathFullyQualified(downloadPath)) throw new EcuStudioException("LIBRARY_PATH", "Download directory must be an absolute path");
            download = Path.GetFullPath(downloadPath);
            if (!Directory.Exists(download)) throw new EcuStudioException("LIBRARY_PATH", $"Directory not found: {download}");
            _policy.EnsureAllowed(download);
        }
        var root = new LibraryRoot { Id = Guid.NewGuid(), Kind = LibraryRootKind.Torrent, Path = Path.GetFileName(fileName), DownloadPath = download, Name = meta.Name, InfoHash = meta.InfoHash };
        lock (_lock)
        {
            if (_index.Roots.FirstOrDefault(r => r.InfoHash == meta.InfoHash) is { } existing)
            {
                // A magnet source waiting for metadata becomes complete; a known torrent is not added twice.
                if (existing.MagnetUri is null || _store.LoadTorrent(existing.Id) is not null)
                    throw new EcuStudioException("LIBRARY_DUPLICATE", $"This torrent is already a source ({existing.Name})");
                _store.SaveTorrent(existing.Id, torrent);
                var completed = existing with { Name = meta.Name, Path = Path.GetFileName(fileName) };
                Commit(_index with { Roots = _index.Roots.Select(r => r.Id == existing.Id ? completed : r).ToList() });
                return completed;
            }
            _store.SaveTorrent(root.Id, torrent);
            Commit(_index with { Roots = [.. _index.Roots, root] });
        }
        return root;
    }

    /// <summary>Adds a .torrent that is already on this machine (no upload size limit beyond the parser's).</summary>
    public LibraryRoot AddTorrentFile(string torrentPath, string? downloadPath)
    {
        if (string.IsNullOrWhiteSpace(torrentPath) || !Path.IsPathFullyQualified(torrentPath)) throw new EcuStudioException("LIBRARY_PATH", "Give an absolute path to the .torrent file");
        var full = Path.GetFullPath(torrentPath);
        if (!File.Exists(full)) throw new EcuStudioException("LIBRARY_PATH", $"File not found: {full}");
        _policy.EnsureAllowed(Path.GetDirectoryName(full)!);
        if (new FileInfo(full).Length > TorrentMetadata.MaxTorrentBytes) throw new EcuStudioException("LIBRARY_TOO_LARGE", ".torrent file is larger than 256 MB");
        return AddTorrent(Path.GetFileName(full), File.ReadAllBytes(full), downloadPath);
    }

    /// <summary>Adds a magnet link; its file list is indexed once the metadata has been fetched from the swarm.</summary>
    public LibraryRoot AddMagnet(string magnetUri, string? hash, string? name)
    {
        if (string.IsNullOrWhiteSpace(hash)) throw new EcuStudioException("MAGNET_INVALID", "Magnet link has no info hash");
        lock (_lock)
        {
            if (_index.Roots.Any(r => r.InfoHash == hash)) throw new EcuStudioException("LIBRARY_DUPLICATE", "This torrent is already a source");
            var root = new LibraryRoot { Id = Guid.NewGuid(), Kind = LibraryRootKind.Torrent, Path = "magnet", MagnetUri = magnetUri, InfoHash = hash, Name = name ?? hash };
            Commit(_index with { Roots = [.. _index.Roots, root] });
            return root;
        }
    }

    /// <summary>Stores metadata fetched for a magnet source.</summary>
    public LibraryRoot SetTorrentMetadata(Guid rootId, byte[] torrent)
    {
        var meta = TorrentMetadata.Parse(torrent);
        lock (_lock)
        {
            var root = Root(rootId);
            if (root.InfoHash is { } h && h != meta.InfoHash) throw new EcuStudioException("TORRENT_METADATA", "Fetched metadata does not belong to this magnet link");
            _store.SaveTorrent(rootId, torrent);
            var updated = root with { Name = root.Name == root.InfoHash ? meta.Name : root.Name, InfoHash = meta.InfoHash };
            Commit(_index with { Roots = _index.Roots.Select(r => r.Id == rootId ? updated : r).ToList() });
            return updated;
        }
    }

    public LibraryRoot SetSourceOptions(Guid rootId, bool? enabled, int? priority)
    {
        lock (_lock)
        {
            var root = Root(rootId);
            root = root with { Enabled = enabled ?? root.Enabled, Priority = priority ?? root.Priority };
            Commit(_index with { Roots = _index.Roots.Select(r => r.Id == rootId ? root : r).ToList() });
            return root;
        }
    }

    /// <summary>Marks fetched torrent files available and reads their identifiers from the content.</summary>
    public IReadOnlyList<LibraryEntry> MarkDownloaded(Guid rootId, IReadOnlyCollection<string> relativePaths)
    {
        lock (_lock)
        {
            var root = Root(rootId);
            var dir = DownloadDirectory(root) ?? throw new EcuStudioException("LIBRARY_UNAVAILABLE", "No download directory for this source");
            var updated = new List<LibraryEntry>();
            var entries = _index.Entries.Select(e =>
            {
                if (e.RootId != rootId || !relativePaths.Contains(e.RelativePath)) return e;
                var local = Path.Combine(dir, e.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(local)) return e;
                var info = new FileInfo(local);
                var analysed = LibraryScanner.Analyze(rootId, e.RelativePath, local, e.Format, info.Length, info.LastWriteTimeUtc, LibraryScanner.Identify(e.RelativePath));
                updated.Add(analysed);
                return analysed;
            }).ToList();
            Commit(_index with { Entries = entries });
            return updated;
        }
    }

    public LibraryRoot UpdateRoot(Guid rootId, string? name, string? downloadPath)
    {
        lock (_lock)
        {
            var root = Root(rootId);
            if (downloadPath is not null && root.Kind == LibraryRootKind.Torrent)
            {
                if (downloadPath.Length > 0 && (!Path.IsPathFullyQualified(downloadPath) || !Directory.Exists(downloadPath)))
                    throw new EcuStudioException("LIBRARY_PATH", $"Directory not found: {downloadPath}");
                if (downloadPath.Length > 0) _policy.EnsureAllowed(Path.GetFullPath(downloadPath));
                root = root with { DownloadPath = downloadPath.Length == 0 ? null : Path.GetFullPath(downloadPath) };
            }
            if (!string.IsNullOrWhiteSpace(name)) root = root with { Name = name.Trim() };
            Commit(_index with { Roots = _index.Roots.Select(r => r.Id == rootId ? root : r).ToList() });
            return root;
        }
    }

    public void RemoveRoot(Guid rootId)
    {
        lock (_lock)
        {
            Root(rootId);
            _store.DeleteTorrent(rootId);
            Commit(new LibraryIndex(_index.Roots.Where(r => r.Id != rootId).ToList(), _index.Entries.Where(e => e.RootId != rootId).ToList()));
        }
    }

    public bool IsScanning(Guid rootId) => _state.GetValueOrDefault(rootId) == ScanState.Scanning;

    /// <summary>Incremental rescan of one root (unchanged files keep their entries).</summary>
    public LibraryRoot Scan(Guid rootId, CancellationToken ct = default)
    {
        var root = Root(rootId);
        if (!_state.TryAdd(rootId, ScanState.Scanning) && !_state.TryUpdate(rootId, ScanState.Scanning, ScanState.Idle) && !_state.TryUpdate(rootId, ScanState.Scanning, ScanState.Failed))
            throw new EcuStudioException("LIBRARY_BUSY", "This library location is already being scanned");
        try
        {
            var previous = _index.Entries.Where(e => e.RootId == rootId).ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
            List<LibraryEntry> entries;
            if (root.Kind == LibraryRootKind.Directory) entries = _scanner.ScanDirectory(root, previous, ct).ToList();
            else
            {
                var torrent = _store.LoadTorrent(rootId) ?? throw new EcuStudioException("LIBRARY_TORRENT", root.MagnetUri is not null
                    ? "Torrent metadata has not been fetched yet" : "Stored .torrent file is missing: add it again");
                entries = _scanner.ScanTorrent(root with { DownloadPath = DownloadDirectory(root) }, TorrentMetadata.Parse(torrent), previous, ct).ToList();
            }
            lock (_lock)
            {
                var updated = root with { LastScanAt = DateTimeOffset.UtcNow, FileCount = entries.Count, TotalBytes = entries.Sum(e => e.Size), LastError = null };
                Commit(new LibraryIndex(_index.Roots.Select(r => r.Id == rootId ? updated : r).ToList(), [.. _index.Entries.Where(e => e.RootId != rootId), .. entries]));
                _state[rootId] = ScanState.Idle;
                return updated;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_lock)
            {
                var failed = root with { LastError = ex.Message, LastScanAt = DateTimeOffset.UtcNow };
                if (_index.Roots.Any(r => r.Id == rootId)) Commit(_index with { Roots = _index.Roots.Select(r => r.Id == rootId ? failed : r).ToList() });
            }
            _state[rootId] = ScanState.Failed;
            throw;
        }
        catch
        {
            _state[rootId] = ScanState.Idle;
            throw;
        }
    }

    public LibrarySearchResult Search(string? query, LibraryFormat? format, bool definitionsOnly, int offset, int limit)
    {
        IEnumerable<LibraryEntry> q = _index.Entries;
        if (format is { } f) q = q.Where(e => e.Format == f);
        if (definitionsOnly) q = q.Where(e => new DefinitionMatch(e, MatchLevel.Unknown, 0, []).IsDefinition);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            q = q.Where(e => terms.All(t => e.RelativePath.Contains(t, StringComparison.OrdinalIgnoreCase)
                || (e.Title?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                || e.Identifiers.SoftwareNumbers.Concat(e.Identifiers.HardwareNumbers).Concat(e.Identifiers.OemNumbers).Concat(e.Identifiers.EcuFamilies)
                    .Any(i => i.Contains(t, StringComparison.OrdinalIgnoreCase))));
        }
        var list = q.ToList();
        return new LibrarySearchResult(list.Count, list.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 500)).ToList());
    }

    /// <summary>Matches over enabled sources only.</summary>
    public IReadOnlyList<DefinitionMatch> Match(BinaryKey key, int limit = 50)
    {
        var index = _index;
        var disabled = index.Roots.Where(r => !r.Enabled).Select(r => r.Id).ToHashSet();
        return DefinitionMatcher.Match(key, disabled.Count == 0 ? index.Entries : index.Entries.Where(e => !disabled.Contains(e.RootId)), limit);
    }

    public LibraryEntry Entry(string entryId) =>
        _index.Entries.FirstOrDefault(e => e.Id == entryId) ?? throw new NotFoundException($"Library entry {entryId} not found");

    /// <summary>Reads a downloaded/indexed file from where it lives. The path is resolved inside its root only.</summary>
    public (LibraryEntry Entry, byte[] Content) Read(string entryId)
    {
        var entry = Entry(entryId);
        if (!entry.Available) throw new EcuStudioException("LIBRARY_UNAVAILABLE", $"{entry.RelativePath} is not downloaded yet");
        var path = ResolvePath(entry);
        var info = new FileInfo(path);
        if (!info.Exists) throw new EcuStudioException("LIBRARY_UNAVAILABLE", $"{entry.RelativePath} is no longer at its location: rescan the library");
        if (info.Length > MaxImportBytes) throw new EcuStudioException("LIBRARY_TOO_LARGE", $"{entry.RelativePath} is larger than {MaxImportBytes / 1024 / 1024} MB");
        return (entry, File.ReadAllBytes(path));
    }

    private string ResolvePath(LibraryEntry entry)
    {
        var root = Root(entry.RootId);
        var baseDir = DownloadDirectory(root) ?? throw new EcuStudioException("LIBRARY_UNAVAILABLE", "Set the torrent download directory first");
        var full = Path.GetFullPath(Path.Combine(baseDir, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal)) throw new EcuStudioException("LIBRARY_PATH", "Entry path escapes its library location");
        return full;
    }

    private LibraryRoot Root(Guid id) => _index.Roots.FirstOrDefault(r => r.Id == id) ?? throw new NotFoundException($"Library location {id} not found");

    private void Commit(LibraryIndex index)
    {
        _store.Save(index);
        _index = index;
    }
}
