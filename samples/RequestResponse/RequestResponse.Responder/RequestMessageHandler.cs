using System;
using R.MessageBus.Interfaces;
using RequestRepsonse.Messages;

namespace RequestResponse.Responder
{
    public class RequestMessageHandler : IMessageHandler<RequestMessage>
    {
        public IConsumeContext Context { get; set; }

        public void Execute(RequestMessage message)
        {
            Console.WriteLine("Received message, sending reply - {0}", message.CorrelationId);
            Context.Reply(new ResponseMessage(message.CorrelationId));
        }
    }
}
