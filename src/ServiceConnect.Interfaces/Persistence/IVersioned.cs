namespace ServiceConnect.Interfaces;

/// <summary>
/// Marker for persistence wrappers that carry a monotonic version for optimistic
/// concurrency control. Lets persistence callers read the version without dynamic
/// dispatch or reflection against the wrapping type.
/// </summary>
/// <remarks>
/// Version is <see cref="long"/>: <see cref="int"/> overflows after ~2.1B updates,
/// which is unreachable for any realistic saga, but the typing change is free
/// (matches MongoDB BSON Int64 natively) and forecloses the failure mode entirely.
/// </remarks>
public interface IVersioned
{
    /// <summary>
    /// Gets the persistence version used for optimistic concurrency control.
    /// </summary>
    long Version { get; }
}
