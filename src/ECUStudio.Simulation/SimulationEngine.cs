using ECUStudio.Calibration.Model;
using ECUStudio.Components;
using ECUStudio.Core;

namespace ECUStudio.Simulation;

public interface ISimulationEngine
{
    PointResult Evaluate(SimulationInput input, OperatingPoint point, EnvironmentConditions env, bool trace = false);
}

/// <summary>
/// Steady-state engine model for torque-oriented diesel ECUs. Walks the ECU torque path
/// (driver wish → limiters → torque→IQ → smoke limiter) and the boost path, then estimates
/// torque with independent models (A torque-structure, B fuel-energy, C airflow, D empirical)
/// combined by a confidence-weighted ensemble whose interval widens with disagreement.
/// Thread-safe and stateless: points can be evaluated in parallel.
/// </summary>
public sealed class SimulationEngine : ISimulationEngine
{
    public PointResult Evaluate(SimulationInput input, OperatingPoint point, EnvironmentConditions env, bool trace = false)
    {
        var cal = input.Calibration;
        var a = input.Assumptions;
        var hw = input.Hardware;
        var cap = input.ConfidenceCap;
        var steps = trace ? new List<TraceStep>() : null;
        var rpm = point.Rpm;
        var pAtm = env.PressureMbar;

        var displacementCc = hw.Engine.Number(P.DisplacementCc);
        var cylinders = (int)(hw.Engine.Number(P.Cylinders) ?? 0);
        var engineKnown = displacementCc is > 0 && cylinders > 0;
        var vCyl = engineKnown ? displacementCc!.Value * 1e-6 / cylinders : 0;

        // ---- torque path -------------------------------------------------------------
        var dw = cal.ByRole(MapRole.DriverWish);
        var tl = cal.ByRole(MapRole.TorqueLimiter);
        var gtl = cal.ByRole(MapRole.GearTorqueLimiter);
        var tq2iq = cal.ByRole(MapRole.TorqueToIq);
        var smoke = cal.ByRole(MapRole.SmokeLimiter);

        double? requested = dw?.Lookup(rpm, point.PedalPct);
        steps?.Add(new TraceStep("Requested torque", "DriverWish(rpm, pedal)", requested, "Nm", dw?.Id, dw is null ? "Driver Wish not identified" : null));
        double? permitted = requested;
        var torqueLimiter = Limiter.DriverWish;
        if (tl is not null)
        {
            var limit = tl.Lookup(rpm, pAtm);
            steps?.Add(new TraceStep("Torque limit", "TorqueLimiter(rpm, p_atm)", limit, "Nm", tl.Id));
            if (permitted is null || limit < permitted) { permitted = limit; torqueLimiter = Limiter.TorqueLimiter; }
        }
        if (gtl is not null)
        {
            var limit = gtl.Lookup(rpm, point.Gear);
            steps?.Add(new TraceStep("Gear torque limit", "GearTorqueLimiter(rpm, gear)", limit, "Nm", gtl.Id));
            if (permitted is null || limit < permitted) { permitted = limit; torqueLimiter = Limiter.GearLimiter; }
        }

        double? iqReq = null;
        if (permitted is { } pt && tq2iq is not null)
        {
            if (pt > tq2iq.YAxis[^1]) torqueLimiter = Limiter.ConversionAxisEnd;
            iqReq = tq2iq.Lookup(rpm, pt);
            steps?.Add(new TraceStep("Requested IQ", "TorqueToIq(rpm, permitted torque)", iqReq, "mg/stroke", tq2iq.Id));
        }

        // ---- air path ----------------------------------------------------------------
        var bt = cal.ByRole(MapRole.BoostTarget);
        var bl = cal.ByRole(MapRole.BoostLimiter);
        var svbl = cal.ByRole(MapRole.Svbl);
        double? boostTarget = null, map = null;
        var boostLimiter = Limiter.None;
        if (bt is not null && iqReq is { } iqForBoost)
        {
            boostTarget = bt.Lookup(rpm, iqForBoost);
            map = boostTarget;
            boostLimiter = Limiter.BoostTarget;
            steps?.Add(new TraceStep("Boost target", "BoostTarget(rpm, IQ)", boostTarget, "mbar", bt.Id));
            if (bl is not null)
            {
                var lim = bl.Lookup(rpm, pAtm);
                steps?.Add(new TraceStep("Boost limit", "BoostLimiter(rpm, p_atm)", lim, "mbar", bl.Id));
                if (lim < map) { map = lim; boostLimiter = Limiter.BoostLimiter; }
            }
            if (svbl is not null && svbl.Values[0] < map) { map = svbl.Values[0]; boostLimiter = Limiter.Svbl; }
            map = Math.Max(pAtm, map!.Value);
            if (rpm < a.SpoolCompleteRpm)
            {
                var spool = Math.Clamp((rpm - 900) / (a.SpoolCompleteRpm - 900), 0, 1);
                var reachable = pAtm + (map.Value - pAtm) * spool;
                if (reachable < map - 1) { map = reachable; boostLimiter = Limiter.Spool; }
                steps?.Add(new TraceStep("Reachable MAP", "p_atm + (target − p_atm) · spool(rpm)", map, "mbar", null, "Spool assumption below full-boost rpm"));
            }
        }

        double? pr = null, t2K = null, timK = null, airMass = null, airFlow = null, corrFlow = null;
        var t1K = Physics.CToK(env.AmbientTempC + a.IntakeHeatingK);
        var p1 = pAtm - a.AirFilterDropMbar;
        if (map is { } mapV && engineKnown)
        {
            pr = (mapV + a.IntercoolerDropMbar) / p1;
            t2K = Physics.CompressorOutletK(t1K, pr.Value, a.CompressorEfficiency);
            var ambK = Physics.CToK(env.AmbientTempC);
            timK = t2K - a.IntercoolerEffectiveness * (t2K - ambK);
            var ve = VolumetricEfficiency(a.VolumetricEfficiency, rpm);
            airMass = Physics.AirMassPerStrokeMg(ve, vCyl, Physics.AirDensity(mapV, timK.Value));
            airFlow = Physics.MassFlowKgS(airMass.Value, cylinders, rpm);
            corrFlow = Physics.CorrectedFlow(airFlow.Value, t1K, p1);
            steps?.Add(new TraceStep("Pressure ratio", "(MAP + Δp_ic) / (p_atm − Δp_filter)", pr, "-"));
            steps?.Add(new TraceStep("Compressor outlet T", "T1·(1 + (PR^0.286 − 1)/η_c)", t2K - 273.15, "°C"));
            steps?.Add(new TraceStep("Manifold T", "T2 − ε_ic·(T2 − T_amb)", timK - 273.15, "°C"));
            steps?.Add(new TraceStep("Air mass", "VE · V_cyl · MAP/(R·T_im)", airMass, "mg/stroke", null, $"VE {ve:0.00} (assumed)"));
        }

        // ---- fuel --------------------------------------------------------------------
        double? iq = iqReq;
        var fuelLimiter = Limiter.None;
        if (smoke is not null && airMass is { } am && iqReq is not null)
        {
            var iqSmoke = smoke.Lookup(rpm, am);
            steps?.Add(new TraceStep("Smoke limit", "SmokeLimiter(rpm, air mass)", iqSmoke, "mg/stroke", smoke.Id));
            if (iqSmoke < iq) { iq = iqSmoke; fuelLimiter = Limiter.SmokeLimiter; }
        }
        if (iq is { } iqv && iqv < 0) iq = 0;
        double? lambda = iq is > 0.5 && airMass is { } am2 ? am2 / (iq.Value * a.StoichiometricAfr) : null;
        if (lambda is not null) steps?.Add(new TraceStep("Lambda", "air mass / (IQ · AFR_st)", lambda, "-"));

        var soiMap = cal.ByRole(MapRole.Soi);
        var durMap = cal.ByRole(MapRole.Duration);
        double? soi = iq is not null ? soiMap?.Lookup(rpm, iq.Value) : null;
        double? duration = iq is not null ? durMap?.Lookup(rpm, iq.Value) : null;
        if (soi is not null) steps?.Add(new TraceStep("SOI", "Soi(rpm, IQ)", soi, soiMap!.Definition.Unit, soiMap.Id));
        if (duration is not null) steps?.Add(new TraceStep("Duration", "Duration(rpm, IQ)", duration, durMap!.Definition.Unit, durMap.Id));
        var soiRetard = 0.0;
        if (soi is not null && input.StockReference?.ByRole(MapRole.Soi) is { } stockSoi && iq is not null)
            soiRetard = Math.Max(0, stockSoi.Lookup(rpm, iq.Value) - soi.Value);

        // ---- torque models -----------------------------------------------------------
        var models = new List<ModelEstimate>(4);
        double effLambda = lambda ?? a.TypicalFullLoadLambda;
        var eta = Physics.BrakeEfficiency(a.PeakBrakeEfficiency, rpm, effLambda, soiRetard);

        // A: torque structure via the OEM (stock) torque→IQ characteristic.
        var refConv = input.StockReference?.ByRole(MapRole.TorqueToIq);
        var convIsStock = refConv is not null;
        refConv ??= tq2iq;
        if (refConv is not null && iq is not null)
        {
            var tA = InverseWithExtrapolation(refConv, rpm, iq.Value);
            var confA = (convIsStock ? 0.65 : 0.4) * refConv.Definition.Confidence;
            models.Add(new ModelEstimate("A", "Torque structure (OEM torque→IQ inverse)", tA, tA * 0.92, tA * 1.08, confA, true,
                convIsStock ? "Stock conversion map used as the OEM efficiency reference" : "Modified conversion map used: reflects the tuner's assumption, not OEM data"));
        }
        else models.Add(new ModelEstimate("A", "Torque structure", null, 0, 0, 0, false, "Torque→IQ map not identified"));

        // B: fuel energy.
        if (iq is not null && engineKnown)
        {
            var kw = Physics.FuelPowerKw(iq.Value, cylinders, rpm, a.FuelLhvMjPerKg, eta);
            var tB = Physics.TorqueFromPowerKw(kw, rpm);
            var rel = a.BrakeEfficiencyUncertainty / a.PeakBrakeEfficiency + 0.01;
            models.Add(new ModelEstimate("B", "Fuel energy (ṁf·LHV·ηb)", tB, tB * (1 - rel), tB * (1 + rel), 0.6, true, $"ηb {eta:0.000}"));
            steps?.Add(new TraceStep("Torque (model B)", "ṁf·LHV·ηb / ω", tB, "Nm", null, $"ηb {eta:0.000}"));
        }
        else models.Add(new ModelEstimate("B", "Fuel energy", null, 0, 0, 0, false, engineKnown ? "Injected quantity unknown" : "Engine displacement/cylinders unknown"));

        // C: airflow, only meaningful at (near) full load where the engine runs close to its smoke limit.
        var fullLoad = point.PedalPct >= 90;
        if (airMass is { } amC && engineKnown && fullLoad)
        {
            var lam = a.TypicalFullLoadLambda;
            var iqAir = amC / (lam * a.StoichiometricAfr);
            var etaC = Physics.BrakeEfficiency(a.PeakBrakeEfficiency, rpm, lam, soiRetard);
            var tC = Physics.TorqueFromPowerKw(Physics.FuelPowerKw(iqAir, cylinders, rpm, a.FuelLhvMjPerKg, etaC), rpm);
            var lamRel = a.TypicalFullLoadLambdaUncertainty / lam;
            var veRel = a.VolumetricEfficiencyUncertainty / a.VolumetricEfficiency;
            var rel = Math.Sqrt(lamRel * lamRel + veRel * veRel + 0.0064);
            models.Add(new ModelEstimate("C", "Airflow (air mass at typical full-load λ)", tC, tC * (1 - rel), tC * (1 + rel), 0.45, true, $"λ assumed {lam:0.00}"));
        }
        else models.Add(new ModelEstimate("C", "Airflow", null, 0, 0, 0, false, !fullLoad ? "Only evaluated at full load" : "Air mass unknown"));

        // D: known-engine empirical (OEM rated torque per mg of stock fuel at the rated-torque speed).
        var ratedTorque = hw.Engine.Number(P.RatedTorqueNm);
        var ratedRpm = hw.Engine.Number(P.RatedTorqueRpm);
        if (ratedTorque is { } rt && ratedRpm is { } rr && input.StockReference is { } stockCal && iq is not null && engineKnown)
        {
            var stockIq = StockFullLoadIq(stockCal, rr, pAtm);
            if (stockIq is > 1)
            {
                var k = rt / stockIq.Value;
                var etaRef = Physics.BrakeEfficiency(a.PeakBrakeEfficiency, rr, a.TypicalFullLoadLambda, 0);
                var tD = k * iq.Value * (eta / etaRef);
                var conf = 0.55 * hw.Engine.Get(P.RatedTorqueNm).Confidence;
                models.Add(new ModelEstimate("D", "Known-engine empirical (OEM Nm/mg)", tD, tD * 0.93, tD * 1.07, conf, true, $"{k:0.00} Nm/mg from {rt:0} Nm @ {rr:0} rpm"));
            }
            else models.Add(new ModelEstimate("D", "Known-engine empirical", null, 0, 0, 0, false, "Stock full-load IQ not computable"));
        }
        else models.Add(new ModelEstimate("D", "Known-engine empirical", null, 0, 0, 0, false, input.StockReference is null ? "Needs stock file" : "Rated torque unknown"));

        var axisEnd = Math.Max(tl?.XAxis[^1] ?? 0, dw?.XAxis[^1] ?? 0);
        var beyond = axisEnd > 0 && rpm > axisEnd + 1;
        var torque = Ensemble(models, beyond ? Math.Min(cap, 0.2) : cap);
        if (beyond) torque = torque with { Note = $"RPM beyond last torque-map breakpoint ({axisEnd:0}); rpm limiter not modelled" };
        var powerKw = torque.IsKnown
            ? Estimate.Of(Physics.PowerKwFromTorque(torque.Value!.Value, rpm), Physics.PowerKwFromTorque(torque.Low!.Value, rpm), Physics.PowerKwFromTorque(torque.High!.Value, rpm), "kW", torque.Confidence)
            : Estimate.Unknown("kW");
        steps?.Add(new TraceStep("Torque (ensemble)", "Σ wᵢ·Tᵢ / Σ wᵢ, interval widened by model spread", torque.Value, "Nm"));

        // ---- thermal -------------------------------------------------------------------
        var egt = Estimate.Unknown("°C", "Needs air mass and IQ");
        if (lambda is { } lam2 && timK is { } tim && iq is > 0.5)
        {
            var fraction = a.ExhaustEnergyFraction + 0.004 * soiRetard;
            var e = Physics.EgtC(tim, lam2, a.StoichiometricAfr, a.FuelLhvMjPerKg, fraction, a.ExhaustCp);
            egt = Estimate.Around(e, 0.12, "°C", Math.Min(0.35, cap));
            steps?.Add(new TraceStep("EGT (pre-turbine)", "T_im + f_exh·LHV / (cp·(1 + λ·AFR_st))", e, "°C"));
        }

        var rail = input.HasCommonRail
            ? cal.ByRole(MapRole.RailPressure) is { } rp && iq is not null ? Estimate.Around(rp.Lookup(rpm, iq.Value), 0, "bar", Math.Min(cap, rp.Definition.Confidence), ValueKind.EcuRequested) : Estimate.Unknown("bar")
            : Estimate.NotApplicable("bar", "Unit-injector engine: no common rail");

        double MapConf(CalibrationMap? m) => Math.Min(cap, m?.Definition.Confidence ?? 0);
        Estimate Requested(double? v, string unit, CalibrationMap? m) => v is { } x ? Estimate.Exact(x, unit, MapConf(m), ValueKind.EcuRequested) : Estimate.Unknown(unit);

        return new PointResult
        {
            Point = point,
            Environment = env,
            RequestedTorque = Requested(requested, "Nm", dw),
            PermittedTorque = Requested(permitted, "Nm", tl ?? dw),
            TorqueLimiter = torqueLimiter,
            FuelLimiter = fuelLimiter,
            BoostLimiter = boostLimiter,
            IqRequested = Requested(iqReq, "mg/stroke", tq2iq),
            Iq = Requested(iq, "mg/stroke", fuelLimiter == Limiter.SmokeLimiter ? smoke : tq2iq),
            BoostTarget = Requested(boostTarget, "mbar", bt),
            Map = map is { } m2 ? Estimate.Around(m2, 0.03, "mbar", Math.Min(MapConf(bt), rpm < a.SpoolCompleteRpm ? 0.35 : 0.6), ValueKind.Estimated, "Assumes the turbo reaches the requested boost") : Estimate.Unknown("mbar"),
            PressureRatio = pr is { } prv ? Estimate.Around(prv, 0.04, "-", Math.Min(cap, 0.55)) : Estimate.Unknown("-"),
            CompressorOutletTemp = t2K is { } t2 ? Estimate.Around(t2 - 273.15, 0.08, "°C", Math.Min(cap, 0.45)) : Estimate.Unknown("°C"),
            IntakeManifoldTemp = timK is { } ti ? Estimate.Around(ti - 273.15, 0.1, "°C", Math.Min(cap, 0.45)) : Estimate.Unknown("°C"),
            AirMass = airMass is { } amv ? Estimate.Around(amv, 0.08, "mg/stroke", Math.Min(cap, 0.5)) : Estimate.Unknown("mg/stroke"),
            AirFlow = airFlow is { } af ? Estimate.Around(af, 0.08, "kg/s", Math.Min(cap, 0.5)) : Estimate.Unknown("kg/s"),
            CorrectedAirFlow = corrFlow is { } cf ? Estimate.Around(cf, 0.08, "kg/s", Math.Min(cap, 0.5)) : Estimate.Unknown("kg/s"),
            Lambda = lambda is { } l ? Estimate.Around(l, 0.1, "-", Math.Min(cap, 0.45)) : Estimate.Unknown("-"),
            Soi = Requested(soi, soiMap?.Definition.Unit ?? "°BTDC", soiMap),
            Duration = Requested(duration, durMap?.Definition.Unit ?? "°CA", durMap),
            RailPressure = rail,
            Torque = torque,
            PowerKw = powerKw,
            PowerHp = powerKw.Scale(Physics.KwToHp, "hp"),
            Egt = egt,
            Models = models,
            BeyondCalibratedRange = beyond,
            Trace = steps,
        };
    }

