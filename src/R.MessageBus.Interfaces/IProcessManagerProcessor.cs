namespace R.MessageBus.Interfaces
{
    public interface IProcessManagerProcessor
    {
        void ProcessMessage<T>(T message, IConsumeContext context) where T : Message;
    }
}