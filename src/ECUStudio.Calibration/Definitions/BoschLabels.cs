using System.Text.RegularExpressions;
using ECUStudio.Calibration.Model;

namespace ECUStudio.Calibration.Definitions;

/// <summary>
/// Roles of Bosch EDC16/EDC17 characteristics by their label (the A2L name), e.g. AccPed_trqEngHiGear_MAP (driver's
/// wish) or FMTC_trq2qBas_MAP (torque → injection quantity). Labels follow the Bosch scheme Component_quantityWhat_TYPE,
/// so the component prefix and the physical quantity decide the role; descriptions are only a fallback.
/// <see cref="Primary"/> marks the base map of a role (not a correction or an alternative data set), which wins when
/// several maps share a role.
/// </summary>
public static partial class BoschLabels
{
    private sealed record Rule(Regex Pattern, MapRole Role, Regex? Primary = null);

    private static Regex R(string p) => new(p, RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Rule[] Rules =
    [
        new(R(@"^AccPed_trq"), MapRole.DriverWish, R(@"HiG(ea)?r")),
        new(R(@"^FMTC_trq2q"), MapRole.TorqueToIq, R(@"Bas")),
        new(R(@"Smk|SmkLim|_mAirSmk|rLmbdSmk|qSmk"), MapRole.SmokeLimiter, R(@"Bas|_MAP$")),
        new(R(@"^EngPrt_trq|TrqLim|^PrpCtl_trqLim"), MapRole.TorqueLimiter, R(@"APSLim|Lim_MAP$")),
        new(R(@"^EngPrt_n(Max|Lim)|^EngICO_n|nMaxLim"), MapRole.RpmLimiter),
        new(R(@"^(PCR|BstCtl|ChrCtl)_p(Max|Lim|OvrBst)|pBstLim|pMaxSng"), MapRole.BoostLimiter),
        new(R(@"SVBL|Svbl|pSngVal"), MapRole.Svbl),
        new(R(@"^(PCR|BstCtl|ChrCtl)_pDes"), MapRole.BoostTarget, R(@"Bas|Val")),
        new(R(@"^(PCR|BstCtl|ChrCtl|TrbCh|VSA)_r(Des|Out|Pos)|N75|_VNT"), MapRole.VntDuty, R(@"Bas|Pilot")),
        new(R(@"^InjCrv_phiMI|^InjCrv_phiSOI|^PD_phi|^InjVlv_phi"), MapRole.Soi, R(@"Bas|MI1?Bas|_MAP$")),
        new(R(@"^InjCrv_ti|^PD_ti|^InjUn_ti|^InjVlv_ti"), MapRole.Duration),
        new(R(@"^Rail_p(SetPoint|Des)"), MapRole.RailPressure, R(@"Bas")),
        new(R(@"^AirCtl_rLmbd|^LmbdCtl_rDes"), MapRole.LambdaTarget),
        new(R(@"^ExhMod_t|^EGTCtl|^EngPrt_tExh|tExhLim"), MapRole.EgtProtection),
        new(R(@"^GlbDa_trqGear|^PrpCtl_trqGear|GearLim|trqGbx"), MapRole.GearTorqueLimiter),
    ];

    /// <summary>Role for a Bosch label, or Unknown when the label does not follow a known scheme.</summary>
    public static MapRole RoleOf(string label, out bool primary)
    {
        primary = false;
        foreach (var rule in Rules)
        {
            if (!rule.Pattern.IsMatch(label)) continue;
            primary = rule.Primary?.IsMatch(label) ?? true;
            return rule.Role;
        }
        return MapRole.Unknown;
    }

    /// <summary>Component prefix of a Bosch label ("AccPed" for AccPed_trqEngHiGear_MAP), used to group maps.</summary>
    public static string? Component(string label)
    {
        var m = ComponentRegex().Match(label);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^([A-Z][A-Za-z0-9]{1,11})_[a-z]")]
    private static partial Regex ComponentRegex();
}
