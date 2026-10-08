using System.Text.Json.Serialization;
using ECUStudio.Application.Analysis;
using ECUStudio.Application.Library;
using ECUStudio.Application.Projects;
using ECUStudio.Binary;
using ECUStudio.Calibration.Library;
using ECUStudio.Core;

namespace ECUStudio.Application.Acquisition;

[JsonConverter(typeof(JsonStringEnumConverter<AcquisitionState>))]
public enum AcquisitionState
{
    Identify, LocalSearch, TorrentSearch, WaitingMetadata, Downloading, Verifying, Importing, MatchingMaps,
    /// <summary>A definition was verified and bound.</summary>
    Done,
    /// <summary>Only probable matches exist: the user decides whether to download and test one.</summary>
    AwaitingConfirmation,
    /// <summary>No suitable definition: the analysis continues with heuristic map discovery.</summary>
    NotFound,
    Failed,
}

/// <summary>Why a search ended without a definition, for a precise message instead of "not found".</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AcquisitionReason>))]
public enum AcquisitionReason
{
    /// <summary>No library folder or torrent is configured.</summary>
    NoSources,
    /// <summary>A torrent is still being indexed; the search repeats when indexing finishes.</summary>
    NotIndexed,
    /// <summary>No A2L/definition for this software in any source.</summary>
    NoCandidates,
    /// <summary>Files were found but none could be downloaded (no peers, stalled, firewall).</summary>
    DownloadFailed,
    /// <summary>Definitions were checked and none fits the binary.</summary>
    Incompatible,
}

/// <summary>A definition file that could be used for the binary, before it is fetched.</summary>
public sealed record AcquisitionCandidate
{
    public required string EntryId { get; init; }
    public required string Path { get; init; }
    public required string FileName { get; init; }
    public required LibraryFormat Format { get; init; }
    public long Size { get; init; }
    public bool Available { get; init; }
    public required string Source { get; init; }
    public Guid SourceId { get; init; }
    public int Rank { get; init; }
    public MatchConfidence Confidence { get; init; }
    public MatchLevel Level { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
    /// <summary>Versions named by the file (e.g. 9351) and the binary's version, for the "version differs" prompt.</summary>
    public IReadOnlyList<string> Versions { get; init; } = [];
}

/// <summary>Transfer numbers for the progress display.</summary>
public sealed record TransferInfo(string File, long BytesDone, long BytesTotal, long BytesPerSecond, int Peers, int Seeds, double? SecondsLeft, bool FromCache = false);

/// <summary>A heuristic candidate that turned out to be a map of the definition.</summary>
public sealed record ReconciledCandidate(int Address, string CandidateLabel, string MapId, string MapName, string? Note);

/// <summary>State of the automatic definition search for one project (persisted with the project).</summary>
public sealed record DefinitionAcquisition
{
    public Guid? JobId { get; init; }
    public required AcquisitionState State { get; init; }
    public AcquisitionReason? Reason { get; init; }
    public string? FileSha256 { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? BinarySoftwareVersion { get; init; }
    public IReadOnlyList<AcquisitionCandidate> Candidates { get; init; } = [];
    public AcquisitionCandidate? Chosen { get; init; }
    public DefinitionCompatibilityResult? Verification { get; init; }
    public TransferInfo? Transfer { get; init; }
    public IReadOnlyList<string> Log { get; init; } = [];
    public int ReconciledCount { get; init; }
    public IReadOnlyList<ReconciledCandidate> Reconciled { get; init; } = [];
    public Guid? AnalysisId { get; init; }
}

/// <summary>Request to start a search; <see cref="EntryId"/> fetches one specific candidate the user approved.</summary>
public sealed record AcquisitionRequest(Guid? FileId = null, string? EntryId = null);

/// <summary>Torrent source as shown in Settings.</summary>
public sealed record TorrentSource(Guid Id, string Name, string? InfoHash, string? MagnetUri, int FileCount, long TotalBytes, bool Indexed, bool MetadataPending,
    string? DownloadDirectory, bool Enabled, int Priority, int Downloaded, string? LastError);
