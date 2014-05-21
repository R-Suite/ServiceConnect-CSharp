namespace R.MessageBus.Interfaces
{
    public interface IMessageHandler<TMessage> where TMessage : Message
    {
        IConsumeContext Context { get; set; }
        void Execute(TMessage message);
    }
}
