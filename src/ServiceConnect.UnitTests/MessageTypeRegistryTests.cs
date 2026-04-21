using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MessageTypeRegistryTests
{
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
    public void Register_CollidingType_Throws()
    {
        // M4 regression: previously a second Register call under the same FullName
        // (e.g. two message types with the same namespace+name in different assemblies)
        // overwrote the first entry silently, making dispatch non-deterministic.
        //
        // Simulate the collision by pre-seeding the internal dictionary under
        // FakeMessage1's FullName with a different Type. The registry must reject
        // the subsequent Register(FakeMessage1) call rather than overwrite.
        var registry = new MessageTypeRegistry();
        var field = typeof(MessageTypeRegistry).GetField(
            "_registeredTypes",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var dict = (System.Collections.Concurrent.ConcurrentDictionary<string, Type>)field.GetValue(registry)!;
        dict[typeof(FakeMessage1).FullName!] = typeof(object);

        Assert.Throws<InvalidOperationException>(() => registry.Register(typeof(FakeMessage1)));
    }
}
