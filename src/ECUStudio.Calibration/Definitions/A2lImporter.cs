using System.Globalization;
using System.Text;
using ECUStudio.Binary;
using ECUStudio.Calibration.Model;
using ECUStudio.Calibration.Plugins;
using ECUStudio.Core;

namespace ECUStudio.Calibration.Definitions;

/// <summary>
/// ASAM MCD-2 MC (A2L) importer, calibration subset: CHARACTERISTIC (VALUE, CURVE, MAP, VAL_BLK) and AXIS_PTS
/// with RECORD_LAYOUT, COMPU_METHOD (IDENTICAL, LINEAR, linear RAT_FUNC), STD_AXIS / COM_AXIS / FIX_AXIS and
/// MOD_COMMON byte order. Anything outside that subset (non-linear or verbal conversions, CUBOID, 64-bit types,
/// rescale axes, pointer addressing) is skipped per characteristic and listed in the notes, never approximated.
/// Axis point counts are taken from MAX_AXIS_POINTS; when the layout stores the count in the binary
/// (NO_AXIS_PTS_X/Y) the decoder later checks the stored axis against the map size. Items are assumed packed
/// (no ALIGNMENT padding), which holds for Bosch EDC16 layouts. ECU addresses above 16 MB are rebased to
/// file offsets by subtracting their common top byte.
/// </summary>
public sealed class A2lImporter : IDefinitionImporter
{
    public SourceType Source => SourceType.A2L;
    public bool CanImport(string fileName) => fileName.EndsWith(".a2l", StringComparison.OrdinalIgnoreCase);

    public ExternalDefinition Import(string fileName, string content)
    {
        var root = A2lParser.Parse(content);
        var module = root.Find("PROJECT")?.Find("MODULE") ?? root.Find("MODULE") ?? throw new DefinitionException("A2L has no MODULE block");
        var bigEndianDefault = module.Find("MOD_COMMON")?.Keyword("BYTE_ORDER") is not "MSB_LAST"; // MSB_FIRST = big endian (default for PowerPC EDC16)
        var layouts = module.All("RECORD_LAYOUT").ToDictionary(b => b.Arg(0), b => b, StringComparer.Ordinal);
        var compu = module.All("COMPU_METHOD").ToDictionary(b => b.Arg(0), b => b, StringComparer.Ordinal);
        var axisPts = module.All("AXIS_PTS").ToDictionary(b => b.Arg(0), b => b, StringComparer.Ordinal);

        var raw = new List<(MapDefinition Def, long Address, long? XAddr, long? YAddr, long Record)>();
        var skipped = new List<string>();
        foreach (var c in module.All("CHARACTERISTIC"))
        {
            var name = c.Arg(0);
            try
            {
                if (Build(c, layouts, compu, axisPts, bigEndianDefault) is { } built) raw.Add(built);
            }
            catch (A2lUnsupportedException ex) { skipped.Add($"{name}: {ex.Message}"); }
        }
        if (raw.Count == 0)
            throw new DefinitionException(skipped.Count > 0 ? $"A2L contains no importable characteristics ({skipped.Count} skipped, e.g. {skipped[0]})" : "A2L contains no CHARACTERISTIC blocks");

        // ECU address space → file offsets.
        var maxAddr = raw.Max(r => r.Address);
        var rebase = maxAddr >= 0x0100_0000 ? raw.Min(r => r.Address) & 0xFF00_0000L : 0;
        int Off(long a)
        {
            var o = a - rebase;
            if (o is < 0 or > int.MaxValue) throw new DefinitionException($"A2L address 0x{a:X} cannot be mapped to a file offset (base 0x{rebase:X})");
            return (int)o;
        }
        var maps = raw.Select(r => r.Def with
        {
            Address = Off(r.Address),
            XAxis = r.Def.XAxis is { Address: not null } x && r.XAddr is { } xa ? x with { Address = Off(xa) } : r.Def.XAxis,
            YAxis = r.Def.YAxis is { Address: not null } y && r.YAddr is { } ya ? y with { Address = Off(ya) } : r.Def.YAxis,
            Record = r.Def.Record is { } rec ? rec with { Address = Off(r.Record) } : null,
        }).ToList();

        var notes = new List<string> { $"A2L: {maps.Count} characteristic(s) imported, {skipped.Count} skipped" };
        if (layouts.Values.Any(l => l.Tokens.Contains("NO_AXIS_PTS_X") || l.Tokens.Contains("NO_AXIS_PTS_Y")))
            notes.Add("Some layouts store the axis point count in the binary: sizes are taken from MAX_AXIS_POINTS and must match the stored counts");
        if (rebase != 0) notes.Add($"ECU address base 0x{rebase:X8} subtracted to get file offsets; verify against the binary layout");
        notes.AddRange(skipped.Take(50).Select(s => "skipped " + s));
        var modPar = module.Find("MOD_PAR");
        var epk = modPar?.Keyword("EPK") is { Length: > 0 } e ? e.Trim() : null;
        int? epkAddress = modPar?.Keyword("ADDR_EPK") is { } ea && TryParseLong(ea, out var eaddr) && eaddr - rebase is >= 0 and < int.MaxValue ? (int)(eaddr - rebase) : null;
        return new ExternalDefinition(SourceType.A2L, maps, fileName) { Notes = notes, AxisCount = axisPts.Count, Epk = epk, EpkAddress = epkAddress };
    }

