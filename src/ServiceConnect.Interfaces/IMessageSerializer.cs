namespace ServiceConnect.Interfaces;

public interface IMessageSerializer
{
    byte[] Serialize<T>(T message) where T : Message;
    void Serialize<T>(T message, System.Buffers.IBufferWriter<byte> output) where T : Message;
    T Deserialize<T>(byte[] data) where T : Message;
    T Deserialize<T>(ReadOnlySpan<byte> data) where T : Message;
    object Deserialize(byte[] data, Type type);
    object Deserialize(ReadOnlySpan<byte> data, Type type);
    object Deserialize(ReadOnlyMemory<byte> data, Type type)
        => Deserialize(data.ToArray(), type);
    T Deserialize<T>(ReadOnlyMemory<byte> data) where T : Message
        => (T)Deserialize(data, typeof(T));
    object Deserialize(in System.Buffers.ReadOnlySequence<byte> data, Type type)
        => Deserialize(System.Buffers.BuffersExtensions.ToArray(data), type);
}
