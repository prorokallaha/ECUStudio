using System.Text.Json;
using ECUStudio.Core;

namespace ECUStudio.AI;

public sealed record RejectedClaim(string Agent, string Text, string Reason);

/// <summary>
/// Converts AI JSON into domain findings. Free text is never accepted as an engineering fact:
/// findings without evidence that resolves to the context's evidence index are rejected.
/// </summary>
public static class AIResponseValidator
{
    public static (List<Finding> Accepted, List<RejectedClaim> Rejected) ParseFindings(JsonElement root, string agent, ISet<string> allowedRefs, string arrayName = "findings")
    {
        var accepted = new List<Finding>();
        var rejected = new List<RejectedClaim>();
        if (!root.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array) return (accepted, rejected);

        foreach (var f in arr.EnumerateArray())
        {
            var text = Str(f, "finding");
            if (string.IsNullOrWhiteSpace(text)) { rejected.Add(new(agent, "(empty)", "No finding text")); continue; }
            var evidence = ParseEvidence(f, allowedRefs, out var invalidRefs);
            if (evidence.Count == 0)
            {
                rejected.Add(new(agent, text, invalidRefs.Count > 0 ? $"Evidence refs not in context: {string.Join(", ", invalidRefs.Take(3))}" : "No evidence"));
                continue;
            }
            var confidence = Math.Clamp(Num(f, "confidence") ?? 0, 0, 1);
            if (invalidRefs.Count > 0) confidence *= 0.8; // partially unsupported
            accepted.Add(new Finding
            {
                Code = "AI_" + agent.ToUpperInvariant().Replace(' ', '_'),
                Text = text,
                Severity = SeverityExtensions.Parse(Str(f, "severity") ?? "UNKNOWN"),
                Confidence = Math.Round(confidence, 2),
                Evidence = evidence,
                Assumptions = StrArr(f, "assumptions"),
                Unknowns = StrArr(f, "unknowns"),
                AffectedComponents = StrArr(f, "affected_components"),
                RelatedMaps = StrArr(f, "related_maps"),
                Source = FindingSource.AI,
                SourceDetail = agent,
            });
        }
        return (accepted, rejected);
    }

    public static List<Evidence> ParseEvidence(JsonElement parent, ISet<string> allowedRefs, out List<string> invalid)
    {
        invalid = [];
        var list = new List<Evidence>();
        if (!parent.TryGetProperty("evidence", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in arr.EnumerateArray())
        {
            var r = Str(e, "ref");
            if (r is null || !allowedRefs.Contains(r)) { if (r is not null) invalid.Add(r); continue; }
            var type = Enum.TryParse<EvidenceType>((Str(e, "type") ?? "").Replace("_", ""), true, out var t) ? t : EvidenceType.Rule;
            list.Add(new Evidence(type, r, Str(e, "detail") ?? ""));
        }
        return list;
    }

    /// <summary>Hard guard: SAFE cannot stand while critical parameters are unknown.</summary>
    public static Severity GuardSafe(Severity s, IReadOnlyCollection<string> criticalUnknowns) =>
        s == Severity.Safe && criticalUnknowns.Count > 0 ? Severity.Unknown : s;

    public static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static double? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    public static List<string> StrArr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];
}
