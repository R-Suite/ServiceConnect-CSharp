//Copyright (C) 2015  Timothy Watson, Jakub Pachansky

//This program is free software; you can redistribute it and/or
//modify it under the terms of the GNU General Public License
//as published by the Free Software Foundation; either version 2
//of the License, or (at your option) any later version.

//This program is distributed in the hope that it will be useful,
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//GNU General Public License for more details.

//You should have received a copy of the GNU General Public License
//along with this program; if not, write to the Free Software
//Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

using System;
using System.Collections.Generic;
using System.Reflection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Container.Default;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistance.InMemory;
using ServiceConnect.Persistance.SqlServer;

namespace ServiceConnect
{
    /// <summary>
    /// Bus configuration.
    /// 
    /// Implicit initialization <see cref="Configuration"/>:
    /// Initialize from default values.
    /// 
    /// </summary>
    public class Configuration : IConfiguration
    {
        private const string DefaultDatabaseName = "RMessageBusPersistantStore";
        private const string DefaultConnectionString = "mongodb://localhost/";
        private const string DefaultHost= "localhost";

        #region Private Fields

        //private string _configurationPath;
        private string _endPoint;
        private string _queueName;
        private string _errorQueueName;
        private string _auditQueueName;
        private bool? _auditingEnabled;
        private Type _containerType = typeof(DefaultBusContainer);
        private IBusContainer _busContainer;
        private IProcessManagerFinder _processManagerFinder;

        #endregion

        #region Public Properties

        public Type ConsumerType { get; set; }
        public Type ProducerType { get; set; }
        public Type ProcessManagerFinder { get; set; }
        public Type AggregatorPersistor { get; set; }
        public Type MessageBusReadStream { get; set; }
        public Type MessageBusWriteStream { get; set; }
        public Type AggregatorProcessor { get; set; }
        public Type ConsumerPoolType { get; set; }
        public bool ScanForMesssageHandlers { get; set; }
        public bool AutoStartConsuming { get; set; }
        public string PersistenceStoreConnectionString { get; set; }
        public string PersistenceStoreDatabaseName { get; set; }
        public ITransportSettings TransportSettings { get; set; }
        public IDictionary<string, IList<string>> QueueMappings { get; set; }
        public Action<Exception> ExceptionHandler { get; set; }
        public bool AddBusToContainer { get; set; }
        public int Threads { get; set; }
        public IList<Type> BeforeConsumingFilters { get; set; }
        public IList<Type> AfterConsumingFilters { get; set; }
        public IList<Type> OutgoingFilters { get; set; }
        public bool EnableProcessManagerTimeouts { get; set; }

        #endregion

        public Configuration()
        {
            ScanForMesssageHandlers = true;
            AddBusToContainer = true;
            AutoStartConsuming = true;

            var defaultQueueName = Assembly.GetEntryAssembly() != null ? Assembly.GetEntryAssembly().GetName().Name : System.Diagnostics.Process.GetCurrentProcess().ProcessName;

            TransportSettings = new TransportSettings 
            { 
                QueueName = defaultQueueName,
                ClientSettings = new Dictionary<string, object>()
            };
            
            SetTransportSettings();
            SetPersistanceSettings();

            QueueMappings = new Dictionary<string, IList<string>>();

            ConsumerType = typeof(Consumer);
            ProducerType = typeof(Producer);
            ProcessManagerFinder = typeof (SqlServerProcessManagerFinder);
            AggregatorPersistor = typeof (InMemoryAggregatorPersistor);
            MessageBusReadStream = typeof (MessageBusReadStream);
            MessageBusWriteStream = typeof (MessageBusWriteStream);
            AggregatorProcessor = typeof(AggregatorProcessor);

            Threads = 1;

            BeforeConsumingFilters = new List<Type>();
            AfterConsumingFilters = new List<Type>();
            OutgoingFilters = new List<Type>();

            ConsumerPoolType = typeof(Consumer);
        }

