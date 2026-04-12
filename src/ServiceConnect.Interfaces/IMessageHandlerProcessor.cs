namespace ServiceConnect.Interfaces;

public interface IMessageHandlerProcessor
{
    Task ProcessMessage<T>(string message, IConsumeContext context) where T : Message;
}
