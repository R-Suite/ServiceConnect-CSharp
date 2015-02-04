namespace R.MessageBus.Interfaces
{
    public interface IStreamProcessor
    {
        void ProcessMessage<T>(T message, IMessageBusReadStream stream) where T : Message;
    }
}