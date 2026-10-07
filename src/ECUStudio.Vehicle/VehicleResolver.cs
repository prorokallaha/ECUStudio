using System.Globalization;
using System.Text.Json.Serialization;
using ECUStudio.Components;
using ECUStudio.Core;
using ECUStudio.Vehicle.Resolution;

namespace ECUStudio.Vehicle;

/// <summary>How one piece of evidence relates to a variant.</summary>
public sealed record FactCheck(VehicleFact Fact, string Evidence, string? VariantValue, bool? Match, double Confidence, string Provider);

public sealed record VariantCandidate(VehicleVariant Variant, double Probability, IReadOnlyList<string> Reasons)
{
    /// <summary>Evidence this variant contradicts (e.g. "displacement 2.0 L from ECU, variant has 1.9 L").</summary>
    public IReadOnlyList<string> Conflicts { get; init; } = [];
    public IReadOnlyList<FactCheck> Checks { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<VehicleMatchStatus>))]
public enum VehicleMatchStatus
{
    /// <summary>One engine/hardware configuration clearly ahead.</summary>
    Resolved,
    /// <summary>Several catalogued variants remain plausible.</summary>
    Ambiguous,
    /// <summary>Strong evidence contradicts every catalogued variant: the vehicle is not in the database.</summary>
    NotInDatabase,
    /// <summary>No VIN and no ECU facts.</summary>
    NoData,
}

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

public sealed record VehicleResolution(VinInfo? Vin, IReadOnlyList<VariantCandidate> Candidates, VehicleProfile Profile)
{
    public VehicleMatchStatus Status { get; init; } = VehicleMatchStatus.Ambiguous;
    /// <summary>Probability that the vehicle is not any catalogued variant.</summary>
    public double UnlistedProbability { get; init; }
    public IReadOnlyList<VehicleEvidence> Evidence { get; init; } = [];
    public IReadOnlyList<ConfigurationItem> Configuration { get; init; } = [];
}

/// <summary>
/// Combines evidence from independent providers (VIN, ECU identification, OEM catalogue, binary fingerprints, user
/// knowledge, user overrides) against variant hypotheses from vehicle databases. Each fact either matches, contradicts
/// or does not apply to a variant; contradictions are weighted by the evidence confidence. An explicit "not in
/// database" hypothesis keeps the result honest when the knowledge base lacks the vehicle: the best catalogued variant
/// is never adopted if strong evidence (ECU engine string, ECU family, VIN platform) contradicts it.
/// </summary>
public sealed class VehicleResolver
{
    /// <summary>Prior weight of "a vehicle the database does not contain" relative to one catalogued variant.</summary>
    public const double UnlistedPrior = 0.1;
    /// <summary>Evidence at or above this confidence is decisive: a variant contradicting it is never adopted.</summary>
    public const double HardEvidence = 0.85;

    private readonly VehicleKnowledgeBase _kb;
    private readonly IReadOnlyList<IVehicleDatabaseProvider> _databases;
    private readonly IReadOnlyList<IVehicleEvidenceProvider> _providers;

    public VehicleResolver(VehicleKnowledgeBase kb, IEnumerable<IVehicleEvidenceProvider>? providers = null, IEnumerable<IVehicleDatabaseProvider>? databases = null)
    {
        _kb = kb;
        _databases = databases?.ToList() ?? [new LocalVehicleDatabaseProvider(kb)];
        _providers = providers?.ToList() ?? DefaultProviders();
    }

    public static IReadOnlyList<IVehicleEvidenceProvider> DefaultProviders(IVehicleKnowledgeStore? store = null, Func<IEnumerable<SoftwareFingerprint>>? fingerprints = null) =>
    [
        new VinProvider(), new EcuIdentificationProvider(), OemCatalogProvider.LoadEmbedded(),
        new BinaryFingerprintProvider(fingerprints ?? (() => [])), new LocalKnowledgeProvider(store ?? new InMemoryVehicleKnowledgeStore()), new UserOverrideProvider(),
    ];

