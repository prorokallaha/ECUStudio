using System.Buffers.Binary;
using System.Text.Json.Serialization;

namespace ECUStudio.Binary;

[JsonConverter(typeof(JsonStringEnumConverter<ChecksumAlgorithm>))]
public enum ChecksumAlgorithm
{
    /// <summary>Sum of bytes.</summary>
    Add8,
    /// <summary>Sum of 16-bit words (word byte order = <see cref="ChecksumSpec.Endian"/>).</summary>
    Add16,
    /// <summary>Sum of 32-bit words.</summary>
    Add32,
    /// <summary>CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF).</summary>
    Crc16Ccitt,
    /// <summary>CRC-32/ISO-HDLC (zlib).</summary>
    Crc32,
}

/// <summary>
/// One checksum block as described by a definition: the covered range [Start, End), the algorithm and where the
/// expected value is stored. Sums are truncated to <see cref="StoreSize"/> bytes; <see cref="Complement"/> means the
/// stored value is the bitwise NOT of the computed one (Bosch-style sum/complement pairs).
/// </summary>
public sealed record ChecksumSpec
{
    public required string Name { get; init; }
    public required int Start { get; init; }
    /// <summary>Exclusive end of the covered range.</summary>
    public required int End { get; init; }
    public required ChecksumAlgorithm Algorithm { get; init; }
    public required int StoredAt { get; init; }
    /// <summary>Bytes of the stored value (1, 2 or 4). CRC widths are fixed by the algorithm.</summary>
    public int StoreSize { get; init; } = 4;
    public Endianness Endian { get; init; } = Endianness.Big;
    /// <summary>Initial value added to sums. Ignored for CRCs.</summary>
    public uint Seed { get; init; }
    public bool Complement { get; init; }

    public int EffectiveStoreSize => Algorithm switch { ChecksumAlgorithm.Crc16Ccitt => 2, ChecksumAlgorithm.Crc32 => 4, _ => StoreSize };
    public string Describe() => $"{Algorithm}{(Complement ? " (complement)" : "")} 0x{Start:X}–0x{End - 1:X} → 0x{StoredAt:X}";
}

/// <summary>
/// Verifies checksum blocks that a definition describes. Only described blocks are checked: an image can carry
/// further checksums the definition does not know about, so "all described blocks valid" is reported as such and
/// never as "ready to flash". ECUStudio does not correct checksums.
/// </summary>
public static class ChecksumVerifier
{
    public static ChecksumReport Verify(ReadOnlySpan<byte> image, IReadOnlyList<ChecksumSpec> specs, string scope)
    {
        if (specs.Count == 0)
            return new ChecksumReport(ChecksumStatus.NotImplemented, [], $"No checksum blocks are described for this image ({scope}).");

        var blocks = new List<ChecksumBlock>(specs.Count);
        foreach (var s in specs)
        {
            if (Validate(s, image.Length) is { } problem)
            {
                blocks.Add(new ChecksumBlock(s.Name, s.Start, s.End, s.Describe(), ChecksumStatus.Unknown, Computed: $"definition error: {problem}"));
                continue;
            }
            var computed = Compute(image, s);
            var stored = ReadStored(image, s);
            var expected = s.Complement ? ~computed & Mask(s.EffectiveStoreSize) : computed;
            var width = s.EffectiveStoreSize * 2;
            blocks.Add(new ChecksumBlock(s.Name, s.Start, s.End, s.Describe(), stored == expected ? ChecksumStatus.Valid : ChecksumStatus.Invalid,
                Stored: "0x" + stored.ToString($"X{width}"), Computed: "0x" + expected.ToString($"X{width}")));
        }

        var invalid = blocks.Count(b => b.Status == ChecksumStatus.Invalid);
        var unknown = blocks.Count(b => b.Status == ChecksumStatus.Unknown);
        var overall = invalid > 0 ? ChecksumStatus.Invalid : unknown > 0 ? ChecksumStatus.Unknown : ChecksumStatus.Valid;
        var note = overall switch
        {
            ChecksumStatus.Invalid => $"{invalid} of {blocks.Count} described checksum block(s) do not match ({scope}): the file was modified without checksum correction, or the definition is wrong.",
            ChecksumStatus.Unknown => $"{unknown} of {blocks.Count} described block(s) could not be checked because the definition is inconsistent with the image ({scope}).",
            _ => $"All {blocks.Count} described checksum block(s) match ({scope}). Blocks not described in the definition are not checked: this is not a flash-readiness check.",
        };
        return new ChecksumReport(overall, blocks, note);
    }

    public static uint Compute(ReadOnlySpan<byte> image, ChecksumSpec s)
    {
        var data = image[s.Start..s.End];
        var big = s.Endian == Endianness.Big;
        uint sum = s.Seed;
        switch (s.Algorithm)
        {
            case ChecksumAlgorithm.Add8:
                foreach (var b in data) sum += b;
                break;
            case ChecksumAlgorithm.Add16:
                for (var i = 0; i < data.Length; i += 2)
                    sum += big ? BinaryPrimitives.ReadUInt16BigEndian(data[i..]) : BinaryPrimitives.ReadUInt16LittleEndian(data[i..]);
                break;
            case ChecksumAlgorithm.Add32:
                for (var i = 0; i < data.Length; i += 4)
                    sum += big ? BinaryPrimitives.ReadUInt32BigEndian(data[i..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
                break;
            case ChecksumAlgorithm.Crc16Ccitt:
                return Crc16Ccitt(data);
            case ChecksumAlgorithm.Crc32:
                return Checksums.Crc32(data);
            default:
                throw new ArgumentOutOfRangeException(nameof(s), s.Algorithm, "unknown checksum algorithm");
        }
        return sum & Mask(s.EffectiveStoreSize);
    }

    public static ushort Crc16Ccitt(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var k = 0; k < 8; k++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    internal static string? Validate(ChecksumSpec s, int length)
    {
        var size = s.EffectiveStoreSize;
        if (size is not (1 or 2 or 4)) return $"store size {size} (expected 1, 2 or 4)";
        if (s.Start < 0 || s.End <= s.Start || s.End > length) return $"range 0x{s.Start:X}–0x{s.End:X} outside the {length}-byte image";
        var word = s.Algorithm switch { ChecksumAlgorithm.Add16 => 2, ChecksumAlgorithm.Add32 => 4, _ => 1 };
        if ((s.End - s.Start) % word != 0) return $"range length {s.End - s.Start} is not a multiple of {word}";
        if (s.StoredAt < 0 || s.StoredAt + size > length) return $"stored value at 0x{s.StoredAt:X} is outside the image";
        if (s.StoredAt < s.End && s.StoredAt + size > s.Start) return "stored value lies inside the covered range (unsupported)";
        return null;
    }

    private static uint ReadStored(ReadOnlySpan<byte> image, ChecksumSpec s)
    {
        var v = image.Slice(s.StoredAt, s.EffectiveStoreSize);
        var big = s.Endian == Endianness.Big;
        return s.EffectiveStoreSize switch
        {
            1 => v[0],
            2 => big ? BinaryPrimitives.ReadUInt16BigEndian(v) : BinaryPrimitives.ReadUInt16LittleEndian(v),
            _ => big ? BinaryPrimitives.ReadUInt32BigEndian(v) : BinaryPrimitives.ReadUInt32LittleEndian(v),
        };
    }

    private static uint Mask(int bytes) => bytes >= 4 ? uint.MaxValue : (1u << (8 * bytes)) - 1;
}