    private static (MapDefinition, long, long?, long?, long)? Build(A2lBlock c, Dictionary<string, A2lBlock> layouts, Dictionary<string, A2lBlock> compu,
        Dictionary<string, A2lBlock> axisPts, bool bigEndianDefault)
    {
        // CHARACTERISTIC name "long id" type address deposit maxDiff conversion lower upper
        var name = c.Arg(0);
        var type = c.Arg(2);
        if (type is "ASCII") return null;
        if (type is not ("VALUE" or "CURVE" or "MAP" or "VAL_BLK")) throw new A2lUnsupportedException($"type {type}");
        var address = ParseLong(c.Arg(3));
        var layout = layouts.GetValueOrDefault(c.Arg(4)) ?? throw new A2lUnsupportedException($"unknown RECORD_LAYOUT {c.Arg(4)}");
        var (factor, offset, unit) = Conversion(compu, c.Arg(6));
        var endian = c.Keyword("BYTE_ORDER") is { } bo ? bo == "MSB_FIRST" ? Endianness.Big : Endianness.Little : bigEndianDefault ? Endianness.Big : Endianness.Little;
        var axes = c.All("AXIS_DESCR").ToList();

        int cols = 1, rows = 1;
        if (type == "VAL_BLK")
        {
            cols = c.KeywordArgs("MATRIX_DIM") is { Count: > 0 } md ? md.Select(t => (int)ParseLong(t)).Where(v => v > 0).Aggregate(1, (a, b) => a * b)
                : c.Keyword("NUMBER") is { } n ? (int)ParseLong(n) : throw new A2lUnsupportedException("VAL_BLK without NUMBER/MATRIX_DIM");
        }
        if (type is "CURVE" or "MAP" && axes.Count < (type == "MAP" ? 2 : 1)) throw new A2lUnsupportedException($"{type} without AXIS_DESCR");
        if (axes.Count > 0) cols = (int)ParseLong(axes[0].Arg(3));
        if (type == "MAP") rows = (int)ParseLong(axes[1].Arg(3));

        // Walk the record layout to find where values (and inline axes) start inside the deposit.
        var items = LayoutItems(layout);
        var fnc = items.FirstOrDefault(i => i.Kind == "FNC_VALUES") ?? throw new A2lUnsupportedException($"RECORD_LAYOUT {layout.Arg(0)} has no FNC_VALUES");
        var counts = new Dictionary<char, int> { ['X'] = cols, ['Y'] = rows };
        long Offset(string kind)
        {
            long o = 0;
            foreach (var it in items)
            {
                if (it.Kind == kind) return o;
                o += it.Kind switch
                {
                    "AXIS_PTS_X" => it.Type.Size() * counts['X'],
                    "AXIS_PTS_Y" => it.Type.Size() * counts['Y'],
                    "FNC_VALUES" => it.Type.Size() * cols * rows,
                    _ => it.Type.Size(),
                };
            }
            return -1;
        }

        AxisDefinition? Axis(A2lBlock? d, char letter, int length, out long? axisAddress)
        {
            axisAddress = null;
            if (d is null) return null;
            // AXIS_DESCR attribute inputQuantity conversion maxAxisPoints lower upper
            var attr = d.Arg(0);
            var (af, ao, au) = Conversion(compu, d.Arg(2));
            var label = d.Arg(1) is { Length: > 0 } iq && iq != "NO_INPUT_QUANTITY" ? iq : $"{letter} axis";
            switch (attr)
            {
                case "FIX_AXIS":
                    return new AxisDefinition { Name = label, Unit = au, Length = length, FixedValues = FixAxis(d, length).Select(v => v * af + ao).ToArray() };
                case "STD_AXIS":
                {
                    var item = items.FirstOrDefault(i => i.Kind == $"AXIS_PTS_{letter}") ?? throw new A2lUnsupportedException($"STD_AXIS but layout has no AXIS_PTS_{letter}");
                    axisAddress = address + Offset(item.Kind);
                    return new AxisDefinition { Name = label, Unit = au, Length = length, Address = 0, DataType = item.Type, Factor = af, Offset = ao, LowerLimit = Limit(d.Arg(4)), UpperLimit = Limit(d.Arg(5)) };
                }
                case "COM_AXIS" or "RES_AXIS" or "CURVE_AXIS":
                {
                    if (attr == "CURVE_AXIS") throw new A2lUnsupportedException("CURVE_AXIS");
                    var refName = d.Keyword("AXIS_PTS_REF") ?? throw new A2lUnsupportedException($"{attr} without AXIS_PTS_REF");
                    var ap = axisPts.GetValueOrDefault(refName) ?? throw new A2lUnsupportedException($"unknown AXIS_PTS {refName}");
                    // AXIS_PTS name "long id" address inputQuantity deposit maxDiff conversion maxAxisPoints lower upper
                    var apLayout = layouts.GetValueOrDefault(ap.Arg(4)) ?? throw new A2lUnsupportedException($"unknown RECORD_LAYOUT {ap.Arg(4)}");
                    var apItems = LayoutItems(apLayout);
                    var pts = apItems.FirstOrDefault(i => i.Kind.StartsWith("AXIS_PTS_", StringComparison.Ordinal)) ?? throw new A2lUnsupportedException($"layout {apLayout.Arg(0)} has no AXIS_PTS");
                    long o = 0;
                    foreach (var it in apItems) { if (it == pts) break; o += it.Type.Size(); }
                    var (cf, co, cu) = Conversion(compu, ap.Arg(6));
                    axisAddress = ParseLong(ap.Arg(2)) + o;
                    return new AxisDefinition { Name = ap.Arg(3) is { Length: > 0 } q && q != "NO_INPUT_QUANTITY" ? q : refName, Unit = cu, Length = length, Address = 0, DataType = pts.Type, Factor = cf, Offset = co,
                        LowerLimit = Limit(ap.Arg(8)), UpperLimit = Limit(ap.Arg(9)) };
                }
                default:
                    throw new A2lUnsupportedException($"axis attribute {attr}");
            }
        }

        var x = Axis(axes.ElementAtOrDefault(0), 'X', cols, out var xAddr);
        long? yAddr = null;
        var y = type == "MAP" ? Axis(axes.ElementAtOrDefault(1), 'Y', rows, out yAddr) : null;
        var description = c.Arg(1);
        // The Bosch label decides first; the (often German) description is the fallback.
        var role = BoschLabels.RoleOf(name, out var primary);
        if (role == MapRole.Unknown) role = XdfImporter.GuessRole(name + " " + description);
        var def = new MapDefinition
        {
            Id = "a2l_" + Sanitize(name),
            Name = name,
            Description = description,
            Role = role,
            Address = 0,
            Rows = rows,
            Cols = cols,
            DataType = fnc.Type,
            Endian = endian,
            Factor = factor,
            Offset = offset,
            Unit = unit,
            Order = fnc.Order,
            XAxis = x,
            YAxis = y,
            Source = SourceType.A2L,
            // Base maps of a role rank above its corrections and variants.
            Confidence = role != MapRole.Unknown && !primary ? 0.88 : 0.9,
            LowerLimit = Limit(c.Arg(7)),
            UpperLimit = Limit(c.Arg(8)),
            // Counts stored in the binary: sizes above are the maxima; the binder reads the real ones per binary.
            Record = items.Any(i => i.Kind is "NO_AXIS_PTS_X" or "NO_AXIS_PTS_Y")
                ? new InlineRecord(0, items.Select(i => new RecordItem(i.Kind, i.Type)).ToList(), cols, rows) : null,
        };
        return (def, address + Offset("FNC_VALUES"), xAddr, yAddr, address);
    }

