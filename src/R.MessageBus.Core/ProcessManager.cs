using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
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

        /// <summary>
        /// Configure mapper and finds process manager data using configured ProcessManagerFinder
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        public IPersistanceData<T> FindProcessManagerData(Message message)
        {
            // FindProcessManagerData is always called on new instance of ProcessManager<T>
            _mapper = new ProcessManagerPropertyMapper();

            ConfigureHowToFindProcessManager(_mapper);

            // Default mapping
            _mapper.ConfigureMapping<IProcessManagerData, Message>(m => m.CorrelationId, pm => pm.CorrelationId);

            return ProcessManagerFinder.FindData<T>(_mapper, message);
        }

        protected virtual void ConfigureHowToFindProcessManager(ProcessManagerPropertyMapper mapper)
        {}
    }
}
