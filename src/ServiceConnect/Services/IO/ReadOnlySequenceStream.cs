using System.Buffers;

namespace ServiceConnect.Services.IO;

internal sealed class ReadOnlySequenceStream : Stream
{
    private ReadOnlySequence<byte> _remaining;
    private readonly long _totalLength;

    public ReadOnlySequenceStream(ReadOnlySequence<byte> sequence)
    {
        _remaining = sequence;
        _totalLength = sequence.Length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _totalLength;
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining.IsEmpty) return 0;
        int toRead = (int)Math.Min(count, _remaining.Length);
        _remaining.Slice(0, toRead).CopyTo(buffer.AsSpan(offset, toRead));
        _remaining = _remaining.Slice(toRead);
        return toRead;
    }

    public override int Read(Span<byte> buffer)
    {
        if (_remaining.IsEmpty) return 0;
        int toRead = (int)Math.Min(buffer.Length, _remaining.Length);
        _remaining.Slice(0, toRead).CopyTo(buffer.Slice(0, toRead));
        _remaining = _remaining.Slice(toRead);
        return toRead;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
