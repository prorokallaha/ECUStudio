using ECUStudio.Binary;
using ECUStudio.Calibration.Analysis;
using ECUStudio.Calibration.Model;
using ECUStudio.Calibration.Plugins;
using ECUStudio.Calibration.Scanning;
using ECUStudio.Components;
using ECUStudio.Core;
using ECUStudio.Risk;
using ECUStudio.Simulation;
using ECUStudio.Vehicle;

namespace ECUStudio.Application.Analysis;

public sealed record AnalysisRequest
{
    public required BinaryImage Modified { get; init; }
    public BinaryImage? Stock { get; init; }
    public string? Vin { get; init; }
    public IReadOnlyList<HardwareOverride> Overrides { get; init; } = [];
    public ExternalDefinition? Definition { get; init; }
    public string? PreferredVariantId { get; init; }
    public string? TransmissionId { get; init; }
    public IReadOnlyList<ConfirmedCandidate> ConfirmedCandidates { get; init; } = [];
    public bool HasDiagnosticLogs { get; init; }
    public Guid? ProjectId { get; init; }
    public GridOptions? Grid { get; init; }
}

public sealed record ConfirmedCandidate(int Address, MapRole Role);

/// <summary>Everything computed for one analysis, kept in memory for interactive endpoints (dyno, inspector, hex).</summary>
public sealed record AnalysisSession
{
    public required AnalysisReport Report { get; init; }
    public required AnalysisRequest Request { get; init; }
    public required IEcuPlugin Plugin { get; init; }
    public required CalibrationSet ModCalibration { get; init; }
    public CalibrationSet? StockCalibration { get; init; }
    public DiffResult? Diff { get; init; }
    public required SimulationInput ModInput { get; init; }
    public SimulationInput? StockInput { get; init; }
    public required VehicleProfile Profile { get; init; }
}

/// <summary>
/// Orchestrates the deterministic pipeline:
/// BIN → plugin detect/identify → definitions → CalibrationSet → diff/anomalies/Stage1 →
/// vehicle/components → simulation grid → risk → explain. No AI here (see AIAnalysisService).
/// </summary>
public sealed class AnalysisPipeline(PluginRegistry plugins, VehicleKnowledgeBase kb, ISimulationEngine engine)
{
    public const string AnalysisVersion = "0.1.0";

    public static readonly (string Id, string Label)[] Steps =
    [
        ("identify", "Identifying ECU"), ("read", "Reading calibration"), ("maps", "Finding maps"), ("stock", "Matching stock binary"),
        ("vehicle", "Resolving vehicle"), ("components", "Resolving components"), ("dependencies", "Building dependencies"),
        ("simulation", "Running simulation"), ("risk", "Running risk analysis"),
    ];