    private sealed record LayoutItem(string Kind, int Position, DataType Type, ValueOrder Order);

    private static readonly HashSet<string> LayoutKinds =
        ["FNC_VALUES", "AXIS_PTS_X", "AXIS_PTS_Y", "NO_AXIS_PTS_X", "NO_AXIS_PTS_Y", "SRC_ADDR_X", "SRC_ADDR_Y", "RIP_ADDR_X", "RIP_ADDR_Y", "RIP_ADDR_W",
         "SHIFT_OP_X", "SHIFT_OP_Y", "OFFSET_X", "OFFSET_Y", "DIST_OP_X", "DIST_OP_Y", "IDENTIFICATION", "RESERVED", "NO_RESCALE_X", "AXIS_RESCALE_X"];

    private static List<LayoutItem> LayoutItems(A2lBlock layout)
    {
        var t = layout.Tokens;
        var list = new List<LayoutItem>();
        for (var i = 1; i < t.Count; i++)
        {
            if (!LayoutKinds.Contains(t[i])) continue;
            if (t[i] is "AXIS_RESCALE_X" or "NO_RESCALE_X") throw new A2lUnsupportedException($"layout {layout.Arg(0)} uses rescale axes");
            var pos = (int)ParseLong(t[i + 1]);
            if (t[i] == "RESERVED")
            {
                // RESERVED position DataSize (BYTE / WORD / LONG)
                list.Add(new LayoutItem("RESERVED", pos, t[i + 2] switch { "BYTE" => DataType.UInt8, "WORD" => DataType.UInt16, _ => DataType.UInt32 }, ValueOrder.RowMajor));
                continue;
            }
            var type = DataTypeOf(t[i + 2]);
            var order = ValueOrder.RowMajor;
            if (t[i] == "FNC_VALUES")
            {
                // FNC_VALUES position datatype indexMode addressing. COLUMN_DIR: for each X, all Y (Y varies fastest).
                order = t.ElementAtOrDefault(i + 3) == "COLUMN_DIR" ? ValueOrder.ColumnMajor : ValueOrder.RowMajor;
                if (t.ElementAtOrDefault(i + 4) is "PBYTE" or "PWORD" or "PLONG") throw new A2lUnsupportedException("indirect (pointer) addressing");
            }
            if (t[i] is "AXIS_PTS_X" or "AXIS_PTS_Y" && t.ElementAtOrDefault(i + 3) is "INDEX_DECR") throw new A2lUnsupportedException("decreasing axis storage");
            list.Add(new LayoutItem(t[i], pos, type, order));
        }
        list.Sort((a, b) => a.Position.CompareTo(b.Position));
        return list;
    }

