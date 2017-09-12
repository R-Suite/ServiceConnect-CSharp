using System;
using System.Threading.Tasks;
using AsyncProcessManager.Messages;
using ServiceConnect.Interfaces;

namespace AsyncProcessManager.Consumer
{
    public class MessageHandler : IAsyncMessageHandler<MessageRequest>
    {
        private readonly IBus _bus;

        public MessageHandler(IBus bus)
        {
            _bus = bus;
        }

        public async Task Execute(MessageRequest message)
        {
            await Task.Run(() =>
            {
                Console.WriteLine("Received message.");
                _bus.Send("AsyncProcessManager.ProcessManager", new MessageResponse(message.CorrelationId));
            });
        }

        public IConsumeContext Context { get; set; }
    }
}