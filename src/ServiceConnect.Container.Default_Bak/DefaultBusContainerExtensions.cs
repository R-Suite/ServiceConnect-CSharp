using System;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Container;

namespace ServiceConnect.Container.Default
{
    public static class DefaultBusContainerExtensions
    {
        /// <summary>
        /// Initialize the bus with existing instance of ServicesRegistrar (custom ServiceConnect) Container.
        /// </summary>
        /// <param name="configuration"></param>
        /// <param name="container"></param>
        public static void SetContainer(this IConfiguration configuration, IServicesRegistrar container)
        {
            configuration.SetContainerType<DefaultBusContainer>();
            var busContainer = configuration.GetContainer();
            busContainer.Initialize(container);
        }

        /// <summary>
        /// Configure existing instance of ServicesRegistrar (custom ServiceConnect) Container. 
        /// If existing instance does not exist, new one will be created.
        /// </summary>
        /// <param name="configuration"></param>
        /// <param name="containerAction"></param>
        public static void ConfigureExistingContainer(this IConfiguration configuration, Action<IServicesRegistrar> containerAction)
        {
            configuration.SetContainerType<DefaultBusContainer>();
            var busContainer = configuration.GetContainer();
            containerAction((IServicesRegistrar)busContainer.GetContainer());
        }
    }
}
