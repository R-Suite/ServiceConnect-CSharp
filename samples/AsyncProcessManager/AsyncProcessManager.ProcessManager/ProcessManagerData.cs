using System;
using ServiceConnect.Interfaces;

namespace AsyncProcessManager.ProcessManager
{
    public class ProcessManagerData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
    }
}