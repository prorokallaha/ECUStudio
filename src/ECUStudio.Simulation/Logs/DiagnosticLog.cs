using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ECUStudio.Core;

namespace ECUStudio.Simulation.Logs;

/// <summary>Physical channels the validator understands. Everything else in a log is ignored (and listed as unmapped).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LogChannel>))]
public enum LogChannel
{
    Time,
    Rpm,
    Pedal,
    BoostActual,
    BoostSpecified,
    AtmosphericPressure,
    MafActual,
    MafSpecified,
    IqActual,
    IqRequested,
    IntakeTemp,
    CoolantTemp,
    Egt,
}

public sealed record LogColumn(LogChannel Channel, string Header, string Unit, int SourceIndex);

/// <summary>
/// A parsed diagnostic log in canonical units: rpm, %, mbar (absolute), mg/stroke, °C, s.
/// Values are column arrays aligned by sample index; NaN marks a missing cell.
/// </summary>
public sealed class DiagnosticLog
{
    public required string Name { get; init; }
    public required string Format { get; init; }
    public required int SampleCount { get; init; }
    public required IReadOnlyDictionary<LogChannel, double[]> Columns { get; init; }
    public required IReadOnlyList<LogColumn> Mapping { get; init; }
    public IReadOnlyList<string> UnmappedHeaders { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool Has(LogChannel c) => Columns.ContainsKey(c);
    public double[]? this[LogChannel c] => Columns.GetValueOrDefault(c);
}

/// <summary>
/// Parses VCDS measuring-block CSV exports and generic CSV logs (one header row, optional
/// qualifier/unit rows). Units are converted only when recognised; an unrecognised unit drops
/// the channel with a warning instead of guessing.
/// </summary>
public static partial class LogParser
{
    public const int MaxBytes = 16 * 1024 * 1024;

    private sealed record Alias(LogChannel Channel, string[] All, string[]? Any = null, string[]? None = null);

    // Order matters: specific (specified/requested) aliases are tried before generic ones.
    private static readonly Alias[] Aliases =
    [
        new(LogChannel.Time, ["time"], None: ["injection", "duration"]),
        new(LogChannel.Time, ["stamp"]),
        new(LogChannel.Time, ["zeit"]),
        new(LogChannel.Rpm, ["engine speed"]),
        new(LogChannel.Rpm, ["motordrehzahl"]),
        new(LogChannel.Rpm, ["rpm"]),
        new(LogChannel.Rpm, ["nmot"]),
        new(LogChannel.Pedal, ["pedal"]),
        new(LogChannel.Pedal, ["accelerator"]),
        new(LogChannel.Pedal, ["fahrpedal"]),
        new(LogChannel.Pedal, ["throttle"]),
        new(LogChannel.BoostSpecified, ["boost"], ["specified", "requested", "target", "setpoint"]),
        new(LogChannel.BoostSpecified, ["ladedruck"], ["soll"]),
        new(LogChannel.BoostActual, ["boost"], None: ["duty", "valve", "limit", "control"]),
        new(LogChannel.BoostActual, ["ladedruck"], None: ["steller", "tastverh"]),
        new(LogChannel.BoostActual, ["manifold", "pressure"]),
        new(LogChannel.BoostActual, ["map"], ["actual", "abs"]),
        new(LogChannel.AtmosphericPressure, ["atmospheric"]),
        new(LogChannel.AtmosphericPressure, ["ambient", "pressure"]),
        new(LogChannel.AtmosphericPressure, ["umgebungsdruck"]),
        new(LogChannel.AtmosphericPressure, ["baro"]),
        new(LogChannel.MafSpecified, ["air mass"], ["specified", "requested", "target"]),
        new(LogChannel.MafSpecified, ["luftmasse"], ["soll"]),
        new(LogChannel.MafActual, ["air mass"]),
        new(LogChannel.MafActual, ["luftmasse"]),
        new(LogChannel.MafActual, ["maf"]),
        new(LogChannel.IqRequested, ["injection quantity"], ["requested", "driver", "specified", "wunsch"]),
        new(LogChannel.IqActual, ["injection quantity"], None: ["limit", "smoke", "torque", "deviation", "cyl"]),
        new(LogChannel.IqActual, ["einspritzmenge"], None: ["begrenz", "abweich", "zyl"]),
        new(LogChannel.IqActual, ["fuel quantity"]),
        new(LogChannel.IqActual, ["iq"], None: ["limit", "smoke"]),
        new(LogChannel.IntakeTemp, ["intake", "temp"]),
        new(LogChannel.IntakeTemp, ["ansaugluft"]),
        new(LogChannel.IntakeTemp, ["iat"]),
        new(LogChannel.CoolantTemp, ["coolant"]),
        new(LogChannel.CoolantTemp, ["kühlmittel"]),
        new(LogChannel.CoolantTemp, ["kuehlmittel"]),
        new(LogChannel.Egt, ["egt"]),
        new(LogChannel.Egt, ["exhaust", "temp"]),
        new(LogChannel.Egt, ["abgastemp"]),
    ];

