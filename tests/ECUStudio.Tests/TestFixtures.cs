using ECUStudio.Application.Analysis;
using ECUStudio.Application.DevTools;
using ECUStudio.Binary;
using ECUStudio.Calibration.Definitions;
using ECUStudio.Calibration.Plugins;
using ECUStudio.Calibration.Plugins.Edc16U34;
using ECUStudio.Simulation;
using ECUStudio.Vehicle;

namespace ECUStudio.Tests;

public static class Fixtures
{
    public static readonly Lazy<SyntheticEdc16U34.Result> Stock = new(() => SyntheticEdc16U34.Generate(SyntheticVariant.Stock));
    public static readonly Lazy<SyntheticEdc16U34.Result> Stage1 = new(() => SyntheticEdc16U34.Generate(SyntheticVariant.Stage1));
    public static readonly Lazy<SyntheticEdc16U34.Result> Aggressive = new(() => SyntheticEdc16U34.Generate(SyntheticVariant.Stage1Aggressive));

    public static DefinitionDatabase Db => new([Stock.Value.Definition]);
    public static Edc16U34Plugin Plugin(bool withDb = true) => new(withDb ? Db : DefinitionDatabase.Empty);
    public static PluginRegistry Registry(bool withDb = true) => new([Plugin(withDb)]);
    public static readonly Lazy<VehicleKnowledgeBase> Kb = new(VehicleKnowledgeBase.LoadEmbedded);

    public static BinaryImage Image(Lazy<SyntheticEdc16U34.Result> r, string name) => BinaryImage.FromBytes(r.Value.Image, name);

    public static AnalysisPipeline Pipeline(bool withDb = true) => new(Registry(withDb), Kb.Value, new SimulationEngine());

    public static readonly GridOptions FastGrid = new() { CoarseRpmStep = 500, Pedals = [0, 50, 100], MaxRefineDepth = 1 };

    public const string GolfVin = "WVWZZZ1KZ6W123456";
}
