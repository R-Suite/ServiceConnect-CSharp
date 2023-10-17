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

using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Interfaces;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using ConsumerEventHandler = ServiceConnect.Interfaces.ConsumerEventHandler;

namespace ServiceConnect.Client.RabbitMQ
{
    public class Client
    {
        private IModel _model;
        private readonly IServiceConnectConnection _connection;
        private ConsumerEventHandler _consumerEventHandler;
        private readonly ITransportSettings _transportSettings;
        private readonly ILogger _logger;

        private bool _autoDelete;
        private string _queueName;
        private readonly int _maxRetries;
        private readonly ushort _retryCount;
        private readonly bool _errorsDisabled;
        private readonly ushort _prefetchCount;
        private readonly bool _disablePrefetch;
        private readonly ushort _retryTimeInSeconds;
        private string _retryQueueName;
        private string _errorExchange;
        private string _auditExchange;

        private int _messagesBeingProcessed = 0;
        private AsyncEventingBasicConsumer _consumer;

        public Client(IServiceConnectConnection connection, ITransportSettings transportSettings, ILogger logger)
        {
            _connection = connection;
            _transportSettings = transportSettings;
            _logger = logger;

            _maxRetries = transportSettings.MaxRetries;
            _autoDelete = transportSettings.ClientSettings.ContainsKey("AutoDelete") && (bool)transportSettings.ClientSettings["AutoDelete"];
            _errorsDisabled = transportSettings.DisableErrors;
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
                _messagesBeingProcessed++;

                if (!args.BasicProperties.Headers.ContainsKey("TypeName") &&
                    !args.BasicProperties.Headers.ContainsKey("FullTypeName"))
                {
                    const string errMsg = "Error processing message, Message headers must contain type name.";
                    _logger.Error(errMsg);
                }

                if (args.Redelivered)
                {
                    SetHeader(args.BasicProperties.Headers, "Redelivered", true);
                }

                await ProcessMessage(args);
            }
            catch (Exception ex)
            {
                _logger.Error("Error processing message", ex);
                throw;
            }
            finally
            {
                try
                {
                    _model.BasicAck(args.DeliveryTag, false);
                }
                catch (Exception ex)
                {
                    _logger.Warn("Error acking the message", ex);
                }

                _messagesBeingProcessed--;
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

                string typeName = Encoding.UTF8.GetString((byte[])(headers.ContainsKey("FullTypeName") ? headers["FullTypeName"] : headers["TypeName"]));

                result = await _consumerEventHandler(args.Body.ToArray(), typeName, headers);

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
                            _logger.Warn("Error serializing exception", ex);
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

                    _logger.Error(string.Format("Max number of retries exceeded. MessageId: {0}", args.BasicProperties.MessageId));
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
            _retryQueueName = queueName + ".Retries";
            _errorExchange = _transportSettings.ErrorQueueName;
            _auditExchange = _transportSettings.AuditQueueName;

            if (autoDelete.HasValue)
            {
                _autoDelete = autoDelete.Value;
            }

            Retry.Do(CreateConsumer, ex =>
            {
                _logger.Error(string.Format("Error creating model - queueName: {0}", queueName), ex);
            }, new TimeSpan(0, 0, 0, _retryTimeInSeconds), _retryCount);
        }

        private void CreateConsumer()
        {
            _model = _connection.CreateModel();

            if (!_disablePrefetch)
            {
                _model.BasicQos(0, _prefetchCount, false);
            }

            _consumer = new AsyncEventingBasicConsumer(_model);
            _consumer.Received += Event;

            _ = _model.BasicConsume(_queueName, false, _consumer);

            _logger.Debug("Started consuming");
        }

        public void ConsumeMessageType(string messageTypeName)
        {
            // messageTypeName is the name of the exchange
            _model.QueueBind(_queueName, messageTypeName, string.Empty);
        }

        public string Type => "RabbitMQ";

        private string GetErrorMessage(Exception exception)
        {
            StringBuilder sbMessage = new();
            _ = sbMessage.Append(exception.Message + Environment.NewLine);
            Exception ie = exception.InnerException;
            while (ie != null)
            {
                _ = sbMessage.Append(ie.Message + Environment.NewLine);
                ie = ie.InnerException;
            }

            return sbMessage.ToString();
        }

        private static void SetHeader<T>(IDictionary<string, object> headers, string key, T value)
        {
            if (Equals(value, default(T)))
            {
                _ = headers.Remove(key);
            }
            else
            {
                headers[key] = value;
            }
        }

        public void StopConsuming()
        {
            Dispose();
        }

        public void Dispose()
        {
            // Stop consuming
            if (_consumer != null)
            {
                foreach (string tag in _consumer.ConsumerTags)
                {
                    try
                    {
                        _model.BasicCancel(tag);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Error cancelling consumer", ex);
                    }
                }
            }

            if (_autoDelete && _model != null)
            {
                _logger.Debug("Deleting retry queue");
                _ = _model.QueueDelete(_queueName + ".Retries");
            }

            // Dispose model
            if (_model != null)
            {
                try
                {

                    _logger.Debug("Disposing Model");
                    _model.Dispose();
                    _model = null;
                }
                catch (Exception ex)
                {
                    _logger.Error("Error disposing consumer", ex);
                }
            }

            // Wait until all messages have been processed.
            int timeout = 0;
            while (_messagesBeingProcessed > 0 && timeout < 6000)
            {
                System.Threading.Thread.Sleep(100);
                timeout++;
            }
        }
    }
}