    /// <summary>
    /// Confidence-weighted ensemble. Interval half-width combines the models' own uncertainty with
    /// their disagreement; confidence drops as relative spread grows.
    /// </summary>
    public static Estimate Ensemble(IReadOnlyList<ModelEstimate> models, double cap)
    {
        double wSum = 0, center = 0, halfInternal = 0, min = double.MaxValue, max = double.MinValue;
        var used = 0;
        foreach (var m in models)
        {
            if (!m.Available || m.TorqueNm is not { } t || m.Confidence <= 0) continue;
            wSum += m.Confidence;
            center += m.Confidence * t;
            halfInternal += m.Confidence * (m.High - m.Low) / 2;
            min = Math.Min(min, t);
            max = Math.Max(max, t);
            used++;
        }
        if (used == 0 || wSum <= 0) return Estimate.Unknown("Nm", "No torque model had enough data");
        center /= wSum;
        halfInternal /= wSum;
        var spread = max - min;
        var half = Math.Sqrt(halfInternal * halfInternal + spread / 2 * (spread / 2));
        var relSpread = center > 1 ? spread / center : 1;
        var conf = wSum / used * (1 - Math.Min(0.6, relSpread * 1.5));
        if (used == 1) conf *= 0.75;
        conf = Math.Min(conf, cap);
        return Estimate.Of(center, center - half, center + half, "Nm", Math.Max(0.05, conf), ValueKind.Estimated, $"{used} model(s), spread {spread:0} Nm");
    }

