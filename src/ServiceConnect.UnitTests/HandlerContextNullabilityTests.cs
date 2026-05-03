using System.Reflection;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests;

public class HandlerContextNullabilityTests
{
    // v8: Context moved from property to parameter on HandleAsync / ExecuteAsync.
    // These tests pin that the interfaces no longer expose a Context property (which
    // would silently reintroduce the thread-safety issue on singleton handlers).

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

    private sealed class DummyData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public int Version { get; set; }
    }
}
