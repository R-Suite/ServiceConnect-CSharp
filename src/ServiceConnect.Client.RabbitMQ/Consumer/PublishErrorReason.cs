namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Why the message is being published to the error exchange. Drives log-message wording so
/// operators can distinguish "retry budget exhausted" from "header corruption" from
/// "permanently invalid payload" at a glance.
/// </summary>
internal enum PublishErrorReason
{
    /// <summary>Retry counter reached the configured maximum; this is the normal final-attempt path.</summary>
    MaxRetriesExceeded,

    /// <summary>The inbound message's RetryCount header was negative, non-numeric, or above the configured cap. Route to error instead of looping.</summary>
    MalformedRetryCountHeader,

    /// <summary>Payload was rejected at deserialise time (JsonException-class). Retrying produces the same failure; route directly to error.</summary>
    PermanentlyInvalidPayload,
}
