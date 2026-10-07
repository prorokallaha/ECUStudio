using System.Text.Json;
using ECUStudio.AI;
using ECUStudio.Application.Analysis;
using ECUStudio.Core;

namespace ECUStudio.Tests;

/// <summary>Scripted provider: answers by output schema shape, counts calls. No network.</summary>
internal sealed class FakeProvider(Func<AIRequest, string> respond) : IAIProvider
{
    public int Calls;
    public string Name => "fake";
    public bool IsConfigured => true;
    public Task<AIResponse> CompleteJsonAsync(AIRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        var json = JsonDocument.Parse(respond(request)).RootElement.Clone();
        return Task.FromResult(new AIResponse(json, request.Model ?? "fake", new AIUsage(1000, 200, 0, 0), "end_turn"));
    }
}

public class AIValidationTests
{
    private static readonly HashSet<string> Refs = ["map:torque_limiter", "risk:turbo"];

    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void Findings_without_resolvable_evidence_are_rejected()
    {
        var (acc, rej) = AIResponseValidator.ParseFindings(J("""
            {"findings":[
              {"finding":"Torque limiter raised 20%","severity":"REVIEW","confidence":0.8,"evidence":[{"type":"map","ref":"map:torque_limiter","detail":"+20%"}]},
              {"finding":"Pistons will melt","severity":"DANGER","confidence":0.9,"evidence":[]},
              {"finding":"Injector duty 110%","severity":"DANGER","confidence":0.9,"evidence":[{"type":"map","ref":"map:invented"}]}
            ]}
            """), "Calibration Analyst", Refs);
        var f = Assert.Single(acc);
        Assert.Equal(FindingSource.AI, f.Source);
        Assert.Equal(Severity.Review, f.Severity);
        Assert.Equal(2, rej.Count);
        Assert.Contains(rej, r => r.Reason == "No evidence");
        Assert.Contains(rej, r => r.Reason.Contains("map:invented"));
    }

    [Fact]
    public void Partially_unsupported_evidence_reduces_confidence()
    {
        var (acc, _) = AIResponseValidator.ParseFindings(J("""
            {"findings":[{"finding":"x","severity":"WARNING","confidence":1.0,
              "evidence":[{"type":"risk","ref":"risk:turbo"},{"type":"map","ref":"map:nope"}]}]}
            """), "Turbo Analyst", Refs);
        Assert.Equal(0.8, Assert.Single(acc).Confidence);
    }

    [Fact]
    public void Safe_is_downgraded_to_unknown_while_critical_parameters_are_unknown()
    {
        Assert.Equal(Severity.Unknown, AIResponseValidator.GuardSafe(Severity.Safe, ["turbo max pressure ratio"]));
        Assert.Equal(Severity.Safe, AIResponseValidator.GuardSafe(Severity.Safe, []));
        Assert.Equal(Severity.Warning, AIResponseValidator.GuardSafe(Severity.Warning, ["x"]));
    }
}

public class ConsensusTests
{
    [Fact]
    public void AI_can_never_lower_physics_severity()
    {
        var c = ConsensusEngine.Combine("turbo", Severity.Warning, 0.6, [new("Turbo Analyst", Severity.Safe, 0.95), new("Safety Reviewer", Severity.Safe, 0.95)], []);
        Assert.Equal(Severity.Warning, c.FinalSeverity);
        Assert.True(c.Agreement < 1);
    }

    [Fact]
    public void AI_alone_escalates_at_most_to_warning()
    {
        var c = ConsensusEngine.Combine("engine", Severity.Review, 0.6, [new("Engine Analyst", Severity.Danger, 0.9)], []);
        Assert.Equal(Severity.Warning, c.FinalSeverity);
        Assert.Contains("Escalated", c.Rationale);
    }

    [Fact]
    public void Low_confidence_AI_votes_do_not_escalate()
    {
        var c = ConsensusEngine.Combine("engine", Severity.Review, 0.6, [new("Engine Analyst", Severity.Danger, 0.2)], []);
        Assert.Equal(Severity.Review, c.FinalSeverity);
    }

    [Fact]
    public void Disagreement_lowers_confidence()
    {
        var agree = ConsensusEngine.Combine("x", Severity.Review, 0.7, [new("a", Severity.Review, 0.7)], []);
        var split = ConsensusEngine.Combine("x", Severity.Review, 0.7, [new("a", Severity.Warning, 0.7)], []);
        Assert.True(split.Confidence < agree.Confidence);
    }
}

