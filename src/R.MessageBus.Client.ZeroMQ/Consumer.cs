using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using R.MessageBus.Interfaces;
using ZeroMQ;

namespace R.MessageBus.Client.ZeroMQ
{
    public class Consumer : IConsumer
    {
        private readonly ITransportSettings _transportSettings;
        private ConsumerEventHandler _consumerEventHandler;

        public Consumer(ITransportSettings transportSettings)
        {
            _transportSettings = transportSettings;
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }

        public void StartConsuming(ConsumerEventHandler messageReceived, string queueName, bool? exclusive = null, bool? autoDelete = null)
        {
            _consumerEventHandler = messageReceived;

            if (_transportSettings.ClientSettings.ContainsKey("ReceiverHost"))
            {
                new Thread(() =>
                {
                    Thread.CurrentThread.IsBackground = true;
                    using (var context = new ZContext())
                    using (var receiver = new ZSocket(context, ZSocketType.PULL))
                    {
                        // Bind
                        receiver.Bind(_transportSettings.ClientSettings["ReceiverHost"].ToString());

                        while (true)
                        {
                            // Receive
                            ZError error;
                            ZMessage incoming;
                            if (null == (incoming = receiver.ReceiveMessage(out error)))
                            {
                                if (error == ZError.ETERM)
                                    return; // Interrupted
                                throw new ZException(error);
                            }

                            using (incoming)
                            {
                                var msg = new byte[incoming[1].Length];
                                incoming[1].Read(msg, 0, (int) incoming[1].Length);

                                IDictionary<string, object> headers =
                                    JsonConvert.DeserializeObject<Dictionary<string, object>>(incoming[0].ReadString());
                                var typeName = headers["FullTypeName"].ToString();
                                headers = headers.ToDictionary(k => k.Key, v => (object) Encoding.UTF8.GetBytes(v.Value.ToString()));

                                messageReceived(msg, typeName, headers);
                            }
                        }
                    }
                }).Start();
            }
        }

        public void StopConsuming()
        {
            throw new NotImplementedException();
        }

        public void ConsumeMessageType(string messageTypeName)
        {
            if (_transportSettings.ClientSettings.ContainsKey("SubscriberHost"))
            {
                new Thread(() =>
                {
                    using (var context = new ZContext())
                    using (var subscriber = new ZSocket(context, ZSocketType.SUB))
                    {
                        subscriber.Connect(_transportSettings.ClientSettings["SubscriberHost"].ToString());

                        // Subscribe to messageTypeName
                        subscriber.Subscribe(messageTypeName);

                        while (true)
                        {
                            // Receive
                            ZError error;
                            ZMessage incoming;
                            if (null == (incoming = subscriber.ReceiveMessage(out error)))
                            {
                                if (error == ZError.ETERM)
                                    return; // Interrupted
                                throw new ZException(error);
                            }

                            using (incoming)
                            {
                                var msg = new byte[incoming[2].Length];
                                incoming[2].Read(msg, 0, (int) incoming[2].Length);

                                IDictionary<string, object> headers =
                                    JsonConvert.DeserializeObject<Dictionary<string, object>>(incoming[1].ReadString());
                                var typeName = headers["FullTypeName"].ToString();
                                headers = headers.ToDictionary(k => k.Key, v => (object) Encoding.UTF8.GetBytes(v.Value.ToString()));

                                _consumerEventHandler(msg, typeName, headers);
                            }
                        }
                    }
                }).Start();
            }
        }

        public string Type { get; private set; }
    }
}
