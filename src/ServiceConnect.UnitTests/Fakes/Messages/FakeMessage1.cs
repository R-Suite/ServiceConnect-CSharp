using System;
using ServiceConnect.Interfaces;

namespace ServiceConnect.UnitTests.Fakes.Messages
{
    public class FakeMessage1 : Message
    {
        public FakeMessage1(Guid correlationId) : base(correlationId) { }
        public string Username { get; set; } = "";
    }
}
