using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ECUStudio.Core;

namespace ECUStudio.Components;

[JsonConverter(typeof(JsonStringEnumConverter<ComponentKind>))]
public enum ComponentKind { Engine, Turbo, Injectors, FuelSystem, Transmission, Clutch, Intercooler, Sensors, Emissions }

/// <summary>Well-known parameter keys. A missing or UNKNOWN parameter is never replaced by a guess.</summary>
public static class P
{
    // engine
    public const string DisplacementCc = "displacement_cc";
    public const string Cylinders = "cylinders";
    public const string BoreMm = "bore_mm";
    public const string StrokeMm = "stroke_mm";
    public const string CompressionRatio = "compression_ratio";
    public const string RatedPowerKw = "rated_power_kw";
    public const string RatedPowerRpm = "rated_power_rpm";
    public const string RatedTorqueNm = "rated_torque_nm";
    public const string RatedTorqueRpm = "rated_torque_rpm";
    public const string InjectionSystem = "injection_system";
    public const string MaxDesignTorqueNm = "max_design_torque_nm";
    public const string MaxRpm = "max_rpm";
    // turbo
    public const string Model = "model";
    public const string MaxPressureRatio = "max_pressure_ratio";
    public const string MaxCorrectedFlowKgS = "max_corrected_flow_kg_s";
    public const string MaxTurbineInletTempC = "max_turbine_inlet_temp_c";
    public const string MaxShaftSpeedRpm = "max_shaft_speed_rpm";
    // injectors / fuel
    public const string MaxDeliveryMg = "max_delivery_mg";
    public const string MaxInjectionDurationDegCa = "max_injection_duration_deg_ca";
    public const string MaxInjectionPressureBar = "max_injection_pressure_bar";
    public const string MaxRailPressureBar = "max_rail_pressure_bar";
    public const string PumpMaxDeliveryMg = "pump_max_delivery_mg";
    // drivetrain
    public const string RatedTorqueInputNm = "rated_input_torque_nm";
    public const string Gears = "gears";
    // thermal
    public const string MaxEgtC = "max_egt_c";
}

public sealed record ComponentSpec
{
    public required string Id { get; init; }
    public required ComponentKind Kind { get; init; }
    public required string Name { get; init; }
    public SourceType Source { get; init; } = SourceType.Database;
    public double Confidence { get; init; }
    public bool UserVerified { get; init; }
    public bool IsOverride { get; init; }
    public Dictionary<string, Param> Parameters { get; init; } = new();
    public string? Note { get; init; }

    public Param Get(string key) => Parameters.TryGetValue(key, out var p) ? p : Param.Unknown();

    public double? Number(string key) => Get(key) is { IsKnown: true, Number: { } v } ? v : null;

    public static ComponentSpec Unknown(ComponentKind kind) => new()
    {
        Id = $"unknown_{kind.ToString().ToLowerInvariant()}", Kind = kind, Name = "Unknown", Source = SourceType.Unknown, Confidence = 0,
    };
}

/// <summary>The installed hardware the simulation and risk engines run against.</summary>
public sealed record HardwareProfile
{
    public required ComponentSpec Engine { get; init; }
    public required ComponentSpec Turbo { get; init; }
    public required ComponentSpec Injectors { get; init; }
    public required ComponentSpec FuelSystem { get; init; }
    public required ComponentSpec Transmission { get; init; }
    public required ComponentSpec Clutch { get; init; }
    public required ComponentSpec Intercooler { get; init; }
    public required ComponentSpec Sensors { get; init; }
    public required ComponentSpec Emissions { get; init; }

    public IEnumerable<ComponentSpec> All => [Engine, Turbo, Injectors, FuelSystem, Transmission, Clutch, Intercooler, Sensors, Emissions];

    public ComponentSpec Get(ComponentKind kind) => kind switch
    {
        ComponentKind.Engine => Engine,
        ComponentKind.Turbo => Turbo,
        ComponentKind.Injectors => Injectors,
        ComponentKind.FuelSystem => FuelSystem,
        ComponentKind.Transmission => Transmission,
        ComponentKind.Clutch => Clutch,
        ComponentKind.Intercooler => Intercooler,
        ComponentKind.Sensors => Sensors,
        _ => Emissions,
    };

    public HardwareProfile With(ComponentSpec spec) => spec.Kind switch
    {
        ComponentKind.Engine => this with { Engine = spec },
        ComponentKind.Turbo => this with { Turbo = spec },
        ComponentKind.Injectors => this with { Injectors = spec },
        ComponentKind.FuelSystem => this with { FuelSystem = spec },
        ComponentKind.Transmission => this with { Transmission = spec },
        ComponentKind.Clutch => this with { Clutch = spec },
        ComponentKind.Intercooler => this with { Intercooler = spec },
        ComponentKind.Sensors => this with { Sensors = spec },
        _ => this with { Emissions = spec },
    };
}

/// <summary>User replacement of a component, e.g. "Stock turbo → GTB1756VK".</summary>
public sealed record HardwareOverride
{
    public required ComponentKind Kind { get; init; }
    /// <summary>Catalog id to install, or null for a custom component described by <see cref="Custom"/>.</summary>
    public string? CatalogId { get; init; }
    public ComponentSpec? Custom { get; init; }
    public string? Note { get; init; }
}

public sealed class ComponentCatalog
{
    private readonly Dictionary<string, ComponentSpec> _items;

    public ComponentCatalog(IEnumerable<ComponentSpec> items) => _items = items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ComponentSpec> Items => _items.Values;

    public ComponentSpec? Get(string id) => _items.GetValueOrDefault(id);

    public IEnumerable<ComponentSpec> OfKind(ComponentKind kind) => _items.Values.Where(i => i.Kind == kind);

    public static ComponentCatalog LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ECUStudio.Components.Data.components.json")
            ?? throw new InvalidOperationException("Embedded component catalog missing");
        var items = JsonSerializer.Deserialize<List<ComponentSpec>>(stream, Json.Options) ?? [];
        return new ComponentCatalog(items);
    }

    /// <summary>Applies overrides. Overridden components are marked as user-sourced but keep catalog limits/confidence.</summary>
    public HardwareProfile Apply(HardwareProfile profile, IEnumerable<HardwareOverride> overrides)
    {
        foreach (var o in overrides)
        {
            var spec = o.Custom ?? (o.CatalogId is { } id ? Get(id) : null)
                ?? throw new NotFoundException($"Component '{o.CatalogId}' not found in catalog");
            if (spec.Kind != o.Kind) throw new EcuStudioException("COMPONENT_KIND_MISMATCH", $"Component '{spec.Id}' is a {spec.Kind}, not a {o.Kind}");
            profile = profile.With(spec with { IsOverride = true, UserVerified = true, Note = o.Note ?? spec.Note });
        }
        return profile;
    }
}
