using System.Buffers.Binary;
using System.Text;
using ECUStudio.Binary;
using ECUStudio.Calibration.Definitions;
using ECUStudio.Calibration.Model;

namespace ECUStudio.Application.DevTools;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<SyntheticVariant>))]
public enum SyntheticVariant { Stock, Stage1, Stage1Aggressive }

/// <summary>
/// Generates SYNTHETIC EDC16U34-like images for tests, benchmarks and the demo project.
/// Map shapes are physically plausible for a 1.9 TDI PD 77 kW but are NOT a real calibration,
/// and the matching definition file is flagged <c>synthetic: true</c>.
/// </summary>
public static class SyntheticEdc16U34
{
    public const string SoftwareNumber = "1037399999";
    public const string HardwareNumber = "0281099999";
    public const string PartNumber = "03G906021ZZ";
    public const int Size = 0x80000;

    private static readonly double[] RpmDw = [800, 1000, 1250, 1500, 1750, 2000, 2250, 2500, 2750, 3000, 3500, 4000, 4500, 5000];
    private static readonly double[] Pedal = [0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
    private static readonly double[] RpmTl = [1000, 1250, 1500, 1750, 1900, 2000, 2250, 2500, 2750, 3000, 3250, 3500, 3750, 4000, 4400, 4800];
    private static readonly double[] StockTorque = [150, 185, 220, 245, 250, 250, 250, 245, 235, 225, 212, 200, 192, 184, 160, 120];
    private static readonly double[] Atm = [700, 850, 1000, 1050];
    private static readonly double[] Rpm12 = [800, 1000, 1250, 1500, 1750, 2000, 2500, 3000, 3500, 4000, 4500, 5000];
    private static readonly double[] TorqueAxis = [0, 50, 100, 150, 200, 250, 300, 350];
    private static readonly double[] AirAxis = [150, 250, 350, 450, 550, 650, 750, 850, 950, 1050];
    private static readonly double[] IqAxis = [0, 10, 20, 30, 40, 45, 50, 55, 60];

    public sealed record Result(byte[] Image, DefinitionFile Definition);

    public static Result Generate(SyntheticVariant variant)
    {
        var img = new byte[Size];
        Array.Fill(img, (byte)0xFF);
        var rng = new Random(1234);
        // pseudo code area
        for (var i = 0; i < 0x3C000; i++) img[i] = (byte)rng.Next(256);
        WriteAscii(img, 0x40010, $"EDC16U34 SYNTHETIC TEST IMAGE {PartNumber} 1,9L R4 EDC G000SG {HardwareNumber} {SoftwareNumber}");

        var stage1 = variant != SyntheticVariant.Stock;
        var aggressive = variant == SyntheticVariant.Stage1Aggressive;
        var maps = new List<DefinitionFile.MapEntry>();
        var addr = 0x50000;

        double TlMul(double rpm) => !stage1 ? 1 : aggressive ? 1.20 : rpm < 1500 ? 1.08 : 1.24;

        // Driver wish [0.1 Nm]
        maps.Add(Write(img, ref addr, "driver_wish", "Driver Wish", MapRole.DriverWish, RpmDw, Pedal, 1, 0.01, "rpm", "%", AxisQuantity.EngineSpeed, AxisQuantity.PedalPosition, 0.1, "Nm",
            (rpm, p) => { var max = 320 * (rpm > 4000 ? 1 - (rpm - 4000) / 2500 : 1); var v = max * Math.Pow(p / 100, 1.15); return stage1 ? v * (aggressive ? 1.2 : 1.1) : v; }));
        // Torque limiter [0.1 Nm] by rpm × atmospheric pressure
        maps.Add(Write(img, ref addr, "torque_limiter", "Torque Limiter", MapRole.TorqueLimiter, RpmTl, Atm, 1, 1, "rpm", "mbar", AxisQuantity.EngineSpeed, AxisQuantity.AtmosphericPressure, 0.1, "Nm",
            (rpm, atm) => Interp(RpmTl, StockTorque, rpm) * (atm < 900 ? 0.85 + 0.15 * (atm - 700) / 200 : 1) * TlMul(rpm)));
        // Torque → IQ [0.01 mg]
        maps.Add(Write(img, ref addr, "torque_to_iq", "Torque to IQ Conversion", MapRole.TorqueToIq, Rpm12, TorqueAxis, 1, 0.1, "rpm", "Nm", AxisQuantity.EngineSpeed, AxisQuantity.Torque, 0.01, "mg/stroke",
            (rpm, t) => { var v = (t + 15 + rpm / 400) / (5.6 * EtaShape(rpm)); return aggressive ? v * 1.2 : v; }));
        // Smoke limiter [0.01 mg] by air mass
        maps.Add(Write(img, ref addr, "smoke_limiter", "Smoke Limiter", MapRole.SmokeLimiter, Rpm12, AirAxis, 1, 1, "rpm", "mg/stroke", AxisQuantity.EngineSpeed, AxisQuantity.AirMass, 0.01, "mg/stroke",
            (rpm, air) => air / (1.2 * 14.5) * (!stage1 ? 1 : aggressive ? 1.25 : 1.18)));
        // Boost target [mbar abs]
        maps.Add(Write(img, ref addr, "boost_target", "Boost Target", MapRole.BoostTarget, Rpm12, IqAxis, 1, 0.01, "rpm", "mg/stroke", AxisQuantity.EngineSpeed, AxisQuantity.InjectionQuantity, 1, "mbar",
            (rpm, iq) =>
            {
                var full = rpm < 1500 ? 1600 + (rpm - 800) / 700 * 450 : rpm <= 3000 ? 2150 : 2150 - (rpm - 3000) / 2000 * 300;
                if (stage1) full += aggressive ? full * 0.2 - 200 : rpm >= 1750 ? 180 : 80;
                var f = Math.Clamp(iq / 45, 0, 1.15);
                return 1000 + (full - 1000) * Math.Min(1, f);
            }));
        // Boost limiter [mbar abs] by atmospheric pressure
        maps.Add(Write(img, ref addr, "boost_limiter", "Boost Limiter", MapRole.BoostLimiter, Rpm12, Atm, 1, 1, "rpm", "mbar", AxisQuantity.EngineSpeed, AxisQuantity.AtmosphericPressure, 1, "mbar",
            (rpm, atm) =>
            {
                var sea = rpm < 1500 ? 1900 : 2250 - Math.Max(0, rpm - 3500) / 1500 * 200;
                var v = sea - (1000 - Math.Min(atm, 1000)) * (aggressive ? 0.2 : 1.1);
                return stage1 ? v + (aggressive ? 450 : 220) : v;
            }));
        // SOI [0.01 °BTDC]
        maps.Add(Write(img, ref addr, "soi_main", "Start of Injection", MapRole.Soi, Rpm12, IqAxis, 1, 0.01, "rpm", "mg/stroke", AxisQuantity.EngineSpeed, AxisQuantity.InjectionQuantity, 0.01, "°BTDC",
            (rpm, iq) => 3 + rpm / 4000 * 12 + iq / 60 * 4));
        // Duration [0.01 °CA]
        maps.Add(Write(img, ref addr, "duration_main", "Injection Duration", MapRole.Duration, Rpm12, IqAxis, 1, 0.01, "rpm", "mg/stroke", AxisQuantity.EngineSpeed, AxisQuantity.InjectionQuantity, 0.01, "°CA",
            (rpm, iq) => iq * 0.42 * (1 + rpm / 8000)));
        // VNT duty [0.01 %]
        maps.Add(Write(img, ref addr, "vnt_duty", "N75 / VNT Duty", MapRole.VntDuty, Rpm12, IqAxis, 1, 0.01, "rpm", "mg/stroke", AxisQuantity.EngineSpeed, AxisQuantity.InjectionQuantity, 0.01, "%",
            (rpm, iq) => 40 + Math.Min(40, iq * 0.7) - rpm / 500));

        // SVBL scalar (no Bosch header; only reachable via definitions)
        addr = (addr + 0x100) & ~0xFF;
        var svbl = !stage1 ? 2500 : aggressive ? 65535 : 2700;
        BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(addr), (ushort)svbl);
        maps.Add(new DefinitionFile.MapEntry { Id = "svbl", Name = "SVBL", Role = MapRole.Svbl, Address = addr, Rows = 1, Cols = 1, Unit = "mbar", Factor = 1, Confidence = 0.9 });
        addr += 0x100;

        // Two maps intentionally NOT in the definition → unknown-map workflow candidates.
        Write(img, ref addr, "unknown_a", "?", MapRole.Unknown, Rpm12, IqAxis, 1, 0.01, "rpm", "mg", AxisQuantity.EngineSpeed, AxisQuantity.InjectionQuantity, 1, "raw",
            (rpm, iq) => 600 + iq * 3 + rpm / 40);
        Write(img, ref addr, "unknown_b", "?", MapRole.Unknown, [1000, 2000, 3000, 4000, 5000], [700, 850, 1000], 1, 1, "rpm", "mbar", AxisQuantity.EngineSpeed, AxisQuantity.AtmosphericPressure, 1, "raw",
            (rpm, atm) => 4500 - rpm / 4 + atm);

        // Checksum blocks (SYNTHETIC layout, not the real EDC16U34 scheme). Stock and Stage 1 are corrected the way a
        // tuning tool would; the aggressive variant's code patch is applied afterwards, so its code CRC no longer matches.
        if (addr > CalEnd) throw new InvalidOperationException($"Synthetic maps overflow the calibration block (0x{addr:X})");
        foreach (var c in ChecksumBlocks) StoreChecksum(img, c);

        if (aggressive)
        {
            // simulated code patch
            img[0x12340] ^= 0xFF; img[0x12341] ^= 0x0F; img[0x2A000] = 0x60; img[0x2A001] = 0x00;
        }

        var def = new DefinitionFile
        {
            Plugin = "edc16u34",
            Title = "SYNTHETIC EDC16U34 test definition",
            SoftwareNumbers = [SoftwareNumber],
            Synthetic = true,
            Maps = maps.Where(m => m.Role != MapRole.Unknown).ToList(),
            Checksums = ChecksumBlocks,
        };
        return new Result(img, def);
    }

