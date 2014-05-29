﻿using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    /// <summary>
    /// See ProccessManager (G. Hohpe, B. Woolf; Enterprise Integration Patterns)
    /// </summary>
    public abstract class ProcessManager<T> where T : class, IProcessManagerData
    {
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

        /// <summary>
        /// Provides default implementation for finding ProcessManager by message correlation id.
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        public virtual IPersistanceData<T> FindProcessManagerData(Message message)
        {
            return ProcessManagerFinder.FindData<T>(message.CorrelationId);
        }
    }
}
