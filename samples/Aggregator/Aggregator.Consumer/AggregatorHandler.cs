using System;
using System.Collections.Generic;
using Aggregator.Messages;
using R.MessageBus.Core;

namespace Aggregator.Consumer
{
    public class StreamHandler : Aggregator<TestMessage>
    {
        //public override int BatchSize()
        //{
        //    return 100;
        //}
        public override TimeSpan Timeout()
        {
            return new TimeSpan(0, 0, 0, 1);
        }
        
        public override void Execute(IList<TestMessage> message)
        {
            Console.WriteLine("***** Received batch of messages ******");
        }
    }
}