        /// <summary>
        /// Adds a message queue mapping. 
        /// </summary>
        /// <param name="messageType">Type of message</param>
        /// <param name="queue">Queue to send the message to</param>
        public void AddQueueMapping(Type messageType, string queue)
        {
            if (!QueueMappings.ContainsKey(messageType.FullName))
            {
                QueueMappings.Add(messageType.FullName, new List<string>());
            }

            QueueMappings[messageType.FullName].Add(queue);
        }

        /// <summary>
        /// Adds message queue mappings. 
        /// </summary>
        /// <param name="messageType">Type of message</param>
        /// <param name="queues">Queues to send the message to</param>
        public void AddQueueMapping(Type messageType, IList<string> queues)
        {
            if (!QueueMappings.ContainsKey(messageType.FullName))
            {
                QueueMappings.Add(messageType.FullName, new List<string>());
            }

            foreach (string queue in queues)
            {
                QueueMappings[messageType.FullName].Add(queue);
            }
        }

        public void SetExceptionHandler(Action<Exception> exceptionHandler)
        {
            ExceptionHandler = exceptionHandler;
        }

        /// <summary>
        /// Sets the client host server
        /// </summary>
        /// <param name="host">Server connection string</param>
        public void SetHost(string host)
        {
            TransportSettings.Host = host;
        }

