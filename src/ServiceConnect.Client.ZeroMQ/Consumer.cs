using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Common.Logging;
using NetMQ.Sockets;
using Newtonsoft.Json;
using ServiceConnect.Interfaces;
using NetMQ;

namespace ServiceConnect.Client.ZeroMQ
{
    /// <summary>
    /// ***************
    /// Experimental - NOT for production use
    /// ***************
    /// </summary>
    public class Consumer : IConsumer
    {
        private static readonly ILog Logger = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private readonly ITransportSettings _transportSettings;
        private ConsumerEventHandler _consumerEventHandler;
        private readonly int _maxRetries;
        private readonly bool _errorsDisabled;
        //private readonly ZContext _errorPublishContext;
        //private readonly ZSocket _errorPublisher;
        //private readonly ZContext _auditPublishContext;
        //private readonly ZSocket _auditPublisher;
        private readonly Object _lock = new Object();

        public Consumer(ITransportSettings transportSettings)
        {
            _transportSettings = transportSettings;
            _maxRetries = transportSettings.MaxRetries;
            _errorsDisabled = transportSettings.DisableErrors;

            //if (_transportSettings.ClientSettings.ContainsKey("ErrorPublisherHost"))
            //{
            //    _errorPublishContext = new ZContext();
            //    _errorPublisher = new ZSocket(_errorPublishContext, ZSocketType.PUB);
            //    _errorPublisher.Linger = TimeSpan.FromMilliseconds(1);
            //    _errorPublisher.Bind(_transportSettings.ClientSettings["ErrorPublisherHost"].ToString());
            //}

            //if (_transportSettings.ClientSettings.ContainsKey("AuditPublisherHost"))
            //{
            //    _auditPublishContext = new ZContext();
            //    _auditPublisher = new ZSocket(_auditPublishContext, ZSocketType.PUB);
            //    _auditPublisher.Linger = TimeSpan.FromMilliseconds(1);
            //    _auditPublisher.Bind(_transportSettings.ClientSettings["AuditPublisherHost"].ToString());
            //}
        }

        public void Dispose()
        {
            //if (null != _errorPublisher)
            //{
            //    _errorPublisher.Dispose();
            //    _errorPublishContext.Dispose();
            //}

            //if (null != _auditPublisher)
            //{
            //    _auditPublisher.Dispose();
            //    _auditPublishContext.Dispose();
            //}
        }

        public void StartConsuming(ConsumerEventHandler messageReceived, string queueName, bool? exclusive = null, bool? autoDelete = null)
        {
            _consumerEventHandler = messageReceived;

            if (_transportSettings.ClientSettings.ContainsKey("ReceiverHost"))
            {
                    //var random = new Random();
                using (var receiver = new ResponseSocket())
                {
                    // Bind
                    receiver.Bind(_transportSettings.ClientSettings["ReceiverHost"].ToString());

                    var cycles = 0;

                    while (true)
                    {
                        NetMQMessage request = receiver.ReceiveMultipartMessage();
                        cycles++;

                        //if (cycles > 3 && random.Next(0, 10) == 0)
                        //{
                        //    Console.WriteLine("S: Simulating a crash");
                        //    Thread.Sleep(5000);
                        //}
                        //else if (cycles > 3 && random.Next(0, 10) == 0)
                        //{
                        //    Console.WriteLine("S: Simulating CPU overload");
                        //    Thread.Sleep(1000);
                        //}

                        var msgBody = request[1].ToByteArray();

                        IDictionary<string, object> headers =
                            JsonConvert.DeserializeObject<Dictionary<string, object>>(request[0].ConvertToString());

                        var typeName = (headers.ContainsKey("FullTypeName")
                            ? headers["FullTypeName"]
                            : headers["TypeName"]).ToString();

                        headers = headers.ToDictionary(k => k.Key,
                            v => (object) Encoding.UTF8.GetBytes(v.Value.ToString()));

                        ProceesMessageRec(msgBody, typeName, headers);

                        receiver.SendFrame("ok");
                    }
                }
            }
        }

        public void StopConsuming()
        {
            throw new NotImplementedException();
        }

