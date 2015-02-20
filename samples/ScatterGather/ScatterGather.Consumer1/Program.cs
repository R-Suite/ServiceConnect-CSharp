using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using R.MessageBus;

namespace ScatterGather.Consumer1
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer 1 ***********");
            Bus.Initialize(x =>
            {
                x.SetQueueName("Consumer1");
            });

            Console.ReadLine();
        }
    }
}
