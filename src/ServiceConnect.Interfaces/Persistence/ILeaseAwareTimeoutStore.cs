namespace ServiceConnect.Interfaces;

public interface ILeaseAwareTimeoutStore
{
    Task RemoveDispatchedTimeoutAsync(Guid id, Guid lockOwner, CancellationToken cancellationToken = default);
    Task ReleaseDispatchedTimeoutAsync(Guid id, Guid lockOwner, CancellationToken cancellationToken = default);
}
