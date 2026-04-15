using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

public class InterfaceCleanupTests
{
    [Fact]
    public void HandlerReference_DoesNotExposeRoutingKeys()
    {
        Assert.Null(typeof(HandlerReference).GetProperty("RoutingKeys"));
    }

    [Fact]
    public void IConsumeContext_CancellationToken_IsReadOnly()
    {
        var property = typeof(IConsumeContext).GetProperty(nameof(IConsumeContext.CancellationToken))!;

        Assert.NotNull(property);
        Assert.Null(property.SetMethod);
    }

    [Fact]
    public void IBusConfiguration_DoesNotExposeNestedConfigurations()
    {
        Assert.Null(typeof(IBusConfiguration).GetProperty("Transport"));
        Assert.Null(typeof(IBusConfiguration).GetProperty("Queues"));
        Assert.Null(typeof(IBusConfiguration).GetProperty("Persistence"));
        Assert.Null(typeof(IBusConfiguration).GetProperty("Pipeline"));
    }

    [Fact]
    public void IRequestReplyManager_MethodsDoNotExposeSendDelegate()
    {
        var sendRequest = typeof(IRequestReplyManager).GetMethod(nameof(IRequestReplyManager.SendRequestAsync))!;
        var sendRequestMulti = typeof(IRequestReplyManager).GetMethod(nameof(IRequestReplyManager.SendRequestMultiAsync))!;

        Assert.DoesNotContain(sendRequest.GetParameters(), parameter => parameter.ParameterType.Name.Contains("Func"));
        Assert.DoesNotContain(sendRequestMulti.GetParameters(), parameter => parameter.ParameterType.Name.Contains("Func"));
    }

    [Fact]
    public void ProcessManagerTimeoutService_Constructor_UsesLazyBusInsteadOfServiceProvider()
    {
        var constructor = typeof(ProcessManagerTimeoutService).GetConstructors().Single();
        var parameterTypes = constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        Assert.DoesNotContain(typeof(IServiceProvider), parameterTypes);
        Assert.Contains(typeof(Lazy<IBus>), parameterTypes);
    }
}
