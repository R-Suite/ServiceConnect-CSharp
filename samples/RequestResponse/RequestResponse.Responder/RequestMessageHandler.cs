using R.MessageBus.Interfaces;
using RequestRepsonse.Messages;

namespace RequestResponse.Responder
{
    public class RequestMessageHandler : IMessageHandler<RequestMessage>
    {
        public IConsumeContext Context { get; set; }

        public void Execute(RequestMessage message)
        {
            Context.Reply(new ResponseMessage(message.CorrelationId));
        }
    }
}
