using System;
using System.Collections.Generic;
using Aggregator.Messages;
using ServiceConnect.Interfaces;

namespace Aggregator.Consumer
{
    public class TestMessageHandler : Aggregator<TestMessage>
    {
        public override int BatchSize()
        {
            return 100;
        }
        public override TimeSpan Timeout()
        {
            return new TimeSpan(0, 0, 0, 2);
        }
        
        public override void Execute(IList<TestMessage> message)
        {
            Console.WriteLine("***** Received batch of messages ({0}) ******", message.Count);
        }
    }
}