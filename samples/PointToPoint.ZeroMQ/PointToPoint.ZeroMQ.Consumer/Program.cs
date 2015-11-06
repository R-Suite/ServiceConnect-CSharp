using System;
using ServiceConnect;
using ServiceConnect.Client.ZeroMQ;
using ServiceConnect.Container.StructureMap;

namespace PointToPoint.ZeroMQ.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.SetConsumer<ServiceConnect.Client.ZeroMQ.Consumer>();
                config.SetProducer<ServiceConnect.Client.ZeroMQ.Producer>();
                config.SetReceiverHost("tcp://127.0.0.1:5555");
                config.SetSubscriberHost("tcp://127.0.0.1:5556");
                config.SetContainerType<StructureMapContainer>();
            });

            Console.ReadLine();
        }
    }
}