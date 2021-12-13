using System;
using System.Collections.Generic;
using ServiceConnect;

namespace Middleware.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.SetHost("localhost");
                config.SetQueueName("Middleware.Consumer");
                config.SetNumberOfClients(10);
                config.AddMiddleware<Middleware1>();
                config.AddMiddleware<Middleware2>();
            });

            bus.StartConsuming();

            Console.ReadLine();
        }
    }
}
