using System;
using R.MessageBus.Interfaces;

namespace R.MessageBus.UnitTests.Fakes.Messages
{
    [Serializable]
    public class FakeMessage2 : Message
    {
        public FakeMessage2(Guid correlationId) : base(correlationId) { }
        public string DisplayName { get; set; }
    }
}