using R.MessageBus.Interfaces;
using R.MessageBus.UnitTests.Fakes.ProcessManagers;

namespace R.MessageBus.UnitTests.Fakes
{
    public class FakePersistanceData : IPersistanceData<FakeProcessManagerData>
    {
        public FakeProcessManagerData Data { get; set; }
    }
}