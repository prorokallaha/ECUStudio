using ECUStudio.Binary;
using ECUStudio.Calibration.Model;
using ECUStudio.Core;

namespace ECUStudio.Calibration.Analysis;

public sealed record CalibrationBuildResult(CalibrationSet Set, IReadOnlyList<string> Errors);

public static class CalibrationBuilder
{
    /// <summary>Decodes every definition; a broken definition is reported, it does not abort the analysis.</summary>
    public static CalibrationBuildResult Build(BinaryImage image, string pluginId, IEnumerable<MapDefinition> definitions)
    {
        var maps = new List<CalibrationMap>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var def in definitions)
        {
            if (!seen.Add(def.Id)) { errors.Add($"Duplicate map id '{def.Id}' ignored"); continue; }
            try { maps.Add(MapDecoder.Decode(image, def)); }
            catch (DefinitionException ex) { errors.Add(ex.Message); }
        }
        return new CalibrationBuildResult(new CalibrationSet(image.Sha256, pluginId, maps), errors);
    }
}
