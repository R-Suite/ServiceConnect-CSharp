using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Common.Logging;
using Newtonsoft.Json;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    public class ProcessManagerProcessor : IProcessManagerProcessor
    {
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly IProcessManagerFinder _processManagerFinder;
        private readonly IBusContainer _container;

        public ProcessManagerProcessor(IProcessManagerFinder processManagerFinder, IBusContainer container)
        {
            _processManagerFinder = processManagerFinder;
            _container = container;
        }

        public void ProcessMessage<T>(string message, IConsumeContext context) where T : Message
        {
            StartProcessManagers<T>(message, context);
            LoadExistingProcessManagers<T>(message, context);
        }

        private void StartProcessManagersBaseType<T, TB>(string message, IConsumeContext context) where T : Message where TB : Message
        {
            List<HandlerReference> processManagerInstances = _container.GetHandlerTypes(typeof(IStartProcessManager<TB>)).ToList();

            InitStartProcessManagerHandlers<T>(message, context, processManagerInstances, typeof(TB));
        }

        private void StartProcessManagers<T>(string message, IConsumeContext context, Type baseType = null) where T : Message
        {
            List<HandlerReference> processManagerInstances = _container.GetHandlerTypes(typeof(IStartProcessManager<T>)).ToList();

            InitStartProcessManagerHandlers<T>(message, context, processManagerInstances, baseType);
        }

        private void InitStartProcessManagerHandlers<T>(string message, IConsumeContext context, IEnumerable<HandlerReference> processManagerInstances, Type baseType = null) where T : Message
        {
            Type msgType = baseType ?? typeof(T);

            foreach (HandlerReference processManagerInstance in processManagerInstances)
            {
                try
                {
                    // Create instance of the project manager
                    object processManager = _container.GetInstance(processManagerInstance.HandlerType);

                    // Get Data Type
                    Type dataType = processManagerInstance.HandlerType.BaseType.GetGenericArguments()[0];

                    // Create new instance 
                    var data = (IProcessManagerData) Activator.CreateInstance(dataType);

                    // Set data on process manager
                    PropertyInfo dataProp = processManagerInstance.HandlerType.GetProperty("Data");
                    dataProp.SetValue(processManager, data, null);

                    // Set context property value
                    PropertyInfo contextProp = processManagerInstance.HandlerType.GetProperty("Context",
                        typeof (IConsumeContext));
                    contextProp.SetValue(processManager, context, null);

                    // Execute process manager execute method
                    var messageObject = JsonConvert.DeserializeObject(message, typeof (T));
                    processManagerInstance.HandlerType.GetMethod("Execute", new[] { msgType }).Invoke(processManager, new[] { messageObject });

                    // Get data after execute has finished
                    data = (IProcessManagerData) dataProp.GetValue(processManager);

                    // Persist data
                    _processManagerFinder.InsertData(data);
                }
                catch (Exception ex)
                {
                    Logger.Error(
                        string.Format("Error executing process manager start handler. {0}",
                            processManagerInstance.HandlerType.FullName), ex);
                    throw;
                }
            }

            // This is used when processing Sent (rather than Published) messages
            // Get message BaseType and call ProcessMessage recursively to see if there are any handlers interested in the BaseType
            Type newBaseType = msgType.BaseType;
            if (newBaseType != null && newBaseType.Name != typeof(object).Name)
            {
                MethodInfo startProcessManagers = GetType().GetMethod("StartProcessManagersBaseType", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo genericStartProcessManagers = startProcessManagers.MakeGenericMethod(typeof (T), newBaseType);
                genericStartProcessManagers.Invoke(this, new object[] {message, context});
            }
        }


        private void LoadExistingProcessManagersBaseType<T, TB>(string message, IConsumeContext context) where T : Message where TB : Message
        {
            IEnumerable<HandlerReference> handlerReferences = _container.GetHandlerTypes(typeof(IMessageHandler<TB>))
                                                                        .Where(h => h.HandlerType.BaseType != null &&
                                                                                    h.HandlerType.BaseType.Name == typeof(ProcessManager<>).Name);

            InitLoadExistingProcessManagerHandlers<T>(message, context, handlerReferences, typeof(TB));
        }

        private void LoadExistingProcessManagers<T>(string message, IConsumeContext context, Type baseType = null) where T : Message
        {
            IEnumerable<HandlerReference> handlerReferences = _container.GetHandlerTypes(typeof(IMessageHandler<T>))
                                                                        .Where(h => h.HandlerType.BaseType != null &&
                                                                                    h.HandlerType.BaseType.Name == typeof(ProcessManager<>).Name);

            InitLoadExistingProcessManagerHandlers<T>(message, context, handlerReferences, baseType);
        }

        private void InitLoadExistingProcessManagerHandlers<T>(string message, IConsumeContext context, IEnumerable<HandlerReference> handlerReferences, Type baseType = null) where T : Message
        {
            Type msgType = baseType ?? typeof(T);

            foreach (HandlerReference handlerReference in handlerReferences)
            {
                try
                {
                    var messageObject = (Message) JsonConvert.DeserializeObject(message, typeof (T));

                    // Create instance of the project manager
                    object processManager = _container.GetInstance(handlerReference.HandlerType);

                    // Set Process Manager Finder property
                    PropertyInfo processManagerFinderProp = handlerReference.HandlerType.GetProperty("ProcessManagerFinder");
                    processManagerFinderProp.SetValue(processManager, _processManagerFinder, null);

                    // Execute FindProcessManagerData
                    object persistanceData = handlerReference.HandlerType.GetMethod("FindProcessManagerData").Invoke(processManager, new[] {messageObject});

                    // Get data type
                    Type dataType = handlerReference.HandlerType.BaseType.GetGenericArguments()[0];

                    if (null == persistanceData)
                    {
                        Logger.Warn(string.Format("ProcessManagerData not found for {0}. message.CorrelationId = {1}", handlerReference.HandlerType, messageObject.CorrelationId));
                        continue;
                    }

                    // Get data from persistance data
                    var persistanceType = typeof (IPersistanceData<>).MakeGenericType(dataType);
                    PropertyInfo dataProp = persistanceType.GetProperty("Data");
                    object data = dataProp.GetValue(persistanceData);

                    // Set data property value
                    PropertyInfo prop = handlerReference.HandlerType.GetProperty("Data", dataType);
                    prop.SetValue(processManager, data, null);

                    // Set context property value
                    PropertyInfo contextProp = handlerReference.HandlerType.GetProperty("Context", typeof (IConsumeContext));
                    contextProp.SetValue(processManager, context, null);

                    // ***Execute handler***
                    handlerReference.HandlerType.GetMethod("Execute", new[] { msgType }).Invoke(processManager, new object[] { messageObject });

                    // Get Complete property value
                    PropertyInfo completeProperty = handlerReference.HandlerType.GetProperty("Complete");
                    var isComplete = (bool) completeProperty.GetValue(processManager);

                    if (isComplete)
                    {
                        // Delete if the process manager is complete
                        _processManagerFinder.GetType()
                            .GetMethod("DeleteData")
                            .MakeGenericMethod(dataType)
                            .Invoke(_processManagerFinder, new[] {persistanceData});
                    }
                    else
                    {
                        // Otherwise update
                        _processManagerFinder.GetType()
                            .GetMethod("UpdateData")
                            .MakeGenericMethod(dataType)
                            .Invoke(_processManagerFinder, new[] {persistanceData});
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(
                        string.Format("Error executing process manager handler. {0}", handlerReference.HandlerType.FullName),
                        ex);
                    throw;
                }
            }

            // This is used when processing Sent (rather than Published) messages
            // Get message BaseType and call ProcessMessage recursively to see if there are any handlers interested in the BaseType
            Type newBaseType = msgType.BaseType;
            if (newBaseType != null && newBaseType.Name != typeof (object).Name)
            {
                MethodInfo loadExistingProcessManagers = GetType().GetMethod("LoadExistingProcessManagersBaseType", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo genericLoadExistingProcessManagers = loadExistingProcessManagers.MakeGenericMethod(typeof (T),newBaseType);
                genericLoadExistingProcessManagers.Invoke(this, new object[] {message, context});
            }
        }
    }
}