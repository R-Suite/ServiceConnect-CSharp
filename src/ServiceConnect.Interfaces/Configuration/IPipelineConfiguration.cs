namespace ServiceConnect.Interfaces.Configuration;

public interface IPipelineConfiguration
{
    IList<Type> BeforeConsumingFilters { get; }
    IList<Type> AfterConsumingFilters { get; }
    IList<Type> OutgoingFilters { get; }
    IList<Type> MessageProcessingMiddleware { get; }
    IList<Type> SendMessageMiddleware { get; }
}
