namespace R.MessageBus.Interfaces
{
    public interface IMessageHandler<TMessage> where TMessage : Message
    {
        void Execute(TMessage command);
    }
}
