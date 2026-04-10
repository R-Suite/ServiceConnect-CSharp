namespace ServiceConnect.Interfaces;

public interface IStreamManager
{
    IMessageBusWriteStream CreateStream<T>(string endpoint, T message) where T : Message;
    Task ProcessStream(byte[] message, Type type, IDictionary<string, object> headers);
}
