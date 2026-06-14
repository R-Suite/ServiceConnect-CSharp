using System.Reflection;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class ProcessManagerTimeoutServiceTests
{
    private readonly Mock<IBusConfiguration> _mockConfig = new();
    private readonly Mock<ITimeoutStore> _mockFinder = new();
    private readonly Mock<IBus> _mockBus = new();
    private readonly ILogger<ProcessManagerTimeoutService> _logger =
        new Mock<ILogger<ProcessManagerTimeoutService>>().Object;

    private ProcessManagerTimeoutService CreateSut(ITimeoutStore? finder = null) =>
        new(_mockConfig.Object, new Lazy<IBus>(() => _mockBus.Object), finder, _logger);

    [Fact]
    public async Task StartAsync_TimeoutsDisabled_DoesNotPoll()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(false);
        var sut = CreateSut(_mockFinder.Object);

        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        _mockFinder.Verify(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_NoFinderRegistered_DoesNotThrow()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);
        var sut = CreateSut();

        var exception = await Record.ExceptionAsync(async () =>
        {
            await sut.StartAsync(CancellationToken.None);
            await Task.Delay(50);
            await sut.StopAsync(CancellationToken.None);
        });

        Assert.Null(exception);
    }

    [Fact]
    public async Task PollOnce_DueTimeouts_RemovesDispatched()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var timeoutId = Guid.NewGuid();
        var batch = new TimeoutsBatch
        {
            DueTimeouts =
            [
                new TimeoutData
                {
                    Id = timeoutId,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "test-queue",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>(),
                    Locked = false
                }
            ],
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.Is<SendOptions>(options => options.EndPoint == "test-queue"),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut(_mockFinder.Object);

        await sut.PollOnceAsync();

        _mockBus.Verify(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.Is<SendOptions>(options => options.EndPoint == "test-queue"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _mockFinder.Verify(f => f.RemoveDispatchedTimeoutAsync(timeoutId, (Guid?)null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PollOnce_IncludesStoredHeaders_WhenDispatchingTimeout()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var timeoutId = Guid.NewGuid();
        var batch = new TimeoutsBatch
        {
            DueTimeouts =
            [
                new TimeoutData
                {
                    Id = timeoutId,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "test-queue",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object> { ["X-Custom-Header"] = "value" }
                }
            ],
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.IsAny<SendOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut(_mockFinder.Object);

        await sut.PollOnceAsync();

        _mockBus.Verify(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.Is<SendOptions>(options =>
                    options.Headers != null &&
                    options.Headers["X-Custom-Header"] == "value"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PollOnce_DoesNotForwardReservedTransportHeaders_WhenDispatchingTimeout()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var reservedHeaders = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = "spoofed-message-type",
            [HeaderKeys.TypeName] = "spoofed-type-name",
            [HeaderKeys.FullTypeName] = "spoofed-full-type-name",
            [HeaderKeys.MessageId] = "spoofed-message-id",
            [HeaderKeys.DestinationAddress] = "spoofed-destination",
            [HeaderKeys.SourceAddress] = "spoofed-source",
            [HeaderKeys.RequestMessageId] = "spoofed-request-message-id",
            [HeaderKeys.ResponseMessageId] = "spoofed-response-message-id",
            [HeaderKeys.RoutingKey] = "spoofed-routing-key",
            [HeaderKeys.RoutingSlip] = "spoofed-routing-slip",
            [HeaderKeys.Publish] = "spoofed-publish",
            [HeaderKeys.SequenceId] = "spoofed-sequence-id",
            [HeaderKeys.PacketNumber] = "spoofed-packet-number",
            [HeaderKeys.LastPacketNumber] = "spoofed-last-packet-number",
            [HeaderKeys.ByteStream] = "spoofed-byte-stream",
            [HeaderKeys.TimeSent] = "spoofed-time-sent",
            [HeaderKeys.TimeReceived] = "spoofed-time-received",
            [HeaderKeys.TimeProcessed] = "spoofed-time-processed",
            [HeaderKeys.SourceMachine] = "spoofed-source-machine",
            [HeaderKeys.DestinationMachine] = "spoofed-destination-machine",
            [HeaderKeys.Redelivered] = "spoofed-redelivered",
            [HeaderKeys.ConsumerType] = "spoofed-consumer-type",
            [HeaderKeys.Language] = "spoofed-language",
            [HeaderKeys.Exception] = "spoofed-exception",
            [HeaderKeys.RetryCount] = "spoofed-retry-count",
            [HeaderKeys.CorrelationId] = "spoofed-correlation-id",
            [HeaderKeys.Priority] = "spoofed-priority"
        };

        var timeoutId = Guid.NewGuid();
        var batch = new TimeoutsBatch
        {
            DueTimeouts =
            [
                new TimeoutData
                {
                    Id = timeoutId,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "test-queue",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>(reservedHeaders)
                    {
                        ["X-Custom-Header"] = "value"
                    }
                }
            ],
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        SendOptions? dispatchedOptions = null;
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.IsAny<SendOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<TimeoutMessage, SendOptions?, CancellationToken>((_, options, _) => dispatchedOptions = options)
            .Returns(Task.CompletedTask);

        var sut = CreateSut(_mockFinder.Object);

        await sut.PollOnceAsync();

        _mockBus.Verify(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.IsAny<SendOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        var sendOptions = Assert.IsType<SendOptions>(dispatchedOptions);
        var outgoingHeaders = Assert.IsAssignableFrom<IDictionary<string, string>>(sendOptions.Headers);
        Assert.Equal("value", outgoingHeaders["X-Custom-Header"]);

        foreach (var reservedHeader in reservedHeaders.Keys)
        {
            Assert.DoesNotContain(reservedHeader, outgoingHeaders.Keys);
        }
    }

    [Fact]
    public async Task PollOnce_IgnoresUnsupportedStoredHeaderValues_WhenDispatchingTimeout()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var timeoutId = Guid.NewGuid();
        var batch = new TimeoutsBatch
        {
            DueTimeouts =
            [
                new TimeoutData
                {
                    Id = timeoutId,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "test-queue",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>
                    {
                        ["X-Custom-Header"] = "value",
                        ["Unsupported"] = new object()
                    }
                }
            ],
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.IsAny<SendOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut(_mockFinder.Object);

        await sut.PollOnceAsync();

        _mockBus.Verify(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.Is<SendOptions>(options =>
                    options.Headers != null &&
                    options.Headers["X-Custom-Header"] == "value" &&
                    !options.Headers.ContainsKey("Unsupported")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PollOnce_SendFails_ReleasesTimeoutForRetry()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var timeoutId = Guid.NewGuid();
        var batch = new TimeoutsBatch
        {
            DueTimeouts =
            [
                new TimeoutData
                {
                    Id = timeoutId,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "test-queue",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>(),
                    Locked = true,
                    LockedBy = Guid.NewGuid(),
                    LockExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
                }
            ],
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.Is<SendOptions>(options => options.EndPoint == "test-queue"),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var sut = CreateSut(_mockFinder.Object);

        await sut.PollOnceAsync();

        _mockFinder.Verify(f => f.ReleaseDispatchedTimeoutAsync(timeoutId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockFinder.Verify(f => f.RemoveDispatchedTimeoutAsync(timeoutId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_WithLockOwner_PassesOwnerToRemove()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var timeoutId = Guid.NewGuid();
        var lockOwner = Guid.NewGuid();
        var store = new Mock<ITimeoutStore>();
        var batch = new TimeoutsBatch
        {
            DueTimeouts =
            [
                new TimeoutData
                {
                    Id = timeoutId,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "test-queue",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>(),
                    Locked = true,
                    LockedBy = lockOwner,
                    LockExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
                }
            ],
        };

        store.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        store.Setup(f => f.RemoveDispatchedTimeoutAsync(timeoutId, (Guid?)lockOwner, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.Is<SendOptions>(options => options.EndPoint == "test-queue"),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut(store.Object);

        await sut.PollOnceAsync();

        store.Verify(f => f.RemoveDispatchedTimeoutAsync(timeoutId, (Guid?)lockOwner, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(f => f.RemoveDispatchedTimeoutAsync(timeoutId, (Guid?)null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_WithoutLockOwner_PassesNullToRemove()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var timeoutId = Guid.NewGuid();
        var store = new Mock<ITimeoutStore>();
        var batch = new TimeoutsBatch
        {
            DueTimeouts =
            [
                new TimeoutData
                {
                    Id = timeoutId,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "test-queue",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>(),
                    Locked = true,
                    LockedBy = Guid.Empty,
                    LockExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
                }
            ],
        };

        store.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        store.Setup(f => f.RemoveDispatchedTimeoutAsync(timeoutId, (Guid?)null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.Is<SendOptions>(options => options.EndPoint == "test-queue"),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut(store.Object);

        await sut.PollOnceAsync();

        store.Verify(f => f.RemoveDispatchedTimeoutAsync(timeoutId, (Guid?)null, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(f => f.RemoveDispatchedTimeoutAsync(timeoutId, It.Is<Guid?>(g => g.HasValue), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_SendFails_WithLockOwner_PassesOwnerToRelease()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var timeoutId = Guid.NewGuid();
        var lockOwner = Guid.NewGuid();
        var store = new Mock<ITimeoutStore>();
        var batch = new TimeoutsBatch
        {
            DueTimeouts =
            [
                new TimeoutData
                {
                    Id = timeoutId,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "test-queue",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>(),
                    Locked = true,
                    LockedBy = lockOwner,
                    LockExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
                }
            ],
        };

        store.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        store.Setup(f => f.ReleaseDispatchedTimeoutAsync(timeoutId, (Guid?)lockOwner, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.Is<SendOptions>(options => options.EndPoint == "test-queue"),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var sut = CreateSut(store.Object);

        await sut.PollOnceAsync();

        store.Verify(f => f.ReleaseDispatchedTimeoutAsync(timeoutId, (Guid?)lockOwner, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(f => f.ReleaseDispatchedTimeoutAsync(timeoutId, (Guid?)null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StopAsync_DisposesAndClearsCancellationSource()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);
        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TimeoutsBatch { DueTimeouts = [] });

        var sut = CreateSut(_mockFinder.Object);

        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        var ctsField = typeof(ProcessManagerTimeoutService)
            .GetField("_stoppingCts", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(ctsField);
        Assert.Null(ctsField!.GetValue(sut));
    }

    [Fact]
    public async Task PollOnce_RemoveDispatchedThrowsForeignOCE_DoesNotPropagateAndDoesNotRelease()
    {
        // A foreign OCE (one that does not share the loop cancellation token) thrown by
        // RemoveDispatchedTimeoutAsync must NOT propagate out of PollOnceAsync. Bubbling
        // it up would let PollLoop's catch break without logging, silently ending the
        // polling task. The foreign OCE is caught by the per-timeout
        // `when (ex is not OperationCanceledException)` filter (so Release isn't attempted —
        // the inner catch only handles non-OCE failures), then by the outer foreign-OCE
        // catch in PollOnceAsync which logs a warning and returns cleanly.
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var timeoutId = Guid.NewGuid();
        var batch = new TimeoutsBatch
        {
            DueTimeouts =
            [
                new TimeoutData
                {
                    Id = timeoutId,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "test-queue",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>(),
                }
            ],
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.IsAny<SendOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockFinder.Setup(f => f.RemoveDispatchedTimeoutAsync(timeoutId, (Guid?)null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = CreateSut(_mockFinder.Object);

        // Foreign OCE — outer token is default(CancellationToken) (never cancelled); the OCE
        // is therefore not the shutdown OCE and must not propagate.
        var ex = await Record.ExceptionAsync(() => sut.PollOnceAsync());
        Assert.Null(ex);

        // The per-timeout catch filters OCEs out (only non-OCE failures trigger Release), so
        // ReleaseDispatchedTimeoutAsync is not called.
        _mockFinder.Verify(f => f.ReleaseDispatchedTimeoutAsync(timeoutId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_WhenSendSucceeds_PropagatesLifecycleTokenToRemove()
    {
        // The lifecycle token must be propagated to ReleaseDispatchedTimeoutAsync — it may
        // already be cancelled if the token was signalled during SendAsync, but it is the
        // same token supplied to PollOnceAsync. Passing CancellationToken.None instead would
        // let release work continue past a shutdown signal.
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var timeoutId = Guid.NewGuid();
        var batch = new TimeoutsBatch
        {
            DueTimeouts =
            [
                new TimeoutData
                {
                    Id = timeoutId,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "test-queue",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>()
                }
            ],
        };

        using var cts = new CancellationTokenSource();
        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.IsAny<SendOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        CancellationToken removeToken = default;
        _mockFinder.Setup(f => f.RemoveDispatchedTimeoutAsync(timeoutId, (Guid?)null, It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid?, CancellationToken>((_, _, token) => removeToken = token)
            .Returns(Task.CompletedTask);

        var sut = CreateSut(_mockFinder.Object);

        await sut.PollOnceAsync(cts.Token);

        // The token passed to Remove must be the lifecycle token, not CancellationToken.None.
        Assert.Equal(cts.Token, removeToken);
    }

    [Fact]
    public async Task PollOnce_ReleaseDispatchedThrowsForeignOCE_DoesNotPropagate()
    {
        // Same contract as the Remove counterpart but on the Release path: SendAsync
        // fails, then ReleaseDispatchedTimeoutAsync throws a foreign OCE during error
        // recovery. Letting that OCE escape would hit PollLoop's break-without-logging
        // branch; the outer foreign-OCE catch in PollOnceAsync logs a warning and
        // returns cleanly.
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var timeoutId = Guid.NewGuid();
        var batch = new TimeoutsBatch
        {
            DueTimeouts =
            [
                new TimeoutData
                {
                    Id = timeoutId,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "test-queue",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>(),
                }
            ],
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.IsAny<SendOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        _mockFinder.Setup(f => f.ReleaseDispatchedTimeoutAsync(timeoutId, (Guid?)null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = CreateSut(_mockFinder.Object);

        var ex = await Record.ExceptionAsync(() => sut.PollOnceAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task PollOnceAsync_RemoveDispatchedTimeout_ReceivesPropagatedCancellationToken()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var timeoutId = Guid.NewGuid();
        var batch = new TimeoutsBatch
        {
            DueTimeouts =
            [
                new TimeoutData
                {
                    Id = timeoutId,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "test-queue",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>(),
                    Locked = false
                }
            ],
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.IsAny<SendOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        CancellationToken? observedRemoveToken = null;
        _mockFinder
            .Setup(f => f.RemoveDispatchedTimeoutAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid?, CancellationToken>((_, _, ct) => observedRemoveToken = ct)
            .Returns(Task.CompletedTask);

        var sut = CreateSut(_mockFinder.Object);

        using var cts = new CancellationTokenSource();

        await sut.PollOnceAsync(cts.Token);

        Assert.NotNull(observedRemoveToken);
        Assert.Equal(cts.Token, observedRemoveToken.Value);
    }

    [Fact]
    public async Task PollOnce_RemoveFailsTransiently_ReturnsSentCountNotZero()
    {
        // When RemoveDispatchedTimeoutAsync throws on every item, PollOnceAsync must still
        // return sentCount (the number of successful sends) so the catch-up loop in PollLoop
        // can continue draining the backlog at full rate instead of exiting after one batch.
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var timeoutId = Guid.NewGuid();
        var batch = new TimeoutsBatch
        {
            DueTimeouts =
            [
                new TimeoutData
                {
                    Id = timeoutId,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "test-queue",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>()
                }
            ],
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(batch);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.IsAny<SendOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockFinder.Setup(f => f.RemoveDispatchedTimeoutAsync(timeoutId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store unavailable"));

        var sut = CreateSut(_mockFinder.Object);

        var result = await sut.PollOnceAsync();

        // Sent one item successfully; result must reflect the send count, not zero.
        Assert.Equal(1, result);
        // Send was called — message was delivered.
        _mockBus.Verify(b => b.SendAsync(
            It.IsAny<TimeoutMessage>(), It.IsAny<SendOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // Remove was attempted.
        _mockFinder.Verify(f => f.RemoveDispatchedTimeoutAsync(timeoutId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // Release must NOT be called — the message was sent; this is not a send failure.
        _mockFinder.Verify(f => f.ReleaseDispatchedTimeoutAsync(timeoutId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PollOnce_RemoveFailsTransiently_LogsWarningPerItem()
    {
        // A warning must be emitted for each remove failure so the operator can observe
        // store degradation without the loop silently slowing down.
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var loggerMock = new Mock<ILogger<ProcessManagerTimeoutService>>();
        var timeoutId1 = Guid.NewGuid();
        var timeoutId2 = Guid.NewGuid();
        var batch = new TimeoutsBatch
        {
            DueTimeouts =
            [
                new TimeoutData
                {
                    Id = timeoutId1,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "q",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>()
                },
                new TimeoutData
                {
                    Id = timeoutId2,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "q",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>()
                }
            ],
        };

        var store = new Mock<ITimeoutStore>();
        store.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(batch);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.IsAny<SendOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        store.Setup(f => f.RemoveDispatchedTimeoutAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store unavailable"));

        var sut = new ProcessManagerTimeoutService(
            _mockConfig.Object,
            new Lazy<IBus>(() => _mockBus.Object),
            store.Object,
            loggerMock.Object);

        var result = await sut.PollOnceAsync();

        // Both items were sent — result is 2.
        Assert.Equal(2, result);
        // One warning logged per remove failure.
        loggerMock.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<InvalidOperationException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task PollOnce_EmptyDestinationRow_DoesNotIncrementSentCount()
    {
        // A row with an empty Destination has no message to send, so it must not count
        // toward sentCount and must not drive the catch-up loop forward. The row is still
        // removed below, but send count stays at zero.
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var timeoutId = Guid.NewGuid();
        var batch = new TimeoutsBatch
        {
            DueTimeouts =
            [
                new TimeoutData
                {
                    Id = timeoutId,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>(),
                    Locked = false
                }
            ],
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(batch);
        _mockFinder.Setup(f => f.RemoveDispatchedTimeoutAsync(timeoutId, (Guid?)null, It.IsAny<CancellationToken>()))
                   .Returns(Task.CompletedTask);

        var sut = CreateSut(_mockFinder.Object);

        var result = await sut.PollOnceAsync();

        // No send was made for an empty-destination row.
        Assert.Equal(0, result);
        _mockBus.Verify(b => b.SendAsync(
            It.IsAny<TimeoutMessage>(), It.IsAny<SendOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StopAsyncAndDisposeAsync_ConcurrentlyRaced_DoesNotDoubleDisposeCts()
    {
        // Both StopAsync and DisposeAsync take responsibility for _cts.Dispose(); both must
        // claim the CTS via Interlocked.Exchange so that a race between them does not have
        // both call Dispose on the same instance and raise ObjectDisposedException.
        // Note: the race window is narrow, so this trial-loop is intentionally aggressive.
        for (var trial = 0; trial < 50; trial++)
        {
            var mockConfig = new Mock<IBusConfiguration>();
            mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);
            var mockFinder = new Mock<ITimeoutStore>();
            mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new TimeoutsBatch { DueTimeouts = [] });

            var sut = new ProcessManagerTimeoutService(
                mockConfig.Object,
                new Lazy<IBus>(() => new Mock<IBus>().Object),
                mockFinder.Object,
                new Mock<ILogger<ProcessManagerTimeoutService>>().Object);

            await sut.StartAsync(CancellationToken.None);

            var stopTask = Task.Run(() => sut.StopAsync(CancellationToken.None));
            var disposeTask = Task.Run(async () => await sut.DisposeAsync());

            var stopEx = await Record.ExceptionAsync(async () => await stopTask);
            var disposeEx = await Record.ExceptionAsync(async () => await disposeTask);

            Assert.Null(stopEx);
            Assert.Null(disposeEx);
        }
    }
}
