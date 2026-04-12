using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

public sealed class PipelineConfiguration : IPipelineConfiguration
{
    public IList<Type> BeforeConsumingFilters { get; } = new List<Type>();
    public IList<Type> AfterConsumingFilters { get; } = new List<Type>();
    public IList<Type> OutgoingFilters { get; } = new List<Type>();
    public IList<Type> MessageProcessingMiddleware { get; } = new List<Type>();
    public IList<Type> SendMessageMiddleware { get; } = new List<Type>();
}
