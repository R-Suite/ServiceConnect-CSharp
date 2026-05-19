using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.DependencyInjection;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using ServiceConnect.Persistence.MongoDb;

namespace ServiceConnect.Examples.StressHarness.Orchestrator;

/// <summary>
/// Owns two independent <see cref="IBus"/> instances under a single process — one prefixed
/// <c>stress-a.*</c>, the other prefixed <c>stress-b.*</c>. Each bus has its own
/// <see cref="IServiceProvider"/> because <c>AddServiceConnect</c> is single-bus per service
/// collection (it rejects a second call) so the only supported way to host two buses in one
/// process is two parallel service collections sharing only the logger factory.
/// </summary>
/// <remarks>
/// Pattern drivers (see <c>IPatternDriver</c>) register their per-bus handlers and ancillary
/// services through the <c>registerPerBus</c> callback supplied to <see cref="StartAsync"/>.
/// The callback receives both the framework's <see cref="ServiceConnectBuilder"/> and a
/// short bus tag (<c>"alpha"</c> / <c>"beta"</c>) so a single driver can register asymmetric
/// handler topologies (e.g. only the receiver bus binds to a contract type).
/// </remarks>
public sealed class HarnessHost : IAsyncDisposable
{
    /// <summary>Alpha bus — queues prefixed <c>stress-a</c>.</summary>
    public IBus Alpha { get; }

    /// <summary>Beta bus — queues prefixed <c>stress-b</c>.</summary>
    public IBus Beta { get; }

    /// <summary>Service provider scoping the alpha bus; resolves alpha-side handlers.</summary>
    public IServiceProvider AlphaServices { get; }

    /// <summary>Service provider scoping the beta bus; resolves beta-side handlers.</summary>
    public IServiceProvider BetaServices { get; }

    private HarnessHost(
        IBus alpha,
        ServiceProvider alphaServices,
        IBus beta,
        ServiceProvider betaServices)
    {
        Alpha = alpha;
        AlphaServices = alphaServices;
        Beta = beta;
        BetaServices = betaServices;
    }