    public AnalysisSession Run(AnalysisRequest req, IProgress<StepProgress>? progress = null, CancellationToken ct = default)
    {
        void Step(string id, StepState s, double? f = null, string? msg = null) =>
            progress?.Report(new StepProgress(id, Steps.First(x => x.Id == id).Label, s, f, msg));

        Step("identify", StepState.Running);
        var (plugin, detection) = plugins.Detect(req.Modified);
        var ident = plugin.Identify(req.Modified);
        if (req.Stock is not null)
        {
            var stockDetection = plugin.Detect(req.Stock);
            if (stockDetection.Score < 0.3) throw new IncompatibleBinariesException("Stock file is not recognised by the same ECU plugin as the modified file");
            if (req.Stock.Length != req.Modified.Length) throw new IncompatibleBinariesException($"Stock ({req.Stock.Length}) and modified ({req.Modified.Length}) sizes differ");
        }
        Step("identify", StepState.Done, 1, $"{ident.EcuFamily}, SW {ident.SoftwareNumber.Text ?? "?"}");
        ct.ThrowIfCancellationRequested();

        Step("read", StepState.Running);
        var resolution = plugin.ResolveDefinitions(req.Modified, ident, req.Definition);
        var definitions = resolution.Definitions.ToList();
        var candidates = resolution.Candidates.ToList();
        foreach (var confirmed in req.ConfirmedCandidates)
        {
            var idx = candidates.FindIndex(c => c.Address == confirmed.Address);
            if (idx < 0) continue;
            var c = candidates[idx] with { Status = CandidateStatus.Confirmed, ConfirmedRole = confirmed.Role };
            candidates[idx] = c;
            definitions.RemoveAll(d => d.Role == confirmed.Role && d.Source == SourceType.SignatureScan);
            definitions.Add(Calibration.Plugins.Edc16U34.Edc16U34Plugin.FromCandidate(c, confirmed.Role) with { Source = SourceType.User, Confidence = 0.8 });
        }
        var modBuild = CalibrationBuilder.Build(req.Modified, plugin.PluginId, definitions);
        Step("read", StepState.Done, 1, $"{modBuild.Set.Maps.Count} maps decoded");
        Step("maps", StepState.Done, 1, $"{candidates.Count} candidate map(s)");

        Step("stock", req.Stock is null ? StepState.Skipped : StepState.Running);
        CalibrationSet? stockSet = null;
        DiffResult? diff = null;
        var calFindings = new List<Finding>();
        if (req.Stock is not null)
        {
            stockSet = CalibrationBuilder.Build(req.Stock, plugin.PluginId, definitions).Set;
            var extra = candidates.Select(c => new ByteRange(c.HeaderAddress, 4 + 2 * (c.Rows + c.Cols + c.Rows * c.Cols)));
            diff = MapDiffer.Compare(req.Stock, req.Modified, stockSet, modBuild.Set, ident.Sections, extra);
            calFindings.AddRange(AnomalyDetector.Detect(diff, modBuild.Set));
            calFindings.AddRange(Stage1ConsistencyAnalyzer.Analyze(stockSet, modBuild.Set, diff, plugin.HasCommonRail));
            Step("stock", StepState.Done, 1, $"{diff.Modified.Count()} modified map(s), {diff.ChangedBytes} bytes");
        }
        ct.ThrowIfCancellationRequested();

        Step("vehicle", StepState.Running);
        var vehicle = new VehicleResolver(kb).Resolve(req.Vin, plugin.PluginId, plugin.EngineFamilies, req.Overrides, req.PreferredVariantId, req.TransmissionId);
        Step("vehicle", StepState.Done, 1, vehicle.Profile.Model.Text);
        Step("components", StepState.Done, 1, $"{req.Overrides.Count} override(s)");

        var availability = Availability(req, resolution, vehicle.Profile);

        Step("dependencies", StepState.Running);
        var graph = DependencyGraphBuilder.Build(modBuild.Set, diff, plugin.HasCommonRail);
        Step("dependencies", StepState.Done, 1);

        Step("simulation", StepState.Running, 0);
        var modInput = new SimulationInput
        {
            Calibration = modBuild.Set, StockReference = stockSet, Hardware = vehicle.Profile.Hardware,
            ConfidenceCap = availability.Score, HasCommonRail = plugin.HasCommonRail,
        };
        var stockInput = stockSet is null ? null : modInput with { Calibration = stockSet };
        var grid = new OperatingGrid(engine);
        var simProgress = new Progress<double>(f => Step("simulation", StepState.Running, stockInput is null ? f : f / 2));
        var modGrid = grid.Run(modInput, req.Grid, simProgress, ct);
        GridResult? stockGrid = null;
        if (stockInput is not null)
            stockGrid = grid.Run(stockInput, req.Grid, new Progress<double>(f => Step("simulation", StepState.Running, 0.5 + f / 2)), ct);
        Step("simulation", StepState.Done, 1, $"{modGrid.EvaluatedPoints + (stockGrid?.EvaluatedPoints ?? 0)} operating points");

        Step("risk", StepState.Running);
        var risk = RiskEngine.Evaluate(new RiskInput
        {
            Hardware = vehicle.Profile.Hardware, Modified = modGrid, Stock = stockGrid, ModCalibration = modBuild.Set,
            CalibrationFindings = calFindings, HasCommonRail = plugin.HasCommonRail, ConfidenceCap = availability.Score,
        });
        graph = Overlay(graph, risk);
        var explanations = diff is null ? [] : Explainer.Explain(diff, graph, risk, modGrid, stockGrid);
        Step("risk", StepState.Done, 1, risk.Overall.ToWire());

        var report = new AnalysisReport
        {
            Id = Guid.NewGuid(),
            ProjectId = req.ProjectId,
            CreatedAt = DateTimeOffset.UtcNow,
            AnalysisVersion = AnalysisVersion,
            ModifiedSha256 = req.Modified.Sha256,
            StockSha256 = req.Stock?.Sha256,
            ModifiedName = req.Modified.FileName,
            StockName = req.Stock?.FileName,
            Detection = detection,
            Ecu = ident,
            Checksums = plugin.VerifyChecksums(req.Modified),
            Vehicle = vehicle,
            DataAvailability = availability,
            DefinitionSource = resolution.Source,
            DefinitionNotes = [.. resolution.Notes, .. modBuild.Errors],
            Maps = modBuild.Set.Maps.Select(m => Summary(m, diff, graph)).OrderBy(m => m.Category).ThenBy(m => m.Name).ToList(),
            Candidates = candidates,
            ModifiedMaps = diff?.Modified.Select(d => new DiffSummary(d.MapId, d.Name, d.Role, d.ChangedCells, d.TotalCells, Math.Round(d.MeanDeltaPct, 1), Math.Round(d.MaxDeltaPct, 1), d.StockMax, d.ModMax, d.Unit, d.MeanRatio)).ToList() ?? [],
            ChangedBytes = diff?.ChangedBytes ?? 0,
            UnmappedChanges = diff?.UnmappedChanges ?? [],
            CalibrationFindings = calFindings,
            Dependencies = graph,
            Simulation = SummarizeSimulation(modGrid, stockGrid, modInput.Assumptions),
            Risk = risk,
            Explanations = explanations,
            KeyMetrics = KeyMetrics(modGrid, stockGrid, risk),
            MainFindings = MainFindings(diff, risk, modBuild.Set),
            Unknowns = risk.CriticalUnknowns.Concat(vehicle.Profile.Notes).Distinct().ToList(),
        };

        return new AnalysisSession
        {
            Report = report, Request = req, Plugin = plugin, ModCalibration = modBuild.Set, StockCalibration = stockSet, Diff = diff,
            ModInput = modInput, StockInput = stockInput, Profile = vehicle.Profile,
        };
    }

