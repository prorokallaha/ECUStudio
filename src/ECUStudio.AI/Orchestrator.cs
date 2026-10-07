using System.Text.Json;
using System.Text.Json.Nodes;
using ECUStudio.Core;

namespace ECUStudio.AI;

public sealed record AIContext
{
    /// <summary>Deterministic JSON (stable ordering) shared by all agents of one analysis.</summary>
    public required string ContextJson { get; init; }
    public required ISet<string> AllowedRefs { get; init; }
    public required string BinaryHash { get; init; }
    public string? StockHash { get; init; }
    public required string ProfileHash { get; init; }
    public required string AnalysisVersion { get; init; }
    public required IReadOnlyList<(string Component, Severity Severity, double Confidence)> PhysicsComponents { get; init; }
    public required IReadOnlyList<string> CriticalUnknowns { get; init; }
    public string ContextHash => Hashing.Sha256Hex(ContextJson);
}

public sealed record AgentRun(string Agent, bool FromCache, AIUsage Usage, int Accepted, int Rejected, string? Error);

public sealed record VerificationResult(string Claim, string Agent, string Verdict, double Confidence, string Rationale);

public sealed record AIAnalysisResult
{
    public required IReadOnlyList<Finding> Findings { get; init; }
    public required IReadOnlyList<RejectedClaim> Rejected { get; init; }
    public required IReadOnlyList<VerificationResult> Verifications { get; init; }
    public required IReadOnlyList<ComponentConsensus> Consensus { get; init; }
    public required Severity Verdict { get; init; }
    public required double Confidence { get; init; }
    public IReadOnlyList<string> Contradictions { get; init; } = [];
    public IReadOnlyList<string> MissingData { get; init; } = [];
    public required IReadOnlyList<AgentRun> Runs { get; init; }
    public required AIUsage Usage { get; init; }
    public required string Model { get; init; }
}

public sealed record AssistantAnswer(string Answer, double Confidence, IReadOnlyList<Evidence> Evidence, IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Unknowns, IReadOnlyList<string> SuggestedChecks, bool FromCache, int RejectedEvidence);

public sealed record MapHypothesisResult(string Role, double Confidence, string Rationale, IReadOnlyList<Evidence> Evidence);

