using System.Buffers;

namespace ServiceConnect.Services.IO;

internal sealed class ReadOnlySequenceStream(ReadOnlySequence<byte> sequence) : Stream
{
    private ReadOnlySequence<byte> _remaining = sequence;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    // Stream contract: Length must throw when CanSeek is false. Callers that need the
    // payload size already have it on the ReadOnlySequence<byte> they passed in.
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining.IsEmpty)
        {
            return 0;
        }

        int toRead = (int)Math.Min(count, _remaining.Length);
        _remaining.Slice(0, toRead).CopyTo(buffer.AsSpan(offset, toRead));
        _remaining = _remaining.Slice(toRead);
        return toRead;
    }

    public override int Read(Span<byte> buffer)
    {
        if (_remaining.IsEmpty)
        {
            return 0;
        }

        int toRead = (int)Math.Min(buffer.Length, _remaining.Length);
        _remaining.Slice(0, toRead).CopyTo(buffer[..toRead]);
        _remaining = _remaining.Slice(toRead);
        return toRead;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
