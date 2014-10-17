namespace R.MessageBus.Interfaces
{
    public interface IMessageHandlerProcessor
    {
        void ProcessMessage<T>(string message, IConsumeContext context) where T : Message;
    }
}