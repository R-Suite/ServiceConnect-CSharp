using System;
using System.Collections.Generic;
using BusSettings;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Container;
using R.MessageBus.Interfaces;
using R.MessageBus.Persistance.MongoDb;
using Exchange = R.MessageBus.Interfaces.Exchange;
using Queue = R.MessageBus.Interfaces.Queue;

namespace R.MessageBus
{
    public class Configuration : IConfiguration
    {
        #region Private Fields

        private string _endPoint;
        private string _configurationPath;

        #endregion

        #region Public Properties

        public Type ConsumerType { get; set; }
        public Type Container { get; set; }
        public Type ProcessManagerFinder { get; set; }
        public bool ScanForMesssageHandlers { get; set; }
        public string PersistenceStoreConnectionString { get; set; }
        public string PersistenceStoreDatabaseName { get; set; }
        public ITransportSettings TransportSettings { get; set; }

        #endregion

        public Configuration()
        {
            _configurationPath = AppDomain.CurrentDomain.SetupInformation.ConfigurationFile;
            SetTransportSettings(_configurationPath);

            ConsumerType = typeof(Consumer);
            Container = typeof(StructuremapContainer);
            ProcessManagerFinder = typeof(MongoDbProcessManagerFinder);
        }

        /// <summary>
        /// Load settings from configFilePath. 
        /// Use default App.config when configFilePath is not specified. 
        /// </summary>
        /// <param name="configFilePath">configuration file path</param>
        /// <param name="endPoint">RabbitMq settings endpoint name</param>
        public void LoadSettings(string configFilePath = null, string endPoint = null)
        {
            if (null != configFilePath)
            {
                _configurationPath = configFilePath;
            }

            _endPoint = endPoint;

            SetTransportSettings(_configurationPath, _endPoint);
        }
        
        /// <summary>
        /// Sets the container.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        public void SetContainer<T>() where T : class, IBusContainer
        {
            Container = typeof(T);
        }

        /// <summary>
        /// Sets the process manager finder
        /// </summary>
        /// <typeparam name="T"></typeparam>
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
            return (IConsumer)Activator.CreateInstance(ConsumerType, TransportSettings);
        }

        public IBusContainer GetContainer()
        {
            return (IBusContainer)Activator.CreateInstance(Container);
        }

        public IProcessManagerFinder GetProcessManagerFinder()
        {
            return (IProcessManagerFinder)Activator.CreateInstance(ProcessManagerFinder, PersistenceStoreConnectionString, PersistenceStoreDatabaseName);
        }

        #region Private Methods

        private void SetTransportSettings(string configFilePath, string endPoint = null)
        {
            var configurationManager = new ConfigurationManagerWrapper(configFilePath);

            var section = configurationManager.GetSection<BusSettings.BusSettings>("BusSettings");

            if (section != null)
            {
                Settings.Settings settings = !string.IsNullOrEmpty(endPoint) ? section.Settings.GetItemByKey(endPoint) : section.Settings.GetItemAt(0);

                if (null != settings)
                {
                    PersistenceStoreDatabaseName = PersistenceStoreDatabaseName ?? settings.PersistantStore.Database;
                    PersistenceStoreConnectionString = PersistenceStoreConnectionString ?? settings.PersistantStore.ConnectionString;
                    
                    TransportSettings = GetTransportSettingsFromBusSettings(settings);
                }
            }

            if (null == TransportSettings)
            {
                PersistenceStoreDatabaseName = PersistenceStoreDatabaseName ?? "RMessageBusPersistantStore";
                PersistenceStoreConnectionString = PersistenceStoreConnectionString ?? "host=localhost";
                
                TransportSettings = GetTransportSettingsFromDefaults();
            }
        }

        private ITransportSettings GetTransportSettingsFromBusSettings(Settings.Settings settings)
        {
            ITransportSettings transportSettings = new TransportSettings();
            transportSettings.Host = settings.Host;
            transportSettings.MaxRetries = settings.Retries.MaxRetries;
            transportSettings.RetryDelay = settings.Retries.RetryDelay;
            transportSettings.Username = settings.Username;
            transportSettings.Password = settings.Password;
            transportSettings.NoAck = settings.NoAck;
            transportSettings.Queue.Name = settings.Queue.Name;
            transportSettings.Queue.RoutingKey = settings.Queue.RoutingKey;
            transportSettings.Queue.Arguments = GetQueueArguments(settings);
            transportSettings.Queue.AutoDelete = settings.Queue.AutoDelete;
            transportSettings.Queue.Durable = settings.Queue.Durable;
            transportSettings.Queue.Exclusive = settings.Queue.Exclusive;
            transportSettings.Queue.IsReadOnly = settings.Queue.IsReadOnly();
            transportSettings.Exchange.Name = settings.Exchange.Name;
            transportSettings.Exchange.Type = settings.Exchange.Type;
            transportSettings.Exchange.Arguments = GetExchangeArguments(settings);
            transportSettings.Exchange.AutoDelete = settings.Exchange.AutoDelete;
            transportSettings.Exchange.Durable = settings.Exchange.Durable;
            transportSettings.Exchange.IsReadOnly = settings.Exchange.IsReadOnly();

            return transportSettings;
        }

        private ITransportSettings GetTransportSettingsFromDefaults()
        {
            ITransportSettings transportSettings = new TransportSettings();
            transportSettings.Host = "localhost";
            transportSettings.MaxRetries = 3;
            transportSettings.RetryDelay = 3000;
            transportSettings.Username = null;
            transportSettings.Password = null;
            transportSettings.NoAck = false;
            transportSettings.Queue = new Queue
            {
                Name = null,
                RoutingKey = null,
                Arguments = null,
                AutoDelete = false,
                Durable = true,
                Exclusive = false,
                IsReadOnly = false
            };
            transportSettings.Exchange = new Exchange
            {
                Name = "RMessageBusExchange", //todo: review default exchange name
                Type = "direct",
                Arguments = null,
                AutoDelete = false,
                Durable = false,
                IsReadOnly = false,
            };

            return transportSettings;
        }

        private static Dictionary<string, object> GetExchangeArguments(Settings.Settings settings)
        {
            var exchangeeArguments = new Dictionary<string, object>();
            for (var i = 0; i < settings.Exchange.Arguments.Count; i++)
            {
                exchangeeArguments.Add(settings.Exchange.Arguments[i].Name, settings.Exchange.Arguments[i].Value);
            }
            return exchangeeArguments;
        }

        private static Dictionary<string, object> GetQueueArguments(Settings.Settings settings)
        {
            var queueArguments = new Dictionary<string, object>();
            for (var i = 0; i < settings.Queue.Arguments.Count; i++)
            {
                queueArguments.Add(settings.Queue.Arguments[i].Name, settings.Queue.Arguments[i].Value);
            }
            return queueArguments;
        }

        #endregion
    }
}