using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Persistence.InMemory;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using ServiceConnect.UnitTests.Fakes;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class ProcessManagerProcessorTests
{
    private static readonly IBusConfiguration DefaultBusConfig = new BusConfiguration();
    private static readonly IQueueConfiguration DefaultQueueConfig = new QueueConfiguration
    {
        QueueName = "test-queue",
        ErrorQueueName = "errors",
        AuditQueueName = "audit"
    };

    private static (ServiceCollection services, Mock<IBus> mockBus, Mock<IProcessManagerFinder> mockFinder) CreateBaseServices()
    {
        var services = new ServiceCollection();
        var mockBus = new Mock<IBus>();
        var mockFinder = new Mock<IProcessManagerFinder>();
        services.AddSingleton(mockBus.Object);
        services.AddSingleton<IProcessManagerFinder>(mockFinder.Object);
        return (services, mockBus, mockFinder);
    }

    private static ProcessManagerHandlerRegistry BuildRegistry(params HandlerReference[] refs)
        => new([.. refs], NullLogger<ProcessManagerHandlerRegistry>.Instance);

    private static (ConsumeScopeAccessor accessor, IDisposable scope) BuildScopeAccessor(IServiceProvider provider)
    {
        var accessor = new ConsumeScopeAccessor();
        var scope = accessor.Push(provider);
        return (accessor, scope);
    }

    [Fact]
    public async Task ProcessAsync_NullMessage_ReturnsNotHandled()
    {
        var registry = BuildRegistry();
        var provider = new ServiceCollection().BuildServiceProvider();
        var (accessor, scopeHandle) = BuildScopeAccessor(provider);
        using var _scopePm = scopeHandle;
        var processor = new ProcessManagerProcessor(registry, accessor, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), null,
            new Dictionary<string, object>(), new Envelope());

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_NoDescriptor_ReturnsNotHandled()
    {
        var (services, _, _) = CreateBaseServices();
        var registry = BuildRegistry(); // empty
        var provider = services.BuildServiceProvider();
        var (accessor, scopeHandle) = BuildScopeAccessor(provider);
        using var _scopePm = scopeHandle;
        var processor = new ProcessManagerProcessor(registry, accessor, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        var msg = new PmTestMessage(Guid.NewGuid()) { Content = "x" };
        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg,
            new Dictionary<string, object>(), new Envelope());

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_NoFinder_ReturnsNotHandled()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IBus>().Object);
        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmTestMessage),
            HandlerType = typeof(PmTestHandler)
        });
        services.AddSingleton<IProcessHandler<PmTestData, PmTestMessage>>(new PmTestHandler());
        var provider = services.BuildServiceProvider();
        var (accessor, scopeHandle) = BuildScopeAccessor(provider);
        using var _scopePm = scopeHandle;
        var processor = new ProcessManagerProcessor(registry, accessor, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        var msg = new PmTestMessage(Guid.NewGuid()) { Content = "x" };
        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg,
            new Dictionary<string, object>(), new Envelope());

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_NoHandlerInDi_ReturnsNotHandled()
    {
        var (services, _, _) = CreateBaseServices();
        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmTestMessage),
            HandlerType = typeof(PmTestHandler)
        });
        var provider = services.BuildServiceProvider();
        var (accessor, scopeHandle) = BuildScopeAccessor(provider);
        using var _scopePm = scopeHandle;
        var processor = new ProcessManagerProcessor(registry, accessor, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        var msg = new PmTestMessage(Guid.NewGuid()) { Content = "x" };
        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg,
            new Dictionary<string, object>(), new Envelope());

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_NewData_InsertsAndReturnsHandled()
    {
        var (services, _, mockFinder) = CreateBaseServices();
        var handler = new PmTestHandler();
        services.AddSingleton<IProcessHandler<PmTestData, PmTestMessage>>(handler);

        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmTestMessage),
            HandlerType = typeof(PmTestHandler)
        });

        mockFinder.Setup(f => f.FindDataAsync<PmTestData>(
            It.IsAny<IProcessManagerPropertyMapper>(), It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IPersistenceData<PmTestData>?)null);

        var provider = services.BuildServiceProvider();
        var (accessor, scopeHandle) = BuildScopeAccessor(provider);
        using var _scopePm = scopeHandle;
        var processor = new ProcessManagerProcessor(registry, accessor, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        var correlationId = Guid.NewGuid();
        var msg = new PmTestMessage(correlationId) { Content = "test" };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg,
            new Dictionary<string, object>(), new Envelope());

        Assert.Equal(ProcessResult.Handled, result);
        Assert.True(handler.Invoked);
        mockFinder.Verify(f => f.InsertDataAsync(
            It.Is<IProcessManagerData>(d => d.CorrelationId == correlationId),
            It.IsAny<CancellationToken>()), Times.Once);
        mockFinder.Verify(f => f.UpdateDataAsync(
            It.IsAny<IPersistenceData<PmTestData>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_ExistingData_UpdatesAndReturnsHandled()
    {
        var (services, _, mockFinder) = CreateBaseServices();
        var handler = new PmTestHandler();
        services.AddSingleton<IProcessHandler<PmTestData, PmTestMessage>>(handler);

        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmTestMessage),
            HandlerType = typeof(PmTestHandler)
        });

        var existingData = new PmTestData { CorrelationId = Guid.NewGuid(), Counter = 5 };
        var persistence = new PmTestPersistenceData { Data = existingData };

        mockFinder.Setup(f => f.FindDataAsync<PmTestData>(
            It.IsAny<IProcessManagerPropertyMapper>(), It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(persistence);

        var provider = services.BuildServiceProvider();
        var (accessor, scopeHandle) = BuildScopeAccessor(provider);
        using var _scopePm = scopeHandle;
        var processor = new ProcessManagerProcessor(registry, accessor, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        var msg = new PmTestMessage(existingData.CorrelationId) { Content = "update" };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg,
            new Dictionary<string, object>(), new Envelope());

        Assert.Equal(ProcessResult.Handled, result);
        Assert.True(handler.Invoked);
        Assert.Equal(6, existingData.Counter);
        mockFinder.Verify(f => f.UpdateDataAsync(persistence, It.IsAny<CancellationToken>()), Times.Once);
        mockFinder.Verify(f => f.InsertDataAsync(It.IsAny<IProcessManagerData>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_OnConcurrencyException_HandlerInvokedOnceAndExceptionPropagates()
    {
        // On ConcurrencyException the handler must be invoked exactly once and
        // the exception must bubble up to the transport. Retrying inside the
        // processor would multiply every handler side-effect (HTTP calls,
        // bus.Send, logs), and retry cadence belongs to MessageRetryHandler —
        // not a hardcoded in-process schedule.
        var (services, _, mockFinder) = CreateBaseServices();
        var handler = new PmTestHandler();
        services.AddSingleton<IProcessHandler<PmTestData, PmTestMessage>>(handler);

        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmTestMessage),
            HandlerType = typeof(PmTestHandler)
        });

        var existingData = new PmTestData { CorrelationId = Guid.NewGuid(), Counter = 5 };
        var persistence = new PmTestPersistenceData { Data = existingData };

        mockFinder.Setup(f => f.FindDataAsync<PmTestData>(
            It.IsAny<IProcessManagerPropertyMapper>(), It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(persistence);
        mockFinder.Setup(f => f.UpdateDataAsync(It.IsAny<IPersistenceData<PmTestData>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceConnect.Interfaces.Exceptions.ConcurrencyException("stale version"));

        var provider = services.BuildServiceProvider();
        var (accessor, scopeHandle) = BuildScopeAccessor(provider);
        using var _scopePm = scopeHandle;
        var processor = new ProcessManagerProcessor(registry, accessor, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        var msg = new PmTestMessage(existingData.CorrelationId) { Content = "update" };

        await Assert.ThrowsAsync<ServiceConnect.Interfaces.Exceptions.ConcurrencyException>(() =>
            processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg,
                new Dictionary<string, object>(), new Envelope()));

        Assert.Equal(1, handler.InvokeCount);
        mockFinder.Verify(f => f.UpdateDataAsync(It.IsAny<IPersistenceData<PmTestData>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_TwoConcurrentDispatchesSameCorrelationId_SerializeFindHandleInsertUpdate()
    {
        // Two messages for the same saga arriving concurrently must be serialized through
        // the find→handle→persist cycle. Without per-correlation serialization both observe
        // FindData==null, both run user HandleAsync (with side effects), and both call
        // InsertDataAsync — the loser nacks on the unique CorrelationId index. With the
        // per-correlation lock, dispatch 2 waits until dispatch 1 commits, then sees the
        // just-inserted row and takes the update path. Result: exactly one Insert, exactly
        // one Update, two distinct handler invocations.
        var (services, _, mockFinder) = CreateBaseServices();
        var handler = new PmTestHandler();
        services.AddSingleton<IProcessHandler<PmTestData, PmTestMessage>>(handler);

        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmTestMessage),
            HandlerType = typeof(PmTestHandler)
        });

        // FindData behaviour: returns null until the first InsertData has run, then returns
        // the inserted row on every subsequent call. A simple flag (set by the InsertData
        // mock) flips behaviour atomically — no need for SetupSequence which couples ordering.
        IProcessManagerData? insertedRow = null;
        mockFinder.Setup(f => f.FindDataAsync<PmTestData>(
                It.IsAny<IProcessManagerPropertyMapper>(), It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => insertedRow is null
                ? null
                : new PmTestPersistenceData { Data = (PmTestData)insertedRow });

        // Gate the first InsertData. Dispatch 1 enters InsertData and parks; dispatch 2
        // arrives, takes the per-correlation lock wait, and is held until 1 completes.
        var insertEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInsert = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mockFinder.Setup(f => f.InsertDataAsync(It.IsAny<IProcessManagerData>(), It.IsAny<CancellationToken>()))
            .Returns(async (IProcessManagerData d, CancellationToken _) =>
            {
                insertEntered.TrySetResult();
                await releaseInsert.Task.ConfigureAwait(false);
                insertedRow = d;
            });
        mockFinder.Setup(f => f.UpdateDataAsync(It.IsAny<IPersistenceData<PmTestData>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var provider = services.BuildServiceProvider();
        var (accessor, scopeHandle) = BuildScopeAccessor(provider);
        using var _scopePm = scopeHandle;
        var processor = new ProcessManagerProcessor(registry, accessor, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        var correlationId = Guid.NewGuid();

        // Dispatch 1: enters first, parks inside InsertData.
        var dispatch1 = Task.Run(() => processor.ProcessAsync(
            new byte[] { 1 }, typeof(PmTestMessage),
            new PmTestMessage(correlationId) { Content = "first" },
            new Dictionary<string, object>(), new Envelope()));

        await insertEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Dispatch 2: starts now. The per-correlation lock blocks it until dispatch 1
        // releases. If the lock is missing (the bug), dispatch 2 also calls FindData
        // (which still returns null, since insertedRow is set only after the gate is
        // released) and then races into a second InsertData call.
        var dispatch2 = Task.Run(() => processor.ProcessAsync(
            new byte[] { 1 }, typeof(PmTestMessage),
            new PmTestMessage(correlationId) { Content = "second" },
            new Dictionary<string, object>(), new Envelope()));

        // Give dispatch 2 a chance to enter ProcessAsync and park on the lock. Without the
        // lock it would race ahead to FindData/InsertData and the test would still pass for
        // the wrong reason — so we briefly observe that it has NOT yet completed.
        await Task.Delay(100);
        Assert.False(dispatch2.IsCompleted, "Dispatch 2 should be blocked behind the per-correlation lock until dispatch 1 commits.");

        // Release dispatch 1.
        releaseInsert.SetResult();

        await Task.WhenAll(dispatch1, dispatch2).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, handler.InvokeCount);
        mockFinder.Verify(f => f.InsertDataAsync(It.IsAny<IProcessManagerData>(), It.IsAny<CancellationToken>()), Times.Once);
        mockFinder.Verify(f => f.UpdateDataAsync(It.IsAny<IPersistenceData<PmTestData>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_DistinctCorrelationIds_DoNotSerializeAgainstEachOther()
    {
        // Different correlation ids must not block each other — the per-correlation lock
        // is per-id, not a single global mutex.
        var (services, _, mockFinder) = CreateBaseServices();
        var handler = new PmTestHandler();
        services.AddSingleton<IProcessHandler<PmTestData, PmTestMessage>>(handler);

        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmTestMessage),
            HandlerType = typeof(PmTestHandler)
        });

        mockFinder.Setup(f => f.FindDataAsync<PmTestData>(
                It.IsAny<IProcessManagerPropertyMapper>(), It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IPersistenceData<PmTestData>?)null);

        // Both InsertData calls park until the test releases them; with two distinct ids
        // both should park simultaneously, proving they don't serialize.
        var bothInserting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int insertingCount = 0;
        mockFinder.Setup(f => f.InsertDataAsync(It.IsAny<IProcessManagerData>(), It.IsAny<CancellationToken>()))
            .Returns(async (IProcessManagerData d, CancellationToken _) =>
            {
                if (Interlocked.Increment(ref insertingCount) == 2)
                {
                    bothInserting.TrySetResult();
                }
                await release.Task.ConfigureAwait(false);
            });

        var provider = services.BuildServiceProvider();
        var (accessor, scopeHandle) = BuildScopeAccessor(provider);
        using var _scopePm = scopeHandle;
        var processor = new ProcessManagerProcessor(registry, accessor, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        var d1 = Task.Run(() => processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage),
            new PmTestMessage(Guid.NewGuid()), new Dictionary<string, object>(), new Envelope()));
        var d2 = Task.Run(() => processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage),
            new PmTestMessage(Guid.NewGuid()), new Dictionary<string, object>(), new Envelope()));

        // If the lock were global, only one would reach InsertData; bothInserting would
        // never fire and the WaitAsync would time out.
        await bothInserting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        release.SetResult();
        await Task.WhenAll(d1, d2).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ProcessAsync_CancelledToken_ThrowsOce()
    {
        var (services, _, _) = CreateBaseServices();
        var registry = BuildRegistry();
        var provider = services.BuildServiceProvider();
        var (accessor, scopeHandle) = BuildScopeAccessor(provider);
        using var _scopePm = scopeHandle;
        var processor = new ProcessManagerProcessor(registry, accessor, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), new PmTestMessage(Guid.NewGuid()),
                new Dictionary<string, object>(), new Envelope(), cts.Token));
    }

    [Fact]
    public async Task ProcessAsync_HandlerThrows_PersistsPartialStateBeforeRethrow()
    {
        // When the handler mutates `data` and then throws (e.g., a nested bus.Send
        // failure), the mutation must persist so the redelivery path resumes from the
        // mutated state rather than re-running the handler against the previously
        // committed snapshot. The original exception still propagates after the
        // best-effort persist.
        var (services, _, mockFinder) = CreateBaseServices();
        var handler = new PmThrowingHandler();
        services.AddSingleton<IProcessHandler<PmTestData, PmTestMessage>>(handler);

        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmTestMessage),
            HandlerType = typeof(PmThrowingHandler)
        });

        mockFinder.Setup(f => f.FindDataAsync<PmTestData>(
            It.IsAny<IProcessManagerPropertyMapper>(), It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IPersistenceData<PmTestData>?)null);

        var provider = services.BuildServiceProvider();
        var (accessor, scopeHandle) = BuildScopeAccessor(provider);
        using var _scopePm = scopeHandle;
        var processor = new ProcessManagerProcessor(registry, accessor, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        var msg = new PmTestMessage(Guid.NewGuid()) { Content = "will-throw" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg,
                new Dictionary<string, object>(), new Envelope()));

        // For a new saga (FindData==null), the partial-state persist takes the Insert
        // path so the redelivery sees a row instead of starting fresh.
        mockFinder.Verify(f => f.InsertDataAsync(
            It.IsAny<IProcessManagerData>(), It.IsAny<CancellationToken>()), Times.Once);
        mockFinder.Verify(f => f.UpdateDataAsync(
            It.IsAny<IPersistenceData<PmTestData>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_HandlerThrowsButPersistAlsoFails_RethrowsOriginalHandlerException()
    {
        // The catch is best-effort: a persist failure in the throwing path must not mask
        // the original handler exception. The persist failure is logged at Error.
        var (services, _, mockFinder) = CreateBaseServices();
        var handler = new PmThrowingHandler();
        services.AddSingleton<IProcessHandler<PmTestData, PmTestMessage>>(handler);

        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmTestMessage),
            HandlerType = typeof(PmThrowingHandler)
        });

        mockFinder.Setup(f => f.FindDataAsync<PmTestData>(
            It.IsAny<IProcessManagerPropertyMapper>(), It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IPersistenceData<PmTestData>?)null);
        mockFinder.Setup(f => f.InsertDataAsync(It.IsAny<IProcessManagerData>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("transient store failure"));

        var provider = services.BuildServiceProvider();
        var (accessor, scopeHandle) = BuildScopeAccessor(provider);
        using var _scopePm = scopeHandle;
        var processor = new ProcessManagerProcessor(registry, accessor, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage),
                new PmTestMessage(Guid.NewGuid()) { Content = "boom" },
                new Dictionary<string, object>(), new Envelope()));

        // The handler's InvalidOperationException must propagate, not the TimeoutException
        // from the failed best-effort persist.
        Assert.Equal("handler failure", thrown.Message);
    }

    [Fact]
    public async Task ProcessAsync_WhenHandlerMutatesAndThrows_PersistsMutationToInMemoryStore()
    {
        // Save-on-throw contract: mutations made by the handler before the throw are
        // persisted (best-effort) so the redelivery path resumes from the mutated
        // state. Without this, a handler that mutates `data.Counter++` then throws
        // would have the mutation discarded; on redelivery the handler runs against
        // the previously-committed Counter and the mutation is silently lost.
        var finder = new InMemoryProcessManagerFinder(new ProcessManagerPredicateCache(), new InMemoryPersistenceState(TimeProvider.System));
        var existing = new PmMutableData { CorrelationId = Guid.NewGuid(), Counter = 5 };
        await finder.InsertDataAsync(existing, CancellationToken.None);

        var services = new ServiceCollection();
        services.AddSingleton<IBus>(new Mock<IBus>().Object);
        services.AddSingleton<IProcessManagerFinder>(finder);
        services.AddSingleton<IProcessHandler<PmMutableData, PmMutableMessage>>(new PmMutatingThrowingHandler());

        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmMutableMessage),
            HandlerType = typeof(PmMutatingThrowingHandler)
        });
        var provider = services.BuildServiceProvider();
        var (accessor, scopeHandle) = BuildScopeAccessor(provider);
        using var _scopePm = scopeHandle;
        var processor = new ProcessManagerProcessor(registry, accessor, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessAsync(new byte[] { 1 }, typeof(PmMutableMessage), new PmMutableMessage(existing.CorrelationId),
                new Dictionary<string, object>(), new Envelope()));

        var mapper = new TestProcessManagerPropertyMapper();
        mapper.ConfigureMapping<PmMutableData, PmMutableMessage>(d => d.CorrelationId, m => m.CorrelationId);
        var reloaded = await finder.FindDataAsync<PmMutableData>(mapper, new PmMutableMessage(existing.CorrelationId), CancellationToken.None);

        Assert.NotNull(reloaded);
        // Handler ran Counter++ before throwing; the mutation must be visible after
        // the rethrow so redelivery resumes from the mutated state.
        Assert.Equal(6, reloaded!.Data.Counter);
    }

    [Fact]
    public async Task ProcessAsync_RunsConfigureMapperPerMessage()
    {
        DummyPmHandler.ResetCounter();

        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(DummyPmMessage),
            HandlerType = typeof(DummyPmHandler)
        });

        var services = new ServiceCollection();
        services.AddSingleton<IProcessManagerFinder>(new Mock<IProcessManagerFinder>().Object);
        services.AddSingleton<IProcessHandler<DummyPmData, DummyPmMessage>>(new DummyPmHandler());
        var provider = services.BuildServiceProvider();

        var (accessor, scopeHandle) = BuildScopeAccessor(provider);
        using var _scopePm = scopeHandle;
        var processor = new ProcessManagerProcessor(
            registry, accessor, new Lazy<IBus>(() => new Mock<IBus>().Object),
            NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig,
            new ConsumeContextPool(), new ConsumeContextAccessor());

        var message = new DummyPmMessage(Guid.NewGuid());
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = ReadOnlyMemory<byte>.Empty };

        const int messageCount = 16;
        var tasks = Enumerable.Range(0, messageCount)
            .Select(_ => processor.ProcessAsync(
                ReadOnlyMemory<byte>.Empty, typeof(DummyPmMessage), message, headers, envelope))
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(messageCount, DummyPmHandler.ConfigureCount);
    }

    [Fact]
    public async Task ProcessAsync_ResolvesHandlerAndFinderFromCurrentConsumeScope_NotRoot()
    {
        var rootHandler = new ScopeProbePmHandler();
        var scopedHandler = new ScopeProbePmHandler();
        var rootFinder = new ScopeProbePmFinder();
        var scopedFinder = new ScopeProbePmFinder();

        var scopedServices = new ServiceCollection();
        scopedServices.AddSingleton<IProcessManagerFinder>(scopedFinder);
        scopedServices.AddSingleton<IProcessHandler<ScopeProbePmData, ScopeProbePmMessage>>(scopedHandler);
        var scopedProvider = scopedServices.BuildServiceProvider();

        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(ScopeProbePmMessage),
            HandlerType = typeof(ScopeProbePmHandler)
        });

        var scopeAccessor = new ConsumeScopeAccessor();
        var processor = new ProcessManagerProcessor(
            registry, scopeAccessor, new Lazy<IBus>(() => new Mock<IBus>().Object),
            NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig,
            new ConsumeContextPool(), new ConsumeContextAccessor());

        using (scopeAccessor.Push(scopedProvider))
        {
            await processor.ProcessAsync(
                ReadOnlyMemory<byte>.Empty,
                typeof(ScopeProbePmMessage),
                new ScopeProbePmMessage(Guid.NewGuid()),
                new Dictionary<string, object>(),
                new Envelope { Headers = new Dictionary<string, object>(), Body = ReadOnlyMemory<byte>.Empty },
                CancellationToken.None);
        }

        Assert.Equal(0, rootHandler.Invocations);
        Assert.Equal(1, scopedHandler.Invocations);
        Assert.Equal(0, rootFinder.FindCount);
        Assert.Equal(1, scopedFinder.FindCount);
    }

    [Fact]
    public async Task ProcessAsync_ConsecutiveMessages_ConfigureMapperRunsPerMessage()
    {
        // Each delivery resolves a fresh handler instance, so per-instance state inside ConfigureMapper must be re-observed.
        var configureCount = 0;
        var probeHandler = new ScopeProbePmHandler(() => Interlocked.Increment(ref configureCount));

        var services = new ServiceCollection();
        services.AddSingleton<IProcessManagerFinder>(new ScopeProbePmFinder());
        services.AddSingleton<IProcessHandler<ScopeProbePmData, ScopeProbePmMessage>>(probeHandler);
        var provider = services.BuildServiceProvider();

        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(ScopeProbePmMessage),
            HandlerType = typeof(ScopeProbePmHandler)
        });

        var scopeAccessor = new ConsumeScopeAccessor();
        var processor = new ProcessManagerProcessor(
            registry, scopeAccessor, new Lazy<IBus>(() => new Mock<IBus>().Object),
            NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig,
            new ConsumeContextPool(), new ConsumeContextAccessor());

        using (scopeAccessor.Push(provider))
        {
            for (var i = 0; i < 3; i++)
            {
                await processor.ProcessAsync(
                    ReadOnlyMemory<byte>.Empty,
                    typeof(ScopeProbePmMessage),
                    new ScopeProbePmMessage(Guid.NewGuid()),
                    new Dictionary<string, object>(),
                    new Envelope { Headers = new Dictionary<string, object>(), Body = ReadOnlyMemory<byte>.Empty },
                    CancellationToken.None);
            }
        }

        Assert.Equal(3, configureCount);
    }

    [Fact]
    public async Task ProcessAsync_SetsAmbientConsumeHeadersDuringHandlerAndClearsThemAfterward()
    {
        var timeoutStore = new PmCapturingTimeoutStore();
        var consumeAccessor = new ConsumeContextAccessor();
        var bus = PmTestBusFactory.Create(DefaultQueueConfig, timeoutStore, consumeAccessor);

        var services = new ServiceCollection();
        var mockFinder = new Mock<IProcessManagerFinder>();
        services.AddSingleton<IBus>(bus);
        services.AddSingleton<IProcessManagerFinder>(mockFinder.Object);
        services.AddSingleton<IProcessHandler<PmTestData, PmTestMessage>>(new PmTimeoutRequestingHandler(bus));

        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmTestMessage),
            HandlerType = typeof(PmTimeoutRequestingHandler)
        });

        mockFinder.Setup(f => f.FindDataAsync<PmTestData>(
                It.IsAny<IProcessManagerPropertyMapper>(), It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IPersistenceData<PmTestData>?)null);

        var provider = services.BuildServiceProvider();
        var (scopeAccessor, scopeHandle) = BuildScopeAccessor(provider);
        using var _scopePm = scopeHandle;
        var processor = new ProcessManagerProcessor(registry, scopeAccessor, new Lazy<IBus>(() => bus), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), consumeAccessor);

        var correlationId = Guid.NewGuid();
        var headers = new Dictionary<string, object>
        {
            ["Custom"] = "value",
            [HeaderKeys.MessageId] = "managed-message-id"
        };

        await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), new PmTestMessage(correlationId), headers, new Envelope { Headers = headers, Body = new byte[] { 1 } });
        await bus.RequestTimeoutAsync(correlationId, TimeSpan.FromMinutes(2));

        Assert.Equal(2, timeoutStore.Inserted.Count);
        Assert.Equal("value", timeoutStore.Inserted[0].Headers["Custom"]);
        Assert.False(timeoutStore.Inserted[0].Headers.ContainsKey(HeaderKeys.MessageId));
        Assert.Empty(timeoutStore.Inserted[1].Headers);
    }
}

