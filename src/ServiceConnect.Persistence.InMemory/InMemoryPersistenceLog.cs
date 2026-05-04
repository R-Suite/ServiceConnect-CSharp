using Microsoft.Extensions.Logging;

namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Source-generated logger entries emitted by the in-memory persistence package.
/// </summary>
internal static partial class InMemoryPersistenceLog
{
    /// <summary>
    /// Stable event id for the one-shot registration warning.
    /// </summary>
    public const int InMemoryPersistenceRegisteredEventId = 1;

    [LoggerMessage(
        EventId = InMemoryPersistenceRegisteredEventId,
        EventName = "InMemoryPersistenceRegistered",
        Level = LogLevel.Warning,
        Message = "In-memory persistence is registered. State is held in-process and is not durable across restarts. This is intended for development and tests; use a real persistor (e.g. MongoDB) in production.")]
    public static partial void InMemoryPersistenceRegistered(ILogger logger);
}
