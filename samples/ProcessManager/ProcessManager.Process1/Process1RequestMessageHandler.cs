using System;
using ProcessManager.Messages;
using R.MessageBus.Interfaces;

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
            Console.WriteLine("In ProcessManager.Process1.Process1RequestMessageHandler.Execute() - {0}", message.CorrelationId);

            _bus.Send("ProcessManager.Host", new Process1ResponseMessage(Guid.NewGuid()) { Name = "Name1", Age = 1});
        }

        public IConsumeContext Context { get; set; }
    }
}
