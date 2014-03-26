using System;
using System.Configuration;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Container;
using R.MessageBus.Interfaces;
using R.MessageBus.Persistance.MongoDb;

namespace R.MessageBus
{
    public class Configuration : IConfiguration
    {
        private string _endPoint;
        private string _configurationPath;

        public Type ConsumerType { get; set; }
        public Type Container { get; set; }
        public bool ScanForMesssageHandlers { get; set; }
        public Type ProcessManagerFinder { get; set; }
        public ITransportSettings TransportSettings { get; set; }

        public Configuration()
        {
            _configurationPath = AppDomain.CurrentDomain.SetupInformation.ConfigurationFile;
            ConsumerType = typeof(Consumer);
            Container = typeof(StructuremapContainer);
            ProcessManagerFinder = typeof(MongoDbProcessManagerFinder);
        }

        public void LoadSettings(string configFilePath = null, string endPoint = null)
        {
            var configurationManager = new ConfigurationManagerWrapper(configFilePath);

            var section = configurationManager.GetSection<BusSettings.BusSettings>("BusSettings");

            if (section == null)
            {
                throw new ConfigurationErrorsException("The configuration file must contain a BusSettings section");
            }

            Settings.Settings settings = section.Settings.GetItemByKey(endPoint);

            if (settings == null)
            {
                throw new ConfigurationErrorsException(string.Format("Settings for endpoint {0} could not be found", endPoint));
            }
        }

        public void SetContainer<T>() where T : class, IBusContainer
        {
            Container = typeof(T);
        }

        public void SetProcessManagerFinder<T>() where T : class, IProcessManagerFinder
        {
            ProcessManagerFinder = typeof (T);
        }

        public void SetConsumer<T>() where T : class, IConsumer 
        {
            ConsumerType = typeof(T);
        }

        public IConsumer GetConsumer()
        {
            return (IConsumer)Activator.CreateInstance(ConsumerType, _endPoint, _configurationPath);
        }

        public IBusContainer GetContainer()
        {
            return (IBusContainer)Activator.CreateInstance(Container);
        }

        public IProcessManagerFinder GetProcessManagerFinder()
        {
            // todo
            return null;
            return (IProcessManagerFinder)Activator.CreateInstance(ProcessManagerFinder);
        }
    }
}