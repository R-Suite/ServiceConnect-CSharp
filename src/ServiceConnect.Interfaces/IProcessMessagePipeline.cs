using ServiceConnect.Interfaces;
using System;
using System.Threading.Tasks;

namespace ServiceConnect.Interfaces
{
    public interface IProcessMessagePipeline
    {
        Task ExecutePipeline(IConsumeContext context, Type typeObject, Envelope envelope);
    }
}