    private static DataAvailability Availability(AnalysisRequest req, DefinitionResolution res, VehicleProfile profile)
    {
        var factors = new List<string> { "BIN" };
        var score = 0.3;
        if (req.Vin is not null) { score += 0.08; factors.Add("VIN"); }
        if (req.Stock is not null) { score += 0.07; factors.Add("stock BIN"); }
        if (res.Definitions.Any(d => d.Source is SourceType.DefinitionDb or SourceType.Xdf or SourceType.Damos or SourceType.A2L or SourceType.User))
        { score += 0.2; factors.Add($"map definitions ({res.Source})"); }
        else factors.Add("maps from signature scan only");
        if (profile.Hardware.All.Any(c => c.UserVerified)) { score += 0.12; factors.Add("user-verified hardware"); }
        if (req.HasDiagnosticLogs) { score += 0.15; factors.Add("diagnostic logs"); }
        score = Math.Round(Math.Clamp(score, 0.2, 0.9), 2);
        return new DataAvailability(ConfidenceLevels.FromScore(score), score, factors);
    }

    private static MapSummary Summary(CalibrationMap m, DiffResult? diff, DependencyGraph graph)
    {
        var d = diff?.Maps.FirstOrDefault(x => x.MapId == m.Id);
        var def = m.Definition;
        return new MapSummary
        {
            Id = m.Id, Name = def.Name, Role = def.Role, Category = def.Category, Address = def.Address, Rows = def.Rows, Cols = def.Cols,
            DataType = def.DataType, Endian = def.Endian, Factor = def.Factor, Offset = def.Offset, Unit = def.Unit,
            XAxis = def.XAxis is null ? "-" : $"{def.XAxis.Name} [{def.XAxis.Unit}] × {def.Cols}",
            YAxis = def.YAxis is null ? "-" : $"{def.YAxis.Name} [{def.YAxis.Unit}] × {def.Rows}",
            Source = def.Source, Confidence = def.Confidence,
            Modified = d?.IsModified ?? false, ModifiedPct = Math.Round((d?.ChangedFraction ?? 0) * 100, 1), MaxDeltaPct = Math.Round(d?.MaxDeltaPct ?? 0, 1),
            DependencyCount = graph.DependencyCount(m.Id), WhyItMatters = def.Role.WhyItMatters(), Min = m.Min, Max = m.Max,
        };
    }

