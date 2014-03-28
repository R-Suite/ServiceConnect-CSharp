using R.MessageBus.Interfaces;
using R.MessageBus.UnitTests.Fakes.Messages;

namespace R.MessageBus.UnitTests.Fakes.Handlers
{
    public class FakeHandler2 : IMessageHandler<FakeMessage2>
    {
        public void Execute(FakeMessage2 command) { }
    }
}