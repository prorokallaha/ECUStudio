using ECUStudio.Binary;
using ECUStudio.Calibration.Model;
using ECUStudio.Core;

namespace ECUStudio.Calibration.Analysis;

/// <summary>
/// Checks that a Stage 1 style modification is internally consistent: requests, limiters,
/// conversions, air and injection maps must move together. Calibration-level only;
/// physics-based consequences (lambda, EGT, margins) are evaluated by the risk engine.
/// </summary>
public static class Stage1ConsistencyAnalyzer
{
    private const double Significant = 3.0; // % change considered intentional

    public static List<Finding> Analyze(CalibrationSet stock, CalibrationSet mod, DiffResult diff, bool hasCommonRail)
    {
        var f = new List<Finding>();
        MapDiff? D(MapRole r) => mod.ByRole(r) is { } m ? diff.Maps.FirstOrDefault(d => d.MapId == m.Id) : null;
        double Inc(MapRole r) => D(r)?.MaxIncreasePct ?? 0;
        bool Raised(MapRole r) => Inc(r) > Significant || (D(r)?.MeanDeltaPct ?? 0) > Significant;
        Evidence Ev(MapRole r) => D(r) is { } d
            ? new Evidence(EvidenceType.Diff, $"diff:{d.MapId}", $"max {d.StockMax:0.#}→{d.ModMax:0.#} {d.Unit} ({d.MaxIncreasePct:+0.#;-0.#}%), mean {d.MeanDeltaPct:+0.#;-0.#}%")
            : new Evidence(EvidenceType.Rule, $"missing:{r}", $"{r.DisplayName()} not identified");
        string[] Ids(params MapRole[] roles) => roles.Select(r => mod.ByRole(r)?.Id).OfType<string>().ToArray();

        // Missing maps reduce what can be checked; say so explicitly.
        var essential = new List<MapRole> { MapRole.DriverWish, MapRole.TorqueLimiter, MapRole.TorqueToIq, MapRole.SmokeLimiter, MapRole.BoostTarget, MapRole.BoostLimiter, MapRole.Svbl, MapRole.Soi, MapRole.Duration };
        if (hasCommonRail) essential.Add(MapRole.RailPressure);
        var missing = essential.Where(r => mod.ByRole(r) is null).ToList();
        if (missing.Count > 0)
        {
            f.Add(new Finding
            {
                Code = "STAGE1_MAPS_MISSING",
                Text = $"Not identified: {string.Join(", ", missing.Select(m => m.DisplayName()))}. Related consistency checks are skipped.",
                Severity = Severity.Unknown,
                Confidence = 1,
                Evidence = missing.Select(m => new Evidence(EvidenceType.Rule, $"missing:{m}", "no definition / candidate below threshold")).ToList(),
                Unknowns = missing.Select(m => $"{m.DisplayName()} location and content").ToList(),
            });
        }

        // Driver wish vs torque limiter
        if (Raised(MapRole.DriverWish) && !Raised(MapRole.TorqueLimiter) && mod.ByRole(MapRole.TorqueLimiter) is not null)
            f.Add(Make("DW_WITHOUT_LIMITER", "Driver Wish raised but Torque Limiter unchanged: peak torque stays capped, only pedal response changes", Severity.Review, 0.8,
                [Ev(MapRole.DriverWish), Ev(MapRole.TorqueLimiter)], Ids(MapRole.DriverWish, MapRole.TorqueLimiter)));
        if (Raised(MapRole.TorqueLimiter) && mod.ByRole(MapRole.DriverWish) is { } dw && mod.ByRole(MapRole.TorqueLimiter) is { } tl && dw.Max < tl.Max * 0.97)
            f.Add(Make("LIMITER_ABOVE_REQUEST", $"Torque Limiter max ({tl.Max:0} Nm) exceeds Driver Wish max ({dw.Max:0} Nm): the limiter increase is not reachable", Severity.Review, 0.75,
                [Ev(MapRole.TorqueLimiter), Ev(MapRole.DriverWish)], Ids(MapRole.DriverWish, MapRole.TorqueLimiter)));

        // Torque request vs conversion map range
        if (mod.ByRole(MapRole.TorqueToIq) is { } tq && mod.ByRole(MapRole.TorqueLimiter) is { } tl2)
        {
            var axisTop = tq.YAxis[^1];
            if (tl2.Max > axisTop * 1.01)
                f.Add(Make("TORQUE_BEYOND_CONVERSION_AXIS", $"Torque Limiter allows {tl2.Max:0} Nm but the Torque→IQ map axis ends at {axisTop:0} Nm: requests above are clamped to the last breakpoint", Severity.Review, 0.8,
                    [Ev(MapRole.TorqueLimiter), new Evidence(EvidenceType.Map, $"map:{tq.Id}", $"Y-axis top {axisTop:0} Nm")], Ids(MapRole.TorqueLimiter, MapRole.TorqueToIq)));
        }

        // Fuel request vs smoke limiter
        var fuelUp = Math.Max(Inc(MapRole.TorqueToIq), Inc(MapRole.TorqueLimiter));
        if (fuelUp > Significant && !Raised(MapRole.SmokeLimiter) && mod.ByRole(MapRole.SmokeLimiter) is not null)
            f.Add(Make("SMOKE_LIMITER_CAPS", "Torque/IQ request raised but Smoke Limiter unchanged: injected fuel stays capped by air mass", Severity.Review, 0.75,
                [Ev(MapRole.TorqueToIq), Ev(MapRole.TorqueLimiter), Ev(MapRole.SmokeLimiter)], Ids(MapRole.TorqueToIq, MapRole.TorqueLimiter, MapRole.SmokeLimiter)));
        if (Inc(MapRole.SmokeLimiter) > Inc(MapRole.BoostTarget) + 8)
            f.Add(Make("SMOKE_WITHOUT_AIR", $"Smoke Limiter raised {Inc(MapRole.SmokeLimiter):0}% while Boost Target raised {Inc(MapRole.BoostTarget):0}%: more fuel per unit of air, lower lambda, higher EGT and smoke", Severity.Warning, 0.7,
                [Ev(MapRole.SmokeLimiter), Ev(MapRole.BoostTarget)], Ids(MapRole.SmokeLimiter, MapRole.BoostTarget), ["thermal", "turbo"]));

        // Boost chain
        if (Raised(MapRole.BoostTarget) && mod.ByRole(MapRole.BoostLimiter) is { } bl && mod.ByRole(MapRole.BoostTarget) is { } bt && bl.Max < bt.Max)
            f.Add(Make("BOOST_LIMITER_CAPS", $"Boost Target max {bt.Max:0} mbar exceeds Boost Limiter max {bl.Max:0} mbar: target is capped", Severity.Review, 0.8,
                [Ev(MapRole.BoostTarget), Ev(MapRole.BoostLimiter)], Ids(MapRole.BoostTarget, MapRole.BoostLimiter)));
        if (mod.ByRole(MapRole.Svbl) is { } svbl && mod.ByRole(MapRole.BoostTarget) is { } bt2)
        {
            var sv = svbl.Values[0];
            if (sv < bt2.Max)
                f.Add(Make("SVBL_BELOW_TARGET", $"SVBL {sv:0} mbar is below Boost Target max {bt2.Max:0} mbar: overboost cut (limp mode) likely", Severity.Warning, 0.8,
                    [Ev(MapRole.Svbl), Ev(MapRole.BoostTarget)], Ids(MapRole.Svbl, MapRole.BoostTarget), ["turbo"]));
            else if (sv - bt2.Max > 400 || svbl.Raw[0] >= svbl.Definition.DataType.MaxRaw() * 0.98)
                f.Add(Make("SVBL_PROTECTION_WEAKENED", $"SVBL {sv:0} mbar is {sv - bt2.Max:0} mbar above max Boost Target: overboost protection effectively removed", Severity.Warning, 0.75,
                    [Ev(MapRole.Svbl), Ev(MapRole.BoostTarget)], Ids(MapRole.Svbl, MapRole.BoostTarget), ["turbo"]));
        }
        if (D(MapRole.BoostLimiter) is { IsModified: true } bld && mod.ByRole(MapRole.BoostLimiter) is { } blm && stock.ByRole(MapRole.BoostLimiter) is { } bls && blm.Rows > 1)
        {
            // altitude protection: lowest atmospheric-pressure row raised more than sea-level row
            double RowMax(CalibrationMap m, int r) => Enumerable.Range(0, m.Cols).Max(c => m[r, c]);
            var lowRowInc = RowMax(blm, 0) / RowMax(bls, 0) - 1;
            var highRowInc = RowMax(blm, blm.Rows - 1) / RowMax(bls, bls.Rows - 1) - 1;
            if (lowRowInc > highRowInc + 0.03)
                f.Add(Make("ALTITUDE_PROTECTION_REDUCED", $"Boost Limiter raised more at low atmospheric pressure ({lowRowInc:P0}) than at sea level ({highRowInc:P0}): turbo pressure ratio at altitude increases", Severity.Warning, 0.7,
                    [new Evidence(EvidenceType.Diff, $"diff:{bld.MapId}", $"row p_atm={blm.YAxis[0]:0} mbar +{lowRowInc:P0}")], [bld.MapId], ["turbo"]));
        }

        // Injection
        if (fuelUp > 8 && !Raised(MapRole.Soi) && mod.ByRole(MapRole.Soi) is not null)
            f.Add(Make("SOI_UNCHANGED", $"Fuel request raised ~{fuelUp:0}% with SOI unchanged: longer injection ends later in the expansion stroke, raising EGT", Severity.Review, 0.65,
                [Ev(MapRole.Soi), Ev(MapRole.TorqueToIq)], Ids(MapRole.Soi, MapRole.TorqueToIq), ["thermal"]));
        if (mod.ByRole(MapRole.Duration) is { } dur)
        {
            // Highest IQ the torque path can request: Torque→IQ evaluated at the torque limiter (sea level).
            double maxIq = 0;
            if (mod.ByRole(MapRole.TorqueToIq) is { } conv && mod.ByRole(MapRole.TorqueLimiter) is { } lim)
                foreach (var rpm in lim.XAxis) maxIq = Math.Max(maxIq, conv.Lookup(rpm, lim.Lookup(rpm, 1013)));
            if (maxIq > dur.YAxis[^1] * 1.01)
                f.Add(Make("DURATION_AXIS_TOO_SHORT", $"Requested IQ up to {maxIq:0.#} mg but Duration map IQ axis ends at {dur.YAxis[^1]:0.#} mg: duration is clamped, actual fuel will not follow the request", Severity.Warning, 0.75,
                    [new Evidence(EvidenceType.Map, $"map:{dur.Id}", $"IQ axis top {dur.YAxis[^1]:0.#} mg")], Ids(MapRole.Duration, MapRole.TorqueToIq, MapRole.SmokeLimiter), ["injectors"]));
        }
        if (Raised(MapRole.Soi) && Inc(MapRole.Soi) > 15)
            f.Add(Make("SOI_ADVANCED_STRONGLY", $"SOI advanced up to {Inc(MapRole.Soi):0}%: higher peak cylinder pressure and mechanical load", Severity.Review, 0.6,
                [Ev(MapRole.Soi)], Ids(MapRole.Soi), ["engine"]));

        // Protections
        foreach (var role in new[] { MapRole.EgtProtection, MapRole.TemperatureProtection, MapRole.GearboxTorqueMonitor })
            if (Raised(role))
                f.Add(Make("PROTECTION_RELAXED", $"{role.DisplayName()} relaxed ({Inc(role):+0;-0}%)", Severity.Warning, 0.7, [Ev(role)], Ids(role), AnomalyDetector.AffectedBy(role)));

        if (!hasCommonRail)
            f.Add(new Finding
            {
                Code = "RAIL_NOT_APPLICABLE", Text = "Unit-injector (PD) engine: rail pressure checks are not applicable; injection pressure is cam-driven",
                Severity = Severity.Safe, Confidence = 1, Evidence = [new Evidence(EvidenceType.Rule, "plugin:injection_system", "plugin reports no common rail")],
            });

        return f;

        static Finding Make(string code, string text, Severity sev, double conf, IReadOnlyList<Evidence> ev, string[] maps, string[]? comps = null) => new()
        {
            Code = code, Text = text, Severity = sev, Confidence = conf, Evidence = ev, RelatedMaps = maps,
            AffectedComponents = comps ?? [], Source = FindingSource.Rule, SourceDetail = "Stage1ConsistencyAnalyzer",
        };
    }
}
