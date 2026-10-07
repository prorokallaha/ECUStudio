using System.Text;
using System.Text.Json.Serialization;
using ECUStudio.Binary;
using ECUStudio.Calibration.Model;
using ECUStudio.Calibration.Plugins;
using ECUStudio.Calibration.Scanning;
using ECUStudio.Core;

namespace ECUStudio.Calibration.Library;

[JsonConverter(typeof(JsonStringEnumConverter<DefinitionFit>))]
public enum DefinitionFit
{
    /// <summary>Made for this software: identifiers agree and every map validates at its own address.</summary>
    Exact,
    /// <summary>Nearly every map validates (some may have been relocated).</summary>
    Compatible,
    /// <summary>Part of the maps validate; only those are used.</summary>
    Partial,
    Incompatible,
    Unknown,
}

/// <summary>A map found at another address than the definition states (other software version).</summary>
public sealed record MapRelocation(string MapId, string Name, int SourceDefinitionAddress, int ResolvedBinaryAddress, double MatchConfidence);

public sealed record InvalidMap(string MapId, string Name, string Reason);

/// <summary>Verification of a definition against one concrete binary. Only matched maps are used.</summary>
public sealed record DefinitionCompatibilityResult
{
    public required int Score { get; init; }
    public required DefinitionFit Status { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public IReadOnlyList<string> Conflicts { get; init; } = [];
    public int TotalMaps { get; init; }
    public int MatchedMaps { get; init; }
    public int InvalidMaps { get; init; }
    public int RelocatedMaps { get; init; }
    /// <summary>Tables and curves among the matched objects (more than one cell).</summary>
    public int Tables { get; init; }
    /// <summary>Single values among the matched objects.</summary>
    public int Parameters { get; init; }
    public int Axes { get; init; }
    public IReadOnlyList<MapRelocation> Relocations { get; init; } = [];
    /// <summary>First invalid maps with the reason (capped).</summary>
    public IReadOnlyList<InvalidMap> Invalid { get; init; } = [];

    public bool Usable => Status is DefinitionFit.Exact or DefinitionFit.Compatible or DefinitionFit.Partial;

    /// <summary>Summary in the older three-state form used by the import dialog.</summary>
    public CompatibilityReport ToReport() => new()
    {
        Status = Status switch
        {
            DefinitionFit.Exact or DefinitionFit.Compatible => CompatibilityStatus.Compatible,
            DefinitionFit.Incompatible => CompatibilityStatus.Incompatible,
            _ => CompatibilityStatus.Warning,
        },
        MapCount = TotalMaps, MapsDecoded = MatchedMaps, MapsOutOfRange = InvalidMaps,
        Reasons = [.. Evidence, .. Conflicts.Select(c => "conflict: " + c)],
    };
}

public sealed record VerifiedDefinition(DefinitionCompatibilityResult Result, ExternalDefinition Definition);

/// <summary>Resolves records whose axis point counts are stored in the binary.</summary>
public static class InlineRecordBinder
{
    /// <summary>Concrete definition for the record at <paramref name="recordAddress"/>, or null with the reason.</summary>
    public static MapDefinition? Bind(MapDefinition d, ReadOnlySpan<byte> data, int recordAddress, out string? problem)
    {
        problem = null;
        if (d.Record is not { } rec) return d;
        int? nx = null, ny = null;
        int? xAddr = null, yAddr = null, dataAddr = null;
        long pos = recordAddress;
        foreach (var item in rec.Items)
        {
            var size = item.Type.Size();
            switch (item.Kind)
            {
                case "NO_AXIS_PTS_X":
                case "NO_AXIS_PTS_Y":
                {
                    if (pos < 0 || pos + size > data.Length) { problem = "record outside the file"; return null; }
                    var n = (int)ValueReader.ReadRaw(data, (int)pos, item.Type, d.Endian);
                    if (item.Kind == "NO_AXIS_PTS_X") nx = n; else ny = n;
                    pos += size;
                    break;
                }
                case "AXIS_PTS_X":
                    xAddr = (int)pos;
                    pos += (long)size * (nx ?? rec.MaxCols);
                    break;
                case "AXIS_PTS_Y":
                    yAddr = (int)pos;
                    pos += (long)size * (ny ?? rec.MaxRows);
                    break;
                case "FNC_VALUES":
                    dataAddr = (int)pos;
                    pos += (long)size * (nx ?? rec.MaxCols) * (ny ?? rec.MaxRows);
                    break;
                default:
                    pos += size;
                    break;
            }
        }
        var cols = nx ?? rec.MaxCols;
        var rows = ny ?? rec.MaxRows;
        if (cols < 1 || cols > rec.MaxCols || rows < 1 || rows > rec.MaxRows)
        {
            problem = $"stored size {cols}×{rows} does not fit the definition maximum {rec.MaxCols}×{rec.MaxRows}";
            return null;
        }
        if (dataAddr is null || pos > data.Length) { problem = "record outside the file"; return null; }
        return d with
        {
            Address = dataAddr.Value,
            Cols = cols,
            Rows = rows,
            XAxis = d.XAxis is { } x && xAddr is { } xa ? x with { Address = xa, Length = cols } : d.XAxis,
            YAxis = d.YAxis is { } y && yAddr is { } ya ? y with { Address = ya, Length = rows } : d.YAxis,
            Record = rec with { Address = recordAddress },
        };
    }
}

/// <summary>
/// Checks a definition against a binary before it is used and, for another software version of the same project,
/// tries to find each map's equivalent in this binary. Evidence: identifiers (family, project, SW, OEM, version, EPK),
/// address ranges, axis monotonicity and declared physical limits, stored axis counts, and the relative layout
/// (address shifts voted by maps found with the same structure). The binary is only read.
/// </summary>
public static class DefinitionVerifier
{
    private const int MaxShift = 0x40000;

