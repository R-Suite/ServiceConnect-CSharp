using System;
using System.Collections.Generic;
using System.Text;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    public class ConsumeContext : IConsumeContext
    {
        private IBus _bus;

        public IBus Bus
        {
            set { _bus = value; }
        }

        public IDictionary<string, object> Headers { get; set; }

        public void Reply<TReply>(TReply message)  where TReply : Message
        {
            if (Headers.ContainsKey("DestinationAddress"))
            {
                _bus.Send(Encoding.ASCII.GetString((byte[])Headers["DestinationAddress"]), message);
            }
            else
            {
                throw new ArgumentException("DestinationAddress not found in message headers.");
            }
        }
    }
}