public class OrchestratorTests
{
    private static readonly Lazy<AIContext> Context = new(() => AIContextBuilder.Build(Fixtures.Pipeline().Run(new AnalysisRequest
    {
        Modified = Fixtures.Image(Fixtures.Stage1, "stage1.bin"),
        Stock = Fixtures.Image(Fixtures.Stock, "stock.bin"),
        Vin = Fixtures.GolfVin,
        Grid = Fixtures.FastGrid,
    })));

    private static string Respond(AIRequest r, string evidenceRef)
    {
        var props = r.OutputSchema["properties"]!.AsObject();
        if (props.ContainsKey("verdict") && props.ContainsKey("rationale"))
            return """{"verdict":"SUPPORTED","confidence":0.7,"rationale":"consistent with map diff"}""";
        var finding = $$"""{"finding":"Turbo works harder at altitude","severity":"SAFE","confidence":0.9,"affected_components":["turbo"],"evidence":[{"type":"risk","ref":"{{evidenceRef}}","detail":"pr"}]}""";
        if (props.ContainsKey("verdict"))
            return $$"""{"verdict":"SAFE","confidence":0.9,"findings":[{{finding}}],"contradictions":[]}""";
        return $$"""{"findings":[{{finding}}],"missing_data":["turbo compressor map"]}""";
    }

    [Fact]
    public void Context_contains_no_raw_binary_and_has_evidence_index()
    {
        var ctx = Context.Value;
        Assert.NotEmpty(ctx.AllowedRefs);
        Assert.True(ctx.ContextJson.Length < 400_000, $"context {ctx.ContextJson.Length} chars");
        Assert.DoesNotContain(Convert.ToBase64String(Fixtures.Stage1.Value.Image.AsSpan(0x40000, 64)), ctx.ContextJson);
        // Re-running the same inputs must give a byte-identical context, otherwise the AI cache never hits.
        var again = AIContextBuilder.Build(Fixtures.Pipeline().Run(new AnalysisRequest
        {
            Modified = Fixtures.Image(Fixtures.Stage1, "stage1.bin"),
            Stock = Fixtures.Image(Fixtures.Stock, "stock.bin"),
            Vin = Fixtures.GolfVin,
            Grid = Fixtures.FastGrid,
        }));
        Assert.Equal(ctx.ContextHash, again.ContextHash);
    }

    [Fact]
    public async Task Analysis_respects_physics_and_safe_guard_and_uses_cache()
    {
        var ctx = Context.Value;
        var evidenceRef = ctx.AllowedRefs.First();
        var provider = new FakeProvider(r => Respond(r, evidenceRef));
        var cache = new InMemoryAICacheStore();
        var orchestrator = new AIOrchestrator(provider, cache, "test-model");

        var first = await orchestrator.AnalyzeAsync(ctx);
        Assert.NotEmpty(first.Findings);
        Assert.All(first.Findings, f => Assert.NotEmpty(f.Evidence));
        // AI said SAFE everywhere; physics found unknowns, so the verdict cannot be SAFE.
        Assert.NotEqual(Severity.Safe, first.Verdict);
        foreach (var c in first.Consensus) Assert.True(c.FinalSeverity.Rank() >= c.PhysicsSeverity.Rank() || c.FinalSeverity == Severity.Unknown, c.Component);
        Assert.Contains("turbo compressor map", first.MissingData);

        var calls = provider.Calls;
        var second = await orchestrator.AnalyzeAsync(ctx);
        Assert.Equal(calls, provider.Calls);
        Assert.All(second.Runs, r => Assert.True(r.FromCache, r.Agent));
        Assert.Equal(first.Verdict, second.Verdict);
    }

    [Fact]
    public async Task Ask_without_valid_evidence_caps_confidence()
    {
        var provider = new FakeProvider(_ => """{"answer":"Probably fine","confidence":0.95,"evidence":[{"type":"map","ref":"map:does_not_exist"}],"assumptions":[],"unknowns":[],"suggested_checks":[]}""");
        var answer = await new AIOrchestrator(provider, new InMemoryAICacheStore(), "m").AskAsync(Context.Value, "Is it safe?", "{}");
        Assert.True(answer.Confidence <= 0.2);
        Assert.Equal(1, answer.RejectedEvidence);
        Assert.Empty(answer.Evidence);
    }

    [Fact]
    public async Task Unconfigured_provider_fails_clearly()
    {
        var o = new AIOrchestrator(new UnconfiguredAIProvider(), new InMemoryAICacheStore(), "m");
        var ex = await Assert.ThrowsAsync<AIUnavailableException>(() => o.AskAsync(Context.Value, "q", "{}"));
        Assert.Equal(503, ex.HttpStatus);
    }
}
