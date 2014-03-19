using R.MessageBus.Interfaces;
using StructureMap;

namespace R.MessageBus
{
    public static class Container
    {
        public static void Initialize()
        {
            ObjectFactory.Configure(x => x.Scan(y =>
            {
                y.AssembliesFromApplicationBaseDirectory();
                y.ConnectImplementationsToTypesClosing(typeof(IMessageHandler<>));
            }));
        }
    }
}