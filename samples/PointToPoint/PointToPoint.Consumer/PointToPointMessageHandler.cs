using System;
using System.Text;
using System.Threading;
using PointToPoint.Messages;
using R.MessageBus.Interfaces;

namespace PointToPoint.Consumer
{
    public class PointToPointMessageHandler : IMessageHandler<PointToPointMessage>
    {
        public void Execute(PointToPointMessage command)
        {
            Thread.Sleep(100);
        }

        public IConsumeContext Context { get; set; }
    }
}