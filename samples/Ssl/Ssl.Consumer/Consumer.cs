using System;
using R.MessageBus.Interfaces;
using Ssl.Messages;

namespace Ssl.Consumer
{
    public class Consumer : IMessageHandler<SslMessage>
    {
        public void Execute(SslMessage message)
        {
            Console.WriteLine("Consumed message");
        }

        public IConsumeContext Context { get; set; }
    }
}