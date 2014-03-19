using System;
using R.MessageBus.Interfaces;

namespace R.MessageBus.TestPubSub
{
    public class TestHandler : IMessageHandler<TestMessage>
    {
        public void Execute(TestMessage command)
        {
            throw new Exception("Test Exception.");
        }
    }
}