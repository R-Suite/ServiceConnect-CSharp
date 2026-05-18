namespace ServiceConnect.Client.RabbitMQ.Configuration;

/// <summary>
/// Strongly-typed RabbitMQ transport options. Use via the
/// <c>UseRabbitMQ(opts =&gt; ...)</c> extension overload — set only the
/// properties you want; the lambda stuffs non-null values into
/// <c>ITransportConfiguration.ClientSettings</c> using the keys from
/// <see cref="RabbitMQSettingKeys"/>. Properties mirror <see cref="RabbitMQSettingKeys"/> 1:1.
/// </summary>
/// <remarks>
/// This is the typed user-facing surface; internally the consumer host and producer
/// still read from the <c>ClientSettings</c> dictionary. A future release may switch
/// internals to <c>IOptions{RabbitMqOptions}</c>.
/// </remarks>
public sealed record class RabbitMqOptions
{
    /// <summary>RabbitMQ TCP port. Defaults to 5672 for plain AMQP, 5671 for AMQPS.</summary>
    public int? Port { get; set; }

    /// <summary>Whether declared queues should be durable. Default: true.</summary>
    public bool? Durable { get; set; }

    /// <summary>Whether declared queues should be exclusive. Default: false.</summary>
    public bool? Exclusive { get; set; }

    /// <summary>Whether declared queues should be auto-deleted. Default: false.</summary>
    public bool? AutoDelete { get; set; }

    /// <summary>Additional x-arguments for the primary queue declaration.</summary>
    public IDictionary<string, object?>? Arguments { get; set; }

    /// <summary>Additional x-arguments for retry-queue declarations.</summary>
    public IDictionary<string, object?>? RetryQueueArguments { get; set; }

    /// <summary>Additional x-arguments for utility queues (audit, error).</summary>
    public IDictionary<string, object?>? UtilityQueueArguments { get; set; }

    /// <summary>Requested consumer prefetch count.</summary>
    public ushort? PrefetchCount { get; set; }

    /// <summary>Whether prefetch configuration should be disabled (consumer-side).</summary>
    public bool? DisablePrefetch { get; set; }

    /// <summary>Maximum inbound message body size, in bytes.</summary>
    public long? MessageSize { get; set; }

    /// <summary>Whether publisher confirms are enabled for outbound publishes.</summary>
    public bool? PublisherAcknowledgements { get; set; }

    /// <summary>Publisher retry attempt count.</summary>
    public int? RetryCount { get; set; }

    /// <summary>Delay between publish retries, in seconds (not milliseconds).</summary>
    public ushort? RetrySeconds { get; set; }

    /// <summary>
    /// Whether AMQP heartbeats are enabled. Default: true. <b>Disabling removes broker-side
    /// dead-peer detection</b> — see <see cref="RabbitMQSettingKeys.HeartbeatEnabled"/> for full
    /// rationale.
    /// </summary>
    public bool? HeartbeatEnabled { get; set; }

    /// <summary>Heartbeat interval in seconds.</summary>
    public ushort? HeartbeatTime { get; set; }

    /// <summary>Maximum time to wait for a broker ack under publisher confirms. Default: 30s.</summary>
    public TimeSpan? PublishTimeout { get; set; }

    /// <summary>Maximum outstanding publisher confirms before back-pressure. Default: 256.</summary>
    public int? MaxOutstandingPublishConfirms { get; set; }

    /// <summary>Interval between auto-recovery attempts after a connection drop.</summary>
    public TimeSpan? NetworkRecoveryInterval { get; set; }

    /// <summary>
    /// Validates the option values that have explicit range constraints. Properties
    /// typed as <see cref="ushort"/>? are non-negative by type and need no runtime check;
    /// this method covers the <see cref="int"/>?, <see cref="long"/>?, and
    /// <see cref="TimeSpan"/>? properties whose acceptable range cannot be expressed in
    /// the type system.
    /// </summary>
    /// <returns>
    /// A list of human-readable error messages — one per invalid property. Returns an
    /// empty list when all set values are within range. Properties left at <see langword="null"/>
    /// (i.e. not configured) are skipped; defaults are not asserted here.
    /// </returns>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (Port is { } port && (port < 1 || port > 65535))
        {
            errors.Add($"Port must be between 1 and 65535 (was {port}).");
        }

        if (RetryCount is { } retryCount && retryCount < 0)
        {
            errors.Add($"RetryCount must be non-negative (was {retryCount}).");
        }

        if (MessageSize is { } messageSize && messageSize <= 0)
        {
            errors.Add($"MessageSize must be positive (was {messageSize}).");
        }

        if (PublishTimeout is { } publishTimeout && publishTimeout <= TimeSpan.Zero)
        {
            errors.Add($"PublishTimeout must be positive (was {publishTimeout}).");
        }

        if (MaxOutstandingPublishConfirms is { } maxOutstanding && maxOutstanding <= 0)
        {
            errors.Add($"MaxOutstandingPublishConfirms must be positive (was {maxOutstanding}).");
        }

        if (NetworkRecoveryInterval is { } recoveryInterval && recoveryInterval <= TimeSpan.Zero)
        {
            errors.Add($"NetworkRecoveryInterval must be positive (was {recoveryInterval}).");
        }

        return errors;
    }
}
