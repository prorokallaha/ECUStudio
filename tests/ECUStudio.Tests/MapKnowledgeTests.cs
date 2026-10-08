using System.Text;
using ECUStudio.AI;
using ECUStudio.Application.Analysis;
using ECUStudio.Application.Projects;
using ECUStudio.Calibration.Scanning;
using ECUStudio.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ECUStudio.Tests;

public class MapKnowledgeTests
{
    private static ServiceProvider Services(IAIProvider? ai = null)
    {
        var services = new ServiceCollection().AddEcuStudio(new EcuStudioOptions { Storage = "memory", AnthropicApiKey = null });
        if (ai is not null) services.AddSingleton(ai);
        return services.BuildServiceProvider();
    }

    private static async Task<AnalysisSession> Analyse(StudioService studio, string name, byte[] image)
    {
        var project = await studio.CreateProjectAsync(name, null, null);
        var file = await studio.AddFileAsync(project.Id, name, image, FileRole.Modified, null, null);
        return await studio.RunAnalysisAsync(await studio.GetProjectAsync(project.Id), file, null, null, CancellationToken.None);
    }

    [Fact]
    public async Task Confirmation_carries_over_to_the_same_software_only_with_identical_structure()
    {
        await using var sp = Services();
        var studio = sp.GetRequiredService<StudioService>();
        var first = await Analyse(studio, "stock.bin", Fixtures.Stock.Value.Image);
        var candidate = first.Report.Candidates.First(c => c.Status == CandidateStatus.Candidate);
        await studio.DecideCandidateAsync(first.Report.Id, candidate.Id, "confirm", "SmokeLimiter", "checked against logs");

        // Same SW/HW, different file: the confirmation applies.
        var stage1 = await Analyse(studio, "stage1.bin", Fixtures.Stage1.Value.Image);
        var carried = stage1.Report.Candidates.Single(c => c.Address == candidate.Address);
        Assert.Equal(CandidateStatus.Confirmed, carried.Status);
        Assert.Contains(stage1.Report.DefinitionNotes, n => n.Contains("confirmed earlier", StringComparison.Ordinal));

        // Different SW number: never transferred.
        var other = (byte[])Fixtures.Stock.Value.Image.Clone();
        var at = other.AsSpan().IndexOf(Encoding.ASCII.GetBytes("1037399999"));
        other[at + 9] = (byte)'8';
        var otherSw = await Analyse(studio, "other.bin", other);
        Assert.NotEqual(CandidateStatus.Confirmed, otherSw.Report.Candidates.Single(c => c.Address == candidate.Address).Status);
    }

    [Fact]
    public async Task Map_investigation_sends_a_small_slice_and_caps_confidence()
    {
        AIRequest? seen = null;
        var fake = new FakeProvider(r =>
        {
            seen = r;
            return """
            {"purpose":"TorqueLimiter","purpose_text":"Ограничитель момента","confidence":0.97,
             "evidence":[{"type":"MAP","ref":"map:torque_limiter","detail":"rpm × atmospheric pressure axes"}],
             "counter_evidence":["values could also be a smoke limit"],"value_unit":"Nm","x_axis":"rpm","y_axis":"mbar",
             "related_maps":["driver_wish"],"verification_steps":["Log requested vs actual torque at altitude"],
             "alternatives":[{"purpose":"SmokeLimiter","confidence":0.95,"rationale":"similar shape"}]}
            """;
        });
        await using var sp = Services(fake);
        var studio = sp.GetRequiredService<StudioService>();
        var session = await Analyse(studio, "stock.bin", Fixtures.Stock.Value.Image);

        var result = await studio.InvestigateMapAsync(session.Report.Id, "torque_limiter", null, "ru");

        Assert.Equal("TorqueLimiter", result.Purpose);
        Assert.Equal(0.8, result.Confidence); // AI output is a hypothesis: capped
        Assert.Equal(0.8, result.Alternatives[0].Confidence);
        Assert.NotEmpty(result.CounterEvidence);
        Assert.NotEmpty(result.VerificationSteps);
        Assert.InRange(result.BytesSent, 1, 512);
        Assert.NotNull(seen);
        Assert.Contains("<language>Russian</language>", seen!.Instruction, StringComparison.Ordinal);
        Assert.True(seen.Instruction.Length < 20_000, $"instruction is {seen.Instruction.Length} chars");
        Assert.DoesNotContain(Convert.ToHexString(Fixtures.Stock.Value.Image.AsSpan(0x50000, 4096)), seen.Instruction + seen.ContextJson, StringComparison.Ordinal);
    }
}
