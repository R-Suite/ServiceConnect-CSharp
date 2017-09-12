using System;
using System.Threading.Tasks;
using AsyncProcessManager.Messages;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;

namespace AsyncProcessManager.ProcessManager
{
    public class ProcessManagerHandler : ProcessManager<ProcessManagerData>, IStartAsyncProcessManager<StartMessage>, IAsyncMessageHandler<MessageResponse>
    {
        private readonly IBus _bus;

        public ProcessManagerHandler(IBus bus)
        {
            _bus = bus;
        }

        public async Task Execute(StartMessage message)
        {
            await Task.Run(() =>
            {
                Data.CorrelationId = message.CorrelationId;
                Console.WriteLine("Received start.");
                _bus.Send("AsyncProcessManager.Consumer", new MessageRequest(message.CorrelationId));
            });
        }

        public async Task Execute(MessageResponse message)
        {
            await Task.Run(() =>
            {
                Console.WriteLine("Received message response.");
                _bus.Send("AsyncProcessManager.Producer", new CompleteMessage(message.CorrelationId));
                MarkAsComplete();
            });
        }
    }
}