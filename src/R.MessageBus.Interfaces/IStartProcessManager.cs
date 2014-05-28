namespace R.MessageBus.Interfaces
{
    public interface IStartProcessManager<TMessage> where TMessage : Message
    {
        IConsumeContext Context { get; set; }
        void Execute(TMessage message);
    }
}
