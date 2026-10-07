using System.Text.Json.Serialization;
using ECUStudio.Binary;
using ECUStudio.Binary.Editing;
using ECUStudio.Core;

namespace ECUStudio.Calibration.Model;

public readonly record struct Cell(int Row, int Col);

[JsonConverter(typeof(JsonStringEnumConverter<MapOperationKind>))]
public enum MapOperationKind
{
    /// <summary>Every selected cell := operand.</summary>
    Set,
    /// <summary>+ operand.</summary>
    Add,
    /// <summary>× operand.</summary>
    Multiply,
    /// <summary>× (1 + operand / 100).</summary>
    Percent,
    /// <summary>Bilinear fill of the selection's bounding box from its four corner cells.</summary>
    Interpolate,
    /// <summary>Per row: straight line between the first and last selected cell.</summary>
    Linearize,
    /// <summary>3×3 mean, limited to selected cells.</summary>
    Smooth,
    /// <summary>Explicit values per cell (paste, single-cell edit).</summary>
    Values,
}

public sealed record MapOperation(MapOperationKind Kind, IReadOnlyList<Cell> Cells, double Operand = 0, IReadOnlyList<double>? Values = null);

/// <summary>One cell of an edit preview: requested value, value after quantisation to the stored type, and its bytes.</summary>
public sealed record CellChange(int Row, int Col, double Old, double Requested, double Stored, int Address, string OldBytes, string NewBytes, bool Clamped);

public sealed record MapEditPreview(string MapId, IReadOnlyList<CellChange> Changes, int ChangedBytes, int ClampedCells, IReadOnlyList<ByteWrite> Writes);

/// <summary>
/// Computes map edits as byte writes without touching the binary. Values are quantised to the stored data type
/// (raw = round((value − offset) / factor), clamped to the type range); clamped cells are reported, never hidden.
/// Axes are not changed by map operations.
/// </summary>
public static class MapEditor
{
    public static MapEditPreview Preview(CalibrationMap map, MapOperation op)
    {
        var def = map.Definition;
        if (op.Cells.Count == 0) throw new DefinitionException("No cells selected");
        foreach (var c in op.Cells)
            if (c.Row < 0 || c.Row >= def.Rows || c.Col < 0 || c.Col >= def.Cols) throw new DefinitionException($"Cell ({c.Row},{c.Col}) is outside the {def.Rows}×{def.Cols} map");
        if (def.Factor == 0) throw new DefinitionException($"Map '{def.Id}' has factor 0 and cannot be edited");

        var requested = Compute(map, op);
        var size = def.DataType.Size();
        var changes = new List<CellChange>();
        var writes = new List<ByteWrite>();
        foreach (var (cell, value) in requested)
        {
            var idx = cell.Row * def.Cols + cell.Col;
            var address = def.Address + Index(def, cell) * size;
            var raw = (value - def.Offset) / def.Factor;
            var (min, max) = Range(def.DataType);
            var clamped = def.DataType != DataType.Float32 && (Math.Round(raw) < min || Math.Round(raw) > max);
            var bytes = new byte[size];
            ValueReader.WriteRaw(bytes, 0, def.DataType, def.Endian, raw);
            var storedRaw = ValueReader.ReadRaw(bytes, 0, def.DataType, def.Endian);
            var stored = storedRaw * def.Factor + def.Offset;
            var oldBytes = BytesOf(map, def, cell, size);
            if (oldBytes.AsSpan().SequenceEqual(bytes)) continue;
            changes.Add(new CellChange(cell.Row, cell.Col, map.Values[idx], value, stored, address, Convert.ToHexString(oldBytes), Convert.ToHexString(bytes), clamped));
            writes.Add(new ByteWrite(address, bytes));
        }
        return new MapEditPreview(def.Id, changes, changes.Count * size, changes.Count(c => c.Clamped), writes);
    }

    /// <summary>Byte ranges occupied by the map values (for "revert map").</summary>
    public static ByteRange ValueRange(MapDefinition def) => new(def.Address, def.ByteLength);

