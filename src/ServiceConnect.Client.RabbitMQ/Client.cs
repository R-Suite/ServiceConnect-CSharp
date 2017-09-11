//Copyright (C) 2015  Timothy Watson, Jakub Pachansky

//This program is free software; you can redistribute it and/or
//modify it under the terms of the GNU General Public License
//as published by the Free Software Foundation; either version 2
//of the License, or (at your option) any later version.

//This program is distributed in the hope that it will be useful,
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//GNU General Public License for more details.

//You should have received a copy of the GNU General Public License
//along with this program; if not, write to the Free Software
//Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Common.Logging;
using Newtonsoft.Json;
using ServiceConnect.Interfaces;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ConsumerEventHandler = ServiceConnect.Interfaces.ConsumerEventHandler;

namespace ServiceConnect.Client.RabbitMQ
{
    public class Client
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(Client));
        private readonly ITransportSettings _transportSettings;
        private readonly Connection _connection;
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
        private readonly bool _errorsDisabled;
        private readonly bool _purgeQueuesOnStartup;
        private readonly IDictionary<string, object> _queueArguments;
        private readonly bool _durable;
        private readonly ushort _prefetchCount;
        private readonly bool _disablePrefetch;
        private readonly ushort _retryCount;
        private readonly ushort _retryTimeInSeconds;

        public Client(Connection connection, ITransportSettings transportSettings)
        {
            _connection = connection;
            _transportSettings = transportSettings;
            
            _retryDelay = transportSettings.RetryDelay;
            _maxRetries = transportSettings.MaxRetries;
            _durable = !transportSettings.ClientSettings.ContainsKey("Durable") || (bool)transportSettings.ClientSettings["Durable"];
            _exclusive = transportSettings.ClientSettings.ContainsKey("Exclusive") && (bool)transportSettings.ClientSettings["Exclusive"];
            _autoDelete = transportSettings.ClientSettings.ContainsKey("AutoDelete") && (bool)transportSettings.ClientSettings["AutoDelete"];
            _queueArguments = transportSettings.ClientSettings.ContainsKey("Arguments") ? (IDictionary<string, object>)transportSettings.ClientSettings["Arguments"] : new Dictionary<string, object>();
            _errorsDisabled = transportSettings.DisableErrors;
            _purgeQueuesOnStartup = transportSettings.PurgeQueueOnStartup;
            _prefetchCount = transportSettings.ClientSettings.ContainsKey("PrefetchCount") ? Convert.ToUInt16((int)transportSettings.ClientSettings["PrefetchCount"]) : Convert.ToUInt16(20);
            _disablePrefetch = transportSettings.ClientSettings.ContainsKey("DisablePrefetch") && (bool)transportSettings.ClientSettings["DisablePrefetch"];
            _retryCount = transportSettings.ClientSettings.ContainsKey("RetryCount") ? Convert.ToUInt16((int)transportSettings.ClientSettings["RetryCount"]) : Convert.ToUInt16(60);
            _retryTimeInSeconds = transportSettings.ClientSettings.ContainsKey("RetrySeconds") ? Convert.ToUInt16((int)transportSettings.ClientSettings["RetrySeconds"]) : Convert.ToUInt16(10);
        }

        /// <summary>
        /// Event fired on HandleBasicDeliver
        /// </summary>
        /// <param name="consumer"></param>
        /// <param name="args"></param>
        public async Task Event(object consumer, BasicDeliverEventArgs args)
        {
            try
            {
                if (!args.BasicProperties.Headers.ContainsKey("TypeName") && !args.BasicProperties.Headers.ContainsKey("FullTypeName"))
                {
                    const string errMsg = "Error processing message, Message headers must contain type name.";
                    Logger.Error(errMsg);
                }

                if (args.Redelivered)
                {
                    SetHeader(args.BasicProperties.Headers, "Redelivered", true);
                }

                await ProcessMessage(args);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                throw;
            }
            finally
            {
                _model.BasicAck(args.DeliveryTag, false);
            }
        }

        private async Task ProcessMessage(BasicDeliverEventArgs args)
        {
            ConsumeEventResult result;
            IDictionary<string, object> headers = args.BasicProperties.Headers;

            try
            {
                SetHeader(args.BasicProperties.Headers, "TimeReceived", DateTime.UtcNow.ToString("O"));
                SetHeader(args.BasicProperties.Headers, "DestinationMachine", Environment.MachineName);
                SetHeader(args.BasicProperties.Headers, "DestinationAddress", _transportSettings.QueueName);

                var typeName = Encoding.UTF8.GetString((byte[])(headers.ContainsKey("FullTypeName") ? headers["FullTypeName"] : headers["TypeName"]));

                result = await _consumerEventHandler(args.Body, typeName, headers);

                SetHeader(args.BasicProperties.Headers, "TimeProcessed", DateTime.UtcNow.ToString("O"));
            }
            catch (Exception ex)
            {
                result = new ConsumeEventResult
                {
                    Exception = ex,
                    Success = false
                };
            }

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
                    SetHeader(args.BasicProperties.Headers, "RetryCount", retryCount);

                    _model.BasicPublish(string.Empty, _retryQueueName, args.BasicProperties, args.Body);
                }
                else
                {
                    if (result.Exception != null)
                    {
                        string jsonException = string.Empty;
                        try
                        {
                            jsonException = JsonConvert.SerializeObject(result.Exception);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn("Error serializing exception", ex);
                        }

                        SetHeader(args.BasicProperties.Headers, "Exception", JsonConvert.SerializeObject(new
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
                string messageType = null;
                if (headers.ContainsKey("MessageType"))
                {
                    messageType = Encoding.UTF8.GetString((byte[])headers["MessageType"]);
                }

                if (_transportSettings.AuditingEnabled && messageType != "ByteStream")
                {
                    _model.BasicPublish(_auditExchange, string.Empty, args.BasicProperties, args.Body);
                }
            }
        }

        public void StartConsuming(ConsumerEventHandler messageReceived, string queueName, bool? exclusive = null, bool? autoDelete = null)
        {
            _consumerEventHandler = messageReceived;
            _queueName = queueName;

            if (exclusive.HasValue)
                _exclusive = exclusive.Value;

            if (autoDelete.HasValue)
                _autoDelete = autoDelete.Value;

            Retry.Do(CreateConsumer, ex =>
            {
                Logger.Error(string.Format("Error creating model - queueName: {0}", queueName), ex);
            }, new TimeSpan(0, 0, 0, _retryTimeInSeconds), _retryCount);
        }

        private void CreateConsumer()
        {
            _model = _connection.CreateModel();

            if (!_disablePrefetch)
            {
                _model.BasicQos(0, _prefetchCount, false);
            }

            // WORK QUEUE
            var queueName = ConfigureQueue();

            // RETRY QUEUE
            ConfigureRetryQueue();

            // ERROR QUEUE
            _errorExchange = ConfigureErrorExchange();
            var errorQueue = ConfigureErrorQueue();

            if (!string.IsNullOrEmpty(_errorExchange))
            {
                _model.QueueBind(errorQueue, _errorExchange, string.Empty, null);
            }

            // Purge all messages on queue
            if (_purgeQueuesOnStartup)
            {
                Logger.Debug("Purging queue");
                _model.QueuePurge(queueName);
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

            var consumer = new AsyncEventingBasicConsumer(_model);
            consumer.Received += Event;
            
            _model.BasicConsume(queueName, false, consumer);

            Logger.Debug("Started consuming");
        }

        public void ConsumeMessageType(string messageTypeName)
        {
            string exchange = ConfigureExchange(messageTypeName, "fanout");

            if (!string.IsNullOrEmpty(exchange))
            {
                _model.QueueBind(_queueName, messageTypeName, string.Empty);
            }
        }

        public string Type
        {
            get
            {
                return "RabbitMQ";
            }
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

        private static void SetHeader<T>(IDictionary<string, object> headers, string key, T value)
        {
            if (Equals(value, default(T)))
            {
                headers.Remove(key);
            }
            else
            {
                headers[key] = value;
            }
        }

        private string ConfigureQueue()
        {
            try
            {
                _model.QueueDeclare(_queueName, _durable, _exclusive, _autoDelete, _queueArguments);
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
            string retryDeadLetterExchangeName = _queueName + ".Retries.DeadLetter";
            
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
                // We never have consumers on the retry queue.  Therefore set autodelete to false.
                _model.QueueDeclare(_retryQueueName, _durable, false, false, arguments);
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

        private string ConfigureExchange(string exchangeName, string type)
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
            if (_autoDelete && _model != null)
            {
                Logger.Debug("Deleting retry queue");
                _model.QueueDelete(_queueName + ".Retries");
            }

            if (_model != null)
            {
                Logger.Debug("Disposing Model");
                _model.Dispose();
                _model = null;
            }
        }
    }
}