using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using R.MessageBus.Interfaces;
using ZeroMQ;

namespace R.MessageBus.Client.ZeroMQ
{
    public class Consumer : IConsumer
    {
        private readonly string[] _hosts;
        private int _activeHost;
        private readonly ITransportSettings _transportSettings;

        public Consumer(ITransportSettings transportSettings)
        {
            _transportSettings = transportSettings;

            _hosts = transportSettings.Host.Split(',');
            _activeHost = 0;
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }

        public void StartConsuming(ConsumerEventHandler messageReceived, string queueName, bool? exclusive = null, bool? autoDelete = null)
        {
            using (var context = new ZContext())
            using (var responder = new ZSocket(context, ZSocketType.PULL))
            {
                // Bind
                responder.Bind(_hosts[_activeHost]);

                while (true)
                {
                    // Receive
                    ZError error;
                    ZMessage incoming;
                    if (null == (incoming = responder.ReceiveMessage(out error)))
                    {
                        if (error == ZError.ETERM)
                            return;    // Interrupted
                        throw new ZException(error);
                    }

                    using (incoming)
                    {
                        var msg = new byte[incoming[1].Length];
                        incoming[1].Read(msg, 0, (int) incoming[1].Length);

                        IDictionary<string, object> headers = JsonConvert.DeserializeObject<Dictionary<string, object>>(incoming[0].ReadString());
                        var typeName = headers["FullTypeName"].ToString();
                        headers = headers.ToDictionary(k => k.Key, v => (object) Encoding.UTF8.GetBytes(v.Value.ToString()));

                        messageReceived(msg, typeName, headers);
                    }
                }
            }
        }

        public void StopConsuming()
        {
            throw new NotImplementedException();
        }

        public void ConsumeMessageType(string messageTypeName)
        {
            throw new NotImplementedException();
        }

        public string Type { get; private set; }
    }
}
