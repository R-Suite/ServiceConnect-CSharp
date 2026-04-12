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
}