    public VehicleResolution Resolve(VehicleQuery query)
    {
        var notes = new List<string>();
        VinInfo? vin = null;
        if (!string.IsNullOrWhiteSpace(query.Vin))
        {
            vin = VinDecoder.Decode(query.Vin);
            notes.AddRange(vin.Warnings);
        }

        var evidence = _providers.SelectMany(p => p.Collect(query, vin)).ToList();
        var variants = _databases.SelectMany(d => d.Variants).GroupBy(v => v.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

        var scored = new List<(VehicleVariant V, double S, List<FactCheck> Checks)>();
        foreach (var v in variants)
        {
            double s = 1;
            var checks = new List<FactCheck>();
            foreach (var e in evidence)
            {
                var (match, value) = Check(v, e);
                checks.Add(new FactCheck(e.Fact, e.Value, value, match, e.Confidence, e.Provider));
                s *= match switch
                {
                    true => 1,
                    // Year ranges are fuzzy at the edges (model year vs. production date).
                    false when e.Fact == VehicleFact.ModelYear && int.TryParse(e.Value, out var y) && (y == v.YearFrom - 1 || y == v.YearTo + 1) => 0.6,
                    false => Math.Max(0.005, Math.Pow(1 - e.Confidence, 2)),
                    null => 1,
                };
            }
            scored.Add((v, s, checks));
        }

        var unlisted = UnlistedPrior;
        var total = scored.Sum(x => x.S) + unlisted;
        var candidates = scored
            .OrderByDescending(x => x.S)
            .Select(x => new VariantCandidate(x.V, Math.Round(x.S / total, 3),
                x.Checks.Where(c => c.Match == true).Select(Describe).ToList())
            {
                Conflicts = x.Checks.Where(c => c.Match == false).Select(Describe).ToList(),
                Checks = x.Checks,
            })
            .Where(c => c.Probability >= 0.01)
            .ToList();
        var unlistedP = Math.Round(unlisted / total, 3);

        // An explicit user choice wins over every other source; contradictions are reported, not silently dropped.
        var userChoice = query.PreferredVariantId is { } pid ? candidates.FirstOrDefault(c => c.Variant.Id.Equals(pid, StringComparison.OrdinalIgnoreCase)) : null;
        var best = userChoice ?? candidates.FirstOrDefault();
        var hardConflict = userChoice is not null ? [] : best?.Checks.Where(c => c.Match == false && c.Confidence >= HardEvidence).ToList() ?? [];
        if (userChoice is not null && userChoice.Checks.Where(c => c.Match == false && c.Fact != VehicleFact.Variant).ToList() is { Count: > 0 } contradicted)
            notes.Add($"The selected variant contradicts: {string.Join("; ", contradicted.Select(Describe))}. Verify the selection.");
        var status = evidence.Count == 0 ? VehicleMatchStatus.NoData
            : userChoice is not null ? VehicleMatchStatus.Resolved
            : best is null || hardConflict.Count > 0 || unlistedP > best.Probability ? VehicleMatchStatus.NotInDatabase
            : EngineConfigurationProbability(candidates, best) >= 0.75 ? VehicleMatchStatus.Resolved
            : VehicleMatchStatus.Ambiguous;
        var adopted = status is VehicleMatchStatus.Resolved or VehicleMatchStatus.Ambiguous ? best : null;
        if (status == VehicleMatchStatus.NotInDatabase)
            notes.Add(best is null
                ? "No catalogued variant fits the evidence."
                : $"No catalogued variant fits the evidence; closest is {best.Variant.Model} {best.Variant.EngineCode}, which contradicts: {string.Join("; ", hardConflict.Select(Describe))}. Hardware is not taken from it.");

        var hardware = BuildHardware(adopted is null ? [] : userChoice is not null ? [userChoice with { Probability = 1 }]
                : candidates.Where(c => !c.Checks.Any(k => k.Match == false && k.Confidence >= HardEvidence)).ToList(),
            query, evidence, notes);
        hardware = _kb.Catalog.Apply(hardware, query.Overrides);

        var userSelected = query.PreferredVariantId is not null && adopted?.Variant.Id.Equals(query.PreferredVariantId, StringComparison.OrdinalIgnoreCase) == true;
        // Probability never reaches certainty from VIN/ECU alone.
        var p = adopted is null ? 0 : Math.Min(adopted.Probability, userSelected ? 0.95 : 0.85);
        var engineCodes = userChoice is not null ? [(Code: userChoice.Variant.EngineCode, P: 1.0)] : candidates.GroupBy(c => c.Variant.EngineCode).Select(g => (Code: g.Key, P: g.Sum(c => c.Probability))).OrderByDescending(x => x.P).ToList();
        if (adopted is not null && engineCodes.Count > 1 && engineCodes[0].P < 0.75)
            notes.Add($"Engine code ambiguous: {string.Join(", ", engineCodes.Take(4).Select(c => $"{c.Code} ({c.P:P0})"))}");

        var ecuEngine = EcuIdentificationProvider.ParseEngineText(query.Ecu?.EngineText);
        var profile = new VehicleProfile
        {
            Make = adopted is not null ? Param.Str(adopted.Variant.Make, SourceType.VinDecode, p)
                : vin?.Manufacturer is { } m ? Param.Str(m, SourceType.VinDecode, 0.9) : Param.Unknown(),
            Model = adopted is not null ? Param.Str(adopted.Variant.Model, vin is null ? SourceType.VariantTypical : SourceType.VinDecode, p)
                : vin?.PlatformName is { } pn ? Param.Str(ecuEngine is { } ee ? $"{pn} {ee.Litres}" : pn, SourceType.VinDecode, 0.7, "model line from VIN platform code; engine from ECU string") : Param.Unknown(),
            ModelYear = vin?.ModelYear is { } year ? Param.Num(year, "-", SourceType.VinDecode, 0.9) : Param.Unknown(),
            Vin = vin is null ? Param.Unknown() : Param.Str(vin.Vin, SourceType.User, 1),
            Platform = vin?.PlatformCode is { } pc ? Param.Str(pc, SourceType.VinDecode, 0.85) : Param.Unknown(),
            EngineCode = adopted is null ? Param.Unknown(note: "not determined: no catalogued variant fits")
                : Param.Str(engineCodes[0].Code, SourceType.VariantTypical, Math.Round(Math.Min(p, engineCodes[0].P), 2), "VIN does not encode the engine code"),
            VariantId = adopted?.Variant.Id,
            VariantProbability = p,
            Hardware = hardware,
            Overrides = query.Overrides,
            Notes = notes,
        };

        var resolution = new VehicleResolution(vin, candidates, profile) { Status = status, UnlistedProbability = unlistedP, Evidence = evidence };
        return resolution with { Configuration = VehicleConfigurationResolver.Resolve(resolution, query, _kb.Catalog) };
    }

    /// <summary>Probability mass of candidates sharing the best candidate's engine and ECU (same hardware, maybe other code).</summary>
    private static double EngineConfigurationProbability(IReadOnlyList<VariantCandidate> candidates, VariantCandidate best) =>
        candidates.Where(c => c.Variant.Components.GetValueOrDefault("engine") == best.Variant.Components.GetValueOrDefault("engine")
                              && c.Variant.EcuPlugins.SequenceEqual(best.Variant.EcuPlugins)).Sum(c => c.Probability);

    private (bool? Match, string? VariantValue) Check(VehicleVariant v, VehicleEvidence e)
    {
        switch (e.Fact)
        {
            case VehicleFact.Wmi: return (v.Wmi.Contains(e.Value), string.Join("/", v.Wmi));
            case VehicleFact.Platform: return (v.PlatformCodes.Contains(e.Value), string.Join("/", v.PlatformCodes));
            case VehicleFact.ModelYear:
                return int.TryParse(e.Value, out var y) ? (y >= v.YearFrom && y <= v.YearTo, $"{v.YearFrom}–{v.YearTo}") : (null, null);
            case VehicleFact.EcuPlugin: return (v.EcuPlugins.Contains(e.Value, StringComparer.OrdinalIgnoreCase), string.Join("/", v.EcuPlugins));
            case VehicleFact.Displacement:
            {
                var cc = Engine(v)?.Number(P.DisplacementCc);
                if (cc is null) return (null, null);
                var litres = (Math.Round(cc.Value / 100.0, MidpointRounding.AwayFromZero) / 10).ToString("0.0", CultureInfo.InvariantCulture);
                return (litres == e.Value, litres);
            }
            case VehicleFact.Cylinders:
            {
                var cyl = Engine(v)?.Number(P.Cylinders);
                return cyl is null ? (null, null) : (cyl.Value.ToString(CultureInfo.InvariantCulture) == e.Value, cyl.Value.ToString(CultureInfo.InvariantCulture));
            }
            case VehicleFact.EngineCode: return (e.Value.Split('|').Contains(v.EngineCode, StringComparer.OrdinalIgnoreCase), v.EngineCode);
            case VehicleFact.EngineFamily: return (v.EngineFamily.Equals(e.Value, StringComparison.OrdinalIgnoreCase), v.EngineFamily);
            case VehicleFact.Variant: return (v.Id.Equals(e.Value, StringComparison.OrdinalIgnoreCase), v.Id);
            default: return (null, null);
        }
    }

    private ComponentSpec? Engine(VehicleVariant v) => v.Components.TryGetValue("engine", out var id) ? _kb.Catalog.Get(id) : null;

    private static string Describe(FactCheck c) => c.Fact switch
    {
        VehicleFact.Displacement => $"displacement {c.Evidence} L ({c.Provider}){(c.Match == false ? $", variant has {c.VariantValue} L" : "")}",
        VehicleFact.Cylinders => $"{c.Evidence} cylinders ({c.Provider}){(c.Match == false ? $", variant has {c.VariantValue}" : "")}",
        VehicleFact.EcuPlugin => $"ECU {c.Evidence}{(c.Match == false ? $", variant is fitted with {c.VariantValue}" : " fitted to this variant")}",
        VehicleFact.Platform => $"platform code {c.Evidence}{(c.Match == false ? $", variant is {c.VariantValue}" : "")}",
        VehicleFact.Wmi => $"WMI {c.Evidence}",
        VehicleFact.ModelYear => $"model year {c.Evidence}{(c.Match == false ? $", variant built {c.VariantValue}" : "")}",
        VehicleFact.EngineCode => $"engine code {c.Evidence.Replace("|", "/", StringComparison.Ordinal)} ({c.Provider})",
        VehicleFact.Variant => c.Match == true ? "selected by user" : "user selected another variant",
        _ => $"{c.Fact} {c.Evidence}",
    };

    private HardwareProfile BuildHardware(IReadOnlyList<VariantCandidate> candidates, VehicleQuery query, List<VehicleEvidence> evidence, List<string> notes)
    {
        // Per component kind: the component id with the highest summed probability across compatible candidates.
        ComponentSpec Pick(ComponentKind kind, string key)
        {
            var groups = candidates.Where(c => c.Variant.Components.ContainsKey(key))
                .GroupBy(c => c.Variant.Components[key]).Select(g => (Id: g.Key, P: g.Sum(c => c.Probability))).ToList();
            if (groups.Count == 0) return ComponentSpec.Unknown(kind);
            var best = groups.MaxBy(x => x.P);
            if (_kb.Catalog.Get(best.Id) is not { } spec) return ComponentSpec.Unknown(kind);
            // Inferred from variants: confidence capped by how many of them agree.
            return spec with { Confidence = Math.Round(Math.Min(spec.Confidence, Math.Max(0.1, best.P)), 2) };
        }

        var engine = candidates.Count == 0 ? EvidenceOnlyEngine(evidence) : Pick(ComponentKind.Engine, "engine");
        var transmission = Pick(ComponentKind.Transmission, "transmission");
        if (query.TransmissionId is not null && _kb.Catalog.Get(query.TransmissionId) is { } t) transmission = t with { UserVerified = true, Source = SourceType.User };
        else if (candidates.FirstOrDefault() is { } top && top.Variant.TransmissionOptions.Count > 1)
            notes.Add($"Transmission not determined from VIN; options: {string.Join(", ", top.Variant.TransmissionOptions)}. Defaulting to {transmission.Name}.");

        return new HardwareProfile
        {
            Engine = engine,
            Turbo = Pick(ComponentKind.Turbo, "turbo"),
            Injectors = Pick(ComponentKind.Injectors, "injectors"),
            FuelSystem = Pick(ComponentKind.FuelSystem, "fuelSystem"),
            Transmission = transmission,
            Clutch = transmission.Id == "trans_dsg_dq250" ? _kb.Catalog.Get("clutch_dsg_integrated")! : Pick(ComponentKind.Clutch, "clutch"),
            Intercooler = Pick(ComponentKind.Intercooler, "intercooler"),
            Sensors = Pick(ComponentKind.Sensors, "sensors"),
            Emissions = Pick(ComponentKind.Emissions, "emissions"),
        };
    }

    /// <summary>Engine known only from the ECU string: displacement and cylinders, everything else UNKNOWN.</summary>
    private static ComponentSpec EvidenceOnlyEngine(List<VehicleEvidence> evidence)
    {
        var disp = evidence.FirstOrDefault(e => e.Fact == VehicleFact.Displacement);
        var cyl = evidence.FirstOrDefault(e => e.Fact == VehicleFact.Cylinders);
        if (disp is null && cyl is null) return ComponentSpec.Unknown(ComponentKind.Engine);
        var parameters = new Dictionary<string, Param>();
        if (disp is not null && double.TryParse(disp.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var l))
            parameters[P.DisplacementCc] = Param.Num(l * 1000, "cc", SourceType.EcuBinary, 0.6, "nominal litres from the ECU string, not the exact displacement");
        if (cyl is not null && double.TryParse(cyl.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var c))
            parameters[P.Cylinders] = Param.Num(c, "-", SourceType.EcuBinary, cyl.Confidence);
        return new ComponentSpec
        {
            Id = "engine_from_ecu", Kind = ComponentKind.Engine, Name = $"Engine {disp?.Value} L (from ECU identification, not catalogued)",
            Source = SourceType.EcuBinary, Confidence = 0.5, Parameters = parameters,
            Note = "No catalogued variant matches; power, torque and limits are UNKNOWN until the engine is verified.",
        };
    }
}
