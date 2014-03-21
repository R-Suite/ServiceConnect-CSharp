using System;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Container;
using R.MessageBus.Interfaces;

namespace R.MessageBus
{
    public class Configuration : IConfiguration
    {
        public Type ConsumerType { get; set; }
        public Type Container { get; set; }
        public string EndPoint { get; set; }
        public string ConfigurationPath { get; set; }
        public bool ScanForMesssageHandlers { get; set; }

        public Configuration()
        {
            ConfigurationPath = AppDomain.CurrentDomain.SetupInformation.ConfigurationFile;
            ConsumerType = typeof(Consumer);
            Container = typeof(StructuremapContainer);
        }

        public void SetContainer<T>() where T : class, IBusContainer
        {
            Container = typeof(T);
        }

        public void SetConsumer<T>() where T : class, IConsumer 
        {
            ConsumerType = typeof(T);
        }

        public IConsumer GetConsumer()
        {
            return (IConsumer)Activator.CreateInstance(ConsumerType, EndPoint, ConfigurationPath);
        }

        public IBusContainer GetContainer()
        {
            return (IBusContainer)Activator.CreateInstance(Container);
        }
    }
}