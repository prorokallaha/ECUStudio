using ECUStudio.Core;

namespace ECUStudio.AI;

public sealed record ComponentVote(string Source, Severity Severity, double Confidence);

public sealed record ComponentConsensus(string Component, Severity PhysicsSeverity, Severity FinalSeverity, double Confidence, double Agreement, IReadOnlyList<ComponentVote> Votes, string Rationale);

/// <summary>
/// Combines physics/risk with AI analysts and the safety reviewer. Physics first:
/// AI can never lower a physics severity, may escalate at most to WARNING on its own,
/// and disagreement lowers confidence.
/// </summary>
public static class ConsensusEngine
{
    public static ComponentConsensus Combine(string component, Severity physics, double physicsConfidence, IReadOnlyList<ComponentVote> aiVotes, IReadOnlyCollection<string> criticalUnknowns)
    {
        var votes = new List<ComponentVote> { new("physics", physics, physicsConfidence) };
        votes.AddRange(aiVotes);
        var modal = votes.GroupBy(v => v.Severity).OrderByDescending(g => g.Sum(v => v.Confidence)).First().Key;
        var agreement = votes.Count(v => v.Severity == modal) / (double)votes.Count;

        var aiWorst = aiVotes.Count == 0 ? Severity.Safe : SeverityExtensions.Worst(aiVotes.Where(v => v.Confidence >= 0.4).Select(v => v.Severity).DefaultIfEmpty(Severity.Safe));
        if (aiWorst == Severity.Danger && physics.Rank() < Severity.Warning.Rank()) aiWorst = Severity.Warning;
        var final = aiWorst.Rank() > physics.Rank() && aiWorst != Severity.Unknown ? aiWorst : physics;
        final = AIResponseValidator.GuardSafe(final, criticalUnknowns);

        var meanConf = votes.Average(v => v.Confidence);
        var confidence = Math.Round(meanConf * (0.5 + 0.5 * agreement), 2);
        var rationale = agreement >= 0.99 ? "All sources agree."
            : $"Sources disagree ({string.Join(", ", votes.Select(v => $"{v.Source}: {v.Severity.ToWire()}"))}); confidence reduced.";
        if (final != physics) rationale += $" Escalated from physics {physics.ToWire()} by AI evidence.";
        return new ComponentConsensus(component, physics, final, confidence, Math.Round(agreement, 2), votes, rationale);
    }
}
