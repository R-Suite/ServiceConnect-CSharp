using System;
using System.Threading.Tasks;

namespace ServiceConnect.Interfaces
{
    public delegate Task ProcessMessageDelegate(IConsumeContext context, Type typeObject, Envelope envelope);

    public interface IBusMiddleware
    {
        ProcessMessageDelegate Next { get; set; }

        Task Process(IConsumeContext context, Type typeObject, Envelope envelope);
    }
}
