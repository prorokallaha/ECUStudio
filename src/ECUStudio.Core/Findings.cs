using System.Text.Json.Serialization;

namespace ECUStudio.Core;

[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity
{
    Safe,
    Review,
    Warning,
    Danger,
    Unknown,
}

public static class SeverityExtensions
{
    /// <summary>
    /// Ordering used for aggregation. UNKNOWN ranks above REVIEW: it is never "safe",
    /// but it is not evidence of overload either.
    /// </summary>
    public static int Rank(this Severity s) => s switch
    {
        Severity.Safe => 0,
        Severity.Review => 1,
        Severity.Unknown => 2,
        Severity.Warning => 3,
        Severity.Danger => 4,
        _ => 2,
    };

    public static Severity Worst(IEnumerable<Severity> items)
    {
        var any = false;
        var worst = Severity.Safe;
        foreach (var s in items)
        {
            any = true;
            if (s.Rank() > worst.Rank()) worst = s;
        }
        return any ? worst : Severity.Unknown;
    }

    public static string ToWire(this Severity s) => s.ToString().ToUpperInvariant();

    public static Severity Parse(string value) => value.Trim().ToUpperInvariant() switch
    {
        "SAFE" => Severity.Safe,
        "REVIEW" => Severity.Review,
        "WARNING" => Severity.Warning,
        "DANGER" => Severity.Danger,
        _ => Severity.Unknown,
    };
}

[JsonConverter(typeof(JsonStringEnumConverter<FindingSource>))]
public enum FindingSource { Rule, Physics, Risk, AI, Consensus }

[JsonConverter(typeof(JsonStringEnumConverter<EvidenceType>))]
public enum EvidenceType { Map, Diff, Simulation, Component, KnowledgeBase, Rule, Log, Binary }

/// <summary>A reference to an object in the analysis context that supports a finding.</summary>
public sealed record Evidence(EvidenceType Type, string Ref, string Detail);

/// <summary>Unified finding shape for rules, physics, risk and AI analysts.</summary>
public sealed record Finding
{
    public required string Code { get; init; }
    public required string Text { get; init; }
    public required Severity Severity { get; init; }
    public required double Confidence { get; init; }
    public IReadOnlyList<Evidence> Evidence { get; init; } = [];
    public IReadOnlyList<string> Assumptions { get; init; } = [];
    public IReadOnlyList<string> Unknowns { get; init; } = [];
    public IReadOnlyList<string> AffectedComponents { get; init; } = [];
    public IReadOnlyList<string> RelatedMaps { get; init; } = [];
    public FindingSource Source { get; init; } = FindingSource.Rule;
    public string? SourceDetail { get; init; }
    /// <summary>Optional RPM band the finding applies to.</summary>
    public double[]? RpmRange { get; init; }
}
