using System;
using System.Collections.Generic;
using BusConfiguration;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Container;
using R.MessageBus.Interfaces;
using R.MessageBus.Persistance.MongoDb;
using Exchange = R.MessageBus.Interfaces.Exchange;
using Queue = R.MessageBus.Interfaces.Queue;

namespace R.MessageBus
{
    /// <summary>
    /// Bus configuration.
    /// 
    /// Implicit initialization <see cref="Configuration"/>:
    /// Initialize from default values.
    /// 
    /// Explicit initialization <see cref="LoadSettings"/>:
    /// Initialize from BusSettings section of a custom configuration file,
    /// throw exception if the section is not found
    /// </summary>
    public class Configuration : IConfiguration
    {
        private const string DefaultDatabaseName = "RMessageBusPersistantStore";
        private const string DefaultConnectionString = "mongodb://localhost/";
        private const string DefaultExchangeName = "RMessageBusExchange";
        private const string DefaultHost= "localhost";

        #region Private Fields

        private string _endPoint;
        private string _configurationPath;

        #endregion

        #region Public Properties

        public Type ConsumerType { get; set; }
        public Type PublisherType { get; set; }
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

            SetTransportSettings();
            SetPersistanceSettings();

            ConsumerType = typeof(Consumer);
            PublisherType = typeof(Publisher);
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

            //todo: candidate for IoC
            var configurationManager = new ConfigurationManagerWrapper(_configurationPath);

            var section = configurationManager.GetSection<BusSettings.BusSettings>("BusSettings");

            if (section == null) throw new ArgumentException("BusSettings section not found in the configuration file.");

            SetTransportSettings(section, _endPoint);
            SetPersistanceSettings(section, _endPoint);
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

        /// <summary>
        /// Sets consumer
        /// </summary>
        /// <typeparam name="T"></typeparam>
        public void SetConsumer<T>() where T : class, IConsumer 
        {
            ConsumerType = typeof(T);
        }

        /// <summary>
        /// Sets publisher
        /// </summary>
        /// <typeparam name="T"></typeparam>
        public void SetPublisher<T>() where T : class, IPublisher
        {
            PublisherType = typeof(T);
        }

        /// <summary>
        /// Gets instance of IConsumer type
        /// </summary>
        /// <returns></returns>
        public IConsumer GetConsumer()
        {
            return (IConsumer)Activator.CreateInstance(ConsumerType, TransportSettings);
        }

        /// <summary>
        /// Gets instance of IPublisher type
        /// </summary>
        /// <returns></returns>
        public IPublisher GetPublisher()
        {
            return (IPublisher)Activator.CreateInstance(PublisherType, TransportSettings);
        }

        /// <summary>
        /// Gets instance of IBusContainer type
        /// </summary>
        /// <returns></returns>
        public IBusContainer GetContainer()
        {
            return (IBusContainer)Activator.CreateInstance(Container);
        }

        /// <summary>
        /// Gets instance of IProcessManagerFinder type
        /// </summary>
        /// <returns></returns>
        public IProcessManagerFinder GetProcessManagerFinder()
        {
            return (IProcessManagerFinder)Activator.CreateInstance(ProcessManagerFinder, PersistenceStoreConnectionString, PersistenceStoreDatabaseName);
        }

        #region Private Methods

        private void SetTransportSettings(BusSettings.BusSettings section = null, string endPoint = null)
        {
            if (null != section)
            {
                var endPointSettings = !string.IsNullOrEmpty(endPoint) ? section.EndpointSettings.GetItemByKey(endPoint) : section.EndpointSettings.GetItemAt(0);
                var transportSettings = endPointSettings.TransportSettings;

                if (null != transportSettings)
                {
                    TransportSettings = GetTransportSettingsFromBusSettings(transportSettings);

                    return;
                }
            }

            // Set defaults
            TransportSettings = GetTransportSettingsFromDefaults();
        }

        private void SetPersistanceSettings(BusSettings.BusSettings section = null, string endPoint = null)
        {
            if (null != section)
            {
                var endPointSettings = !string.IsNullOrEmpty(endPoint) ? section.EndpointSettings.GetItemByKey(endPoint) : section.EndpointSettings.GetItemAt(0);
                var persistanceSettings = endPointSettings.PersistanceSettings;

                if (null != persistanceSettings)
                {
                    PersistenceStoreDatabaseName = !string.IsNullOrEmpty(persistanceSettings.Database) ? persistanceSettings.Database : DefaultDatabaseName;
                    PersistenceStoreConnectionString = PersistenceStoreConnectionString ?? persistanceSettings.ConnectionString;

                    return;
                }
            }

            // Set defaults
            PersistenceStoreDatabaseName = PersistenceStoreDatabaseName ?? DefaultDatabaseName;
            PersistenceStoreConnectionString = PersistenceStoreConnectionString ?? DefaultConnectionString;
        }

        private ITransportSettings GetTransportSettingsFromBusSettings(BusConfiguration.TransportSettings settings)
        {
            ITransportSettings transportSettings = new R.MessageBus.Settings.TransportSettings();
            transportSettings.Host = settings.Host;
            transportSettings.MaxRetries = settings.Retries.MaxRetries;
            transportSettings.RetryDelay = settings.Retries.RetryDelay;
            transportSettings.Username = settings.Username;
            transportSettings.Password = settings.Password;
            transportSettings.NoAck = settings.NoAck;
            transportSettings.Queue = new Queue
            {
                Name = settings.Queue.Name,
                RoutingKey = settings.Queue.RoutingKey,
                Arguments = GetQueueArguments(settings),
                AutoDelete = settings.Queue.AutoDelete,
                Durable = settings.Queue.Durable,
                Exclusive = settings.Queue.Exclusive,
                IsReadOnly = settings.Queue.IsReadOnly()
            };
            transportSettings.Exchange = new Exchange
            {
                Name = settings.Exchange.Name,
                Arguments = GetExchangeArguments(settings),
                AutoDelete = settings.Exchange.AutoDelete,
                Durable = settings.Exchange.Durable,
                IsReadOnly = settings.Exchange.IsReadOnly(),
            };

            return transportSettings;
        }

        private ITransportSettings GetTransportSettingsFromDefaults()
        {
            ITransportSettings transportSettings = new R.MessageBus.Settings.TransportSettings();
            transportSettings.Host = DefaultHost;
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
                Name = DefaultExchangeName,
                Arguments = null,
                AutoDelete = false,
                Durable = false,
                IsReadOnly = false,
            };

            return transportSettings;
        }

        private static Dictionary<string, object> GetExchangeArguments(BusConfiguration.TransportSettings settings)
        {
            var exchangeeArguments = new Dictionary<string, object>();
            for (var i = 0; i < settings.Exchange.Arguments.Count; i++)
            {
                exchangeeArguments.Add(settings.Exchange.Arguments[i].Name, settings.Exchange.Arguments[i].Value);
            }
            return exchangeeArguments;
        }

        private static Dictionary<string, object> GetQueueArguments(BusConfiguration.TransportSettings settings)
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