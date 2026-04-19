namespace ServiceConnect.Interfaces;

/// <summary>
/// Finds and persists process-manager state.
/// </summary>
public interface IProcessManagerFinder
{
    /// <summary>
    /// Finds the persisted process-manager state that matches an incoming message.
    /// </summary>
    /// <typeparam name="T">The process-manager data type.</typeparam>
    /// <param name="mapper">The property mapper describing correlation rules.</param>
    /// <param name="message">The incoming message.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The persisted data wrapper, or <see langword="null"/> when no match exists.</returns>
    Task<IPersistenceData<T>?> FindDataAsync<T>(IProcessManagerPropertyMapper mapper, Message message, CancellationToken cancellationToken = default) where T : class, IProcessManagerData;

    /// <summary>
    /// Inserts new process-manager state.
    /// </summary>
    /// <param name="data">The process-manager state to insert.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task InsertDataAsync(IProcessManagerData data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates existing process-manager state.
    /// </summary>
    /// <typeparam name="T">The process-manager data type.</typeparam>
    /// <param name="data">The persisted data wrapper to update.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task UpdateDataAsync<T>(IPersistenceData<T> data, CancellationToken cancellationToken = default) where T : class, IProcessManagerData;

    /// <summary>
    /// Deletes persisted process-manager state.
    /// </summary>
    /// <typeparam name="T">The process-manager data type.</typeparam>
    /// <param name="data">The persisted data wrapper to delete.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task DeleteDataAsync<T>(IPersistenceData<T> data, CancellationToken cancellationToken = default) where T : class, IProcessManagerData;
}
