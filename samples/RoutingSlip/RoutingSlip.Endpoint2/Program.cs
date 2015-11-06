using System;
using ServiceConnect;

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
