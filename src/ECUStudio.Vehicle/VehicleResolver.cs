using ECUStudio.Components;
using ECUStudio.Core;

namespace ECUStudio.Vehicle;

public sealed record VariantCandidate(VehicleVariant Variant, double Probability, IReadOnlyList<string> Reasons);

public sealed record VehicleProfile
{
    public Param Make { get; init; } = Param.Unknown();
    public Param Model { get; init; } = Param.Unknown();
    public Param ModelYear { get; init; } = Param.Unknown();
    public Param Vin { get; init; } = Param.Unknown();
    public Param Platform { get; init; } = Param.Unknown();
    public Param EngineCode { get; init; } = Param.Unknown();
    public string? VariantId { get; init; }
    public double VariantProbability { get; init; }
    public required HardwareProfile Hardware { get; init; }
    public IReadOnlyList<HardwareOverride> Overrides { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Stable hash of everything that influences physics/risk (part of the AI cache key).</summary>
    public string ProfileHash => Hashing.StableHash(new { VariantId, Hardware, Overrides });
}

public sealed record VehicleResolution(VinInfo? Vin, IReadOnlyList<VariantCandidate> Candidates, VehicleProfile Profile);

/// <summary>
/// VIN + ECU identification → ranked VehicleVariant candidates → VehicleProfile.
/// The VIN is evidence, not proof: probabilities stay below certainty and every inferred
/// component keeps its own source/confidence until the user verifies or overrides it.
/// </summary>
public sealed class VehicleResolver(VehicleKnowledgeBase kb)
{
    public VehicleResolution Resolve(string? vin, string? ecuPluginId, IReadOnlyList<string> engineFamilies,
        IReadOnlyList<HardwareOverride>? overrides = null, string? preferredVariantId = null, string? transmissionId = null)
    {
        VinInfo? info = null;
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(vin))
        {
            info = VinDecoder.Decode(vin);
            notes.AddRange(info.Warnings);
        }

        var scored = new List<(VehicleVariant V, double S, List<string> R)>();
        foreach (var v in kb.Variants)
        {
            double s = 1;
            var reasons = new List<string>();
            if (ecuPluginId is not null)
            {
                if (v.EcuPlugins.Contains(ecuPluginId, StringComparer.OrdinalIgnoreCase)) { s *= 2; reasons.Add($"ECU {ecuPluginId} fitted to this variant"); }
                else s *= 0.05;
            }
            if (engineFamilies.Count > 0 && !engineFamilies.Contains(v.EngineFamily)) s *= 0.1;
            if (info is not null)
            {
                if (v.Wmi.Contains(info.Wmi)) { s *= 2; reasons.Add($"WMI {info.Wmi}"); } else s *= 0.1;
                if (info.PlatformCode is { } pc && v.PlatformCodes.Contains(pc)) { s *= 4; reasons.Add($"platform code {pc}"); }
                else if (info.PlatformCode is not null) s *= 0.15;
                if (info.ModelYear is { } y)
                {
                    if (y >= v.YearFrom && y <= v.YearTo) { s *= 1.5; reasons.Add($"model year {y}"); } else s *= 0.2;
                }
            }
            if (preferredVariantId is not null && v.Id.Equals(preferredVariantId, StringComparison.OrdinalIgnoreCase)) { s *= 50; reasons.Add("selected by user"); }
            scored.Add((v, s, reasons));
        }

        var total = scored.Sum(x => x.S);
        var candidates = scored
            .OrderByDescending(x => x.S)
            .Select(x => new VariantCandidate(x.V, total == 0 ? 0 : Math.Round(x.S / total, 3), x.R))
            .Where(c => c.Probability >= 0.01)
            .ToList();

        var best = candidates.FirstOrDefault();
        var hardware = BuildHardware(best, transmissionId, notes);
        overrides ??= [];
        hardware = kb.Catalog.Apply(hardware, overrides);

        // Probability never reaches certainty from VIN/ECU alone.
        var p = best is null ? 0 : Math.Min(best.Probability, preferredVariantId is null ? 0.85 : 0.95);
        if (best is not null && candidates.Count > 1 && candidates[1].Variant.EngineCode != best.Variant.EngineCode)
            notes.Add($"Engine code ambiguous: {string.Join(", ", candidates.Take(4).Select(c => $"{c.Variant.EngineCode} ({c.Probability:P0})"))}");

        var profile = new VehicleProfile
        {
            Make = best is null ? Param.Unknown() : Param.Str(best.Variant.Make, info is null ? SourceType.VariantTypical : SourceType.VinDecode, p),
            Model = best is null ? Param.Unknown() : Param.Str(best.Variant.Model, info is null ? SourceType.VariantTypical : SourceType.VinDecode, p),
            ModelYear = info?.ModelYear is { } year ? Param.Num(year, "-", SourceType.VinDecode, 0.9) : Param.Unknown(),
            Vin = info is null ? Param.Unknown() : Param.Str(info.Vin, SourceType.User, 1),
            Platform = info?.PlatformCode is { } pc2 ? Param.Str(pc2, SourceType.VinDecode, 0.85) : Param.Unknown(),
            EngineCode = best is null ? Param.Unknown() : Param.Str(best.Variant.EngineCode, SourceType.VariantTypical, Math.Round(p * 0.8, 2), "VIN does not encode engine code"),
            VariantId = best?.Variant.Id,
            VariantProbability = p,
            Hardware = hardware,
            Overrides = overrides,
            Notes = notes,
        };
        return new VehicleResolution(info, candidates, profile);
    }

    private HardwareProfile BuildHardware(VariantCandidate? best, string? transmissionId, List<string> notes)
    {
        ComponentSpec Pick(ComponentKind kind, string key)
        {
            if (best is null || !best.Variant.Components.TryGetValue(key, out var id)) return ComponentSpec.Unknown(kind);
            var spec = kb.Catalog.Get(id) ?? ComponentSpec.Unknown(kind);
            // Inferred from the variant: confidence capped by the variant probability.
            return spec with { Confidence = Math.Round(Math.Min(spec.Confidence, Math.Max(0.1, best.Probability)), 2) };
        }

        var transmission = Pick(ComponentKind.Transmission, "transmission");
        if (transmissionId is not null && kb.Catalog.Get(transmissionId) is { } t) transmission = t with { UserVerified = true, Source = SourceType.User };
        else if (best is not null && best.Variant.TransmissionOptions.Count > 1)
            notes.Add($"Transmission not determined from VIN; options: {string.Join(", ", best.Variant.TransmissionOptions)}. Defaulting to {transmission.Name}.");

        return new HardwareProfile
        {
            Engine = Pick(ComponentKind.Engine, "engine"),
            Turbo = Pick(ComponentKind.Turbo, "turbo"),
            Injectors = Pick(ComponentKind.Injectors, "injectors"),
            FuelSystem = Pick(ComponentKind.FuelSystem, "fuelSystem"),
            Transmission = transmission,
            Clutch = transmission.Id == "trans_dsg_dq250" ? kb.Catalog.Get("clutch_dsg_integrated")! : Pick(ComponentKind.Clutch, "clutch"),
            Intercooler = Pick(ComponentKind.Intercooler, "intercooler"),
            Sensors = Pick(ComponentKind.Sensors, "sensors"),
            Emissions = Pick(ComponentKind.Emissions, "emissions"),
        };
    }
}
