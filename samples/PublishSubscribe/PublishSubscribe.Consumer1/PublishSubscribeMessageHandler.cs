using System;
using PublishSubscribe.Messages;
using R.MessageBus;
using R.MessageBus.Interfaces;

namespace PublishSubscribe.Consumer1
{
    public class PublishSubscribeMessageHandler : IMessageHandler<PublishSubscribeMessage>
    {
        public void Execute(PublishSubscribeMessage message)
        {
            Console.WriteLine("Consumer 1 Received Message - {0}", message.CorrelationId);
            Console.WriteLine("Now = {0}", DateTime.Now);

            var bus = Bus.Initialize(x =>
            {
                //x.SetHost("lonappdev04");
            });

            int i = 0;
            while (i < 300000)
            {
                i++;
                //Console.WriteLine("Press enter to send message");
                //Console.ReadLine();

                var id = Guid.NewGuid();
                bus.Send("Consumer2", new PublishSubscribeMessage(id));

                //Console.WriteLine("Consumer1 sent message - {0}", id);
                //Console.WriteLine("");
            }

            Console.WriteLine("i = {0}", i);
        }

        public IConsumeContext Context { get; set; }
    }
}