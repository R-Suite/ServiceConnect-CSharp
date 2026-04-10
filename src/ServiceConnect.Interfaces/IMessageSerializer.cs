namespace ServiceConnect.Interfaces;

public interface IMessageSerializer
{
    byte[] Serialize<T>(T message) where T : Message;
    T Deserialize<T>(byte[] data) where T : Message;
    object Deserialize(byte[] data, Type type);
}
