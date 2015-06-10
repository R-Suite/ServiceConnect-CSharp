using System;
using System.Collections.Generic;
using R.MessageBus;
using RequestRepsonse.Messages;

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
