using System;
using R.MessageBus.Interfaces;
using R.MessageBus.Interfaces.Container;

namespace R.MessageBus.Core.Container
{
    public static class DefaultBusContainerExtensions
    {
        public static void InitializeContainer(this IConfiguration configuration, IServicesRegistrar container)
        {
            configuration.SetContainerType<DefaultBusContainer>();
            var busContainer = configuration.GetContainer();
            busContainer.Initialize(container);
        }

        public static void ConfigureContainer(this IConfiguration configuration, Action<IServicesRegistrar> containerAction)
        {
            var busContainer = configuration.GetContainer();
            containerAction((IServicesRegistrar)busContainer.GetContainer());
        }
    }
}
