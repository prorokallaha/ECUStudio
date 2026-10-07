using System.Text.Json.Serialization;

namespace ECUStudio.Calibration.Library;

[JsonConverter(typeof(JsonStringEnumConverter<LibraryFormat>))]
public enum LibraryFormat
{
    A2L,
    Damos,
    Xdf,
    EcuDef,
    /// <summary>Raw flash or calibration dump (.bin/.ori/.mod/...).</summary>
    Binary,
    /// <summary>Intel HEX / Motorola S-record.</summary>
    Hex,
    /// <summary>WinOLS project (closed format: name-level metadata only).</summary>
    Ols,
    Kp,
    Csv,
    Xml,
    Archive,
    Other,
}

[JsonConverter(typeof(JsonStringEnumConverter<LibraryRootKind>))]
public enum LibraryRootKind
{
    /// <summary>A local directory, e.g. where a torrent client downloads to. Files are used in place, never copied.</summary>
    Directory,
    /// <summary>Metadata of a .torrent file: file list only; contents become available once downloaded.</summary>
    Torrent,
}

/// <summary>A registered archive location.</summary>
public sealed record LibraryRoot
{
    public required Guid Id { get; init; }
    public required LibraryRootKind Kind { get; init; }
    /// <summary>Directory path, or the .torrent file path.</summary>
    public required string Path { get; init; }
    /// <summary>For torrents: directory where the client stores the payload (optional).</summary>
    public string? DownloadPath { get; init; }
    public string? Name { get; init; }
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastScanAt { get; init; }
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
    public string? LastError { get; init; }
}

/// <summary>Identifiers found in a file name or content. Only what is literally present; nothing is inferred.</summary>
public sealed record LibraryIdentifiers
{
    public IReadOnlyList<string> SoftwareNumbers { get; init; } = [];
    public IReadOnlyList<string> HardwareNumbers { get; init; } = [];
    public IReadOnlyList<string> OemNumbers { get; init; } = [];
    public IReadOnlyList<string> EcuFamilies { get; init; } = [];
    public IReadOnlyList<string> EngineHints { get; init; } = [];

    public bool IsEmpty => SoftwareNumbers.Count + HardwareNumbers.Count + OemNumbers.Count + EcuFamilies.Count + EngineHints.Count == 0;
}

/// <summary>One indexed file. The index stores metadata only; the file itself stays where it is.</summary>
public sealed record LibraryEntry
{
    public required string Id { get; init; }
    public required Guid RootId { get; init; }
    public required string RelativePath { get; init; }
    public required LibraryFormat Format { get; init; }
    public long Size { get; init; }
    public DateTimeOffset? Modified { get; init; }
    /// <summary>False for torrent entries that are not downloaded yet.</summary>
    public bool Available { get; init; } = true;
    public LibraryIdentifiers Identifiers { get; init; } = new();
    /// <summary>Identifiers read from the content (stronger than ones from the file name).</summary>
    public bool ContentIdentified { get; init; }
    public string? Title { get; init; }
    /// <summary>Characteristics / tables declared (A2L CHARACTERISTIC, XDF XDFTABLE).</summary>
    public int? ObjectCount { get; init; }
    /// <summary>SHA-256 of binaries up to <see cref="LibraryScanner.MaxHashBytes"/>.</summary>
    public string? Sha256 { get; init; }
    public string? Error { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<MatchLevel>))]
public enum MatchLevel { Exact, Strong, Probable, Weak, Unknown }

/// <summary>A library entry matched against a binary, with the reasons.</summary>
public sealed record DefinitionMatch(LibraryEntry Entry, MatchLevel Level, double Score, IReadOnlyList<string> Reasons)
{
    public bool IsDefinition => Entry.Format is LibraryFormat.A2L or LibraryFormat.Damos or LibraryFormat.Xdf or LibraryFormat.EcuDef or LibraryFormat.Kp;
    /// <summary>True when the format can be imported now (A2L, XDF, native JSON).</summary>
    public bool Importable => Entry.Format is LibraryFormat.A2L or LibraryFormat.Xdf or LibraryFormat.EcuDef;
}

/// <summary>What the binary is matched with.</summary>
public sealed record BinaryKey(string? SoftwareNumber, string? HardwareNumber, string? OemNumber, string? EcuFamily, string? Sha256, string? EngineHint);
