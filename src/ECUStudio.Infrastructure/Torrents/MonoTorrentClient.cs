using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using ECUStudio.Application.Acquisition;
using ECUStudio.Core;
using MonoTorrent;
using MonoTorrent.Client;

namespace ECUStudio.Infrastructure.Torrents;

public sealed record MonoTorrentOptions
{
    /// <summary>Engine state (DHT table, partial pieces). Default: under the application data directory.</summary>
    public string? StateDirectory { get; init; }
    /// <summary>Upload is capped while a file is being fetched and stops with the torrent when the file is complete.</summary>
    public int MaxUploadBytesPerSecond { get; init; } = 16 * 1024;
    /// <summary>0: any free port.</summary>
    public int ListenPort { get; init; }
    public bool UseDht { get; init; } = true;
    /// <summary>UPnP/NAT-PMP port mapping on the router, so peers behind NAT can connect back.</summary>
    public bool PortForwarding { get; init; } = true;
    /// <summary>Peers added directly (LAN mirror, tests). Trackers and DHT are still used.</summary>
    public IReadOnlyList<IPEndPoint> ExtraPeers { get; init; } = [];
    /// <summary>A transfer that receives no data for this long fails (the next candidate is tried).</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// <see cref="ITorrentClient"/> over MonoTorrent. Only the requested files get a download priority; everything else in
/// the archive is <see cref="Priority.DoNotDownload"/>. The torrent is stopped as soon as the requested files are complete.
/// </summary>
public sealed class MonoTorrentClient : ITorrentClient, IAsyncDisposable
{
    private static readonly PropertyInfo? PrioritySetter =
        typeof(ClientEngine).Assembly.GetType("MonoTorrent.Client.TorrentFileInfo")?.GetProperty("Priority", BindingFlags.Public | BindingFlags.Instance);

    private readonly MonoTorrentOptions _options;
    private readonly Lazy<ClientEngine> _engine;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perTorrent = new();

