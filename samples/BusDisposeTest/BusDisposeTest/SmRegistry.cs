using StructureMap;
using StructureMap.Configuration.DSL;

namespace BusDisposeTest
{
    public class SmRegistry : Registry
    {
        public SmRegistry()
        {
            ObjectFactory.Configure(
                cfg =>
                {
                    cfg.For<ITestClass>().Use<TestClass>();
                });
        }
    }
}
