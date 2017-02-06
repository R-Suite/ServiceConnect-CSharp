using System;
using ProcessManager.Messages;
using ServiceConnect.Interfaces;

namespace ProcessManager.Process2
{
    public class Process2RequestMessageHandler : IMessageHandler<Process2RequestMessage>
    {
        private readonly IBus _bus;

        public Process2RequestMessageHandler(IBus bus)
        {
            _bus = bus;
        }

        public void Execute(Process2RequestMessage message)
        {
            _bus.Send("ProcessManager.Host", new Process2ResponseMessage(message.CorrelationId));
        }

        public IConsumeContext Context { get; set; }
    }
}
