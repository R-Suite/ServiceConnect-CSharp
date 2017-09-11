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
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Common.Logging;
using Newtonsoft.Json;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Core
{
    public class ProcessManagerProcessor : IProcessManagerProcessor
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(ProcessManagerProcessor));

        private readonly IProcessManagerFinder _processManagerFinder;
        private readonly IBusContainer _container;

        public ProcessManagerProcessor(IProcessManagerFinder processManagerFinder, IBusContainer container)
        {
            _processManagerFinder = processManagerFinder;
            _container = container;
        }

        public async Task ProcessMessage<T>(string message, IConsumeContext context) where T : Message
        {
            await StartProcessManagers<T>(message, context);
            await LoadExistingProcessManagers<T>(message, context);
        }

        private async Task StartProcessManagersBaseType<T, TB>(string message, IConsumeContext context) where T : Message where TB : Message
        {
            List<HandlerReference> processManagerInstances = _container.GetHandlerTypes(typeof(IStartProcessManager<TB>), typeof(IStartAsyncProcessManager<TB>)).ToList();

            await InitStartProcessManagerHandlers<T>(message, context, processManagerInstances, typeof(TB));
        }

        private async Task StartProcessManagers<T>(string message, IConsumeContext context, Type baseType = null) where T : Message
        {
            List<HandlerReference> processManagerInstances = _container.GetHandlerTypes(typeof(IStartProcessManager<T>), typeof(IStartAsyncProcessManager<T>)).ToList();

            await InitStartProcessManagerHandlers<T>(message, context, processManagerInstances, baseType);
        }

        private async Task InitStartProcessManagerHandlers<T>(string message, IConsumeContext context, IEnumerable<HandlerReference> processManagerInstances, Type baseType = null) where T : Message
        {
            Type msgType = baseType ?? typeof(T);

            foreach (HandlerReference processManagerInstance in processManagerInstances)
            {
                try
                {
                    var messageObject = JsonConvert.DeserializeObject(message, typeof (T));

                    // Create instance of the project manager
                    object processManager = _container.GetInstance(processManagerInstance.HandlerType);

                    // Set Process Manager Finder property
                    PropertyInfo processManagerFinderProp = processManagerInstance.HandlerType.GetProperty("ProcessManagerFinder");
                    processManagerFinderProp.SetValue(processManager, _processManagerFinder, null);

                    // Execute FindProcessManagerData - see if already exists
                    object persistanceData = processManagerInstance.HandlerType.GetMethod("FindProcessManagerData").Invoke(processManager, new[] { messageObject });

                    // Get Data Type
                    Type dataType = processManagerInstance.HandlerType.GetTypeInfo().BaseType.GetGenericArguments()[0];

                    bool processManagerAlreadyExists = true;
                    object data;

                    // Process Manager Data does not exist, create new instance 
                    if (null == persistanceData)
                    {
                        processManagerAlreadyExists = false;
                        data = (IProcessManagerData)Activator.CreateInstance(dataType);
                    }
                    else
                    {
                        // Get data from persistance data
                        Type persistanceType = typeof(IPersistanceData<>).MakeGenericType(dataType);
                        PropertyInfo dataProp = persistanceType.GetProperty("Data");
                        data = dataProp.GetValue(persistanceData);
                    }

                    // Set data on process manager
                    PropertyInfo prop = processManagerInstance.HandlerType.GetProperty("Data", dataType);
                    prop.SetValue(processManager, data, null);

                    // Set context property value
                    PropertyInfo contextProp = processManagerInstance.HandlerType.GetProperty("Context", typeof (IConsumeContext));
                    contextProp.SetValue(processManager, context, null);

                    // Execute process manager execute method
                    var result = processManagerInstance.HandlerType.GetMethod("Execute", new[] { msgType }).Invoke(processManager, new[] { messageObject });

                    if (result != null && result is Task handlerTask)
                    {
                        await handlerTask;
                    }
                    
                    // Persist data if does not exist
                    if (!processManagerAlreadyExists)
                    {
                        // Get data after execute has finished
                        data = (IProcessManagerData)prop.GetValue(processManager);

                        // Insert it
                        _processManagerFinder.InsertData((IProcessManagerData) data);
                    }
                    else
                    {
                        // Otherwise update
                        _processManagerFinder.GetType()
                            .GetMethod("UpdateData")
                            .MakeGenericMethod(dataType)
                            .Invoke(_processManagerFinder, new[] { persistanceData });
                    }
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
            Type newBaseType = msgType.GetTypeInfo().BaseType;
            if (newBaseType != null && newBaseType.Name != typeof(object).Name)
            {
                MethodInfo startProcessManagers = GetType().GetMethod("StartProcessManagersBaseType", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo genericStartProcessManagers = startProcessManagers.MakeGenericMethod(typeof (T), newBaseType);
                await (Task)genericStartProcessManagers.Invoke(this, new object[] {message, context});
            }
        }


        private async Task LoadExistingProcessManagersBaseType<T, TB>(string message, IConsumeContext context) where T : Message where TB : Message
        {
            IEnumerable<HandlerReference> handlerReferences = _container.GetHandlerTypes(typeof(IMessageHandler<TB>), typeof(IAsyncMessageHandler<TB>))
                                                                        .Where(h => h.HandlerType.GetTypeInfo().BaseType != null &&
                                                                                    h.HandlerType.GetTypeInfo().BaseType.Name == typeof(ProcessManager<>).Name);

            await InitLoadExistingProcessManagerHandlers<T>(message, context, handlerReferences, typeof(TB));
        }

        private async Task LoadExistingProcessManagers<T>(string message, IConsumeContext context, Type baseType = null) where T : Message
        {
            IEnumerable<HandlerReference> handlerReferences = _container.GetHandlerTypes(typeof(IMessageHandler<T>), typeof(IAsyncMessageHandler<T>))
                                                                        .Where(h => h.HandlerType.GetTypeInfo().BaseType != null &&
                                                                                    h.HandlerType.GetTypeInfo().BaseType.Name == typeof(ProcessManager<>).Name);

            await InitLoadExistingProcessManagerHandlers<T>(message, context, handlerReferences, baseType);
        }

        private async Task InitLoadExistingProcessManagerHandlers<T>(string message, IConsumeContext context, IEnumerable<HandlerReference> handlerReferences, Type baseType = null) where T : Message
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
                    Type dataType = handlerReference.HandlerType.GetTypeInfo().BaseType.GetGenericArguments()[0];

                    if (null == persistanceData)
                    {
                        Logger.Warn(string.Format("ProcessManagerData not found for {0}. message.CorrelationId = {1}", handlerReference.HandlerType, messageObject.CorrelationId));
                        continue;
                    }

                    // Get data from persistance data
                    Type persistanceType = typeof (IPersistanceData<>).MakeGenericType(dataType);
                    PropertyInfo dataProp = persistanceType.GetProperty("Data");
                    object data = dataProp.GetValue(persistanceData);

                    // Set data property value
                    PropertyInfo prop = handlerReference.HandlerType.GetProperty("Data", dataType);
                    prop.SetValue(processManager, data, null);

                    // Set context property value
                    PropertyInfo contextProp = handlerReference.HandlerType.GetProperty("Context", typeof (IConsumeContext));
                    contextProp.SetValue(processManager, context, null);

                    // ***Execute handler***
                    var result = handlerReference.HandlerType.GetMethod("Execute", new[] { msgType }).Invoke(processManager, new object[] { messageObject });

                    if (result != null && result is Task handlerTask)
                    {
                        await handlerTask;
                    }

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
            Type newBaseType = msgType.GetTypeInfo().BaseType;
            if (newBaseType != null && newBaseType.Name != typeof (object).Name)
            {
                MethodInfo loadExistingProcessManagers = GetType().GetMethod("LoadExistingProcessManagersBaseType", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo genericLoadExistingProcessManagers = loadExistingProcessManagers.MakeGenericMethod(typeof (T),newBaseType);
                await (Task)genericLoadExistingProcessManagers.Invoke(this, new object[] {message, context});
            }
        }
    }
}