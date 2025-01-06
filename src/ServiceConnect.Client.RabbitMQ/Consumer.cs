using RabbitMQ.Client;
using ServiceConnect.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ServiceConnect.Client.RabbitMQ
{
    public class Consumer : IConsumer
    {
        private IModel _model;
        private bool _durable;
        private int _retryDelay;
        private bool _exclusive;
        private bool _autoDelete;
        private IServiceConnectConnection _connection;
        private readonly ILogger _logger;
        private ITransportSettings _transportSettings;
        private IDictionary<string, object> _queueArguments;
        private IDictionary<string, object> _retryQueueArguments;
        private IDictionary<string, object> _utilityQueueArguments;
        private readonly ConcurrentBag<Client> _clients = new();

        public Consumer(ILogger logger)
        {
            _logger = logger;
        }

        public Consumer(IServiceConnectConnection connection, ILogger logger)
        {
            _connection = connection;
            _logger = logger;
        }

        public void StartConsuming(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler, IConfiguration config)
        {
            _transportSettings = config.TransportSettings;
            _durable = !_transportSettings.ClientSettings.ContainsKey("Durable") || (bool)_transportSettings.ClientSettings["Durable"];
            _exclusive = _transportSettings.ClientSettings.ContainsKey("Exclusive") && (bool)_transportSettings.ClientSettings["Exclusive"];
            _autoDelete = _transportSettings.ClientSettings.ContainsKey("AutoDelete") && (bool)_transportSettings.ClientSettings["AutoDelete"];
            _queueArguments = _transportSettings.ClientSettings.ContainsKey("Arguments") ? (IDictionary<string, object>)_transportSettings.ClientSettings["Arguments"] : new Dictionary<string, object>();
            _retryQueueArguments = _transportSettings.ClientSettings.ContainsKey("RetryQueueArguments") ? (IDictionary<string, object>)_transportSettings.ClientSettings["RetryQueueArguments"] : new Dictionary<string, object>();
            _utilityQueueArguments = _transportSettings.ClientSettings.ContainsKey("UtilityQueueArguments") ? (IDictionary<string, object>)_transportSettings.ClientSettings["UtilityQueueArguments"] : new Dictionary<string, object>();
            _retryDelay = _transportSettings.RetryDelay;

            _connection ??= new Connection(config.TransportSettings, queueName, _logger);

            _model ??= _connection.CreateModel();

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
                _logger.Debug("Purging queue");
                _ = _model.QueuePurge(queueName);
            }

            // Configure retry queue ( but only if retries are expected )
            if (_transportSettings.MaxRetries > 0)
            {
                ConfigureRetryQueue(queueName);
            }

            // Configure Error Queue/Exchange
            string errorExchange = ConfigureErrorExchange();
            string errorQueue = ConfigureErrorQueue();

            if (!string.IsNullOrEmpty(errorExchange))
            {
                _model.QueueBind(errorQueue, errorExchange, string.Empty, null);
            }

            // Configure Audit Queue/Exchange
            if (_transportSettings.AuditingEnabled)
            {
                string auditExchange = ConfigureAuditExchange();
                string auditQueue = ConfigureAuditQueue();

                if (!string.IsNullOrEmpty(auditExchange))
                {
                    _model.QueueBind(auditQueue, auditExchange, string.Empty, null);
                }
            }

            int clientCount = config.Clients;

            for (int i = 0; i < clientCount; i++)
            {
                Client client = new(_connection, config.TransportSettings, _logger);
                client.StartConsuming(eventHandler, queueName);
                foreach (string messageType in messageTypes)
                {
                    client.ConsumeMessageType(messageType);
                }
                _clients.Add(client);
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
                _logger.Warn(string.Format("Error declaring exchange {0}", ex.Message));
            }
        }

        private void ConfigureQueue(string queueName)
        {
            try
            {
                _ = _model.QueueDeclare(queueName, _durable, _exclusive, _autoDelete, _queueArguments);
            }
            catch (Exception ex)
            {
                _logger.Warn(string.Format("Error declaring queue - {0}", ex.Message));
            }
        }

        private void ConfigureRetryQueue(string queueName)
        {
            // When message goes to retry queue, it falls-through to dead-letter exchange (after _retryDelay)
            // dead-letter exchange is of type "direct" and bound to the original queue.
            string retryQueueName = queueName + ".Retries";
            string retryDeadLetterExchangeName = queueName + ".Retries.DeadLetter";

            try
            {
                _model.ExchangeDeclare(retryDeadLetterExchangeName, "direct", _durable, _autoDelete, null);
            }
            catch (Exception ex)
            {
                _logger.Warn(string.Format("Error declaring dead letter exchange - {0}", ex.Message));
            }

            try
            {
                _model.QueueBind(queueName, retryDeadLetterExchangeName, retryQueueName); // only redeliver to the original queue (use _queueName as routing key)
            }
            catch (Exception ex)
            {
                _logger.Warn(string.Format("Error binding dead letter queue - {0}", ex.Message));
            }

            Dictionary<string, object> arguments = new(_retryQueueArguments)
            {
                {"x-dead-letter-exchange", retryDeadLetterExchangeName},
                {"x-message-ttl", _retryDelay}
            };

            try
            {
                // We never have consumers on the retry queue.  Therefore set autodelete to false.
                _ = _model.QueueDeclare(retryQueueName, _durable, false, false, arguments);
            }
            catch (Exception ex)
            {
                _logger.Warn(string.Format("Error declaring queue {0}", ex.Message));
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
                _logger.Warn(string.Format("Error declaring error exchange {0}", ex.Message));
            }

            return _transportSettings.ErrorQueueName;
        }

        private string ConfigureErrorQueue()
        {
            try
            {
                _ = _model.QueueDeclare(_transportSettings.ErrorQueueName, true, false, false, _utilityQueueArguments);
            }
            catch (Exception ex)
            {
                _logger.Warn(string.Format("Error declaring error queue {0}", ex.Message));
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
                _logger.Warn(string.Format("Error declaring audit exchange {0}", ex.Message));
            }

            return _transportSettings.AuditQueueName;
        }

        private string ConfigureAuditQueue()
        {
            try
            {
                _ = _model.QueueDeclare(_transportSettings.AuditQueueName, true, false, false, _utilityQueueArguments);
            }
            catch (Exception ex)
            {
                _logger.Warn(string.Format("Error declaring audit queue {0}", ex.Message));
            }
            return _transportSettings.AuditQueueName;
        }

        public bool IsConnected()
        {
            return _connection?.IsConnected() ?? false;
        }
    }
}