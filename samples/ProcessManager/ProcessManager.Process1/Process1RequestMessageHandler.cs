using System;
using ProcessManager.Messages;
using ServiceConnect.Interfaces;

namespace ProcessManager.Process1
{
    public class Process1RequestMessageHandler : IMessageHandler<Process1RequestMessage>
    {
        private readonly IBus _bus;

        public Process1RequestMessageHandler(IBus bus)
        {
            _bus = bus;
        }

        public void Execute(Process1RequestMessage message)
        {
            _bus.Send("ProcessManager.Host", new Process1ResponseMessage(message.CorrelationId));
        }

        public IConsumeContext Context { get; set; }
    }
}
