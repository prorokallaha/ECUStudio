using System.Text.Json.Serialization;
using ECUStudio.Binary;
using ECUStudio.Core;

namespace ECUStudio.Calibration.Model;

[JsonConverter(typeof(JsonStringEnumConverter<MapRole>))]
public enum MapRole
{
    Unknown,
    DriverWish,
    TorqueLimiter,
    GearTorqueLimiter,
    TorqueToIq,
    SmokeLimiter,
    BoostTarget,
    BoostLimiter,
    Svbl,
    VntDuty,
    Soi,
    Duration,
    RailPressure,
    LambdaTarget,
    EgtProtection,
    TemperatureProtection,
    RpmLimiter,
    GearboxTorqueMonitor,
}

[JsonConverter(typeof(JsonStringEnumConverter<MapCategory>))]
public enum MapCategory { Torque, Fuel, Air, Protection, Unknown }

/// <summary>Physical meaning of an axis, used both for decoding and signature classification.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AxisQuantity>))]
public enum AxisQuantity { Unknown, EngineSpeed, PedalPosition, InjectionQuantity, AtmosphericPressure, AirMass, Torque, Gear, Temperature, BoostPressure, Index }

[JsonConverter(typeof(JsonStringEnumConverter<ValueOrder>))]
public enum ValueOrder { RowMajor, ColumnMajor }

public static class MapRoles
{
    public static MapCategory Category(this MapRole role) => role switch
    {
        MapRole.DriverWish or MapRole.TorqueLimiter or MapRole.GearTorqueLimiter or MapRole.TorqueToIq or MapRole.GearboxTorqueMonitor => MapCategory.Torque,
        MapRole.SmokeLimiter or MapRole.Soi or MapRole.Duration or MapRole.RailPressure or MapRole.LambdaTarget => MapCategory.Fuel,
        MapRole.BoostTarget or MapRole.BoostLimiter or MapRole.Svbl or MapRole.VntDuty => MapCategory.Air,
        MapRole.EgtProtection or MapRole.TemperatureProtection or MapRole.RpmLimiter => MapCategory.Protection,
        _ => MapCategory.Unknown,
    };

    public static string DisplayName(this MapRole role) => role switch
    {
        MapRole.DriverWish => "Driver Wish",
        MapRole.TorqueLimiter => "Torque Limiter",
        MapRole.GearTorqueLimiter => "Gear Torque Limiter",
        MapRole.TorqueToIq => "Torque → IQ Conversion",
        MapRole.SmokeLimiter => "Smoke Limiter",
        MapRole.BoostTarget => "Boost Target",
        MapRole.BoostLimiter => "Boost Limiter",
        MapRole.Svbl => "SVBL (single value boost limit)",
        MapRole.VntDuty => "VNT / N75 Duty",
        MapRole.Soi => "Start of Injection",
        MapRole.Duration => "Injection Duration",
        MapRole.RailPressure => "Rail Pressure",
        MapRole.LambdaTarget => "Lambda Target",
        MapRole.EgtProtection => "EGT Protection",
        MapRole.TemperatureProtection => "Temperature Protection",
        MapRole.RpmLimiter => "RPM Limiter",
        MapRole.GearboxTorqueMonitor => "Gearbox Torque Monitoring",
        _ => "Unknown",
    };

    /// <summary>Short physical explanation shown as "Why does this map matter?".</summary>
    public static string WhyItMatters(this MapRole role) => role switch
    {
        MapRole.DriverWish => "Translates pedal position and RPM into requested torque. Raising it changes response, not the maximum, unless limiters are raised too.",
        MapRole.TorqueLimiter => "Caps the torque request depending on RPM and atmospheric pressure. Usually the first cap on peak torque.",
        MapRole.GearTorqueLimiter => "Caps torque per gear to protect gearbox and driveline.",
        MapRole.TorqueToIq => "Converts requested torque to injected fuel. Its slope encodes the OEM's efficiency model of the engine.",
        MapRole.SmokeLimiter => "Caps fuel by measured air mass. Defines minimum lambda: raising it without more air means richer mixture, smoke and higher EGT.",
        MapRole.BoostTarget => "Requested manifold pressure by RPM and fuel. Determines air mass and turbo work.",
        MapRole.BoostLimiter => "Caps boost by RPM and atmospheric pressure. Protects the turbo at altitude (higher pressure ratio).",
        MapRole.Svbl => "Single value boost limit: hard overboost protection threshold.",
        MapRole.VntDuty => "Base duty for the VNT actuator. Affects spool and turbine speed.",
        MapRole.Soi => "Start of injection angle. Advance raises efficiency and peak cylinder pressure; retard raises EGT.",
        MapRole.Duration => "Injection duration needed for a given fuel quantity. Longer duration extends combustion late into the expansion stroke.",
        MapRole.RailPressure => "Common-rail target pressure. Not applicable to unit-injector (PD) engines.",
        MapRole.LambdaTarget => "Target air/fuel ratio.",
        MapRole.EgtProtection => "Reduces fuel when modelled/measured EGT is too high.",
        MapRole.TemperatureProtection => "Reduces torque at high coolant/fuel/intake temperatures.",
        MapRole.RpmLimiter => "Engine speed limitation.",
        MapRole.GearboxTorqueMonitor => "Torque reported to / limited for the transmission controller.",
        _ => "Purpose not established. Treated as a candidate until confirmed.",
    };
}

