using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging;
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
            {
                throw new ArgumentNullException($"{nameof(assemblies)}[{i}]", "Assembly array element is null.");
            }
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
        {
            throw new InvalidOperationException("TransportConfiguration.Host must be a non-empty host or comma-separated host list.");
        }

        if (transport.RetryDelay < 0)
        {
            throw new InvalidOperationException($"TransportConfiguration.RetryDelay must be non-negative (got {transport.RetryDelay}).");
        }

        if (transport.MaxRetries < 0)
        {
            throw new InvalidOperationException($"TransportConfiguration.MaxRetries must be non-negative (got {transport.MaxRetries}).");
        }

        if (transport.GracefulShutdownTimeoutMilliseconds < 0)
        {
            throw new InvalidOperationException($"TransportConfiguration.GracefulShutdownTimeoutMilliseconds must be non-negative (got {transport.GracefulShutdownTimeoutMilliseconds}).");
        }
    }

    /// <summary>
    /// Emits a warning via <paramref name="logger"/> when TLS is disabled against a
    /// non-loopback host and <see cref="ITransportConfiguration.SuppressPlaintextWarning"/>
    /// is not set. Called at host startup so the configured <c>ILogger</c> is available.
    /// </summary>
    /// <remarks>
    /// Loopback recognition covers: <c>"localhost"</c> (case-insensitive), IPv4 loopback
    /// (127.x.x.x — via <see cref="IPAddress.IsLoopback"/>), IPv6 loopback (<c>::1</c>),
    /// and the bracket form <c>[::1]</c> used in some URI host strings.
    /// One warning per call regardless of how many non-loopback entries a cluster host list contains.
    /// </remarks>
    internal static void WarnIfPlaintextOnNonLoopbackHost(ITransportConfiguration transport, ILogger logger)
    {
        if (transport.SslEnabled || transport.SuppressPlaintextWarning || string.IsNullOrEmpty(transport.Host))
        {
            return;
        }

        foreach (var entry in transport.Host.Split(','))
        {
            var trimmed = entry.Trim();
            if (trimmed.Length == 0 || IsLoopbackHost(trimmed))
            {
                continue;
            }
            ServiceConnectLog.PlaintextOnNonLoopbackHost(logger, trimmed);
            return; // one warning per call regardless of how many non-loopback entries
        }
    }

    // Recognises the standard loopback forms a transport host might carry:
    //   - "localhost" (DNS name, case-insensitive)
    //   - IPv4 loopback 127.x.x.x (covered by IPAddress.IsLoopback)
    //   - IPv6 loopback ::1 (covered by IPAddress.IsLoopback)
    //   - Bracket-wrapped IPv6 [::1] used in URI host components — strip brackets before parse
    private static bool IsLoopbackHost(string host)
    {
        // Strip bracket wrapping before IP parse so "[::1]" resolves correctly.
        var candidate = host.Length >= 2 && host[0] == '[' && host[^1] == ']'
            ? host[1..^1]
            : host;

        if (IPAddress.TryParse(candidate, out var addr))
        {
            return IPAddress.IsLoopback(addr);
        }
        return string.Equals(candidate, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Configures queue names and explicit message routing mappings.
    /// </summary>
    /// <param name="configure">The callback that mutates queue settings.</param>
    /// <returns>The current builder instance.</returns>
    public ServiceConnectBuilder ConfigureQueues(Action<IQueueConfiguration> configure)
    {
        configure(BusConfig.Queues);
        ValidateQueues(BusConfig.Queues);
        return this;
    }

    // Empty/whitespace QueueName produces an opaque AMQP error at broker-connect time.
    // Catching it here surfaces an actionable message at startup.
    internal static void ValidateQueues(IQueueConfiguration queues)
    {
        if (string.IsNullOrWhiteSpace(queues.QueueName))
        {
            throw new InvalidOperationException(
                "QueueConfiguration.QueueName must be a non-empty, non-whitespace string.");
        }
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
    /// Internal pipeline-configuration hook used by the framework's own builder extensions
    /// (e.g. <c>AddTelemetry</c>) that need to mutate the middleware lists directly. Public
    /// callers should use the strongly-typed <see cref="AddOutgoingFilter{T}"/> /
    /// <see cref="AddBeforeConsumingFilter{T}"/> / <see cref="AddSendMessageMiddleware{T}"/>
    /// (etc.) entry points instead, which insulate consumers from internal refactors of
    /// the pipeline-configuration shape.
    /// </summary>
    /// <param name="configure">The callback that mutates pipeline settings.</param>
    /// <returns>The current builder instance.</returns>
    internal ServiceConnectBuilder ConfigurePipeline(Action<PipelineConfiguration> configure)
    {
        configure(BusConfig.Pipeline);
        return this;
    }

    /// <summary>
    /// Registers middleware that wraps outgoing send and publish operations at the
    /// outermost position — runs first on the way out, last on the way back. Use for
    /// cross-cutting concerns that need to bracket every other middleware (tracing,
    /// metrics). De-duplicates by middleware type: a repeat call with the same
    /// <typeparamref name="T"/> is a no-op rather than producing two registrations.
    /// </summary>
    /// <typeparam name="T">The middleware type.</typeparam>
    /// <returns>The current builder instance.</returns>
    public ServiceConnectBuilder InsertSendMessageMiddlewareOutermost<T>() where T : class, ISendMessageMiddleware
    {
        var list = BusConfig.Pipeline.SendMessageMiddleware;
        if (!list.Contains(typeof(T)))
        {
            list.Insert(0, typeof(T));
        }
        return this;
    }

    /// <summary>
    /// Registers middleware that wraps incoming message processing at the outermost
    /// position — runs first on the way in, last on the way out. Use for cross-cutting
    /// concerns that need to bracket every other middleware (tracing, metrics).
    /// De-duplicates by middleware type: a repeat call with the same
    /// <typeparamref name="T"/> is a no-op rather than producing two registrations.
    /// </summary>
    /// <typeparam name="T">The middleware type.</typeparam>
    /// <returns>The current builder instance.</returns>
    public ServiceConnectBuilder InsertMessageProcessingMiddlewareOutermost<T>() where T : class, IMessageProcessingMiddleware
    {
        var list = BusConfig.Pipeline.MessageProcessingMiddleware;
        if (!list.Contains(typeof(T)))
        {
            list.Insert(0, typeof(T));
        }
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
        ValidateBus(BusConfig);
        return this;
    }

    // Guard against silently-broken bus configuration at startup. A ConsumerCount below 1
    // causes the client-construction loop in Consumer.StartConsumingAsync to be skipped
    // entirely, leaving the bus reporting IsConsuming=true while dispatching nothing.
    // Task.WaitAsync / SemaphoreSlim.WaitAsync / PeriodicTimer all reject TimeSpan values
    // greater than uint.MaxValue ms (~49.7 days). Any user-supplied timeout configured
    // beyond that range — even TimeSpan.MaxValue, which a "wait forever" intent might
    // suggest — produces an ArgumentOutOfRangeException at the framework's first await,
    // long after startup, with no operator-actionable signal. The cap below catches the
    // misconfiguration at startup. Use Timeout.InfiniteTimeSpan when the intent is
    // truly "wait indefinitely" — the BCL's APIs have explicit support for it.
    private static readonly TimeSpan MaxAcceptedTimeSpan = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private static void ValidateBus(IBusConfiguration bus)
    {
        if (bus.ConsumerCount < 1)
        {
            throw new InvalidOperationException(
                $"BusConfiguration.ConsumerCount must be at least 1 (got {bus.ConsumerCount}).");
        }

        // DisposeTimeout flows into Task.WaitAsync / SemaphoreSlim.WaitAsync which throw
        // ArgumentOutOfRangeException for any negative value other than Timeout.InfiniteTimeSpan,
        // and for any TimeSpan greater than uint.MaxValue ms. Catch the misconfiguration at
        // startup rather than at host teardown where the AOORE escapes
        // ProcessManagerTimeoutService.DisposeAsync's narrower catch.
        if (bus.DisposeTimeout != Timeout.InfiniteTimeSpan && bus.DisposeTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"BusConfiguration.DisposeTimeout must be positive or Timeout.InfiniteTimeSpan (got {bus.DisposeTimeout}).");
        }
        if (bus.DisposeTimeout != Timeout.InfiniteTimeSpan && bus.DisposeTimeout > MaxAcceptedTimeSpan)
        {
            throw new InvalidOperationException(
                $"BusConfiguration.DisposeTimeout must be at most {MaxAcceptedTimeSpan} (uint.MaxValue ms); " +
                $"got {bus.DisposeTimeout}. Use Timeout.InfiniteTimeSpan if you want to wait indefinitely.");
        }

        // ProcessManagerTimeoutPollInterval is consumed by `new PeriodicTimer(interval, …)`
        // which rejects values > int.MaxValue ms. Without this check, a TimeSpan.MaxValue
        // (or any > ~24.8 days) silently faults the polling task at startup and the host
        // comes up "started" but never polls.
        if (bus.ProcessManagerTimeoutPollInterval <= TimeSpan.Zero ||
            bus.ProcessManagerTimeoutPollInterval > TimeSpan.FromMilliseconds(int.MaxValue))
        {
            throw new InvalidOperationException(
                $"BusConfiguration.ProcessManagerTimeoutPollInterval must be positive and at most " +
                $"{TimeSpan.FromMilliseconds(int.MaxValue)} (int.MaxValue ms); got {bus.ProcessManagerTimeoutPollInterval}.");
        }

        // MaxRoutingSlipHops <= 0 silently disables routing-slip forwarding
        // (HandlerProcessor short-circuits at the limit check). Either reject or document;
        // we reject at startup so the misconfig surfaces with a clear remediation.
        if (bus.MaxRoutingSlipHops <= 0)
        {
            throw new InvalidOperationException(
                $"BusConfiguration.MaxRoutingSlipHops must be at least 1 (got {bus.MaxRoutingSlipHops}). " +
                "Routing-slip processing is disabled via BusConfiguration.EnableRoutingSlipProcessing=false, " +
                "not by setting MaxRoutingSlipHops to zero or negative.");
        }
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
    /// Adds a filter that runs only after a successful handler invocation
    /// (the dispatcher chain returned <c>Success = true</c> and
    /// <c>NotHandled = false</c>). Failures and unhandled messages skip this stage.
    /// Use for at-most-once side effects that depend on the handler having
    /// completed — e.g. recording a deduplication key, publishing an audit
    /// event, writing to an outbox.
    /// </summary>
    /// <typeparam name="T">The filter type.</typeparam>
    /// <returns>The current builder instance.</returns>
    public ServiceConnectBuilder AddOnConsumedSuccessfullyFilter<T>() where T : class, IFilter
    {
        BusConfig.Pipeline.OnConsumedSuccessfullyFilters.Add(typeof(T));
        return this;
    }

    /// <summary>
    /// Appends middleware that wraps outgoing send and publish operations. De-duplicates
    /// by middleware type: a repeat call with the same <typeparamref name="T"/> is a no-op
    /// rather than producing two registrations — matches the dedup semantics of
    /// <see cref="InsertSendMessageMiddlewareOutermost{T}"/> so two feature modules each
    /// calling the framework's own builder extensions can't accidentally wrap the pipeline
    /// twice (which would otherwise double-emit telemetry spans and overwrite the outer
    /// <c>traceparent</c> with the inner span's context).
    /// </summary>
    /// <typeparam name="T">The middleware type.</typeparam>
    /// <returns>The current builder instance.</returns>
    public ServiceConnectBuilder AddSendMessageMiddleware<T>() where T : class, ISendMessageMiddleware
    {
        var list = BusConfig.Pipeline.SendMessageMiddleware;
        if (!list.Contains(typeof(T)))
        {
            list.Add(typeof(T));
        }
        return this;
    }

    /// <summary>
    /// Appends middleware that wraps incoming message processing. De-duplicates by
    /// middleware type — see <see cref="AddSendMessageMiddleware{T}"/> for the rationale.
    /// </summary>
    /// <typeparam name="T">The middleware type.</typeparam>
    /// <returns>The current builder instance.</returns>
    public ServiceConnectBuilder AddMessageProcessingMiddleware<T>() where T : class, IMessageProcessingMiddleware
    {
        var list = BusConfig.Pipeline.MessageProcessingMiddleware;
        if (!list.Contains(typeof(T)))
        {
            list.Add(typeof(T));
        }
        return this;
    }
}
