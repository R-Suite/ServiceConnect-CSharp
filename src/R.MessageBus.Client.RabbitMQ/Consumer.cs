using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using log4net;
using R.MessageBus.Interfaces;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ConsumerEventHandler = R.MessageBus.Interfaces.ConsumerEventHandler;

namespace R.MessageBus.Client.RabbitMQ
{
    public class Consumer : IDisposable, IConsumer
    {
        private static readonly ILog Logger = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private readonly ITransportSettings _transportSettings;
        private readonly IMessageSerializer _messageSerializer;
        private IConnection _connection;
        private IModel _model;
        private ConsumerEventHandler _consumerEventHandler;
        private string _errorExchange;
        private string _auditExchange;
        private readonly int _retryDelay;
        private readonly int _maxRetries;
        private string _queueName;
        private string _retryQueueName;
        private bool _exclusive;

        public Consumer(ITransportSettings transportSettings, IMessageSerializer messageSerializer)
        {
            _transportSettings = transportSettings;
            _messageSerializer = messageSerializer;

            _retryDelay = transportSettings.RetryDelay;
            _maxRetries = transportSettings.MaxRetries;
            _exclusive = transportSettings.Queue.Exclusive;
        }

        /// <summary>
        /// Event fired on HandleBasicDeliver
        /// </summary>
        /// <param name="consumer"></param>
        /// <param name="args"></param>
        public void Event(IBasicConsumer consumer, BasicDeliverEventArgs args)
        {
            var headers = args.BasicProperties.Headers;

            SetHeader(args, "TimeReceived", DateTime.Now.ToString(CultureInfo.InvariantCulture));
            SetHeader(args, "DestinationMachine", Environment.MachineName);

            ConsumeEventResult result = _consumerEventHandler(args.Body, headers);
            _model.BasicAck(args.DeliveryTag, false);

            SetHeader(args, "TimeProcessed", DateTime.Now.ToString(CultureInfo.InvariantCulture));

            if (!result.Success)
            {
                int retryCount = 0;

                if (args.BasicProperties.Headers.ContainsKey("RetryCount"))
                {
                    retryCount = (int)args.BasicProperties.Headers["RetryCount"];
                }

                if (retryCount < _maxRetries)
                {
                    retryCount++;
                    SetHeader(args, "RetryCount", retryCount);

                    _model.BasicPublish(string.Empty, _retryQueueName, args.BasicProperties, args.Body);
                }
                else
                {
                    if (result.Exception != null)
                    {
                        SetHeader(args, "Exception", _messageSerializer.Serialize(new
                        {
                            TimeStamp = DateTime.Now, 
                            ExceptionType = result.Exception.GetType().FullName,
                            result.Exception.Message, 
                            result.Exception.StackTrace,
                            result.Exception.Source
                        }));
                    }

                    Logger.ErrorFormat("Max number of retries exceeded. MessageId: {0}", args.BasicProperties.MessageId);
                    _model.BasicPublish(_errorExchange, string.Empty, args.BasicProperties, args.Body);
                }
            }
            else
            {
                if (_transportSettings.AuditingEnabled)
                {
                    _model.BasicPublish(_auditExchange, string.Empty, args.BasicProperties, args.Body);
                }
            }
        }

