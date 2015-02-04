using System;

namespace R.MessageBus.Interfaces
{
    public delegate void MessageBusStreamComplete(string sequenceId);

    public interface IMessageBusReadStream
    {
        void Write(byte[] data, Int64 packetNumber);
        byte[] Read();
        bool IsComplete();
        Int64 LastPacketNumber { get; set; }
        MessageBusStreamComplete CompleteEventHandler { get; set; }
        string SequenceId { get; set; }
        int HandlerCount { get; set; }
    }
}