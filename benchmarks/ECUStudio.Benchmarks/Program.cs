using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using ECUStudio.Application.Analysis;
using ECUStudio.Application.DevTools;
using ECUStudio.Binary;
using ECUStudio.Calibration.Analysis;
using ECUStudio.Calibration.Definitions;
using ECUStudio.Calibration.Model;
using ECUStudio.Calibration.Plugins;
using ECUStudio.Calibration.Plugins.Edc16U34;
using ECUStudio.Calibration.Scanning;
using ECUStudio.Risk;
using ECUStudio.Simulation;
using ECUStudio.Vehicle;

// dotnet run -c Release --project benchmarks/ECUStudio.Benchmarks -- --filter '*'
BenchmarkSwitcher.FromAssembly(typeof(EcuBenchmarks).Assembly).Run(args);

/// <summary>Hot paths of one analysis on a 512 KB synthetic EDC16U34 image (stock vs. Stage 1).</summary>
[MemoryDiagnoser]
public class EcuBenchmarks
{
    private BinaryImage _stock = null!, _mod = null!;
    private IReadOnlyList<MapDefinition> _defs = null!;
    private CalibrationSet _stockSet = null!, _modSet = null!;
    private CalibrationMap _boost = null!;
    private AnalysisSession _session = null!;
    private SimulationEngine _engine = null!;
    private GridResult _modGrid = null!, _stockGrid = null!;
    private static readonly GridOptions Fast = new() { CoarseRpmStep = 500, Pedals = [0, 50, 100], MaxRefineDepth = 1 };

    [GlobalSetup]
    public void Setup()
    {
        var stock = SyntheticEdc16U34.Generate(SyntheticVariant.Stock);
        var stage1 = SyntheticEdc16U34.Generate(SyntheticVariant.Stage1);
        _stock = BinaryImage.FromBytes(stock.Image, "stock.bin");
        _mod = BinaryImage.FromBytes(stage1.Image, "stage1.bin");
        var db = new DefinitionDatabase([stock.Definition]);
        var pipeline = new AnalysisPipeline(new PluginRegistry([new Edc16U34Plugin(db)]), VehicleKnowledgeBase.LoadEmbedded(), new SimulationEngine());
        _session = pipeline.Run(new AnalysisRequest { Modified = _mod, Stock = _stock, Vin = "WVWZZZ1KZ6W123456", Grid = Fast });
        _defs = _session.ModCalibration.Maps.Select(m => m.Definition).ToList();
        _modSet = _session.ModCalibration;
        _stockSet = _session.StockCalibration!;
        _boost = _modSet.ByRole(MapRole.BoostTarget)!;
        _engine = new SimulationEngine();
        _modGrid = new OperatingGrid(_engine).Run(_session.ModInput, Fast);
        _stockGrid = new OperatingGrid(_engine).Run(_session.StockInput!, Fast);
    }

    [Benchmark(Description = "Scan: Bosch map scanner (512 KB)")]
    public int Scan() => BoschMapScanner.Scan(_mod.Data.Span).Count;

    [Benchmark(Description = "Extract: decode all defined maps")]
    public int Extract() => CalibrationBuilder.Build(_mod, "edc16u34", _defs).Set.Maps.Count;

    [Benchmark(Description = "Interpolate: 10k bilinear lookups", OperationsPerInvoke = 10_000)]
    public double Interpolate()
    {
        double s = 0;
        for (var i = 0; i < 10_000; i++) s += Interpolation.Bilinear(_boost, 800 + i % 4000, 5 + i % 60);
        return s;
    }

    [Benchmark(Description = "Diff: byte + map diff")]
    public int Diff() => MapDiffer.Compare(_stock, _mod, _stockSet, _modSet, _session.Report.Ecu.Sections).Maps.Count;

    [Benchmark(Description = "Simulate: one operating point")]
    public PointResult SimulatePoint() => _engine.Evaluate(_session.ModInput, new OperatingPoint(2200, 100), EnvironmentConditions.Reference);

    [Benchmark(Description = "Simulate: operating grid + scenarios")]
    public int SimulateGrid() => new OperatingGrid(_engine).Run(_session.ModInput, Fast).EvaluatedPoints;

    [Benchmark(Description = "Risk: component margins")]
    public int Risk() => RiskEngine.Evaluate(new RiskInput
    {
        Hardware = _session.Profile.Hardware, Modified = _modGrid, Stock = _stockGrid, ModCalibration = _modSet, HasCommonRail = false,
    }).Components.Count;

    [Benchmark(Description = "Full analysis pipeline")]
    public int Full() => new AnalysisPipeline(new PluginRegistry([new Edc16U34Plugin(new DefinitionDatabase([SyntheticEdc16U34.Generate(SyntheticVariant.Stock).Definition]))]),
        VehicleKnowledgeBase.LoadEmbedded(), _engine).Run(new AnalysisRequest { Modified = _mod, Stock = _stock, Grid = Fast }).Report.Maps.Count;
}
