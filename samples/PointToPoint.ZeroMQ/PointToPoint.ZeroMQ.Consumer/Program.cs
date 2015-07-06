using System;
using R.MessageBus;
using R.MessageBus.Client.ZeroMQ;
using R.MessageBus.Core;

namespace PointToPoint.ZeroMQ.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.SetConsumer<R.MessageBus.Client.ZeroMQ.Consumer>();
                config.SetProducer<R.MessageBus.Client.ZeroMQ.Producer>();
                config.SetReceiverHost("tcp://127.0.0.1:5555");
                config.SetSubscriberHost("tcp://127.0.0.1:5556");
            });

            Console.ReadLine();
        }
    }
}