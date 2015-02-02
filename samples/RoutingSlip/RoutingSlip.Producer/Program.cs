using System;
using System.Collections.Generic;
using R.MessageBus;
using RoutingSlip.Messages;

namespace RoutingSlip.Producer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Producer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.ScanForMesssageHandlers = false;
            });

            Console.WriteLine("Press enter to send message");
            Console.ReadLine();

            var id = Guid.NewGuid();
            bus.Route(new RoutingSlipMessage(id), new List<string> { "RoutingSlip.Endpoint1", "RoutingSlip.Endpoint2" });

            Console.WriteLine("Routed message - {0}", id);
            Console.WriteLine("");
        }
    }
}