file class PmTestMessage(Guid correlationId) : Message(correlationId)
{
    public string Content { get; set; } = string.Empty;
}

file class PmTestData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
    public int Counter { get; set; }
}

file class PmTestPersistenceData : IPersistenceData<PmTestData>
{
    public PmTestData Data { get; set; } = new();
}

file class PmTestHandler : IProcessHandler<PmTestData, PmTestMessage>
{
    public bool Invoked { get; private set; }
    public int InvokeCount { get; private set; }

    public void ConfigureMapper(IProcessManagerPropertyMapper mapper) { }

    public Task HandleAsync(PmTestMessage message, PmTestData data, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        data.Counter++;
        Invoked = true;
        InvokeCount++;
        return Task.CompletedTask;
    }
}

file class PmThrowingHandler : IProcessHandler<PmTestData, PmTestMessage>
{
    public void ConfigureMapper(IProcessManagerPropertyMapper mapper) { }

    public Task HandleAsync(PmTestMessage message, PmTestData data, IConsumeContext context, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("handler failure");
}

file class PmMutableMessage(Guid correlationId) : Message(correlationId)
{
}

file class PmMutableData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
    public int Counter { get; set; }
}

file class PmMutatingThrowingHandler : IProcessHandler<PmMutableData, PmMutableMessage>
{
    public void ConfigureMapper(IProcessManagerPropertyMapper mapper)
        => mapper.ConfigureMapping<PmMutableData, PmMutableMessage>(d => d.CorrelationId, m => m.CorrelationId);

    public Task HandleAsync(PmMutableMessage message, PmMutableData data, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        data.Counter++;
        throw new InvalidOperationException("handler failure");
    }
}

