namespace ServiceConnect.Interfaces.Exceptions;

/// <summary>
/// Thrown by a persistence implementation when an optimistic-concurrency update fails
/// because another writer modified the same aggregate between read and write.
/// Callers should typically retry the full read-modify-write cycle on a new snapshot.
/// </summary>
public sealed class ConcurrencyException(string message, Exception? innerException = null)
    : ServiceConnectException(message, innerException);
