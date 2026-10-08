using ECUStudio.Simulation.Logs;
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
using ECUStudio.Vehicle.Resolution;

namespace ECUStudio.Application.Analysis;

public sealed record AnalysisRequest
{
    public required BinaryImage Modified { get; init; }
    public BinaryImage? Stock { get; init; }
    public string? Vin { get; init; }
    public IReadOnlyList<HardwareOverride> Overrides { get; init; } = [];
    public ExternalDefinition? Definition { get; init; }
    /// <summary>Where <see cref="Definition"/> came from (project binding or library match), copied to the report.</summary>
    public Library.DefinitionBinding? DefinitionBinding { get; init; }
    public IReadOnlyList<string> DefinitionNotes { get; init; } = [];
    public string? PreferredVariantId { get; init; }
    public string? TransmissionId { get; init; }
    public IReadOnlyList<ConfirmedCandidate> ConfirmedCandidates { get; init; } = [];
    /// <summary>Diagnostic logs recorded on this vehicle; validated against the model, they raise data availability only when they agree.</summary>
    public IReadOnlyList<LogInput> Logs { get; init; } = [];
    public Guid? ProjectId { get; init; }
    public GridOptions? Grid { get; init; }
}

/// <param name="Structure">When set (knowledge carried over from another file with the same SW/HW), the candidate at
/// <paramref name="Address"/> must have this exact structure fingerprint or the confirmation is not applied.</param>
public sealed record ConfirmedCandidate(int Address, MapRole Role, string? Structure = null, string? Origin = null);

/// <param name="AgainstStock">True when the log was recorded while the stock calibration was flashed.</param>
public sealed record LogInput(string Id, DiagnosticLog Log, bool AgainstStock);

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
public sealed class AnalysisPipeline(PluginRegistry plugins, VehicleKnowledgeBase kb, ISimulationEngine engine, VehicleResolver? vehicleResolver = null)
{
    public const string AnalysisVersion = "0.1.0";

