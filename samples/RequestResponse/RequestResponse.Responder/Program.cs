using System;
using R.MessageBus;

namespace RequestResponse.Responder
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Responder ***********");

            Bus.Initialize(x =>
            {
                x.SetQueueName("Responder");
            });
            
            Console.ReadLine();
        }
    }
}
