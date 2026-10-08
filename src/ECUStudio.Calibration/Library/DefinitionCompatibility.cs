using System.Text.Json.Serialization;
using ECUStudio.Binary;
using ECUStudio.Calibration.Model;
using ECUStudio.Calibration.Plugins;
using ECUStudio.Core;

namespace ECUStudio.Calibration.Library;

[JsonConverter(typeof(JsonStringEnumConverter<CompatibilityStatus>))]
public enum CompatibilityStatus { Compatible, Warning, Incompatible }

/// <summary>Result of checking a definition against a concrete binary before it is used.</summary>
public sealed record CompatibilityReport
{
    public required CompatibilityStatus Status { get; init; }
    public required int MapCount { get; init; }
    public int MapsDecoded { get; init; }
    public int MapsOutOfRange { get; init; }
    public int AxesNotMonotonic { get; init; }
    /// <summary>True / false when the definition names a SW number; null when it names none.</summary>
    public bool? SoftwareMatches { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
}

/// <summary>
/// Checks that a definition fits a binary: software number, every address inside the file, and decoded axes rising
/// monotonically (a definition for another software version typically lands on data and produces noisy axes).
/// </summary>
public static class DefinitionCompatibility
{
    public static CompatibilityReport Check(ExternalDefinition definition, BinaryImage image, EcuIdentification ident, LibraryIdentifiers? definitionIds = null)
    {
        var reasons = new List<string>();
        var ids = definitionIds ?? LibraryScanner.Identify(definition.Name);
        bool? swMatches = null;
        if (ident.SoftwareNumber.IsKnown && ids.SoftwareNumbers.Count > 0)
        {
            swMatches = ids.SoftwareNumbers.Contains(ident.SoftwareNumber.Text);
            reasons.Add(swMatches.Value
                ? $"SW {ident.SoftwareNumber.Text} matches the definition"
                : $"definition is for SW {string.Join(", ", ids.SoftwareNumbers)}, binary is SW {ident.SoftwareNumber.Text}");
        }
        else reasons.Add("definition does not name a SW number: compatibility rests on the structural checks");

        int decoded = 0, outOfRange = 0, notMonotonic = 0, failed = 0;
        foreach (var def in definition.Maps)
        {
            if (!InRange(def, image.Length)) { outOfRange++; continue; }
            try
            {
                var map = MapDecoder.Decode(image, def);
                decoded++;
                if ((def.XAxis is { Length: > 1, Address: not null } && !Rising(map.XAxis)) || (def.YAxis is { Length: > 1, Address: not null } && !Rising(map.YAxis)))
                    notMonotonic++;
            }
            catch (DefinitionException) { failed++; }
        }

        var total = definition.Maps.Count;
        if (total == 0) reasons.Add("definition contains no maps");
        if (outOfRange > 0) reasons.Add($"{outOfRange} of {total} map(s) point outside the {image.Length}-byte file");
        if (failed > 0) reasons.Add($"{failed} map(s) could not be decoded");
        if (notMonotonic > 0) reasons.Add($"{notMonotonic} of {decoded} decoded map(s) have axes that do not rise monotonically");

        var bad = total == 0 ? 1.0 : (double)(outOfRange + failed) / total;
        var noisy = decoded == 0 ? 0 : (double)notMonotonic / decoded;
        var status = total == 0 || bad > 0.5 || noisy > 0.5 || (swMatches == false && noisy > 0.2)
            ? CompatibilityStatus.Incompatible
            : swMatches == false || bad > 0 || noisy > 0.1 ? CompatibilityStatus.Warning : CompatibilityStatus.Compatible;
        return new CompatibilityReport
        {
            Status = status, MapCount = total, MapsDecoded = decoded, MapsOutOfRange = outOfRange, AxesNotMonotonic = notMonotonic,
            SoftwareMatches = swMatches, Reasons = reasons,
        };
    }

    private static bool InRange(MapDefinition d, int length)
    {
        if (d.Address < 0 || (long)d.Address + d.ByteLength > length) return false;
        foreach (var a in new[] { d.XAxis, d.YAxis })
            if (a?.Address is { } addr && (addr < 0 || (long)addr + (long)a.Length * a.DataType.Size() > length)) return false;
        return true;
    }

    private static bool Rising(double[] axis)
    {
        for (var i = 1; i < axis.Length; i++) if (!(axis[i] > axis[i - 1])) return false;
        return true;
    }
}
