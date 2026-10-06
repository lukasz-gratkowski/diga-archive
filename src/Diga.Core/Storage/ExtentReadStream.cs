using L = Diga.Core.Localization.AppText;
namespace Diga.Core.Storage;

/// <summary>Independent, seekable read cursor over validated disk extents; never writes to the device.</summary>
internal sealed class ExtentReadStream : Stream
{
    private readonly DiskByteSource _source;
    private readonly DataExtent[] _extents;
    private readonly long[] _starts;
    private readonly long _length;
    private long _position;
    private bool _disposed;

    internal ExtentReadStream(DiskByteSource source, IReadOnlyList<DataExtent> extents, long length)
    {
        _source = source;
        _length = length;
        _extents = extents.ToArray();
        _starts = new long[_extents.Length];
        long total = 0;
        for (int i = 0; i < _extents.Length; i++)
        {
            var extent = _extents[i];
            if (extent.Length <= 0) throw new InvalidDataException(L.T("Core.Storage.Extent.LengthInvalid"));
            if (!extent.ZeroFill) source.CheckRange(extent.Offset, extent.Length);
            _starts[i] = total;
            total = checked(total + extent.Length);
        }
        if (length < 0 || total < length) throw new InvalidDataException(L.T("Core.Storage.Extent.AllocationShort"));
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length { get { CheckDisposed(); return _length; } }
    public override long Position { get { CheckDisposed(); return _position; } set => Seek(value, SeekOrigin.Begin); }
    private void CheckDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        CheckDisposed();
        if (_position >= _length || buffer.IsEmpty) return 0;
        int wanted = (int)Math.Min(buffer.Length, _length - _position);
        int copied = 0;
        while (copied < wanted)
        {
            int index = Array.BinarySearch(_starts, _position);
            if (index < 0) index = ~index - 1;
            if (index < 0 || index >= _extents.Length) throw new InvalidDataException(L.T("Core.Storage.Extent.AllocationIncomplete"));
            DataExtent extent = _extents[index];
            long within = _position - _starts[index];
            int count = (int)Math.Min(wanted - copied, extent.Length - within);
            if (count <= 0) throw new InvalidDataException(L.T("Core.Storage.Extent.CannotAdvance"));
            if (extent.ZeroFill) buffer.Slice(copied, count).Clear();
            else _source.ReadExactly(extent.Offset + within, buffer.Slice(copied, count));
            _position += count;
            copied += count;
        }
        return copied;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        CheckDisposed();
        long next;
        try { next = checked(origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, SeekOrigin.End => _length + offset, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }); }
        catch (OverflowException ex) { throw new IOException(L.T("Core.Storage.Extent.SeekOutOfRange"), ex); }
        if (next < 0) throw new IOException(L.T("Core.Storage.Extent.SeekBeforeStart"));
        _position = next;
        return next;
    }
    public override void Flush() { CheckDisposed(); }
    public override void SetLength(long value) => throw new NotSupportedException(L.T("Core.Storage.Extent.ReadOnly"));
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(L.T("Core.Storage.Extent.ReadOnly"));
    protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
}
