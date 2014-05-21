using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using log4net;
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

        public void ProcessMessage<T>(T message, IConsumeContext context) where T : Message
        {
            StartProcessManagers(message, context);
            LoadExistingProcessManagers(message, context);
        }

        private void StartProcessManagers<T>(T message, IConsumeContext context) where T : Message
        {
            List<HandlerReference> processManagerInstances = _container.GetHandlerTypes(typeof(IStartProcessManager<T>)).ToList();

            foreach (HandlerReference processManagerInstance in processManagerInstances)
            {
                try
                {
                    // Create instance of the project manager
                    object processManager = _container.GetInstance(processManagerInstance.HandlerType);

                    // Get Data Type
                    Type dataType = processManagerInstance.HandlerType.BaseType.GetGenericArguments()[0];

                    // Create new instance 
                    var data = (IProcessManagerData)Activator.CreateInstance(dataType);

                    // Set data on process manager
                    PropertyInfo dataProp = processManagerInstance.HandlerType.GetProperty("Data");
                    dataProp.SetValue(processManager, data, null);

                    // Set context property value
                    PropertyInfo contextProp = processManagerInstance.HandlerType.GetProperty("Context", dataType);
                    contextProp.SetValue(processManager, context, null);

                    // Execute process manager execute method
                    processManagerInstance.HandlerType.GetMethod("Execute", new[] { typeof(T) }).Invoke(processManager, new object[] { message });

                    // Get data after execute has finished
                    data = (IProcessManagerData)dataProp.GetValue(processManager);

                    // Persist data
                    _processManagerFinder.InsertData(data);
                }
                catch (Exception ex)
                {
                    Logger.Error(string.Format("Error executing process manager start handler. {0}", processManagerInstance.HandlerType.FullName), ex);
                    throw;
                }
            }
        }

        private void LoadExistingProcessManagers<T>(T message, IConsumeContext context) where T : Message
        {
            IEnumerable<HandlerReference> handlerReferences = _container.GetHandlerTypes(typeof(IMessageHandler<T>))
                                                                        .Where(h => h.HandlerType.BaseType != null &&
                                                                                    h.HandlerType.BaseType.Name == typeof(ProcessManager<>).Name);

            foreach (HandlerReference handlerReference in handlerReferences)
            {
                try
                {
                    // Create instance of the project manager
                    object processManager = _container.GetInstance(handlerReference.HandlerType);

                    // Set Process Manager Finder property
                    PropertyInfo processManagerFinderProp = handlerReference.HandlerType.GetProperty("ProcessManagerFinder");
                    processManagerFinderProp.SetValue(processManager, _processManagerFinder, null);

                    // Execute FindProcessManagerData
                    object persistanceData = handlerReference.HandlerType.GetMethod("FindProcessManagerData")
                                                                            .Invoke(processManager, new object[] { message });

                    // Get data type
                    Type dataType = handlerReference.HandlerType.BaseType.GetGenericArguments()[0];

                    // Get data from persistance data
                    var persistanceType = typeof (IPersistanceData<>).MakeGenericType(dataType);
                    PropertyInfo dataProp = persistanceType.GetProperty("Data");
                    object data = dataProp.GetValue(persistanceData);

                    // Set data property value
                    PropertyInfo prop = handlerReference.HandlerType.GetProperty("Data", dataType);
                    prop.SetValue(processManager, data, null);

                    // Set context property value
                    PropertyInfo contextProp = handlerReference.HandlerType.GetProperty("Context", dataType);
                    contextProp.SetValue(processManager, context, null);

                    // Execute handler
                    handlerReference.HandlerType.GetMethod("Execute", new[] { typeof(T) }).Invoke(processManager, new object[] {message});

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
                    Logger.Error(string.Format("Error executing process manager handler. {0}", handlerReference.HandlerType.FullName),
                        ex);
                    throw;
                }
            }
        }
    }
}