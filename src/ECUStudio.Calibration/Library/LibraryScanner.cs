using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ECUStudio.Calibration.Library;

/// <summary>
/// Indexes an archive location without copying it. Each file contributes metadata only: format, identifiers from the
/// file name and (for definitions and small binaries) from the content, object counts and a content hash for binaries.
/// Rescans are incremental: a file whose size and modification time did not change keeps its previous entry.
/// </summary>
public sealed partial class LibraryScanner
{
    /// <summary>Binaries up to this size are hashed and searched for identification strings (ECU dumps are 0.25–8 MB).</summary>
    public const long MaxHashBytes = 8 * 1024 * 1024;
    /// <summary>Bytes of a text definition searched for identifiers.</summary>
    public const int MaxHeaderBytes = 4 * 1024 * 1024;
    /// <summary>Upper bound for counting objects in a text definition.</summary>
    public const long MaxCountBytes = 256L * 1024 * 1024;

    [GeneratedRegex(@"(?<![0-9])103[79]\d{6}(?![0-9])")] private static partial Regex BoschSwRegex();
    [GeneratedRegex(@"(?<![0-9])02[68]1\d{6}(?![0-9])")] private static partial Regex BoschHwRegex();
    [GeneratedRegex(@"(?<![0-9A-Z])[0-9][0-9A-Z]{2}[ _.+-]?9(?:06|07)[ _.+-]?\d{3}[ _.+-]?[A-Z]{0,3}(?![0-9A-Z])")] private static partial Regex VagOemRegex();
    /// <summary>Four-digit software version directly after a VAG part number.</summary>
    [GeneratedRegex(@"^[ _.+-]{1,3}(\d{4})(?![0-9])")] private static partial Regex VersionAfterOemRegex();
    [GeneratedRegex(@"(?<![A-Z])(EDC1[5-7][A-Z]{0,2}\d{0,2}|MED?1[57][.]?\d{0,2}|ME7[.]\d(?:[.]\d)?|MED9[.]?\d{0,2}|SIMOS\s?\d{1,2}(?:[.]\d)?|PPD1[.]\d|SID\d{3}|DCM\d[.]\d[A-Z]?)(?![A-Z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex EcuFamilyRegex();
    [GeneratedRegex(@"(?<![0-9])([1-6][.,]\d)\s?(TDI|TSI|TFSI|CDI|HDI|DCI|CRDI|D|L)(?![A-Z])", RegexOptions.IgnoreCase)]
    private static partial Regex EngineRegex();
    [GeneratedRegex(@"(?<![0-9])103[79]\d{6}P\d{3}([A-Z0-9]{4})")] private static partial Regex ProjectInSwRegex();
    [GeneratedRegex(@"(?:^|[/\\])SW[/\\]([A-Z0-9]{4})(?=[/\\])")] private static partial Regex ProjectFolderRegex();

    public static LibraryFormat FormatOf(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name.EndsWith(".ecudef.json", StringComparison.Ordinal)) return LibraryFormat.EcuDef;
        return Path.GetExtension(name) switch
        {
            ".a2l" or ".asap2" => LibraryFormat.A2L,
            ".dam" or ".damos" => LibraryFormat.Damos,
            ".xdf" => LibraryFormat.Xdf,
            ".bin" or ".ori" or ".org" or ".mod" or ".fls" or ".fla" or ".original" => LibraryFormat.Binary,
            ".hex" or ".s19" or ".s37" or ".mot" or ".srec" => LibraryFormat.Hex,
            ".ols" or ".olsx" => LibraryFormat.Ols,
            ".kp" => LibraryFormat.Kp,
            ".csv" => LibraryFormat.Csv,
            ".xml" => LibraryFormat.Xml,
            ".zip" or ".rar" or ".7z" => LibraryFormat.Archive,
            _ => LibraryFormat.Other,
        };
    }

    public static string EntryId(Guid rootId, string relativePath) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{rootId:N}/{relativePath.Replace('\\', '/')}")))[..20];

    /// <summary>Identifiers literally present in a text (file name, header, ASCII of a dump).</summary>
    public static LibraryIdentifiers Identify(string text)
    {
        static IReadOnlyList<string> All(Regex r, string s, Func<Match, string>? map = null) =>
            r.Matches(s).Select(m => (map ?? (x => x.Value))(m)).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
        return new LibraryIdentifiers
        {
            SoftwareNumbers = All(BoschSwRegex(), text),
            HardwareNumbers = All(BoschHwRegex(), text),
            OemNumbers = All(VagOemRegex(), text, m => NormalizeOem(m.Value)),
            EcuFamilies = All(EcuFamilyRegex(), text, m => m.Value.ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal)),
            EngineHints = All(EngineRegex(), text, m => $"{m.Groups[1].Value.Replace(',', '.')} {m.Groups[2].Value.ToUpperInvariant()}"),
            ProjectCodes = All(ProjectInSwRegex(), text, m => m.Groups[1].Value).Union(All(ProjectFolderRegex(), text, m => m.Groups[1].Value)).ToList(),
            SoftwareVersions = VagOemRegex().Matches(text)
                .Select(m => VersionAfterOemRegex().Match(text.AsSpan(m.Index + m.Length, Math.Min(8, text.Length - m.Index - m.Length)).ToString()))
                .Where(v => v.Success).Select(v => v.Groups[1].Value).Distinct().Take(8).ToList(),
        };
    }