    public static readonly (string Id, string Label)[] Steps =
    [
        ("identify", "Identifying ECU"), ("read", "Reading calibration"), ("maps", "Finding maps"), ("stock", "Matching stock binary"),
        ("vehicle", "Resolving vehicle"), ("components", "Resolving components"), ("logs", "Validating logs"), ("dependencies", "Building dependencies"),
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
        var knowledgeNotes = new List<string>();
        foreach (var confirmed in req.ConfirmedCandidates)
        {
            var idx = candidates.FindIndex(c => c.Address == confirmed.Address);
            if (idx < 0) continue;
            if (confirmed.Structure is { } fp && MapKnowledge.Fingerprint(candidates[idx]) != fp)
            {
                knowledgeNotes.Add($"Confirmation of {confirmed.Role} at 0x{confirmed.Address:X} ({confirmed.Origin}) not applied: the structure there differs");
                continue;
            }
            if (confirmed.Origin is { } origin) knowledgeNotes.Add($"{confirmed.Role} at 0x{confirmed.Address:X} confirmed earlier ({origin}); same SW/HW and identical structure");
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
            // Candidate maps and described checksum storage are known regions: a corrected checksum is not a code patch.
            var extra = candidates.Select(c => new ByteRange(c.HeaderAddress, 4 + 2 * (c.Rows + c.Cols + c.Rows * c.Cols)))
                .Concat(resolution.Checksums.Select(c => new ByteRange(c.StoredAt, c.EffectiveStoreSize)));
            diff = MapDiffer.Compare(req.Stock, req.Modified, stockSet, modBuild.Set, ident.Sections, extra);
            for (var i = 0; i < candidates.Count; i++)
                candidates[i] = candidates[i] with { Change = CandidateChange.Compare(candidates[i], req.Stock.Span, req.Modified.Span) };
            calFindings.AddRange(AnomalyDetector.Detect(diff, modBuild.Set));
            calFindings.AddRange(Stage1ConsistencyAnalyzer.Analyze(stockSet, modBuild.Set, diff, plugin.HasCommonRail));
            Step("stock", StepState.Done, 1, $"{diff.Modified.Count()} modified map(s), {candidates.Count(c => c.Change?.IsModified == true)} modified candidate(s), {diff.ChangedBytes} bytes");
        }
        ct.ThrowIfCancellationRequested();

        Step("vehicle", StepState.Running);
        var vehicle = (vehicleResolver ?? new VehicleResolver(kb)).Resolve(new VehicleQuery
        {
            Vin = req.Vin,
            Ecu = new EcuFacts
            {
                PluginId = plugin.PluginId, EcuFamily = ident.EcuFamily, DetectionScore = detection.Score, EngineFamilies = plugin.EngineFamilies,
                BoschNumber = ident.BoschNumber.Text, HardwareNumber = ident.HardwareNumber.Text, SoftwareNumber = ident.SoftwareNumber.Text,
                OemPartNumber = ident.OemPartNumber.Text, EngineText = ident.EngineCode.Text, FileSha256 = req.Modified.Sha256,
            },
            Overrides = req.Overrides, PreferredVariantId = req.PreferredVariantId, TransmissionId = req.TransmissionId,
        });
        Step("vehicle", StepState.Done, 1, vehicle.Profile.Model.Text);
        Step("components", StepState.Done, 1, $"{req.Overrides.Count} override(s)");

        // Logs are compared on model values (not confidences), so they are validated before the final confidence cap is known.
        Step("logs", req.Logs.Count == 0 ? StepState.Skipped : StepState.Running);
        var hw = vehicle.Profile.Hardware;
        var preMod = new SimulationInput { Calibration = modBuild.Set, StockReference = stockSet, Hardware = hw, HasCommonRail = plugin.HasCommonRail };
        var logValidations = req.Logs
            .Where(l => !l.AgainstStock || stockSet is not null)
            .Select(l => LogValidator.Validate(l.Id, l.Log, l.AgainstStock ? preMod with { Calibration = stockSet! } : preMod, engine, l.AgainstStock, LogValidator.Cylinders(hw)))
            .ToList();
        calFindings.AddRange(LogFindings(logValidations));
        if (req.Logs.Count > 0) Step("logs", StepState.Done, 1, string.Join(", ", logValidations.Select(v => $"{v.Name}: {v.Status}")));

        var availability = Availability(req, resolution, vehicle.Profile, logValidations);

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
        var simProgress = new InlineProgress<double>(f => Step("simulation", StepState.Running, stockInput is null ? f : f / 2));
        var modGrid = grid.Run(modInput, req.Grid, simProgress, ct);
        GridResult? stockGrid = null;
        if (stockInput is not null)
            stockGrid = grid.Run(stockInput, req.Grid, new InlineProgress<double>(f => Step("simulation", StepState.Running, 0.5 + f / 2)), ct);
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
            Checksums = plugin.VerifyChecksums(req.Modified, resolution.Checksums),
            Vehicle = vehicle,
            DataAvailability = availability,
            DefinitionSource = resolution.Source,
            DefinitionNotes = [.. req.DefinitionNotes, .. resolution.Notes, .. knowledgeNotes, .. modBuild.Errors],
            DefinitionBinding = req.DefinitionBinding,
            Maps = modBuild.Set.Maps.Select(m => Summary(m, diff, graph)).OrderBy(m => m.Category).ThenBy(m => m.Name).ToList(),
            Candidates = candidates,
            ModifiedMaps = diff?.Modified.Select(d => new DiffSummary(d.MapId, d.Name, d.Role, d.ChangedCells, d.TotalCells, Math.Round(d.MeanDeltaPct, 1), Math.Round(d.MaxDeltaPct, 1), d.StockMax, d.ModMax, d.Unit, d.MeanRatio)).ToList() ?? [],
            ChangedBytes = diff?.ChangedBytes ?? 0,
            UnmappedChanges = diff?.UnmappedChanges ?? [],
            CalibrationFindings = calFindings,
            Dependencies = graph,
            Simulation = SummarizeSimulation(modGrid, stockGrid, modInput.Assumptions) with { Coverage = SimulationCoverage.Evaluate(modInput) },
            Risk = risk,
            Explanations = explanations,
            KeyMetrics = KeyMetrics(modGrid, stockGrid, risk),
            MainFindings = MainFindings(diff, risk, modBuild.Set, candidates),
            Unknowns = risk.CriticalUnknowns.Concat(vehicle.Profile.Notes).Distinct().ToList(),
            Logs = logValidations,
        };

