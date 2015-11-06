using System;
using ServiceConnect;

namespace McDonalds.FoodPreparer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Food Preparer ***********");
            var bus = Bus.Initialize(x =>
            {
                x.SetAuditingEnabled(true);
            });

            bus.StartConsuming();

            Console.ReadLine();
        }
    }
}
