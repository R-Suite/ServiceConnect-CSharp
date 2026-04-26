using System;
using ServiceConnect.Interfaces;

namespace ServiceConnect.UnitTests.Fakes.Messages;

public class FakeMessage1(Guid correlationId) : Message(correlationId)
{
    public string Username { get; set; } = "";
}