    private static double VolumetricEfficiency(double ve, double rpm) => rpm switch
    {
        < 1500 => ve * 0.97,
        <= 3500 => ve,
        _ => ve * (1 - 0.06 * Math.Min(1, (rpm - 3500) / 1500)),
    };

    private static double InverseWithExtrapolation(CalibrationMap conv, double rpm, double iq)
    {
        var top = conv.YAxis[^1];
        var iqTop = conv.Lookup(rpm, top);
        if (iq <= iqTop || conv.Rows < 2) return Interpolation.InverseY(conv, rpm, iq);
        var prev = conv.YAxis[^2];
        var iqPrev = conv.Lookup(rpm, prev);
        var slope = (top - prev) / Math.Max(1e-6, iqTop - iqPrev); // Nm per mg
        return top + (iq - iqTop) * slope;
    }

    /// <summary>Full-load IQ of the stock calibration at the given rpm (torque path only, smoke limiter ignored).</summary>
    private static double? StockFullLoadIq(CalibrationSet stock, double rpm, double pAtm)
    {
        var dw = stock.ByRole(MapRole.DriverWish);
        var tl = stock.ByRole(MapRole.TorqueLimiter);
        var conv = stock.ByRole(MapRole.TorqueToIq);
        if (conv is null || (dw is null && tl is null)) return null;
        var t = Math.Min(dw?.Lookup(rpm, 100) ?? double.MaxValue, tl?.Lookup(rpm, pAtm) ?? double.MaxValue);
        return conv.Lookup(rpm, t);
    }
}
