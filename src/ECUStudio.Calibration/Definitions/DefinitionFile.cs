using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ECUStudio.Binary;
using ECUStudio.Calibration.Model;
using ECUStudio.Core;

namespace ECUStudio.Calibration.Definitions;

/// <summary>
/// Native ECUStudio definition format (*.ecudef.json). Used for the plugin Definition DB
/// (keyed by software number) and as the target format of XDF/A2L importers.
/// </summary>
public sealed record DefinitionFile
{
    public required string Plugin { get; init; }
    public string? Title { get; init; }
    public IReadOnlyList<string> SoftwareNumbers { get; init; } = [];
    /// <summary>True for synthetic definitions used in tests/demos. Never treated as real-vehicle data.</summary>
    public bool Synthetic { get; init; }
    public IReadOnlyList<MapEntry> Maps { get; init; } = [];

    public sealed record MapEntry
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public MapRole Role { get; init; }
        [JsonConverter(typeof(HexIntConverter))] public int Address { get; init; }
        public int Rows { get; init; } = 1;
        public int Cols { get; init; } = 1;
        public DataType DataType { get; init; } = DataType.UInt16;
        public Endianness Endian { get; init; } = Endianness.Big;
        public double Factor { get; init; } = 1;
        public double Offset { get; init; }
        public string Unit { get; init; } = "-";
        public ValueOrder Order { get; init; }
        public AxisEntry? XAxis { get; init; }
        public AxisEntry? YAxis { get; init; }
        public double Confidence { get; init; } = 0.9;
        public string? Description { get; init; }
    }

    public sealed record AxisEntry
    {
        public required string Name { get; init; }
        public string Unit { get; init; } = "-";
        public AxisQuantity Quantity { get; init; }
        [JsonConverter(typeof(NullableHexIntConverter))] public int? Address { get; init; }
        public int Length { get; init; }
        public DataType DataType { get; init; } = DataType.UInt16;
        public double Factor { get; init; } = 1;
        public double Offset { get; init; }
        public double[]? Values { get; init; }
    }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static DefinitionFile Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<DefinitionFile>(json, JsonOptions) ?? throw new DefinitionException("Definition file is empty");
        }
        catch (JsonException ex)
        {
            throw new DefinitionException($"Invalid definition JSON: {ex.Message}");
        }
    }

    public IReadOnlyList<MapDefinition> ToDefinitions(SourceType source) => Maps.Select(m => new MapDefinition
    {
        Id = m.Id,
        Name = m.Name,
        Role = m.Role,
        Address = m.Address,
        Rows = m.Rows,
        Cols = m.Cols,
        DataType = m.DataType,
        Endian = m.Endian,
        Factor = m.Factor,
        Offset = m.Offset,
        Unit = m.Unit,
        Order = m.Order,
        XAxis = ToAxis(m.XAxis),
        YAxis = ToAxis(m.YAxis),
        Source = source,
        Confidence = Synthetic ? Math.Min(m.Confidence, 0.9) : m.Confidence,
        Description = m.Description,
    }).ToList();

    private static AxisDefinition? ToAxis(AxisEntry? a) => a is null ? null : new AxisDefinition
    {
        Name = a.Name, Unit = a.Unit, Quantity = a.Quantity, Address = a.Address, Length = a.Length,
        DataType = a.DataType, Factor = a.Factor, Offset = a.Offset, FixedValues = a.Values,
    };
}

public sealed class HexIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number ? reader.GetInt32() : ParseHex(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteStringValue($"0x{value:X6}");

    internal static int ParseHex(string s) => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? int.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : int.Parse(s, CultureInfo.InvariantCulture);
}

public sealed class NullableHexIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.Number => reader.GetInt32(),
            _ => HexIntConverter.ParseHex(reader.GetString()!),
        };

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue(); else writer.WriteStringValue($"0x{value:X6}");
    }
}

/// <summary>Loads *.ecudef.json files from a directory: &lt;root&gt;/&lt;plugin&gt;/*.ecudef.json.</summary>
public sealed class DefinitionDatabase
{
    private readonly List<DefinitionFile> _files = [];

    public DefinitionDatabase(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFiles(root, "*.ecudef.json", SearchOption.AllDirectories))
            _files.Add(DefinitionFile.Parse(File.ReadAllText(path)));
    }

    public DefinitionDatabase(IEnumerable<DefinitionFile> files) => _files.AddRange(files);

    public static DefinitionDatabase Empty { get; } = new((string?)null);

    public IReadOnlyList<DefinitionFile> Files => _files;

    public DefinitionFile? Find(string plugin, string? softwareNumber) =>
        softwareNumber is null ? null : _files.FirstOrDefault(f =>
            f.Plugin.Equals(plugin, StringComparison.OrdinalIgnoreCase) &&
            f.SoftwareNumbers.Any(s => s.Equals(softwareNumber, StringComparison.OrdinalIgnoreCase)));
}
