namespace R.MessageBus.Interfaces
{
    public interface IMessageHandlerProcessor
    {
        void ProcessMessage<T>(T message) where T : Message;
    }
}