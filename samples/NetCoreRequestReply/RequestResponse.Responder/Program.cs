using System;
using ServiceConnect;

namespace RequestResponse.Responder
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Responder ***********");

            Bus.Initialize(x =>
            {
                x.SetQueueName("NetCoreResponder");
            });

            Console.ReadLine();
        }
    }
}
