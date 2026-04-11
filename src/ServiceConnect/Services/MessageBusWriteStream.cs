using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public class MessageBusWriteStream : IMessageBusWriteStream
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
            [HeaderKeys.MessageType] = "ByteStream"
        };
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        if (_closed) throw new ObjectDisposedException(nameof(MessageBusWriteStream));

        var packet = new byte[count];
        Array.Copy(buffer, offset, packet, 0, count);

        var headers = new Dictionary<string, string>(_baseHeaders)
        {
            [HeaderKeys.PacketNumber] = _packetNumber.ToString()
        };

        Task.Run(() => _producer.SendBytesAsync(_endpoint, packet, headers)).GetAwaiter().GetResult();
        _packetNumber++;
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;

        var headers = new Dictionary<string, string>(_baseHeaders)
        {
            [HeaderKeys.PacketNumber] = _packetNumber.ToString(),
            [HeaderKeys.LastPacketNumber] = _packetNumber.ToString()
        };

        Task.Run(() => _producer.SendBytesAsync(_endpoint, Array.Empty<byte>(), headers)).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Close();
    }
}