    private static DependencyGraph Overlay(DependencyGraph g, RiskReport risk)
    {
        var nodes = g.Nodes.Select(n =>
        {
            if (n.MapId is null || n.State is NodeState.Unknown or NodeState.NotApplicable) return n;
            var sev = SeverityExtensions.Worst(risk.Findings.Where(f => f.RelatedMaps.Contains(n.MapId) && f.Severity != Severity.Unknown).Select(f => f.Severity).DefaultIfEmpty(Severity.Safe));
            return sev switch
            {
                Severity.Danger => n with { State = NodeState.Danger },
                Severity.Warning => n with { State = NodeState.Warning },
                _ => n,
            };
        }).ToList();
        return g with { Nodes = nodes };
    }

    private static List<WotSample> Wot(GridResult g) => g.ReferencePoints.Where(p => p.Point.PedalPct >= 99).OrderBy(p => p.Point.Rpm)
        .Select(p => new WotSample(p.Point.Rpm, p.Torque, p.PowerHp, p.Map, p.Iq, p.Lambda, p.Egt, p.TorqueLimiter, p.FuelLimiter, p.BoostLimiter)).ToList();

    private static SimulationSummary SummarizeSimulation(GridResult mod, GridResult? stock, ModelAssumptions a) => new()
    {
        ModifiedWot = Wot(mod),
        StockWot = stock is null ? null : Wot(stock),
        Scenarios = mod.Scenarios.Select(s => new ScenarioSummary(s.Scenario.Id, s.Scenario.Label,
            MaxBy(s.WotLine, p => p.PowerHp), MaxBy(s.WotLine, p => p.Torque), MaxBy(s.WotLine, p => p.Egt),
            MinBy(s.WotLine, p => p.Lambda), MaxBy(s.WotLine, p => p.PressureRatio))).ToList(),
        EvaluatedPoints = mod.EvaluatedPoints + (stock?.EvaluatedPoints ?? 0),
        Assumptions = a.Describe(),
    };

    internal static Estimate MaxBy(IEnumerable<PointResult> pts, Func<PointResult, Estimate> f) =>
        pts.Where(p => !p.BeyondCalibratedRange).Select(f).Where(e => e.IsKnown).MaxBy(e => e.Value!.Value) ?? Estimate.Unknown();
    internal static Estimate MinBy(IEnumerable<PointResult> pts, Func<PointResult, Estimate> f) =>
        pts.Where(p => !p.BeyondCalibratedRange).Select(f).Where(e => e.IsKnown).MinBy(e => e.Value!.Value) ?? Estimate.Unknown();

