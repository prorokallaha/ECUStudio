using ECUStudio.Core;

namespace ECUStudio.Simulation;

public sealed record DynoRequest
{
    public int Gear { get; init; } = 4;
    public double RpmStart { get; init; } = 1000;
    public double RpmEnd { get; init; } = 4800;
    public double RpmStep { get; init; } = 100;
    public double ThrottlePct { get; init; } = 100;
    public double AmbientTempC { get; init; } = 20;
    public double? AtmosphericPressureMbar { get; init; }
    public double AltitudeM { get; init; }

    public void Validate()
    {
        if (RpmStart < 500 || RpmEnd > 7000 || RpmEnd <= RpmStart) throw new EcuStudioException("INVALID_DYNO_REQUEST", "RPM range must be within 500–7000 and end > start");
        if (RpmStep < 25 || (RpmEnd - RpmStart) / RpmStep > 400) throw new EcuStudioException("INVALID_DYNO_REQUEST", "RPM step too small (max 400 points)");
        if (ThrottlePct is < 0 or > 100) throw new EcuStudioException("INVALID_DYNO_REQUEST", "Throttle must be 0–100 %");
        if (Gear is < 1 or > 8) throw new EcuStudioException("INVALID_DYNO_REQUEST", "Gear must be 1–8");
        if (AmbientTempC is < -40 or > 60) throw new EcuStudioException("INVALID_DYNO_REQUEST", "Ambient temperature must be −40…+60 °C");
    }

    public EnvironmentConditions Environment => new() { AmbientTempC = AmbientTempC, AltitudeM = AltitudeM, AtmosphericPressureMbar = AtmosphericPressureMbar };
}

public sealed record DynoCurve(string Label, IReadOnlyList<PointResult> Points, Estimate PeakPowerHp, double PeakPowerRpm, Estimate PeakTorque, double PeakTorqueRpm);

public sealed record DynoResult(DynoRequest Request, DynoCurve? Stock, DynoCurve Modified, IReadOnlyList<string> Assumptions)
{
    public string Disclaimer => "Virtual dyno: steady-state engineering estimate at the flywheel, not a measurement.";
}

public sealed class VirtualDyno(ISimulationEngine engine)
{
    public DynoResult Run(DynoRequest request, SimulationInput modified, SimulationInput? stock)
    {
        request.Validate();
        var env = request.Environment;
        var rpms = new List<double>();
        for (var r = request.RpmStart; r <= request.RpmEnd + 1e-6; r += request.RpmStep) rpms.Add(r);

        DynoCurve Sweep(string label, SimulationInput input)
        {
            var points = new PointResult[rpms.Count];
            Parallel.For(0, rpms.Count, i => points[i] = engine.Evaluate(input, new OperatingPoint(rpms[i], request.ThrottlePct, request.Gear), env, trace: true));
            var known = points.Where(p => p.PowerHp.IsKnown && !p.BeyondCalibratedRange).ToList();
            var peakP = known.MaxBy(p => p.PowerHp.Value!.Value);
            var peakT = known.MaxBy(p => p.Torque.Value!.Value);
            return new DynoCurve(label, points,
                peakP?.PowerHp ?? Estimate.Unknown("hp"), peakP?.Point.Rpm ?? 0,
                peakT?.Torque ?? Estimate.Unknown("Nm"), peakT?.Point.Rpm ?? 0);
        }

        return new DynoResult(request, stock is null ? null : Sweep("Stock", stock), Sweep("Modified", modified), modified.Assumptions.Describe());
    }
}
