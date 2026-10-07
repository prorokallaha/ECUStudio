using ECUStudio.Components;
using ECUStudio.Vehicle;
using ECUStudio.Vehicle.Resolution;

namespace ECUStudio.Tests;

public class VehicleResolverTests
{
    private const string PassatVin = "WVWZZZ3CZ7E123456"; // 3C = Passat B6, model year 2007

    private static EcuFacts Edc16(string engineText, string oem = "03G906021AB", string sw = "1037370000") => new()
    {
        PluginId = "edc16u34", EcuFamily = "Bosch EDC16U34", DetectionScore = 0.95, EngineText = engineText,
        OemPartNumber = oem, SoftwareNumber = sw, HardwareNumber = "0281012345", EngineFamilies = ["VAG_PD_19", "VAG_PD_20"],
    };

    private static VehicleResolution Resolve(VehicleQuery q, VehicleKnowledgeBase? kb = null) => new VehicleResolver(kb ?? Fixtures.Kb.Value).Resolve(q);

    [Theory]
    [InlineData("R4 2,0L EDC G000AG", "2.0", 4)]
    [InlineData("1,9l R4 EDC G000SG", "1.9", 4)]
    [InlineData("V6 3,0L EDC", "3.0", 6)]
    public void Engine_string_gives_displacement_and_cylinders_in_either_order(string text, string litres, int cylinders) =>
        Assert.Equal((litres, cylinders), EcuIdentificationProvider.ParseEngineText(text));

    [Fact]
    public void Passat_b6_with_2_0_ecu_string_resolves_to_a_2_0_variant_not_1_9()
    {
        var r = Resolve(new VehicleQuery { Vin = PassatVin, Ecu = Edc16("R4 2,0L EDC G000AG") });

        Assert.Equal(VehicleMatchStatus.Resolved, r.Status);
        Assert.Equal("BMP", r.Profile.EngineCode.Text);
        Assert.Equal(1968, r.Profile.Hardware.Engine.Number(P.DisplacementCc));
        Assert.True(r.Candidates[0].Probability > 0.8);
        Assert.DoesNotContain(r.Candidates, c => c.Variant.EngineCode is "BKC" or "BXE" or "BLS" && c.Probability > 0.05);
        // Siemens-equipped 2.0 TDI variants are ruled out by the ECU family.
        Assert.DoesNotContain(r.Candidates, c => c.Variant.EngineCode == "BKP" && c.Probability > 0.05);
    }

    [Fact]
    public void When_the_database_lacks_the_engine_the_closest_wrong_variant_is_not_adopted()
    {
        var full = Fixtures.Kb.Value;
        var only19 = new VehicleKnowledgeBase(full.Variants.Where(v => v.EngineFamily == "VAG_PD_19").ToList(), full.Catalog);
        var r = Resolve(new VehicleQuery { Vin = PassatVin, Ecu = Edc16("R4 2,0L EDC G000AG") }, only19);

        Assert.Equal(VehicleMatchStatus.NotInDatabase, r.Status);
        Assert.Null(r.Profile.VariantId);
        Assert.False(r.Profile.EngineCode.IsKnown);
        Assert.Equal("engine_from_ecu", r.Profile.Hardware.Engine.Id);
        Assert.Equal(2000, r.Profile.Hardware.Engine.Number(P.DisplacementCc));
        Assert.False(r.Profile.Hardware.Turbo.Get(P.Model).IsKnown);
        Assert.Contains("Passat B6", r.Profile.Model.Text);
        Assert.Contains(r.Profile.Notes, n => n.Contains("contradicts", StringComparison.Ordinal) && n.Contains("2.0", StringComparison.Ordinal));
        Assert.Equal(ConfigurationStatus.Unknown, r.Configuration.Single(i => i.Key == "engine_code").Status);
        Assert.Equal("2.0 L", r.Configuration.Single(i => i.Key == "displacement").Value);
    }

