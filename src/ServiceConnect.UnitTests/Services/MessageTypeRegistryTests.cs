using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class MessageTypeRegistryTests
{
    private sealed class TypeA { }
    private sealed class TypeB { }

    [Fact]
    public async Task TryResolve_ConcurrentWithRegister_EventuallyResolvesNewlyRegisteredType()
    {
        // The TryResolve + Register race must not cache a stale frozen snapshot — doing so
        // would permanently hide the newly-registered type until the next Register
        // invalidated the cache again. Stress the race across many trials; if the
        // version-aware invalidation regresses, at least one trial wedges and times out.
        for (var trial = 0; trial < 50; trial++)
        {
            var registry = new MessageTypeRegistry();
            registry.Register(typeof(TypeA));
            // Warm the cache so _types is non-null going in.
            registry.TryResolve(typeof(TypeA).FullName!, out _);
            // Force the cache path to be re-built: read _types via a second warm call.
            registry.TryResolve(typeof(TypeA).FullName!, out _);

            // Now invalidate concurrently: one thread constantly resolving, one registering.
            using var gate = new ManualResetEventSlim();
            var bName = typeof(TypeB).FullName!;
            var resolveTask = Task.Run(() =>
            {
                gate.Set();
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < deadline)
                {
                    if (registry.TryResolve(bName, out _))
                    {
                        return;
                    }

                    Thread.Yield();
                }
            });

            gate.Wait();
            registry.Register(typeof(TypeB));

            var completed = await Task.WhenAny(resolveTask, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(completed == resolveTask,
                $"Trial {trial}: stale-snapshot race wedged TryResolve for TypeB.");
        }
    }

    [Fact]
    public void TryResolve_RegisterDuringSnapshotCasWindow_DoesNotCacheStaleSnapshot()
    {
        var registry = new MessageTypeRegistry();
        registry.Register(typeof(TypeA));
        // Warm cache so _types != null.
        registry.TryResolve(typeof(TypeA).FullName!, out _);

        // Invalidate cache so next TryResolve takes the snapshot-and-CAS path.
        // Use reflection to null _types, simulating what a concurrent Register would do.
        var typesField = typeof(MessageTypeRegistry).GetField(
            "_types",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        typesField.SetValue(registry, null);

        // Simulate a concurrent Register in the race window by having the hook register TypeB.
        var registered = false;
        registry._testHookBeforeCas = () =>
        {
            if (!registered)
            {
                registry.Register(typeof(TypeB));
                registered = true;
            }
        };
        // Trigger the snapshot-and-CAS path. _types is null so this will snapshot and CAS.
        // The hook fires mid-CAS and registers TypeB, advancing _version. TryResolve must
        // detect the version advance and invalidate the published snapshot.
        registry.TryResolve("nonexistent", out _);

        // After the race, TypeB must be resolvable. If a stale snapshot were cached without
        // TypeB the lookup would return false.
        var found = registry.TryResolve(typeof(TypeB).FullName!, out var resolved);
        Assert.True(found, "Stale snapshot was cached — race guard missing.");
        Assert.Equal(typeof(TypeB), resolved);
    }


    [Fact]
    public void TryResolve_RegisteredByAssemblyQualifiedName_ReturnsTrue()
    {
        var registry = new MessageTypeRegistry();
        registry.Register(typeof(FakeMessage1));
        var result = registry.TryResolve(typeof(FakeMessage1).AssemblyQualifiedName!, out var type);
        Assert.True(result);
        Assert.Equal(typeof(FakeMessage1), type);
    }

    [Fact]
    public void TryResolve_RegisteredByFullName_ReturnsTrue()
    {
        var registry = new MessageTypeRegistry();
        registry.Register(typeof(FakeMessage1));
        var result = registry.TryResolve(typeof(FakeMessage1).FullName!, out var type);
        Assert.True(result);
        Assert.Equal(typeof(FakeMessage1), type);
    }

    [Fact]
    public void TryResolve_UnregisteredType_ReturnsFalse()
    {
        var registry = new MessageTypeRegistry();
        var result = registry.TryResolve("Some.Unknown.Type, SomeAssembly", out _);
        Assert.False(result);
    }

    [Fact]
    public void Register_DuplicateType_DoesNotThrow()
    {
        var registry = new MessageTypeRegistry();
        registry.Register(typeof(FakeMessage1));
        registry.Register(typeof(FakeMessage1));
        var result = registry.TryResolve(typeof(FakeMessage1).FullName!, out _);
        Assert.True(result);
    }

    [Fact]
    public void TryResolve_Unknown_SetsOutParameterToNull()
    {
        // IMessageTypeRegistry.TryResolve carries [MaybeNullWhen(false)] so callers get
        // correct nullable flow analysis when the type is not found.
        var registry = new MessageTypeRegistry();

        var success = registry.TryResolve("Unknown.TypeName", out Type? resolved);

        Assert.False(success);
        Assert.Null(resolved);
    }

    [Fact]
    public void Register_CollidingType_Throws()
    {
        // Two message types sharing the same FullName (same namespace+name across
        // different assemblies) would otherwise make dispatch non-deterministic.
        // Register must throw on the second entry instead of silently overwriting.
        //
        // Simulate the collision by pre-seeding the internal dictionary under
        // FakeMessage1's FullName with a different Type, then assert the subsequent
        // Register(FakeMessage1) call is rejected.
        var registry = new MessageTypeRegistry();
        var field = typeof(MessageTypeRegistry).GetField(
            "_registeredTypes",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var dict = (System.Collections.Concurrent.ConcurrentDictionary<string, Type>)field.GetValue(registry)!;
        dict[typeof(FakeMessage1).FullName!] = typeof(object);

        Assert.Throws<InvalidOperationException>(() => registry.Register(typeof(FakeMessage1)));
    }
}
