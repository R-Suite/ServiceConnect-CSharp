using System;
using R.MessageBus;

namespace RoutingSlip.Endpoint2
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Endpoint 2 ***********");
            Bus.Initialize();
            
            Console.ReadLine();
        }
    }
}
