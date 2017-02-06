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
using System.ComponentModel;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Core
{
    /// <summary>
    /// See ProccessManager (G. Hohpe, B. Woolf; Enterprise Integration Patterns)
    /// </summary>
    public abstract class ProcessManager<T> where T : class, IProcessManagerData
    {
        private ProcessManagerPropertyMapper _mapper;

        public IConsumeContext Context { get; set; }

        /// <summary>
        /// The ProcessManager's strongly typed data.
        /// </summary>
        public T Data { get; set; }

        /// <summary>
        /// Use to locate/delete ProcessManager data in a persistant store 
        /// </summary>
        public IProcessManagerFinder ProcessManagerFinder { get; set; }

        /// <summary>
        /// Marks the ProcessManager as complete.
        /// </summary>
        protected virtual void MarkAsComplete()
        {
            Complete = true;
        }

        public bool Complete { get; set; }

        protected virtual void RequestTimeout(TimeSpan timeout)
        {
            var timeoutData = new TimeoutData
            {
                Destination = Context.Headers["DestinationAddress"].ToString(),
                ProcessManagerId = Data.CorrelationId,
                Headers = Context.Headers,
                Id = Guid.NewGuid(),
                Time = DateTime.UtcNow.Add(timeout)
            };

            ProcessManagerFinder.InsertTimeout(timeoutData);
        }

        /// <summary>
        /// Configure mapper and finds process manager data using configured ProcessManagerFinder
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        public virtual IPersistanceData<T> FindProcessManagerData(Message message)
        {
            // FindProcessManagerData is always called on new instance of ProcessManager<T>
            _mapper = new ProcessManagerPropertyMapper();

            ConfigureHowToFindProcessManager(_mapper);

            // Default mapping
            _mapper.ConfigureMapping<IProcessManagerData, Message>(m => m.CorrelationId, pm => pm.CorrelationId);

            return ProcessManagerFinder.FindData<T>(_mapper, message);
        }
        
        protected virtual void ConfigureHowToFindProcessManager(IProcessManagerPropertyMapper mapper)
        {}
    }
}