    /// <summary>
    /// Builds both service providers, resolves the two <see cref="IBus"/> singletons, and
    /// starts each consumer. Throws if either bus fails to start; partially-started state
    /// is cleaned up before the exception propagates.
    /// </summary>
    /// <param name="options">Shared broker / persistence / reporting settings.</param>
    /// <param name="registerPerBus">
    /// Callback invoked once per bus during DI composition. Receives the framework
    /// <see cref="ServiceConnectBuilder"/> already preconfigured with transport, queues, and
    /// persistence, and a bus tag (<c>"alpha"</c> or <c>"beta"</c>) so the driver can
    /// register asymmetric topologies. Use <see cref="ServiceConnectBuilder.AddRegistration"/>
    /// inside the callback to register handlers and helper services onto the matching
    /// service collection.
    /// </param>
    /// <param name="loggerFactory">Logger factory shared across both service providers.</param>
    /// <param name="cancellationToken">
    /// Threaded into each <see cref="IBus.StartConsumingAsync"/> call so a slow broker
    /// handshake honours orchestrator-level cancellation.
    /// </param>
    public static async Task<HarnessHost> StartAsync(
        HarnessOptions options,
        Action<ServiceConnectBuilder, string> registerPerBus,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registerPerBus);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var alphaServices = BuildServices(options, loggerFactory, busTag: "alpha", queuePrefix: "stress-a", registerPerBus);
        ServiceProvider? betaServices = null;
        IBus? alpha = null;
        IBus? beta = null;
        try
        {
            betaServices = BuildServices(options, loggerFactory, busTag: "beta", queuePrefix: "stress-b", registerPerBus);

            alpha = alphaServices.GetRequiredService<IBus>();
            beta = betaServices.GetRequiredService<IBus>();

            await alpha.StartConsumingAsync(cancellationToken).ConfigureAwait(false);
            await beta.StartConsumingAsync(cancellationToken).ConfigureAwait(false);

            return new HarnessHost(alpha, alphaServices, beta, betaServices);
        }
        catch
        {
            // Roll back any work that completed before the throw so the caller does not
            // leak a half-started bus pair. Dispose the bus first (if it was constructed)
            // and then the owning provider, in reverse construction order. Swallow disposal
            // exceptions to surface the original failure to the caller.
            if (beta is not null)
            {
                try { await beta.DisposeAsync().ConfigureAwait(false); } catch { /* preserve original */ }
            }
            if (betaServices is not null)
            {
                try { await betaServices.DisposeAsync().ConfigureAwait(false); } catch { /* preserve original */ }
            }
            if (alpha is not null)
            {
                try { await alpha.DisposeAsync().ConfigureAwait(false); } catch { /* preserve original */ }
            }
            try { await alphaServices.DisposeAsync().ConfigureAwait(false); } catch { /* preserve original */ }
            throw;
        }
    }

    private static ServiceProvider BuildServices(
        HarnessOptions options,
        ILoggerFactory loggerFactory,
        string busTag,
        string queuePrefix,
        Action<ServiceConnectBuilder, string> registerPerBus)
    {
        var services = new ServiceCollection();

        // Share the harness-level logger factory so both buses log through the same sinks
        // and console formatter. AddLogging() registers ILogger<T> against the existing
        // factory rather than building a fresh one.
        services.AddSingleton(loggerFactory);
        services.AddLogging();

        // Drivers that don't register their own handler-reference list still need
        // IReadOnlyList<HandlerReference> resolvable — AddServiceConnect snapshots it into
        // the bus singleton. An empty default is harmless for send-only buses and is
        // overridden by any driver-side registration that comes later (last writer wins
        // for IReadOnlyList<T>; TryAdd inside AddServiceConnect respects a pre-existing
        // registration). Adding the singleton up-front avoids forcing every driver to
        // remember to do this themselves.
        services.AddSingleton<IReadOnlyList<HandlerReference>>([]);

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(transport =>
            {
                transport.Host = ExtractHost(options.BrokerUri);
                // Plaintext to local rabbit; production deployments would flip SslEnabled
                // and configure credentials. Suppress the framework's non-loopback plaintext
                // warning when the broker URI points off-host so harness logs aren't spammed.
                transport.SslEnabled = false;
            });

            builder.ConfigureQueues(queues =>
            {
                queues.QueueName = $"{queuePrefix}.work";
                queues.ErrorQueueName = $"{queuePrefix}.errors";
                queues.AuditQueueName = $"{queuePrefix}.audit";
            });

            if (string.Equals(options.PersistenceMode, "mongo", StringComparison.OrdinalIgnoreCase))
            {
                var connectionString = options.MongoConnectionString
                    ?? throw new InvalidOperationException(
                        "HarnessOptions.MongoConnectionString must be set when PersistenceMode is 'mongo'.");

                builder.UseMongoDbPersistence(persistence =>
                {
                    persistence.ConnectionString = connectionString;
                    // Distinct database per bus so saga / aggregator / timeout state from the
                    // two buses can't accidentally collide on shared collections.
                    persistence.DatabaseName = $"stress_{busTag}";
                });
            }
            else
            {
                builder.UseInMemoryPersistence();
            }

            builder.ConfigureBus(bus =>
            {
                // Drivers register their handlers explicitly through registerPerBus; no
                // AppDomain-wide scan is desirable in a multi-bus host because both buses
                // would otherwise bind to every handler type loaded into the process.
                bus.ScanForMessageHandlers = false;
            });

            registerPerBus(builder, busTag);
        });

        return services.BuildServiceProvider();
    }

    // Accepts both forms: a bare host ("localhost", "rabbit.internal") and an AMQP-form
    // URI ("amqp://host:5672"). The framework's transport.Host expects a host string
    // (or comma-separated host list); only the authority host is forwarded — the port
    // and scheme components of a URI are not honoured here. Operators that need a
    // non-default port set it via RabbitMqOptions.Port or SetClientSetting separately.
    private static string ExtractHost(string brokerUri)
    {
        if (string.IsNullOrWhiteSpace(brokerUri))
        {
            throw new ArgumentException("HarnessOptions.BrokerUri must be a non-empty host or AMQP URI.", nameof(brokerUri));
        }

        if (Uri.TryCreate(brokerUri, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            return uri.Host;
        }

        // Bare host form — return as-is. Validation of host-name characters happens at
        // the framework's ConfigureTransport call site.
        return brokerUri;
    }

    /// <summary>
    /// Disposes both buses then both service providers, swallowing per-step exceptions so
    /// a wedged bus on one side doesn't prevent teardown of the other. Idempotent re-entry
    /// is provided by <c>ServiceProvider.DisposeAsync</c> and <c>Bus.DisposeAsync</c>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Buses first so consumer pumps stop before the provider yanks the underlying
        // hosted-service registrations and transport singletons out from under them.
        await SafeDisposeAsync(Alpha).ConfigureAwait(false);
        await SafeDisposeAsync(Beta).ConfigureAwait(false);
        await SafeDisposeAsync(AlphaServices).ConfigureAwait(false);
        await SafeDisposeAsync(BetaServices).ConfigureAwait(false);
    }

    private static async ValueTask SafeDisposeAsync(object target)
    {
        try
        {
            switch (target)
            {
                case IAsyncDisposable async:
                    await async.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable sync:
                    sync.Dispose();
                    break;
            }
        }
        catch
        {
            // Best-effort teardown: one wedged side must not block the other from
            // releasing its resources. The orchestrator surfaces any prior failure via
            // its own assertion-failure / flow-result reporting.
        }
    }
}
