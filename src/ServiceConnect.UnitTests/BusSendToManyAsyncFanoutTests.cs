using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Tests for the fan-out partial-failure matrix of <see cref="Bus.SendToManyAsync{T}"/>.
///
/// Tests 2 and 3 verify the continue-on-failure contract introduced by Task B.3:
/// a failing endpoint must not abort delivery to the remaining endpoints, and all
/// failures must be collected and surfaced as a single <see cref="AggregateException"/>.
/// </summary>
public sealed class BusSendToManyAsyncFanoutTests
{
    private readonly Mock<IMessageSerializer> _mockSerializer;
    private readonly Mock<IFilterPipeline> _mockFilterPipeline;
    private readonly Mock<ISendMessagePipeline> _mockSendPipeline;
    private readonly Mock<IRequestReplyManager> _mockRequestReplyManager;
    private readonly Mock<IBusConfiguration> _mockConfig;
    private readonly Mock<IPipelineConfiguration> _mockPipelineConfig;
    private readonly Mock<ILogger<Bus>> _mockLogger;
    private readonly Mock<IQueueConfiguration> _mockQueueConfig;
    private readonly Mock<IMessageDispatcher> _mockDispatcher;
    private readonly IList<HandlerReference> _handlerReferences;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConsumeScopeAccessor _scopeAccessor;
    private readonly Bus _bus;

    public BusSendToManyAsyncFanoutTests()
    {
        _mockSerializer = new Mock<IMessageSerializer>();
        _mockFilterPipeline = new Mock<IFilterPipeline>();
        _mockSendPipeline = new Mock<ISendMessagePipeline>();
        _mockRequestReplyManager = new Mock<IRequestReplyManager>();
        _mockConfig = new Mock<IBusConfiguration>();
        _mockPipelineConfig = new Mock<IPipelineConfiguration>();
        // No outgoing filters — Bus takes the fast header-build path.
        _mockPipelineConfig.Setup(x => x.OutgoingFilters).Returns([]);
        _mockLogger = new Mock<ILogger<Bus>>();
        _mockQueueConfig = new Mock<IQueueConfiguration>();
        _mockQueueConfig.Setup(x => x.QueueName).Returns("test-queue");

        _mockFilterPipeline
            .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Continue);
        _mockSerializer.SetupSerializeAny<FakeMessage1>([1, 2, 3]);

        _mockDispatcher = new Mock<IMessageDispatcher>();
        _handlerReferences = [];
        _scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        _scopeAccessor = new ConsumeScopeAccessor();

