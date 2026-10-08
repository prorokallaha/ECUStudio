using ECUStudio.Calibration.Definitions;
using ECUStudio.Calibration.Library;
using ECUStudio.Calibration.Model;
using Xunit;

namespace ECUStudio.Tests;

public class BoschLabelTests
{
    [Theory]
    [InlineData("AccPed_trqEngHiGear_MAP", MapRole.DriverWish, true)]
    [InlineData("AccPed_trqEngLoGear_MAP", MapRole.DriverWish, false)]
    [InlineData("FMTC_trq2qBas_MAP", MapRole.TorqueToIq, true)]
    [InlineData("EngPrt_trqAPSLim_MAP", MapRole.TorqueLimiter, true)]
    [InlineData("FlMng_rLmbdSmk_MAP", MapRole.SmokeLimiter, true)]
    [InlineData("PCR_pDesBas_MAP", MapRole.BoostTarget, true)]
    [InlineData("PCR_pMaxBas_MAP", MapRole.BoostLimiter, true)]
    [InlineData("InjCrv_phiMI1Bas1_MAP", MapRole.Soi, true)]
    [InlineData("Rail_pSetPointBase_MAP", MapRole.RailPressure, true)]
    [InlineData("CoEng_stEngOff_C", MapRole.Unknown, false)]
    public void Bosch_labels_map_to_roles(string label, MapRole role, bool primary)
    {
        Assert.Equal(role, BoschLabels.RoleOf(label, out var p));
        if (role != MapRole.Unknown) Assert.Equal(primary, p);
    }

    [Fact]
    public void Component_prefix_groups_labels()
    {
        Assert.Equal("AccPed", BoschLabels.Component("AccPed_trqEngHiGear_MAP"));
        Assert.Equal("FMTC", BoschLabels.Component("FMTC_trq2qBas_MAP"));
        Assert.Null(BoschLabels.Component("KF_1C14D4"));
    }

    [Theory]
    [InlineData("1037382425", "382425")]
    [InlineData("1037399389", "399389")]
    [InlineData("0281012345", null)]
    public void Short_software_number(string sw, string? expected) => Assert.Equal(expected, DefinitionMatcher.ShortSoftware(sw));

    [Theory]
    [InlineData("Org/03G906021JH_0131_382415_P447_HAXE_EDC16U34_3.42/x.a2l", 382415)]
    [InlineData("ADACT/VW_passat_2.0TDI_EDC 16_382425_cal/VW_passat_2.0TDI_EDC 16_382425_cal_ori.bin", 382425)]
    [InlineData("SW/HAXE/Daten/C447HAXE_00_13.a2l", null)]
    public void Short_software_number_in_names(string path, int? expected) => Assert.Equal(expected, DefinitionMatcher.ShortSoftwareIn(path));
}
