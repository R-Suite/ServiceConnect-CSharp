using System;
using RequestRepsonse.Messages;
using ServiceConnect;

namespace RequestResponse.Requestor
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Requestor ***********");

            var bus = Bus.Initialize(config =>
            {
                config.SetQueueName("NetCoreRequestor");
            });

            while (true)
            {
                Console.WriteLine("Press enter to send messages");
                Console.ReadLine();

                var id = Guid.NewGuid();
                Console.WriteLine("Sending async message - {0}", id);
                bus.SendRequest<RequestMessage, ResponseMessage>("NetCoreResponder", new RequestMessage(id), r => Console.WriteLine("Sent async message reply - {0}", r.CorrelationId));
                Console.WriteLine();
            }
        }
    }
}
