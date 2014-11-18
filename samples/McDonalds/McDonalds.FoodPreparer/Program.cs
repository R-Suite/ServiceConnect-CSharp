using System;
using R.MessageBus;

namespace McDonalds.FoodPreparer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Food Preparer ***********");
            var bus = Bus.Initialize(x =>
            {
                x.SetHost("lonappdev04");
                x.ScanForMesssageHandlers = true;
                x.SetAuditingEnabled(true);
            });

            bus.StartConsuming();

            Console.ReadLine();
        }
    }
}
