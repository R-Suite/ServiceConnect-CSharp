namespace R.MessageBus.Interfaces
{
    public interface IStartProcessManager<TMessage> : IMessageHandler<TMessage> where TMessage : Message
    {
    }
}
