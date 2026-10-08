using System.Text.Json.Serialization;
using ECUStudio.Calibration.Library;

namespace ECUStudio.Application.Acquisition;

[JsonConverter(typeof(JsonStringEnumConverter<TransferPhase>))]
public enum TransferPhase { Metadata, Connecting, Downloading, Verifying, Done }

/// <summary>Live state of a torrent transfer, reported by the client.</summary>
public sealed record TorrentTransferProgress(TransferPhase Phase, long BytesDone, long BytesTotal, long BytesPerSecond, int Peers, int Seeds)
{
    public double Fraction => BytesTotal <= 0 ? 0 : Math.Clamp((double)BytesDone / BytesTotal, 0, 1);
    public TimeSpan? Remaining => BytesPerSecond <= 0 || BytesTotal <= 0 ? null : TimeSpan.FromSeconds((BytesTotal - BytesDone) / (double)BytesPerSecond);
}

/// <summary>
/// Selective download: only <see cref="Files"/> (paths as listed by <see cref="TorrentMetadata"/>, including the torrent
/// name) are fetched into <see cref="SaveDirectory"/>, laid out as <c>SaveDirectory/&lt;path&gt;</c>.
/// </summary>
public sealed record TorrentDownloadRequest(byte[] Torrent, string SaveDirectory, IReadOnlyList<string> Files);

/// <summary>
/// BitTorrent access behind one interface so business logic does not depend on a library. Implementations stop the
/// torrent when the requested files are complete (no seeding of the archive afterwards).
/// </summary>
public interface ITorrentClient
{
    /// <summary>Short name shown in the UI ("MonoTorrent", "Local mirror").</summary>
    string Name { get; }

    /// <summary>Fetches the .torrent metainfo for a magnet link from the swarm.</summary>
    Task<byte[]> FetchMetadataAsync(string magnetUri, IProgress<TorrentTransferProgress>? progress, CancellationToken ct);

    Task DownloadAsync(TorrentDownloadRequest request, IProgress<TorrentTransferProgress>? progress, CancellationToken ct);
}

/// <summary>Magnet link fields needed before metadata is known.</summary>
public static class Magnet
{
    public static (string InfoHash, string? Name) Parse(string uri)
    {
        if (!uri.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) throw new Core.EcuStudioException("MAGNET_INVALID", "Not a magnet link");
        string? hash = null, name = null;
        foreach (var part in uri[8..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq];
            var value = Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            if (key == "xt" && value.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase)) hash = Normalize(value[9..]);
            else if (key == "dn") name = value;
        }
        return (hash ?? throw new Core.EcuStudioException("MAGNET_INVALID", "Magnet link has no BitTorrent v1 info hash (xt=urn:btih:…)"), name);
    }

    private static string Normalize(string h)
    {
        if (h.Length == 40 && h.All(Uri.IsHexDigit)) return h.ToLowerInvariant();
        if (h.Length == 32) return Convert.ToHexStringLower(Base32(h.ToUpperInvariant()));
        throw new Core.EcuStudioException("MAGNET_INVALID", "Unrecognised info hash in magnet link");
    }

    private static byte[] Base32(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in s)
        {
            var v = alphabet.IndexOf(c);
            if (v < 0) throw new Core.EcuStudioException("MAGNET_INVALID", "Invalid base32 info hash");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8) { bytes.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
        }
        return bytes.ToArray();
    }
}

/// <summary>
/// Development / test client: "downloads" from a local folder laid out like the torrent (e.g. a copy of the files a
/// real client fetched). Reports progress in chunks like a transfer would. Used where BitTorrent traffic is blocked.
/// </summary>
public sealed class LocalMirrorTorrentClient(string mirrorDirectory, int chunkBytes = 1 << 20, TimeSpan? chunkDelay = null) : ITorrentClient
{
    public string Name => "Local mirror";

    public Task<byte[]> FetchMetadataAsync(string magnetUri, IProgress<TorrentTransferProgress>? progress, CancellationToken ct)
    {
        var (hash, _) = Magnet.Parse(magnetUri);
        var path = Path.Combine(mirrorDirectory, hash + ".torrent");
        progress?.Report(new TorrentTransferProgress(TransferPhase.Metadata, 0, 0, 0, 0, 0));
        if (!File.Exists(path)) throw new Core.EcuStudioException("TORRENT_METADATA", "Metadata for this magnet link is not available from the mirror");
        return File.ReadAllBytesAsync(path, ct);
    }

    public async Task DownloadAsync(TorrentDownloadRequest request, IProgress<TorrentTransferProgress>? progress, CancellationToken ct)
    {
        var meta = TorrentMetadata.Parse(request.Torrent);
        var files = request.Files.Select(p => meta.Files.FirstOrDefault(f => f.Path == p)
            ?? throw new Core.EcuStudioException("TORRENT_FILE", $"{p} is not part of this torrent")).ToList();
        var total = files.Sum(f => f.Length);
        long done = 0;
        progress?.Report(new TorrentTransferProgress(TransferPhase.Connecting, 0, total, 0, 1, 1));
        var started = DateTime.UtcNow;
        foreach (var f in files)
        {
            var source = Path.Combine(mirrorDirectory, f.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source) || new FileInfo(source).Length != f.Length)
                throw new Core.EcuStudioException("TORRENT_NO_PEERS", $"No source has {f.Path}");
            var target = Path.Combine(request.SaveDirectory, f.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var tmp = target + ".part";
            await using (var input = File.OpenRead(source))
            await using (var output = File.Create(tmp))
            {
                var buffer = new byte[chunkBytes];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    var elapsed = Math.Max(0.001, (DateTime.UtcNow - started).TotalSeconds);
                    progress?.Report(new TorrentTransferProgress(TransferPhase.Downloading, done, total, (long)(done / elapsed), 1, 1));
                    if (chunkDelay is { } d) await Task.Delay(d, ct);
                }
            }
            File.Move(tmp, target, overwrite: true);
        }
        progress?.Report(new TorrentTransferProgress(TransferPhase.Done, total, total, 0, 1, 1));
    }
}