        public void ConsumeMessageType(KeyValuePair<string, IList<string>> messageTypeName)
        {
            //if (_transportSettings.ClientSettings.ContainsKey("SubscriberHost"))
            //{
            //    new Thread(() =>
            //    {
            //        using (var context = new ZContext())
            //        using (var subscriber = new ZSocket(context, ZSocketType.SUB))
            //        {
            //            subscriber.Connect(_transportSettings.ClientSettings["SubscriberHost"].ToString());
            //            subscriber.Subscribe(messageTypeName.Key);

            //            while (true)
            //            {
            //                // Receive
            //                ZError error;
            //                ZMessage incoming;
            //                if (null == (incoming = subscriber.ReceiveMessage(out error)))
            //                {
            //                    if (error == ZError.ETERM)
            //                        return; // Interrupted
            //                    throw new ZException(error);
            //                }

            //                using (incoming)
            //                {
            //                    var msgBody = new byte[incoming[2].Length];
            //                    incoming[2].Read(msgBody, 0, (int)incoming[2].Length);

            //                    IDictionary<string, object> headers =
            //                        JsonConvert.DeserializeObject<Dictionary<string, object>>(incoming[1].ReadString());
            //                    var typeName = headers["FullTypeName"].ToString();
            //                    headers = headers.ToDictionary(k => k.Key, v => (object) Encoding.UTF8.GetBytes(v.Value.ToString()));

            //                    SetHeader(headers, "TimeReceived", DateTime.UtcNow.ToString("O"));
            //                    SetHeader(headers, "DestinationMachine", Environment.MachineName);
            //                    SetHeader(headers, "DestinationAddress", _transportSettings.ClientSettings["SubscriberHost"].ToString());

            //                    ProceesMessageRec(msgBody, typeName, headers);
            //                }
            //            }
            //        }
            //    }).Start();
            //}
        }

        public string Type { get; private set; }

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

        private void ProceesMessageRec(byte[] msgBody, string typeName, IDictionary<string, object> headers, int retryCount = 0)
        {
            ConsumeEventResult result = _consumerEventHandler(msgBody, typeName, headers);

            if (!result.Success)
            {
                if (retryCount < _maxRetries)
                {
                    retryCount++;

                    Thread.Sleep(_transportSettings.RetryDelay);

                    ProceesMessageRec(msgBody, typeName, headers, retryCount);
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

                        SetHeader(headers, "Exception", JsonConvert.SerializeObject(new
                        {
                            TimeStamp = DateTime.Now,
                            ExceptionType = result.Exception.GetType().FullName,
                            Message = GetErrorMessage(result.Exception),
                            result.Exception.StackTrace,
                            result.Exception.Source,
                            Exception = jsonException
                        }));

                        //if (null != _errorPublisher)
                        //{
                        //    var serializedHeaders = JsonConvert.SerializeObject(headers);

                        //    var msg = new ZMessage();
                        //    msg.Append(new ZFrame(_transportSettings.ErrorQueueName));
                        //    msg.Append(new ZFrame(serializedHeaders));
                        //    msg.Append(new ZFrame(msgBody));

                        //    _auditPublisher.SendMessage(msg);
                        //}
                    }

                    Logger.ErrorFormat("Max number of retries exceeded. MessageId: {0}", headers["MessageId"]);
                }
            }
            else if (!_errorsDisabled)
            {
                string messageType = null;
                if (headers.ContainsKey("MessageType"))
                {
                    messageType = Encoding.UTF8.GetString((byte[])headers["MessageType"]);
                }

                //if (_transportSettings.AuditingEnabled && messageType != "ByteStream" && null != _auditPublisher)
                //{
                //    var serializedHeaders = JsonConvert.SerializeObject(headers);

                //    var msg = new ZMessage();
                //    msg.Append(new ZFrame(_transportSettings.AuditQueueName));
                //    msg.Append(new ZFrame(serializedHeaders));
                //    msg.Append(new ZFrame(msgBody));

                //    _auditPublisher.SendMessage(msg);
                //}
            }
        }
    }
}
