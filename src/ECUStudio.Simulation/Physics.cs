namespace ECUStudio.Simulation;

/// <summary>Deterministic physical relations. No calibration knowledge here.</summary>
public static class Physics
{
    public const double RAir = 287.05;
    public const double Kappa = 1.4;
    public const double KwToHp = 1.35962; // metric horsepower (PS)

    public static double BarometricPressureMbar(double altitudeM) => 1013.25 * Math.Pow(1 - 2.25577e-5 * altitudeM, 5.25588);

    public static double CToK(double c) => c + 273.15;

    /// <summary>Compressor outlet temperature from inlet temperature, pressure ratio and isentropic efficiency.</summary>
    public static double CompressorOutletK(double inletK, double pressureRatio, double efficiency) =>
        inletK * (1 + (Math.Pow(Math.Max(1, pressureRatio), (Kappa - 1) / Kappa) - 1) / efficiency);

    public static double AirDensity(double pressureMbar, double tempK) => pressureMbar * 100 / (RAir * tempK);

    /// <summary>Air mass trapped per cylinder per cycle [mg].</summary>
    public static double AirMassPerStrokeMg(double ve, double cylinderVolumeM3, double densityKgM3) => ve * cylinderVolumeM3 * densityKgM3 * 1e6;

    /// <summary>Mass flow [kg/s] from per-stroke mass [mg], 4-stroke.</summary>
    public static double MassFlowKgS(double perStrokeMg, int cylinders, double rpm) => perStrokeMg * 1e-6 * cylinders * rpm / 120;

    public static double CorrectedFlow(double massFlowKgS, double inletK, double inletMbar) =>
        massFlowKgS * Math.Sqrt(inletK / 298.15) / (inletMbar / 1013.25);

    public static double TorqueFromPowerKw(double kw, double rpm) => rpm <= 0 ? 0 : kw * 1000 / (rpm * 2 * Math.PI / 60);

    public static double PowerKwFromTorque(double nm, double rpm) => nm * rpm * 2 * Math.PI / 60 / 1000;

    /// <summary>Brake power from fuel energy: P = ṁf · LHV · ηb.</summary>
    public static double FuelPowerKw(double iqMg, int cylinders, double rpm, double lhvMjKg, double brakeEfficiency) =>
        MassFlowKgS(iqMg, cylinders, rpm) * lhvMjKg * 1e6 * brakeEfficiency / 1000;

    /// <summary>
    /// Brake efficiency shape: peak in the mid-speed band, friction losses at high rpm,
    /// heat losses at low rpm, incomplete combustion when rich (λ &lt; 1.25).
    /// </summary>
    public static double BrakeEfficiency(double peak, double rpm, double lambda, double soiRetardDeg)
    {
        double shape = rpm switch
        {
            < 1000 => 0.88,
            < 1800 => 0.88 + 0.12 * (rpm - 1000) / 800,
            <= 2600 => 1.0,
            <= 4500 => 1.0 - 0.15 * (rpm - 2600) / 1900,
            _ => 0.85 - 0.05 * Math.Min(1, (rpm - 4500) / 1000),
        };
        var richPenalty = double.IsFinite(lambda) && lambda < 1.25 ? Math.Max(0.6, 1 - 0.35 * (1.25 - lambda)) : 1.0;
        var soiPenalty = Math.Max(0.85, 1 - 0.006 * Math.Max(0, soiRetardDeg));
        return peak * shape * richPenalty * soiPenalty;
    }

    /// <summary>Pre-turbine EGT estimate from an exhaust energy balance [°C].</summary>
    public static double EgtC(double manifoldTempK, double lambda, double afrStoich, double lhvMjKg, double exhaustFraction, double cp) =>
        manifoldTempK + exhaustFraction * lhvMjKg * 1e6 / (cp * (1 + lambda * afrStoich)) - 273.15;
}
