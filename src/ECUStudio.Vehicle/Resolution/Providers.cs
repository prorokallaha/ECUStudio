using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ECUStudio.Core;

namespace ECUStudio.Vehicle.Resolution;

/// <summary>VIN: manufacturer (WMI), VAG platform (VDS positions 7–8), model year (position 10).</summary>
public sealed class VinProvider : IVehicleEvidenceProvider
{
    public string Name => "VIN";

    public IEnumerable<VehicleEvidence> Collect(VehicleQuery query, VinInfo? vin)
    {
        if (vin is null) yield break;
        yield return new(VehicleFact.Wmi, vin.Wmi, 0.9, Name, $"WMI {vin.Wmi}{(vin.Manufacturer is null ? "" : $" = {vin.Manufacturer}")}");
        if (vin.PlatformCode is { } pc)
            yield return new(VehicleFact.Platform, pc, 0.85, Name, $"VDS positions 7–8 = {pc}{(vin.PlatformName is null ? "" : $" ({vin.PlatformName})")}");
        if (vin.ModelYear is { } y)
            yield return new(VehicleFact.ModelYear, y.ToString(CultureInfo.InvariantCulture), 0.6, Name, $"position 10 = model year {y}");
    }
}

/// <summary>
/// ECU identification read from the binary: plugin (family) and the engine string Bosch stores next to the SW number
/// ("R4 2,0L EDC …"), which gives displacement and cylinder count independently of the VIN.
/// </summary>
public sealed partial class EcuIdentificationProvider : IVehicleEvidenceProvider
{
    public string Name => "ECU identification";

    [GeneratedRegex(@"\b([1-6])[,.](\d)\s?L", RegexOptions.IgnoreCase)]
    private static partial Regex LitresRegex();

    [GeneratedRegex(@"\b[RV]\s?(\d{1,2})\b")]
    private static partial Regex CylindersRegex();

    public IEnumerable<VehicleEvidence> Collect(VehicleQuery query, VinInfo? vin)
    {
        if (query.Ecu is not { } ecu) yield break;
        if (ecu.PluginId is { } plugin)
            yield return new(VehicleFact.EcuPlugin, plugin, Math.Clamp(ecu.DetectionScore, 0.3, 0.95), Name, $"binary identified as {ecu.EcuFamily ?? plugin} (score {ecu.DetectionScore:0.00})");
        if (ParseEngineText(ecu.EngineText) is { } e)
        {
            yield return new(VehicleFact.Displacement, e.Litres, 0.9, Name, $"engine string in binary: \"{ecu.EngineText}\"");
            yield return new(VehicleFact.Cylinders, e.Cylinders.ToString(CultureInfo.InvariantCulture), 0.9, Name, $"engine string in binary: \"{ecu.EngineText}\"");
        }
    }

    public static (string Litres, int Cylinders)? ParseEngineText(string? text)
    {
        // Bosch writes both "R4 2,0L EDC …" and "2,0l R4 EDC …".
        if (string.IsNullOrWhiteSpace(text) || LitresRegex().Match(text) is not { Success: true } l || CylindersRegex().Match(text) is not { Success: true } c) return null;
        return ($"{l.Groups[1].Value}.{l.Groups[2].Value}", int.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture));
    }
}

/// <summary>OEM ECU part-number prefix → ECU family and engine codes it was fitted to (local table; an OEM catalogue API later).</summary>
public sealed class OemCatalogProvider : IVehicleEvidenceProvider
{
    public sealed record Entry(string Prefix, string EcuFamily, IReadOnlyList<string> EngineCodes, double Confidence);
    private sealed record File(IReadOnlyList<Entry> Entries);

    private readonly IReadOnlyList<Entry> _entries;
    public OemCatalogProvider(IReadOnlyList<Entry> entries) => _entries = entries;
    public string Name => "OEM ECU catalogue";

