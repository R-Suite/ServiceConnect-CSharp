namespace R.MessageBus.Interfaces
{
    public interface IMessageHandlerProcessor
    {
        void ProcessMessage<T>(T message, IConsumeContext context) where T : Message;
    }
}