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

            var bus = Bus.Initialize(config => config.SetQueueName("Requestor"));

            while (true)
            {
                Console.WriteLine("Press enter to send message");
                Console.ReadLine();

                var id = Guid.NewGuid();
                var result = bus.SendRequest<RequestMessage, ResponseMessage>("Responder", new RequestMessage(id));

                Console.WriteLine("Sent message - {0}", id);
                Console.WriteLine("");
            }
        }
    }
}
