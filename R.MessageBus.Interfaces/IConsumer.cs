namespace R.MessageBus.Interfaces
{
    public interface IConsumer
    {
        void StartConsuming(ConsumerEventHandler messageReceived, string routingKey, string queueName = null);
        void StopConsuming();
        void Dispose();
    }
}