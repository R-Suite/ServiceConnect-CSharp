namespace R.MessageBus.Interfaces
{
    public interface IPublisher
    {
        void Publish<T>(T message) where T : Message;
        void Disconnect();
        void Dispose();
    }
}