namespace ServiceConnect.Interfaces;

public interface IStreamHandler<TMessage> where TMessage : Message
{
    IMessageBusReadStream Stream { get; set; }
    void Execute(TMessage stream);
}
