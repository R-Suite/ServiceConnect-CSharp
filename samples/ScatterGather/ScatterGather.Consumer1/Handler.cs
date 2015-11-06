using System;
using System.Threading;
using ServiceConnect.Interfaces;
using ScatterGather.Messages;

namespace ScatterGather.Consumer1
{
    public class Handler : IMessageHandler<Request>
    {
        public void Execute(Request message)
        {
            Console.WriteLine("Consumer 1 Received Message - {0}", message.CorrelationId);

            if (message.Delay)
            {
                Thread.Sleep(1000);
            }
            
            Context.Reply(new Response(message.CorrelationId)
            {
                Endpoint = "Consumer1"
            });
        }

        public IConsumeContext Context { get; set; }
    }
}