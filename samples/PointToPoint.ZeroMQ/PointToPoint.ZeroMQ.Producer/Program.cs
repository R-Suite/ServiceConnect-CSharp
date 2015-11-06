using System;
using System.Collections.Generic;
using PointToPoint.ZeroMQ.Messages;
using ServiceConnect;
using ServiceConnect.Client.ZeroMQ;

namespace PointToPoint.ZeroMQ.Producer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Producer ***********");
            var bus = Bus.Initialize(config =>
            {
                //config.AddQueueMapping(typeof(PointToPointMessage), "PointToPoint2");
                config.SetConsumer<ServiceConnect.Client.ZeroMQ.Consumer>();
                config.SetProducer<ServiceConnect.Client.ZeroMQ.Producer>();
                config.ScanForMesssageHandlers = false;
                config.AutoStartConsuming = false;
                config.SetPublisherHost("tcp://127.0.0.1:5556");
            });

            while (true)
            {
                Console.WriteLine("Press enter to send message");
                Console.ReadLine();

                Console.WriteLine("Start: {0}", DateTime.Now);
                var id = Guid.NewGuid();
                for (int i = 0; i < 10000; i++)
                {
                    //bus.Send("PointToPoint.ZeroMQ.Consumer", new PointToPointMessage(id) { Count = i });
                    bus.Send("tcp://127.0.0.1:5555", new PointToPointMessage(id) { Count = i });
                    
                    //bus.Publish(new PointToPointMessage(id) { Count = i});
                    //Console.WriteLine("Sent message - {0}", i);
                }

                Console.WriteLine("End: {0}", DateTime.Now);

                Console.WriteLine("");
            }
        }
    }
}
