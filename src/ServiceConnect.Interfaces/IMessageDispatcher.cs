namespace ServiceConnect.Interfaces;

public interface IMessageDispatcher
{
    Task<ConsumeEventResult> Dispatch(byte[] messageBytes, string messageType, IDictionary<string, object> headers);
}
