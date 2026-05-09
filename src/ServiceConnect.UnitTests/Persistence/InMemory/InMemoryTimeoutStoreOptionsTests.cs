using System.Reflection;
using System.Threading;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence.InMemory;

public class InMemoryTimeoutStoreOptionsTests
{
    [Fact]
    public void Dispose_DisposesOwnedPersistenceState()
    {
        var options = new InMemoryPersistenceOptions { LockLeaseDuration = TimeSpan.FromMinutes(1) };
        var store = new InMemoryTimeoutStore(options);

        var stateField = typeof(InMemoryTimeoutStore).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic);
        var state = stateField!.GetValue(store);
        Assert.NotNull(state);

        // Reflect to grab SyncRoot — the kernel-handle-backed primitive that leaks if Dispose
        // doesn't propagate. Pre-dispose: usable. Post-dispose: throws ObjectDisposedException.
        var syncRootProperty = state!.GetType().GetProperty("SyncRoot");
        var syncRoot = (ReaderWriterLockSlim)syncRootProperty!.GetValue(state)!;
        syncRoot.EnterReadLock();
        syncRoot.ExitReadLock();

        store.Dispose();

        Assert.Throws<ObjectDisposedException>(syncRoot.EnterReadLock);
    }

    [Fact]
    public void Dispose_DoesNotDisposeNonOwnedPersistenceState()
    {
        // Internal ctor path — caller-supplied state must not be disposed by the store.
        var options = new InMemoryPersistenceOptions { LockLeaseDuration = TimeSpan.FromMinutes(1) };

        // Use reflection to invoke the internal ctor.
        var stateType = typeof(InMemoryTimeoutStore).Assembly.GetType("ServiceConnect.Persistence.InMemory.InMemoryPersistenceState");
        Assert.NotNull(stateType);
        var stateCtor = stateType!.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(TimeProvider)], null)
            ?? stateType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public,
                null, [typeof(TimeProvider)], null);
        Assert.NotNull(stateCtor);
        var sharedState = stateCtor!.Invoke([null]);

        var storeCtor = typeof(InMemoryTimeoutStore).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(InMemoryPersistenceOptions), stateType, typeof(TimeProvider)], null);
        Assert.NotNull(storeCtor);
        var store = (InMemoryTimeoutStore)storeCtor!.Invoke([options, sharedState, null]);

        var syncRootProperty = stateType.GetProperty("SyncRoot");
        var syncRoot = (ReaderWriterLockSlim)syncRootProperty!.GetValue(sharedState)!;

        store.Dispose();

        // sharedState's SyncRoot is still usable — store didn't dispose it.
        syncRoot.EnterReadLock();
        syncRoot.ExitReadLock();

        // Cleanup: dispose the shared state explicitly so this test doesn't leak the lock.
        ((IDisposable)sharedState).Dispose();
    }
}