    private static List<KeyMetric> KeyMetrics(GridResult mod, GridResult? stock, RiskReport risk)
    {
        var mw = mod.ReferencePoints.Where(p => p.Point.PedalPct >= 99).ToList();
        var sw = stock?.ReferencePoints.Where(p => p.Point.PedalPct >= 99).ToList();
        KeyMetric M(string id, string label, Func<PointResult, Estimate> f, bool min = false, string? link = null) =>
            new(id, label, sw is null ? null : (min ? MinBy(sw, f) : MaxBy(sw, f)), min ? MinBy(mw, f) : MaxBy(mw, f), null, null, link);
        KeyMetric Comp(string id, string label, string component)
        {
            var c = risk.Components.FirstOrDefault(x => x.Component == component);
            if (c is null) return new KeyMetric(id, label, null, Estimate.Unknown(), "n/a");
            var detail = c.ShowExactUtilization && c.Utilization.IsKnown ? $"{c.Utilization.Display().Value}% utilization"
                : $"{c.Metric} · load {c.LoadLevel.ToString().ToUpperInvariant()} · limit {(c.Limit.IsKnown ? "known" : "UNKNOWN")}";
            return new KeyMetric(id, label, null, c.ShowExactUtilization ? c.Utilization : c.Load, detail, c.Severity, $"risks/{component}");
        }
        return
        [
            M("power", "Estimated Power", p => p.PowerHp, link: "dyno"),
            M("torque", "Estimated Torque", p => p.Torque, link: "dyno"),
            M("boost", "Max Boost", p => p.Map, link: "maps/boost_target"),
            M("iq", "Max IQ", p => p.Iq, link: "maps/torque_to_iq"),
            M("egt", "Estimated EGT", p => p.Egt, link: "risks/thermal"),
            Comp("turbo", "Turbo Load", "turbo"),
            Comp("injectors", "Injector Load", "injectors"),
            Comp("transmission", "Transmission Load", "transmission"),
        ];
    }

    private static List<MainFinding> MainFindings(DiffResult? diff, RiskReport risk, CalibrationSet set)
    {
        var list = new List<MainFinding>();
        if (diff is not null)
        {
            foreach (var d in diff.Modified.OrderByDescending(d => Math.Abs(d.MaxIncreasePct)).Take(8))
            {
                var abs = d.ModMax - d.StockMax;
                var text = d.Unit == "mbar" ? $"{d.Name} {abs:+0;-0} mbar" : $"{d.Name} {d.MaxIncreasePct:+0;-0}%";
                list.Add(new MainFinding(text, Severity.Review, $"maps/{d.MapId}", "MAP_CHANGED"));
            }
            foreach (var role in new[] { MapRole.Soi, MapRole.Duration })
                if (set.ByRole(role) is { } m && diff.Maps.FirstOrDefault(x => x.MapId == m.Id) is { IsModified: false })
                    list.Add(new MainFinding($"{role.DisplayName()} unchanged", Severity.Review, $"maps/{m.Id}", "MAP_UNCHANGED"));
        }
        foreach (var c in risk.Components.Where(c => c.Load.IsKnown && c.Severity != Severity.Safe))
        {
            var text = c.ShowExactUtilization && c.Margin.IsKnown
                ? $"{c.Label} estimated margin {c.Margin.Display().Value}%"
                : $"{c.Label}: load {c.LoadLevel.ToString().ToUpperInvariant()}{(c.ChangeVsStockPct is { } ch ? $" ({ch:+0;-0}% vs stock)" : "")}, limit {(c.Limit.IsKnown ? "known" : "UNKNOWN")}";
            list.Add(new MainFinding(text, c.Severity, $"risks/{c.Component}", $"MARGIN_{c.Component.ToUpperInvariant()}"));
        }
        foreach (var f in risk.Findings.Where(f => f.Source == FindingSource.Rule && f.Severity is Severity.Warning or Severity.Danger).Take(6))
            list.Add(new MainFinding(f.Text, f.Severity, f.RelatedMaps.Count > 0 ? $"maps/{f.RelatedMaps[0]}" : null, f.Code));
        return list;
    }
}
