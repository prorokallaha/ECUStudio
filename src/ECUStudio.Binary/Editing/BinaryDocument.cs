using ECUStudio.Core;

namespace ECUStudio.Binary.Editing;

/// <summary>One contiguous run of changed bytes. <see cref="Old"/> is what the working buffer held before the edit.</summary>
public sealed record BytePatch(int Address, byte[] Old, byte[] New)
{
    public int Length => New.Length;
}

/// <summary>One undoable user action (a hex edit, a map operation, a revert). Stored with the project.</summary>
public sealed record EditOperation
{
    public required Guid Id { get; init; }
    public required DateTimeOffset At { get; init; }
    /// <summary>"hex", "map", "revert" …</summary>
    public required string Source { get; init; }
    public string? MapId { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<BytePatch> Patches { get; init; }

    public int ChangedBytes => Patches.Sum(p => p.Length);
}

/// <summary>Write request: bytes to place at an address (no insertion: the length of the file never changes).</summary>
public readonly record struct ByteWrite(int Address, byte[] Bytes);

/// <summary>
/// Editable binary built from three parts: the original buffer (never modified), the ordered patch history and the
/// working buffer derived from both. Edits overwrite bytes in place; nothing can shift data, change the size or
/// remove padding. Undo restores the exact previous bytes, so undoing everything is byte-identical to the original.
/// </summary>
public sealed class BinaryDocument
{
    private readonly byte[] _original;
    private readonly byte[] _working;
    private readonly List<EditOperation> _undo = [];
    private readonly List<EditOperation> _redo = [];

    public BinaryDocument(byte[] original, IEnumerable<EditOperation>? history = null, IEnumerable<EditOperation>? redo = null)
    {
        _original = (byte[])original.Clone();
        _working = (byte[])original.Clone();
        OriginalSha256 = Hashing.Sha256Hex(_original);
        foreach (var op in history ?? [])
        {
            Write(op, forward: true);
            _undo.Add(op);
        }
        // Redo entries were recorded against the state reached after the full history.
        var check = (byte[])_working.Clone();
        foreach (var op in (redo ?? []).Reverse())
        {
            foreach (var p in op.Patches)
            {
                if (!check.AsSpan(p.Address, p.Length).SequenceEqual(p.Old)) throw new BinaryEditException($"Stored redo step '{op.Description}' does not fit the current bytes");
                p.New.CopyTo(check, p.Address);
            }
            _redo.Insert(0, op);
        }
    }

    public int Length => _original.Length;
    public string OriginalSha256 { get; }
    public string WorkingSha256 => Hashing.Sha256Hex(_working);
    public ReadOnlySpan<byte> Original => _original;
    public ReadOnlySpan<byte> Working => _working;
    public IReadOnlyList<EditOperation> History => _undo;
    public IReadOnlyList<EditOperation> RedoStack => _redo;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsModified => !_working.AsSpan().SequenceEqual(_original);

    public byte[] WorkingCopy() => (byte[])_working.Clone();

    /// <summary>Applies writes as one operation. Returns null when no byte actually changes.</summary>
    public EditOperation? Apply(string source, string description, IEnumerable<ByteWrite> writes, string? mapId = null)
    {
        var target = (byte[])_working.Clone();
        foreach (var w in writes)
        {
            if (w.Address < 0 || w.Bytes.Length == 0 || (long)w.Address + w.Bytes.Length > _working.Length)
                throw new BinaryEditException($"Write of {w.Bytes.Length} byte(s) at 0x{w.Address:X} is outside the {_working.Length}-byte file");
            w.Bytes.CopyTo(target, w.Address);
        }
        var patches = Diff(_working, target);
        if (patches.Count == 0) return null;
        var op = new EditOperation { Id = Guid.NewGuid(), At = DateTimeOffset.UtcNow, Source = source, MapId = mapId, Description = description, Patches = patches };
        Write(op, forward: true);
        _undo.Add(op);
        _redo.Clear();
        return op;
    }

    public EditOperation? Undo()
    {
        if (_undo.Count == 0) return null;
        var op = _undo[^1];
        Write(op, forward: false);
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(op);
        return op;
    }

    public EditOperation? Redo()
    {
        if (_redo.Count == 0) return null;
        var op = _redo[^1];
        Write(op, forward: true);
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(op);
        return op;
    }

    /// <summary>Restores the original bytes in the given ranges (as a new, undoable operation).</summary>
    public EditOperation? Revert(IEnumerable<ByteRange> ranges, string description, string? mapId = null) =>
        Apply("revert", description, ranges.Select(r =>
        {
            if (r.Start < 0 || r.Length <= 0 || (long)r.Start + r.Length > _original.Length)
                throw new BinaryEditException($"Range 0x{r.Start:X}+{r.Length} is outside the file");
            return new ByteWrite(r.Start, _original.AsSpan(r.Start, r.Length).ToArray());
        }).ToList(), mapId);

    public EditOperation? RevertAll() => Revert(ChangedRanges(), "Revert all changes");

    /// <summary>Contiguous ranges where the working buffer differs from the original.</summary>
    public IReadOnlyList<ByteRange> ChangedRanges() => Diff(_original, _working).Select(p => new ByteRange(p.Address, p.Length)).ToList();

    public int ChangedBytes()
    {
        var n = 0;
        for (var i = 0; i < _working.Length; i++) if (_working[i] != _original[i]) n++;
        return n;
    }

    private void Write(EditOperation op, bool forward)
    {
        // Verify every patch first so a mismatching operation leaves the buffer untouched.
        foreach (var p in op.Patches)
        {
            if (p.Old.Length != p.New.Length || p.Address < 0 || (long)p.Address + p.Length > _working.Length)
                throw new BinaryEditException($"Edit '{op.Description}' has an invalid patch at 0x{p.Address:X}");
            var expected = forward ? p.Old : p.New;
            if (!_working.AsSpan(p.Address, p.Length).SequenceEqual(expected))
                throw new BinaryEditException($"Edit '{op.Description}' does not fit the current bytes at 0x{p.Address:X}");
        }
        foreach (var p in op.Patches) (forward ? p.New : p.Old).CopyTo(_working, p.Address);
    }

    private static List<BytePatch> Diff(byte[] from, byte[] to)
    {
        var patches = new List<BytePatch>();
        var i = 0;
        while (i < from.Length)
        {
            if (from[i] == to[i]) { i++; continue; }
            var start = i;
            while (i < from.Length && from[i] != to[i]) i++;
            patches.Add(new BytePatch(start, from.AsSpan(start, i - start).ToArray(), to.AsSpan(start, i - start).ToArray()));
        }
        return patches;
    }
}

public sealed class BinaryEditException(string message) : EcuStudioException("BINARY_EDIT", message, 409);
