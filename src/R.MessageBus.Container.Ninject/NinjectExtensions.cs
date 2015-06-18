using System;
using Ninject;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Container.Ninject
{
    public static class NinjectExtensions
    {
        public static void InitializeContainer(this IConfiguration configuration, StandardKernel container)
        {
            configuration.SetContainerType<NinjectContainer>();
            var busContainer = configuration.GetContainer();
            busContainer.Initialize(container);
        }

        public static void ConfigureContainer(this IConfiguration configuration, Action<StandardKernel> containerAction)
        {
            var busContainer = configuration.GetContainer();
            containerAction((StandardKernel)busContainer.GetContainer());
        }
    }
}
