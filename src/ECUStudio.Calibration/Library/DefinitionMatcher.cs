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

    private static DefinitionMatch? Score(BinaryKey key, string? family, string? oem, LibraryEntry e)
    {
        var ids = e.Identifiers;
        var where = e.ContentIdentified ? "in file content" : "in file name";
        var reasons = new List<string>();

        if (key.Sha256 is { } sha && e.Sha256 is { } esha && string.Equals(sha, esha, StringComparison.OrdinalIgnoreCase))
            return new DefinitionMatch(e, MatchLevel.Exact, 1.0, ["identical file (SHA-256)"]);

        var swMatch = key.SoftwareNumber is { } sw && ids.SoftwareNumbers.Contains(sw);
        var hwKnown = key.HardwareNumber is { } && ids.HardwareNumbers.Count > 0;
        var hwMatch = key.HardwareNumber is { } hw && ids.HardwareNumbers.Contains(hw);
        var oemMatch = oem is not null && ids.OemNumbers.Contains(oem);
        var familyExact = family is not null && ids.EcuFamilies.Any(f => NormalizeFamily(f) == family);
        var familyPartial = !familyExact && family is not null && ids.EcuFamilies.Any(f => NormalizeFamily(f) is { } nf && (family.StartsWith(nf, StringComparison.Ordinal) || nf.StartsWith(family, StringComparison.Ordinal)));
        var familyConflict = family is not null && ids.EcuFamilies.Count > 0 && !familyExact && !familyPartial;
        var projectMatch = key.ProjectCode is { } pc && ids.ProjectCodes.Contains(pc);
        var engineMatch = key.EngineHint is { } eh && ids.EngineHints.Any(h => h.StartsWith(eh, StringComparison.OrdinalIgnoreCase));

        if (familyConflict) return null; // a definition for another ECU family is never offered
        if (swMatch) reasons.Add($"SW {key.SoftwareNumber} {where}");
        if (hwMatch) reasons.Add($"HW {key.HardwareNumber} {where}");
        else if (hwKnown) reasons.Add($"HW differs: file has {string.Join(", ", ids.HardwareNumbers)}, binary has {key.HardwareNumber}");
        if (oemMatch) reasons.Add($"OEM part {key.OemNumber} {where}");
        if (familyExact) reasons.Add($"ECU family {key.EcuFamily}");
        else if (familyPartial) reasons.Add($"ECU family {string.Join(", ", ids.EcuFamilies)} (partial match to {key.EcuFamily})");
        if (engineMatch) reasons.Add($"engine {key.EngineHint}");
        if (projectMatch) reasons.Add($"Bosch project {key.ProjectCode} {where} (same software family)");

        var otherSw = !swMatch && ids.SoftwareNumbers.Count > 0 && key.SoftwareNumber is not null;
        if (otherSw) reasons.Add($"different SW: file has {string.Join(", ", ids.SoftwareNumbers.Take(3))}; addresses may differ");

        var bonus = (e.ContentIdentified ? 0.03 : 0) + (e.Available ? 0.01 : 0);
        if (swMatch && hwMatch) return new DefinitionMatch(e, MatchLevel.Exact, 0.95 + bonus, reasons);
        if (swMatch && !hwKnown) return new DefinitionMatch(e, MatchLevel.Strong, 0.85 + bonus, reasons);
        if (swMatch) return new DefinitionMatch(e, MatchLevel.Probable, 0.65 + bonus, reasons);
        if (oemMatch) return new DefinitionMatch(e, MatchLevel.Probable, (otherSw ? 0.5 : 0.6) + (projectMatch ? 0.05 : 0) + bonus, reasons);
        // Same project, other SW version: structure is usually close, but addresses must be checked before use.
        if (projectMatch) return new DefinitionMatch(e, MatchLevel.Probable, 0.45 + bonus, [.. reasons, "SW version not confirmed: check addresses before use"]);
        if (familyExact || familyPartial)
            return new DefinitionMatch(e, MatchLevel.Weak, (familyExact ? 0.3 : 0.2) + (engineMatch ? 0.05 : 0) + bonus, reasons);
        return null;
    }
}
