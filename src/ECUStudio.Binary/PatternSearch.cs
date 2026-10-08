using System.Globalization;

namespace ECUStudio.Binary;

/// <summary>Search helpers for the binary viewer and plugins.</summary>
public static class PatternSearch
{
    /// <summary>
    /// Parses "7F FF ?? 12" (spaces optional, ?? = wildcard). Returns bytes and a wildcard mask.
    /// </summary>
    public static (byte[] Bytes, bool[] Wildcard) ParseHexPattern(string pattern)
    {
        var clean = pattern.Replace(" ", "", StringComparison.Ordinal).Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        if (clean.Length == 0 || clean.Length % 2 != 0) throw new FormatException("Hex pattern must contain whole bytes");
        var bytes = new byte[clean.Length / 2];
        var mask = new bool[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            var token = clean.Substring(i * 2, 2);
            if (token == "??") { mask[i] = true; continue; }
            bytes[i] = byte.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return (bytes, mask);
    }

    public static List<int> FindHex(ReadOnlySpan<byte> data, string pattern, int maxResults = 1000)
    {
        var (bytes, mask) = ParseHexPattern(pattern);
        if (!mask.Contains(true)) return FindAll(data, bytes, maxResults);

        var results = new List<int>();
        for (var i = 0; i <= data.Length - bytes.Length && results.Count < maxResults; i++)
        {
            var match = true;
            for (var j = 0; j < bytes.Length; j++)
            {
                if (!mask[j] && data[i + j] != bytes[j]) { match = false; break; }
            }
            if (match) results.Add(i);
        }
        return results;
    }

    public static List<int> FindAll(ReadOnlySpan<byte> data, ReadOnlySpan<byte> needle, int maxResults = 1000)
    {
        var results = new List<int>();
        var offset = 0;
        while (results.Count < maxResults)
        {
            var idx = data[offset..].IndexOf(needle);
            if (idx < 0) break;
            results.Add(offset + idx);
            offset += idx + 1;
            if (offset >= data.Length) break;
        }
        return results;
    }

    public static List<int> FindValue(ReadOnlySpan<byte> data, double value, DataType type, Endianness endian, double tolerance = 0, int alignment = 1, int maxResults = 1000)
    {
        var results = new List<int>();
        var size = type.Size();
        for (var i = 0; i <= data.Length - size && results.Count < maxResults; i += alignment)
        {
            var v = ValueReader.ReadRaw(data, i, type, endian);
            if (Math.Abs(v - value) <= tolerance) results.Add(i);
        }
        return results;
    }

    public static List<int> FindAscii(ReadOnlySpan<byte> data, string text, int maxResults = 100)
        => FindAll(data, System.Text.Encoding.ASCII.GetBytes(text), maxResults);

    /// <summary>Extracts a printable ASCII run starting at <paramref name="offset"/>.</summary>
    public static string ReadAsciiRun(ReadOnlySpan<byte> data, int offset, int maxLength = 64)
    {
        var end = offset;
        while (end < data.Length && end - offset < maxLength && data[end] >= 0x20 && data[end] < 0x7F) end++;
        return System.Text.Encoding.ASCII.GetString(data[offset..end]);
    }
}
