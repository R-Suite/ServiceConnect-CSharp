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
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ServiceConnect.Interfaces;
using System.Reflection;

namespace ServiceConnect.Core
{
    public class RequestConfiguration : IRequestConfiguration
    {
        private readonly Guid _requestMessageId;
        private Task _task;
        private Action<object> _action;
        public Guid RequestMessageId
        {
            get { return _requestMessageId; }
        }

        public int EndpointsCount { get; set; }
        public int ProcessedCount { get; set; }

        public RequestConfiguration(Guid requestMessageId)
        {
            _requestMessageId = requestMessageId;
        }

        public Task SetHandler(Action<object> handler)
        {
            _task = new Task(() => {});
            _action = handler;

            return _task;
        }

        public void ProcessMessage(string message, string type)
        {
#if NETSTANDARD1_6
            var assemblies = Microsoft.Extensions.DependencyModel.DependencyContext.Default.RuntimeLibraries;

            Type typeObject = null;
            foreach(var assembly in assemblies)
            {
                try 
                {          
                    var ass = Assembly.Load(new AssemblyName(assembly.Name));
                    typeObject = ass.GetType(type);

                    if (null != typeObject)
                        break;
                    }
                catch (Exception)
                {}
            }
#else
            var typeObject = Type.GetType(type) ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(type)).FirstOrDefault(t => t != null);
#endif

            var messageObject = JsonConvert.DeserializeObject(message, typeObject);

            ProcessedCount++;

            _action(messageObject);
            
            if (EndpointsCount == ProcessedCount)
            {
                _task.Start();
            }
        }
    }
}
