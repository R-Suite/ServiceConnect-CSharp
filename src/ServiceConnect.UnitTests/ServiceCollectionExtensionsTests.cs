using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System.Reflection;
using ServiceConnect;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests;

public class ServiceCollectionExtensionsTests
{
    private static IServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        // SendMessagePipeline depends on IProducer
        services.AddSingleton(new Mock<IProducer>().Object);
        // Bus depends on ILogger<Bus>
        services.AddLogging();
        return services;
    }

    [Fact]
    public void AddServiceConnect_RegistersIBus()
    {
        var services = CreateServices();

        services.AddServiceConnect(b => b
            .ConfigureQueues(q => q.QueueName = "test")
            .ConfigureBus(c => c.ScanForMessageHandlers = false));

        var provider = services.BuildServiceProvider();
        var bus = provider.GetService<IBus>();

        Assert.NotNull(bus);
    }

    [Fact]
    public void AddServiceConnect_InvokesBuilderCallback()
    {
        var services = CreateServices();
        var callbackInvoked = false;

        services.AddServiceConnect(b =>
        {
            callbackInvoked = true;
            b.ConfigureQueues(q => q.QueueName = "callback-queue");
            b.ConfigureBus(c => c.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var queueConfig = provider.GetRequiredService<ServiceConnect.Interfaces.Configuration.IQueueConfiguration>();

        Assert.True(callbackInvoked);
        Assert.Equal("callback-queue", queueConfig.QueueName);
    }

    [Fact]
    public void AddServiceConnect_RegistersCoreServices()
    {
        var services = CreateServices();

        services.AddServiceConnect(b => b
            .ConfigureQueues(q => q.QueueName = "test")
            .ConfigureBus(c => c.ScanForMessageHandlers = false));

        var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IMessageSerializer>());
        Assert.NotNull(provider.GetService<IFilterPipeline>());
        Assert.NotNull(provider.GetService<IRequestReplyManager>());
        Assert.NotNull(provider.GetService<ISendMessagePipeline>());
    }

    [Fact]
    public void AddServiceConnect_MapsPublicAndInternalReplyManagerContracts_ToSameSingleton()
    {
        var services = CreateServices();

        services.AddServiceConnect(b => b
            .ConfigureQueues(q => q.QueueName = "test")
            .ConfigureBus(c => c.ScanForMessageHandlers = false));

        var provider = services.BuildServiceProvider();
        var publicContract = provider.GetRequiredService<IRequestReplyManager>();
        var internalContract = provider.GetRequiredService<IReplyStatusRequestReplyManager>();

        Assert.Same(publicContract, internalContract);
    }

    [Fact]
    public void AddServiceConnect_OverriddenRequestReplyManager_DoesNotBreakInternalReplyStatusContract()
    {
        // Replacing the public IRequestReplyManager must not null out the internal
        // IReplyStatusRequestReplyManager — that contract resolves to the concrete
        // RequestReplyManager singleton directly so dispatch never sees a null trust-query.
        var services = CreateServices();

        services.AddSingleton<IRequestReplyManager, OverrideRequestReplyManager>();
        services.AddServiceConnect(b => b
            .ConfigureQueues(q => q.QueueName = "test")
            .ConfigureBus(c => c.ScanForMessageHandlers = false));

        var provider = services.BuildServiceProvider();

        Assert.IsType<OverrideRequestReplyManager>(provider.GetRequiredService<IRequestReplyManager>());
        Assert.NotNull(provider.GetService<IReplyStatusRequestReplyManager>());
    }

    [Fact]
    public void AddServiceConnect_AllowsOverriddenRequestReplyManager_ToResolveDispatcherPath()
    {
        var services = CreateServices();

        services.AddSingleton<IRequestReplyManager, OverrideRequestReplyManager>();
        services.AddServiceConnect(b => b
            .ConfigureQueues(q => q.QueueName = "test")
            .ConfigureBus(c => c.ScanForMessageHandlers = false));

        var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ReplyProcessor>());
        Assert.NotNull(provider.GetRequiredService<IMessageDispatcher>());
    }

    [Fact]
    public void ServiceCollectionExtensions_DefinesRegistrationHelpers()
    {
        string[] expectedHelpers =
        [
            "RegisterConfiguration",
            "RegisterCoreServices",
            "RegisterProcessors",
            "RegisterHandlers",
            "RegisterBus"
        ];

        foreach (var helper in expectedHelpers)
        {
            var method = typeof(ServiceCollectionExtensions).GetMethod(
                helper,
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(method);
            Assert.Equal(typeof(void), method!.ReturnType);
        }
    }

    [Fact]
    public void AddServiceConnect_ThrowsWhenInboundMiddlewareIsNotRegistered()
    {
        // Inbound middleware referenced by the pipeline must be registered in DI,
        // or it will fail to resolve at dispatch time. We surface this at startup.
        var services = CreateServices();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddServiceConnect(b => b
                .ConfigureQueues(q => q.QueueName = "test")
                .ConfigureBus(c => c.ScanForMessageHandlers = false)
                .AddMessageProcessingMiddleware<TestInboundMiddleware>()));

        Assert.Contains(nameof(TestInboundMiddleware), exception.Message);
    }

    [Fact]
    public void AddServiceConnect_ThrowsWhenBeforeConsumingFilterIsNotRegistered()
    {
        var services = CreateServices();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddServiceConnect(b => b
                .ConfigureQueues(q => q.QueueName = "test")
                .ConfigureBus(c => c.ScanForMessageHandlers = false)
                .AddBeforeConsumingFilter<TestInboundFilter>()));

        Assert.Contains(nameof(TestInboundFilter), exception.Message);
    }

    [Fact]
    public void AddServiceConnect_ThrowsWhenOutgoingFilterIsNotRegistered()
    {
        var services = CreateServices();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddServiceConnect(b => b
                .ConfigureQueues(q => q.QueueName = "test")
                .ConfigureBus(c => c.ScanForMessageHandlers = false)
                .AddOutgoingFilter<TestInboundFilter>()));

        Assert.Contains(nameof(TestInboundFilter), exception.Message);
    }

    [Fact]
    public void AddServiceConnect_AcceptsScopedInboundMiddleware()
    {
        // Unlike send middleware (which must be singleton because it runs in the
        // producer scope), inbound middleware may be registered with any lifetime
        // because it resolves from the per-message scope.
        var services = CreateServices();
        services.AddScoped<TestInboundMiddleware>();

        services.AddServiceConnect(b => b
            .ConfigureQueues(q => q.QueueName = "test")
            .ConfigureBus(c => c.ScanForMessageHandlers = false)
            .AddMessageProcessingMiddleware<TestInboundMiddleware>());

        // Did not throw — registration accepted.
        var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<IBus>());
    }

    [Fact]
    public void AddServiceConnect_AcceptsTransientBeforeConsumingFilter()
    {
        var services = CreateServices();
        services.AddTransient<TestInboundFilter>();

        services.AddServiceConnect(b => b
            .ConfigureQueues(q => q.QueueName = "test")
            .ConfigureBus(c => c.ScanForMessageHandlers = false)
            .AddBeforeConsumingFilter<TestInboundFilter>());

        var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<IBus>());
    }

    [Fact]
    public void RegisterHandlerType_PreRegisteredTransient_DoesNotAddDuplicate()
    {
        // If the caller pre-registered the handler transient, auto-registration must
        // not add a second descriptor — duplicates cause HandlerProcessor to dispatch
        // the same message twice because GetServices(...) yields both instances.
        var services = new ServiceCollection();
        services.AddTransient<IMessageHandler<H5Msg>, H5Handler>();

        InvokeRegisterHandlerType(services, typeof(H5Handler), typeof(H5Msg));

        Assert.Single(services, d => d.ServiceType == typeof(IMessageHandler<H5Msg>));
    }

    [Fact]
    public void RegisterHandlerType_PreRegisteredScoped_DoesNotAddDuplicate()
    {
        var services = new ServiceCollection();
        services.AddScoped<IMessageHandler<H5Msg>, H5Handler>();

        InvokeRegisterHandlerType(services, typeof(H5Handler), typeof(H5Msg));

        Assert.Single(services, d => d.ServiceType == typeof(IMessageHandler<H5Msg>));
    }

    [Fact]
    public void RegisterHandlerType_NoPreRegistration_AddsAsTransient()
    {
        var services = new ServiceCollection();

        InvokeRegisterHandlerType(services, typeof(H5Handler), typeof(H5Msg));

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IMessageHandler<H5Msg>));
        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
        Assert.Equal(typeof(H5Handler), descriptor.ImplementationType);
    }

    [Fact]
    public void RegisterHandlerType_PreRegisteredAggregator_DoesNotAddDuplicate()
    {
        // Aggregators register against the base Aggregator<TMsg> generic — the same
        // dedup rule applies there.
        var services = new ServiceCollection();
        services.AddScoped<Aggregator<H5Msg>, H5Aggregator>();

        InvokeRegisterHandlerType(services, typeof(H5Aggregator), typeof(H5Msg));

        Assert.Single(services, d => d.ServiceType == typeof(Aggregator<H5Msg>));
    }

    [Fact]
    public void AddServiceConnect_WithPreRegisteredHandler_ResolvesSingleInstance()
    {
        // End-to-end guard: scanning finds H5Handler, caller also pre-registered it —
        // after AddServiceConnect the container must resolve exactly one instance for
        // IMessageHandler<H5Msg>, otherwise the dispatcher would invoke the handler twice.
        var services = CreateServices();
        services.AddTransient<IMessageHandler<H5Msg>, H5Handler>();

        services.AddServiceConnect(b => b
            .ConfigureQueues(q => q.QueueName = "test")
            .ScanAssemblies(typeof(H5Handler).Assembly));

        using var provider = services.BuildServiceProvider();
        var handlers = provider.GetServices<IMessageHandler<H5Msg>>().ToList();
        Assert.Single(handlers);
        Assert.IsType<H5Handler>(handlers[0]);
    }

    private static void InvokeRegisterHandlerType(IServiceCollection services, Type handlerType, Type messageType)
    {
        var method = typeof(ServiceCollectionExtensions).GetMethod(
            "RegisterHandlerType",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var handlerRef = new HandlerReference { MessageType = messageType, HandlerType = handlerType };
        method!.Invoke(null, new object[] { services, handlerRef });
    }

    [Fact]
    public void AddServiceConnect_ThrowsWhenSendMiddlewareIsNotSingleton()
    {
        var services = CreateServices();
        services.AddTransient<TestSendMiddleware>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddServiceConnect(b => b
                .ConfigureQueues(q => q.QueueName = "test")
                .ConfigureBus(c => c.ScanForMessageHandlers = false)
                .AddSendMessageMiddleware<TestSendMiddleware>()));

        Assert.Contains(nameof(TestSendMiddleware), exception.Message);
        Assert.Contains("singleton", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class H5Msg : Message
{
    public H5Msg() : base(Guid.NewGuid()) { }
}

public sealed class H5Handler : IMessageHandler<H5Msg>
{
    public IConsumeContext? Context { get; set; }
    public Task HandleAsync(H5Msg message) => Task.CompletedTask;
}

public sealed class H5Aggregator : Aggregator<H5Msg>
{
    public override void Execute(IList<H5Msg> messages) { }
}

file sealed class TestInboundMiddleware : IMessageProcessingMiddleware
{
    public Task<ConsumeEventResult> Process(
        ReadOnlyMemory<byte> messageBytes,
        Type messageType,
        object message,
        IDictionary<string, object> headers,
        Envelope envelope,
        MessageProcessingDelegate next,
        CancellationToken cancellationToken = default) =>
        next(messageBytes, messageType, message, headers, envelope, cancellationToken);
}

file sealed class TestInboundFilter : IFilter
{
    public Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}

file sealed class TestSendMiddleware : ISendMessageMiddleware
{
    public Task Process(
        Type messageType,
        byte[] messageBytes,
        Dictionary<string, string> headers,
        string? endPoint,
        SendMessageDelegate next,
        CancellationToken cancellationToken = default) =>
        next(messageType, messageBytes, headers, endPoint, cancellationToken);
}

file sealed class OverrideRequestReplyManager : IRequestReplyManager
{
    public Task<TReply> SendRequestAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        ServiceConnect.Interfaces.Options.RequestOptions options,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message =>
        throw new NotSupportedException();

    public Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        ServiceConnect.Interfaces.Options.RequestOptions options,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message =>
        throw new NotSupportedException();

    public Task PublishRequestAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        ServiceConnect.Interfaces.Options.RequestOptions options,
        Action<TReply> onReply,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message =>
        throw new NotSupportedException();

    public void ProcessReply(string messageId, ReadOnlyMemory<byte> messageBytes, Type type) =>
        throw new NotSupportedException();
}
