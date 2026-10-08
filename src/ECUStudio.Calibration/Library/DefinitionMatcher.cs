namespace ECUStudio.Calibration.Library;

/// <summary>
/// Ranks library entries against one binary. Levels:
/// Exact (identical file, or SW + HW both match), Strong (SW matches), Probable (same OEM part number, same Bosch project
/// code, or SW matches with a different HW), Weak (same ECU family, optionally the same engine), Unknown (not returned).
/// Every match carries the reasons it was made, including what contradicts it.
/// </summary>
public static class DefinitionMatcher
{
    public static IReadOnlyList<DefinitionMatch> Match(BinaryKey key, IEnumerable<LibraryEntry> entries, int limit = 50)
    {
        var family = NormalizeFamily(key.EcuFamily);
        var oem = key.OemNumber is { Length: > 0 } o ? LibraryScanner.NormalizeOem(o) : null;
        var results = new List<DefinitionMatch>();
        foreach (var e in entries)
        {
            if (Score(key, family, oem, e) is { } m) results.Add(m);
        }
        return results
            .OrderBy(m => m.Level)
            .ThenByDescending(m => m.Score)
            .ThenByDescending(m => m.IsDefinition)
            .ThenByDescending(m => m.Entry.Available)
            .Take(limit)
            .ToList();
    }

    public static string? NormalizeFamily(string? family)
    {
        if (string.IsNullOrWhiteSpace(family)) return null;
        var s = family.ToUpperInvariant().Replace("BOSCH", "", StringComparison.Ordinal).Replace("SIEMENS", "", StringComparison.Ordinal);
        return new string(s.Where(char.IsAsciiLetterOrDigit).ToArray()) is { Length: > 0 } n ? n : null;
    }

    /// <summary>Last six digits of a Bosch software number (1037382425 → 382425), null for anything else.</summary>
    public static string? ShortSoftware(string? sw) =>
        sw is { Length: 10 } s && s.StartsWith("103", StringComparison.Ordinal) && s.All(char.IsAsciiDigit) ? s[4..] : null;

