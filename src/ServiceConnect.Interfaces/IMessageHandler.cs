namespace ServiceConnect.Interfaces;

public interface IMessageHandler<in TMessage> where TMessage : Message
{
    IConsumeContext? Context { get; set; }
    Task HandleAsync(TMessage message);
}
