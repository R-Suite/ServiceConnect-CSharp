using System;
using R.MessageBus.Interfaces;

namespace R.MessageBus.TestPubSub
{
    [Serializable]
    public class TestMessage : Message
    {
        public TestMessage(Guid id) : base(id)
        {
        }
    }
}