    [Fact]
    public void Same_hardware_variants_resolve_the_engine_even_if_the_code_stays_ambiguous()
    {
        var r = Resolve(new VehicleQuery { Vin = Fixtures.GolfVin, Ecu = Edc16("1,9L R4 EDC G000SG") });

        Assert.Equal(VehicleMatchStatus.Resolved, r.Status); // BKC/BXE/BLS share the engine and ECU
        Assert.Contains(r.Profile.EngineCode.Text, new[] { "BKC", "BXE", "BLS" });
        var code = r.Configuration.Single(i => i.Key == "engine_code");
        Assert.Equal(ConfigurationStatus.Ambiguous, code.Status);
        Assert.NotEmpty(code.Alternatives);
        var power = r.Configuration.Single(i => i.Key == "power");
        Assert.Equal("77 kW", power.Value);
        Assert.Equal(ConfigurationStatus.Resolved, power.Status);
    }

    [Fact]
    public void Vin_alone_keeps_the_engine_open()
    {
        var r = Resolve(new VehicleQuery { Vin = PassatVin });
        Assert.NotEqual(VehicleMatchStatus.Resolved, r.Status);
        Assert.Contains(r.Candidates, c => c.Variant.EngineFamily == "VAG_PD_20");
        Assert.Contains(r.Candidates, c => c.Variant.EngineFamily == "VAG_PD_19");
    }

    [Fact]
    public void Configuration_never_invents_part_numbers_and_takes_ecu_facts_from_the_binary()
    {
        var r = Resolve(new VehicleQuery { Vin = PassatVin, Ecu = Edc16("R4 2,0L EDC G000AG") });
        var cfg = r.Configuration.ToDictionary(i => i.Key);

        Assert.Equal(ConfigurationStatus.Unknown, cfg["turbo_part"].Status);
        Assert.Null(cfg["turbo_part"].PartNumber);
        Assert.Equal(ConfigurationStatus.Unknown, cfg["injector_part"].Status);
        Assert.Equal("1037370000", cfg["ecu_sw"].Value);
        Assert.Equal("ECU binary", cfg["ecu_sw"].Source);
        Assert.Equal(ConfigurationStatus.Ambiguous, cfg["gearbox"].Status); // manual or DSG
        Assert.All(r.Configuration.Where(i => i.Status == ConfigurationStatus.Unknown), i => Assert.False(string.IsNullOrEmpty(i.Note)));
    }

    [Fact]
    public void User_selection_wins_and_contradictions_are_reported()
    {
        var r = Resolve(new VehicleQuery { Vin = PassatVin, Ecu = Edc16("R4 2,0L EDC G000AG"), PreferredVariantId = "vw_passat_b6_bkc" });
        Assert.Equal("vw_passat_b6_bkc", r.Profile.VariantId);
        Assert.Equal(VehicleMatchStatus.Resolved, r.Status);
        Assert.Contains(r.Profile.Notes, n => n.Contains("selected variant contradicts", StringComparison.Ordinal));
    }

    [Fact]
    public void Knowledge_confirmed_for_a_software_number_applies_to_that_software_only()
    {
        var store = new InMemoryVehicleKnowledgeStore();
        store.Confirm(new ConfirmedVehicle("1037370000", null, "vw_golf5_bxe", "BXE", DateTimeOffset.UtcNow));
        var resolver = new VehicleResolver(Fixtures.Kb.Value, VehicleResolver.DefaultProviders(store));

        var same = resolver.Resolve(new VehicleQuery { Vin = Fixtures.GolfVin, Ecu = Edc16("1,9L R4 EDC G000SG", sw: "1037370000") });
        Assert.Equal("BXE", same.Profile.EngineCode.Text);
        Assert.Equal(ConfigurationStatus.Resolved, same.Configuration.Single(i => i.Key == "engine_code").Status);

        var other = resolver.Resolve(new VehicleQuery { Vin = Fixtures.GolfVin, Ecu = Edc16("1,9L R4 EDC G000SG", sw: "1037379999") });
        Assert.Equal(ConfigurationStatus.Ambiguous, other.Configuration.Single(i => i.Key == "engine_code").Status);
    }
}
