using System.Text.Json;
using System.Text.Json.Nodes;
using ECUStudio.AI;
using ECUStudio.Calibration.Model;
using ECUStudio.Components;
using ECUStudio.Core;

namespace ECUStudio.Application.Analysis;

/// <summary>
/// Builds the structured, token-lean context Claude receives. Only modified maps (downsampled),
/// the relevant dependency subgraph, simulation summaries, limits and unknowns go in; never the BIN.
/// Every object that may be cited gets an id in evidence_index.
/// </summary>
public static class AIContextBuilder
{
    private const int MaxGrid = 8;

    public static AIContext Build(AnalysisSession s)
    {
        var r = s.Report;
        var refs = new SortedSet<string>(StringComparer.Ordinal);
        var root = new JsonObject();

        root["vehicle"] = new JsonObject
        {
            ["make"] = r.Vehicle.Profile.Make.Text, ["model"] = r.Vehicle.Profile.Model.Text, ["model_year"] = r.Vehicle.Profile.ModelYear.Number,
            ["engine_code"] = r.Vehicle.Profile.EngineCode.Text, ["engine_code_confidence"] = r.Vehicle.Profile.EngineCode.Confidence,
            ["variant_probability"] = r.Vehicle.Profile.VariantProbability,
        };
        refs.Add("vehicle");
        root["ecu"] = new JsonObject
        {
            ["family"] = r.Ecu.EcuFamily, ["sw"] = r.Ecu.SoftwareNumber.Text, ["hw"] = r.Ecu.HardwareNumber.Text, ["oem"] = r.Ecu.OemPartNumber.Text,
            ["common_rail"] = s.Plugin.HasCommonRail, ["definition_source"] = r.DefinitionSource,
        };
        refs.Add("ecu");

        var hw = new JsonObject();
        foreach (var c in s.Profile.Hardware.All)
        {
            var id = c.Kind.ToString().ToLowerInvariant();
            hw[id] = new JsonObject
            {
                ["name"] = c.Name, ["source"] = c.Source.ToString(), ["confidence"] = c.Confidence, ["user_verified"] = c.UserVerified,
                ["parameters"] = new JsonObject(c.Parameters.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, ParamNode(kv.Value)))),
            };
            refs.Add($"component:{id}");
            foreach (var k in c.Parameters.Keys) refs.Add($"component:{id}.{k}");
        }
        root["hardware"] = hw;

        root["maps"] = new JsonArray(r.Maps.Select(m =>
        {
            refs.Add($"map:{m.Id}");
            return (JsonNode)new JsonObject
            {
                ["id"] = m.Id, ["name"] = m.Name, ["role"] = m.Role.ToString(), ["unit"] = m.Unit, ["x"] = m.XAxis, ["y"] = m.YAxis,
                ["source"] = m.Source.ToString(), ["confidence"] = m.Confidence, ["modified"] = m.Modified, ["min"] = Round(m.Min), ["max"] = Round(m.Max),
            };
        }).ToArray());

        var modified = new JsonArray();
        if (s.Diff is not null)
            foreach (var d in s.Diff.Modified)
            {
                refs.Add($"diff:{d.MapId}");
                modified.Add(new JsonObject
                {
                    ["map_id"] = d.MapId, ["role"] = d.Role.ToString(), ["unit"] = d.Unit, ["changed_cells"] = d.ChangedCells, ["total_cells"] = d.TotalCells,
                    ["mean_delta_pct"] = Round(d.MeanDeltaPct), ["max_delta_pct"] = Round(d.MaxDeltaPct), ["stock_max"] = Round(d.StockMax), ["mod_max"] = Round(d.ModMax),
                    ["uniform_ratio"] = d.MeanRatio is { } mr && d.RatioStdDev < 0.006 ? Round(mr, 4) : null,
                    ["x_axis"] = Downsample(d.XAxis), ["y_axis"] = Downsample(d.YAxis),
                    ["stock"] = Grid(d.Stock, d.Rows, d.Cols), ["modified"] = Grid(d.Modified, d.Rows, d.Cols),
                });
            }
        root["modified_maps"] = modified;

        var neighbourhood = s.Report.Dependencies.Neighbourhood(s.Diff?.Modified.Select(d => d.MapId) ?? r.Maps.Select(m => m.Id), 2);
        root["dependencies"] = new JsonObject
        {
            ["nodes"] = new JsonArray(neighbourhood.Nodes.Select(n => (JsonNode)new JsonObject { ["id"] = n.Id, ["label"] = n.Label, ["state"] = n.State.ToString(), ["map_id"] = n.MapId }).ToArray()),
            ["edges"] = new JsonArray(neighbourhood.Edges.Select(e => (JsonNode)new JsonArray(e.From, e.To, e.Relation)).ToArray()),
        };

        root["rule_findings"] = new JsonArray(r.CalibrationFindings.Select((f, i) =>
        {
            refs.Add($"rule:{f.Code}");
            foreach (var e in f.Evidence) refs.Add(e.Ref);
            return (JsonNode)new JsonObject { ["code"] = f.Code, ["text"] = f.Text, ["severity"] = f.Severity.ToWire(), ["confidence"] = f.Confidence, ["maps"] = new JsonArray(f.RelatedMaps.Select(m => (JsonNode)m!).ToArray()) };
        }).ToArray());

        root["candidates"] = new JsonArray(r.Candidates.Take(20).Select(c =>
        {
            refs.Add($"candidate:{c.Id}");
            refs.Add($"0x{c.HeaderAddress:X6}");
            return (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["address"] = $"0x{c.Address:X6}", ["dims"] = $"{c.Rows}x{c.Cols}", ["raw_range"] = $"{c.RawMin}-{c.RawMax}",
                ["x_axis_raw"] = Downsample(c.XAxisRaw), ["y_axis_raw"] = Downsample(c.YAxisRaw),
                ["hypotheses"] = new JsonArray(c.Hypotheses.Take(3).Select(h => (JsonNode)new JsonObject { ["role"] = h.Role.ToString(), ["confidence"] = h.Confidence }).ToArray()),
            };
        }).ToArray());

        JsonArray Wot(IReadOnlyList<WotSample>? w) => w is null ? [] : new JsonArray(w.Where((_, i) => i % 2 == 0).Select(p => (JsonNode)new JsonObject
        {
            ["rpm"] = p.Rpm, ["torque"] = Est(p.TorqueNm), ["power_hp"] = Est(p.PowerHp), ["map_mbar"] = Est(p.BoostMbar), ["iq_mg"] = Est(p.IqMg),
            ["lambda"] = Est(p.Lambda), ["egt_c"] = Est(p.EgtC), ["limiters"] = $"{p.TorqueLimiter}/{p.FuelLimiter}/{p.BoostLimiter}",
        }).ToArray());
        root["simulation"] = new JsonObject
        {
            ["note"] = "Engineering estimates; [value, low, high, confidence]",
            ["wot_modified"] = Wot(r.Simulation.ModifiedWot),
            ["wot_stock"] = Wot(r.Simulation.StockWot),
            ["scenarios"] = new JsonArray(r.Simulation.Scenarios.Select(sc => (JsonNode)new JsonObject
            {
                ["id"] = sc.Id, ["label"] = sc.Label, ["peak_hp"] = Est(sc.PeakPowerHp), ["peak_nm"] = Est(sc.PeakTorqueNm), ["max_egt"] = Est(sc.MaxEgtC),
                ["min_lambda"] = Est(sc.MinLambda), ["max_pr"] = Est(sc.MaxPressureRatio),
            }).ToArray()),
            ["assumptions"] = new JsonArray(r.Simulation.Assumptions.Select(a => (JsonNode)a!).ToArray()),
        };
        refs.Add("simulation:wot_modified");
        refs.Add("simulation:wot_stock");
        foreach (var sc in r.Simulation.Scenarios) refs.Add($"simulation:scenario.{sc.Id}");

        root["component_limits"] = new JsonArray(r.Risk.Components.Select(c =>
        {
            refs.Add($"risk:{c.Component}");
            foreach (var e in c.Evidence) refs.Add(e.Ref);
            return (JsonNode)new JsonObject
            {
                ["component"] = c.Component, ["metric"] = c.Metric, ["load"] = Est(c.Load), ["limit"] = ParamNode(c.Limit),
                ["utilization_pct"] = c.Utilization.IsKnown ? Est(c.Utilization) : null, ["change_vs_stock_pct"] = c.ChangeVsStockPct is { } ch ? Round(ch) : null,
                ["severity"] = c.Severity.ToWire(), ["confidence"] = c.Confidence, ["missing"] = new JsonArray(c.MissingData.Select(m => (JsonNode)m!).ToArray()),
            };
        }).ToArray());
        root["risk"] = new JsonObject { ["overall"] = r.Risk.Overall.ToWire(), ["confidence"] = r.Risk.Confidence, ["explanation"] = r.Risk.OverallExplanation };
        refs.Add("risk:overall");
        root["unknowns"] = new JsonArray(r.Unknowns.Select(u => (JsonNode)u!).ToArray());
        root["critical_unknowns"] = new JsonArray(r.Risk.CriticalUnknowns.Select(u => (JsonNode)u!).ToArray());
        root["data_availability"] = new JsonObject { ["level"] = r.DataAvailability.Level.ToString(), ["factors"] = new JsonArray(r.DataAvailability.Factors.Select(f => (JsonNode)f!).ToArray()) };
        root["evidence_index"] = new JsonArray(refs.Select(x => (JsonNode)x!).ToArray());

        return new AIContext
        {
            ContextJson = root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
            AllowedRefs = refs,
            BinaryHash = r.ModifiedSha256,
            StockHash = r.StockSha256,
            ProfileHash = s.Profile.ProfileHash,
            AnalysisVersion = r.AnalysisVersion,
            PhysicsComponents = r.Risk.Components.Select(c => (c.Component, c.Severity, c.Confidence)).ToList(),
            CriticalUnknowns = r.Risk.CriticalUnknowns,
        };
    }

    private static JsonNode? ParamNode(Param p) => p.IsKnown
        ? new JsonObject { ["value"] = p.Number is { } n ? JsonValue.Create(n) : JsonValue.Create(p.Text), ["unit"] = p.Unit, ["source"] = p.Source.ToString(), ["confidence"] = p.Confidence }
        : JsonValue.Create("UNKNOWN");

    private static JsonNode? Est(Estimate e) => e.IsKnown
        ? new JsonArray(Round(e.Value!.Value), Round(e.Low!.Value), Round(e.High!.Value), Round(e.Confidence, 2))
        : JsonValue.Create(e.Kind.ToString().ToUpperInvariant());

    private static double Round(double v, int digits = 1) => Math.Round(v, digits);

    private static JsonArray Downsample(double[] axis)
    {
        var idx = Indices(axis.Length);
        return new JsonArray(idx.Select(i => (JsonNode)Round(axis[i])).ToArray());
    }

    private static JsonArray Grid(double[] values, int rows, int cols)
    {
        var ri = Indices(rows);
        var ci = Indices(cols);
        return new JsonArray(ri.Select(r => (JsonNode)new JsonArray(ci.Select(c => (JsonNode)Round(values[r * cols + c])).ToArray())).ToArray());
    }

    private static int[] Indices(int n) => n <= MaxGrid ? Enumerable.Range(0, n).ToArray()
        : Enumerable.Range(0, MaxGrid).Select(i => (int)Math.Round(i * (n - 1) / (double)(MaxGrid - 1))).Distinct().ToArray();
}
