using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public sealed class MessageBusWriteStream : IMessageBusWriteStream
{
    private readonly IProducer _producer;
    private readonly string _endpoint;
    private readonly string _sequenceId;
    private readonly Dictionary<string, string> _baseHeaders;
    private long _packetNumber;
    private bool _closed;

    public MessageBusWriteStream(IProducer producer, string endpoint, Type messageType)
    {
        _producer = producer;
        _endpoint = endpoint;
        _sequenceId = Guid.NewGuid().ToString();
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
        ObjectDisposedException.ThrowIf(_closed, this);

        var packet = new byte[count];
        Array.Copy(buffer, offset, packet, 0, count);

        var packetNum = Interlocked.Increment(ref _packetNumber) - 1;

        // Pre-size the dict to avoid rehash during the copy (P-01). A separate dict
        // per packet is required because the producer may mutate / enqueue the
        // dictionary asynchronously, so reuse would race with concurrent writes.
        var headers = new Dictionary<string, string>(_baseHeaders.Count + 1);
        foreach (var kvp in _baseHeaders) headers[kvp.Key] = kvp.Value;
        headers[HeaderKeys.PacketNumber] = packetNum.ToString();

        await _producer.SendBytesAsync(_endpoint, packet, headers).ConfigureAwait(false);
    }

    public async Task CloseAsync()
    {
        if (_closed) return;
        _closed = true;

        var packetNum = Interlocked.Read(ref _packetNumber);

        var headers = new Dictionary<string, string>(_baseHeaders.Count + 2);
        foreach (var kvp in _baseHeaders) headers[kvp.Key] = kvp.Value;
        headers[HeaderKeys.PacketNumber] = packetNum.ToString();
        headers[HeaderKeys.LastPacketNumber] = packetNum.ToString();

        await _producer.SendBytesAsync(_endpoint, [], headers).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
    }
}
