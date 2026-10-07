using System.Collections.Concurrent;
using ECUStudio.Calibration.Model;

namespace ECUStudio.Simulation;

public sealed record GridOptions
{
    public double RpmMin { get; init; } = 800;
    public double RpmMax { get; init; } = 5500;
    public double CoarseRpmStep { get; init; } = 500;
    public double[] Pedals { get; init; } = [0, 25, 50, 75, 100];
    public int MaxRefineDepth { get; init; } = 3;
    /// <summary>Relative torque change between neighbours that triggers refinement.</summary>
    public double RefineTorqueJump { get; init; } = 0.12;
    public bool IncludeScenarios { get; init; } = true;
}

public sealed record Scenario(string Id, string Label, EnvironmentConditions Environment, int Gear);

public sealed record ScenarioResult(Scenario Scenario, IReadOnlyList<PointResult> WotLine);

public sealed record GridResult(IReadOnlyList<PointResult> ReferencePoints, IReadOnlyList<ScenarioResult> Scenarios, int EvaluatedPoints);

/// <summary>
/// Operating-point grid with adaptive sampling. The reference condition is sampled on a coarse
/// RPM × pedal grid and refined where the active limiter changes or torque jumps. Environmental
/// and gear scenarios evaluate only the WOT line, which is where component loads peak.
/// </summary>
public sealed class OperatingGrid(ISimulationEngine engine)
{
    public GridResult Run(SimulationInput input, GridOptions? options = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        options ??= new GridOptions();
        var count = 0;
        var reference = new ConcurrentBag<PointResult>();

        var rpms = Coarse(options).ToList();
        var pedalsDone = 0;
        Parallel.ForEach(options.Pedals, new ParallelOptions { CancellationToken = ct }, pedal =>
        {
            var line = new List<PointResult>(rpms.Count);
            foreach (var rpm in rpms) line.Add(engine.Evaluate(input, new OperatingPoint(rpm, pedal), EnvironmentConditions.Reference));
            var refined = Refine(input, line, pedal, EnvironmentConditions.Reference, 4, options, ct);
            foreach (var p in refined) reference.Add(p);
            Interlocked.Add(ref count, refined.Count);
            progress?.Report(0.6 * Interlocked.Increment(ref pedalsDone) / options.Pedals.Length);
        });

        var scenarioResults = new List<ScenarioResult>();
        if (options.IncludeScenarios)
        {
            var scenarios = Scenarios(input).ToList();
            var results = new ScenarioResult[scenarios.Count];
            var done = 0;
            Parallel.For(0, scenarios.Count, new ParallelOptions { CancellationToken = ct }, i =>
            {
                var s = scenarios[i];
                var line = rpms.Select(r => engine.Evaluate(input, new OperatingPoint(r, 100, s.Gear), s.Environment)).ToList();
                var refined = Refine(input, line, 100, s.Environment, s.Gear, options with { MaxRefineDepth = 1 }, ct);
                results[i] = new ScenarioResult(s, refined);
                Interlocked.Add(ref count, refined.Count);
                progress?.Report(0.6 + 0.4 * Interlocked.Increment(ref done) / scenarios.Count);
            });
            scenarioResults.AddRange(results);
        }

        var ordered = reference.OrderBy(p => p.Point.PedalPct).ThenBy(p => p.Point.Rpm).ToList();
        return new GridResult(ordered, scenarioResults, count);
    }

    public static IEnumerable<Scenario> Scenarios(SimulationInput input)
    {
        yield return new Scenario("cold_ambient", "Ambient −20 °C", new EnvironmentConditions { AmbientTempC = -20 }, 4);
        yield return new Scenario("hot_ambient", "Ambient +45 °C", new EnvironmentConditions { AmbientTempC = 45 }, 4);
        yield return new Scenario("altitude_1500", "Altitude 1500 m", new EnvironmentConditions { AltitudeM = 1500 }, 4);
        yield return new Scenario("altitude_3000", "Altitude 3000 m, +30 °C", new EnvironmentConditions { AltitudeM = 3000, AmbientTempC = 30 }, 4);
        yield return new Scenario("coolant_cold", "Cold engine", new EnvironmentConditions { Coolant = CoolantState.Cold }, 4);
        yield return new Scenario("coolant_hot", "Hot engine", new EnvironmentConditions { Coolant = CoolantState.Hot, AmbientTempC = 35 }, 4);
        if (input.Calibration.ByRole(MapRole.GearTorqueLimiter) is not null)
            for (var g = 1; g <= 6; g++) yield return new Scenario($"gear_{g}", $"Gear {g}", EnvironmentConditions.Reference, g);
    }

    private static IEnumerable<double> Coarse(GridOptions o)
    {
        yield return o.RpmMin;
        var start = Math.Ceiling((o.RpmMin + 1) / o.CoarseRpmStep) * o.CoarseRpmStep;
        for (var r = start; r < o.RpmMax; r += o.CoarseRpmStep) yield return r;
        yield return o.RpmMax;
    }

    private List<PointResult> Refine(SimulationInput input, List<PointResult> line, double pedal, EnvironmentConditions env, int gear, GridOptions o, CancellationToken ct)
    {
        var current = line;
        for (var depth = 0; depth < o.MaxRefineDepth; depth++)
        {
            ct.ThrowIfCancellationRequested();
            var next = new List<PointResult>(current.Count * 2) { current[0] };
            var added = false;
            for (var i = 1; i < current.Count; i++)
            {
                var a = current[i - 1];
                var b = current[i];
                if (NeedsRefinement(a, b, o) && b.Point.Rpm - a.Point.Rpm > 60)
                {
                    var mid = (a.Point.Rpm + b.Point.Rpm) / 2;
                    next.Add(engine.Evaluate(input, new OperatingPoint(Math.Round(mid), pedal, gear), env));
                    added = true;
                }
                next.Add(b);
            }
            current = next;
            if (!added) break;
        }
        return current;
    }

    private static bool NeedsRefinement(PointResult a, PointResult b, GridOptions o)
    {
        if (a.TorqueLimiter != b.TorqueLimiter || a.FuelLimiter != b.FuelLimiter || a.BoostLimiter != b.BoostLimiter) return true;
        if (a.Torque.Value is { } ta && b.Torque.Value is { } tb)
        {
            var denom = Math.Max(1, Math.Max(Math.Abs(ta), Math.Abs(tb)));
            if (Math.Abs(ta - tb) / denom > o.RefineTorqueJump) return true;
        }
        return false;
    }
}