    private static bool TryParseLong(string token, out long value)
    {
        try { value = ParseLong(token); return true; }
        catch (Exception ex) when (ex is FormatException or OverflowException or DefinitionException or A2lUnsupportedException) { value = 0; return false; }
    }

    private static double? Limit(string token) =>
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    private static (double Factor, double Offset, string Unit) Conversion(Dictionary<string, A2lBlock> compu, string name)
    {
        if (name == "NO_COMPU_METHOD") return (1, 0, "-");
        var m = compu.GetValueOrDefault(name) ?? throw new A2lUnsupportedException($"unknown COMPU_METHOD {name}");
        // COMPU_METHOD name "long id" conversionType format unit
        var unit = m.Arg(4) is { Length: > 0 } u ? u : "-";
        switch (m.Arg(2))
        {
            case "IDENTICAL":
                return (1, 0, unit);
            case "LINEAR":
            {
                var k = m.KeywordArgs("COEFFS_LINEAR") ?? throw new A2lUnsupportedException($"{name}: LINEAR without COEFFS_LINEAR");
                return (ParseDouble(k[0]), ParseDouble(k[1]), unit); // phys = a·int + b
            }
            case "RAT_FUNC":
            {
                // int = (a·p² + b·p + c) / (d·p² + e·p + f); linear only when a = d = e = 0 → p = (f·int − c) / b
                var k = m.KeywordArgs("COEFFS") ?? throw new A2lUnsupportedException($"{name}: RAT_FUNC without COEFFS");
                var (a, b, cc, d, e, f) = (ParseDouble(k[0]), ParseDouble(k[1]), ParseDouble(k[2]), ParseDouble(k[3]), ParseDouble(k[4]), ParseDouble(k[5]));
                if (a != 0 || d != 0 || e != 0 || b == 0) throw new A2lUnsupportedException($"{name}: non-linear RAT_FUNC");
                return (f / b, -cc / b, unit);
            }
            default:
                throw new A2lUnsupportedException($"{name}: conversion {m.Arg(2)}");
        }
    }