    public static OemCatalogProvider LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ECUStudio.Vehicle.Data.oem_ecu.json")
            ?? throw new InvalidOperationException("Embedded OEM ECU table missing");
        return new OemCatalogProvider(JsonSerializer.Deserialize<File>(stream, Json.Options)?.Entries ?? []);
    }

    public IEnumerable<VehicleEvidence> Collect(VehicleQuery query, VinInfo? vin)
    {
        if (query.Ecu?.OemPartNumber is not { Length: > 0 } oem) yield break;
        var entry = _entries.Where(e => oem.StartsWith(e.Prefix, StringComparison.OrdinalIgnoreCase)).MaxBy(e => e.Prefix.Length);
        if (entry is null) yield break;
        yield return new(VehicleFact.EngineCode, string.Join('|', entry.EngineCodes), entry.Confidence, Name,
            $"OEM part {oem} ({entry.EcuFamily}) is reported for engines {string.Join(", ", entry.EngineCodes)}");
    }
}

/// <summary>A software version whose vehicle is known (definition archive, fleet database, community data).</summary>
public sealed record SoftwareFingerprint(string SoftwareNumber, string? HardwareNumber, string EngineCode, string Source, double Confidence);

/// <summary>Exact SW (+HW) number match against known software versions.</summary>
public sealed class BinaryFingerprintProvider(Func<IEnumerable<SoftwareFingerprint>> fingerprints) : IVehicleEvidenceProvider
{
    public string Name => "Binary fingerprint";

    public IEnumerable<VehicleEvidence> Collect(VehicleQuery query, VinInfo? vin)
    {
        if (query.Ecu?.SoftwareNumber is not { Length: > 0 } sw) yield break;
        foreach (var f in fingerprints().Where(f => f.SoftwareNumber == sw))
        {
            var hwMatch = f.HardwareNumber is null || f.HardwareNumber == query.Ecu.HardwareNumber;
            if (!hwMatch) continue;
            yield return new(VehicleFact.EngineCode, f.EngineCode, f.Confidence, Name, $"SW {sw}{(f.HardwareNumber is null ? "" : $" + HW {f.HardwareNumber}")} is known as {f.EngineCode} ({f.Source})");
        }
    }
}

/// <summary>Vehicles the user confirmed earlier for the same ECU software (never transferred to another SW).</summary>
public sealed class LocalKnowledgeProvider(IVehicleKnowledgeStore store) : IVehicleEvidenceProvider
{
    public string Name => "Local knowledge";

    public IEnumerable<VehicleEvidence> Collect(VehicleQuery query, VinInfo? vin)
    {
        if (query.Ecu?.SoftwareNumber is not { Length: > 0 } sw) yield break;
        foreach (var c in store.FindBySoftware(sw).Where(c => c.HardwareNumber is null || c.HardwareNumber == query.Ecu.HardwareNumber))
            yield return new(VehicleFact.EngineCode, c.EngineCode, 0.95, Name, $"confirmed by user on {c.ConfirmedAt:yyyy-MM-dd} for SW {sw}");
    }
}

/// <summary>Explicit user choices: always the strongest evidence.</summary>
public sealed class UserOverrideProvider : IVehicleEvidenceProvider
{
    public string Name => "User";

    public IEnumerable<VehicleEvidence> Collect(VehicleQuery query, VinInfo? vin)
    {
        if (query.PreferredVariantId is { } id) yield return new(VehicleFact.Variant, id, 0.99, Name, "variant selected by user");
        if (query.TransmissionId is { } t) yield return new(VehicleFact.Transmission, t, 0.99, Name, "transmission selected by user");
    }
}

/// <summary>Variant hypotheses from the embedded knowledge base.</summary>
public sealed class LocalVehicleDatabaseProvider(VehicleKnowledgeBase kb) : IVehicleDatabaseProvider
{
    public string Name => "Local vehicle database";
    public IReadOnlyList<VehicleVariant> Variants => kb.Variants;
}
