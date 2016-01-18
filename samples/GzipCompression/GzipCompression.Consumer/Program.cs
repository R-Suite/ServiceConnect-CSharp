using System;
using System.Collections.Generic;
using ServiceConnect;
using ServiceConnect.Filters.GzipCompression;

namespace GzipCompression.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** GzipCompression Consumer ***********");
            Bus.Initialize(x =>
            {
                x.SetHost("localhost");
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
