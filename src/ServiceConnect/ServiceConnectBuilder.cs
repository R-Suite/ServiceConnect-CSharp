using System.Reflection;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect;

public sealed class ServiceConnectBuilder
{
    internal BusConfiguration BusConfig { get; } = new();
    public List<Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>> AdditionalRegistrations { get; } = [];

    /// <summary>
    /// Assemblies to scan for message handlers. Populated explicitly via
    /// <see cref="ScanAssemblies"/>; when empty and <see cref="IBusConfiguration.ScanForMessageHandlers"/>
    /// is true, falls back to <see cref="AppDomain.CurrentDomain"/> assemblies. Explicit
    /// registration is preferred because it is deterministic and testable (A-11).
    /// </summary>
    internal List<Assembly> ScanAssembliesList { get; } = [];

    public ServiceConnectBuilder ScanAssemblies(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ScanAssembliesList.AddRange(assemblies);
        return this;
    }

    public ServiceConnectBuilder ConfigureTransport(Action<ITransportConfiguration> configure)
    {
        configure(BusConfig.Transport);
        ValidateTransport(BusConfig.Transport);
        return this;
    }

    // Guard against silently-broken configuration at startup (G-07). Values that would
    // cause confusing runtime errors are rejected with a message pointing at the
    // misconfigured property.
    private static void ValidateTransport(ITransportConfiguration transport)
    {
        if (string.IsNullOrWhiteSpace(transport.Host))
            throw new InvalidOperationException("TransportConfiguration.Host must be a non-empty host or comma-separated host list.");
        if (transport.RetryDelay < 0)
            throw new InvalidOperationException($"TransportConfiguration.RetryDelay must be non-negative (got {transport.RetryDelay}).");
        if (transport.MaxRetries < 0)
            throw new InvalidOperationException($"TransportConfiguration.MaxRetries must be non-negative (got {transport.MaxRetries}).");
        if (transport.GracefulShutdownTimeoutMilliseconds < 0)
            throw new InvalidOperationException($"TransportConfiguration.GracefulShutdownTimeoutMilliseconds must be non-negative (got {transport.GracefulShutdownTimeoutMilliseconds}).");
    }

    public ServiceConnectBuilder ConfigureQueues(Action<IQueueConfiguration> configure)
    {
        configure(BusConfig.Queues);
        return this;
    }

    public ServiceConnectBuilder ConfigurePersistence(Action<IPersistenceConfiguration> configure)
    {
        configure(BusConfig.Persistence);
        return this;
    }

    public ServiceConnectBuilder ConfigurePipeline(Action<PipelineConfiguration> configure)
    {
        configure(BusConfig.Pipeline);
        return this;
    }

    public ServiceConnectBuilder ConfigureBus(Action<IBusConfiguration> configure)
    {
        configure(BusConfig);
        return this;
    }

    public ServiceConnectBuilder AddOutgoingFilter<T>() where T : class, IFilter
    {
        BusConfig.Pipeline.OutgoingFilters.Add(typeof(T));
        return this;
    }

    public ServiceConnectBuilder AddBeforeConsumingFilter<T>() where T : class, IFilter
    {
        BusConfig.Pipeline.BeforeConsumingFilters.Add(typeof(T));
        return this;
    }

    public ServiceConnectBuilder AddAfterConsumingFilter<T>() where T : class, IFilter
    {
        BusConfig.Pipeline.AfterConsumingFilters.Add(typeof(T));
        return this;
    }

    public ServiceConnectBuilder AddSendMessageMiddleware<T>() where T : class, ISendMessageMiddleware
    {
        BusConfig.Pipeline.SendMessageMiddleware.Add(typeof(T));
        return this;
    }

    public ServiceConnectBuilder AddMessageProcessingMiddleware<T>() where T : class, IMessageProcessingMiddleware
    {
        BusConfig.Pipeline.MessageProcessingMiddleware.Add(typeof(T));
        return this;
    }
}
