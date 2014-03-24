using System;
using R.MessageBus.Interfaces;

namespace R.MessageBus.UnitTests.Fakes
{
    [Serializable]
    public class FakeMessage1 : Message
    {
        public FakeMessage1(Guid correlationId) : base(correlationId) { }
        public string Username { get; set; }
    }

    public class FakeHandler1 : IMessageHandler<FakeMessage1>
    {
        public void Execute(FakeMessage1 command)
        {
            Command = command;
        }

        public FakeMessage1 Command { get; set; }
    }

    [Serializable]
    public class FakeMessage2 : Message
    {
        public FakeMessage2(Guid correlationId) : base(correlationId) { }
        public string DisplayName { get; set; }
    }

    public class FakeHandler2 : IMessageHandler<FakeMessage2>
    {
        public void Execute(FakeMessage2 command) { }
    }
}