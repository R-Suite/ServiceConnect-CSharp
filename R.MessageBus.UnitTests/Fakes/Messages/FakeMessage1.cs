using System;
using R.MessageBus.Interfaces;

namespace R.MessageBus.UnitTests.Fakes.Messages
{
    [Serializable]
    public class FakeMessage1 : Message
    {
        public FakeMessage1(Guid correlationId) : base(correlationId) { }
        public string Username { get; set; }
    }
}