using System;
using PointToPoint.Messages;
using ServiceConnect.Interfaces;

namespace PointToPoint.Consumer
{
    public class MessageDeduplicationHandler : IMessageHandler<PointToPointMessage>
    {
        public void Execute(PointToPointMessage command)
        {
            if (command.SerialNumber > 999990)
                Console.WriteLine("{0}: Consumer 1 - {1}", DateTime.Now, command.SerialNumber);
        }

        public IConsumeContext Context { get; set; }
    }
}