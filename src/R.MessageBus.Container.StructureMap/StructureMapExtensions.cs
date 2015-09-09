using System;
using R.MessageBus.Interfaces;
using StructureMap;

namespace R.MessageBus.Container.StructureMap
{
    public static class StructureMapExtensions
    {
        /// <summary>
        /// Initialize the bus with existing instance of StructureMap Container.
        /// </summary>
        /// <param name="configuration"></param>
        /// <param name="container"></param>
        public static void SetContainer(this IConfiguration configuration, IContainer container)
        {
            configuration.SetContainerType<StructureMapContainer>();
            var busContainer = configuration.GetContainer();
            busContainer.Initialize(container);
        }        
        
        /// <summary>
        /// Configure existing instance of StructureMap Container. 
        /// If existing instance does not exist, new one will be created.
        /// </summary>
        /// <param name="configuration"></param>
        /// <param name="containerAction"></param>
        public static void ConfigureExistingContainer(this IConfiguration configuration, Action<IContainer> containerAction)
        {
            configuration.SetContainerType<StructureMapContainer>();
            var busContainer = configuration.GetContainer();
            containerAction((IContainer) busContainer.GetContainer());
        }
    }
}


// Setup container
_container.configure(x => {
    //Setup container
})


StructureMapExtensions.ConfigureContainer(c => {
    return _continer;
})