    private const int CalEnd = 0x70000;

    private static readonly DefinitionFile.ChecksumEntry[] ChecksumBlocks =
    [
        new() { Name = "Code CRC32", Start = 0x00000, End = 0x40000, Algorithm = ChecksumAlgorithm.Crc32, StoredAt = 0x7FFF0 },
        new() { Name = "Calibration ADD32", Start = 0x50000, End = CalEnd, Algorithm = ChecksumAlgorithm.Add32, StoredAt = 0x7FFF4 },
        new() { Name = "Calibration ADD32 complement", Start = 0x50000, End = CalEnd, Algorithm = ChecksumAlgorithm.Add32, StoredAt = 0x7FFF8, Complement = true },
    ];

    private static void StoreChecksum(byte[] img, DefinitionFile.ChecksumEntry c)
    {
        var spec = new DefinitionFile { Plugin = "edc16u34", Checksums = [c] }.ToChecksumSpecs()[0];
        var v = ChecksumVerifier.Compute(img, spec);
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(c.StoredAt), c.Complement ? ~v : v);
    }

    private static double EtaShape(double rpm) => rpm switch
    {
        < 1000 => 0.88,
        < 1800 => 0.88 + 0.12 * (rpm - 1000) / 800,
        <= 2600 => 1.0,
        <= 4500 => 1.0 - 0.15 * (rpm - 2600) / 1900,
        _ => 0.85,
    };

    private static DefinitionFile.MapEntry Write(byte[] img, ref int addr, string id, string name, MapRole role, double[] x, double[] y,
        double xFactor, double yFactor, string xUnit, string yUnit, AxisQuantity xq, AxisQuantity yq, double zFactor, string unit, Func<double, double, double> f)
    {
        var header = addr;
        var span = img.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[addr..], (ushort)x.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span[(addr + 2)..], (ushort)y.Length);
        addr += 4;
        var xAddr = addr;
        foreach (var v in x) { BinaryPrimitives.WriteUInt16BigEndian(span[addr..], (ushort)Math.Round(v / xFactor)); addr += 2; }
        var yAddr = addr;
        foreach (var v in y) { BinaryPrimitives.WriteUInt16BigEndian(span[addr..], (ushort)Math.Round(v / yFactor)); addr += 2; }
        var dataAddr = addr;
        foreach (var yv in y)
            foreach (var xv in x)
            {
                ValueReader.WriteRaw(span, addr, DataType.UInt16, Endianness.Big, f(xv, yv) / zFactor);
                addr += 2;
            }
        addr = (addr + 0x40) & ~0xF;
        _ = header;
        return new DefinitionFile.MapEntry
        {
            Id = id, Name = name, Role = role, Address = dataAddr, Rows = y.Length, Cols = x.Length, Factor = zFactor, Unit = unit, Confidence = 0.9,
            XAxis = new DefinitionFile.AxisEntry { Name = xq.ToString(), Unit = xUnit, Quantity = xq, Address = xAddr, Length = x.Length, Factor = xFactor },
            YAxis = new DefinitionFile.AxisEntry { Name = yq.ToString(), Unit = yUnit, Quantity = yq, Address = yAddr, Length = y.Length, Factor = yFactor },
        };
    }

    private static double Interp(double[] xs, double[] ys, double x)
    {
        if (x <= xs[0]) return ys[0];
        for (var i = 1; i < xs.Length; i++)
            if (x <= xs[i]) return ys[i - 1] + (ys[i] - ys[i - 1]) * (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
        return ys[^1];
    }

    private static void WriteAscii(byte[] img, int offset, string text) => Encoding.ASCII.GetBytes(text).CopyTo(img, offset);
}