        /// <summary>
        /// Sets the container type.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        public void SetContainerType<T>() where T : class, IBusContainer
        {
            _containerType = typeof(T);
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
        /// Set the aggregator persistor
        /// </summary>
        /// <typeparam name="T"></typeparam>
        public void SetAggregatorPersistor<T>() where T : class, IAggregatorPersistor
        {
            AggregatorPersistor = typeof (T);
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
        public void SetProducer<T>() where T : class, IProducer
        {
            ProducerType = typeof(T);
        }

        /// <summary>
        /// Sets QueueName
        /// </summary>
        public void SetQueueName(string queueName)
        {
            _queueName = queueName;
            TransportSettings.QueueName = queueName;
        }

        /// <summary>
        /// Sets ErrorQueueName
        /// </summary>
        /// <param name="errorQueueName"></param>
        public void SetErrorQueueName(string errorQueueName)
        {
            _errorQueueName = errorQueueName;
            TransportSettings.ErrorQueueName = errorQueueName;
        }

        /// <summary>
        /// Sets AuditingEnabled
        /// </summary>
        /// <param name="auditingEnabled"></param>
        public void SetAuditingEnabled(bool auditingEnabled)
        {
            _auditingEnabled = auditingEnabled;
            TransportSettings.AuditingEnabled = auditingEnabled;
        }

        /// <summary>
        /// Sets AuditQueueName
        /// </summary>
        /// <param name="auditQueueName"></param>
        public void SetAuditQueueName(string auditQueueName)
        {
            _auditQueueName = auditQueueName;
            TransportSettings.AuditQueueName = auditQueueName;
        }

        /// <summary>
        /// Sets Heartbeat queue name
        /// </summary>
        /// <param name="heartbeatQueueName"></param>
        public void SetHeartbeatQueueName(string heartbeatQueueName)
        {
            TransportSettings.HeartbeatQueueName = heartbeatQueueName;
        }

        /// <summary>
        /// Gets QueueName
        /// </summary>
        public string GetQueueName()
        {
            return TransportSettings.QueueName;
        }

        /// <summary>
        /// Gets ErrorQueueName
        /// </summary>
        /// <returns></returns>
        public string GetErrorQueueName()
        {
            return TransportSettings.ErrorQueueName;
        }

        /// <summary>
        /// Gets AuditQueueName
        /// </summary>
        /// <returns></returns>
        public string GetAuditQueueName()
        {
            return TransportSettings.AuditQueueName;
        }

        /// <summary>
        /// Gets instance of IConsumer type
        /// </summary>
        /// <returns></returns>
        public IConsumer GetConsumer()
        {
            return (IConsumer)Activator.CreateInstance(ConsumerType);
        }

        /// <summary>
        /// Gets instance of IProducer type
        /// </summary>
        /// <returns></returns>
        public IProducer GetProducer()
        {
            return (IProducer)Activator.CreateInstance(ProducerType, TransportSettings, QueueMappings);
        }

        /// <summary>
        /// Gets instance of IBusContainer type
        /// </summary>
        /// <returns></returns>
        public IBusContainer GetContainer()
        {
            if (null != _busContainer)
            {
                return _busContainer;
            }

            _busContainer = (IBusContainer)Activator.CreateInstance(_containerType);

            return _busContainer;
        }

        /// <summary>
        /// Gets instance of IProcessManagerFinder type
        /// </summary>
        /// <returns></returns>
        public IProcessManagerFinder GetProcessManagerFinder()
        {
            if (null == _processManagerFinder)
            {
                _processManagerFinder = (IProcessManagerFinder)Activator.CreateInstance(ProcessManagerFinder, PersistenceStoreConnectionString, PersistenceStoreDatabaseName);
            }

            return _processManagerFinder;
        }

        public IAggregatorPersistor GetAggregatorPersistor()
        {
            return (IAggregatorPersistor)Activator.CreateInstance(AggregatorPersistor, PersistenceStoreConnectionString, PersistenceStoreDatabaseName);
        }

        public IRequestConfiguration GetRequestConfiguration(Guid requestMessageId)
        {
            var configuration = new RequestConfiguration(requestMessageId);
            return configuration;
        }

        public void SetDisableErrors(bool disable)
        {
            TransportSettings.DisableErrors = disable;
        }

        public void PurgeQueuesOnStart()
        {
            TransportSettings.PurgeQueueOnStartup = true;
        }

        public IMessageBusReadStream GetMessageBusReadStream()
        {
            return (IMessageBusReadStream) Activator.CreateInstance(MessageBusReadStream);
        }

        public IMessageBusWriteStream GetMessageBusWriteStream(IProducer producer, string endpoint, string sequenceId, IConfiguration configuration)
        {
            return (IMessageBusWriteStream)Activator.CreateInstance(MessageBusWriteStream, producer, endpoint, sequenceId, configuration);
        }

        public IAggregatorProcessor GetAggregatorProcessor(IAggregatorPersistor aggregatorPersistor, IBusContainer container, Type handlerType)
        {
            return (IAggregatorProcessor)Activator.CreateInstance(AggregatorProcessor, aggregatorPersistor, container, handlerType);
        }

        public void SetThreads(int numberOfThreads)
        {
            Threads = numberOfThreads;
        }
        

        #region Private Methods

        private void SetTransportSettings()
        {
            TransportSettings = GetTransportSettingsFromDefaults();
        }

        private void SetPersistanceSettings()
        {
            // Set defaults
            PersistenceStoreDatabaseName = PersistenceStoreDatabaseName ?? DefaultDatabaseName;
            PersistenceStoreConnectionString = PersistenceStoreConnectionString ?? DefaultConnectionString;
        }

        private ITransportSettings GetTransportSettingsFromDefaults()
        {
            ITransportSettings transportSettings = new TransportSettings();
            transportSettings.Host = DefaultHost;
            transportSettings.MaxRetries = 3;
            transportSettings.RetryDelay = 3000;
            transportSettings.Username = null;
            transportSettings.Password = null;
            transportSettings.QueueName = TransportSettings.QueueName;
            transportSettings.MachineName = Environment.MachineName;
            transportSettings.ErrorQueueName = "errors";
            transportSettings.AuditingEnabled = false;
            transportSettings.AuditQueueName = "audit";
            transportSettings.HeartbeatQueueName = "heartbeat";
            transportSettings.ClientSettings = new Dictionary<string, object>();

            return transportSettings;
        }

        #endregion
    }
}