    private static double[] FixAxis(A2lBlock d, int length)
    {
        if (d.KeywordArgs("FIX_AXIS_PAR") is { Count: >= 3 } par)
        {
            var (o, shift, n) = (ParseDouble(par[0]), (int)ParseLong(par[1]), (int)ParseLong(par[2]));
            return Enumerable.Range(0, Math.Min(n, length)).Select(i => o + i * Math.Pow(2, shift)).ToArray();
        }
        if (d.KeywordArgs("FIX_AXIS_PAR_DIST") is { Count: >= 3 } dist)
        {
            var (o, step, n) = (ParseDouble(dist[0]), ParseDouble(dist[1]), (int)ParseLong(dist[2]));
            return Enumerable.Range(0, Math.Min(n, length)).Select(i => o + i * step).ToArray();
        }
        if (d.Find("FIX_AXIS_PAR_LIST") is { } list) return list.Tokens.Skip(1).Take(length).Select(ParseDouble).ToArray();
        throw new A2lUnsupportedException("FIX_AXIS without parameters");
    }

    private static DataType DataTypeOf(string t) => t switch
    {
        "UBYTE" => DataType.UInt8, "SBYTE" => DataType.Int8, "UWORD" => DataType.UInt16, "SWORD" => DataType.Int16,
        "ULONG" => DataType.UInt32, "SLONG" => DataType.Int32, "FLOAT32_IEEE" => DataType.Float32,
        _ => throw new A2lUnsupportedException($"data type {t}"),
    };

