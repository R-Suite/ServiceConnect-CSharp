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
            if (DateTime.Now.Millisecond%2 == 0)
            {
                Console.WriteLine("Throwing exception - {0}", message.CorrelationId);
                throw new Exception();
            }

            Console.WriteLine("Received message, sending reply - {0}", message.CorrelationId);
            Context.Reply(new ResponseMessage(message.CorrelationId));
        }
    }
}