        public void StartConsuming(ConsumerEventHandler messageReceived, string messageTypeName, string queueName, bool? exclusive = null)
        {
            _consumerEventHandler = messageReceived;
            // Initialize _queueName here rather than in Consumer.ctor from TransportSettings.Queue.Name
            // TransportSettings.Queue.Name may overriden in the Bus and queueName parameter might have a newer value 
            // than TransportSettings.Queue.Name would in Consumer.ctor
            _queueName = queueName;

            if (exclusive.HasValue)
                _exclusive = exclusive.Value;
            
            var connectionFactory = new ConnectionFactory 
            {
                HostName = _transportSettings.Host,
                VirtualHost = "/",
                Protocol = Protocols.FromEnvironment(),
                Port = AmqpTcpEndpoint.UseDefaultPort
            };

            if (!string.IsNullOrEmpty(_transportSettings.Username))
            {
                connectionFactory.UserName = _transportSettings.Username;
            }

            if (!string.IsNullOrEmpty(_transportSettings.Password))
            {
                connectionFactory.Password = _transportSettings.Password;
            }

            _connection = connectionFactory.CreateConnection();

            _model = _connection.CreateModel();

            // WORK QUEUE
            string exchange = ConfigureExchange(messageTypeName);
            queueName = ConfigureQueue();

            if (!string.IsNullOrEmpty(exchange))
            {
                _model.QueueBind(queueName, exchange, string.Empty);
            }

            // RETRY QUEUE
            ConfigureRetryQueue();

            // ERROR QUEUE
            _errorExchange = ConfigureErrorExchange();
            var errorQueue = ConfigureErrorQueue();

            if (!string.IsNullOrEmpty(_errorExchange))
            {
                _model.QueueBind(errorQueue, _errorExchange, string.Empty, null);
            }

            // AUDIT QUEUE
            if (_transportSettings.AuditingEnabled)
            {
                _auditExchange = ConfigureAuditExchange();
                var auditQueue = ConfigureAuditQueue();

                if (!string.IsNullOrEmpty(_auditExchange))
                {
                    _model.QueueBind(auditQueue, _auditExchange, string.Empty, null);
                }
            }

            var consumer = new EventingBasicConsumer();
            consumer.Received += Event;
            _model.BasicConsume(queueName, false, consumer); // noack must be always false
        }
        
        private static void SetHeader<T>(BasicDeliverEventArgs args, string key, T value)
        {
            if (Equals(value, default(T)))
            {
                args.BasicProperties.Headers.Remove(key);
            }
            else
            {
                args.BasicProperties.Headers[key] = value;
            }
        }

        private string ConfigureQueue()
        {
            var arguments = _transportSettings.Queue.Arguments;

            return _model.QueueDeclare(_queueName, _transportSettings.Queue.Durable, _exclusive, _transportSettings.Queue.AutoDelete, arguments);
        }

        private void ConfigureRetryQueue()
        {
            // When message goes to retry queue, it falls-through to dead-letter exchange (after _retryDelay)
            // dead-letter exchange is of type "direct" and bound to the original queue.
            _retryQueueName = _queueName + ".Retries";
            var autoDelete = _exclusive;
            string retryDeadLetterExchangeName = _queueName + ".Retries.DeadLetter";
            _model.ExchangeDeclare(retryDeadLetterExchangeName, "direct", true, autoDelete, null);
            _model.QueueBind(_queueName, retryDeadLetterExchangeName, _retryQueueName); // only redeliver to the original queue (use _queueName as routing key)

            var arguments = new Dictionary<string, object>
            {
                {"x-dead-letter-exchange", retryDeadLetterExchangeName},
                {"x-message-ttl", _retryDelay}
            };

            _model.QueueDeclare(_retryQueueName, _transportSettings.Queue.Durable, _exclusive, false, arguments);
        }

        private string ConfigureErrorQueue()
        {
            return _model.QueueDeclare(_transportSettings.ErrorQueueName, true, false, false, null);
        }

        private string ConfigureAuditQueue()
        {
            return _model.QueueDeclare(_transportSettings.AuditQueueName, true, false, false, null);
        }

        private string ConfigureExchange(string exchangeName)
        {
            var autoDelete = _exclusive;
            _model.ExchangeDeclare(exchangeName, "fanout", true, autoDelete, null);

            return exchangeName;
        }

        private string ConfigureErrorExchange()
        {
            _model.ExchangeDeclare("errors", "direct");

            return "errors";
        }

        private string ConfigureAuditExchange()
        {
            _model.ExchangeDeclare("audits", "direct");

            return "audits";
        }

        public void StopConsuming()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_connection != null)
                _connection.Close();
            if (_model != null)
                _model.Abort();
        }
    }
}