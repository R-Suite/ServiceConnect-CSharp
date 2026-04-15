using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public sealed class MessageBusWriteStream : IMessageBusWriteStream
{
    private readonly IProducer _producer;
    private readonly string _endpoint;
    private readonly string _sequenceId;
    private readonly Dictionary<string, string> _baseHeaders;
    private long _packetNumber;
    private int _closedFlag;

    public MessageBusWriteStream(IProducer producer, string endpoint, Type messageType)
    {
        _producer = producer;
        _endpoint = endpoint;
        _sequenceId = FormatGuid(Guid.NewGuid());
        _baseHeaders = new Dictionary<string, string>
        {
            [HeaderKeys.SequenceId] = _sequenceId,
            [HeaderKeys.FullTypeName] = messageType.AssemblyQualifiedName!,
            [HeaderKeys.TypeName] = messageType.FullName!,
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream
        };
    }

    public async Task WriteAsync(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closedFlag) == 1, this);
        ArgumentNullException.ThrowIfNull(buffer);

        if ((uint)offset > (uint)buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if ((uint)count > (uint)(buffer.Length - offset))
            throw new ArgumentOutOfRangeException(nameof(count));

        var packet = new byte[count];
        Array.Copy(buffer, offset, packet, 0, count);

        var packetNum = Interlocked.Increment(ref _packetNumber) - 1;

        // Pre-size the dict to avoid rehash during the copy (P-01). A separate dict
        // per packet is required because the producer may mutate / enqueue the
        // dictionary asynchronously, so reuse would race with concurrent writes.
        var headers = new Dictionary<string, string>(_baseHeaders.Count + 1);
        foreach (var kvp in _baseHeaders) headers[kvp.Key] = kvp.Value;
        headers[HeaderKeys.PacketNumber] = FormatInt64(packetNum);

        await _producer.SendBytesAsync(_endpoint, packet, headers).ConfigureAwait(false);
    }

    public async Task CloseAsync()
    {
        if (Interlocked.CompareExchange(ref _closedFlag, 1, 0) != 0) return;

        // _packetNumber was post-incremented on each WriteAsync, so after N data
        // packets (indices 0..N-1) its value is N. The close packet reuses that value
        // as its own index, and LastPacketNumber equals the count. The reader's
        // IsComplete loop checks 0..LastPacketNumber inclusive so the empty close
        // packet fills that final slot (L-5). Changing the close-packet payload in the
        // future would break this invariant — see MessageBusReadStream.Read().
        var packetNum = Interlocked.Read(ref _packetNumber);

        var headers = new Dictionary<string, string>(_baseHeaders.Count + 2);
        foreach (var kvp in _baseHeaders) headers[kvp.Key] = kvp.Value;
        var packetNumString = FormatInt64(packetNum);
        headers[HeaderKeys.PacketNumber] = packetNumString;
        headers[HeaderKeys.LastPacketNumber] = packetNumString;

        await _producer.SendBytesAsync(_endpoint, [], headers).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }

    private static string FormatGuid(Guid value)
    {
        Span<char> buffer = stackalloc char[36];
        value.TryFormat(buffer, out var charsWritten);
        return new string(buffer[..charsWritten]);
    }

    private static string FormatInt64(long value)
    {
        Span<char> buffer = stackalloc char[20];
        value.TryFormat(buffer, out var charsWritten);
        return new string(buffer[..charsWritten]);
    }
}
