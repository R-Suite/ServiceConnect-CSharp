namespace ServiceConnect.Interfaces;

/// <summary>
/// Atomic snapshot of <see cref="IProducer"/> health-relevant state.
/// Returned by <see cref="IProducer.GetHealthSnapshot"/> so health-check probes can read
/// the (IsHealthy, HasAttemptedConnection) pair as a single observation; reading the two
/// properties separately admits a race where a publish-success transition lands between
/// the reads and the probe sees stale-IsHealthy + fresh-HasAttemptedConnection — a
/// false-negative Unhealthy.
/// </summary>
/// <param name="IsHealthy">Whether the producer's broker connection is currently open.</param>
/// <param name="HasAttemptedConnection">Whether the producer has ever attempted to connect to the broker.</param>
public readonly record struct ProducerHealthSnapshot(bool IsHealthy, bool HasAttemptedConnection);
