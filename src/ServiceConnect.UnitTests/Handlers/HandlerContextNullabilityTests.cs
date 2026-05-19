using System.Reflection;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Handlers;

public class HandlerContextNullabilityTests
{
    // The handler interfaces receive the per-message IConsumeContext as a parameter on
    // HandleAsync / ExecuteAsync, not as an ambient property. These tests pin that no
    // Context property is reintroduced — a property would silently break thread-safety
    // on singleton-registered handlers.

    [Fact]
    public void IMessageHandler_DoesNotExposeContextProperty()
    {
        var props = typeof(IMessageHandler<>).GetProperties();
        Assert.DoesNotContain(props, p => p.Name == "Context");
    }

    [Fact]
    public void IProcessHandler_DoesNotExposeContextProperty()
    {
        var props = typeof(IProcessHandler<,>).GetProperties();
        Assert.DoesNotContain(props, p => p.Name == "Context");
    }

    [Fact]
    public void IStreamHandler_DoesNotExposeStreamProperty()
    {
        var props = typeof(IStreamHandler<>).GetProperties();
        Assert.DoesNotContain(props, p => p.Name == "Stream");
    }

    [Fact]
    public void IMessageHandler_HandleAsync_HasIConsumeContextParameter()
    {
        var method = typeof(IMessageHandler<Message>).GetMethod("HandleAsync")!;
        var paramTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Contains(typeof(IConsumeContext), paramTypes);
    }

    [Fact]
    public void IStreamHandler_ExecuteAsync_HasIMessageBusReadStreamParameter()
    {
        var method = typeof(IStreamHandler<Message>).GetMethod("ExecuteAsync")!;
        var paramTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Contains(typeof(IMessageBusReadStream), paramTypes);
    }

    [Fact]
    public void IProcessHandler_HandleAsync_HasIConsumeContextParameter()
    {
        // Contract guard: HandleAsync must accept (TMessage, TData, IConsumeContext, CancellationToken).
        // Open generic so the assertion is structural rather than tied to a specific concrete TData/TMessage.
        var method = typeof(IProcessHandler<,>).GetMethod(nameof(IProcessHandler<DummyData, Message>.HandleAsync));
        Assert.NotNull(method);
        var paramTypes = method!.GetParameters().Select(p => p.ParameterType.Name).ToArray();
        Assert.Contains(nameof(IConsumeContext), paramTypes);
    }

    private sealed class DummyData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public int Version { get; set; }
    }
}
