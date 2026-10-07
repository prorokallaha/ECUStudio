using System.Globalization;
using System.Text.Json.Serialization;
using ECUStudio.Components;
using ECUStudio.Core;

namespace ECUStudio.Vehicle.Resolution;

[JsonConverter(typeof(JsonStringEnumConverter<ConfigurationStatus>))]
public enum ConfigurationStatus { Verified, Resolved, Ambiguous, Unknown }

public sealed record ConfigurationAlternative(string Value, double Probability);

/// <summary>One item of the reconstructed vehicle configuration, with provenance. Nothing is invented: no data → Unknown.</summary>
public sealed record ConfigurationItem
{
    public required string Key { get; init; }
    public required string Group { get; init; }
    public string? Value { get; init; }
    public string? PartNumber { get; init; }
    public required string Source { get; init; }
    public double Confidence { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public bool Verified { get; init; }
    public IReadOnlyList<ConfigurationAlternative> Alternatives { get; init; } = [];
    public ConfigurationStatus Status { get; init; }
    public string? Note { get; init; }
}

/// <summary>
/// Reconstructs the vehicle configuration (engine, ECU, turbo, injection, sensors, drivetrain, emissions) from the
/// resolution: ECU facts come from the binary; component facts are aggregated over the catalogued variants that stay
/// compatible, so disagreeing variants become alternatives with LOW confidence instead of one guessed part.
/// </summary>
public static class VehicleConfigurationResolver
{
    public static IReadOnlyList<ConfigurationItem> Resolve(VehicleResolution r, VehicleQuery query, ComponentCatalog catalog)
    {
        var items = new List<ConfigurationItem>();
        var ecu = query.Ecu;
        // Only variants that do not contradict decisive evidence describe this vehicle's hardware.
        var pool = r.Status == VehicleMatchStatus.NotInDatabase ? []
            : r.Profile.VariantId is { } chosen && query.PreferredVariantId is not null
                ? r.Candidates.Where(c => c.Variant.Id == chosen).Select(c => c with { Probability = 1 }).ToList()
                : r.Candidates.Where(c => !c.Checks.Any(k => k.Match == false && k.Confidence >= VehicleResolver.HardEvidence)).ToList();
        var mass = pool.Sum(c => c.Probability);
        var overridden = query.Overrides.Select(o => o.Kind).ToHashSet();

        ComponentSpec? Comp(VehicleVariant v, string key) => v.Components.TryGetValue(key, out var id) ? catalog.Get(id) : null;

        // Aggregates a value over the pool; probability is normalised to the pool and scaled by its share of all hypotheses.
        ConfigurationItem FromVariants(string key, string group, Func<VehicleVariant, (string? Value, double Confidence)> pick, string source, ComponentKind? kind = null)
        {
            if (kind is { } k && overridden.Contains(k))
            {
                var spec = r.Profile.Hardware.Get(k);
                return new ConfigurationItem { Key = key, Group = group, Value = spec.Name, Source = "User", Confidence = 1, Verified = true, Status = ConfigurationStatus.Verified,
                    Evidence = ["set by user"], PartNumber = spec.Get("part_number").Text };
            }
            if (mass <= 0) return Unknown(key, group, r.Status == VehicleMatchStatus.NotInDatabase ? "vehicle not in database" : "no vehicle data");
            var dist = pool.Select(c => (V: pick(c.Variant), P: c.Probability / mass))
                .Where(x => x.V.Value is not null)
                .GroupBy(x => x.V.Value!).Select(g => (Value: g.Key, P: g.Sum(x => x.P), C: g.Max(x => x.V.Confidence))).OrderByDescending(x => x.P).ToList();
            if (dist.Count == 0) return Unknown(key, group, "not recorded for the matching variants");
            var top = dist[0];
            var share = Math.Min(1, mass / (mass + r.UnlistedProbability));
            var confidence = Math.Round(top.P * top.C * share, 2);
            var status = dist.Count > 1 && top.P < 0.8 ? ConfigurationStatus.Ambiguous : ConfigurationStatus.Resolved;
            return new ConfigurationItem
            {
                Key = key, Group = group, Value = top.Value, Source = source, Confidence = confidence, Status = status,
                Alternatives = dist.Skip(1).Select(d => new ConfigurationAlternative(d.Value, Math.Round(d.P, 2))).ToList(),
                Evidence = [$"{dist.Count} value(s) across {pool.Count} compatible variant(s): {string.Join(", ", pool.Take(4).Select(c => $"{c.Variant.EngineCode} {c.Probability:P0}"))}"],
                Note = status == ConfigurationStatus.Ambiguous ? $"{dist.Count} compatible values; requires verification" : null,
            };
        }

        (string?, double) Param(VehicleVariant v, string comp, string param)
        {
            var p = Comp(v, comp)?.Get(param);
            if (p is null || !p.IsKnown) return (null, 0);
            return (p.Text ?? (p.Unit is "-" ? Fmt(p.Number!.Value) : $"{Fmt(p.Number!.Value)} {p.Unit}"), p.Confidence);
        }

        (string?, double) Name(VehicleVariant v, string comp) => Comp(v, comp) is { } c && c.Source != SourceType.Unknown ? (c.Name, c.Confidence) : (null, 0);

        // ---- engine
        items.Add(FromVariants("engine_code", "engine", v => (v.EngineCode, 0.9), "Vehicle database (variant)"));
        var ecuEngine = EcuIdentificationProvider.ParseEngineText(ecu?.EngineText);
        items.Add(ecuEngine is { } ee
            ? Ecu("displacement", "engine", $"{ee.Litres} L", 0.9, $"engine string in binary: \"{ecu!.EngineText}\"")
            : FromVariants("displacement", "engine", v => Param(v, "engine", P.DisplacementCc), "Vehicle database"));
        items.Add(ecuEngine is { } ec
            ? Ecu("cylinders", "engine", ec.Cylinders.ToString(CultureInfo.InvariantCulture), 0.9, $"engine string in binary: \"{ecu!.EngineText}\"")
            : FromVariants("cylinders", "engine", v => Param(v, "engine", P.Cylinders), "Vehicle database"));
        items.Add(FromVariants("power", "engine", v => Param(v, "engine", P.RatedPowerKw), "Vehicle database (OEM rating)", ComponentKind.Engine));
        items.Add(FromVariants("torque", "engine", v => Param(v, "engine", P.RatedTorqueNm), "Vehicle database (OEM rating)", ComponentKind.Engine));
        items.Add(FromVariants("injection_system", "engine", v => Param(v, "engine", P.InjectionSystem), "Vehicle database", ComponentKind.Engine));

        // ---- ECU (read from the binary)
        items.Add(ecu?.EcuFamily is { } fam ? Ecu("ecu", "ecu", fam, Math.Round(Math.Clamp(ecu.DetectionScore, 0, 0.99), 2), "ECU plugin detection") : Unknown("ecu", "ecu", "no binary"));
        items.Add(ecu?.HardwareNumber is { } hw ? Ecu("ecu_hw", "ecu", hw, 0.85, "HW number pattern in binary") : Unknown("ecu_hw", "ecu", "not found in binary"));
        items.Add(ecu?.SoftwareNumber is { } sw ? Ecu("ecu_sw", "ecu", sw, 0.85, "SW number pattern in binary") : Unknown("ecu_sw", "ecu", "not found in binary"));
        items.Add(ecu?.OemPartNumber is { } oem ? Ecu("ecu_oem", "ecu", oem, 0.85, "OEM part number pattern in binary") : Unknown("ecu_oem", "ecu", "not found in binary"));
        items.Add(ecu?.CalibrationId is { } cal ? Ecu("calibration_id", "ecu", cal, 0.8, "calibration id in binary") : Unknown("calibration_id", "ecu", "not found in binary"));

        // ---- air / turbo
        items.Add(FromVariants("turbo", "air", v => Name(v, "turbo"), "Vehicle database (variant typical)", ComponentKind.Turbo));
        items.Add(PartOrUnknown("turbo_part", "air", pool, v => Comp(v, "turbo"), "Turbo part number is not derivable from VIN/ECU; read it from the turbo label"));
        items.Add(FromVariants("intercooler", "air", v => Name(v, "intercooler"), "Vehicle database", ComponentKind.Intercooler));
        items.Add(FromVariants("maf", "air", v => Param(v, "sensors", "maf"), "Vehicle database", ComponentKind.Sensors));
        items.Add(FromVariants("map_sensor", "air", v => Param(v, "sensors", "map"), "Vehicle database", ComponentKind.Sensors));
        items.Add(Unknown("boost_sensor_range", "air", "sensor range is read from the boost sensor linearisation in the definition, not available yet"));

        // ---- fuel
        items.Add(FromVariants("injectors", "fuel", v => Name(v, "injectors"), "Vehicle database (variant typical)", ComponentKind.Injectors));
        items.Add(PartOrUnknown("injector_part", "fuel", pool, v => Comp(v, "injectors"), "Injector part numbers require the injector label or workshop data"));
        items.Add(FromVariants("fuel_pump", "fuel", v => Name(v, "fuelSystem"), "Vehicle database", ComponentKind.FuelSystem));

        // ---- drivetrain
        items.Add(FromVariants("gearbox", "drivetrain", v => Name(v, "transmission"), "Vehicle database (default option)", ComponentKind.Transmission) is var gb && query.TransmissionId is null
            && pool.FirstOrDefault()?.Variant.TransmissionOptions.Count > 1
            ? gb with { Status = ConfigurationStatus.Ambiguous, Confidence = Math.Min(gb.Confidence, 0.35), Note = "Gearbox is not encoded in the VIN; select it in Components",
                Alternatives = pool[0].Variant.TransmissionOptions.Select(id => catalog.Get(id)?.Name ?? id).Where(n => n != gb.Value).Select(n => new ConfigurationAlternative(n, 0)).ToList() }
            : gb);
        items.Add(Unknown("gearbox_code", "drivetrain", "gearbox code is on the gearbox label / build sheet (PR codes)"));
        items.Add(FromVariants("gears", "drivetrain", v => Param(v, "transmission", P.Gears), "Vehicle database", ComponentKind.Transmission));
        items.Add(FromVariants("clutch", "drivetrain", v => Name(v, "clutch"), "Vehicle database", ComponentKind.Clutch));
        items.Add(FromVariants("dual_mass_flywheel", "drivetrain",
            v => Comp(v, "clutch") is { } c ? (c.Name.Contains("dual-mass", StringComparison.OrdinalIgnoreCase) ? "Yes" : null, c.Confidence) : (null, 0), "Vehicle database", ComponentKind.Clutch));
        items.Add(Unknown("drivetrain", "drivetrain", "FWD/4Motion is not encoded reliably in the VIN; check PR codes"));

        // ---- emissions / sensors
        items.Add(FromVariants("emission_standard", "emissions", v => Comp(v, "emissions") is { } c ? (c.Name.Split(',')[0].Trim(), c.Confidence) : (null, 0), "Vehicle database", ComponentKind.Emissions));
        items.Add(FromVariants("dpf", "emissions", v => Param(v, "emissions", "dpf"), "Vehicle database", ComponentKind.Emissions));
        items.Add(FromVariants("egr", "emissions", v => Param(v, "emissions", "egr"), "Vehicle database", ComponentKind.Emissions));
        items.Add(FromVariants("lambda_sensors", "emissions", v => Param(v, "sensors", "lambda"), "Vehicle database", ComponentKind.Sensors));
        items.Add(FromVariants("egt_sensors", "emissions", v => Param(v, "sensors", "egt"), "Vehicle database", ComponentKind.Sensors));
        items.Add(Unknown("glow_plugs", "engine", "not recorded in the vehicle database"));
        items.Add(Unknown("glow_plug_controller", "engine", "not recorded in the vehicle database"));
        return items;
    }

