using System.Text.Json.Serialization;
using ECUStudio.Core;

namespace ECUStudio.Binary;

[JsonConverter(typeof(JsonStringEnumConverter<SectionKind>))]
public enum SectionKind { Boot, Code, Calibration, Data, Empty, Unknown }

public sealed record MemorySection(string Name, int Start, int End, SectionKind Kind, SourceType Source, double Confidence, string? Note = null)
{
    public int Length => End - Start;
}

[JsonConverter(typeof(JsonStringEnumConverter<ChecksumStatus>))]
/// <summary>
/// Valid: described blocks match. Invalid: a described block does not match. Corrected: ECUStudio recalculated the
/// described blocks with an algorithm the plugin implements. Unknown: blocks could not be checked (inconsistent
/// definition). Unsupported: no checksum algorithm/blocks are known for this file: it is never reported as safe to write.
/// </summary>
public enum ChecksumStatus { Valid, Invalid, Corrected, Unknown, Unsupported }

public sealed record ChecksumBlock(string Name, int Start, int End, string Algorithm, ChecksumStatus Status, string? Stored = null, string? Computed = null);

public sealed record ChecksumReport(ChecksumStatus Overall, IReadOnlyList<ChecksumBlock> Blocks, string Note);

/// <summary>Checksum primitives used by plugins that implement real checksum verification.</summary>
public static class Checksums
{
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Crc32(ReadOnlySpan<byte> data, uint seed = 0xFFFFFFFF)
    {
        var crc = seed;
        foreach (var b in data) crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    public static uint Sum16BigEndian(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i + 1 < data.Length; i += 2) sum += (uint)(data[i] << 8 | data[i + 1]);
        return sum;
    }

    /// <summary>Heuristic: classify 4 KB blocks as empty (0xFF/0x00 fill) vs. populated.</summary>
    public static bool IsFill(ReadOnlySpan<byte> block) =>
        block.IndexOfAnyExcept((byte)0xFF) < 0 || block.IndexOfAnyExcept((byte)0x00) < 0;
}
