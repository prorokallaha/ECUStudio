using System.Text.Json.Serialization;

namespace ECUStudio.Core;

/// <summary>Where a fact came from. The UI maps these to one consistent badge system.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SourceType>))]
public enum SourceType
{
    OemSpec,
    PublicSpec,
    Database,
    VariantTypical,
    VinDecode,
    EcuBinary,
    /// <summary>Read from the file name (e.g. a HW number written by the reading tool); weaker than the binary content.</summary>
    FileName,
    Damos,
    A2L,
    Xdf,
    DefinitionDb,
    SignatureScan,
    DiagnosticLog,
    Calculated,
    AIInferred,
    User,
    Assumption,
    Unknown,
}

/// <summary>A knowledge-base parameter: value + unit + source + confidence.</summary>
public sealed record Param
{
    public double? Number { get; init; }
    public string? Text { get; init; }
    public string Unit { get; init; } = "-";
    public SourceType Source { get; init; } = SourceType.Unknown;
    public double Confidence { get; init; }
    public bool UserVerified { get; init; }
    public string? Note { get; init; }

    [JsonIgnore]
    public bool IsKnown => (Number.HasValue || Text is not null) && Source != SourceType.Unknown;

    public static Param Num(double value, string unit, SourceType source, double confidence, string? note = null)
        => new() { Number = value, Unit = unit, Source = source, Confidence = confidence, Note = note };

    public static Param Str(string value, SourceType source, double confidence, string? note = null)
        => new() { Text = value, Source = source, Confidence = confidence, Note = note };

    public static Param Unknown(string unit = "-", string? note = null)
        => new() { Unit = unit, Source = SourceType.Unknown, Confidence = 0, Note = note };
}
