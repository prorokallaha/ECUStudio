using System.Buffers;
using System.IO.MemoryMappedFiles;
using ECUStudio.Core;

namespace ECUStudio.Binary;

/// <summary>
/// Immutable ECU image. Data is exposed as <see cref="ReadOnlyMemory{T}"/> so map decoding,
/// scanning and diffing work zero-copy on spans. Large files can be memory-mapped.
/// </summary>
public sealed class BinaryImage : IDisposable
{
    public const int MaxSupportedSize = 16 * 1024 * 1024;

    private readonly IDisposable? _owner;
    private string? _sha256;

    private BinaryImage(ReadOnlyMemory<byte> data, string fileName, IDisposable? owner)
    {
        Data = data;
        FileName = fileName;
        _owner = owner;
    }

    public ReadOnlyMemory<byte> Data { get; }
    public ReadOnlySpan<byte> Span => Data.Span;
    public int Length => Data.Length;
    public string FileName { get; }
    public string Sha256 => _sha256 ??= Hashing.Sha256Hex(Data.Span);

    public static BinaryImage FromBytes(byte[] data, string fileName = "image.bin")
    {
        Validate(data.Length, fileName);
        return new BinaryImage(data, fileName, null);
    }

    /// <summary>Loads a file; files above <paramref name="mmapThreshold"/> are memory-mapped instead of copied.</summary>
    public static BinaryImage FromFile(string path, int mmapThreshold = 4 * 1024 * 1024)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new InvalidBinaryException($"File not found: {path}");
        Validate(info.Length, info.Name);
        if (info.Length < mmapThreshold)
            return new BinaryImage(File.ReadAllBytes(path), info.Name, null);

        var manager = new MemoryMappedFileMemoryManager(path, (int)info.Length);
        return new BinaryImage(manager.Memory, info.Name, manager);
    }

    private static void Validate(long length, string fileName)
    {
        if (length == 0) throw new InvalidBinaryException($"'{fileName}' is empty");
        if (length > MaxSupportedSize) throw new InvalidBinaryException($"'{fileName}' is {length} bytes; maximum supported size is {MaxSupportedSize}");
    }

    public ReadOnlySpan<byte> Slice(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > Length)
            throw new ArgumentOutOfRangeException(nameof(offset), $"Range 0x{offset:X}+{length} is outside image of {Length} bytes");
        return Data.Span.Slice(offset, length);
    }

    public bool Contains(int offset, int length) => offset >= 0 && length >= 0 && offset + length <= Length;

    public void Dispose() => _owner?.Dispose();

    private sealed unsafe class MemoryMappedFileMemoryManager : MemoryManager<byte>
    {
        private readonly MemoryMappedFile _file;
        private readonly MemoryMappedViewAccessor _view;
        private readonly byte* _pointer;
        private readonly int _length;

        public MemoryMappedFileMemoryManager(string path, int length)
        {
            _length = length;
            _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _view = _file.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
            byte* ptr = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            _pointer = ptr + _view.PointerOffset;
        }

        public override Span<byte> GetSpan() => new(_pointer, _length);
        public override MemoryHandle Pin(int elementIndex = 0) => new(_pointer + elementIndex);
        public override void Unpin() { }

        protected override void Dispose(bool disposing)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _file.Dispose();
        }
    }
}
