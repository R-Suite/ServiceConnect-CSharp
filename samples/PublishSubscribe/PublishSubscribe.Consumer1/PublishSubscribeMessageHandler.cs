using System;
using PublishSubscribe.Messages;
using ServiceConnect.Interfaces;

namespace PublishSubscribe.Consumer1
{
    public class PublishSubscribeMessageHandler : IMessageHandler<PublishSubscribeMessage>
    {
        public void Execute(PublishSubscribeMessage message)
        {
            Console.WriteLine("Consumer 1 Received Message - {0}", message.CorrelationId);
            Console.WriteLine("Now = {0}", DateTime.Now);
        }

        public IConsumeContext Context { get; set; }
    }
}