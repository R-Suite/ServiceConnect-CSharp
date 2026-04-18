namespace ServiceConnect.Interfaces;

public interface IMessageBusReadStream
{
    void Write(byte[] data, long packetNumber);
    byte[] Read();
    bool IsComplete();
    void SetLastPacketNumber(long lastPacketNumber);
    long LastPacketNumber { get; }
    string SequenceId { get; }

    System.Buffers.ReadOnlySequence<byte> ReadSequence()
        => new System.Buffers.ReadOnlySequence<byte>(Read());
}
