namespace ServiceConnect.Interfaces.Configuration;

public interface IPipelineConfiguration
{
    IReadOnlyList<Type> BeforeConsumingFilters { get; }
    IReadOnlyList<Type> AfterConsumingFilters { get; }
    IReadOnlyList<Type> OutgoingFilters { get; }
    IReadOnlyList<Type> MessageProcessingMiddleware { get; }
    IReadOnlyList<Type> SendMessageMiddleware { get; }
}