        return new AnalysisSession
        {
            Report = report, Request = req, Plugin = plugin, ModCalibration = modBuild.Set, StockCalibration = stockSet, Diff = diff,
            ModInput = modInput, StockInput = stockInput, Profile = vehicle.Profile,
        };
    }

    private static IEnumerable<Finding> LogFindings(IReadOnlyList<LogValidation> logs)
    {
        foreach (var l in logs)
            foreach (var c in l.Channels.Where(c => c.Status == LogAgreement.Deviates))
                yield return new Finding
                {
                    Code = "MODEL_LOG_MISMATCH",
                    Text = $"{c.Label}: logged values deviate from the model by {c.BiasPct:+0.#;-0.#} % (tolerance ±{c.TolerancePct:0} %) in '{l.Name}'. {c.Note}.",
                    Severity = Severity.Review,
                    Confidence = 0.7,
                    Evidence = [new Evidence(EvidenceType.Log, $"log:{l.LogId}:{c.Channel}", $"bias {c.BiasPct:0.#} %, {c.Bins.Count} rpm bins, {l.WotSamples} full-load samples")],
                    Assumptions = ["Log was recorded with the analysed calibration flashed" + (l.AgainstStock ? " (stock)" : "")],
                    Unknowns = ["Sensor calibration and logging latency"],
                    Source = FindingSource.Physics,
                };
    }

    private static DataAvailability Availability(AnalysisRequest req, DefinitionResolution res, VehicleProfile profile, IReadOnlyList<LogValidation> logs)
    {
        var factors = new List<string> { "BIN" };
        var score = 0.3;
        if (req.Vin is not null) { score += 0.08; factors.Add("VIN"); }
        if (req.Stock is not null) { score += 0.07; factors.Add("stock BIN"); }
        if (res.Definitions.Any(d => d.Source is SourceType.DefinitionDb or SourceType.Xdf or SourceType.Damos or SourceType.A2L or SourceType.User))
        { score += 0.2; factors.Add($"map definitions ({res.Source})"); }
        else factors.Add("maps from signature scan only");
        if (profile.Hardware.All.Any(c => c.UserVerified)) { score += 0.12; factors.Add("user-verified hardware"); }
        if (logs.Any(l => l.Status == LogAgreement.Deviates)) factors.Add("diagnostic log disagrees with model (not counted)");
        else if (logs.Any(l => l.Status == LogAgreement.Agrees)) { score += 0.15; factors.Add("diagnostic log agrees with model"); }
        else if (logs.Count > 0) factors.Add("diagnostic log without enough full-load data (not counted)");
        score = Math.Round(Math.Clamp(score, 0.2, 0.9), 2);
        return new DataAvailability(ConfidenceLevels.FromScore(score), score, factors);
    }

    private static MapSummary Summary(CalibrationMap m, DiffResult? diff, DependencyGraph graph)
    {
        var d = diff?.Maps.FirstOrDefault(x => x.MapId == m.Id);
        var def = m.Definition;
        return new MapSummary
        {
            Id = m.Id, Name = def.Name, Description = string.IsNullOrWhiteSpace(def.Description) ? null : def.Description,
            Group = ECUStudio.Calibration.Definitions.BoschLabels.Component(def.Name), Role = def.Role, Category = def.Category, Address = def.Address, Rows = def.Rows, Cols = def.Cols,
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

    private static List<MainFinding> MainFindings(DiffResult? diff, RiskReport risk, CalibrationSet set, IReadOnlyList<MapCandidate> candidates)
    {
        var list = new List<MainFinding>();
        // Changed maps whose purpose is not established: shown by structure, never by a hypothesised name.
        // Copies with identical axes (per gear / per mode variants) are reported once.
        var changedGroups = candidates.Where(c => c.Change?.IsModified == true && c.Status != CandidateStatus.Rejected)
            .GroupBy(MapKnowledge.Fingerprint)
            .OrderByDescending(g => g.Sum(c => c.Change!.ChangedCells)).Take(8);
        foreach (var g in changedGroups)
        {
            var c = g.First();
            var ch = c.Change!;
            var copies = g.Count() > 1 ? $" (+{g.Count() - 1} with the same axes)" : "";
            var text = $"{c.DisplayName}{copies}: {ch.ChangedCells}/{ch.TotalCells} cells changed, mean {ch.MeanDeltaPct:+0.#;-0.#;0} %{(ch.AxesChanged ? ", axes changed" : "")}";
            list.Add(new MainFinding(text, Severity.Review, $"maps?candidate={c.Id}", "CANDIDATE_CHANGED",
                new Dictionary<string, string> { ["candidateId"] = c.Id, ["copies"] = (g.Count() - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        }
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

/// <summary>
/// Invokes the handler on the reporting thread. <see cref="Progress{T}"/> posts to the thread pool, so its callbacks can
/// arrive after the step (or the whole job) has finished and overwrite a terminal state.
/// </summary>
internal sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
