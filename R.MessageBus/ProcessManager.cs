using R.MessageBus.Interfaces;

namespace R.MessageBus
{
    /// <summary>
    /// See ProccessManager (G. Hohpe, B. Woolf; Enterprise Integration Patterns)
    /// </summary>
    public abstract class ProcessManager<T> where T : class, IProcessManagerData
    {
        /// <summary>
        /// The ProcessManager's strongly typed data.
        /// </summary>
        public T Data
        {
            get { return VersionData.Data; }
            set { VersionData = new VersionData<T> { Data = value }; }
        }

        /// <summary>
        /// Decorates ProcessManager's strongly typed data with version.
        /// </summary>
        private VersionData<T> VersionData { get; set; }

        /// <summary>
        /// Use to locate/delete ProcessManager data in a persistant store 
        /// </summary>
        public IProcessManagerFinder ProcessManagerFinder { get; set; }

        /// <summary>
        /// Marks the ProcessManager as complete.
        /// This may result in the sagas state being deleted by the persister.
        /// </summary>
        protected virtual void MarkAsComplete()
        {
            ProcessManagerFinder.DeleteData(VersionData);
        }

        /// <summary>
        /// Provides default implementation for finding ProcessManager by message correlation id.
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        public VersionData<T> FindProcessManagerData(Message message)
        {
            return ProcessManagerFinder.FindData<T>(message.CorrelationId);
        }
    }
}
