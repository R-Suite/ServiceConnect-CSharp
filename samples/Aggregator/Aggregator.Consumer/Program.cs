using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using R.MessageBus;

namespace Aggregator.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer 1 ***********");
            Bus.Initialize(x =>
            {
                x.SetQueueName("Aggregator.Consumer");
                x.SetHost("lonappdev04");
                x.PurgeQueuesOnStart();
            });

            Console.ReadLine();
        }
    }
}