    private static IEnumerable<(Cell Cell, double Value)> Compute(CalibrationMap map, MapOperation op)
    {
        var def = map.Definition;
        double V(int r, int c) => map.Values[r * def.Cols + c];
        var cells = op.Cells.Distinct().ToList();
        switch (op.Kind)
        {
            case MapOperationKind.Set: return cells.Select(c => (c, op.Operand));
            case MapOperationKind.Add: return cells.Select(c => (c, V(c.Row, c.Col) + op.Operand));
            case MapOperationKind.Multiply: return cells.Select(c => (c, V(c.Row, c.Col) * op.Operand));
            case MapOperationKind.Percent: return cells.Select(c => (c, V(c.Row, c.Col) * (1 + op.Operand / 100)));
            case MapOperationKind.Values:
                if (op.Values is null || op.Values.Count != op.Cells.Count) throw new DefinitionException("Values must have one entry per selected cell");
                return op.Cells.Select((c, i) => (c, op.Values[i]));
            case MapOperationKind.Interpolate:
            {
                int r0 = cells.Min(c => c.Row), r1 = cells.Max(c => c.Row), c0 = cells.Min(c => c.Col), c1 = cells.Max(c => c.Col);
                if (r0 == r1 && c0 == c1) return [];
                double q00 = V(r0, c0), q01 = V(r0, c1), q10 = V(r1, c0), q11 = V(r1, c1);
                return cells.Select(c =>
                {
                    var ty = r1 == r0 ? 0 : (double)(c.Row - r0) / (r1 - r0);
                    var tx = c1 == c0 ? 0 : (double)(c.Col - c0) / (c1 - c0);
                    return (c, (1 - ty) * ((1 - tx) * q00 + tx * q01) + ty * ((1 - tx) * q10 + tx * q11));
                });
            }
            case MapOperationKind.Linearize:
                return cells.GroupBy(c => c.Row).SelectMany(g =>
                {
                    int a = g.Min(c => c.Col), b = g.Max(c => c.Col);
                    double va = V(g.Key, a), vb = V(g.Key, b);
                    return g.Select(c => (c, b == a ? va : va + (vb - va) * (c.Col - a) / (b - a)));
                });
            case MapOperationKind.Smooth:
            {
                var set = cells.ToHashSet();
                return cells.Select(c =>
                {
                    double sum = 0; var n = 0;
                    for (var dr = -1; dr <= 1; dr++)
                    for (var dc = -1; dc <= 1; dc++)
                    {
                        var nc = new Cell(c.Row + dr, c.Col + dc);
                        if (!set.Contains(nc)) continue;
                        sum += V(nc.Row, nc.Col); n++;
                    }
                    return (c, sum / n);
                });
            }
            default: throw new DefinitionException($"Unknown map operation {op.Kind}");
        }
    }

    private static int Index(MapDefinition def, Cell c) => def.Order == ValueOrder.RowMajor ? c.Row * def.Cols + c.Col : c.Col * def.Rows + c.Row;

    private static byte[] BytesOf(CalibrationMap map, MapDefinition def, Cell c, int size)
    {
        // Re-encode the current raw value: identical to the file bytes for integer types.
        var bytes = new byte[size];
        ValueReader.WriteRaw(bytes, 0, def.DataType, def.Endian, map.Raw[c.Row * def.Cols + c.Col]);
        return bytes;
    }

    private static (double Min, double Max) Range(DataType t) => t switch
    {
        DataType.UInt8 => (0, byte.MaxValue),
        DataType.Int8 => (sbyte.MinValue, sbyte.MaxValue),
        DataType.UInt16 => (0, ushort.MaxValue),
        DataType.Int16 => (short.MinValue, short.MaxValue),
        DataType.UInt32 => (0, uint.MaxValue),
        DataType.Int32 => (int.MinValue, int.MaxValue),
        _ => (float.MinValue, float.MaxValue),
    };
}
