namespace R.MessageBus.Interfaces
{
    public interface IProducer
    {
        void Publish<T>(T message) where T : Message;
        void Send<T>(T message) where T : Message;
        void Send<T>(string endPoint, T message) where T : Message;
        void Disconnect();
        void Dispose();
    }
}