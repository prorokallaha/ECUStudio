using System.Text.Json.Serialization;
using ECUStudio.Binary;
using ECUStudio.Calibration.Model;
using ECUStudio.Core;

namespace ECUStudio.Calibration.Scanning;

[JsonConverter(typeof(JsonStringEnumConverter<CandidateStatus>))]
public enum CandidateStatus { Candidate, Confirmed, Rejected }

public sealed record RoleHypothesis(MapRole Role, double Confidence, string Rationale, SourceType Source, IReadOnlyList<Evidence> Evidence);

public sealed record AxisGuess(AxisQuantity Quantity, double Likelihood, double Factor, string Unit);

/// <summary>
/// A map found by structure scanning whose purpose is not established.
/// It never becomes a confirmed map automatically: only a user decision does that.
/// </summary>
public sealed record MapCandidate
{
    public required string Id { get; init; }
    public int Address { get; init; }
    public int HeaderAddress { get; init; }
    public int Rows { get; init; }
    public int Cols { get; init; }
    public DataType DataType { get; init; } = DataType.UInt16;
    public Endianness Endian { get; init; } = Endianness.Big;
    public required double[] XAxisRaw { get; init; }
    public required double[] YAxisRaw { get; init; }
    public double RawMin { get; init; }
    public double RawMax { get; init; }
    public IReadOnlyList<AxisGuess> XAxisGuesses { get; init; } = [];
    public IReadOnlyList<AxisGuess> YAxisGuesses { get; init; } = [];
    public IReadOnlyList<RoleHypothesis> Hypotheses { get; init; } = [];
    public CandidateStatus Status { get; init; } = CandidateStatus.Candidate;
    public MapRole? ConfirmedRole { get; init; }
    public string? DecisionNote { get; init; }

    public RoleHypothesis? Best => Hypotheses.Count == 0 ? null : Hypotheses[0];

    /// <summary>
    /// Label shown for the candidate. A hypothesis is never presented as the map's name:
    /// "Unknown map · candidate: Torque to IQ, 18 %" until the user confirms a role.
    /// </summary>
    public string DisplayName => Status == CandidateStatus.Confirmed && ConfirmedRole is { } r
        ? $"{r.DisplayName()} (confirmed by user)"
        : Best is { Role: not MapRole.Unknown } b
            ? $"Unknown map {Rows}×{Cols} @0x{Address:X} · candidate: {b.Role.DisplayName()}, {b.Confidence * 100:0} %"
            : $"Unknown map {Rows}×{Cols} @0x{Address:X}";
    public int XAxisAddress => HeaderAddress + 4;
    public int YAxisAddress => XAxisAddress + Cols * 2;
}
