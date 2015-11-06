using System;
using System.Threading;
using ServiceConnect.Interfaces;
using RecipientList.Messages;

namespace RecipientList.Consumer1
{
    public class PublishSubscribeMessageHandler : IMessageHandler<RecipientListMessage>
    {
        public void Execute(RecipientListMessage message)
        {
            Console.WriteLine("Consumer 1 Received Message - {0}", message.CorrelationId);

            if (message.Delay)
            {
                Thread.Sleep(1000);
            }

            if (message.SendReply)
            {
                Context.Reply(new RecipientListResponse(message.CorrelationId)
                {
                    Endpoint = "Consumer1"
                });
            }
        }

        public IConsumeContext Context { get; set; }
    }
}