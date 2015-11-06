﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PointToPoint.ZeroMQ.Messages;
using ServiceConnect.Interfaces;

namespace PointToPoint.ZeroMQ.Consumer
{
    public class PointToPointMessageHandler : IMessageHandler<PointToPointMessage>
    {
        public void Execute(PointToPointMessage command)
        {
            //throw new Exception("test");
            Console.WriteLine("Received message - {0} {1}", command.Count, DateTime.Now);
        }

        public IConsumeContext Context { get; set; }
    }
}
