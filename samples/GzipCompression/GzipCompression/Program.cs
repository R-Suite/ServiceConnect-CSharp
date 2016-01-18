using System;
using System.Collections.Generic;
using System.Linq;
using GzipCompression.Messages;
using ServiceConnect;
using ServiceConnect.Filters.GzipCompression;

namespace GzipCompression
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** GzipCompression Producer ***********");
            var bus = Bus.Initialize(x =>
            {
                x.SetHost("localhost");
                x.OutgoingFilters = new List<Type>
                {
                    typeof(OutgoingGzipCompressionFilter)
                };
            });

            var chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var random = new Random();
            var result = new string(
                Enumerable.Repeat(chars, 100000)
                          .Select(s => s[random.Next(s.Length)])
                          .ToArray());

            bus.Send("GzipCompressionConsumer", new CompressionMessage(Guid.NewGuid())
            {
                Data = result
            });

            Console.ReadLine();
        }
    }
}
