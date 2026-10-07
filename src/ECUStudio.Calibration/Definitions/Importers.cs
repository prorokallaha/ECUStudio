using System.Globalization;
using System.Xml.Linq;
using ECUStudio.Binary;
using ECUStudio.Calibration.Model;
using ECUStudio.Calibration.Plugins;
using ECUStudio.Core;

namespace ECUStudio.Calibration.Definitions;

public interface IDefinitionImporter
{
    SourceType Source { get; }
    bool CanImport(string fileName);
    ExternalDefinition Import(string fileName, string content);
}

public static class DefinitionImporters
{
    public static readonly IReadOnlyList<IDefinitionImporter> All =
        [new NativeJsonImporter(), new XdfImporter(), new NotYetSupportedImporter(SourceType.A2L, ".a2l"), new NotYetSupportedImporter(SourceType.Damos, ".dam", ".damos"), new NotYetSupportedImporter(SourceType.Database, ".ols")];

    public static ExternalDefinition Import(string fileName, string content)
    {
        var importer = All.FirstOrDefault(i => i.CanImport(fileName))
            ?? throw new DefinitionException($"Unsupported definition format: {Path.GetExtension(fileName)}");
        return importer.Import(fileName, content);
    }
}

public sealed class NativeJsonImporter : IDefinitionImporter
{
    public SourceType Source => SourceType.DefinitionDb;
    public bool CanImport(string fileName) => fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    public ExternalDefinition Import(string fileName, string content)
    {
        var file = DefinitionFile.Parse(content);
        return new ExternalDefinition(SourceType.User, file.ToDefinitions(SourceType.User), fileName);
    }
}

/// <summary>Explicit placeholder: the format is recognised but not parsed yet. Fails loudly instead of guessing.</summary>
public sealed class NotYetSupportedImporter(SourceType source, params string[] extensions) : IDefinitionImporter
{
    public SourceType Source => source;
    public bool CanImport(string fileName) => extensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    public ExternalDefinition Import(string fileName, string content) =>
        throw new DefinitionException($"{source} import is planned but not implemented yet. Convert to XDF or *.ecudef.json for now.");
}

/// <summary>
/// TunerPro XDF importer (subset): XDFTABLE / XDFCONSTANT with EMBEDDEDDATA and linear MATH
/// equations of the form "X*a+b" / "X*a" / "X/a". Non-linear equations are rejected per table.
/// </summary>
public sealed class XdfImporter : IDefinitionImporter
{
    public SourceType Source => SourceType.Xdf;
    public bool CanImport(string fileName) => fileName.EndsWith(".xdf", StringComparison.OrdinalIgnoreCase);

    public ExternalDefinition Import(string fileName, string content)
    {
        XDocument doc;
        try { doc = XDocument.Parse(content); }
        catch (System.Xml.XmlException ex) { throw new DefinitionException($"Invalid XDF XML: {ex.Message}"); }

        var root = doc.Root ?? throw new DefinitionException("XDF has no root element");
        var header = root.Element("XDFHEADER");
        var baseOffset = ParseInt(header?.Element("BASEOFFSET")?.Attribute("offset")?.Value) ?? 0;
        var bigEndian = header?.Element("DEFAULTS")?.Attribute("lsbfirst")?.Value != "1";
        var maps = new List<MapDefinition>();
        var index = 0;

        foreach (var table in root.Elements("XDFTABLE"))
        {
            index++;
            var title = table.Element("title")?.Value?.Trim() ?? $"Table {index}";
            var axes = table.Elements("XDFAXIS").ToDictionary(a => a.Attribute("id")?.Value ?? "", a => a);
            if (!axes.TryGetValue("z", out var z)) continue;
            var data = z.Element("EMBEDDEDDATA");
            var address = ParseInt(data?.Attribute("mmedaddress")?.Value);
            if (address is null) continue;
            var rows = ParseInt(data?.Attribute("mmedrowcount")?.Value) ?? 1;
            var cols = ParseInt(data?.Attribute("mmedcolcount")?.Value) ?? 1;
            var bits = ParseInt(data?.Attribute("mmedelementsizebits")?.Value) ?? 16;
            var flags = ParseInt(data?.Attribute("mmedtypeflags")?.Value) ?? 0;
            var (factor, offset) = ParseLinear(z.Element("MATH")?.Attribute("equation")?.Value, title);

            maps.Add(new MapDefinition
            {
                Id = $"xdf_{index:D3}",
                Name = title,
                Role = GuessRole(title),
                Address = address.Value + baseOffset,
                Rows = rows,
                Cols = cols,
                DataType = ToDataType(bits, (flags & 0x01) != 0),
                Endian = bigEndian ? Endianness.Big : Endianness.Little,
                Factor = factor,
                Offset = offset,
                Unit = z.Element("units")?.Value ?? "-",
                XAxis = ParseAxis(axes.GetValueOrDefault("x"), cols, baseOffset, title),
                YAxis = ParseAxis(axes.GetValueOrDefault("y"), rows, baseOffset, title),
                Source = SourceType.Xdf,
                Confidence = 0.85,
                Description = table.Element("description")?.Value,
            });
        }
        if (maps.Count == 0) throw new DefinitionException("XDF contains no importable tables");
        return new ExternalDefinition(SourceType.Xdf, maps, fileName);
    }