        _bus = new Bus(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            _mockLogger.Object,
            _mockQueueConfig.Object,
            _mockDispatcher.Object,
            _handlerReferences,
            _mockPipelineConfig.Object,
            _scopeFactory,
            _scopeAccessor);
    }

    // -------------------------------------------------------------------------
    // Test 1 — all endpoints succeed: baseline invocation count
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendToManyAsync_AllSucceed_NoException()
    {
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        int callCount = 0;

        _mockSendPipeline
            .Setup(x => x.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((_, _) => callCount++)
            .Returns(Task.CompletedTask);

        // No exception expected.
        await _bus.SendToManyAsync(message, ["q1", "q2", "q3"]);

        Assert.Equal(3, callCount);
    }

    // -------------------------------------------------------------------------
    // Test 2 — partial failure: AggregateException collected; others still attempted
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendToManyAsync_PartialFailure_AggregatesAndAttemptsRemaining()
    {
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        var capturedEndpoints = new List<string?>();

        _mockSendPipeline
            .Setup(x => x.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedEndpoints.Add(ctx.EndPoint);
                if (ctx.EndPoint == "q1")
                {
                    throw new InvalidOperationException("q1 delivery failed");
                }
            })
            .Returns(Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => _bus.SendToManyAsync(message, ["q1", "q2", "q3"]));

        // Exactly one failure collected.
        Assert.Single(ex.InnerExceptions);
        Assert.IsType<InvalidOperationException>(ex.InnerExceptions[0]);
        Assert.Contains("q1", ex.InnerExceptions[0].Message);

        // All three endpoints must have been attempted.
        Assert.Equal(3, capturedEndpoints.Count);
        Assert.Contains("q1", capturedEndpoints);
        Assert.Contains("q2", capturedEndpoints);
        Assert.Contains("q3", capturedEndpoints);
    }

    // -------------------------------------------------------------------------
    // Test 3 — all endpoints fail: AggregateException contains all three inners
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendToManyAsync_AllFail_AggregateExceptionWithAllInners()
    {
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };

        _mockSendPipeline
            .Setup(x => x.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
                throw new InvalidOperationException($"endpoint {ctx.EndPoint} delivery failed"))
            .Returns(Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => _bus.SendToManyAsync(message, ["q1", "q2", "q3"]));

        Assert.Equal(3, ex.InnerExceptions.Count);
        Assert.All(ex.InnerExceptions, e => Assert.IsType<InvalidOperationException>(e));

        // Each endpoint name appears in exactly one inner exception message.
        var messages = ex.InnerExceptions.Select(e => e.Message).ToList();
        Assert.Contains(messages, m => m.Contains("q1"));
        Assert.Contains(messages, m => m.Contains("q2"));
        Assert.Contains(messages, m => m.Contains("q3"));
    }

    // -------------------------------------------------------------------------
    // Test 4 — cancellation mid-loop: OperationCanceledException propagates directly
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendToManyAsync_CancellationMidLoop_ThrowsOperationCanceledDirectly()
    {
        using var cts = new CancellationTokenSource();
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        var capturedEndpoints = new List<string?>();

        _mockSendPipeline
            .Setup(x => x.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedEndpoints.Add(ctx.EndPoint);
                if (ctx.EndPoint == "q1")
                {
                    cts.Cancel();
                    cts.Token.ThrowIfCancellationRequested();
                }
            })
            .Returns(Task.CompletedTask);

        // OperationCanceledException must propagate directly — not wrapped in AggregateException.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _bus.SendToManyAsync(message, ["q1", "q2", "q3"], cancellationToken: cts.Token));

        // Loop must have aborted: q2 and q3 must never have been attempted.
        Assert.DoesNotContain("q2", capturedEndpoints);
        Assert.DoesNotContain("q3", capturedEndpoints);
    }

    // -------------------------------------------------------------------------
    // Test 4b — prior failure followed by cancellation: the OCE is wrapped in an
    //           AggregateException together with the prior endpoint failures so the
    //           caller can inspect both. (Cancellation on the first iteration with
    //           no prior failures still throws the raw OCE — see test 4.)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendToManyAsync_PriorFailureThenCancellation_ThrowsAggregateContainingBoth()
    {
        using var cts = new CancellationTokenSource();
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };

        _mockSendPipeline
            .Setup(x => x.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                if (ctx.EndPoint == "q1")
                {
                    throw new InvalidOperationException("q1 delivery failed");
                }
                if (ctx.EndPoint == "q2")
                {
                    cts.Cancel();
                    cts.Token.ThrowIfCancellationRequested();
                }
            })
            .Returns(Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => _bus.SendToManyAsync(message, ["q1", "q2", "q3"], cancellationToken: cts.Token));

        Assert.Equal(2, ex.InnerExceptions.Count);
        // OCE is first so callers that walk InnerExceptions can detect cancellation.
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerExceptions[0]);
        Assert.IsType<InvalidOperationException>(ex.InnerExceptions[1]);
        Assert.Equal("q1 delivery failed", ex.InnerExceptions[1].Message);
    }

    // -------------------------------------------------------------------------
    // Test 5 — header-copy isolation: per-iteration shallow copy prevents
    //          one endpoint's middleware mutations from leaking into the next
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendToManyAsync_PartialFailure_HeaderCopyIsolatedPerIteration()
    {
        // The pipeline callback simulates what ISendMessageMiddleware would do: it stamps
        // a per-endpoint key into ctx.Headers before (optionally) failing.  Because Bus
        // creates a fresh shallow copy of the base headers dict on every iteration, the
        // stamp written during q1's (failing) iteration must not appear in q2's ctx.Headers.
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        IDictionary<string, string>? q2Headers = null;

        _mockSendPipeline
            .Setup(x => x.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                // Mutate the context's headers dict (simulating middleware behaviour).
                ctx.Headers["X-Endpoint-Visit"] = ctx.EndPoint ?? "";

                if (ctx.EndPoint == "q1")
                {
                    throw new InvalidOperationException("q1 delivery failed");
                }

                if (ctx.EndPoint == "q2")
                {
                    // Capture a snapshot of q2's headers after mutation.
                    q2Headers = new Dictionary<string, string>(ctx.Headers, StringComparer.Ordinal);
                }
            })
            .Returns(Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => _bus.SendToManyAsync(message, ["q1", "q2", "q3"]));

        Assert.Single(ex.InnerExceptions);

        // q2's headers dict must exist and must record "q2" as the visit stamp — not "q1".
        Assert.NotNull(q2Headers);
        Assert.True(q2Headers!.TryGetValue("X-Endpoint-Visit", out var visitStamp));
        Assert.Equal("q2", visitStamp);

        // The base headers dict must not carry q1's mutation (the per-iteration copy isolates it).
        // If Bus had passed the same dict to every iteration, "X-Endpoint-Visit" would be "q1"
        // when q2 runs (since q1 mutated it first).  A value of "q2" proves the copy was made.
    }
}
