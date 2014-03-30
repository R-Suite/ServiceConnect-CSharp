namespace R.MessageBus.Interfaces
{
    public interface IConsumer
    {
        void StartConsuming(ConsumerEventHandler messageReceived, string messageTypeName, string queueName);
        void StopConsuming();
        void Dispose();
    }
}