    private static ConfigurationItem Ecu(string key, string group, string value, double confidence, string evidence) => new()
    {
        Key = key, Group = group, Value = value, Source = "ECU binary", Confidence = confidence, Evidence = [evidence], Status = ConfigurationStatus.Resolved,
    };

    private static ConfigurationItem Unknown(string key, string group, string why) => new()
    {
        Key = key, Group = group, Source = "-", Confidence = 0, Status = ConfigurationStatus.Unknown, Note = why,
    };

    private static ConfigurationItem PartOrUnknown(string key, string group, IReadOnlyList<VariantCandidate> pool, Func<VehicleVariant, ComponentSpec?> comp, string why)
    {
        var parts = pool.Select(c => comp(c.Variant)?.Get("part_number")).Where(p => p is { IsKnown: true }).Select(p => p!.Text!).Distinct().ToList();
        return parts.Count switch
        {
            0 => Unknown(key, group, why),
            1 => new ConfigurationItem { Key = key, Group = group, Value = parts[0], PartNumber = parts[0], Source = "Vehicle database", Confidence = 0.5, Status = ConfigurationStatus.Resolved },
            _ => new ConfigurationItem { Key = key, Group = group, Source = "Vehicle database", Confidence = 0.2, Status = ConfigurationStatus.Ambiguous,
                Alternatives = parts.Select(p => new ConfigurationAlternative(p, 0)).ToList(), Note = $"{parts.Count} compatible part numbers; requires verification" },
        };
    }

    private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
