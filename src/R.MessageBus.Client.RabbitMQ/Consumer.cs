using System;
using System.Collections.Generic;
using System.Text;
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
        private bool _autoDelete;
        private string _messageTypeName;
        private bool _connectionClosed;
        private readonly string[] _hosts;
        private int _activeHost;
        private bool _errorsDisabled;

        public Consumer(ITransportSettings transportSettings, IMessageSerializer messageSerializer)
        {
            _transportSettings = transportSettings;
            _messageSerializer = messageSerializer;

            _hosts = transportSettings.Host.Split(',');
            _activeHost = 0;

            _retryDelay = transportSettings.RetryDelay;
            _maxRetries = transportSettings.MaxRetries;
            _exclusive = transportSettings.Queue.Exclusive;
            _autoDelete = transportSettings.Queue.AutoDelete;
            _errorsDisabled = transportSettings.DisableErrors;
        }

        /// <summary>
        /// Event fired on HandleBasicDeliver
        /// </summary>
        /// <param name="consumer"></param>
        /// <param name="args"></param>
        public void Event(IBasicConsumer consumer, BasicDeliverEventArgs args)
        {
            var headers = args.BasicProperties.Headers;

            SetHeader(args, "TimeReceived", DateTime.UtcNow.ToString("O"));
            SetHeader(args, "DestinationMachine", Environment.MachineName);

            string message = Encoding.UTF8.GetString(args.Body);

            if (!headers.ContainsKey("FullTypeName"))
            {
                throw new Exception(string.Format("Error processing message, Message headers must contain FullTypeName."));
            }

            var typeName = Encoding.UTF8.GetString((byte[])headers["FullTypeName"]);

            ConsumeEventResult result = _consumerEventHandler(message, typeName, headers);
            _model.BasicAck(args.DeliveryTag, false);

            SetHeader(args, "TimeProcessed", DateTime.UtcNow.ToString("O"));

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
                        string jsonException = string.Empty;
                        try
                        {
                            jsonException = _messageSerializer.Serialize(result.Exception);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn("Error serializing exception", ex);
                        }

                        SetHeader(args, "Exception", _messageSerializer.Serialize(new
                        {
                            TimeStamp = DateTime.Now,
                            ExceptionType = result.Exception.GetType().FullName,
                            Message = GetErrorMessage(result.Exception),
                            result.Exception.StackTrace,
                            result.Exception.Source,
                            Exception = jsonException
                        }));
                    }

                    Logger.ErrorFormat("Max number of retries exceeded. MessageId: {0}", args.BasicProperties.MessageId);
                    _model.BasicPublish(_errorExchange, string.Empty, args.BasicProperties, args.Body);
                }
            }
            else if (!_errorsDisabled)
            {
                if (_transportSettings.AuditingEnabled)
                {
                    _model.BasicPublish(_auditExchange, string.Empty, args.BasicProperties, args.Body);
                }
            }
        }

        public void StartConsuming(ConsumerEventHandler messageReceived, string messageTypeName, string queueName, bool? exclusive = null, bool? autoDelete = null)
        {
            _consumerEventHandler = messageReceived;
            _queueName = queueName;
            _messageTypeName = messageTypeName;

            if (exclusive.HasValue)
                _exclusive = exclusive.Value;

            if (autoDelete.HasValue)
                _autoDelete = autoDelete.Value;

            CreateConsumer();
        }

        private void CreateConsumer()
        {
            Logger.Info(string.Format("Connecting to queue - {0}", _queueName));

            var connectionFactory = new ConnectionFactory
            {
                HostName = _hosts[_activeHost],
                Protocol = Protocols.FromEnvironment(),
                Port = AmqpTcpEndpoint.UseDefaultPort,
                RequestedHeartbeat = 30
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
            string exchange = ConfigureExchange(_messageTypeName);
            var queueName = ConfigureQueue();

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
            consumer.Shutdown += ConsumerShutdown;
            _model.BasicConsume(queueName, false, consumer);
        }

        private void ConsumerShutdown(object sender, ShutdownEventArgs e)
        {
            if (_connectionClosed)
            {
                return;
            }

            if (_hosts.Length > 1)
            {
                if (_activeHost < _hosts.Length - 1)
                {
                    _activeHost++;
                }
                else
                {
                    _activeHost = 0;
                }
            }

            Retry.Do(CreateConsumer, ex => Logger.Error("Error connecting to queue - {0}", ex), new TimeSpan(0, 0, 0, 10));
        }

        private string GetErrorMessage(Exception exception)
        {
            var sbMessage = new StringBuilder();
            sbMessage.Append(exception.Message + Environment.NewLine);
            var ie = exception.InnerException;
            while (ie != null)
            {
                sbMessage.Append(ie.Message + Environment.NewLine);
                ie = ie.InnerException;
            }

            return sbMessage.ToString();
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
            try
            {
                _model.QueueDeclare(_queueName, _transportSettings.Queue.Durable, _exclusive, _autoDelete, arguments);
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring queue - {0}", ex.Message));
            }
            return _queueName;
        }

        private void ConfigureRetryQueue()
        {
            // When message goes to retry queue, it falls-through to dead-letter exchange (after _retryDelay)
            // dead-letter exchange is of type "direct" and bound to the original queue.
            _retryQueueName = _queueName + ".Retries";
            var autoDelete = _exclusive;
            string retryDeadLetterExchangeName = _queueName + ".Retries.DeadLetter";

            try
            {
                _model.ExchangeDeclare(retryDeadLetterExchangeName, "direct", true, autoDelete, null);
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring dead letter exchange - {0}", ex.Message));
            }

            try
            {
                _model.QueueBind(_queueName, retryDeadLetterExchangeName, _retryQueueName); // only redeliver to the original queue (use _queueName as routing key)
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
                _model.QueueDeclare(_retryQueueName, _transportSettings.Queue.Durable, _exclusive, false, arguments);
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring queue {0}", ex.Message));
            }
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

        private string ConfigureExchange(string exchangeName)
        {
            try
            {
                _model.ExchangeDeclare(exchangeName, "fanout", true, _exclusive, null);
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring exchange {0}", ex.Message));
            }

            return exchangeName;
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

        public void StopConsuming()
        {
            Dispose();
        }

        public void Dispose()
        {
           if (_connection != null)
           {
               _connectionClosed = true;
                _connection.Close(500);
            }
            if (_model != null)
                _model.Abort();
        }
    }
}