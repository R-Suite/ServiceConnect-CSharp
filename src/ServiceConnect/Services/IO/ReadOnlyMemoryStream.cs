namespace ServiceConnect.Services.IO;

internal sealed class ReadOnlyMemoryStream : Stream
{
    private readonly ReadOnlyMemory<byte> _buffer;
    private int _position;

    public ReadOnlyMemoryStream(ReadOnlyMemory<byte> buffer)
    {
        _buffer = buffer;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    // Stream contract: Length must throw when CanSeek is false. Callers that need the
    // payload size already have it at the ReadOnlyMemory<byte> they passed in.
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int remaining = _buffer.Length - _position;
        if (remaining <= 0) return 0;
        int toRead = Math.Min(count, remaining);
        _buffer.Span.Slice(_position, toRead).CopyTo(buffer.AsSpan(offset, toRead));
        _position += toRead;
        return toRead;
    }

    public override int Read(Span<byte> buffer)
    {
        int remaining = _buffer.Length - _position;
        if (remaining <= 0) return 0;
        int toRead = Math.Min(buffer.Length, remaining);
        _buffer.Span.Slice(_position, toRead).CopyTo(buffer.Slice(0, toRead));
        _position += toRead;
        return toRead;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
