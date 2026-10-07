using System.Text;
using System.Text.RegularExpressions;
using ECUStudio.Binary;
using ECUStudio.Calibration.Definitions;
using ECUStudio.Calibration.Model;
using ECUStudio.Calibration.Scanning;
using ECUStudio.Core;

namespace ECUStudio.Calibration.Plugins.Edc16U34;

/// <summary>
/// Bosch EDC16U34 (VAG 1.9/2.0 TDI PD, unit injectors). MPC5xx → big-endian 16-bit cells.
/// Definition priority: user definition → Definition DB (by SW number) → signature scan.
/// </summary>
public sealed partial class Edc16U34Plugin(DefinitionDatabase definitionDb, double scanRoleThreshold = 0.6) : IEcuPlugin
{
    public const string Id = "edc16u34";
    private static readonly int[] KnownSizes = [0x80000, 0x100000];

    public string PluginId => Id;
    public string DisplayName => "Bosch EDC16U34";
    public IReadOnlyList<string> EngineFamilies { get; } = ["VAG_PD_19", "VAG_PD_20"];
    public bool HasCommonRail => false;

    [GeneratedRegex(@"0281\d{6}")] private static partial Regex HwRegex();
    [GeneratedRegex(@"1037\d{6}")] private static partial Regex SwRegex();
    [GeneratedRegex(@"0[0-9A-Z]{2}906021[A-Z]{0,3}")] private static partial Regex OemRegex();
    [GeneratedRegex(@"R4 ?1[,.]9L?[^\x00]{0,20}")] private static partial Regex EngineRegex();

    public DetectionResult Detect(BinaryImage image)
    {
        var reasons = new List<string>();
        double score = 0;
        if (KnownSizes.Contains(image.Length)) { score += 0.2; reasons.Add($"size 0x{image.Length:X} matches EDC16U34 flash"); }
        else reasons.Add($"size 0x{image.Length:X} is not a known EDC16U34 size");

        var text = Ascii(image.Span);
        if (text.Contains("EDC16U34", StringComparison.Ordinal)) { score += 0.45; reasons.Add("ASCII marker 'EDC16U34'"); }
        else if (text.Contains("EDC16", StringComparison.Ordinal)) { score += 0.15; reasons.Add("ASCII marker 'EDC16' (variant not stated)"); }
        if (OemRegex().IsMatch(text)) { score += 0.2; reasons.Add("VAG part number pattern 0xx906021*"); }
        if (SwRegex().IsMatch(text)) { score += 0.1; reasons.Add("Bosch SW number pattern 1037xxxxxx"); }
        if (HwRegex().IsMatch(text)) { score += 0.05; reasons.Add("Bosch HW number pattern 0281xxxxxx"); }
        return new DetectionResult(Id, Math.Min(1, score), reasons);
    }

    public EcuIdentification Identify(BinaryImage image)
    {
        var text = Ascii(image.Span);
        var detection = Detect(image);
        var notes = new List<string>(detection.Reasons);
        var sections = EstimateSections(image);
        Param Match(Regex r, double conf) => r.Match(text) is { Success: true } m ? Param.Str(m.Value.Trim(), SourceType.EcuBinary, conf) : Param.Unknown();

        return new EcuIdentification
        {
            PluginId = Id,
            EcuFamily = "Bosch EDC16U34",
            Manufacturer = "Bosch",
            HardwareNumber = Match(HwRegex(), 0.85),
            BoschNumber = Match(HwRegex(), 0.85),
            SoftwareNumber = Match(SwRegex(), 0.85),
            OemPartNumber = Match(OemRegex(), 0.85),
            EngineCode = Match(EngineRegex(), 0.6),
            Processor = "Freescale MPC5xx (PowerPC), big-endian",
            Endianness = Endianness.Big,
            FlashSize = image.Length,
            Sections = sections,
            Confidence = Math.Round(detection.Score, 2),
            Notes = notes,
        };
    }

