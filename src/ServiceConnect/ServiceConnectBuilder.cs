using System.Reflection;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect;

/// <summary>
/// Fluent builder used to configure ServiceConnect registrations before adding them to a service collection.
/// </summary>
public sealed class ServiceConnectBuilder
{
    internal BusConfiguration BusConfig { get; } = new();
    private readonly List<Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>> _additionalRegistrations = [];
    /// <summary>
    /// Gets additional service registrations that will be applied after core ServiceConnect services are registered.
    /// </summary>
    public IReadOnlyList<Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>> AdditionalRegistrations => _additionalRegistrations;

    /// <summary>
    /// Assemblies to scan for message handlers. Populated explicitly via
    /// <see cref="ScanAssemblies"/>; when empty and <see cref="IBusConfiguration.ScanForMessageHandlers"/>
    /// is true, falls back to <see cref="AppDomain.CurrentDomain"/> assemblies. Explicit
    /// registration is preferred because it is deterministic and testable.
    /// </summary>
    internal List<Assembly> ScanAssembliesList { get; } = [];

    /// <summary>
    /// Registers assemblies to scan for message handlers instead of relying on all currently loaded assemblies.
    /// </summary>
    /// <param name="assemblies">The assemblies that contain handlers.</param>
    /// <returns>The current builder instance.</returns>
    public ServiceConnectBuilder ScanAssemblies(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        for (int i = 0; i < assemblies.Length; i++)
        {
            if (assemblies[i] is null)
                throw new ArgumentNullException($"{nameof(assemblies)}[{i}]", "Assembly array element is null.");
        }
        ScanAssembliesList.AddRange(assemblies);
        return this;
    }

    /// <summary>
    /// Adds a custom service-registration callback to run during <c>AddServiceConnect</c>.
    /// </summary>
    /// <param name="registration">The callback that registers additional services.</param>
    /// <returns>The current builder instance.</returns>
    public ServiceConnectBuilder AddRegistration(Action<Microsoft.Extensions.DependencyInjection.IServiceCollection> registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _additionalRegistrations.Add(registration);
        return this;
    }

    /// <summary>
    /// Configures transport settings such as host, retry, and TLS behavior.
    /// </summary>
    /// <param name="configure">The callback that mutates the transport configuration.</param>
    /// <returns>The current builder instance.</returns>
    public ServiceConnectBuilder ConfigureTransport(Action<ITransportConfiguration> configure)
    {
        configure(BusConfig.Transport);
        ValidateTransport(BusConfig.Transport);
        return this;
    }

    // Guard against silently-broken configuration at startup. Values that would
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

    /// <summary>
    /// Configures queue names and explicit message routing mappings.
    /// </summary>
    /// <param name="configure">The callback that mutates queue settings.</param>
    /// <returns>The current builder instance.</returns>
    public ServiceConnectBuilder ConfigureQueues(Action<IQueueConfiguration> configure)
    {
        configure(BusConfig.Queues);
        return this;
    }

    /// <summary>
    /// Configures persistence settings used by process managers, aggregators, and timeout storage.
    /// </summary>
    /// <param name="configure">The callback that mutates persistence settings.</param>
    /// <returns>The current builder instance.</returns>
    public ServiceConnectBuilder ConfigurePersistence(Action<IPersistenceConfiguration> configure)
    {
        configure(BusConfig.Persistence);
        return this;
    }

    /// <summary>
    /// Configures pipeline filters and middleware registrations.
    /// </summary>
    /// <param name="configure">The callback that mutates pipeline settings.</param>
    /// <returns>The current builder instance.</returns>
    public ServiceConnectBuilder ConfigurePipeline(Action<PipelineConfiguration> configure)
    {
        configure(BusConfig.Pipeline);
        return this;
    }

    /// <summary>
    /// Configures bus-wide runtime behavior such as handler discovery and automatic startup.
    /// </summary>
    /// <param name="configure">The callback that mutates bus settings.</param>
    /// <returns>The current builder instance.</returns>
    public ServiceConnectBuilder ConfigureBus(Action<IBusConfiguration> configure)
    {
        configure(BusConfig);
        return this;
    }

    /// <summary>
    /// Adds an outgoing filter that runs before messages are sent or published.
    /// </summary>
    /// <typeparam name="T">The filter type.</typeparam>
    /// <returns>The current builder instance.</returns>
    public ServiceConnectBuilder AddOutgoingFilter<T>() where T : class, IFilter
    {
        BusConfig.Pipeline.OutgoingFilters.Add(typeof(T));
        return this;
    }

    /// <summary>
    /// Adds a filter that runs before an incoming message reaches handlers.
    /// </summary>
    /// <typeparam name="T">The filter type.</typeparam>
    /// <returns>The current builder instance.</returns>
    public ServiceConnectBuilder AddBeforeConsumingFilter<T>() where T : class, IFilter
    {
        BusConfig.Pipeline.BeforeConsumingFilters.Add(typeof(T));
        return this;
    }

    /// <summary>
    /// Adds a filter that runs after an incoming message has been processed.
    /// </summary>
    /// <typeparam name="T">The filter type.</typeparam>
    /// <returns>The current builder instance.</returns>
    public ServiceConnectBuilder AddAfterConsumingFilter<T>() where T : class, IFilter
    {
        BusConfig.Pipeline.AfterConsumingFilters.Add(typeof(T));
        return this;
    }

    /// <summary>
    /// Adds middleware that wraps outgoing send and publish operations.
    /// </summary>
    /// <typeparam name="T">The middleware type.</typeparam>
    /// <returns>The current builder instance.</returns>
    public ServiceConnectBuilder AddSendMessageMiddleware<T>() where T : class, ISendMessageMiddleware
    {
        BusConfig.Pipeline.SendMessageMiddleware.Add(typeof(T));
        return this;
    }

    /// <summary>
    /// Adds middleware that wraps incoming message processing.
    /// </summary>
    /// <typeparam name="T">The middleware type.</typeparam>
    /// <returns>The current builder instance.</returns>
    public ServiceConnectBuilder AddMessageProcessingMiddleware<T>() where T : class, IMessageProcessingMiddleware
    {
        BusConfig.Pipeline.MessageProcessingMiddleware.Add(typeof(T));
        return this;
    }
}
