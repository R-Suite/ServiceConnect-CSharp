using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Common.Logging;
using RabbitMQ.Client;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Client.RabbitMQ
{
    public class Consumer : IConsumer
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(Consumer));

        private IModel _model;
        private bool _durable;
        private int _retryDelay;
        private bool _exclusive;
        private bool _autoDelete;
        private IServiceConnectConnection _connection;
        private ITransportSettings _transportSettings;
        private IDictionary<string, object> _queueArguments;
        private readonly ConcurrentBag<Client> _clients = new ConcurrentBag<Client>();

        public Consumer()
        {
        }

        public Consumer(IServiceConnectConnection connection)
        {
            _connection = connection;
        }

        public void StartConsuming(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler, IConfiguration config)
        {
            _transportSettings = config.TransportSettings;
            _durable = !_transportSettings.ClientSettings.ContainsKey("Durable") || (bool)_transportSettings.ClientSettings["Durable"];
            _exclusive = _transportSettings.ClientSettings.ContainsKey("Exclusive") && (bool)_transportSettings.ClientSettings["Exclusive"];
            _autoDelete = _transportSettings.ClientSettings.ContainsKey("AutoDelete") && (bool)_transportSettings.ClientSettings["AutoDelete"];
            _queueArguments = _transportSettings.ClientSettings.ContainsKey("Arguments") ? (IDictionary<string, object>)_transportSettings.ClientSettings["Arguments"] : new Dictionary<string, object>();
            _retryDelay = _transportSettings.RetryDelay;

            if (_connection == null)
            {
                _connection = new Connection(config.TransportSettings, queueName);
            }

            if (_model == null)
            {
                _model = _connection.CreateModel();
            }

            // Configure exchanges
            foreach (string messageType in messageTypes)
            {
                ConfigureExchange(messageType, "fanout");
            }

            // Configure queue
            ConfigureQueue(queueName);

            // Purge all messages on queue
            if (_transportSettings.PurgeQueueOnStartup)
            {
                Logger.Debug("Purging queue");
                _model.QueuePurge(queueName);
            }

            // Configure retry queue
            ConfigureRetryQueue(queueName);

            // Configure Error Queue/Exchange
            var errorExchange = ConfigureErrorExchange();
            var errorQueue = ConfigureErrorQueue();

            if (!string.IsNullOrEmpty(errorExchange))
            {
                _model.QueueBind(errorQueue, errorExchange, string.Empty, null);
            }

            // Configure Audit Queue/Exchange
            if (_transportSettings.AuditingEnabled)
            {
                var auditExchange = ConfigureAuditExchange();
                var auditQueue = ConfigureAuditQueue();

                if (!string.IsNullOrEmpty(auditExchange))
                {
                    _model.QueueBind(auditQueue, auditExchange, string.Empty, null);
                }
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

            _model.Dispose();
            _connection.Dispose();
        }

        private void ConfigureExchange(string exchangeName, string type)
        {
            try
            {
                // Hard code auto delete and durable to sensible defaults so that producers and consumers dont try to declare exchanges with different settings.
                _model.ExchangeDeclare(exchangeName, type, true, false, null);
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring exchange {0}", ex.Message));
            }
        }

        private void ConfigureQueue(string queueName)
        {
            try
            {
                _model.QueueDeclare(queueName, _durable, _exclusive, _autoDelete, _queueArguments);
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring queue - {0}", ex.Message));
            }
        }

        private void ConfigureRetryQueue(string queueName)
        {
            // When message goes to retry queue, it falls-through to dead-letter exchange (after _retryDelay)
            // dead-letter exchange is of type "direct" and bound to the original queue.
            var retryQueueName = queueName + ".Retries";
            string retryDeadLetterExchangeName = queueName + ".Retries.DeadLetter";

            try
            {
                _model.ExchangeDeclare(retryDeadLetterExchangeName, "direct", _durable, _autoDelete, null);
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring dead letter exchange - {0}", ex.Message));
            }

            try
            {
                _model.QueueBind(queueName, retryDeadLetterExchangeName, retryQueueName); // only redeliver to the original queue (use _queueName as routing key)
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error binding dead letter queue - {0}", ex.Message));
            }

            var arguments = new Dictionary<string, object>
            {
                {"x-dead-letter-exchange", retryDeadLetterExchangeName},
                {"x-message-ttl", _retryDelay}
            };

            try
            {
                // We never have consumers on the retry queue.  Therefore set autodelete to false.
                _model.QueueDeclare(retryQueueName, _durable, false, false, arguments);
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring queue {0}", ex.Message));
            }
        }

        private string ConfigureErrorExchange()
        {
            try
            {
                _model.ExchangeDeclare(_transportSettings.ErrorQueueName, "direct");
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring error exchange {0}", ex.Message));
            }

            return _transportSettings.ErrorQueueName;
        }

        private string ConfigureErrorQueue()
        {
            try
            {
                _model.QueueDeclare(_transportSettings.ErrorQueueName, true, false, false, null);
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring error queue {0}", ex.Message));
            }

            return _transportSettings.ErrorQueueName;
        }

        private string ConfigureAuditExchange()
        {
            try
            {
                _model.ExchangeDeclare(_transportSettings.AuditQueueName, "direct");
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring audit exchange {0}", ex.Message));
            }

            return _transportSettings.AuditQueueName;
        }

        private string ConfigureAuditQueue()
        {
            try
            {
                _model.QueueDeclare(_transportSettings.AuditQueueName, true, false, false, null);
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring audit queue {0}", ex.Message));
            }
            return _transportSettings.AuditQueueName;
        }
    }
}