/// <summary>
/// Runs the multi-agent review: analysts in parallel → independent verification of critical claims →
/// safety reviewer → consensus with physics. Every call goes through the AI cache first.
/// </summary>
public sealed class AIOrchestrator(IAIProvider provider, IAICacheStore cache, string model, int maxParallel = 3)
{
    public async Task<AIAnalysisResult> AnalyzeAsync(AIContext context, IProgress<StepProgress>? progress = null, CancellationToken ct = default)
    {
        var runs = new List<AgentRun>();
        var findings = new List<Finding>();
        var rejected = new List<RejectedClaim>();
        var outputs = new Dictionary<string, JsonElement>();
        var gate = new SemaphoreSlim(maxParallel);
        var lockObj = new object();
        var done = 0;

        await Task.WhenAll(Agents.Analysts.Select(async agent =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var (json, run) = await CallAsync(agent, context, agent.Instruction + Focus(agent), Schemas.Findings(), ct);
                List<Finding> acc = [];
                List<RejectedClaim> rej = [];
                if (json is { } j) (acc, rej) = AIResponseValidator.ParseFindings(j, agent.Name, context.AllowedRefs);
                lock (lockObj)
                {
                    findings.AddRange(acc);
                    rejected.AddRange(rej);
                    if (json is { } j2) outputs[agent.Name] = j2;
                    runs.Add(run with { Accepted = acc.Count, Rejected = rej.Count });
                }
            }
            finally
            {
                gate.Release();
                progress?.Report(new StepProgress("ai", "Claude review", StepState.Running, 0.6 * Interlocked.Increment(ref done) / Agents.Analysts.Count, agent.Name));
            }
        }));

        // Independent verification of critical claims.
        var verifications = new List<VerificationResult>();
        foreach (var f in findings.Where(f => f.Severity is Severity.Warning or Severity.Danger).ToList())
        {
            var claim = JsonSerializer.Serialize(new { finding = f.Text, severity = f.Severity.ToWire(), evidence = f.Evidence, agent = f.SourceDetail }, Json.Options);
            var (json, run) = await CallAsync(Agents.Verifier, context, Agents.Verifier.Instruction + $"\n<claim>{claim}</claim>", Schemas.Verification(), ct);
            runs.Add(run);
            if (json is not { } j) continue;
            var verdict = AIResponseValidator.Str(j, "verdict") ?? "INSUFFICIENT";
            var vconf = Math.Clamp(AIResponseValidator.Num(j, "confidence") ?? 0, 0, 1);
            verifications.Add(new VerificationResult(f.Text, f.SourceDetail ?? "", verdict, vconf, AIResponseValidator.Str(j, "rationale") ?? ""));
            var idx = findings.IndexOf(f);
            findings[idx] = verdict switch
            {
                "CONTRADICTED" => f with { Severity = Severity.Review, Confidence = Math.Round(f.Confidence * 0.4, 2), Assumptions = [.. f.Assumptions, "Contradicted by independent verification"] },
                "INSUFFICIENT" => f with { Confidence = Math.Round(f.Confidence * 0.6, 2), Unknowns = [.. f.Unknowns, "Independent verification: insufficient evidence"] },
                _ => f with { Confidence = Math.Round(Math.Min(1, (f.Confidence + vconf) / 2 + 0.05), 2) },
            };
        }
        progress?.Report(new StepProgress("ai", "Claude review", StepState.Running, 0.8, "Safety Reviewer"));

        // Safety reviewer sees all analyst outputs.
        var analystJson = JsonSerializer.Serialize(outputs, Json.Options);
        var (review, reviewRun) = await CallAsync(Agents.SafetyReviewer, context,
            Agents.SafetyReviewer.Instruction + $"\n<analyst_outputs>{analystJson}</analyst_outputs>", Schemas.SafetyReview(), ct);
        var (revFindings, revRejected) = review is { } rj ? AIResponseValidator.ParseFindings(rj, Agents.SafetyReviewer.Name, context.AllowedRefs) : ([], []);
        runs.Add(reviewRun with { Accepted = revFindings.Count, Rejected = revRejected.Count });
        findings.AddRange(revFindings);
        rejected.AddRange(revRejected);
        var reviewerVerdict = review is { } r2 ? SeverityExtensions.Parse(AIResponseValidator.Str(r2, "verdict") ?? "UNKNOWN") : Severity.Unknown;

        // Consensus per component.
        var consensus = context.PhysicsComponents.Select(pc =>
        {
            var votes = findings.Where(f => f.AffectedComponents.Contains(pc.Component, StringComparer.OrdinalIgnoreCase))
                .GroupBy(f => f.SourceDetail ?? "ai")
                .Select(g => new ComponentVote(g.Key, SeverityExtensions.Worst(g.Select(x => x.Severity)), g.Max(x => x.Confidence)))
                .ToList();
            return ConsensusEngine.Combine(pc.Component, pc.Severity, pc.Confidence, votes, context.CriticalUnknowns);
        }).ToList();

        var physicsOverall = SeverityExtensions.Worst(consensus.Select(c => c.FinalSeverity));
        var verdictFinal = reviewerVerdict.Rank() > physicsOverall.Rank() && reviewerVerdict != Severity.Unknown
            ? (reviewerVerdict == Severity.Danger && physicsOverall.Rank() < Severity.Warning.Rank() ? Severity.Warning : reviewerVerdict)
            : physicsOverall;
        verdictFinal = AIResponseValidator.GuardSafe(verdictFinal, context.CriticalUnknowns);
        var agreement = consensus.Count == 0 ? 0 : consensus.Average(c => c.Agreement);
        var reviewerConf = review is { } r3 ? Math.Clamp(AIResponseValidator.Num(r3, "confidence") ?? 0, 0, 1) : 0;
        var conf = Math.Round(Math.Min(reviewerConf, consensus.Count == 0 ? 0 : consensus.Average(c => c.Confidence)) * (verdictFinal == reviewerVerdict ? 1 : 0.8), 2);
        progress?.Report(new StepProgress("ai", "Claude review", StepState.Done, 1));

        return new AIAnalysisResult
        {
            Findings = findings,
            Rejected = rejected,
            Verifications = verifications,
            Consensus = consensus,
            Verdict = verdictFinal,
            Confidence = conf,
            Contradictions = review is { } r4 ? AIResponseValidator.StrArr(r4, "contradictions") : [],
            MissingData = outputs.Values.SelectMany(o => AIResponseValidator.StrArr(o, "missing_data")).Distinct().ToList(),
            Runs = runs,
            Usage = runs.Aggregate(AIUsage.Zero, (a, r) => a.Add(r.Usage)),
            Model = model,
        };
    }

    public async Task<AssistantAnswer> AskAsync(AIContext context, string question, string selectionJson, CancellationToken ct = default)
    {
        var instruction = Agents.Assistant.Instruction + $"\n<selection>{selectionJson}</selection>\n<question>{question}</question>";
        var (json, run) = await CallAsync(Agents.Assistant, context, instruction, Schemas.AssistantAnswer(), ct, throwOnError: true);
        var j = json!.Value;
        var evidence = AIResponseValidator.ParseEvidence(j, context.AllowedRefs, out var invalid);
        var conf = Math.Clamp(AIResponseValidator.Num(j, "confidence") ?? 0, 0, 1);
        if (evidence.Count == 0) conf = Math.Min(conf, 0.2);
        return new AssistantAnswer(AIResponseValidator.Str(j, "answer") ?? "", Math.Round(conf, 2), evidence,
            AIResponseValidator.StrArr(j, "assumptions"), AIResponseValidator.StrArr(j, "unknowns"), AIResponseValidator.StrArr(j, "suggested_checks"), run.FromCache, invalid.Count);
    }

    public async Task<List<MapHypothesisResult>> HypothesesAsync(AIContext context, string candidateJson, IEnumerable<string> roles, CancellationToken ct = default)
    {
        var (json, _) = await CallAsync(Agents.UnknownMap, context, Agents.UnknownMap.Instruction + $"\n<candidate>{candidateJson}</candidate>", Schemas.MapHypotheses(roles), ct, throwOnError: true);
        var list = new List<MapHypothesisResult>();
        if (!json!.Value.TryGetProperty("hypotheses", out var arr)) return list;
        foreach (var h in arr.EnumerateArray())
        {
            var ev = AIResponseValidator.ParseEvidence(h, context.AllowedRefs, out _);
            var c = Math.Clamp(AIResponseValidator.Num(h, "confidence") ?? 0, 0, 1);
            // AI hypotheses are capped: they need human confirmation.
            list.Add(new MapHypothesisResult(AIResponseValidator.Str(h, "role") ?? "Unknown", Math.Round(Math.Min(c, ev.Count == 0 ? 0.3 : 0.8), 2), AIResponseValidator.Str(h, "rationale") ?? "", ev));
        }
        return list.OrderByDescending(h => h.Confidence).ToList();
    }

    private async Task<(JsonElement? Json, AgentRun Run)> CallAsync(AgentDefinition agent, AIContext context, string instruction, JsonObject schema, CancellationToken ct, bool throwOnError = false)
    {
        var key = AICacheKeys.Build(context.BinaryHash, context.StockHash, context.ProfileHash, context.AnalysisVersion,
            agent.Name + ":" + Hashing.Sha256Hex(instruction), context.ContextHash, model);
        var cached = await cache.GetAsync(key, ct);
        if (cached is not null)
            return (JsonDocument.Parse(cached.Json).RootElement.Clone(), new AgentRun(agent.Name, true, AIUsage.Zero, 0, 0, null));
        try
        {
            var response = await provider.CompleteJsonAsync(new AIRequest
            {
                SystemPrompt = Agents.SystemPrompt,
                ContextJson = context.ContextJson,
                Instruction = instruction,
                OutputSchema = schema,
                Effort = agent.Effort,
                Model = model,
            }, ct);
            await cache.PutAsync(new AICacheEntry(key, agent.Name, response.Model, response.Json.GetRawText(), response.Usage, DateTimeOffset.UtcNow), ct);
            return (response.Json, new AgentRun(agent.Name, false, response.Usage, 0, 0, null));
        }
        catch (EcuStudioException ex) when (!throwOnError)
        {
            return (null, new AgentRun(agent.Name, false, AIUsage.Zero, 0, 0, ex.Message));
        }
    }

    private static string Focus(AgentDefinition a) => $"\nFocus on context sections: {string.Join(", ", a.ContextSections)}.";
}
