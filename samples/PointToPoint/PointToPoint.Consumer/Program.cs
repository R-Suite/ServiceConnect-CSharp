using System;
using R.MessageBus;
using R.MessageBus.Container.Ninject;
using R.MessageBus.Container.StructureMap;

namespace PointToPoint.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.SetQueueName("PointToPoint2");
                config.SetThreads(2);
                config.SetContainerType<NinjectContainer>();
            });
           
            Console.ReadLine();
        }
    }
}
