using System.Text.Json.Serialization;
using ECUStudio.Components;

namespace ECUStudio.Vehicle.Resolution;

/// <summary>Facts a provider can contribute about the vehicle. Each variant is checked against them.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VehicleFact>))]
public enum VehicleFact
{
    Manufacturer,
    Wmi,
    Platform,
    ModelYear,
    /// <summary>ECU plugin id (e.g. edc16u34) the binary was identified as.</summary>
    EcuPlugin,
    /// <summary>Displacement in litres, one decimal ("2.0").</summary>
    Displacement,
    Cylinders,
    /// <summary>One engine code, or several separated by '|' (any of them is a match).</summary>
    EngineCode,
    EngineFamily,
    /// <summary>User-selected variant id.</summary>
    Variant,
    Transmission,
}

/// <summary>One piece of evidence with where it came from and how much it is trusted.</summary>
public sealed record VehicleEvidence(VehicleFact Fact, string Value, double Confidence, string Provider, string Detail);

/// <summary>ECU facts read from the binary, independent of the ECU plugin assembly.</summary>
public sealed record EcuFacts
{
    public string? PluginId { get; init; }
    public string? EcuFamily { get; init; }
    public double DetectionScore { get; init; }
    public string? BoschNumber { get; init; }
    public string? HardwareNumber { get; init; }
    public string? SoftwareNumber { get; init; }
    public string? OemPartNumber { get; init; }
    public string? CalibrationId { get; init; }
    /// <summary>Raw engine identification text from the binary, e.g. "R4 2,0L EDC G000AG".</summary>
    public string? EngineText { get; init; }
    public IReadOnlyList<string> EngineFamilies { get; init; } = [];
    public string? FileSha256 { get; init; }
}

public sealed record VehicleQuery
{
    public string? Vin { get; init; }
    public EcuFacts? Ecu { get; init; }
    public IReadOnlyList<HardwareOverride> Overrides { get; init; } = [];
    public string? PreferredVariantId { get; init; }
    public string? TransmissionId { get; init; }
}

/// <summary>
/// A source of vehicle evidence (VIN, ECU, OEM catalogue, binary fingerprints, user knowledge, external APIs).
/// The resolver only combines evidence, so a new source plugs in without touching it.
/// </summary>
public interface IVehicleEvidenceProvider
{
    string Name { get; }
    IEnumerable<VehicleEvidence> Collect(VehicleQuery query, VinInfo? vin);
}

/// <summary>Source of variant hypotheses (local JSON today; an OEM/vehicle database API later).</summary>
public interface IVehicleDatabaseProvider
{
    string Name { get; }
    IReadOnlyList<VehicleVariant> Variants { get; }
}

/// <summary>Vehicle facts the user confirmed for one ECU software version (knowledge accumulation).</summary>
public sealed record ConfirmedVehicle(string SoftwareNumber, string? HardwareNumber, string VariantId, string EngineCode, DateTimeOffset ConfirmedAt);

public interface IVehicleKnowledgeStore
{
    IReadOnlyList<ConfirmedVehicle> FindBySoftware(string softwareNumber);
    void Confirm(ConfirmedVehicle confirmation);
}

public sealed class InMemoryVehicleKnowledgeStore : IVehicleKnowledgeStore
{
    private readonly List<ConfirmedVehicle> _items = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<ConfirmedVehicle> FindBySoftware(string softwareNumber)
    {
        lock (_lock) return _items.Where(i => i.SoftwareNumber == softwareNumber).ToList();
    }

    public void Confirm(ConfirmedVehicle confirmation)
    {
        lock (_lock)
        {
            _items.RemoveAll(i => i.SoftwareNumber == confirmation.SoftwareNumber && i.HardwareNumber == confirmation.HardwareNumber);
            _items.Add(confirmation);
        }
    }
}
