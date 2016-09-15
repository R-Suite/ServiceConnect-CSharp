using System;
using System.Collections.Generic;
using System.Threading;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Core
{
    public class ConsumerPool : IConsumerPool
    {
        private readonly List<IConsumer> _consumers = new List<IConsumer>();

        public void AddConsumer(string queueName, IDictionary<string, IList<string>> msgRoutingKeysDict, ConsumerEventHandler eventHandler, IConfiguration config)
        {
            new Thread(() =>
            {
                var consumer = config.GetConsumer();
                consumer.StartConsuming(eventHandler, queueName);
                foreach (var msgRoutingKeys in msgRoutingKeysDict)
                {
                    consumer.ConsumeMessageType(msgRoutingKeys);
                }
                _consumers.Add(consumer);
            }).Start();
        }

        public void Dispose()
        {
            foreach (IConsumer consumer in _consumers)
            {
                consumer.Dispose();
            }
        }
    }
}