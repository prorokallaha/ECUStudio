using System.Globalization;
using System.Text;
using ECUStudio.Simulation;

namespace ECUStudio.Application.DevTools;

/// <summary>
/// Generates a SYNTHETIC VCDS-style full-load pull from the model itself, with sensor noise, turbo spool lag
/// and a small systematic sensor offset. Because it is derived from the model, agreement with the model proves
/// nothing about a real engine: it exists only to demonstrate the log workflow in the demo project and tests.
/// </summary>
public static class SyntheticLog
{
    public static string GenerateVcdsCsv(SimulationInput input, ISimulationEngine engine, int seed = 7, double boostOffsetPct = -2.5)
    {
        var rnd = new Random(seed);
        double Noise(double sd) => sd * (rnd.NextDouble() + rnd.NextDouble() + rnd.NextDouble() - 1.5) / 0.5;
        var ci = CultureInfo.InvariantCulture;
        var env = new EnvironmentConditions { AtmosphericPressureMbar = 990, AmbientTempC = 18 };

        var sb = new StringBuilder();
        sb.AppendLine("Wednesday,07,October,2026,12:00:00:00000,VCID:SYNTHETIC-ECUSTUDIO");
        sb.AppendLine("Address 01: Engine,Labels: none - SYNTHETIC log generated from the ECUStudio model (workflow demo only)");
        sb.AppendLine(",TIME,Engine Speed,Air Mass,Air Mass,Accelerator Position,Boost Pressure,Boost Pressure,Atmospheric Pressure,Injection Quantity,Intake Air Temp");
        sb.AppendLine("Marker,STAMP,,Specified,Actual,,Specified,Actual,,,");
        sb.AppendLine(",s,/min,mg/str,mg/str,%,mbar,mbar,mbar,mg/str,°C");

        var t = 0.0;
        void Row(double rpm, double pedal, double spool)
        {
            var p = engine.Evaluate(input, new OperatingPoint(rpm, pedal, 3), env);
            var map = (p.Map.Value ?? 1000) * spool * (1 + boostOffsetPct / 100) + Noise(12);
            var target = p.BoostTarget.Value ?? map;
            var maf = (p.AirMass.Value ?? 0) * (0.85 + 0.15 * spool) * 1.02 + Noise(8);
            var iq = (p.Iq.Value ?? 0) * 0.98 + Noise(0.6);
            sb.Append(CultureInfo.InvariantCulture, $",{t:0.00},{rpm + Noise(10):0},{(p.AirMass.Value ?? 0):0},{maf:0},{pedal:0},{target:0},{map:0},{990 + Noise(1.5):0},{iq:0.0},{24 + t * 0.15 + Noise(0.4):0}");
            sb.AppendLine();
            t += 0.25;
        }

        // Part load cruise (excluded from the full-load comparison), then a 3rd-gear pull to 4600 rpm.
        for (var i = 0; i < 12; i++) Row(1500 + i * 8, 22, 1);
        for (var rpm = 1250.0; rpm <= 4600; rpm += 45 + rpm / 60)
        {
            var spool = rpm < 1900 ? 0.82 + 0.18 * (rpm - 1250) / 650 : 1; // steady-state model has no turbo lag
            Row(rpm, 100, spool);
        }
        for (var i = 0; i < 6; i++) Row(4400 - i * 400, 0, 1);
        return sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