file sealed class PmTimeoutRequestingHandler(IBus bus) : IProcessHandler<PmTestData, PmTestMessage>
{
    public void ConfigureMapper(IProcessManagerPropertyMapper mapper) { }

    public Task HandleAsync(PmTestMessage message, PmTestData data, IConsumeContext context, CancellationToken cancellationToken = default)
        => bus.RequestTimeoutAsync(message.CorrelationId, TimeSpan.FromMinutes(1));
}

file sealed class PmCapturingTimeoutStore : ITimeoutStore
{
    public List<TimeoutData> Inserted { get; } = [];

    public Task InsertTimeoutAsync(TimeoutData data, CancellationToken cancellationToken = default)
    {
        Inserted.Add(new TimeoutData
        {
            Id = data.Id,
            Destination = data.Destination,
            ProcessManagerId = data.ProcessManagerId,
            Time = data.Time,
            Headers = new Dictionary<string, object>(data.Headers)
        });
        return Task.CompletedTask;
    }

    public Task<TimeoutsBatch> GetTimeoutsBatchAsync(int? batchSize = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task RemoveDispatchedTimeoutAsync(Guid id, Guid? lockOwner = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task ReleaseDispatchedTimeoutAsync(Guid id, Guid? lockOwner = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

file static class PmTestBusFactory
{
    public static Bus Create(IQueueConfiguration queueConfiguration, ITimeoutStore timeoutStore, ConsumeContextAccessor accessor)
    {
        var serializer = new Mock<IMessageSerializer>();
        serializer.SetupSerializeAny<PmTestMessage>([1]);

        var filterPipeline = new Mock<IFilterPipeline>();
        var sendPipeline = new Mock<ISendMessagePipeline>();
        var requestReplyManager = new Mock<IRequestReplyManager>();
        var logger = new Mock<ILogger<Bus>>();
        var dispatcher = new Mock<IMessageDispatcher>();
        var pipelineConfiguration = new Mock<IPipelineConfiguration>();
        pipelineConfiguration.Setup(x => x.OutgoingFilters).Returns([]);

        var rootProvider = new ServiceCollection().BuildServiceProvider();
        return new Bus(
            serializer.Object,
            filterPipeline.Object,
            sendPipeline.Object,
            requestReplyManager.Object,
            logger.Object,
            queueConfiguration,
            dispatcher.Object,
            [],
            pipelineConfiguration.Object,
            rootProvider.GetRequiredService<IServiceScopeFactory>(),
            new ConsumeScopeAccessor(),
            timeoutStore: timeoutStore,
            consumeContextAccessor: accessor);
    }
}

// Fixtures used only by ProcessAsync_RunsConfigureMapperPerMessage.
file class DummyPmMessage(Guid correlationId) : Message(correlationId)
{
}

file class DummyPmData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
}

file class DummyPmHandler : IProcessHandler<DummyPmData, DummyPmMessage>
{
    private static int _configureCount;

    public static int ConfigureCount => _configureCount;

    public static void ResetCounter() => Interlocked.Exchange(ref _configureCount, 0);

    public void ConfigureMapper(IProcessManagerPropertyMapper mapper)
    {
        Interlocked.Increment(ref _configureCount);
        mapper.ConfigureMapping<DummyPmData, DummyPmMessage>(d => d.CorrelationId, m => m.CorrelationId);
    }

    public Task HandleAsync(DummyPmMessage message, DummyPmData data, IConsumeContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

file sealed class ScopeProbePmMessage(Guid correlationId) : Message(correlationId);

file sealed class ScopeProbePmData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
}

file sealed class ScopeProbePmHandler(Action? onConfigureMapper = null)
    : IProcessHandler<ScopeProbePmData, ScopeProbePmMessage>
{
    private int _invocations;
    public int Invocations => Volatile.Read(ref _invocations);

    public void ConfigureMapper(IProcessManagerPropertyMapper mapper)
    {
        onConfigureMapper?.Invoke();
        mapper.ConfigureMapping<ScopeProbePmData, ScopeProbePmMessage>(d => d.CorrelationId, m => m.CorrelationId);
    }

    public Task HandleAsync(ScopeProbePmMessage message, ScopeProbePmData data, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _invocations);
        return Task.CompletedTask;
    }
}

file sealed class ScopeProbePmFinder : IProcessManagerFinder
{
    private int _findCount;
    public int FindCount => Volatile.Read(ref _findCount);

    public Task<IPersistenceData<TData>?> FindDataAsync<TData>(
        IProcessManagerPropertyMapper mapper, Message message, CancellationToken cancellationToken = default)
        where TData : class, IProcessManagerData
    {
        Interlocked.Increment(ref _findCount);
        return Task.FromResult<IPersistenceData<TData>?>(null);
    }

    public Task InsertDataAsync(IProcessManagerData data, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task UpdateDataAsync<TData>(IPersistenceData<TData> persistenceData, CancellationToken cancellationToken = default)
        where TData : class, IProcessManagerData
        => Task.CompletedTask;

    public Task DeleteDataAsync<TData>(IPersistenceData<TData> persistenceData, CancellationToken cancellationToken = default)
        where TData : class, IProcessManagerData
        => Task.CompletedTask;
}