    /// <summary>Six-digit number bounded by non-digits in the file name or its folder.</summary>
    public static int? ShortSoftwareIn(string relativePath)
    {
        foreach (var part in NameParts(relativePath))
        {
            var m = System.Text.RegularExpressions.Regex.Match(part, @"(?<![0-9])(3[0-9]{5}|5[0-9]{5})(?![0-9])");
            if (m.Success) return int.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        return null;
    }

    private static bool NameHasToken(string relativePath, string token) =>
        NameParts(relativePath).Any(p => System.Text.RegularExpressions.Regex.IsMatch(p, $"(?<![0-9]){token}(?![0-9])"));

    private static IEnumerable<string> NameParts(string relativePath)
    {
        var parts = relativePath.Split('/', '\\');
        yield return parts[^1];
        if (parts.Length > 1) yield return parts[^2];
    }

    private static DefinitionMatch? Score(BinaryKey key, string? family, string? oem, LibraryEntry e)
    {
        var ids = e.Identifiers;
        var where = e.ContentIdentified ? "in file content" : "in file name";
        var reasons = new List<string>();

        if (key.Sha256 is { } sha && e.Sha256 is { } esha && string.Equals(sha, esha, StringComparison.OrdinalIgnoreCase))
            return new DefinitionMatch(e, MatchLevel.Exact, 1.0, ["identical file (SHA-256)"], 1);

        var swFull = key.SoftwareNumber is { } sw && ids.SoftwareNumbers.Contains(sw);
        // Archives often name Bosch software by its last six digits ("…_382425_ori.bin", "03G906021JH_0131_382415_P447_HAXE").
        var swShort = !swFull && ShortSoftware(key.SoftwareNumber) is { } shortSw && NameHasToken(e.RelativePath, shortSw);
        var swMatch = swFull || swShort;
        var hwKnown = key.HardwareNumber is { } && ids.HardwareNumbers.Count > 0;
        var hwMatch = key.HardwareNumber is { } hw && ids.HardwareNumbers.Contains(hw);
        var oemMatch = oem is not null && ids.OemNumbers.Contains(oem);
        var familyExact = family is not null && ids.EcuFamilies.Any(f => NormalizeFamily(f) == family);
        var familyPartial = !familyExact && family is not null && ids.EcuFamilies.Any(f => NormalizeFamily(f) is { } nf && (family.StartsWith(nf, StringComparison.Ordinal) || nf.StartsWith(family, StringComparison.Ordinal)));
        var familyConflict = family is not null && ids.EcuFamilies.Count > 0 && !familyExact && !familyPartial;
        var projectMatch = key.ProjectCode is { } pc && ids.ProjectCodes.Contains(pc);
        var versionMatch = key.SoftwareVersion is { } sv && ids.SoftwareVersions.Contains(sv);
        var otherVersion = key.SoftwareVersion is not null && ids.SoftwareVersions.Count > 0 && !versionMatch;
        var engineMatch = key.EngineHint is { } eh && ids.EngineHints.Any(h => h.StartsWith(eh, StringComparison.OrdinalIgnoreCase));

        if (familyConflict) return null; // a definition for another ECU family is never offered
        if (swFull) reasons.Add($"SW {key.SoftwareNumber} {where}");
        else if (swShort) reasons.Add($"SW {ShortSoftware(key.SoftwareNumber)} (short form of {key.SoftwareNumber}) in file name");
        if (hwMatch) reasons.Add($"HW {key.HardwareNumber} {where}");
        else if (hwKnown) reasons.Add($"HW differs: file has {string.Join(", ", ids.HardwareNumbers)}, binary has {key.HardwareNumber}");
        if (oemMatch) reasons.Add($"OEM part {key.OemNumber} {where}");
        if (familyExact) reasons.Add($"ECU family {key.EcuFamily}");
        else if (familyPartial) reasons.Add($"ECU family {string.Join(", ", ids.EcuFamilies)} (partial match to {key.EcuFamily})");
        if (engineMatch) reasons.Add($"engine {key.EngineHint}");
        if (projectMatch) reasons.Add($"Bosch project {key.ProjectCode} {where} (same software family)");
        if (versionMatch) reasons.Add($"software version {key.SoftwareVersion} {where}");
        else if (otherVersion) reasons.Add($"other software version: file has {string.Join(", ", ids.SoftwareVersions)}, binary has {key.SoftwareVersion}");

        var otherSw = !swMatch && ids.SoftwareNumbers.Count > 0 && key.SoftwareNumber is not null;
        if (otherSw) reasons.Add($"different SW: file has {string.Join(", ", ids.SoftwareNumbers.Take(3))}; addresses may differ");

        var bonus = (e.ContentIdentified ? 0.03 : 0) + (e.Available ? 0.01 : 0);
        // Rank per the acquisition order: 1 project + exact SW/version, 2 OEM SW + SW (or SW alone), 3 project + other or
        // unknown version, 4 ECU family + OEM SW, 6 family only.
        var rank = projectMatch && (swMatch || versionMatch) || swMatch && versionMatch ? 1
            : swMatch && (oemMatch || !hwKnown || hwMatch) ? 2
            : projectMatch && !otherSw ? 3
            : oemMatch && versionMatch ? 2
            : oemMatch ? 4
            : swMatch ? 4
            : 6;
        if (swMatch && hwMatch) return new DefinitionMatch(e, MatchLevel.Exact, 0.95 + bonus, reasons, rank);
        if (swMatch && !hwKnown) return new DefinitionMatch(e, MatchLevel.Strong, 0.85 + bonus, reasons, rank);
        if (swMatch) return new DefinitionMatch(e, MatchLevel.Probable, 0.65 + bonus, reasons, rank);
        if (oemMatch && versionMatch) return new DefinitionMatch(e, MatchLevel.Strong, 0.8 + (projectMatch ? 0.05 : 0) + bonus, reasons, rank);
        if (oemMatch) return new DefinitionMatch(e, MatchLevel.Probable, (otherSw ? 0.5 : 0.6) + (projectMatch ? 0.05 : 0) + bonus, reasons, projectMatch ? 3 : rank);
        // Same project, other SW version: structure is usually close, but addresses must be checked before use.
        if (projectMatch) return new DefinitionMatch(e, MatchLevel.Probable, 0.45 + bonus, [.. reasons, "SW version not confirmed: check addresses before use"], rank);
        if (familyExact || familyPartial)
            return new DefinitionMatch(e, MatchLevel.Weak, (familyExact ? 0.3 : 0.2) + (engineMatch ? 0.05 : 0) + bonus, reasons, 6);
        return null;
    }
}
