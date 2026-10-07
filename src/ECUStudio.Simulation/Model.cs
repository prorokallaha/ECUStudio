using System.Text.Json.Serialization;
using ECUStudio.Calibration.Model;
using ECUStudio.Components;
using ECUStudio.Core;

namespace ECUStudio.Simulation;

[JsonConverter(typeof(JsonStringEnumConverter<CoolantState>))]
public enum CoolantState { Cold, Normal, Hot }

public sealed record EnvironmentConditions
{
    public double AmbientTempC { get; init; } = 20;
    public double AltitudeM { get; init; }
    /// <summary>When null, derived from altitude with the standard barometric formula.</summary>
    public double? AtmosphericPressureMbar { get; init; }
    public CoolantState Coolant { get; init; } = CoolantState.Normal;

    public double PressureMbar => AtmosphericPressureMbar ?? Physics.BarometricPressureMbar(AltitudeM);

    public static EnvironmentConditions Reference { get; } = new();
}

public readonly record struct OperatingPoint(double Rpm, double PedalPct, int Gear = 4);

/// <summary>
/// Engineering assumptions of the physical models. Every value is an assumption with an
/// explicit uncertainty and is listed in reports; diagnostic logs can later replace them.
/// </summary>
public sealed record ModelAssumptions
{
    public double VolumetricEfficiency { get; init; } = 0.88;
    public double VolumetricEfficiencyUncertainty { get; init; } = 0.05;
    public double CompressorEfficiency { get; init; } = 0.70;
    public double IntercoolerEffectiveness { get; init; } = 0.60;
    public double PeakBrakeEfficiency { get; init; } = 0.40;
    public double BrakeEfficiencyUncertainty { get; init; } = 0.03;
    public double FuelLhvMjPerKg { get; init; } = 42.6;
    public double StoichiometricAfr { get; init; } = 14.5;
    public double ExhaustEnergyFraction { get; init; } = 0.33;
    public double ExhaustCp { get; init; } = 1150;
    public double TypicalFullLoadLambda { get; init; } = 1.25;
    public double TypicalFullLoadLambdaUncertainty { get; init; } = 0.10;
    public double AirFilterDropMbar { get; init; } = 30;
    public double IntercoolerDropMbar { get; init; } = 50;
    public double IntakeHeatingK { get; init; } = 5;
    /// <summary>RPM below which boost targets are assumed not fully reachable (turbo spool).</summary>
    public double SpoolCompleteRpm { get; init; } = 1700;

    public static ModelAssumptions Default { get; } = new();

    public IReadOnlyList<string> Describe() =>
    [
        $"Volumetric efficiency {VolumetricEfficiency:0.00} ± {VolumetricEfficiencyUncertainty:0.00} (no measured VE)",
        $"Compressor isentropic efficiency {CompressorEfficiency:0.00} (no compressor map)",
        $"Intercooler effectiveness {IntercoolerEffectiveness:0.00}",
        $"Peak brake efficiency {PeakBrakeEfficiency:0.00} ± {BrakeEfficiencyUncertainty:0.00}",
        $"Diesel LHV {FuelLhvMjPerKg} MJ/kg, stoichiometric AFR {StoichiometricAfr}",
        $"Exhaust energy fraction {ExhaustEnergyFraction:0.00} (EGT model)",
        $"Airflow model assumes full-load lambda {TypicalFullLoadLambda:0.00} ± {TypicalFullLoadLambdaUncertainty:0.00}",
        $"Boost target assumed reached above {SpoolCompleteRpm:0} rpm; linear spool below",
        "Power/torque are flywheel estimates, not wheel or measured values",
    ];
}

public sealed record SimulationInput
{
    public required CalibrationSet Calibration { get; init; }
    /// <summary>Stock calibration of the same software, used by models A/D as the OEM reference.</summary>
    public CalibrationSet? StockReference { get; init; }
    public required HardwareProfile Hardware { get; init; }
    public ModelAssumptions Assumptions { get; init; } = ModelAssumptions.Default;
    /// <summary>Upper bound on any confidence, from data availability (BIN only → low).</summary>
    public double ConfidenceCap { get; init; } = 1.0;
    /// <summary>When the injection system has no common rail, rail pressure is NOT_APPLICABLE.</summary>
    public bool HasCommonRail { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<Limiter>))]
public enum Limiter { None, DriverWish, TorqueLimiter, GearLimiter, ConversionAxisEnd, SmokeLimiter, BoostTarget, BoostLimiter, Svbl, Spool }

public sealed record ModelEstimate(string Model, string Description, double? TorqueNm, double Low, double High, double Confidence, bool Available, string? Note);

public sealed record TraceStep(string Quantity, string Formula, double? Value, string Unit, string? MapId = null, string? Note = null);

public sealed record PointResult
{
    public required OperatingPoint Point { get; init; }
    public required EnvironmentConditions Environment { get; init; }
    public Estimate RequestedTorque { get; init; } = Estimate.Unknown("Nm");
    public Estimate PermittedTorque { get; init; } = Estimate.Unknown("Nm");
    public Limiter TorqueLimiter { get; init; }
    public Limiter FuelLimiter { get; init; }
    public Limiter BoostLimiter { get; init; }
    public Estimate IqRequested { get; init; } = Estimate.Unknown("mg/stroke");
    public Estimate Iq { get; init; } = Estimate.Unknown("mg/stroke");
    public Estimate BoostTarget { get; init; } = Estimate.Unknown("mbar");
    public Estimate Map { get; init; } = Estimate.Unknown("mbar");
    public Estimate PressureRatio { get; init; } = Estimate.Unknown("-");
    public Estimate CompressorOutletTemp { get; init; } = Estimate.Unknown("°C");
    public Estimate IntakeManifoldTemp { get; init; } = Estimate.Unknown("°C");
    public Estimate AirMass { get; init; } = Estimate.Unknown("mg/stroke");
    public Estimate AirFlow { get; init; } = Estimate.Unknown("kg/s");
    public Estimate CorrectedAirFlow { get; init; } = Estimate.Unknown("kg/s");
    public Estimate Lambda { get; init; } = Estimate.Unknown("-");
    public Estimate Soi { get; init; } = Estimate.Unknown("°BTDC");
    public Estimate Duration { get; init; } = Estimate.Unknown("°CA");
    public Estimate RailPressure { get; init; } = Estimate.Unknown("bar");
    public Estimate Torque { get; init; } = Estimate.Unknown("Nm");
    public Estimate PowerKw { get; init; } = Estimate.Unknown("kW");
    public Estimate PowerHp { get; init; } = Estimate.Unknown("hp");
    public Estimate Egt { get; init; } = Estimate.Unknown("°C");
    public IReadOnlyList<ModelEstimate> Models { get; init; } = [];
    /// <summary>True when RPM is beyond the last breakpoint of the torque maps: ECU behaviour there (rpm limiter) is not modelled.</summary>
    public bool BeyondCalibratedRange { get; init; }
    public IReadOnlyList<TraceStep>? Trace { get; init; }
}