    public DefinitionResolution ResolveDefinitions(BinaryImage image, EcuIdentification identification, ExternalDefinition? external)
    {
        var notes = new List<string>();
        List<MapDefinition> definitions;
        string source;
        IReadOnlyList<ChecksumSpec> checksums = [];

        if (external is not null)
        {
            definitions = external.Maps.ToList();
            source = $"{external.Source}: {external.Name}";
            notes.AddRange(external.Notes);
            checksums = external.Checksums;
        }
        else if (definitionDb.Find(Id, identification.SoftwareNumber.Text) is { } file)
        {
            definitions = file.ToDefinitions(SourceType.DefinitionDb).ToList();
            source = $"Definition DB: {file.Title ?? identification.SoftwareNumber.Text}";
            checksums = file.ToChecksumSpecs();
            if (file.Synthetic) notes.Add("Definition is SYNTHETIC (test/demo data), not a real vehicle definition.");
        }
        else
        {
            definitions = [];
            source = "Signature scan only";
            notes.Add("No definition for this software number: maps come from structure scanning and are treated as candidates.");
        }

        var raw = BoschMapScanner.Scan(image.Span);
        var candidates = new List<MapCandidate>();
        var index = 0;
        foreach (var r in raw)
        {
            if (definitions.Any(d => Overlaps(d, r))) continue;
            var xg = SignatureClassifier.ClassifyAxis(r.X);
            var yg = SignatureClassifier.ClassifyAxis(r.Y);
            candidates.Add(new MapCandidate
            {
                Id = $"cand_{++index:D3}",
                Address = r.DataAddress,
                HeaderAddress = r.HeaderAddress,
                Rows = r.Rows,
                Cols = r.Cols,
                XAxisRaw = r.X,
                YAxisRaw = r.Y,
                RawMin = r.Z.Min(),
                RawMax = r.Z.Max(),
                XAxisGuesses = xg,
                YAxisGuesses = yg,
                Hypotheses = SignatureClassifier.Hypotheses(r, xg, yg),
            });
        }

        // Use a scanned map in simulation only when its best hypothesis is strong, the role is not
        // covered by a better source, and no other candidate competes for the same role.
        foreach (var group in candidates.Where(c => c.Best is { Role: not MapRole.Unknown } b && b.Confidence >= scanRoleThreshold).GroupBy(c => c.Best!.Role))
        {
            if (definitions.Any(d => d.Role == group.Key)) continue;
            var ordered = group.OrderByDescending(c => c.Best!.Confidence).ToList();
            if (ordered.Count > 1 && ordered[1].Best!.Confidence > ordered[0].Best!.Confidence - 0.1)
            {
                notes.Add($"{group.Key}: {ordered.Count} competing candidates; none used automatically.");
                continue;
            }
            definitions.Add(FromCandidate(ordered[0], group.Key));
            notes.Add($"{group.Key} taken from signature scan candidate {ordered[0].Id} (confidence {ordered[0].Best!.Confidence:0.00}); unconfirmed.");
        }

        return new DefinitionResolution(definitions, candidates, source, notes) { Checksums = checksums };
    }

    public ChecksumReport VerifyChecksums(BinaryImage image, IReadOnlyList<ChecksumSpec> blocks) => blocks.Count == 0
        ? new(ChecksumStatus.NotImplemented, [],
            "No checksum blocks are described for this software version, and EDC16U34 checksum locations are not guessed. Add them to the definition to verify. ECUStudio analyses files; it does not prepare them for flashing.")
        : ChecksumVerifier.Verify(image.Span, blocks, "EDC16U34, blocks from the definition");

    public static MapDefinition FromCandidate(MapCandidate c, MapRole role)
    {
        var (factor, unit) = ValueScaling(role);
        var x = c.XAxisGuesses.FirstOrDefault();
        var y = c.YAxisGuesses.FirstOrDefault(g => ExpectedY(role) == g.Quantity) ?? c.YAxisGuesses.FirstOrDefault();
        return new MapDefinition
        {
            Id = $"scan_{role.ToString().ToLowerInvariant()}_{c.Address:X6}",
            Name = $"{role.DisplayName()} (scanned)",
            Role = role,
            Address = c.Address,
            Rows = c.Rows,
            Cols = c.Cols,
            DataType = DataType.UInt16,
            Endian = Endianness.Big,
            Factor = factor,
            Unit = unit,
            XAxis = new AxisDefinition { Name = x?.Quantity.ToString() ?? "X", Unit = x?.Unit ?? "-", Quantity = x?.Quantity ?? AxisQuantity.Unknown, Address = c.XAxisAddress, Length = c.Cols, Factor = x?.Factor ?? 1 },
            YAxis = new AxisDefinition { Name = y?.Quantity.ToString() ?? "Y", Unit = y?.Unit ?? "-", Quantity = y?.Quantity ?? AxisQuantity.Unknown, Address = c.YAxisAddress, Length = c.Rows, Factor = y?.Factor ?? 1 },
            Source = SourceType.SignatureScan,
            Confidence = c.ConfirmedRole is not null ? 0.8 : c.Best?.Confidence ?? 0,
            Description = "Detected by Bosch header scan; role inferred from axis/value signature.",
        };
    }

