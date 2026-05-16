using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect;
using ServiceConnect.DependencyInjection;
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
    public void AddServiceConnect_ThrowsAtConfigTime_WhenUserRegistersPartialRequestReplyManager()
    {
        // A caller who replaces IRequestReplyManager with a type that does NOT also
        // implement IReplyStatusRequestReplyManager would cause a split-brain: outgoing
        // requests go through the custom impl while reply tracking still goes through
        // the stock RequestReplyManager, silently dropping replies.
        // AddServiceConnect must detect this and throw immediately at configuration time.
        var services = CreateServices();

        services.AddSingleton<IRequestReplyManager, OverrideRequestReplyManager>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddServiceConnect(b => b
                .ConfigureQueues(q => q.QueueName = "test")
                .ConfigureBus(c => c.ScanForMessageHandlers = false)));

        Assert.Contains(nameof(IRequestReplyManager), exception.Message);
        Assert.Contains(nameof(IReplyStatusRequestReplyManager), exception.Message);
    }

    [Fact]
    public void AddServiceConnect_AllowsOverriddenRequestReplyManager_WhenItAlsoImplementsReplyStatusContract()
    {
        // A caller who replaces IRequestReplyManager with a full impl (also implementing
        // IReplyStatusRequestReplyManager) is supported. Both interfaces must resolve to
        // the same instance so the dispatcher path works correctly.
        var services = CreateServices();

        services.AddSingleton<IRequestReplyManager, FullOverrideRequestReplyManager>();
        services.AddServiceConnect(b => b
            .ConfigureQueues(q => q.QueueName = "test")
            .ConfigureBus(c => c.ScanForMessageHandlers = false));

        var provider = services.BuildServiceProvider();

        Assert.IsType<FullOverrideRequestReplyManager>(provider.GetRequiredService<IRequestReplyManager>());
        Assert.NotNull(provider.GetService<IReplyStatusRequestReplyManager>());
        Assert.Same(
            provider.GetRequiredService<IRequestReplyManager>(),
            provider.GetRequiredService<IReplyStatusRequestReplyManager>());
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
        => InvokeRegisterHandlerType(services, handlerType, messageType, HandlerInterfaceKind.MessageHandler);

    private static void InvokeRegisterHandlerType(
        IServiceCollection services,
        Type handlerType,
        Type messageType,
        HandlerInterfaceKind kind)
    {
        // Snapshot pre-existing service types so the user-pre-registration guard fires correctly.
        var preExisting = services.Select(d => d.ServiceType).ToHashSet();
        InvokeRegisterHandlerType(services, handlerType, messageType, kind, preExisting);
    }

    private static void InvokeRegisterHandlerType(
        IServiceCollection services,
        Type handlerType,
        Type messageType,
        HandlerInterfaceKind kind,
        IReadOnlySet<Type>? preExistingServiceTypes)
    {
        var method = typeof(ServiceCollectionExtensions).GetMethod(
            "RegisterHandlerType",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var handlerRef = new HandlerReference { MessageType = messageType, HandlerType = handlerType, InterfaceKind = kind };
        method!.Invoke(null, [services, handlerRef, preExistingServiceTypes]);
    }

    // --- Multi-registration regression tests ---

    [Fact]
    public void RegisterHandlerType_DualInterfaceHandler_RegistersBothMessageHandlerAndProcessHandler()
    {
        // A class implementing both IMessageHandler<A> and IProcessHandler<TData, A> produces
        // two HandlerReferences (one per interface kind). Both must be registered in DI.
        var services = new ServiceCollection();
        // Shared empty snapshot: both refs are scan-discovered in the same scan loop.
        var snapshot = services.Select(d => d.ServiceType).ToHashSet();

        InvokeRegisterHandlerType(services, typeof(DualInterfaceHandler), typeof(DualMsg), HandlerInterfaceKind.MessageHandler, snapshot);
        InvokeRegisterHandlerType(services, typeof(DualInterfaceHandler), typeof(DualMsg), HandlerInterfaceKind.ProcessHandler, snapshot);

        Assert.Contains(services, d => d.ServiceType == typeof(IMessageHandler<DualMsg>)
            && d.ImplementationType == typeof(DualInterfaceHandler));
        Assert.Contains(services, d => d.ServiceType == typeof(IProcessHandler<DualData, DualMsg>)
            && d.ImplementationType == typeof(DualInterfaceHandler));
    }

    [Fact]
    public void RegisterHandlerType_TwoDistinctHandlersForSameMessage_RegistersBoth()
    {
        // Two concrete classes that both implement IMessageHandler<MsgX> must each get
        // their own descriptor. HandlerProcessor resolves via GetServices, so both must
        // be present for both to be dispatched.
        var services = new ServiceCollection();
        // Shared empty snapshot: both handlers are scan-discovered in the same scan loop,
        // so neither was pre-registered by the caller.
        var snapshot = services.Select(d => d.ServiceType).ToHashSet();

        InvokeRegisterHandlerType(services, typeof(MultiHandlerA), typeof(MultiMsg), HandlerInterfaceKind.MessageHandler, snapshot);
        InvokeRegisterHandlerType(services, typeof(MultiHandlerB), typeof(MultiMsg), HandlerInterfaceKind.MessageHandler, snapshot);

        var descriptors = services.Where(d => d.ServiceType == typeof(IMessageHandler<MultiMsg>)).ToList();
        Assert.Equal(2, descriptors.Count);
        Assert.Contains(descriptors, d => d.ImplementationType == typeof(MultiHandlerA));
        Assert.Contains(descriptors, d => d.ImplementationType == typeof(MultiHandlerB));
    }

    [Fact]
    public void RegisterHandlerType_UserPreRegistration_SuppressesScanDiscoveredHandler()
    {
        // When the caller pre-registers a handler for IMessageHandler<MsgY>, a scan-discovered
        // handler for the same message type must not be added. The user registration is authoritative.
        var services = new ServiceCollection();
        services.AddTransient<IMessageHandler<PreRegMsg>, PreRegUserHandler>();

        // Snapshot taken after the user pre-registration and before the scan loop:
        // IMessageHandler<PreRegMsg> is already present, so the scan-discovered handler must be suppressed.
        InvokeRegisterHandlerType(services, typeof(PreRegOtherHandler), typeof(PreRegMsg), HandlerInterfaceKind.MessageHandler);

        var descriptors = services.Where(d => d.ServiceType == typeof(IMessageHandler<PreRegMsg>)).ToList();
        Assert.Single(descriptors);
        Assert.Equal(typeof(PreRegUserHandler), descriptors[0].ImplementationType);
    }

    [Fact]
    public void AddServiceConnect_ScansExplicitAssembliesEvenWhenDiscoveryDisabled()
    {
        // ScanAssemblies(...) must be honoured even when ScanForMessageHandlers=false.
        // The explicit list represents "scan exactly these assemblies"; the global flag
        // must not silently override it.
        var services = new ServiceCollection();
        services.AddServiceConnect(b =>
        {
            b.ConfigureQueues(q => q.QueueName = "test");
            b.ConfigureBus(c => c.ScanForMessageHandlers = false);
            b.ScanAssemblies(typeof(TestHandlerFixture).Assembly);
        });

        using var provider = services.BuildServiceProvider();
        var handler = provider.GetService<IMessageHandler<TestHandlerFixture.SampleMessage>>();
        Assert.NotNull(handler);
    }

    [Fact]
    public void AddServiceConnect_DetectsFactoryRegisteredSingletonHandlers()
    {
        // Pre-registering a handler via a factory singleton must prevent the scanner
        // from adding a second transient descriptor via TryAddEnumerable.
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<TestHandlerFixture.SampleMessage>>(
            _ => new TestHandlerFixture.SampleHandler());
        services.AddServiceConnect(b => b
            .ConfigureQueues(q => q.QueueName = "test")
            .ScanAssemblies(typeof(TestHandlerFixture).Assembly));

        using var provider = services.BuildServiceProvider();
        var handlers = provider.GetServices<IMessageHandler<TestHandlerFixture.SampleMessage>>().ToList();
        Assert.Single(handlers);
    }

    [Fact]
    public void AddServiceConnect_UserFactoryRegistration_SuppressesScannerForSameType()
    {
        // Semantic lock-in for the "user registration is authoritative" guard in
        // RegisterHandlerType: if ANY descriptor answers IMessageHandler<T> before
        // AddServiceConnect runs (regardless of registration form — factory, instance,
        // or type), the scanner must not add a second transient descriptor.
        // This is intentional: callers who want both a manual and a scan-discovered
        // handler for the same message type must register all of them explicitly.
        var services = new ServiceCollection();
        // User factory-registers ONE handler for SampleMessage.
        services.AddSingleton<IMessageHandler<TestHandlerFixture.SampleMessage>>(
            _ => new TestHandlerFixture.SampleHandler());

        // Scanner would otherwise find TestHandlerFixture.SampleHandler too.
        services.AddServiceConnect(b => b
            .ConfigureQueues(q => q.QueueName = "test")
            .ScanAssemblies(typeof(TestHandlerFixture).Assembly));

        using var provider = services.BuildServiceProvider();
        var handlers = provider.GetServices<IMessageHandler<TestHandlerFixture.SampleMessage>>().ToList();

        // User registration is authoritative — no duplicate from scanner.
        Assert.Single(handlers);
    }

    [Fact]
    public void AddServiceConnect_MissingConfigureQueues_Throws()
    {
        // When the user never calls ConfigureQueues, QueueName stays at its default
        // empty string. AddServiceConnect must surface an actionable error immediately
        // rather than failing with an opaque AMQP error at broker-connect time.
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddServiceConnect(b => { /* no ConfigureQueues */ }));

        Assert.Contains("QueueName", ex.Message);
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

    [Fact]
    public void AddServiceConnect_ThrowsWhenDirectDiSendMiddlewareIsTransient()
    {
        // Middleware registered directly via DI against the ISendMessageMiddleware interface
        // (rather than through AddSendMessageMiddleware<>) must also be rejected when non-singleton.
        // The pipeline caches instances at first use, so transient registrations would be silently
        // promoted to singleton lifetime, risking cross-request state leaks.
        var services = CreateServices();
        services.AddTransient<ISendMessageMiddleware, TestSendMiddleware>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddServiceConnect(b => b
                .ConfigureQueues(q => q.QueueName = "test")
                .ConfigureBus(c => c.ScanForMessageHandlers = false)));

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
    public Task HandleAsync(H5Msg message, IConsumeContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class H5Aggregator : Aggregator<H5Msg>
{
    public override Task ExecuteAsync(IReadOnlyList<H5Msg> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// Shared fixture providing <see cref="SampleMessage"/> and <see cref="SampleHandler"/> for
/// ServiceCollectionExtensions tests that scan the unit-test assembly.
/// </summary>
public static class TestHandlerFixture
{
    public sealed class SampleMessage : Message
    {
        public SampleMessage() : base(Guid.NewGuid()) { }
    }

    public sealed class SampleHandler : IMessageHandler<SampleMessage>
    {
        public Task HandleAsync(SampleMessage message, IConsumeContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

file sealed class TestInboundMiddleware : IMessageProcessingMiddleware
{
    public Task<ConsumeEventResult> ProcessAsync(
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
    public Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default) =>
        Task.FromResult(FilterAction.Continue);
}

file sealed class TestSendMiddleware : ISendMessageMiddleware
{
    public Task ProcessAsync(
        SendContext context,
        SendMessageDelegate next,
        CancellationToken cancellationToken) =>
        next(context, cancellationToken);
}

file sealed class OverrideRequestReplyManager : IRequestReplyManager
{
    public Task<TReply> SendRequestAsync<TRequest, TReply>(
        TRequest message,
        IDictionary<string, string> headers,
        ServiceConnect.Interfaces.Options.RequestOptions options,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message =>
        throw new NotSupportedException();

    public Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(
        TRequest message,
        IDictionary<string, string> headers,
        ServiceConnect.Interfaces.Options.RequestOptions options,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message =>
        throw new NotSupportedException();

    public Task PublishRequestAsync<TRequest, TReply>(
        TRequest message,
        IDictionary<string, string> headers,
        ServiceConnect.Interfaces.Options.RequestOptions options,
        Action<TReply> onReply,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message =>
        throw new NotSupportedException();

    public void ProcessReply(string messageId, ReadOnlyMemory<byte> messageBytes, Type type) =>
        throw new NotSupportedException();
}

file sealed class FullOverrideRequestReplyManager : IRequestReplyManager, IReplyStatusRequestReplyManager
{
    public Task<TReply> SendRequestAsync<TRequest, TReply>(
        TRequest message,
        IDictionary<string, string> headers,
        ServiceConnect.Interfaces.Options.RequestOptions options,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message =>
        throw new NotSupportedException();

    public Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(
        TRequest message,
        IDictionary<string, string> headers,
        ServiceConnect.Interfaces.Options.RequestOptions options,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message =>
        throw new NotSupportedException();

    public Task PublishRequestAsync<TRequest, TReply>(
        TRequest message,
        IDictionary<string, string> headers,
        ServiceConnect.Interfaces.Options.RequestOptions options,
        Action<TReply> onReply,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message =>
        throw new NotSupportedException();

    public void ProcessReply(string messageId, ReadOnlyMemory<byte> messageBytes, Type type) =>
        throw new NotSupportedException();

    public bool TryProcessReply(string messageId, ReadOnlyMemory<byte> messageBytes, Type type) =>
        throw new NotSupportedException();

    public bool IsTrackedRequest(string messageId) =>
        throw new NotSupportedException();
}

// --- Fixture types for multi-registration tests ---

public sealed class DualMsg : Message
{
    public DualMsg() : base(Guid.NewGuid()) { }
}

public sealed class DualData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
}

public sealed class DualInterfaceHandler
    : IMessageHandler<DualMsg>, IProcessHandler<DualData, DualMsg>
{
    public Task HandleAsync(DualMsg message, IConsumeContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task HandleAsync(DualMsg message, DualData data, IConsumeContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

public sealed class MultiMsg : Message
{
    public MultiMsg() : base(Guid.NewGuid()) { }
}

public sealed class MultiHandlerA : IMessageHandler<MultiMsg>
{
    public Task HandleAsync(MultiMsg message, IConsumeContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

public sealed class MultiHandlerB : IMessageHandler<MultiMsg>
{
    public Task HandleAsync(MultiMsg message, IConsumeContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

public sealed class PreRegMsg : Message
{
    public PreRegMsg() : base(Guid.NewGuid()) { }
}

public sealed class PreRegUserHandler : IMessageHandler<PreRegMsg>
{
    public Task HandleAsync(PreRegMsg message, IConsumeContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

public sealed class PreRegOtherHandler : IMessageHandler<PreRegMsg>
{
    public Task HandleAsync(PreRegMsg message, IConsumeContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
