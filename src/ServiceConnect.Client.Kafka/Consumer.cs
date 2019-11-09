using System;
using ServiceConnect.Interfaces;
using System.Collections.Generic;
using Confluent.Kafka;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ServiceConnect.Client.Kafka
{
     public class Consumer : IConsumer 
     {
        private readonly ILogger _logger;
        private IConsumer<Ignore, string> _commandConsumer;
        private IList<IConsumer<Ignore, string>> _eventConsumers;
        private ConsumerEventHandler _eventHandler;

        public Consumer(ILogger logger)
        {
            _logger = logger;
            _eventConsumers = new List<IConsumer<Ignore, string>>();
        }

        public void StartConsuming(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler, IConfiguration config)
        {
            _eventHandler = eventHandler;

            // Setup command processor
            var commandConfig = new ConsumerConfig
            {
                GroupId = queueName,
                BootstrapServers = config.TransportSettings.Host,
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false
            };

            _commandConsumer = new ConsumerBuilder<Ignore, string>(commandConfig).Build();
            _commandConsumer.Subscribe(queueName);

            new Task(() => ConsumeMessages(_commandConsumer).GetAwaiter().GetResult()).Start();
            
            // Setup event processor
            foreach (var type in messageTypes)
            {
                var cfg = new ConsumerConfig
                {
                    GroupId = queueName,
                    BootstrapServers = config.TransportSettings.Host,
                    AutoOffsetReset = AutoOffsetReset.Earliest,
                    EnableAutoCommit = false
                };
                var consumer = new ConsumerBuilder<Ignore, string>(cfg).Build();
                consumer.Subscribe(queueName);
                _eventConsumers.Add(consumer);

                new Task(() => ConsumeMessages(consumer).GetAwaiter().GetResult()).Start();
            }
        }

        private async Task ConsumeMessages(IConsumer<Ignore, string> consumer)
        {
            try
            {                
                while(true)
                {
                    try
                    {
                        var message = JsonConvert.DeserializeObject<MessageWrapper>(consumer.Consume().Value);
                        var headers = message.Headers;
                        await _eventHandler(message.Message, (string)headers["TypeName"], headers);
                    }
                    catch (ConsumeException ex)
                    {
                        // Send to error topic
                        _logger.Error("Error consuming message", ex);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Error consuming message", ex);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                consumer.Close();
            }
        }

        public void Dispose()
        {
            _commandConsumer.Dispose();

            foreach(var consumer in _eventConsumers)
            {
                consumer.Dispose();
            }
        }
     }
}