    private sealed record Check(MapDefinition? Bound, bool Ok, bool Strong, string? Reason);

    public static VerifiedDefinition Verify(ExternalDefinition definition, BinaryImage image, EcuIdentification ident,
        LibraryIdentifiers? definitionIds = null, IReadOnlyList<BoschMapScanner.RawMap>? scanned = null)
    {
        var ids = definitionIds ?? LibraryScanner.Identify(definition.Name);
        var evidence = new List<string>();
        var conflicts = new List<string>();
        var fatal = Identifiers(definition, image, ident, ids, evidence, conflicts, out var exactIds);

        var maps = definition.Maps;
        var checks = maps.Select(m => Validate(m, image, m.Record?.Address ?? m.Address)).ToList();
        var strongTotal = checks.Count(c => c.Strong);
        var strongOk = checks.Count(c => c.Strong && c.Ok);
        var relocations = new Dictionary<int, MapRelocation>();

        // Relocate when a noticeable part of the structurally checkable maps does not fit at the stated addresses.
        if (strongTotal > 0 && strongOk < strongTotal * 0.9)
        {
            scanned ??= BoschMapScanner.Scan(image.Span);
            var votes = Votes(maps, scanned);
            var global = votes.OrderByDescending(v => v.Value).Where(v => v.Value >= Math.Min(3, Math.Max(1, strongTotal / 10.0))).Take(6).ToList();
            var totalVotes = Math.Max(1, votes.Values.Sum());
            for (var i = 0; i < maps.Count; i++)
            {
                if (checks[i].Ok || !checks[i].Strong) continue;
                var source = maps[i].Record?.Address ?? maps[i].Address;
                foreach (var (delta, weight) in global)
                {
                    if (delta == 0) continue;
                    var c = Validate(maps[i], image, source + delta);
                    if (!c.Ok) continue;
                    checks[i] = c;
                    relocations[i] = new MapRelocation(maps[i].Id, maps[i].Name, source, source + delta, Math.Round(Math.Min(0.9, 0.6 + 0.3 * weight / totalVotes * global.Count), 2));
                    break;
                }
            }
            // Weakly checkable objects (single values, fixed axes) follow the shift of their nearest relocated neighbour.
            var anchors = relocations.Values.OrderBy(r => r.SourceDefinitionAddress).ToList();
            for (var i = 0; i < maps.Count && anchors.Count > 0; i++)
            {
                if (checks[i].Strong || relocations.ContainsKey(i)) continue;
                var source = maps[i].Record?.Address ?? maps[i].Address;
                var near = anchors.MinBy(a => Math.Abs(a.SourceDefinitionAddress - source))!;
                if (Math.Abs(near.SourceDefinitionAddress - source) > 0x2000) continue;
                var delta = near.ResolvedBinaryAddress - near.SourceDefinitionAddress;
                var c = Validate(maps[i], image, source + delta);
                if (!c.Ok) continue;
                checks[i] = c;
                relocations[i] = new MapRelocation(maps[i].Id, maps[i].Name, source, source + delta, Math.Round(near.MatchConfidence * 0.7, 2));
            }
            if (relocations.Count > 0)
                evidence.Add($"{relocations.Count} map(s) found at shifted addresses (relative layout preserved; most common shift {Hex(global.FirstOrDefault().Key)})");
        }

        var used = new List<MapDefinition>();
        var invalid = new List<InvalidMap>();
        for (var i = 0; i < maps.Count; i++)
        {
            var c = checks[i];
            if (c.Ok && c.Bound is { } b)
                used.Add(relocations.TryGetValue(i, out var r)
                    ? b with { SourceAddress = maps[i].Address, Confidence = Math.Min(b.Confidence, r.MatchConfidence) }
                    : b);
            else invalid.Add(new InvalidMap(maps[i].Id, maps[i].Name, c.Reason ?? "not valid in this binary"));
        }

        var total = maps.Count;
        var fraction = total == 0 ? 0 : (double)used.Count / total;
        var strongFraction = strongTotal == 0 ? fraction : (double)checks.Count(c => c.Strong && c.Ok) / strongTotal;
        var score = (int)Math.Round(100 * Math.Min(fraction, strongFraction));
        if (strongTotal > 0) evidence.Add($"{checks.Count(c => c.Strong && c.Ok)} of {strongTotal} tables have plausible axes{(maps.Any(m => m.Record is not null) ? " and stored sizes" : "")} in this binary");
        if (invalid.Count > 0) conflicts.Add($"{invalid.Count} of {total} object(s) do not fit this binary and are not used");

        var status = total == 0 ? DefinitionFit.Unknown
            : fatal || score < 40 ? DefinitionFit.Incompatible
            : exactIds && score >= 95 && relocations.Count == 0 ? DefinitionFit.Exact
            : score >= 85 ? DefinitionFit.Compatible
            : DefinitionFit.Partial;
        if (fatal) score = Math.Min(score, 10);

        var result = new DefinitionCompatibilityResult
        {
            Score = score, Status = status, Evidence = evidence, Conflicts = conflicts, TotalMaps = total, MatchedMaps = used.Count,
            InvalidMaps = invalid.Count, RelocatedMaps = relocations.Count,
            Tables = used.Count(m => m.Rows * m.Cols > 1), Parameters = used.Count(m => m.Rows * m.Cols == 1), Axes = definition.AxisCount,
            Relocations = relocations.Values.OrderBy(r => r.SourceDefinitionAddress).ToList(), Invalid = invalid.Take(100).ToList(),
        };
        return new VerifiedDefinition(result, definition with { Maps = used });
    }

