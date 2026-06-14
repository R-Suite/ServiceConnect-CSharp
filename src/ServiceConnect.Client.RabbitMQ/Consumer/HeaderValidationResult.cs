namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Result of pre-dispatch header validation. Returned by
/// <see cref="RabbitMqHeaderValidator.ValidateAsync"/>; on a Reject the validator has already
/// routed the delivery through the terminal-failure path so the host should ack rather than
/// nack-with-requeue.
/// </summary>
internal readonly record struct HeaderValidationResult(bool Accepted, string? RejectReason)
{
    public static HeaderValidationResult Accept() => new(true, null);
    public static HeaderValidationResult Reject(string reason) => new(false, reason);
}
