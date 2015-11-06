using System;
using ServiceConnect;

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
