using System;
using System.Threading.Tasks;
using AsyncMessagehandlers.Messages;
using ServiceConnect.Interfaces;

namespace AsyncMessageHandlers.Consumers
{
    public class AsyncMessageHandler2 : IAsyncMessageHandler<AsyncMessage>
    {
        public async Task Execute(AsyncMessage message)
        {
            await Task.Run(() =>
            {
                Console.WriteLine("Executing AsyncMessageHandler2");
            });
        }

        public IConsumeContext Context { get; set; }
    }
}