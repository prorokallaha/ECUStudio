using System.Text.Json.Serialization;
using ECUStudio.Calibration.Model;
using ECUStudio.Core;

namespace ECUStudio.Calibration.Analysis;

[JsonConverter(typeof(JsonStringEnumConverter<NodeKind>))]
public enum NodeKind { Map, Quantity }

[JsonConverter(typeof(JsonStringEnumConverter<NodeState>))]
public enum NodeState { Stock, Modified, Warning, Danger, Unknown, NotApplicable }

public sealed record DependencyNode(string Id, string Label, NodeKind Kind, MapRole? Role, string? MapId, NodeState State, string? Detail);

public sealed record DependencyEdge(string From, string To, string Relation);

public sealed record DependencyGraph(IReadOnlyList<DependencyNode> Nodes, IReadOnlyList<DependencyEdge> Edges)
{
    public int DependencyCount(string mapId)
    {
        var node = Nodes.FirstOrDefault(n => n.MapId == mapId);
        return node is null ? 0 : Edges.Count(e => e.From == node.Id || e.To == node.Id);
    }

    /// <summary>Subgraph within <paramref name="depth"/> hops of the given map nodes (used to keep AI context small).</summary>
    public DependencyGraph Neighbourhood(IEnumerable<string> mapIds, int depth = 2)
    {
        var frontier = new HashSet<string>(Nodes.Where(n => n.MapId is not null && mapIds.Contains(n.MapId)).Select(n => n.Id));
        var keep = new HashSet<string>(frontier);
        for (var i = 0; i < depth; i++)
        {
            var next = Edges.Where(e => frontier.Contains(e.From) || frontier.Contains(e.To)).SelectMany(e => new[] { e.From, e.To }).ToHashSet();
            next.ExceptWith(keep);
            keep.UnionWith(next);
            frontier = next;
        }
        return new DependencyGraph(Nodes.Where(n => keep.Contains(n.Id)).ToList(), Edges.Where(e => keep.Contains(e.From) && keep.Contains(e.To)).ToList());
    }
}

/// <summary>
/// Builds the ECU signal-flow graph for a torque-oriented diesel ECU (EDC16/EDC17 style).
/// Map nodes are coloured by stock/modified state; risk severity is overlaid later.
/// </summary>
public static class DependencyGraphBuilder
{
    private static readonly (string From, string To, string Relation)[] Topology =
    [
        ("DriverWish", "q_requested_torque", "pedal,rpm → Nm"),
        ("q_requested_torque", "q_permitted_torque", "min()"),
        ("TorqueLimiter", "q_permitted_torque", "caps by rpm, p_atm"),
        ("GearTorqueLimiter", "q_permitted_torque", "caps by gear"),
        ("GearboxTorqueMonitor", "q_permitted_torque", "TCU torque limit"),
        ("q_permitted_torque", "TorqueToIq", "Nm → mg"),
        ("TorqueToIq", "q_iq_requested", ""),
        ("q_iq_requested", "q_iq", "min()"),
        ("SmokeLimiter", "q_iq", "caps by air mass"),
        ("q_air_mass", "SmokeLimiter", "input"),
        ("q_iq_requested", "BoostTarget", "IQ axis"),
        ("BoostTarget", "q_boost", "target"),
        ("BoostLimiter", "q_boost", "caps by rpm, p_atm"),
        ("Svbl", "q_boost", "overboost cut"),
        ("VntDuty", "q_boost", "actuator"),
        ("q_boost", "q_air_mass", "VE · ρ"),
        ("q_iq", "Duration", "IQ axis"),
        ("q_iq", "Soi", "IQ axis"),
        ("RailPressure", "Duration", "pressure"),
        ("Duration", "q_combustion", "injection window"),
        ("Soi", "q_combustion", "phasing"),
        ("q_iq", "q_combustion", "fuel energy"),
        ("q_air_mass", "q_lambda", "air"),
        ("q_iq", "q_lambda", "fuel"),
        ("q_lambda", "q_combustion", ""),
        ("q_combustion", "q_torque", "η_b"),
        ("q_combustion", "q_egt", "exhaust energy"),
        ("EgtProtection", "q_iq", "derate"),
        ("TemperatureProtection", "q_permitted_torque", "derate"),
        ("q_boost", "q_turbo_load", "pressure ratio"),
        ("q_air_mass", "q_turbo_load", "corrected flow"),
        ("q_egt", "q_turbo_load", "turbine inlet T"),
        ("q_torque", "q_drivetrain_load", "crank torque"),
    ];

    private static readonly Dictionary<string, string> QuantityLabels = new()
    {
        ["q_requested_torque"] = "Requested Torque", ["q_permitted_torque"] = "Permitted Torque", ["q_iq_requested"] = "Requested IQ",
        ["q_iq"] = "Injected Quantity", ["q_boost"] = "Boost (MAP)", ["q_air_mass"] = "Air Mass", ["q_lambda"] = "Lambda",
        ["q_combustion"] = "Combustion", ["q_torque"] = "Engine Torque", ["q_egt"] = "EGT", ["q_turbo_load"] = "Turbo Load",
        ["q_drivetrain_load"] = "Drivetrain Load",
    };

    public static DependencyGraph Build(CalibrationSet modSet, DiffResult? diff, bool hasCommonRail)
    {
        var nodes = new Dictionary<string, DependencyNode>();
        foreach (var (from, to, _) in Topology)
            foreach (var id in new[] { from, to })
            {
                if (nodes.ContainsKey(id)) continue;
                if (id.StartsWith("q_", StringComparison.Ordinal))
                {
                    nodes[id] = new DependencyNode(id, QuantityLabels[id], NodeKind.Quantity, null, null, NodeState.Stock, null);
                    continue;
                }
                var role = Enum.Parse<MapRole>(id);
                var map = modSet.ByRole(role);
                var state = role == MapRole.RailPressure && !hasCommonRail ? NodeState.NotApplicable
                    : map is null ? NodeState.Unknown
                    : diff?.Maps.FirstOrDefault(d => d.MapId == map.Id) is { IsModified: true } ? NodeState.Modified
                    : NodeState.Stock;
                var detail = state switch
                {
                    NodeState.NotApplicable => "Unit-injector engine: no common rail",
                    NodeState.Unknown => "Map not identified in this binary",
                    _ => map is null ? null : $"{map.Definition.Source}, confidence {map.Definition.Confidence:0.00}",
                };
                nodes[id] = new DependencyNode($"map_{id}", role.DisplayName(), NodeKind.Map, role, map?.Id, state, detail);
            }

        string Key(string raw) => raw.StartsWith("q_", StringComparison.Ordinal) ? raw : $"map_{raw}";
        var edges = Topology.Select(t => new DependencyEdge(Key(t.From), Key(t.To), t.Relation)).ToList();
        return new DependencyGraph(nodes.Values.ToList(), edges);
    }
}
