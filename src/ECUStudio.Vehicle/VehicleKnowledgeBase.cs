using System.Reflection;
using System.Text.Json;
using ECUStudio.Components;
using ECUStudio.Core;

namespace ECUStudio.Vehicle;

public sealed record VehicleVariant
{
    public required string Id { get; init; }
    public required string Make { get; init; }
    public required string Model { get; init; }
    public IReadOnlyList<string> Wmi { get; init; } = [];
    public IReadOnlyList<string> PlatformCodes { get; init; } = [];
    public int YearFrom { get; init; }
    public int YearTo { get; init; }
    public required string EngineCode { get; init; }
    public required string EngineFamily { get; init; }
    public IReadOnlyList<string> EcuPlugins { get; init; } = [];
    public required Dictionary<string, string> Components { get; init; }
    public IReadOnlyList<string> TransmissionOptions { get; init; } = [];
    public string? Note { get; init; }
}

public sealed class VehicleKnowledgeBase(IReadOnlyList<VehicleVariant> variants, ComponentCatalog catalog)
{
    public IReadOnlyList<VehicleVariant> Variants { get; } = variants;
    public ComponentCatalog Catalog { get; } = catalog;

    public static VehicleKnowledgeBase LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ECUStudio.Vehicle.Data.vehicles.json")
            ?? throw new InvalidOperationException("Embedded vehicle knowledge base missing");
        var variants = JsonSerializer.Deserialize<List<VehicleVariant>>(stream, Json.Options) ?? [];
        return new VehicleKnowledgeBase(variants, ComponentCatalog.LoadEmbedded());
    }

    public VehicleVariant? Get(string id) => Variants.FirstOrDefault(v => v.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
