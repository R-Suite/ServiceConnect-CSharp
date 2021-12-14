using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Middleware.Messages;
using ServiceConnect;
using ServiceConnect.Container.StructureMap;
using StructureMap;

namespace Middleware
{
    class Program
    {
        static void Main(string[] args)
        {
            var container = new Container();

            Console.WriteLine("*********** Producer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.SetHost("localhost");
                config.SetContainer(container);
                config.SetQueueName("Middleware.Producer");
                config.SetNumberOfClients(10);
                config.AutoStartConsuming = false;
                config.ScanForMesssageHandlers = false;
            });

            while (true)
            {
                bus.Send("Middleware.Consumer", new MiddlewareMessage(Guid.NewGuid())
                {
                    Value = new Random().Next().ToString()
                });
                Console.ReadLine();
            }
        }
    }
}
