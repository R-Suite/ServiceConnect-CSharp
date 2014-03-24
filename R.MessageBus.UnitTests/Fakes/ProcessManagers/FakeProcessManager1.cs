using System;
using R.MessageBus.Interfaces;
using R.MessageBus.UnitTests.Fakes.Messages;

namespace R.MessageBus.UnitTests.Fakes.ProcessManagers
{
    public class FakeProcessManager1 : ProcessManager<FakeProcessManagerData>,
                                       IStartProcessManager<FakeMessage1>,
                                       IMessageHandler<FakeMessage2>
    {
        public void Execute(FakeMessage1 command)
        {
            throw new NotImplementedException();
        }

        public void Execute(FakeMessage2 command)
        {
            throw new NotImplementedException();
        }
    }
}