using System;
using ProcessManager.Messages;
using R.MessageBus.Interfaces;

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
            Console.WriteLine("In ProcessManager.Process2.Process1RequestMessageHandler.Execute() - {0} ({1})", message.ProcessId, message.CorrelationId);

            _bus.Send("ProcessManager.Host",
                new Process2ResponseMessage(message.CorrelationId)
                {
                    ProcessId = message.ProcessId,
                    Name = "Process_" + message.ProcessId,
                    Widget = new Widget2 {Size = message.ProcessId}
                });
        }

        public IConsumeContext Context { get; set; }
    }
}
