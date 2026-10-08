using ECUStudio.Binary;
using ECUStudio.Core;

namespace ECUStudio.Calibration.Model;

public static class MapDecoder
{
    public static CalibrationMap Decode(BinaryImage image, MapDefinition def)
    {
        if (def.Rows <= 0 || def.Cols <= 0)
            throw new DefinitionException($"Map '{def.Id}' has invalid dimensions {def.Rows}x{def.Cols}");
        if (!image.Contains(def.Address, def.ByteLength))
            throw new DefinitionException($"Map '{def.Id}' at 0x{def.Address:X} ({def.ByteLength} bytes) is outside the image ({image.Length} bytes)");

        var count = def.Rows * def.Cols;
        var raw = new double[count];
        var span = image.Span;
        var size = def.DataType.Size();
        for (var i = 0; i < count; i++)
        {
            int row, col;
            if (def.Order == ValueOrder.RowMajor) { row = i / def.Cols; col = i % def.Cols; }
            else { col = i / def.Rows; row = i % def.Rows; }
            raw[row * def.Cols + col] = ValueReader.ReadRaw(span, def.Address + i * size, def.DataType, def.Endian);
        }

        var values = new double[count];
        for (var i = 0; i < count; i++) values[i] = raw[i] * def.Factor + def.Offset;

        return new CalibrationMap
        {
            Definition = def,
            XAxis = DecodeAxis(image, def.XAxis, def.Cols, def.Endian, def.Id, "X"),
            YAxis = DecodeAxis(image, def.YAxis, def.Rows, def.Endian, def.Id, "Y"),
            Values = values,
            Raw = raw,
        };
    }

    private static double[] DecodeAxis(BinaryImage image, AxisDefinition? axis, int expected, Endianness endian, string mapId, string label)
    {
        if (axis is null)
            return Enumerable.Range(0, expected).Select(i => (double)i).ToArray();
        if (axis.FixedValues is { } fixedValues)
        {
            if (fixedValues.Length != expected)
                throw new DefinitionException($"Map '{mapId}' {label}-axis has {fixedValues.Length} values, expected {expected}");
            return fixedValues;
        }
        if (axis.Address is not { } address)
            throw new DefinitionException($"Map '{mapId}' {label}-axis has neither address nor fixed values");
        var length = axis.Length > 0 ? axis.Length : expected;
        if (length != expected)
            throw new DefinitionException($"Map '{mapId}' {label}-axis length {length} does not match map size {expected}");
        if (!image.Contains(address, length * axis.DataType.Size()))
            throw new DefinitionException($"Map '{mapId}' {label}-axis at 0x{address:X} is outside the image");
        var result = new double[length];
        for (var i = 0; i < length; i++)
            result[i] = ValueReader.ReadRaw(image.Span, address + i * axis.DataType.Size(), axis.DataType, endian) * axis.Factor + axis.Offset;
        return result;
    }
}
