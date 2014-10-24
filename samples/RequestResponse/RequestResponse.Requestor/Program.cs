using System;
using R.MessageBus;
using RequestRepsonse.Messages;

namespace RequestResponse.Requestor
{
    class Program
    {
        private static void Main(string[] args)
        {
            Console.WriteLine("*********** Requestor ***********");

            var bus = Bus.Initialize(config =>
            {
                config.SetQueueName("Requestor");
                config.SetHost("lonappdev04");
            });

            while (true)
            {
                Console.WriteLine("Press enter to send messages");
                Console.ReadLine();

                var id = Guid.NewGuid();
                Console.WriteLine("Sending synchronous message - {0}", id);
                var result = bus.SendRequest<RequestMessage, ResponseMessage>("Responder", new RequestMessage(id));
                Console.WriteLine("Sent synchronous message reply - {0}", result.CorrelationId);
                Console.WriteLine();

                id = Guid.NewGuid();
                Console.WriteLine("Sending async message - {0}", id);
                bus.SendRequest<RequestMessage, ResponseMessage>("Responder", new RequestMessage(id), r => Console.WriteLine("Sent async message reply - {0}", r.CorrelationId));
                Console.WriteLine();
            }
        }
    }
}
