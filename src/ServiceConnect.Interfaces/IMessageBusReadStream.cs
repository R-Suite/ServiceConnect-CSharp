namespace ServiceConnect.Interfaces;

public delegate void MessageBusStreamComplete(string sequenceId);

public interface IMessageBusReadStream
{
    void Write(byte[] data, long packetNumber);
    byte[] Read();
    bool IsComplete();
    long LastPacketNumber { get; set; }
    MessageBusStreamComplete CompleteEventHandler { get; set; }
    string SequenceId { get; set; }
    int HandlerCount { get; set; }
}
