using System.Buffers.Binary;
using System.Text.Json.Serialization;

namespace ECUStudio.Binary;

[JsonConverter(typeof(JsonStringEnumConverter<DataType>))]
public enum DataType { UInt8, Int8, UInt16, Int16, UInt32, Int32, Float32 }

/// <summary>Byte order. EDC16/EDC17 (PowerPC/TriCore) differ, so it is always explicit.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Endianness>))]
public enum Endianness { Big, Little }

public static class DataTypeInfo
{
    public static int Size(this DataType t) => t switch
    {
        DataType.UInt8 or DataType.Int8 => 1,
        DataType.UInt16 or DataType.Int16 => 2,
        _ => 4,
    };

    public static bool IsSigned(this DataType t) => t is DataType.Int8 or DataType.Int16 or DataType.Int32 or DataType.Float32;

    public static double MaxRaw(this DataType t) => t switch
    {
        DataType.UInt8 => byte.MaxValue,
        DataType.Int8 => sbyte.MaxValue,
        DataType.UInt16 => ushort.MaxValue,
        DataType.Int16 => short.MaxValue,
        DataType.UInt32 => uint.MaxValue,
        DataType.Int32 => int.MaxValue,
        _ => float.MaxValue,
    };
}

/// <summary>Allocation-free scalar readers over spans.</summary>
public static class ValueReader
{
    public static double ReadRaw(ReadOnlySpan<byte> data, int offset, DataType type, Endianness endian)
    {
        var s = data.Slice(offset, type.Size());
        var big = endian == Endianness.Big;
        return type switch
        {
            DataType.UInt8 => s[0],
            DataType.Int8 => (sbyte)s[0],
            DataType.UInt16 => big ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s),
            DataType.Int16 => big ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s),
            DataType.UInt32 => big ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s),
            DataType.Int32 => big ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s),
            DataType.Float32 => big ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }

    /// <summary>Reads <paramref name="count"/> consecutive values into <paramref name="destination"/>.</summary>
    public static void ReadRawArray(ReadOnlySpan<byte> data, int offset, int count, DataType type, Endianness endian, Span<double> destination)
    {
        var size = type.Size();
        for (var i = 0; i < count; i++)
            destination[i] = ReadRaw(data, offset + i * size, type, endian);
    }

    public static void WriteRaw(Span<byte> data, int offset, DataType type, Endianness endian, double raw)
    {
        var s = data.Slice(offset, type.Size());
        var big = endian == Endianness.Big;
        switch (type)
        {
            case DataType.UInt8: s[0] = (byte)Math.Clamp(Math.Round(raw), 0, 255); break;
            case DataType.Int8: s[0] = (byte)(sbyte)Math.Clamp(Math.Round(raw), -128, 127); break;
            case DataType.UInt16:
                var u16 = (ushort)Math.Clamp(Math.Round(raw), 0, ushort.MaxValue);
                if (big) BinaryPrimitives.WriteUInt16BigEndian(s, u16); else BinaryPrimitives.WriteUInt16LittleEndian(s, u16);
                break;
            case DataType.Int16:
                var i16 = (short)Math.Clamp(Math.Round(raw), short.MinValue, short.MaxValue);
                if (big) BinaryPrimitives.WriteInt16BigEndian(s, i16); else BinaryPrimitives.WriteInt16LittleEndian(s, i16);
                break;
            case DataType.UInt32:
                var u32 = (uint)Math.Clamp(Math.Round(raw), 0, uint.MaxValue);
                if (big) BinaryPrimitives.WriteUInt32BigEndian(s, u32); else BinaryPrimitives.WriteUInt32LittleEndian(s, u32);
                break;
            case DataType.Int32:
                var i32 = (int)Math.Clamp(Math.Round(raw), int.MinValue, int.MaxValue);
                if (big) BinaryPrimitives.WriteInt32BigEndian(s, i32); else BinaryPrimitives.WriteInt32LittleEndian(s, i32);
                break;
            case DataType.Float32:
                if (big) BinaryPrimitives.WriteSingleBigEndian(s, (float)raw); else BinaryPrimitives.WriteSingleLittleEndian(s, (float)raw);
                break;
        }
    }
}

/// <summary>Linear fixed-point conversion: physical = raw * Factor + Offset.</summary>
public readonly record struct FixedPoint(double Factor, double Offset = 0)
{
    public static readonly FixedPoint Identity = new(1, 0);
    public double ToPhysical(double raw) => raw * Factor + Offset;
    public double ToRaw(double physical) => (physical - Offset) / Factor;
}
