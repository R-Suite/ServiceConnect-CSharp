using System;
using R.MessageBus.Client.RabbitMQ;

namespace R.MessageBus.TestPubSub
{
    class Program
    {
        static void Main(string[] args)
        {
            var consumerBus = new Bus();
            consumerBus.StartConsuming("App.config", "TestPubSubEndPoint");

            var publisher = new Publisher("App.config", "TestPubSubEndPoint");

            publisher.Publish(new TestMessage(Guid.NewGuid()));
        }
    }
}
