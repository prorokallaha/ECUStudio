using ECUStudio.Core;

namespace ECUStudio.Vehicle;

public sealed record VinInfo
{
    public required string Vin { get; init; }
    public required string Wmi { get; init; }
    public string? Manufacturer { get; init; }
    public string? Region { get; init; }
    public string Vds { get; init; } = "";
    public string? PlatformCode { get; init; }
    public int? ModelYear { get; init; }
    public char PlantCode { get; init; }
    public string Serial { get; init; } = "";
    public bool? CheckDigitValid { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>ISO 3779 VIN decoding with VAG-specific platform code extraction (positions 7–8).</summary>
public static class VinDecoder
{
    private static readonly Dictionary<string, string> Wmi = new()
    {
        ["WVW"] = "Volkswagen (passenger)", ["WVG"] = "Volkswagen (SUV/MPV)", ["WV1"] = "Volkswagen Commercial", ["WV2"] = "Volkswagen Commercial (bus/van)",
        ["WAU"] = "Audi", ["TMB"] = "Skoda", ["VSS"] = "SEAT", ["3VW"] = "Volkswagen Mexico", ["9BW"] = "Volkswagen Brazil", ["AAV"] = "Volkswagen South Africa",
    };

    private static readonly int[] Weights = [8, 7, 6, 5, 4, 3, 2, 10, 0, 9, 8, 7, 6, 5, 4, 3, 2];

    public static VinInfo Decode(string vin, int? currentYear = null)
    {
        if (string.IsNullOrWhiteSpace(vin)) throw new InvalidVinException("VIN is empty");
        vin = vin.Trim().ToUpperInvariant();
        if (vin.Length != 17) throw new InvalidVinException($"VIN must have 17 characters, got {vin.Length}");
        foreach (var c in vin)
        {
            if (!char.IsAsciiLetterOrDigit(c)) throw new InvalidVinException($"VIN contains invalid character '{c}'");
            if (c is 'I' or 'O' or 'Q') throw new InvalidVinException($"VIN must not contain '{c}'");
        }

        var warnings = new List<string>();
        var wmi = vin[..3];
        var region = vin[0] switch
        {
            >= 'S' and <= 'Z' => "Europe",
            >= '1' and <= '5' => "North America",
            '9' => "South America",
            >= 'J' and <= 'R' => "Asia",
            >= 'A' and <= 'H' => "Africa",
            _ => null,
        };

        bool? check = null;
        if (region == "North America")
        {
            check = ComputeCheckDigit(vin) == vin[8];
            if (check == false) warnings.Add("Check digit mismatch: VIN may be mistyped");
        }
        else if (char.IsDigit(vin[8]) || vin[8] == 'X')
        {
            var ok = ComputeCheckDigit(vin) == vin[8];
            if (ok) check = true; // EU makers often do not use position 9; only a match is informative
        }

        var year = DecodeYear(vin[9], currentYear ?? DateTime.UtcNow.Year);
        if (year is null) warnings.Add($"Unknown model year code '{vin[9]}'");

        var isVag = wmi is "WVW" or "WVG" or "WV1" or "WV2" or "WAU" or "TMB" or "VSS" or "3VW" or "9BW" or "AAV";
        var platform = isVag ? vin.Substring(6, 2) : null;
        warnings.Add("VIN does not encode the installed engine/turbo/gearbox for VAG Europe; hardware is inferred and must be verified.");

        return new VinInfo
        {
            Vin = vin, Wmi = wmi, Manufacturer = Wmi.GetValueOrDefault(wmi), Region = region, Vds = vin.Substring(3, 6),
            PlatformCode = platform, ModelYear = year, PlantCode = vin[10], Serial = vin[11..], CheckDigitValid = check, Warnings = warnings,
        };
    }

    public static char ComputeCheckDigit(string vin)
    {
        var sum = 0;
        for (var i = 0; i < 17; i++) sum += Transliterate(vin[i]) * Weights[i];
        var r = sum % 11;
        return r == 10 ? 'X' : (char)('0' + r);
    }

    private static int Transliterate(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        'A' or 'J' => 1, 'B' or 'K' or 'S' => 2, 'C' or 'L' or 'T' => 3, 'D' or 'M' or 'U' => 4,
        'E' or 'N' or 'V' => 5, 'F' or 'W' => 6, 'G' or 'P' or 'X' => 7, 'H' or 'Y' => 8, 'R' or 'Z' => 9,
        _ => 0,
    };

    /// <summary>Position 10. Codes repeat every 30 years; picks the latest year not after currentYear + 1.</summary>
    public static int? DecodeYear(char code, int currentYear)
    {
        const string codes = "ABCDEFGHJKLMNPRSTVWXY123456789";
        var idx = codes.IndexOf(code);
        if (idx < 0) return null;
        var year = 1980 + idx;
        while (year + 30 <= currentYear + 1) year += 30;
        return year;
    }
}
