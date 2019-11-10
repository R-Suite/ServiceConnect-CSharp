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
        private IList<IConsumer<Ignore, string>> _consumers;
        private ConsumerEventHandler _eventHandler;

        public Consumer(ILogger logger)
        {
            _logger = logger;
            _consumers = new List<IConsumer<Ignore, string>>();
        }

        public void StartConsuming(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler, IConfiguration config)
        {
            var clientCount = config.Clients;

            _eventHandler = eventHandler;
            
            for (var i = 0; i < clientCount; i++)
            {
                // Setup command processor
                var commandConfig = new ConsumerConfig
                {
                    GroupId = queueName,
                    BootstrapServers = config.TransportSettings.Host,
                    AutoOffsetReset = AutoOffsetReset.Earliest,
                    EnableAutoCommit = true
                };

                var commandConsumer = new ConsumerBuilder<Ignore, string>(commandConfig).Build();
                commandConsumer.Subscribe(queueName);
                _consumers.Add(commandConsumer);
                
                Task.Run(() => ConsumeMessages(commandConsumer));
                
                if (messageTypes.Count > 0)
                {
                    // Setup event processor
                    var cfg = new ConsumerConfig
                    {
                        GroupId = queueName,
                        BootstrapServers = config.TransportSettings.Host,
                        AutoOffsetReset = AutoOffsetReset.Earliest
                    };
                    var consumer = new ConsumerBuilder<Ignore, string>(cfg).Build();
                    consumer.Subscribe(messageTypes);
                    _consumers.Add(consumer);

                    Task.Run(() => ConsumeMessages(consumer));
                }                
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
                        var res = consumer.Consume();
                        var message = JsonConvert.DeserializeObject<MessageWrapper>(res.Value);
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
            foreach(var consumer in _consumers)
            {
                consumer.Dispose();
            }
        }
     }
}
