using System;
using ServiceConnect.Interfaces;
using RecipientList.Messages;

namespace RecipientList.Consumer2
{
    public class PublishSubscribeMessageHandler : IMessageHandler<RecipientListMessage>
    {
        public void Execute(RecipientListMessage message)
        {
            Console.WriteLine("Consumer 2 Received Message - {0}", message.CorrelationId);

            if (message.SendReply)
            {
                Context.Reply(new RecipientListResponse(message.CorrelationId)
                {
                    Endpoint = "Consumer2"
                }); 
            }
        }

        public IConsumeContext Context { get; set; }
    }
}