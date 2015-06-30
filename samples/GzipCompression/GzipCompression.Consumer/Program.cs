using System;
using System.Collections.Generic;
using R.MessageBus;
using R.MessageBus.Filters.GzipCompression;

namespace GzipCompression.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** GzipCompression Consumer ***********");
            Bus.Initialize(x =>
            {
                x.SetHost("lonappdev04");
                x.SetQueueName("GzipCompressionConsumer");
                x.BeforeConsumingFilters = new List<Type>
                {
                    typeof(IncomingGzipCompressionFilter)
                };
            });
            Console.ReadLine();
        }
    }
}
