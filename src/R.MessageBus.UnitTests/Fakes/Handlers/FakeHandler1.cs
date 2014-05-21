using R.MessageBus.Interfaces;
using R.MessageBus.UnitTests.Fakes.Messages;

namespace R.MessageBus.UnitTests.Fakes.Handlers
{
    public class FakeHandler1 : IMessageHandler<FakeMessage1>
    {
        public IConsumeContext Context { get; set; }

        public void Execute(FakeMessage1 command)
        {
            Command = command;
        }

        public FakeMessage1 Command { get; set; }
    }
}