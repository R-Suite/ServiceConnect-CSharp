using System;
using ServiceConnect;
using RequestRepsonse.Messages;
using ServiceConnect.Interfaces;

namespace RequestResponse.Requestor
{
    public class Filter : IFilter
    {
        public bool Process(Envelope envelope)
        {
            if (envelope.Headers.ContainsKey("Authenticated") && bool.Parse(System.Text.Encoding.ASCII.GetString((byte[])envelope.Headers["Authenticated"])))
            {
                Console.WriteLine("authenticated");
                return true;
            }
            Console.WriteLine("not authenticated");
            return false;
        }

        public IBus Bus { get; set; }
    }

    class Program
    {
        private static void Main(string[] args)
        {
            Console.WriteLine("*********** Requestor ***********");

            var bus = Bus.Initialize(config =>
            {
                config.SetQueueName("Requestor");
                config.BeforeConsumingFilters.Add(typeof(Filter));
            });
            
            while (true)
            {
                Console.WriteLine("Press enter to send messages");
                Console.ReadLine();

                //var id = Guid.NewGuid();
                //Console.WriteLine("Sending synchronous message - {0}", id);
                //var result = bus.SendRequest<RequestMessage, ResponseMessage>("Responder", new RequestMessage(id), timeout: 300000);
                //Console.WriteLine("Sent synchronous message reply - {0}", result.CorrelationId);
                //Console.WriteLine();

               var id = Guid.NewGuid();
                Console.WriteLine("Sending async message - {0}", id);
                bus.SendRequest<RequestMessage, ResponseMessage>("Responder", new RequestMessage(id), r => Console.WriteLine("Sent async message reply - {0}", r.CorrelationId));
                Console.WriteLine();
            }
        }
    }
}