    public static string NormalizeOem(string s) => new(s.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static LibraryIdentifiers Merge(LibraryIdentifiers a, LibraryIdentifiers b) => new()
    {
        SoftwareNumbers = a.SoftwareNumbers.Union(b.SoftwareNumbers).ToList(),
        HardwareNumbers = a.HardwareNumbers.Union(b.HardwareNumbers).ToList(),
        OemNumbers = a.OemNumbers.Union(b.OemNumbers).ToList(),
        EcuFamilies = a.EcuFamilies.Union(b.EcuFamilies, StringComparer.OrdinalIgnoreCase).ToList(),
        EngineHints = a.EngineHints.Union(b.EngineHints, StringComparer.OrdinalIgnoreCase).ToList(),
        ProjectCodes = a.ProjectCodes.Union(b.ProjectCodes).ToList(),
        SoftwareVersions = a.SoftwareVersions.Union(b.SoftwareVersions).ToList(),
    };

    /// <summary>Scans a directory. <paramref name="previous"/> (by relative path) lets unchanged files skip content analysis.</summary>
    public IEnumerable<LibraryEntry> ScanDirectory(LibraryRoot root, IReadOnlyDictionary<string, LibraryEntry> previous, CancellationToken ct = default)
    {
        if (!Directory.Exists(root.Path)) throw new DirectoryNotFoundException($"Library directory not found: {root.Path}");
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden };
        foreach (var path in Directory.EnumerateFiles(root.Path, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            var format = FormatOf(path);
            var rel = Path.GetRelativePath(root.Path, path).Replace('\\', '/');
            FileInfo info;
            try { info = new FileInfo(path); } catch (IOException) { continue; }
            var nameIds = Identify(rel);
            if (format == LibraryFormat.Other && nameIds.IsEmpty) continue; // unrelated files are not indexed
            if (previous.TryGetValue(rel, out var old) && old.Size == info.Length && old.Modified == info.LastWriteTimeUtc && old.Error is null)
            {
                yield return old;
                continue;
            }
            yield return Analyze(root.Id, rel, path, format, info.Length, info.LastWriteTimeUtc, nameIds);
        }
    }

    /// <summary>Entries of a .torrent file. Files already downloaded to <see cref="LibraryRoot.DownloadPath"/> are analysed in place.</summary>
    public IEnumerable<LibraryEntry> ScanTorrent(LibraryRoot root, TorrentMetadata torrent, IReadOnlyDictionary<string, LibraryEntry> previous, CancellationToken ct = default)
    {
        foreach (var f in torrent.Files)
        {
            ct.ThrowIfCancellationRequested();
            var format = FormatOf(f.Path);
            var nameIds = Identify(f.Path);
            if (format == LibraryFormat.Other && nameIds.IsEmpty) continue;
            var local = root.DownloadPath is null ? null : Path.Combine(root.DownloadPath, f.Path.Replace('/', Path.DirectorySeparatorChar));
            var available = local is not null && File.Exists(local) && new FileInfo(local).Length == f.Length;
            if (!available)
            {
                yield return new LibraryEntry
                {
                    Id = EntryId(root.Id, f.Path), RootId = root.Id, RelativePath = f.Path, Format = format, Size = f.Length, Available = false, Identifiers = nameIds,
                };
                continue;
            }
            var info = new FileInfo(local!);
            if (previous.TryGetValue(f.Path, out var old) && old.Available && old.Size == info.Length && old.Modified == info.LastWriteTimeUtc && old.Error is null)
            {
                yield return old;
                continue;
            }
            yield return Analyze(root.Id, f.Path, local!, format, info.Length, info.LastWriteTimeUtc, nameIds);
        }
    }

    public static LibraryEntry Analyze(Guid rootId, string rel, string path, LibraryFormat format, long size, DateTimeOffset modified, LibraryIdentifiers nameIds)
    {
        var entry = new LibraryEntry { Id = EntryId(rootId, rel), RootId = rootId, RelativePath = rel, Format = format, Size = size, Modified = modified, Identifiers = nameIds };
        try
        {
            return format switch
            {
                LibraryFormat.A2L => AnalyzeText(entry, path, "/begin CHARACTERISTIC", TitleA2l),
                LibraryFormat.Xdf => AnalyzeText(entry, path, "<XDFTABLE", TitleXdf),
                LibraryFormat.Damos or LibraryFormat.Kp or LibraryFormat.Csv or LibraryFormat.Xml or LibraryFormat.EcuDef => AnalyzeText(entry, path, null, null),
                LibraryFormat.Binary when size <= MaxHashBytes => AnalyzeBinary(entry, path),
                LibraryFormat.Archive when path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) => AnalyzeZip(entry, path),
                _ => entry,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return entry with { Error = ex.Message };
        }
    }

    private static LibraryEntry AnalyzeText(LibraryEntry entry, string path, string? objectMarker, Func<string, string?>? title)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var header = new byte[(int)Math.Min(MaxHeaderBytes, entry.Size)];
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        var text = Encoding.Latin1.GetString(header, 0, read);
        int? count = null;
        if (objectMarker is not null)
        {
            count = CountOccurrences(text, objectMarker);
            if (entry.Size > read)
            {
                // Count the rest without keeping it in memory (bounded); carry the marker length to catch boundary hits.
                using var reader = new StreamReader(stream, Encoding.Latin1);
                var buffer = new char[1 << 16];
                var carry = text.Length >= objectMarker.Length ? text[^(objectMarker.Length - 1)..] : "";
                long total = read;
                int n;
                while (total < MaxCountBytes && (n = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    var chunk = carry + new string(buffer, 0, n);
                    count += CountOccurrences(chunk, objectMarker);
                    carry = chunk.Length >= objectMarker.Length ? chunk[^(objectMarker.Length - 1)..] : chunk;
                    total += n;
                }
            }
        }
        var ids = Identify(text);
        return entry with { Identifiers = Merge(entry.Identifiers, ids), ContentIdentified = !ids.IsEmpty, ObjectCount = count, Title = title?.Invoke(text) };
    }

    private static LibraryEntry AnalyzeBinary(LibraryEntry entry, string path)
    {
        var bytes = File.ReadAllBytes(path);
        var ids = Identify(Ascii(bytes));
        return entry with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Identifiers = Merge(entry.Identifiers, ids), ContentIdentified = !ids.IsEmpty };
    }

