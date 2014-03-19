using System;

namespace R.MessageBus.Interfaces
{
    public interface IProcessManagerData
    {
        Guid CorrelationId { get; set; }
    }
}
