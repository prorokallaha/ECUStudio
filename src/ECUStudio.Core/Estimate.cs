using System.Text.Json.Serialization;

namespace ECUStudio.Core;

/// <summary>What an engineering number is: measured, estimated, requested by the ECU, etc.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ValueKind>))]
public enum ValueKind
{
    Estimated,
    EcuRequested,
    Measured,
    Spec,
    Assumed,
    Unknown,
    NotApplicable,
}

/// <summary>
/// An engineering value with range and confidence. A calculated number is never
/// presented as a measurement: callers display it via <see cref="Display"/> which rounds
/// to a resolution the model actually has (e.g. 145 hp, 137–152, 0.78).
/// </summary>
public sealed record Estimate
{
    public double? Value { get; init; }
    public double? Low { get; init; }
    public double? High { get; init; }
    public string Unit { get; init; } = "-";
    public double Confidence { get; init; }
    public ValueKind Kind { get; init; } = ValueKind.Estimated;
    public string? Note { get; init; }

    [JsonIgnore]
    public bool IsKnown => Value.HasValue && Kind is not (ValueKind.Unknown or ValueKind.NotApplicable);

    public static Estimate Of(double value, double low, double high, string unit, double confidence,
        ValueKind kind = ValueKind.Estimated, string? note = null)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "Estimate value must be finite");
        if (low > high) (low, high) = (high, low);
        return new Estimate
        {
            Value = value,
            Low = Math.Min(low, value),
            High = Math.Max(high, value),
            Unit = unit,
            Confidence = Math.Clamp(confidence, 0, 1),
            Kind = kind,
            Note = note,
        };
    }

    public static Estimate Around(double value, double relativeUncertainty, string unit, double confidence,
        ValueKind kind = ValueKind.Estimated, string? note = null)
    {
        var delta = Math.Abs(value * relativeUncertainty);
        return Of(value, value - delta, value + delta, unit, confidence, kind, note);
    }

    public static Estimate Exact(double value, string unit, double confidence, ValueKind kind, string? note = null)
        => Of(value, value, value, unit, confidence, kind, note);

    public static Estimate Unknown(string unit = "-", string? note = null)
        => new() { Unit = unit, Kind = ValueKind.Unknown, Confidence = 0, Note = note };

    public static Estimate NotApplicable(string unit = "-", string? note = null)
        => new() { Unit = unit, Kind = ValueKind.NotApplicable, Confidence = 1, Note = note };

    public Estimate Scale(double factor, string? unit = null)
    {
        if (!Value.HasValue) return this with { Unit = unit ?? Unit };
        var a = Low!.Value * factor;
        var b = High!.Value * factor;
        return this with { Value = Value * factor, Low = Math.Min(a, b), High = Math.Max(a, b), Unit = unit ?? Unit };
    }

    public EstimateDisplay Display()
    {
        if (!IsKnown) return new EstimateDisplay(Kind.ToString().ToUpperInvariant(), null, null, Unit, Confidence, Kind, Note);
        return new EstimateDisplay(
            EngineeringRounding.Round(Value!.Value, Unit).ToString(System.Globalization.CultureInfo.InvariantCulture),
            EngineeringRounding.Round(Low!.Value, Unit),
            EngineeringRounding.Round(High!.Value, Unit),
            Unit, Math.Round(Confidence, 2), Kind, Note);
    }

    public override string ToString()
    {
        var d = Display();
        return d.Low is null ? $"{d.Value} {Unit}" : $"{d.Value} {Unit} ({d.Low}–{d.High}), confidence {d.Confidence:0.00}";
    }
}

public sealed record EstimateDisplay(string Value, double? Low, double? High, string Unit, double Confidence, ValueKind Kind, string? Note);

public static class EngineeringRounding
{
    private static readonly Dictionary<string, double> Steps = new(StringComparer.Ordinal)
    {
        ["hp"] = 1, ["kW"] = 1, ["Nm"] = 1, ["mbar"] = 10, ["mg/stroke"] = 0.5, ["mg"] = 0.5,
        ["°C"] = 5, ["K"] = 5, ["%"] = 1, ["rpm"] = 10, ["kg/s"] = 0.001, ["-"] = 0.01,
        ["°CA"] = 0.1, ["°BTDC"] = 0.1, ["bar"] = 1, ["g/s"] = 1,
    };

    public static double Round(double value, string unit)
    {
        if (!Steps.TryGetValue(unit, out var step)) return Math.Round(value, 3);
        return Math.Round(Math.Round(value / step) * step, 6);
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<ConfidenceLevel>))]
public enum ConfidenceLevel
{
    Unknown,
    Low,
    LowMedium,
    Medium,
    MediumHigh,
    High,
}

public static class ConfidenceLevels
{
    public static ConfidenceLevel FromScore(double score) => score switch
    {
        <= 0 => ConfidenceLevel.Unknown,
        < 0.35 => ConfidenceLevel.Low,
        < 0.5 => ConfidenceLevel.LowMedium,
        < 0.65 => ConfidenceLevel.Medium,
        < 0.8 => ConfidenceLevel.MediumHigh,
        _ => ConfidenceLevel.High,
    };
}