    /// <summary>VCDS writes Windows-1252; anything that is not valid UTF-8 is read as Latin-1 so umlauts in German labels survive.</summary>
    public static string Decode(byte[] content)
    {
        try { return new System.Text.UTF8Encoding(false, true).GetString(content).TrimStart('\uFEFF'); }
        catch (System.Text.DecoderFallbackException) { return System.Text.Encoding.Latin1.GetString(content); }
    }

    public static DiagnosticLog Parse(string name, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new LogFormatException("Log is empty");
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var delimiter = DetectDelimiter(lines);
        var rows = lines.Select(l => SplitCsv(l, delimiter)).ToList();
        var isVcds = lines.Take(5).Any(l => l.Contains("VCID", StringComparison.OrdinalIgnoreCase) || l.Contains("Group A", StringComparison.OrdinalIgnoreCase)
                                           || l.Contains("VCDS", StringComparison.OrdinalIgnoreCase));

        // Header = row (within the first 40) that maps the most channels.
        int headerRow = -1, bestScore = 1;
        for (var i = 0; i < Math.Min(40, rows.Count); i++)
        {
            var score = rows[i].Select(c => Classify(c)).Where(c => c is not null).Distinct().Count();
            if (score > bestScore) { bestScore = score; headerRow = i; }
        }
        if (headerRow < 0) throw new LogFormatException("No recognisable header: expected columns such as engine speed / boost pressure / air mass / injection quantity");

        // Qualifier and unit rows: non-numeric rows right below the header (VCDS: "Specified"/"Actual", "/min", "mbar").
        var dataStart = headerRow + 1;
        var qualifiers = new List<string[]>();
        while (dataStart < rows.Count && qualifiers.Count < 3 && !IsDataRow(rows[dataStart]))
        {
            qualifiers.Add(rows[dataStart]);
            dataStart++;
        }

        var header = rows[headerRow];
        var width = rows.Skip(dataStart).Where(IsDataRow).Select(r => r.Length).DefaultIfEmpty(header.Length).Max();
        var names = new string[width];
        var headerUnits = new string?[width];
        for (var c = 0; c < width; c++)
        {
            var parts = new List<string> { Cell(header, c) };
            foreach (var q in qualifiers)
            {
                var cell = Cell(q, c);
                if (UnitOf(cell) is { } u && headerUnits[c] is null) headerUnits[c] = u;
                else parts.Add(cell);
            }
            names[c] = string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p))).Trim();
            headerUnits[c] ??= UnitInBrackets(names[c]);
        }

        var dataRows = rows.Skip(dataStart).Where(IsDataRow).ToList();
        if (dataRows.Count == 0) throw new LogFormatException("Log has a header but no numeric rows");

        var warnings = new List<string>();
        var mapping = new List<LogColumn>();
        var columns = new Dictionary<LogChannel, double[]>();
        var unmapped = new List<string>();
        for (var c = 0; c < width; c++)
        {
            if (string.IsNullOrWhiteSpace(names[c])) continue;
            var channel = Classify(names[c]);
            if (channel is null) { unmapped.Add(names[c]); continue; }
            if (columns.ContainsKey(channel.Value))
            {
                if (channel != LogChannel.Time) warnings.Add($"Column '{names[c]}' also maps to {channel}; the first one is used");
                continue;
            }

            var raw = new double[dataRows.Count];
            string? valueUnit = null;
            for (var r = 0; r < dataRows.Count; r++)
            {
                var (v, u) = ParseValue(Cell(dataRows[r], c), delimiter);
                raw[r] = v;
                valueUnit ??= u;
            }
            if (raw.All(double.IsNaN)) { warnings.Add($"Column '{names[c]}' has no numeric values"); continue; }

            var unit = (headerUnits[c] ?? valueUnit ?? "").Trim();
            var converted = Convert(channel.Value, raw, unit, out var canonicalUnit, out var note);
            if (note is not null) warnings.Add($"{channel} ('{names[c]}'): {note}");
            if (converted is null) continue;
            columns[channel.Value] = converted;
            mapping.Add(new LogColumn(channel.Value, names[c], canonicalUnit, c));
        }

        if (!columns.ContainsKey(LogChannel.Rpm)) throw new LogFormatException("Log has no engine speed column; it cannot be matched to operating points");
        if (columns.Count < 2) throw new LogFormatException("Log has engine speed only; nothing to validate against");

        return new DiagnosticLog
        {
            Name = name, Format = isVcds ? "VCDS" : "CSV", SampleCount = dataRows.Count,
            Columns = columns, Mapping = mapping, UnmappedHeaders = unmapped, Warnings = warnings,
        };
    }

    internal static LogChannel? Classify(string header)
    {
        var n = Norm(header);
        if (n.Length == 0) return null;
        foreach (var a in Aliases)
        {
            if (!a.All.All(n.Contains)) continue;
            if (a.Any is not null && !a.Any.Any(n.Contains)) continue;
            if (a.None is not null && a.None.Any(n.Contains)) continue;
            // Short tokens must be whole words ("iq" inside "liquid", "map" inside "mapping").
            if (a.All.Any(t => t.Length <= 3 && !Regex.IsMatch(n, $@"(^|[^a-z]){Regex.Escape(t)}([^a-z]|$)"))) continue;
            return a.Channel;
        }
        return null;
    }

    private static double[]? Convert(LogChannel ch, double[] v, string unit, out string canonical, out string? note)
    {
        note = null;
        var u = unit.ToLowerInvariant().Replace(" ", "");
        var median = Median(v);
        switch (ch)
        {
            case LogChannel.Rpm:
                canonical = "rpm";
                return v;
            case LogChannel.Pedal:
                canonical = "%";
                return v;
            case LogChannel.BoostActual or LogChannel.BoostSpecified or LogChannel.AtmosphericPressure:
                canonical = "mbar";
                if (u is "mbar" or "hpa") return v;
                if (u == "bar") return Scale(v, 1000);
                if (u == "kpa") return Scale(v, 10);
                if (u == "psi") { note = "psi is ambiguous (gauge or absolute); channel ignored"; return null; }
                if (u.Length > 0) { note = $"unknown pressure unit '{unit}'; channel ignored"; return null; }
                // No unit: infer from magnitude of an absolute pressure.
                if (median is > 500 and < 4000) { note = "no unit in log; values treated as mbar"; return v; }
                if (median is > 0.5 and < 4) { note = "no unit in log; values treated as bar"; return Scale(v, 1000); }
                if (median is > 50 and < 400) { note = "no unit in log; values treated as kPa"; return Scale(v, 10); }
                note = "no unit and implausible magnitude; channel ignored";
                return null;
            case LogChannel.MafActual or LogChannel.MafSpecified or LogChannel.IqActual or LogChannel.IqRequested:
                canonical = "mg/stroke";
                if (u.Length == 0 || u is "mg/str" or "mg/stroke" or "mg/h" or "mg/hub" or "mg/stk" or "mg/cyl" or "mg") return v;
                if (u is "g/s" or "kg/h") { canonical = u; note = $"{unit} needs cylinder count to convert to mg/stroke; converted during validation"; return v; }
                note = $"unknown unit '{unit}'; channel ignored";
                return null;
            case LogChannel.IntakeTemp or LogChannel.CoolantTemp or LogChannel.Egt:
                canonical = "°C";
                if (u is "°f" or "f" or "degf") return v.Select(x => (x - 32) * 5 / 9).ToArray();
                if (u is "k") return v.Select(x => x - 273.15).ToArray();
                return v;
            case LogChannel.Time:
                canonical = "s";
                if (u is "ms") return Scale(v, 0.001);
                return v;
            default:
                canonical = unit;
                return v;
        }
    }

    internal static double Median(IEnumerable<double> values)
    {
        var a = values.Where(double.IsFinite).OrderBy(x => x).ToArray();
        return a.Length == 0 ? double.NaN : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    private static double[] Scale(double[] v, double k) => v.Select(x => x * k).ToArray();

    private static string Norm(string s) => WhitespaceRegex().Replace(s.ToLowerInvariant().Replace('_', ' ').Replace('-', ' '), " ").Trim();

    private static string Cell(string[] row, int i) => i < row.Length ? row[i].Trim() : "";

    private static bool IsDataRow(string[] row)
    {
        var numeric = row.Count(c => LeadingNumber().IsMatch(c.Trim()));
        return numeric >= 2 && numeric * 2 >= row.Count(c => !string.IsNullOrWhiteSpace(c));
    }

    private static (double Value, string? Unit) ParseValue(string cell, char delimiter)
    {
        var m = LeadingNumber().Match(cell);
        if (!m.Success) return (double.NaN, null);
        var num = m.Groups[1].Value;
        if (delimiter != ',' && num.Contains(',') && !num.Contains('.')) num = num.Replace(',', '.');
        if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return (double.NaN, null);
        var rest = cell[m.Length..].Trim();
        return (v, rest.Length > 0 ? rest : null);
    }

    private static string? UnitOf(string cell)
    {
        var c = cell.Trim().ToLowerInvariant();
        return c is "/min" or "rpm" or "mbar" or "bar" or "kpa" or "hpa" or "psi" or "mg/str" or "mg/h" or "mg/hub" or "mg/stroke" or "g/s" or "kg/h"
            or "%" or "°c" or "c" or "°f" or "k" or "s" or "ms" or "°kw" or "°ca" ? cell.Trim() : null;
    }

    private static string? UnitInBrackets(string name)
    {
        var m = BracketUnit().Match(name);
        return m.Success ? UnitOf(m.Groups[1].Value) : null;
    }

    private static char DetectDelimiter(string[] lines)
    {
        var sample = lines.Take(40).Where(l => l.Length > 0).ToArray();
        char best = ',';
        var bestScore = -1;
        foreach (var d in new[] { ',', ';', '\t' })
        {
            var counts = sample.Select(l => l.Count(ch => ch == d)).Where(c => c > 0).ToArray();
            if (counts.Length == 0) continue;
            var mode = counts.GroupBy(c => c).OrderByDescending(g => g.Count()).First();
            var score = mode.Count() * mode.Key;
            if (score > bestScore) { bestScore = score; best = d; }
        }
        return best;
    }

    private static string[] SplitCsv(string line, char d)
    {
        if (!line.Contains('"')) return line.Split(d);
        var cells = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else quoted = !quoted; }
            else if (ch == d && !quoted) { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        cells.Add(sb.ToString());
        return cells.ToArray();
    }

    [GeneratedRegex(@"^\s*([-+]?\d+(?:[.,]\d+)?(?:[eE][-+]?\d+)?)")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[\[(]\s*([^\])]+?)\s*[\])]")]
    private static partial Regex BracketUnit();
}

public sealed class LogFormatException(string message) : EcuStudioException("LOG_FORMAT", message, 422);
