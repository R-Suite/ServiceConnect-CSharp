using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

public sealed class PipelineConfiguration : IPipelineConfiguration
{
    public List<Type> BeforeConsumingFilters { get; } = [];
    public List<Type> AfterConsumingFilters { get; } = [];
    public List<Type> OutgoingFilters { get; } = [];
    public List<Type> MessageProcessingMiddleware { get; } = [];
    public List<Type> SendMessageMiddleware { get; } = [];

    // Explicit interface implementations to satisfy the IReadOnlyList<Type> contract
    IReadOnlyList<Type> IPipelineConfiguration.BeforeConsumingFilters => BeforeConsumingFilters;
    IReadOnlyList<Type> IPipelineConfiguration.AfterConsumingFilters => AfterConsumingFilters;
    IReadOnlyList<Type> IPipelineConfiguration.OutgoingFilters => OutgoingFilters;
    IReadOnlyList<Type> IPipelineConfiguration.MessageProcessingMiddleware => MessageProcessingMiddleware;
    IReadOnlyList<Type> IPipelineConfiguration.SendMessageMiddleware => SendMessageMiddleware;
}