    private static AxisDefinition? ParseAxis(XElement? axis, int expected, int baseOffset, string title)
    {
        if (axis is null) return null;
        var data = axis.Element("EMBEDDEDDATA");
        var address = ParseInt(data?.Attribute("mmedaddress")?.Value);
        var (factor, offset) = ParseLinear(axis.Element("MATH")?.Attribute("equation")?.Value, title);
        if (address is null)
        {
            var labels = axis.Elements("LABEL").Select(l => double.TryParse(l.Attribute("value")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
            if (labels.Length != expected || labels.Any(double.IsNaN)) return null;
            return new AxisDefinition { Name = axis.Element("units")?.Value ?? "axis", Unit = axis.Element("units")?.Value ?? "-", FixedValues = labels, Length = expected };
        }
        var bits = ParseInt(data?.Attribute("mmedelementsizebits")?.Value) ?? 16;
        return new AxisDefinition
        {
            Name = axis.Element("units")?.Value ?? "axis",
            Unit = axis.Element("units")?.Value ?? "-",
            Address = address + baseOffset,
            Length = expected,
            DataType = ToDataType(bits, false),
            Factor = factor,
            Offset = offset,
        };
    }

    internal static (double Factor, double Offset) ParseLinear(string? equation, string title)
    {
        if (string.IsNullOrWhiteSpace(equation)) return (1, 0);
        var e = equation.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (e == "X") return (1, 0);
        double factor = 1, offset = 0;
        var rest = e;
        var plus = rest.IndexOfAny(['+', '-'], 1);
        if (plus > 0)
        {
            offset = Number(rest[plus..]);
            rest = rest[..plus];
        }
        if (rest.StartsWith("X*", StringComparison.Ordinal)) factor = Number(rest[2..]);
        else if (rest.StartsWith("X/", StringComparison.Ordinal)) factor = 1 / Number(rest[2..]);
        else if (rest.EndsWith("*X", StringComparison.Ordinal)) factor = Number(rest[..^2]);
        else if (rest != "X") throw NonLinear();
        if (!double.IsFinite(factor) || factor == 0) throw NonLinear();
        return (factor, offset);

        double Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : throw NonLinear();
        DefinitionException NonLinear() => new($"XDF table '{title}': non-linear equation '{equation}' is not supported");
    }

    private static DataType ToDataType(int bits, bool signed) => bits switch
    {
        8 => signed ? DataType.Int8 : DataType.UInt8,
        16 => signed ? DataType.Int16 : DataType.UInt16,
        32 => signed ? DataType.Int32 : DataType.UInt32,
        _ => throw new DefinitionException($"Unsupported element size {bits} bits"),
    };

    private static int? ParseInt(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : int.Parse(s, CultureInfo.InvariantCulture);
    }

    /// <summary>Name-based role guess for imported tables; confidence stays with the definition source.</summary>
    internal static MapRole GuessRole(string title)
    {
        var t = title.ToLowerInvariant();
        if (t.Contains("driver") && t.Contains("wish")) return MapRole.DriverWish;
        if (t.Contains("smoke")) return MapRole.SmokeLimiter;
        if (t.Contains("svbl") || t.Contains("single value")) return MapRole.Svbl;
        if (t.Contains("boost") && t.Contains("limit")) return MapRole.BoostLimiter;
        if (t.Contains("boost")) return MapRole.BoostTarget;
        if (t.Contains("torque") && (t.Contains("iq") || t.Contains("conversion") || t.Contains("quantity"))) return MapRole.TorqueToIq;
        if (t.Contains("gear") && t.Contains("torque")) return MapRole.GearTorqueLimiter;
        if (t.Contains("torque") && t.Contains("limit")) return MapRole.TorqueLimiter;
        if (t.Contains("start of injection") || t.Contains("soi")) return MapRole.Soi;
        if (t.Contains("duration")) return MapRole.Duration;
        if (t.Contains("n75") || t.Contains("vnt")) return MapRole.VntDuty;
        if (t.Contains("rail")) return MapRole.RailPressure;
        if (t.Contains("egt")) return MapRole.EgtProtection;
        return MapRole.Unknown;
    }
}
