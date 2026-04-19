namespace ServiceConnect.Interfaces;

/// <summary>
/// Wraps persisted process-manager data together with persistence metadata.
/// </summary>
/// <typeparam name="T">The process-manager data type.</typeparam>
public interface IPersistenceData<T> where T : class, IProcessManagerData
{
    /// <summary>
    /// Gets or sets the persisted process-manager data.
    /// </summary>
    T Data { get; set; }
}
