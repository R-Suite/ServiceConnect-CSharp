namespace ServiceConnect.Interfaces;

/// <summary>
/// Marker for persistence wrappers that carry a stable storage identifier. Lets
/// persistence callers read the Id without reflection or a generic-parameter cast
/// against the wrapping type (analogous to <see cref="IVersioned"/> for the
/// concurrency version).
/// </summary>
public interface IIdentified
{
    /// <summary>
    /// Gets the stable storage identifier assigned at insert.
    /// </summary>
    Guid Id { get; }
}
