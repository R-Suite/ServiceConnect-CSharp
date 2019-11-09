using System;
using ServiceConnect.Interfaces;
using System.Collections.Generic;
using Confluent.Kafka;

namespace ServiceConnect.Client.Kafka
{
     public class Consumer : IConsumer 
     {
        private readonly ILogger _logger;

        public Consumer(ILogger logger)
        {
            _logger = logger;
        }

        public void StartConsuming(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler, IConfiguration config)
        {
            
        }

        public void Dispose() {

        }
     }
}
