using System;
using R.MessageBus.Interfaces;
using RequestRepsonse.Messages;

namespace RequestResponse.Responder
{
    public class RequestMessageHandler : IMessageHandler<RequestMessage>
    {
        private readonly IBus _bus;

        public RequestMessageHandler(IBus bus)
        {
            _bus = bus;
        }

        public void Execute(RequestMessage message)
        {
            var id = Guid.NewGuid();

            //todo: this will need to change to use Bus.Reply()
            _bus.Send("Requestor", new ResponseMessage(id));
        }
    }
}