    /// <summary>Assumed EDC16 scaling (open tuning community conventions). Overridden by DAMOS/XDF.</summary>
    public static (double Factor, string Unit) ValueScaling(MapRole role) => role switch
    {
        MapRole.DriverWish or MapRole.TorqueLimiter or MapRole.GearTorqueLimiter => (0.1, "Nm"),
        MapRole.TorqueToIq or MapRole.SmokeLimiter => (0.01, "mg/stroke"),
        MapRole.BoostTarget or MapRole.BoostLimiter or MapRole.Svbl => (1, "mbar"),
        MapRole.Soi => (0.01, "°BTDC"),
        MapRole.Duration => (0.01, "°CA"),
        MapRole.VntDuty => (0.01, "%"),
        _ => (1, "raw"),
    };

    private static AxisQuantity ExpectedY(MapRole role) => role switch
    {
        MapRole.DriverWish => AxisQuantity.PedalPosition,
        MapRole.TorqueLimiter or MapRole.BoostLimiter => AxisQuantity.AtmosphericPressure,
        MapRole.TorqueToIq => AxisQuantity.Torque,
        MapRole.SmokeLimiter => AxisQuantity.AirMass,
        MapRole.GearTorqueLimiter => AxisQuantity.Gear,
        _ => AxisQuantity.InjectionQuantity,
    };

    private static bool Overlaps(MapDefinition d, BoschMapScanner.RawMap r) =>
        d.Address < r.DataAddress + r.Z.Length * 2 && r.HeaderAddress < d.Address + d.ByteLength;

    /// <summary>
    /// Section estimate from content: 4 KB fill blocks are Empty; the span covering detected maps
    /// is Calibration; the rest is Code. Calculated, not read from a memory layout document.
    /// </summary>
    private static List<MemorySection> EstimateSections(BinaryImage image)
    {
        var span = image.Span;
        var maps = BoschMapScanner.Scan(span);
        const int block = 0x1000;
        int calStart = -1, calEnd = -1;
        if (maps.Count > 0)
        {
            calStart = maps.Min(m => m.HeaderAddress) & ~0xFFFF;
            calEnd = Math.Min(image.Length, (maps.Max(m => m.DataAddress + m.Z.Length * 2) + 0xFFFF) & ~0xFFFF);
        }
        var sections = new List<MemorySection>();
        SectionKind? current = null;
        var start = 0;
        for (var off = 0; off < image.Length; off += block)
        {
            var len = Math.Min(block, image.Length - off);
            SectionKind kind = Checksums.IsFill(span.Slice(off, len)) ? SectionKind.Empty
                : off >= calStart && off < calEnd ? SectionKind.Calibration
                : SectionKind.Code;
            if (current != kind)
            {
                if (current is { } k) sections.Add(Section(k, start, off));
                current = kind;
                start = off;
            }
        }
        if (current is { } last) sections.Add(Section(last, start, image.Length));
        return sections;

        static MemorySection Section(SectionKind k, int s, int e) => new(
            k.ToString(), s, e, k, SourceType.Calculated, k == SectionKind.Empty ? 0.9 : 0.4,
            k == SectionKind.Empty ? "Fill bytes" : "Estimated from content, not from a memory layout document");
    }

    private static string Ascii(ReadOnlySpan<byte> data)
    {
        var chars = new char[data.Length];
        for (var i = 0; i < data.Length; i++) chars[i] = data[i] is >= 0x20 and < 0x7F ? (char)data[i] : '\0';
        return new string(chars);
    }
}
