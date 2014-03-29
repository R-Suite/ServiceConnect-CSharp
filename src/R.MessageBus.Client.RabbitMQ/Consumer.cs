using System;
using System.Collections.Generic;
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
        private IConnection _connection;
        private IModel _model;
        private ConsumerEventHandler _consumerEventHandler;
        private string _retryExchange;
        private string _errorExchange;
        private readonly int _retryDelay;
        private readonly int _maxRetries;

        public Consumer(ITransportSettings transportSettings)
        {
            _transportSettings = transportSettings;

            _retryDelay = transportSettings.RetryDelay;
            _maxRetries = transportSettings.MaxRetries;
        }

        public void Event(IBasicConsumer consumer, BasicDeliverEventArgs args)
        {
            var success = _consumerEventHandler(args.Body);
            _model.BasicAck(args.DeliveryTag, false);

            if (!success)
            {
                int retryCount = 0;

                if (null == args.BasicProperties.Headers)
                    args.BasicProperties.Headers = new Dictionary<string, object>();

                if (args.BasicProperties.Headers.ContainsKey("RetryCount"))
                {
                    retryCount = (int)args.BasicProperties.Headers["RetryCount"];
                    args.BasicProperties.Headers.Remove("RetryCount");
                }

                if (retryCount < _maxRetries)
                {
                    retryCount++;
                    args.BasicProperties.Headers.Add("RetryCount", retryCount);

                    _model.BasicPublish(_retryExchange, args.RoutingKey, args.BasicProperties, args.Body);
                }
                else
                {
                    Logger.ErrorFormat("Max number of retries exceeded. MessageId : {0}", args.BasicProperties.MessageId);

                    _model.BasicPublish(_errorExchange, string.Empty, args.BasicProperties, args.Body);
                }
            }
        }

        public void StartConsuming(ConsumerEventHandler messageReceived, string routingKey, string queueName = null)
        {
            _consumerEventHandler = messageReceived;
            
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
            string exchange = ConfigureExchange();
            queueName = ConfigureQueue(queueName);

            if (!string.IsNullOrEmpty(exchange))
            {
                _model.QueueBind(queueName, exchange, routingKey);
            }

            //RETRY QUEUE
            _retryExchange = ConfigureRetryExchange();
            var retryQueueName = ConfigureRetryQueue(queueName, exchange);

            if (!string.IsNullOrEmpty(_retryExchange))
            {
                _model.QueueBind(retryQueueName, _retryExchange, routingKey, null);
            }

            //ERROR QUEUE
            _errorExchange = ConfigureErrorExchange();
            var errorQueue = ConfigureErrorQueue();

            if (!string.IsNullOrEmpty(_errorExchange))
            {
                _model.QueueBind(errorQueue, _errorExchange, string.Empty, null);
            }

            var consumer = new EventingBasicConsumer();
            consumer.Received += Event;
            _model.BasicConsume(queueName, _transportSettings.NoAck, consumer);
        }

        private string ConfigureQueue(string queueName)
        {
            var arguments = _transportSettings.Queue.Arguments;

            if (string.IsNullOrEmpty(queueName) && string.IsNullOrEmpty(_transportSettings.Queue.Name))
            {
                return _model.QueueDeclare().QueueName;
            }

            return _model.QueueDeclare(!string.IsNullOrEmpty(queueName) ? queueName : _transportSettings.Queue.Name,
                                        _transportSettings.Queue.Durable,
                                        _transportSettings.Queue.Exclusive,
                                        _transportSettings.Queue.AutoDelete,
                                        arguments);
            
        }

        private string ConfigureRetryQueue(string queueName, string exchangeName)
        {
            // When message goes to retry queue, it falls-through to dead-letter exchange (after _retryDelay)
            // dead-letter exchange is of type "direct" and bound to the original queue.
            string retryDeadLetterExchangeName = exchangeName + ".Retries.DeadLetter";
            _model.ExchangeDeclare(retryDeadLetterExchangeName, "direct");
            _model.QueueBind(queueName, retryDeadLetterExchangeName, string.Empty, null);

            var arguments = new Dictionary<string, object>
            {
                {"x-dead-letter-exchange", retryDeadLetterExchangeName},
                {"x-message-ttl", _retryDelay}
            };

            string retryQueueName = queueName + ".Retries";

            return _model.QueueDeclare(retryQueueName,true,false,false, arguments);
        }

        private string ConfigureErrorQueue()
        {
            return _model.QueueDeclare("errors", true, false, false, null);
        }

        private string ConfigureExchange()
        {
            var arguments = _transportSettings.Exchange.Arguments;

            _model.ExchangeDeclare(_transportSettings.Exchange.Name,
                                    "fanout",
                                    _transportSettings.Exchange.Durable,
                                    _transportSettings.Exchange.AutoDelete,
                                    arguments);

            return _transportSettings.Exchange.Name;
        }

        private string ConfigureRetryExchange()
        {
            string retryExchangeName = _transportSettings.Exchange.Name + ".Retries";

            _model.ExchangeDeclare(retryExchangeName, "direct");

            return retryExchangeName;
        }

        private string ConfigureErrorExchange()
        {
            _model.ExchangeDeclare("errors", "direct");

            return "errors";
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