    private static LibraryEntry AnalyzeZip(LibraryEntry entry, string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var names = string.Join('\n', zip.Entries.Select(e => e.FullName));
        return entry with { Identifiers = Merge(entry.Identifiers, Identify(names)), ObjectCount = zip.Entries.Count, Title = $"{zip.Entries.Count} file(s)" };
    }

    /// <summary>Printable ASCII runs (≥ 4 chars) joined by newlines; non-printable bytes act as separators.</summary>
    public static string Ascii(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        var run = 0;
        var start = sb.Length;
        foreach (var b in data)
        {
            if (b is >= 0x20 and < 0x7F) { sb.Append((char)b); run++; continue; }
            if (run is > 0 and < 4) sb.Length -= run;
            else if (run >= 4) sb.Append('\n');
            run = 0;
        }
        if (run is > 0 and < 4) sb.Length -= run;
        _ = start;
        return sb.ToString();
    }

    private static int CountOccurrences(string text, string marker)
    {
        var count = 0;
        for (var i = text.IndexOf(marker, StringComparison.Ordinal); i >= 0; i = text.IndexOf(marker, i + marker.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string? TitleA2l(string text)
    {
        var m = Regex.Match(text, @"/begin\s+PROJECT\s+(\S+)\s+""([^""]*)""");
        var mod = Regex.Match(text, @"/begin\s+MODULE\s+(\S+)\s+""([^""]*)""");
        return m.Success ? $"{m.Groups[1].Value}{(mod.Success ? " / " + mod.Groups[1].Value : "")}" : null;
    }

    private static string? TitleXdf(string text) =>
        Regex.Match(text, @"<deftitle>([^<]{1,200})</deftitle>", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
}
