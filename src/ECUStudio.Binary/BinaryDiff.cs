namespace ECUStudio.Binary;

public readonly record struct ByteRange(int Start, int Length)
{
    public int End => Start + Length;
    public bool Overlaps(int start, int length) => start < End && Start < start + length;
}

/// <summary>Byte-level comparison of two images of equal size.</summary>
public static class BinaryDiff
{
    /// <summary>
    /// Returns changed ranges. Ranges separated by fewer than <paramref name="mergeGap"/> equal bytes
    /// are merged (a 16-bit cell change often touches only one of its two bytes).
    /// Uses vectorized <c>CommonPrefixLength</c> to skip equal regions.
    /// </summary>
    public static List<ByteRange> ChangedRanges(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int mergeGap = 4)
    {
        if (a.Length != b.Length) throw new ArgumentException("Images must have equal length for byte diff");
        var result = new List<ByteRange>();
        var i = 0;
        var n = a.Length;
        while (i < n)
        {
            var equal = a[i..].CommonPrefixLength(b[i..]);
            i += equal;
            if (i >= n) break;
            var start = i;
            var lastDiff = i;
            while (i < n)
            {
                if (a[i] != b[i]) lastDiff = i;
                else if (i - lastDiff > mergeGap) break;
                i++;
            }
            result.Add(new ByteRange(start, lastDiff - start + 1));
        }
        return result;
    }

    public static int CountChangedBytes(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var count = 0;
        var i = 0;
        while (i < a.Length)
        {
            i += a[i..].CommonPrefixLength(b[i..]);
            if (i >= a.Length) break;
            count++;
            i++;
        }
        return count;
    }
}