    internal static long ParseLong(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new A2lUnsupportedException($"expected integer, got '{s}'");

    private static double ParseDouble(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ParseLong(s)
        : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : throw new A2lUnsupportedException($"expected number, got '{s}'");

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(char.IsAsciiLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_');
        return sb.ToString();
    }
}

internal sealed class A2lUnsupportedException(string message) : Exception(message);

/// <summary>A /begin … /end block: its own tokens (positional arguments and keyword parameters) plus nested blocks.</summary>
internal sealed class A2lBlock(string name)
{
    public string Name { get; } = name;
    public List<string> Tokens { get; } = [];
    public List<A2lBlock> Children { get; } = [];

    public string Arg(int i) => i < Tokens.Count ? Tokens[i] : "";
    public A2lBlock? Find(string name) => Children.FirstOrDefault(c => c.Name == name);
    public IEnumerable<A2lBlock> All(string name) => Children.Where(c => c.Name == name);

    /// <summary>Value after an optional keyword (e.g. BYTE_ORDER MSB_FIRST), searched among this block's own tokens.</summary>
    public string? Keyword(string keyword)
    {
        var i = Tokens.IndexOf(keyword);
        return i >= 0 && i + 1 < Tokens.Count ? Tokens[i + 1] : null;
    }

    /// <summary>Numeric arguments following a keyword, up to the next non-numeric token.</summary>
    public List<string>? KeywordArgs(string keyword)
    {
        var i = Tokens.IndexOf(keyword);
        if (i < 0) return null;
        var list = new List<string>();
        for (var j = i + 1; j < Tokens.Count && IsNumber(Tokens[j]); j++) list.Add(Tokens[j]);
        return list;
    }

    private static bool IsNumber(string t) =>
        t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
}

internal static class A2lParser
{
    public static A2lBlock Parse(string text)
    {
        var root = new A2lBlock("ROOT");
        var stack = new Stack<A2lBlock>();
        stack.Push(root);
        var tokens = Tokenize(text).GetEnumerator();
        while (tokens.MoveNext())
        {
            var t = tokens.Current;
            if (t.Quoted) { stack.Peek().Tokens.Add(t.Text); continue; }
            if (t.Text == "/begin")
            {
                if (!tokens.MoveNext()) throw new DefinitionException("A2L ends after /begin");
                var block = new A2lBlock(tokens.Current.Text);
                stack.Peek().Children.Add(block);
                stack.Push(block);
            }
            else if (t.Text == "/end")
            {
                if (!tokens.MoveNext()) throw new DefinitionException("A2L ends after /end");
                if (stack.Count == 1 || stack.Peek().Name != tokens.Current.Text)
                    throw new DefinitionException($"A2L: /end {tokens.Current.Text} does not close /begin {stack.Peek().Name}");
                stack.Pop();
            }
            else stack.Peek().Tokens.Add(t.Text);
        }
        if (stack.Count != 1) throw new DefinitionException($"A2L: /begin {stack.Peek().Name} is never closed");
        return root;
    }

    private readonly record struct Token(string Text, bool Quoted);

    private static IEnumerable<Token> Tokenize(string s)
    {
        var i = 0;
        var sb = new StringBuilder();
        while (i < s.Length)
        {
            var ch = s[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            if (ch == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new DefinitionException("A2L: unterminated /* comment");
                i = end + 2;
                continue;
            }
            if (ch == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                var end = s.IndexOf('\n', i);
                i = end < 0 ? s.Length : end + 1;
                continue;
            }
            if (ch == '"')
            {
                sb.Clear();
                i++;
                while (i < s.Length)
                {
                    if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i += 2; continue; }
                    if (s[i] == '"') { if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; } break; }
                    sb.Append(s[i++]);
                }
                if (i >= s.Length) throw new DefinitionException("A2L: unterminated string");
                i++;
                yield return new Token(sb.ToString(), true);
                continue;
            }
            var start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '"') i++;
            yield return new Token(s[start..i], false);
        }
    }
}
