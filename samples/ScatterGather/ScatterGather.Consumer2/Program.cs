using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using R.MessageBus;

namespace ScatterGather.Consumer2
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer 2 ***********");
            Bus.Initialize(x =>
            {
                x.SetQueueName("Consumer2");
            });

            Console.ReadLine();
        }
    }
}