    /// <summary>Identifier evidence. Returns true when a conflict rules the definition out (another ECU family).</summary>
    private static bool Identifiers(ExternalDefinition def, BinaryImage image, EcuIdentification ident, LibraryIdentifiers ids,
        List<string> evidence, List<string> conflicts, out bool exact)
    {
        exact = false;
        var fatal = false;
        var family = DefinitionMatcher.NormalizeFamily(ident.EcuFamily);
        if (family is not null && ids.EcuFamilies.Count > 0)
        {
            if (ids.EcuFamilies.Any(f => DefinitionMatcher.NormalizeFamily(f) is { } nf && (nf == family || family.StartsWith(nf, StringComparison.Ordinal) || nf.StartsWith(family, StringComparison.Ordinal))))
                evidence.Add($"ECU family {ident.EcuFamily}");
            else { conflicts.Add($"definition is for {string.Join(", ", ids.EcuFamilies)}, binary is {ident.EcuFamily}"); fatal = true; }
        }
        if (Known(ident.ProjectCode) is { } project && ids.ProjectCodes.Count > 0)
        {
            if (ids.ProjectCodes.Contains(project)) evidence.Add($"Bosch project {project}");
            else conflicts.Add($"definition project {string.Join(", ", ids.ProjectCodes)}, binary project {project}");
        }
        var swMatch = false;
        if (Known(ident.SoftwareNumber) is { } sw && ids.SoftwareNumbers.Count > 0)
        {
            swMatch = ids.SoftwareNumbers.Contains(sw);
            if (swMatch) evidence.Add($"SW {sw}");
            else conflicts.Add($"definition SW {string.Join(", ", ids.SoftwareNumbers)}, binary SW {sw}: addresses are checked map by map");
        }
        if (Known(ident.OemPartNumber) is { } oem && ids.OemNumbers.Count > 0)
        {
            if (ids.OemNumbers.Contains(LibraryScanner.NormalizeOem(oem))) evidence.Add($"OEM software part {oem}");
            else conflicts.Add($"definition OEM part {string.Join(", ", ids.OemNumbers)}, binary {oem}");
        }
        var versionMatch = false;
        if (Known(ident.SoftwareVersion) is { } version && ids.SoftwareVersions.Count > 0)
        {
            versionMatch = ids.SoftwareVersions.Contains(version);
            if (versionMatch) evidence.Add($"software version {version}");
            else conflicts.Add($"definition version {string.Join(", ", ids.SoftwareVersions)}, binary version {version}");
        }
        var epkMatch = false;
        if (def.Epk is { Length: > 0 } epk)
        {
            var at = def.EpkAddress is { } a && a >= 0 && a + epk.Length <= image.Length ? Encoding.ASCII.GetString(image.Span.Slice(a, epk.Length)) : null;
            if (at == epk) { epkMatch = true; evidence.Add($"EPK \"{epk}\" found at its address 0x{def.EpkAddress:X}"); }
            else if (image.Span.IndexOf(Encoding.ASCII.GetBytes(epk)) is var pos and >= 0) { epkMatch = true; evidence.Add($"EPK \"{epk}\" found in the binary at 0x{pos:X}"); }
            else conflicts.Add($"EPK \"{epk}\" of the definition is not in the binary{(at is not null ? $" (binary has \"{Printable(at)}\")" : "")}");
        }
        var highest = def.Maps.Count == 0 ? 0 : def.Maps.Max(m => (long)(m.Record?.Address ?? m.Address) + m.ByteLength);
        if (highest > image.Length) conflicts.Add($"definition addresses reach 0x{highest:X}, the file is 0x{image.Length:X} bytes");
        else if (def.Maps.Count > 0) evidence.Add($"all addresses within the {image.Length / 1024} KB file");
        exact = epkMatch || (swMatch && (versionMatch || ids.SoftwareVersions.Count == 0));
        return fatal;
    }

