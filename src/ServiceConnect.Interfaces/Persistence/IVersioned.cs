namespace ServiceConnect.Interfaces;

/// <summary>
/// Marker for persistence wrappers that carry a monotonic version for optimistic
/// concurrency control. Lets persistence callers read the version without dynamic
/// dispatch or reflection against the wrapping type.
/// </summary>
public interface IVersioned
{
    /// <summary>
    /// Gets the persistence version used for optimistic concurrency control.
    /// </summary>
    int Version { get; }
}
