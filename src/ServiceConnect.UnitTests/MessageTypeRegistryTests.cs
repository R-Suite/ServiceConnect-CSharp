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
    public void TryResolve_Unknown_SetsOutParameterToNull()
    {
        // M21: IMessageTypeRegistry.TryResolve should carry [MaybeNullWhen(false)] so that
        // callers get correct nullable flow analysis when the type is not found.
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
