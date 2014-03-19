using System;
using System.Collections.Generic;
using System.Configuration;
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
        private readonly Settings.Settings _settings;
        private IConnection _connection;
        private IModel _model;
        private ConsumerEventHandler _consumerEventHandler;
        private string _retryExchange;
        private string _errorExchange;
        private readonly int _retryDelay;
        private readonly int _maxRetries; 


        public Consumer(string configPath, string endPoint)
        {
            var configurationManager = new ConfigurationManagerWrapper(configPath);

            var section = configurationManager.GetSection<BusSettings.BusSettings>("BusSettings");

            if (section == null)
            {
                throw new ConfigurationErrorsException("The configuration file must contain a BusSettings section");
            }

            Settings.Settings settings = section.Settings.GetItemByKey(endPoint);

            if (settings == null)
            {
                throw new ConfigurationErrorsException(string.Format("Settings for endpoint {0} could not be found", endPoint));
            }

            _retryDelay = settings.Retries == null ? 3 : settings.Retries.RetryDelay;
            _maxRetries = settings.Retries == null ? 3000 : settings.Retries.MaxRetries;

            _settings = settings;
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
                HostName = _settings.Host,
                VirtualHost = "/",
                Protocol = Protocols.FromEnvironment(),
                Port = AmqpTcpEndpoint.UseDefaultPort
            };

            if (!string.IsNullOrEmpty(_settings.Username))
            {
                connectionFactory.UserName = _settings.Username;
            }

            if (!string.IsNullOrEmpty(_settings.Password))
            {
                connectionFactory.Password = _settings.Password;
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
            _model.BasicConsume(queueName, _settings.NoAck, consumer);
        }

        private string ConfigureQueue(string queueName)
        {
            var arguments = new Dictionary<string, object>();
            if (_settings.Queue.Arguments != null)
            {
                for (int i = 0; i < _settings.Queue.Arguments.Count; i++)
                {
                    arguments.Add(_settings.Queue.Arguments[i].Name, _settings.Queue.Arguments[i].Value);
                }
            }

            if (string.IsNullOrEmpty(queueName) && string.IsNullOrEmpty(_settings.Queue.Name))
            {
                return _model.QueueDeclare().QueueName;
            }

            return  _model.QueueDeclare(!string.IsNullOrEmpty(queueName) ? queueName : _settings.Queue.Name,
                                        _settings.Queue.Durable,
                                        _settings.Queue.Exclusive,
                                        _settings.Queue.AutoDelete,
                                        arguments);
            
        }

        private string ConfigureRetryQueue(string queueName, string exchangeName)
        {
            var arguments = new Dictionary<string, object>
            {
                {"x-dead-letter-exchange", exchangeName},
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
            if (!string.IsNullOrEmpty(_settings.Exchange.Name))
            {
                var arguments = new Dictionary<string, object>();
                if (_settings.Exchange.Arguments != null)
                {
                    for (int i = 0; i < _settings.Exchange.Arguments.Count; i++)
                    {
                        arguments.Add(_settings.Exchange.Arguments[i].Name, _settings.Exchange.Arguments[i].Value);
                    }
                }

                _model.ExchangeDeclare(_settings.Exchange.Name,
                                       _settings.Exchange.Type,
                                       _settings.Exchange.Durable,
                                       _settings.Exchange.AutoDelete,
                                       arguments);
            }
            return _settings.Exchange.Name;
        }

        private string ConfigureRetryExchange()
        {
            string retryExchangeName = _settings.Exchange.Name + ".Retries";

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