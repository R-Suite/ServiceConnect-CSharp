using System;
using System.Threading.Tasks;
using AsyncProcessManager.Messages;
using ServiceConnect.Interfaces;

namespace AsyncProcessManager.Producer
{
    public class CompleteMessageHandler : IAsyncMessageHandler<CompleteMessage>
    {
        public async Task Execute(CompleteMessage message)
        {
            await Task.Run(() =>
            {
                Console.WriteLine("Received process manager complete message");
            });
        }

        public IConsumeContext Context { get; set; }
    }
}