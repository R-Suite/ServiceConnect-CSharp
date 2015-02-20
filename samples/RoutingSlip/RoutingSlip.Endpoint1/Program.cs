using System;
using R.MessageBus;

namespace RoutingSlip.Endpoint1
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Endpoint 1 ***********");
            Bus.Initialize();

            Console.ReadLine();
        }
    }
}
