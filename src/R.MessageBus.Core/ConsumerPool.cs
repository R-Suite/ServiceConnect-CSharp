using System;
using System.Collections.Generic;
using System.Threading;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    public class ConsumerPool : IConsumerPool
    {
        private readonly List<IConsumer> _consumers = new List<IConsumer>(); 

        public void AddConsumer(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler, IConsumer consumer)
        {
            new Thread(() =>
            {
                
            }).Start();
            consumer.StartConsuming(eventHandler, queueName);
            foreach (string messageType in messageTypes)
            {
                consumer.ConsumeMessageType(messageType);
            }
            _consumers.Add(consumer);
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