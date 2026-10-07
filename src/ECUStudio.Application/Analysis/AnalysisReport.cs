using ECUStudio.Simulation.Logs;
using ECUStudio.Binary;
using ECUStudio.Calibration.Analysis;
using ECUStudio.Calibration.Model;
using ECUStudio.Calibration.Plugins;
using ECUStudio.Calibration.Scanning;
using ECUStudio.Core;
using ECUStudio.Risk;
using ECUStudio.Simulation;
using ECUStudio.Vehicle;

namespace ECUStudio.Application.Analysis;

public sealed record DataAvailability(ConfidenceLevel Level, double Score, IReadOnlyList<string> Factors);

public sealed record MapSummary
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public MapRole Role { get; init; }
    public MapCategory Category { get; init; }
    public int Address { get; init; }
    public int Rows { get; init; }
    public int Cols { get; init; }
    public DataType DataType { get; init; }
    public Endianness Endian { get; init; }
    public double Factor { get; init; }
    public double Offset { get; init; }
    public string Unit { get; init; } = "-";
    public string XAxis { get; init; } = "-";
    public string YAxis { get; init; } = "-";
    public SourceType Source { get; init; }
    public double Confidence { get; init; }
    public bool Modified { get; init; }
    public double ModifiedPct { get; init; }
    public double MaxDeltaPct { get; init; }
    public int DependencyCount { get; init; }
    public string WhyItMatters { get; init; } = "";
    public double Min { get; init; }
    public double Max { get; init; }
}

public sealed record DiffSummary(string MapId, string Name, MapRole Role, int ChangedCells, int TotalCells, double MeanDeltaPct, double MaxDeltaPct, double StockMax, double ModMax, string Unit, double? MeanRatio);

public sealed record KeyMetric(string Id, string Label, Estimate? Stock, Estimate Modified, string? Detail = null, Severity? Severity = null, string? Link = null);

/// <summary>A headline finding. <see cref="Args"/> carries structured values so the UI can render it in the user's language.</summary>
public sealed record MainFinding(string Text, Severity Severity, string? Link, string Code, IReadOnlyDictionary<string, string>? Args = null);

public sealed record WotSample(double Rpm, Estimate TorqueNm, Estimate PowerHp, Estimate BoostMbar, Estimate IqMg, Estimate Lambda, Estimate EgtC, Limiter TorqueLimiter, Limiter FuelLimiter, Limiter BoostLimiter);

public sealed record SimulationSummary
{
    public required IReadOnlyList<WotSample> ModifiedWot { get; init; }
    public IReadOnlyList<WotSample>? StockWot { get; init; }
    public required IReadOnlyList<ScenarioSummary> Scenarios { get; init; }
    public int EvaluatedPoints { get; init; }
    public IReadOnlyList<string> Assumptions { get; init; } = [];
    /// <summary>Per output: computed, partial or UNKNOWN, and the missing maps/hardware/files (modified calibration).</summary>
    public IReadOnlyList<OutputCoverage> Coverage { get; init; } = [];
}

public sealed record ScenarioSummary(string Id, string Label, Estimate PeakPowerHp, Estimate PeakTorqueNm, Estimate MaxEgtC, Estimate MinLambda, Estimate MaxPressureRatio);

public sealed record AnalysisReport
{
    public required Guid Id { get; init; }
    public Guid? ProjectId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string AnalysisVersion { get; init; }
    public required string ModifiedSha256 { get; init; }
    public string? StockSha256 { get; init; }
    public required string ModifiedName { get; init; }
    public string? StockName { get; init; }
    public required DetectionResult Detection { get; init; }
    public required EcuIdentification Ecu { get; init; }
    public required ChecksumReport Checksums { get; init; }
    public required VehicleResolution Vehicle { get; init; }
    public required DataAvailability DataAvailability { get; init; }
    public required string DefinitionSource { get; init; }
    public IReadOnlyList<string> DefinitionNotes { get; init; } = [];
    public Library.DefinitionBinding? DefinitionBinding { get; init; }
    public required IReadOnlyList<MapSummary> Maps { get; init; }
    public required IReadOnlyList<MapCandidate> Candidates { get; init; }
    public IReadOnlyList<DiffSummary> ModifiedMaps { get; init; } = [];
    public int ChangedBytes { get; init; }
    public IReadOnlyList<UnmappedChange> UnmappedChanges { get; init; } = [];
    public required IReadOnlyList<Finding> CalibrationFindings { get; init; }
    public required DependencyGraph Dependencies { get; init; }
    public required SimulationSummary Simulation { get; init; }
    public required RiskReport Risk { get; init; }
    public IReadOnlyList<MapExplanation> Explanations { get; init; } = [];
    public required IReadOnlyList<KeyMetric> KeyMetrics { get; init; }
    public required IReadOnlyList<MainFinding> MainFindings { get; init; }
    public IReadOnlyList<string> Unknowns { get; init; } = [];
    public IReadOnlyList<LogValidation> Logs { get; init; } = [];
    public string Disclaimer => "All values are engineering estimates with ranges and confidence, not measurements. No dyno measurement is implied.";
}