    public MonoTorrentClient(MonoTorrentOptions? options = null)
    {
        _options = options ?? new MonoTorrentOptions();
        _engine = new Lazy<ClientEngine>(CreateEngine, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Name => "MonoTorrent";

    private ClientEngine CreateEngine()
    {
        var state = _options.StateDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ECUStudio", "torrent-state");
        Directory.CreateDirectory(state);
        var listen = new IPEndPoint(IPAddress.Any, _options.ListenPort);
        var builder = new EngineSettingsBuilder
        {
            CacheDirectory = state,
            AllowPortForwarding = _options.PortForwarding,
            AutoSaveLoadFastResume = false,
            AutoSaveLoadMagnetLinkMetadata = false,
            MaximumUploadRate = _options.MaxUploadBytesPerSecond,
            ListenEndPoints = new() { ["ipv4"] = listen },
            DhtEndPoint = _options.UseDht ? listen : null,
            AllowLocalPeerDiscovery = _options.UseDht,
        };
        return new ClientEngine(builder.ToSettings());
    }

    public async Task<byte[]> FetchMetadataAsync(string magnetUri, IProgress<TorrentTransferProgress>? progress, CancellationToken ct)
    {
        var link = MagnetLink.Parse(magnetUri);
        progress?.Report(new TorrentTransferProgress(TransferPhase.Metadata, 0, 0, 0, 0, 0));
        var meta = await _engine.Value.DownloadMetadataAsync(link, ct);
        progress?.Report(new TorrentTransferProgress(TransferPhase.Done, meta.Length, meta.Length, 0, 0, 0));
        // DownloadMetadataAsync returns the info dictionary; wrap it into a complete .torrent.
        return WrapInfoDictionary(meta.Span, link.AnnounceUrls);
    }

    public async Task DownloadAsync(TorrentDownloadRequest request, IProgress<TorrentTransferProgress>? progress, CancellationToken ct)
    {
        var torrent = Torrent.Load(request.Torrent);
        var hash = torrent.InfoHashes.V1OrV2.ToHex().ToLowerInvariant();
        var gate = _perTorrent.GetOrAdd(hash, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(ct);
        TorrentManager? manager = null;
        var emptyCreated = new List<string>();
        try
        {
            var wanted = request.Files.Select(f => f.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
            progress?.Report(new TorrentTransferProgress(TransferPhase.Connecting, 0, 0, 0, 0, 0));
            // Files are laid out as SaveDirectory/<torrent name>/<path>, the same as the library's relative paths.
            manager = await _engine.Value.AddAsync(torrent, request.SaveDirectory, new TorrentSettingsBuilder
            {
                CreateContainingDirectory = torrent.Files.Count > 1,
                MaximumUploadRate = _options.MaxUploadBytesPerSecond,
                UploadSlots = 2,
            }.ToSettings());

            var selected = SelectFiles(torrent.Name, manager.Files, f => f.Path, wanted);
            foreach (var file in manager.Files)
                if (!selected.Contains(file)) SetPriorityFast(file, Priority.DoNotDownload);
            // Setting one priority through the API refreshes the piece picker for all of them.
            foreach (var file in selected)
            {
                SetPriorityFast(file, Priority.DoNotDownload);
                await manager.SetFilePriorityAsync(file, Priority.High);
            }

            // MonoTorrent creates every zero-length file of the torrent on start; remember which ones it adds so they
            // can be removed again (the archive layout is not wanted, only the requested files).
            emptyCreated.AddRange(manager.Files.Where(f => f.Length == 0 && !selected.Contains(f) && !File.Exists(f.FullPath)).Select(f => f.FullPath));
            // Nothing of this torrent is on disk yet: an empty resume state skips hashing the whole archive.
            await manager.LoadFastResumeAsync(new FastResume(torrent.InfoHashes, new BitField(torrent.PieceCount()), new BitField(torrent.PieceCount())));
            await manager.StartAsync();
            foreach (var peer in _options.ExtraPeers)
                await manager.AddPeerAsync(new PeerInfo(new Uri($"ipv4://{peer.Address}:{peer.Port}")));

            var total = selected.Sum(f => f.Length);
            var lastData = DateTime.UtcNow;
            long lastDone = -1;
            while (!selected.All(Complete))
            {
                ct.ThrowIfCancellationRequested();
                if (manager.State == TorrentState.Error) throw new IOException(manager.Error?.Exception?.Message ?? "Torrent error");
                var done = selected.Sum(f => (long)(f.BitField.PercentComplete / 100.0 * f.Length));
                if (done != lastDone) { lastDone = done; lastData = DateTime.UtcNow; }
                else if (DateTime.UtcNow - lastData > _options.StallTimeout)
                    throw new TimeoutException(done == 0
                        ? $"no data for {(int)_options.StallTimeout.TotalMinutes} min: {manager.Peers.Available} peer(s) known, {manager.OpenConnections} connected"
                        : $"transfer stalled at {100 * done / Math.Max(1, total)} % for {(int)_options.StallTimeout.TotalMinutes} min ({manager.OpenConnections} connected)");
                var phase = manager.State == TorrentState.Metadata ? TransferPhase.Metadata
                    : manager.OpenConnections == 0 && done == 0 ? TransferPhase.Connecting : TransferPhase.Downloading;
                progress?.Report(new TorrentTransferProgress(phase, done, total, manager.Monitor.DownloadRate, manager.OpenConnections, manager.Peers.Seeds));
                await Task.Delay(500, ct);
            }
            progress?.Report(new TorrentTransferProgress(TransferPhase.Verifying, total, total, 0, manager.OpenConnections, manager.Peers.Seeds));
            await manager.StopAsync(TimeSpan.FromSeconds(10));
            progress?.Report(new TorrentTransferProgress(TransferPhase.Done, total, total, 0, 0, 0));
        }
        finally
        {
            if (manager is not null)
            {
                try
                {
                    if (manager.State is not (TorrentState.Stopped or TorrentState.Error)) await manager.StopAsync(TimeSpan.FromSeconds(10));
                    await _engine.Value.RemoveAsync(manager, RemoveMode.CacheDataOnly);
                }
                catch (Exception) { /* best effort: the file is already on disk or the transfer failed anyway */ }
                RemoveCreatedEmptyFiles(emptyCreated, request.SaveDirectory);
            }
            gate.Release();
        }
    }

    /// <summary>
    /// The torrent files for the requested paths ("&lt;torrent name&gt;/dir/file", '/'-separated as in the library index).
    /// MonoTorrent joins path parts with the OS separator, so on Windows its paths use '\'; both sides are compared
    /// with '/' separators.
    /// </summary>
    public static List<T> SelectFiles<T>(string torrentName, IEnumerable<T> all, Func<T, string> pathOf, IReadOnlyCollection<string> wanted)
    {
        var files = all.ToList();
        static string Norm(string p) => p.Replace('\\', '/').Trim('/');
        var want = wanted.Select(Norm).ToHashSet(StringComparer.Ordinal);
        var prefix = files.Count > 1 ? Norm(torrentName) + "/" : "";
        var selected = files.Where(f => want.Contains(prefix + Norm(pathOf(f)))).ToList();
        if (selected.Count != want.Count)
        {
            var missing = want.Except(files.Select(f => prefix + Norm(pathOf(f)))).First();
            throw new EcuStudioException("TORRENT_FILE_MISSING", $"Requested file is not part of this torrent: {missing}");
        }
        return selected;
    }

    private static void RemoveCreatedEmptyFiles(List<string> paths, string saveDirectory)
    {
        var root = Path.GetFullPath(saveDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var dirs = new HashSet<string>();
        foreach (var p in paths)
        {
            try
            {
                if (File.Exists(p) && new FileInfo(p).Length == 0) File.Delete(p);
                for (var d = Path.GetDirectoryName(p); d is not null && d.Length > root.Length && d.StartsWith(root, StringComparison.Ordinal); d = Path.GetDirectoryName(d)) dirs.Add(d);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        foreach (var d in dirs.OrderByDescending(d => d.Length))
        {
            try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool Complete(ITorrentManagerFile f) => f.Length == 0 || f.BitField.AllTrue;

    /// <summary>
    /// Sets a file priority without the per-call picker refresh: archives with hundreds of thousands of files
    /// would otherwise take minutes before the transfer starts.
    /// </summary>
    private static void SetPriorityFast(ITorrentManagerFile file, Priority priority)
    {
        if (file.Priority == priority) return;
        if (PrioritySetter is not null && PrioritySetter.DeclaringType!.IsInstanceOfType(file)) PrioritySetter.SetValue(file, priority);
    }

    private static byte[] WrapInfoDictionary(ReadOnlySpan<byte> info, IList<string>? trackers)
    {
        using var ms = new MemoryStream();
        void Str(string s) { var b = System.Text.Encoding.UTF8.GetBytes(s); ms.Write(System.Text.Encoding.ASCII.GetBytes($"{b.Length}:")); ms.Write(b); }
        ms.WriteByte((byte)'d');
        if (trackers is { Count: > 0 }) { Str("announce"); Str(trackers[0]); }
        Str("info");
        ms.Write(info);
        ms.WriteByte((byte)'e');
        return ms.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_engine.IsValueCreated) return;
        try { await _engine.Value.StopAllAsync(); } catch (Exception) { }
        _engine.Value.Dispose();
    }
}
