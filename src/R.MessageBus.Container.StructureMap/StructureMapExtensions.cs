using System;
using R.MessageBus.Interfaces;
using StructureMap;

namespace R.MessageBus.Container.StructureMap
{
    public static class StructureMapExtensions
    {
        public static void InitializeContainer(this IConfiguration configuration, IContainer container)
        {
            configuration.SetContainerType<StructureMapContainer>();
            var busContainer = configuration.GetContainer();
            busContainer.Initialize(container);
        }        
        
        public static void ConfigureContainer(this IConfiguration configuration, Action<IContainer> containerAction)
        {
            var busContainer = configuration.GetContainer();
            containerAction((IContainer) busContainer.GetContainer());
        }
    }
}
