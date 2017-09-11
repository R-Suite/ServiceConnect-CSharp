using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Client.RabbitMQ
{
    public class Consumer : IConsumer
    {
        private readonly ConcurrentBag<Client> _clients = new ConcurrentBag<Client>();
        private Connection _connection;

        public void StartConsuming(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler, IConfiguration config)
        {
            if(_connection == null)
            {
                _connection = new Connection(config.TransportSettings, queueName);
            }

            var threadCount = config.Threads;

            for (int i = 0; i < threadCount; i++)
            {
                new Thread(() =>
                {
                    var client = new Client(_connection, config.TransportSettings);
                    client.StartConsuming(eventHandler, queueName);
                    foreach (string messageType in messageTypes)
                    {
                        client.ConsumeMessageType(messageType);
                    }
                    _clients.Add(client);
                }).Start();
            }
        }

        public void Dispose()
        {
            foreach (Client consumer in _clients)
            {
                consumer.Dispose();
            }

            _connection.Dispose();
        }
    }
}