    /// <summary>Address shift votes: maps with stored sizes against scanned records of a fitting size.</summary>
    private static Dictionary<int, double> Votes(IReadOnlyList<MapDefinition> maps, IReadOnlyList<BoschMapScanner.RawMap> scanned)
    {
        var votes = new Dictionary<int, double>();
        var headers = scanned.OrderBy(s => s.HeaderAddress).ToList();
        foreach (var m in maps)
        {
            if (m.Record is not { } rec || m.Rows < 2 && rec.MaxRows < 2) continue;
            var fits = headers.Where(h => Math.Abs(h.HeaderAddress - rec.Address) <= MaxShift && h.Cols <= rec.MaxCols && h.Rows <= rec.MaxRows
                && h.Cols >= Math.Min(rec.MaxCols, 2) && rec.MaxRows >= 2).ToList();
            if (fits.Count == 0) continue;
            var w = 1.0 / fits.Count;
            foreach (var h in fits)
            {
                var d = h.HeaderAddress - rec.Address;
                votes[d] = votes.GetValueOrDefault(d) + w;
            }
        }
        return votes;
    }

    private static Check Validate(MapDefinition m, BinaryImage image, int address)
    {
        MapDefinition? bound;
        string? problem = null;
        if (m.Record is not null) bound = InlineRecordBinder.Bind(m, image.Span, address, out problem);
        else
        {
            var delta = address - m.Address;
            bound = delta == 0 ? m : m with
            {
                Address = address,
                XAxis = m.XAxis is { Address: { } xa } x ? x with { Address = xa + delta } : m.XAxis,
                YAxis = m.YAxis is { Address: { } ya } y ? y with { Address = ya + delta } : m.YAxis,
            };
        }
        var strong = m.XAxis?.Address is not null || m.YAxis?.Address is not null || m.Record is not null;
        if (bound is null) return new(null, false, strong, problem);
        CalibrationMap map;
        try { map = MapDecoder.Decode(image, bound); }
        catch (DefinitionException ex) { return new(bound, false, strong, ex.Message); }

        if (bound.XAxis is { Address: not null } && !Rising(map.XAxis)) return new(bound, false, strong, "X axis does not rise");
        if (bound.YAxis is { Address: not null } && !Rising(map.YAxis)) return new(bound, false, strong, "Y axis does not rise");
        if (bound.XAxis is { Address: not null } xd && !Within(map.XAxis, xd.LowerLimit, xd.UpperLimit, 1.0)) return new(bound, false, strong, "X axis outside its declared limits");
        if (bound.YAxis is { Address: not null } yd && !Within(map.YAxis, yd.LowerLimit, yd.UpperLimit, 1.0)) return new(bound, false, strong, "Y axis outside its declared limits");
        if (!Within(map.Values, bound.LowerLimit, bound.UpperLimit, 0.95)) return new(bound, false, strong, "values outside the declared limits");
        return new(bound, true, strong, null);
    }

    private static bool Rising(double[] axis)
    {
        for (var i = 1; i < axis.Length; i++) if (!(axis[i] > axis[i - 1])) return false;
        return true;
    }

    private static bool Within(double[] values, double? lo, double? hi, double required)
    {
        if (lo is null && hi is null || values.Length == 0) return true;
        var span = (hi ?? 0) - (lo ?? 0);
        var eps = Math.Abs(span) * 1e-6 + 1e-9;
        var inside = values.Count(v => (lo is null || v >= lo - eps) && (hi is null || v <= hi + eps));
        return inside >= values.Length * required;
    }

    private static string? Known(Param p) => p.IsKnown ? p.Text : null;
    private static string Hex(int v) => v < 0 ? $"-0x{-v:X}" : $"+0x{v:X}";
    private static string Printable(string s) => new(s.Select(c => c is >= ' ' and < (char)0x7F ? c : '.').ToArray());
}
