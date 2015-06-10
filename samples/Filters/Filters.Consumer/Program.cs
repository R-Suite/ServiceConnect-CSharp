using System;
using System.Collections.Generic;
using R.MessageBus;

namespace Filters.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.SetQueueName("Filters.Consumer");
                config.SetThreads(10);
                config.BeforeConsumingFilters = new List<Type>
                {
                    typeof(BeforeFilter1),
                    typeof(BeforeFilter2)
                };
                config.AfterConsumingFilters = new List<Type>
                {
                    typeof(AfterFilter1),
                    typeof(AfterFilter2)
                };
                config.SetHost("lonappdev04");
            });

            bus.StartConsuming();

            Console.ReadLine();
        }
    }
}
