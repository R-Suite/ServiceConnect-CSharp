using System;
using System.Threading;
using R.MessageBus.Interfaces;
using ScatterGather.Messages;

namespace ScatterGather.Consumer2
{
    public class Handler : IMessageHandler<Request>
    {
        public void Execute(Request message)
        {
            Console.WriteLine("Consumer 2 Received Message - {0}", message.CorrelationId);

            Context.Reply(new Response(message.CorrelationId)
            {
                Endpoint = "Consumer2"
            });
        }

        public IConsumeContext Context { get; set; }
    }
}