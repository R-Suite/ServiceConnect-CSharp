using System;
using Ninject;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Container.Ninject
{
    public static class NinjectExtensions
    {
        /// <summary>
        /// Initialize the bus with existing instance of Ninject Container.
        /// </summary>
        /// <param name="configuration"></param>
        /// <param name="container"></param>
        public static void SetContainer(this IConfiguration configuration, StandardKernel container)
        {
            configuration.SetContainerType<NinjectContainer>();
            var busContainer = configuration.GetContainer();
            busContainer.Initialize(container);
        }

        /// <summary>
        /// Configure existing instance of Ninject Container. 
        /// If existing instance does not exist, new one will be created.
        /// </summary>
        /// <param name="configuration"></param>
        /// <param name="containerAction"></param>
        public static void ConfigureExistingContainer(this IConfiguration configuration, Action<StandardKernel> containerAction)
        {
            configuration.SetContainerType<NinjectContainer>();
            var busContainer = configuration.GetContainer();
            containerAction((StandardKernel)busContainer.GetContainer());
        }
    }
}
