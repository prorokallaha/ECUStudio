using ECUStudio.Simulation.Logs;
using System.CommandLine;
using System.Text.Json;
using ECUStudio.Application.Analysis;
using ECUStudio.Application.DevTools;
using ECUStudio.Application.Reports;
using ECUStudio.Binary;
using ECUStudio.Calibration.Definitions;
using ECUStudio.Calibration.Plugins;
using ECUStudio.Components;
using ECUStudio.Core;
using ECUStudio.Infrastructure;
using ECUStudio.Simulation;
using Microsoft.Extensions.DependencyInjection;

namespace ECUStudio.Cli;

/// <summary>Project manifest (*.ecu): JSON with paths relative to the manifest file.</summary>
public sealed record ProjectManifest
{
    public string? Name { get; init; }
    public string? Vin { get; init; }
    public string? Stock { get; init; }
    public required string Modified { get; init; }
    public string? Definition { get; init; }
    public string? Variant { get; init; }
    public string? Transmission { get; init; }
    public IReadOnlyList<HardwareOverride> Hardware { get; init; } = [];
    public DynoRequest? Dyno { get; init; }
}

public static class Program
{
    private const int ExitOk = 0, ExitError = 1, ExitRiskGate = 3;

    public static async Task<int> Main(string[] args)
    {
        var json = new Option<bool>("--json") { Description = "Machine-readable JSON output" };
        var output = new Option<FileInfo?>("--out", "-o") { Description = "Write output to a file instead of stdout" };
        var stockOpt = new Option<FileInfo?>("--stock", "-s") { Description = "Stock (original) binary for diff and baseline" };
        var vinOpt = new Option<string?>("--vin") { Description = "17-character VIN (evidence, not proof of hardware)" };
        var defOpt = new Option<FileInfo?>("--definition", "-d") { Description = "Map definition (.xdf, .a2l or .ecudef.json)" };
        var transOpt = new Option<string?>("--transmission") { Description = "Transmission catalog id, e.g. trans_dsg_dq250" };
        var logOpt = new Option<FileInfo[]>("--log", "-l") { Description = "Diagnostic log (VCDS / CSV) recorded with this binary flashed; repeatable", AllowMultipleArgumentsPerToken = true };
        var failOn = new Option<string?>("--fail-on") { Description = "Exit with code 3 when overall risk is at least: review|warning|danger" };
        var definitionsDir = new Option<DirectoryInfo?>("--definitions-dir") { Description = "Definition DB directory (*.ecudef.json)", Recursive = true };

        var root = new RootCommand("ECUStudio — ECU binary analysis, virtual dyno and risk review (estimates, not measurements)");
        root.Options.Add(definitionsDir);

        // identify
        var idFile = new Argument<FileInfo>("file") { Description = "ECU binary" };
        var identify = new Command("identify", "Detect ECU family and read identification") { idFile, json, output };
        identify.SetAction((pr, ct) => Run(pr, output, async sp =>
        {
            var image = BinaryImage.FromFile(Existing(pr.GetValue(idFile)!).FullName);
            var registry = sp.GetRequiredService<PluginRegistry>();
            var (plugin, detection) = registry.Detect(image);
            var ident = plugin.Identify(image);
            var checksums = plugin.VerifyChecksums(image, plugin.ResolveDefinitions(image, ident, null).Checksums);
            if (pr.GetValue(json)) return Serialize(new { file = image.FileName, sha256 = image.Sha256, size = image.Length, detection, identification = ident, checksums });
            return $"""
                File       {image.FileName} ({image.Length / 1024} KB) sha256 {image.Sha256[..16]}…
                ECU        {ident.EcuFamily} — detection score {detection.Score:0.00}
                HW         {MarkdownReport.P(ident.HardwareNumber)}
                SW         {MarkdownReport.P(ident.SoftwareNumber)}
                OEM        {MarkdownReport.P(ident.OemPartNumber)}
                Engine     {MarkdownReport.P(ident.EngineCode)}
                CPU        {ident.Processor}
                Checksums  {checksums.Overall} — {checksums.Note}
                Evidence   {string.Join("; ", detection.Reasons)}
                """;
        }, sp => Task.CompletedTask, pr.GetValue(definitionsDir), ct));
        root.Subcommands.Add(identify);

        // analyze
        var anFile = new Argument<FileInfo>("file") { Description = "Modified (or single) ECU binary" };
        var analyze = new Command("analyze", "Full analysis: maps, diff, simulation, risk") { anFile, stockOpt, vinOpt, defOpt, transOpt, logOpt, json, output, failOn };
        analyze.SetAction((pr, ct) =>
        {
            AnalysisSession? session = null;
            return Run(pr, output, async sp =>
            {
                session = RunPipeline(sp, Existing(pr.GetValue(anFile)!), pr.GetValue(stockOpt), pr.GetValue(vinOpt), pr.GetValue(defOpt), null, pr.GetValue(transOpt), [], ct, pr.GetValue(logOpt));
                return pr.GetValue(json) ? Serialize(session.Report) : MarkdownReport.Render(session.Report);
            }, _ => Task.CompletedTask, pr.GetValue(definitionsDir), ct, () => RiskGate(session, pr.GetValue(failOn)));
        });
        root.Subcommands.Add(analyze);

        // compare
        var cmpStock = new Argument<FileInfo>("stock");
        var cmpMod = new Argument<FileInfo>("modified");
        var compare = new Command("compare", "Diff two binaries: changed maps, unmapped changes, anomalies") { cmpStock, cmpMod, defOpt, json, output };
        compare.SetAction((pr, ct) => Run(pr, output, async sp =>
        {
            var s = RunPipeline(sp, Existing(pr.GetValue(cmpMod)!), Existing(pr.GetValue(cmpStock)!), null, pr.GetValue(defOpt), null, null, [], ct);
            var r = s.Report;
            if (pr.GetValue(json)) return Serialize(new { r.ModifiedName, r.StockName, r.ChangedBytes, r.ModifiedMaps, r.UnmappedChanges, findings = r.CalibrationFindings });
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{r.StockName} → {r.ModifiedName}: {r.ChangedBytes} changed bytes, {r.ModifiedMaps.Count} modified maps, {r.UnmappedChanges.Count} unmapped change regions");
            sb.AppendLine();
            foreach (var d in r.ModifiedMaps)
                sb.AppendLine($"  {d.Name,-28} {d.ChangedCells,4}/{d.TotalCells,-4} cells  mean {d.MeanDeltaPct,7:+0.0;-0.0}%  max {d.MaxDeltaPct,7:+0.0;-0.0}%  {d.StockMax:0.##} → {d.ModMax:0.##} {d.Unit}");
            foreach (var u in r.UnmappedChanges) sb.AppendLine($"  unmapped 0x{u.Start:X6}..0x{u.Start + u.Length:X6} ({u.Length} B) in {u.Section}");
            sb.AppendLine();
            foreach (var f in r.CalibrationFindings.OrderByDescending(f => f.Severity.Rank()))
                sb.AppendLine($"  [{f.Severity.ToWire(),-7}] {f.Code}: {f.Text}");
            return sb.ToString();
        }, _ => Task.CompletedTask, pr.GetValue(definitionsDir), ct));
        root.Subcommands.Add(compare);

        // simulate
        var simFile = new Argument<FileInfo>("project") { Description = "Project manifest (*.ecu, JSON) or a binary" };
        var simulate = new Command("simulate", "Virtual dyno for a project manifest or binary") { simFile, stockOpt, vinOpt, json, output };
        simulate.SetAction((pr, ct) => Run(pr, output, async sp =>
        {
            var file = Existing(pr.GetValue(simFile)!);
            ProjectManifest m;
            if (file.Extension.Equals(".ecu", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                m = JsonSerializer.Deserialize<ProjectManifest>(await File.ReadAllTextAsync(file.FullName, ct), Json.Options) ?? throw new EcuStudioException("INVALID_PROJECT", "Empty project file");
            else
                m = new ProjectManifest { Modified = file.FullName, Stock = pr.GetValue(stockOpt)?.FullName, Vin = pr.GetValue(vinOpt) };
            var dir = file.DirectoryName ?? ".";
            FileInfo? Rel(string? path) => path is null ? null : Existing(new FileInfo(Path.IsPathRooted(path) ? path : Path.Combine(dir, path)));
            var session = RunPipeline(sp, Rel(m.Modified)!, Rel(m.Stock), m.Vin, Rel(m.Definition), m.Variant, m.Transmission, m.Hardware, ct);
            var req = m.Dyno ?? new DynoRequest();
            req.Validate();
            var dyno = new VirtualDyno(sp.GetRequiredService<ISimulationEngine>()).Run(req, session.ModInput, session.StockInput);
            if (pr.GetValue(json)) return Serialize(dyno);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Virtual dyno — gear {req.Gear}, {req.ThrottlePct:0}% pedal, {req.AmbientTempC:0} °C, {req.AltitudeM:0} m");
            if (dyno.Stock is { } st) sb.AppendLine($"Stock     peak {MarkdownReport.E(st.PeakPowerHp)} @ {st.PeakPowerRpm:0} rpm, {MarkdownReport.E(st.PeakTorque)} @ {st.PeakTorqueRpm:0} rpm");
            sb.AppendLine($"Modified  peak {MarkdownReport.E(dyno.Modified.PeakPowerHp)} @ {dyno.Modified.PeakPowerRpm:0} rpm, {MarkdownReport.E(dyno.Modified.PeakTorque)} @ {dyno.Modified.PeakTorqueRpm:0} rpm");
            sb.AppendLine();
            sb.AppendLine("   RPM | Torque Nm (range)      | Power hp (range)     | Boost mbar | IQ mg | λ    | EGT °C | limiter");
            foreach (var p in dyno.Modified.Points.Where((_, i) => i % 2 == 0))
                sb.AppendLine($"{p.Point.Rpm,6:0} | {Range(p.Torque),-22} | {Range(p.PowerHp),-20} | {p.Map.Value,10:0} | {p.Iq.Value,5:0.0} | {p.Lambda.Value,4:0.00} | {p.Egt.Value,6:0} | {p.TorqueLimiter}/{p.FuelLimiter}{(p.BeyondCalibratedRange ? " (beyond calibrated range)" : "")}");
            sb.AppendLine().AppendLine(dyno.Disclaimer);
            return sb.ToString();
        }, _ => Task.CompletedTask, pr.GetValue(definitionsDir), ct));
        root.Subcommands.Add(simulate);

        // demo
        var demoDir = new Argument<DirectoryInfo>("dir") { Description = "Output directory" };
        var demo = new Command("demo", "Write SYNTHETIC EDC16U34 stock/stage1 images and a sample project manifest") { demoDir };
        demo.SetAction((pr, ct) =>
        {
            var dir = pr.GetValue(demoDir)!;
            dir.Create();
            foreach (var (variant, name) in new[] { (SyntheticVariant.Stock, "stock_synthetic.bin"), (SyntheticVariant.Stage1, "stage1_synthetic.bin"), (SyntheticVariant.Stage1Aggressive, "stage1_aggressive_synthetic.bin") })
                File.WriteAllBytes(Path.Combine(dir.FullName, name), SyntheticEdc16U34.Generate(variant).Image);
            File.WriteAllText(Path.Combine(dir.FullName, "demo.ecu"), JsonSerializer.Serialize(new ProjectManifest
            {
                Name = "Synthetic Golf V 1.9 TDI", Vin = "WVWZZZ1KZ6W123456", Stock = "stock_synthetic.bin", Modified = "stage1_synthetic.bin",
                Dyno = new DynoRequest { RpmStep = 250 },
            }, new JsonSerializerOptions(Json.Options) { WriteIndented = true }));
            Console.WriteLine($"Synthetic demo files written to {dir.FullName} (not real calibrations).");
            return Task.FromResult(ExitOk);
        });
        root.Subcommands.Add(demo);

        return await root.Parse(args).InvokeAsync();
    }

    private static AnalysisSession RunPipeline(IServiceProvider sp, FileInfo modified, FileInfo? stock, string? vin, FileInfo? definition,
        string? variant, string? transmission, IReadOnlyList<HardwareOverride> hardware, CancellationToken ct, FileInfo[]? logs = null)
    {
        var pipeline = sp.GetRequiredService<AnalysisPipeline>();
        string? lastStep = null;
        // Steps report fractional progress repeatedly; print each step once.
        var progress = new Progress<StepProgress>(s =>
        {
            if (s.State != StepState.Running || s.Step == lastStep) return;
            lastStep = s.Step;
            Console.Error.WriteLine($"… {s.Label}");
        });
        return pipeline.Run(new AnalysisRequest
        {
            Modified = BinaryImage.FromFile(modified.FullName),
            Stock = stock is null ? null : BinaryImage.FromFile(Existing(stock).FullName),
            Vin = vin,
            Definition = definition is null ? null : DefinitionImporters.Import(definition.Name, File.ReadAllText(Existing(definition).FullName)),
            PreferredVariantId = variant,
            TransmissionId = transmission,
            Overrides = hardware,
            Logs = (logs ?? []).Select(l => new LogInput(l.Name, LogParser.Parse(l.Name, LogParser.Decode(File.ReadAllBytes(Existing(l).FullName))), false)).ToList(),
        }, progress, ct);
    }

    private static async Task<int> Run(ParseResult pr, Option<FileInfo?> outOpt, Func<IServiceProvider, Task<string>> body,
        Func<IServiceProvider, Task> _, DirectoryInfo? definitionsDir, CancellationToken ct, Func<int>? exitCode = null)
    {
        try
        {
            var services = new ServiceCollection();
            services.AddEcuStudio(new EcuStudioOptions { Storage = "memory", DefinitionsPath = definitionsDir?.FullName });
            await using var sp = services.BuildServiceProvider();
            var text = await body(sp);
            if (pr.GetValue(outOpt) is { } f) { await File.WriteAllTextAsync(f.FullName, text, ct); Console.Error.WriteLine($"Written {f.FullName}"); }
            else Console.WriteLine(text);
            return exitCode?.Invoke() ?? ExitOk;
        }
        catch (EcuStudioException ex)
        {
            Console.Error.WriteLine($"error {ex.Code}: {ex.Message}");
            return ExitError;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"error IO: {ex.Message}");
            return ExitError;
        }
    }

    private static int RiskGate(AnalysisSession? session, string? failOn)
    {
        if (session is null || string.IsNullOrWhiteSpace(failOn)) return ExitOk;
        var threshold = SeverityExtensions.Parse(failOn);
        return session.Report.Risk.Overall.Rank() >= threshold.Rank() ? ExitRiskGate : ExitOk;
    }

    private static FileInfo Existing(FileInfo f) => f.Exists ? f : throw new NotFoundException($"File not found: {f.FullName}");

    private static string Range(Estimate e) => e.IsKnown ? $"{e.Value:0} ({e.Low:0}–{e.High:0})" : e.Kind.ToString();

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(Json.Options) { WriteIndented = true });
}