public sealed record AxisDefinition
{
    public required string Name { get; init; }
    public string Unit { get; init; } = "-";
    public AxisQuantity Quantity { get; init; } = AxisQuantity.Unknown;
    public int Length { get; init; }
    /// <summary>Absolute file offset of the axis values; null when <see cref="FixedValues"/> are used.</summary>
    public int? Address { get; init; }
    public DataType DataType { get; init; } = DataType.UInt16;
    public double Factor { get; init; } = 1;
    public double Offset { get; init; }
    public double[]? FixedValues { get; init; }
    /// <summary>Physical limits declared by the definition (A2L AXIS_DESCR lower/upper), used to verify a definition against a binary.</summary>
    public double? LowerLimit { get; init; }
    public double? UpperLimit { get; init; }
}

/// <summary>One element of a record whose layout is resolved against the binary (A2L RECORD_LAYOUT).</summary>
public sealed record RecordItem(string Kind, DataType Type);

/// <summary>
/// Record that stores its own axis point counts (Bosch "NO_AXIS_PTS_X/Y" layouts, e.g. <c>nx ny x[] y[] z[]</c>).
/// The concrete size and offsets come from the counts in the binary, bounded by the maxima of the definition.
/// </summary>
public sealed record InlineRecord(int Address, IReadOnlyList<RecordItem> Items, int MaxCols, int MaxRows);

public sealed record MapDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public MapRole Role { get; init; } = MapRole.Unknown;
    public int Address { get; init; }
    public int Rows { get; init; } = 1;
    public int Cols { get; init; } = 1;
    public DataType DataType { get; init; } = DataType.UInt16;
    public Endianness Endian { get; init; } = Endianness.Big;
    public double Factor { get; init; } = 1;
    public double Offset { get; init; }
    public string Unit { get; init; } = "-";
    public ValueOrder Order { get; init; } = ValueOrder.RowMajor;
    public AxisDefinition? XAxis { get; init; }
    public AxisDefinition? YAxis { get; init; }
    public SourceType Source { get; init; } = SourceType.Unknown;
    public double Confidence { get; init; }
    public string? Description { get; init; }
    /// <summary>Physical value limits declared by the definition (A2L CHARACTERISTIC lower/upper).</summary>
    public double? LowerLimit { get; init; }
    public double? UpperLimit { get; init; }
    /// <summary>Set when sizes and offsets are read from counts stored in the binary.</summary>
    public InlineRecord? Record { get; init; }
    /// <summary>Address in the source definition when the map was relocated to fit another software version.</summary>
    public int? SourceAddress { get; init; }

    public int ByteLength => Rows * Cols * DataType.Size();
    public MapCategory Category => Role.Category();
}

/// <summary>A decoded map. Values are row-major: Values[row * Cols + col]; X = columns, Y = rows.</summary>
public sealed record CalibrationMap
{
    public required MapDefinition Definition { get; init; }
    public required double[] XAxis { get; init; }
    public required double[] YAxis { get; init; }
    public required double[] Values { get; init; }
    public required double[] Raw { get; init; }

    public string Id => Definition.Id;
    public int Rows => Definition.Rows;
    public int Cols => Definition.Cols;
    public double this[int row, int col] => Values[row * Cols + col];

    public double Min => Values.Length == 0 ? double.NaN : Values.Min();
    public double Max => Values.Length == 0 ? double.NaN : Values.Max();

    public double Lookup(double x, double y = 0) => Interpolation.Bilinear(this, x, y);
}

public sealed class CalibrationSet
{
    private readonly Dictionary<string, CalibrationMap> _maps;

    public CalibrationSet(string binarySha256, string pluginId, IEnumerable<CalibrationMap> maps)
    {
        BinarySha256 = binarySha256;
        PluginId = pluginId;
        _maps = maps.ToDictionary(m => m.Id, StringComparer.Ordinal);
    }

    public string BinarySha256 { get; }
    public string PluginId { get; }
    public IReadOnlyCollection<CalibrationMap> Maps => _maps.Values;

    public CalibrationMap? Get(string id) => _maps.GetValueOrDefault(id);

    /// <summary>The highest-confidence map with the role, if any.</summary>
    public CalibrationMap? ByRole(MapRole role)
    {
        CalibrationMap? best = null;
        foreach (var m in _maps.Values)
        {
            if (m.Definition.Role != role) continue;
            // Equal confidence: the larger table is the base map, smaller ones are usually corrections.
            if (best is null || m.Definition.Confidence > best.Definition.Confidence
                || m.Definition.Confidence == best.Definition.Confidence && m.Definition.Rows * m.Definition.Cols > best.Definition.Rows * best.Definition.Cols) best = m;
        }
        return best;
    }

    public IEnumerable<CalibrationMap> AllByRole(MapRole role) => _maps.Values.Where(m => m.Definition.Role == role);
}
