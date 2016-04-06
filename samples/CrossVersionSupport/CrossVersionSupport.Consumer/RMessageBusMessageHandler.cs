using System;
using CrossVersionSupport.Messages;
using R.MessageBus.Interfaces;

namespace CrossVersionSupport.Consumer
{
    public class RMessageBusMessageHandler : IMessageHandler<RMessageBusMessage>
    {
        public void Execute(RMessageBusMessage message)
        {
            Console.WriteLine("RMessageBusMessageHandler received message - {0}", message.Test);
        }

        public IConsumeContext Context { get; set; }
    }
}
