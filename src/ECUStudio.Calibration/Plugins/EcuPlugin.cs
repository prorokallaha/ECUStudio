using ECUStudio.Binary;
using ECUStudio.Calibration.Model;
using ECUStudio.Calibration.Scanning;
using ECUStudio.Core;

namespace ECUStudio.Calibration.Plugins;

public sealed record DetectionResult(string PluginId, double Score, IReadOnlyList<string> Reasons);

public sealed record EcuIdentification
{
    public required string PluginId { get; init; }
    public required string EcuFamily { get; init; }
    public string Manufacturer { get; init; } = "Unknown";
    public Param BoschNumber { get; init; } = Param.Unknown();
    public Param OemPartNumber { get; init; } = Param.Unknown();
    public Param HardwareNumber { get; init; } = Param.Unknown();
    public Param SoftwareNumber { get; init; } = Param.Unknown();
    public Param SoftwareVersion { get; init; } = Param.Unknown();
    public Param EngineCode { get; init; } = Param.Unknown();
    public string Processor { get; init; } = "Unknown";
    public Endianness Endianness { get; init; }
    public int FlashSize { get; init; }
    public IReadOnlyList<MemorySection> Sections { get; init; } = [];
    public double Confidence { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>Result of resolving map definitions for one binary.</summary>
public sealed record DefinitionResolution(
    IReadOnlyList<MapDefinition> Definitions,
    IReadOnlyList<MapCandidate> Candidates,
    string Source,
    IReadOnlyList<string> Notes)
{
    /// <summary>Checksum blocks described by the resolved definition (empty when none are known).</summary>
    public IReadOnlyList<ChecksumSpec> Checksums { get; init; } = [];
}

/// <summary>
/// ECU family plugin. The core (calibration analysis, simulation, risk) only talks to this
/// interface, so new families (EDC15/17, MED17, MD1, MG1, SID, Delphi, Marelli) plug in
/// without changes to the simulation core.
/// </summary>
public interface IEcuPlugin
{
    string PluginId { get; }
    string DisplayName { get; }
    /// <summary>Engine families this plugin's physics defaults apply to (e.g. "VAG_PD_19").</summary>
    IReadOnlyList<string> EngineFamilies { get; }
    /// <summary>True when the injection system has a common rail (rail pressure applicable).</summary>
    bool HasCommonRail { get; }

    DetectionResult Detect(BinaryImage image);
    EcuIdentification Identify(BinaryImage image);
    DefinitionResolution ResolveDefinitions(BinaryImage image, EcuIdentification identification, ExternalDefinition? external);
    /// <summary>Verifies the checksum blocks a definition describes; never guesses undescribed ones.</summary>
    ChecksumReport VerifyChecksums(BinaryImage image, IReadOnlyList<ChecksumSpec> blocks);
}

/// <summary>A user-supplied definition (XDF, DAMOS, A2L, OLS export, native JSON).</summary>
public sealed record ExternalDefinition(SourceType Source, IReadOnlyList<MapDefinition> Maps, string Name)
{
    /// <summary>Importer remarks (skipped objects, rebasing) surfaced in the definition resolution.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>Checksum blocks described by the definition file (native JSON only).</summary>
    public IReadOnlyList<ChecksumSpec> Checksums { get; init; } = [];
}

public sealed class PluginRegistry
{
    private readonly List<IEcuPlugin> _plugins;

    public PluginRegistry(IEnumerable<IEcuPlugin> plugins) => _plugins = plugins.ToList();

    public IReadOnlyList<IEcuPlugin> Plugins => _plugins;

    public IEcuPlugin? Get(string id) => _plugins.FirstOrDefault(p => p.PluginId.Equals(id, StringComparison.OrdinalIgnoreCase));

    public (IEcuPlugin Plugin, DetectionResult Detection) Detect(BinaryImage image, double minScore = 0.3)
    {
        var results = _plugins.Select(p => (Plugin: p, Detection: p.Detect(image))).OrderByDescending(r => r.Detection.Score).ToList();
        if (results.Count == 0 || results[0].Detection.Score < minScore)
        {
            var reasons = results.SelectMany(r => r.Detection.Reasons.Select(x => $"{r.Plugin.PluginId}: {x}")).ToList();
            throw new UnsupportedEcuException("No ECU plugin recognised this binary. " + string.Join("; ", reasons));
        }
        return results[0];
    }
}
