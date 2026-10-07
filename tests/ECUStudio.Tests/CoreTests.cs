using ECUStudio.Core;
using ECUStudio.Vehicle;

namespace ECUStudio.Tests;

public class EstimateTests
{
    [Fact]
    public void Of_orders_bounds_and_keeps_value_inside_range()
    {
        var e = Estimate.Of(100, 120, 90, "Nm", 1.4);
        Assert.Equal(90, e.Low);
        Assert.Equal(120, e.High);
        Assert.Equal(1, e.Confidence); // clamped
        Assert.True(e.IsKnown);
    }

    [Fact]
    public void Non_finite_value_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Estimate.Of(double.NaN, 0, 1, "Nm", 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Estimate.Of(double.PositiveInfinity, 0, 1, "Nm", 0.5));
    }

    [Fact]
    public void Unknown_and_not_applicable_are_never_known_values()
    {
        Assert.False(Estimate.Unknown("Nm").IsKnown);
        Assert.False(Estimate.NotApplicable("bar").IsKnown);
        Assert.Equal("UNKNOWN", Estimate.Unknown("Nm").Display().Value);
    }

    [Theory]
    [InlineData(144.6, "hp", 145)]
    [InlineData(2337, "mbar", 2340)]
    [InlineData(763, "°C", 765)]
    [InlineData(48.3, "mg/stroke", 48.5)]
    [InlineData(0.7849, "-", 0.78)]
    public void Display_rounds_to_model_resolution_not_false_precision(double value, string unit, double expected)
    {
        Assert.Equal(expected, EngineeringRounding.Round(value, unit));
    }

    [Fact]
    public void Scale_with_negative_factor_keeps_low_below_high()
    {
        var e = Estimate.Of(10, 8, 12, "-", 0.5).Scale(-2);
        Assert.Equal(-20, e.Value);
        Assert.Equal(-24, e.Low);
        Assert.Equal(-16, e.High);
    }

    [Fact]
    public void Unknown_outranks_review_but_not_warning_in_aggregation()
    {
        Assert.Equal(Severity.Unknown, SeverityExtensions.Worst([Severity.Safe, Severity.Review, Severity.Unknown]));
        Assert.Equal(Severity.Warning, SeverityExtensions.Worst([Severity.Unknown, Severity.Warning]));
    }
}

public class VinDecoderTests
{
    [Fact]
    public void Decodes_vag_europe_vin()
    {
        var v = VinDecoder.Decode(" wvwzzz1kz6w123456 ", currentYear: 2026);
        Assert.Equal("WVWZZZ1KZ6W123456", v.Vin);
        Assert.Equal("WVW", v.Wmi);
        Assert.Equal("Europe", v.Region);
        Assert.Equal("1K", v.PlatformCode);
        Assert.Equal(2006, v.ModelYear);
        Assert.Equal('W', v.PlantCode);
        Assert.Contains(v.Warnings, w => w.Contains("does not encode the installed engine"));
    }

    [Fact]
    public void North_american_check_digit_is_validated()
    {
        Assert.Equal('X', VinDecoder.ComputeCheckDigit("1M8GDM9AXKP042788"));
        Assert.True(VinDecoder.Decode("1M8GDM9AXKP042788", 2026).CheckDigitValid);
        var bad = VinDecoder.Decode("1M8GDM9A1KP042788", 2026);
        Assert.False(bad.CheckDigitValid);
        Assert.Contains(bad.Warnings, w => w.Contains("Check digit"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("WVWZZZ1KZ6W12345")]
    [InlineData("WVWZZZ1KZ6W1234567")]
    [InlineData("WVWZZZ1KZ6W12345O")]
    [InlineData("WVWZZZ1KZ6W12345-")]
    public void Invalid_vins_throw_domain_error(string vin)
    {
        var ex = Assert.Throws<InvalidVinException>(() => VinDecoder.Decode(vin));
        Assert.Equal("INVALID_VIN", ex.Code);
    }

    [Theory]
    [InlineData('6', 2026, 2006)]
    [InlineData('A', 2026, 2010)]
    [InlineData('Y', 2026, 2000)]
    [InlineData('T', 2026, 2026)]
    [InlineData('V', 2026, 2027)]
    public void Model_year_picks_latest_cycle_not_in_future(char code, int now, int expected)
    {
        Assert.Equal(expected, VinDecoder.DecodeYear(code, now));
    }
}
