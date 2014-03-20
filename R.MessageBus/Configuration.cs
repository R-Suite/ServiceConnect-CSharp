using System;
using System.Reflection;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Interfaces;

namespace R.MessageBus
{
    public class Configuration : IConfiguration
    {
        public Type ConsumerType { get; set; }
        public IBusContainer Container { get; set; }
        public string EndPoint { get; set; }
        public string ConfigurationPath { get; set; }

        public Configuration()
        {
            ConfigurationPath = AppDomain.CurrentDomain.SetupInformation.ConfigurationFile;
            ConsumerType = typeof(Consumer);
        }

        public void SetContainer<T>() where T : class, IBusContainer
        {
            Container = (T)Activator.CreateInstance(typeof(T));
        }

        public void SetConsumer<T>() where T : class, IConsumer 
        {
            ConsumerType = typeof(T);
        }

        public IConsumer GetConsumer()
        {
            return (IConsumer)Activator.CreateInstance(ConsumerType, EndPoint, ConfigurationPath);
        }
    }
}