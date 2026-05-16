using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Services;

/// <summary>
/// Hosted-service adapter that starts and stops bus consumption with the application host.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single-use lifecycle.</b> The underlying <see cref="IBus"/> permanently latches its
/// stopped flag after <see cref="StopAsync"/> completes. If the host or an orchestrator
/// recycles this service — calling <see cref="StartAsync"/> again on the same instance
/// without disposing the bus — <see cref="IBus.StartConsumingAsync"/> will throw
/// <see cref="InvalidOperationException"/>, which surfaces to the host as a startup failure.
/// </para>
/// <para>
/// The correct recovery path is to let the DI container dispose the bus (and this hosted
/// service) and resolve fresh instances for the new application lifetime. Do not attempt to
/// restart the same <see cref="IBus"/> instance after a stop.
/// </para>
/// </remarks>
internal sealed class BusHostedService(
    IBus bus,
    IBusConfiguration config,
    ITransportConfiguration transport,
    ILogger<BusHostedService> logger,
    IReadOnlyList<HandlerScanWarning>? scanWarnings = null) : IHostedService
{
    /// <summary>
    /// Starts the bus automatically when <see cref="IBusConfiguration.AutoStartConsuming"/> is enabled.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel host startup.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Replay handler-scan warnings captured before the logger was available.
        // A broken handler assembly that survives the scan with no warning shows up
        // only when a message arrives with no registered handler — surfacing the
        // partial-scan warning here turns silent under-discovery into a startup log.
        if (scanWarnings is { Count: > 0 })
        {
            foreach (var warning in scanWarnings)
            {
                logger.LogWarning(
                    warning.Exception,
                    "Assembly {AssemblyName} threw {ExceptionType} during handler scan: {Detail}",
                    warning.AssemblyName, warning.ExceptionType, warning.Detail);
            }
        }

        // Adapter-independent plaintext check: warn when TLS is off against a non-loopback
        // host so the safeguard survives adapter swaps. Docker Compose service names (e.g.
        // "rabbitmq") that resolve to an internal network address but aren't loopback will
        // fire here; set SuppressPlaintextWarning=true to silence intentional plaintext.
        ServiceConnectBuilder.WarnIfPlaintextOnNonLoopbackHost(transport, logger);

        if (!config.ValidateReplyDestinations)
        {
            logger.LogWarning(
                "ValidateReplyDestinations is disabled. Replies will not be verified against known queue mappings, " +
                "allowing spoofed SourceAddress headers to redirect replies. This is not recommended for production.");
        }

        if (!config.AutoStartConsuming)
        {
            logger.LogInformation("AutoStartConsuming is disabled.");
            return;
        }

        // Let exceptions propagate — the host should observe startup failures
        // rather than silently report success when consuming never started.
        await bus.StartConsumingAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Bus auto-started consuming.");
    }

    /// <summary>
    /// Stops bus consumption during host shutdown.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel host shutdown.</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await bus.StopConsumingAsync(cancellationToken).ConfigureAwait(false);
    }
}
