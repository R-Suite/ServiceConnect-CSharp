using System.Runtime.CompilerServices;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

/// <summary>
/// Mutable implementation of <see cref="IBusConfiguration"/> used during application startup.
/// </summary>
/// <remarks>
/// The configuration is frozen by <c>AddServiceConnect</c> at the end of the configure callback,
/// so callers that resolve <see cref="IBusConfiguration"/> from DI cannot mutate <c>DisposeTimeout</c>,
/// <c>MaxRoutingSlipHops</c>, or other top-level fields and bypass the builder validators that
/// guard runtime invariants. Sub-configurations (Transport, Queues, Persistence, Pipeline) are
/// not yet frozen — registering them post-freeze remains the responsibility of those types.
/// </remarks>
internal sealed class BusConfiguration : IBusConfiguration
{
    private bool _frozen;
    private bool _scanForMessageHandlers = true;
    private bool _autoStartConsuming = true;
    private bool _enableProcessManagerTimeouts;
    private TimeSpan _processManagerTimeoutPollInterval = TimeSpan.FromSeconds(30);
    private int _consumerCount = 1;
    private Func<Exception, CancellationToken, ValueTask>? _exceptionHandler;
    private bool _includeMachineNameInHeaders;
    private bool _validateReplyDestinations = true;
    private bool _enableRoutingSlipProcessing = true;
    private int _maxRoutingSlipHops = 32;
    private bool _deadLetterUnhandledMessages;
    private bool _strictReplyValidation;
    private TimeSpan _disposeTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public bool ScanForMessageHandlers { get => _scanForMessageHandlers; set { ThrowIfFrozen(); _scanForMessageHandlers = value; } }
    /// <inheritdoc />
    public bool AutoStartConsuming { get => _autoStartConsuming; set { ThrowIfFrozen(); _autoStartConsuming = value; } }
    /// <inheritdoc />
    public bool EnableProcessManagerTimeouts { get => _enableProcessManagerTimeouts; set { ThrowIfFrozen(); _enableProcessManagerTimeouts = value; } }
    /// <inheritdoc />
    public TimeSpan ProcessManagerTimeoutPollInterval { get => _processManagerTimeoutPollInterval; set { ThrowIfFrozen(); _processManagerTimeoutPollInterval = value; } }
    /// <inheritdoc />
    public int ConsumerCount { get => _consumerCount; set { ThrowIfFrozen(); _consumerCount = value; } }
    /// <inheritdoc />
    public Func<Exception, CancellationToken, ValueTask>? ExceptionHandler { get => _exceptionHandler; set { ThrowIfFrozen(); _exceptionHandler = value; } }
    /// <inheritdoc />
    public bool IncludeMachineNameInHeaders { get => _includeMachineNameInHeaders; set { ThrowIfFrozen(); _includeMachineNameInHeaders = value; } }
    /// <inheritdoc />
    public bool ValidateReplyDestinations { get => _validateReplyDestinations; set { ThrowIfFrozen(); _validateReplyDestinations = value; } }
    /// <inheritdoc />
    public bool EnableRoutingSlipProcessing { get => _enableRoutingSlipProcessing; set { ThrowIfFrozen(); _enableRoutingSlipProcessing = value; } }
    /// <inheritdoc />
    public int MaxRoutingSlipHops { get => _maxRoutingSlipHops; set { ThrowIfFrozen(); _maxRoutingSlipHops = value; } }
    /// <inheritdoc />
    public bool DeadLetterUnhandledMessages { get => _deadLetterUnhandledMessages; set { ThrowIfFrozen(); _deadLetterUnhandledMessages = value; } }
    /// <inheritdoc />
    public bool StrictReplyValidation { get => _strictReplyValidation; set { ThrowIfFrozen(); _strictReplyValidation = value; } }
    /// <inheritdoc />
    public TimeSpan DisposeTimeout { get => _disposeTimeout; set { ThrowIfFrozen(); _disposeTimeout = value; } }
    /// <summary>
    /// Gets the transport configuration used to connect to the broker.
    /// </summary>
    public ITransportConfiguration Transport { get; } = new TransportConfiguration();
    /// <summary>
    /// Gets the queue configuration used for local queue names and explicit routing mappings.
    /// </summary>
    public IQueueConfiguration Queues { get; } = new QueueConfiguration();
    /// <summary>
    /// Gets the persistence configuration used for stateful ServiceConnect features.
    /// </summary>
    public IPersistenceConfiguration Persistence { get; } = new PersistenceConfiguration();
    /// <summary>
    /// Gets the configured pipeline filters and middleware.
    /// </summary>
    public PipelineConfiguration Pipeline { get; } = new PipelineConfiguration();

    /// <summary>
    /// Latches this configuration so further setter calls throw <see cref="InvalidOperationException"/>.
    /// Called by <c>AddServiceConnect</c> after the user's configure callback returns.
    /// </summary>
    internal void Freeze() => _frozen = true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfFrozen([CallerMemberName] string? propertyName = null)
    {
        if (_frozen)
        {
            throw new InvalidOperationException(
                $"BusConfiguration is frozen — '{propertyName}' cannot be modified after AddServiceConnect has returned. " +
                "Configure all properties inside the AddServiceConnect callback.");
        }
    }
}
