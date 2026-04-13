namespace ServiceConnect.Interfaces;

public interface IMessageBusReadStream
{
    void Write(byte[] data, long packetNumber);
    byte[] Read();
    bool IsComplete();
    void SetLastPacketNumber(long lastPacketNumber);
    long LastPacketNumber { get; }
    string SequenceId { get; }
}
