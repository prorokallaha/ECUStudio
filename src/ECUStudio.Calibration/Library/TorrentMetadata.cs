using System.Text;
using ECUStudio.Core;

namespace ECUStudio.Calibration.Library;

public sealed record TorrentFile(string Path, long Length);

/// <summary>File list of a .torrent (BitTorrent v1 metainfo). Only metadata is read; nothing is downloaded.</summary>
public sealed record TorrentMetadata(string Name, IReadOnlyList<TorrentFile> Files, long TotalLength)
{
    /// <summary>BitTorrent v1 info hash: SHA-1 of the bencoded "info" dictionary, lowercase hex.</summary>
    public string InfoHash { get; init; } = "";

    public const int MaxTorrentBytes = 256 * 1024 * 1024;

    public static TorrentMetadata Parse(byte[] data)
    {
        if (data.Length > MaxTorrentBytes) throw new DefinitionException(".torrent file is larger than 256 MB");
        var pos = 0;
        if (Bencode.Read(data, ref pos) is not Dictionary<string, object> root || !root.TryGetValue("info", out var infoObj) || infoObj is not Dictionary<string, object> info)
            throw new DefinitionException("Not a .torrent file: missing 'info' dictionary");
        var name = Text(info, "name.utf-8") ?? Text(info, "name") ?? throw new DefinitionException(".torrent has no name");
        var files = new List<TorrentFile>();
        if (info.TryGetValue("files", out var list) && list is List<object> items)
        {
            foreach (var item in items.OfType<Dictionary<string, object>>())
            {
                var parts = (item.GetValueOrDefault("path.utf-8") ?? item.GetValueOrDefault("path")) as List<object>
                    ?? throw new DefinitionException(".torrent file entry without path");
                var segments = parts.OfType<byte[]>().Select(p => Encoding.UTF8.GetString(p)).ToList();
                if (segments.Any(s => s is "" or "." or ".." || s.Contains('/') || s.Contains('\\')))
                    throw new DefinitionException(".torrent contains an unsafe file path");
                files.Add(new TorrentFile(string.Join('/', [name, .. segments]), item.GetValueOrDefault("length") as long? ?? 0));
            }
        }
        else files.Add(new TorrentFile(name, info.GetValueOrDefault("length") as long? ?? 0));
        if (name is "" or "." or ".." || name.Contains('/') || name.Contains('\\')) throw new DefinitionException(".torrent has an unsafe name");
        return new TorrentMetadata(name, files, files.Sum(f => f.Length)) { InfoHash = InfoHashOf(data) };
    }

    private static string InfoHashOf(byte[] data)
    {
        // Walk the top-level dictionary to find the exact bytes of the "info" value.
        var pos = 1;
        while (pos < data.Length && data[pos] != 'e')
        {
            var key = (byte[])Bencode.Read(data, ref pos);
            var start = pos;
            Bencode.Read(data, ref pos);
            if (Encoding.ASCII.GetString(key) == "info")
                return Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(data.AsSpan(start, pos - start)));
        }
        throw new DefinitionException("Not a .torrent file: missing 'info' dictionary");
    }

    private static string? Text(Dictionary<string, object> d, string key) => d.GetValueOrDefault(key) is byte[] b ? Encoding.UTF8.GetString(b) : null;
}

/// <summary>Minimal bencode reader: integers (long), byte strings (byte[]), lists and dictionaries.</summary>
internal static class Bencode
{
    private const int MaxDepth = 64;

    public static object Read(byte[] d, ref int pos, int depth = 0)
    {
        if (depth > MaxDepth) throw new DefinitionException("bencode nesting too deep");
        if (pos >= d.Length) throw new DefinitionException("Unexpected end of bencode data");
        switch ((char)d[pos])
        {
            case 'i':
            {
                var end = Array.IndexOf(d, (byte)'e', pos);
                if (end < 0) throw new DefinitionException("Unterminated bencode integer");
                var v = long.Parse(Encoding.ASCII.GetString(d, pos + 1, end - pos - 1), System.Globalization.CultureInfo.InvariantCulture);
                pos = end + 1;
                return v;
            }
            case 'l':
            {
                pos++;
                var list = new List<object>();
                while (pos < d.Length && d[pos] != 'e') list.Add(Read(d, ref pos, depth + 1));
                pos++;
                return list;
            }
            case 'd':
            {
                pos++;
                var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                while (pos < d.Length && d[pos] != 'e')
                {
                    var key = Encoding.UTF8.GetString((byte[])ReadString(d, ref pos));
                    dict[key] = Read(d, ref pos, depth + 1);
                }
                pos++;
                return dict;
            }
            default:
                return ReadString(d, ref pos);
        }
    }

    private static object ReadString(byte[] d, ref int pos)
    {
        var colon = Array.IndexOf(d, (byte)':', pos);
        if (colon < 0 || colon - pos > 12) throw new DefinitionException("Invalid bencode string length");
        var len = int.Parse(Encoding.ASCII.GetString(d, pos, colon - pos), System.Globalization.CultureInfo.InvariantCulture);
        if (len < 0 || colon + 1 + len > d.Length) throw new DefinitionException("bencode string exceeds data");
        var s = d.AsSpan(colon + 1, len).ToArray();
        pos = colon + 1 + len;
        return s;
    }
}
