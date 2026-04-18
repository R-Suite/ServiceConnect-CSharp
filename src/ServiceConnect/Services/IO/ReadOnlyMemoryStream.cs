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
    public override long Length => _buffer.Length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        int remaining = _buffer.Length - _position;
        if (remaining <= 0) return 0;
        int toCopy = Math.Min(remaining, count);
        _buffer.Span.Slice(_position, toCopy).CopyTo(buffer.AsSpan(offset, toCopy));
        _position += toCopy;
        return toCopy;
    }

    public override int Read(Span<byte> buffer)
    {
        int remaining = _buffer.Length - _position;
        if (remaining <= 0) return 0;
        int toCopy = Math.Min(remaining, buffer.Length);
        _buffer.Span.Slice(_position, toCopy).CopyTo(buffer[..toCopy]);
        _position += toCopy;
        return toCopy;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
