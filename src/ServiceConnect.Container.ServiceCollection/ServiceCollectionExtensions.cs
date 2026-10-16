using System;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Container.ServiceCollection
{
    public static class ServiceCollectionExtensions
    {
        public static void AddServiceConnect(this IServiceCollection services, Action<IConfiguration> config)
        {
            Action<IConfiguration> useServiceCollection = newConfig =>
            {
                newConfig.SetContainerType<ServiceCollectionContainer>();
                var busContainer = newConfig.GetContainer();
                busContainer.Initialize(services);
            };

            Bus.Initialize(useServiceCollection + config);
        }
    }
}