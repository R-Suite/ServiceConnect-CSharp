using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
                config.BeforeFilters = new List<Type>
                {
                    typeof(BeforeFilter1),
                    typeof(BeforeFilter2)
                };
                config.AfterFilters = new List<Type>
                {
                    typeof(AfterFilter1),
                    typeof(AfterFilter2)
                };
            });

            bus.StartConsuming();

            Console.ReadLine();
        }
    }
}
