using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using System.Reflection;
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

        _mockFinder.Verify(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>()), Times.Never);
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
            DueTimeouts = new List<TimeoutData>
            {
                new TimeoutData
                {
                    Id = timeoutId,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "test-queue",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>(),
                    Locked = false
                }
            },
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(batch);
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
            DueTimeouts = new List<TimeoutData>
            {
                new TimeoutData
                {
                    Id = timeoutId,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "test-queue",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object> { [HeaderKeys.RetryCount] = 3 }
                }
            },
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(batch);
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
                    options.Headers[HeaderKeys.RetryCount] == "3"),
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
            [HeaderKeys.Exception] = "spoofed-exception"
        };

        var timeoutId = Guid.NewGuid();
        var batch = new TimeoutsBatch
        {
            DueTimeouts = new List<TimeoutData>
            {
                new TimeoutData
                {
                    Id = timeoutId,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "test-queue",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>(reservedHeaders)
                    {
                        [HeaderKeys.RetryCount] = "3"
                    }
                }
            },
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(batch);
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
        Assert.Equal("3", outgoingHeaders[HeaderKeys.RetryCount]);

        foreach (var reservedHeader in reservedHeaders.Keys)
            Assert.DoesNotContain(reservedHeader, outgoingHeaders.Keys);
    }

    [Fact]
    public async Task PollOnce_IgnoresUnsupportedStoredHeaderValues_WhenDispatchingTimeout()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var timeoutId = Guid.NewGuid();
        var batch = new TimeoutsBatch
        {
            DueTimeouts = new List<TimeoutData>
            {
                new TimeoutData
                {
                    Id = timeoutId,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "test-queue",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>
                    {
                        [HeaderKeys.RetryCount] = "3",
                        ["Unsupported"] = new object()
                    }
                }
            },
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(batch);
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
                    options.Headers[HeaderKeys.RetryCount] == "3" &&
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
            DueTimeouts = new List<TimeoutData>
            {
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
            },
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(batch);
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
            DueTimeouts = new List<TimeoutData>
            {
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
            },
        };

        store.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(batch);
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
            DueTimeouts = new List<TimeoutData>
            {
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
            },
        };

        store.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(batch);
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
            DueTimeouts = new List<TimeoutData>
            {
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
            },
        };

        store.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(batch);
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
        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TimeoutsBatch { DueTimeouts = [] });

        var sut = CreateSut(_mockFinder.Object);

        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        var ctsField = typeof(ProcessManagerTimeoutService)
            .GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(ctsField);
        Assert.Null(ctsField!.GetValue(sut));
    }

    [Fact]
    public async Task PollOnce_RemoveDispatchedThrowsOCE_Propagates()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var timeoutId = Guid.NewGuid();
        var batch = new TimeoutsBatch
        {
            DueTimeouts = new List<TimeoutData>
            {
                new TimeoutData
                {
                    Id = timeoutId,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "test-queue",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>(),
                }
            },
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.IsAny<SendOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockFinder.Setup(f => f.RemoveDispatchedTimeoutAsync(timeoutId, (Guid?)null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = CreateSut(_mockFinder.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.PollOnceAsync());

        _mockFinder.Verify(f => f.ReleaseDispatchedTimeoutAsync(timeoutId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_WhenSendSucceeds_UsesNonCanceledTokenForRemove()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var timeoutId = Guid.NewGuid();
        var batch = new TimeoutsBatch
        {
            DueTimeouts = new List<TimeoutData>
            {
                new TimeoutData
                {
                    Id = timeoutId,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "test-queue",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>()
                }
            },
        };

        using var cts = new CancellationTokenSource();
        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.IsAny<SendOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .Returns(Task.CompletedTask);

        CancellationToken removeToken = default;
        _mockFinder.Setup(f => f.RemoveDispatchedTimeoutAsync(timeoutId, (Guid?)null, It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid?, CancellationToken>((_, _, token) => removeToken = token)
            .Returns(Task.CompletedTask);

        var sut = CreateSut(_mockFinder.Object);

        await sut.PollOnceAsync(cts.Token);

        Assert.False(removeToken.IsCancellationRequested);
    }

    [Fact]
    public async Task PollOnce_ReleaseDispatchedThrowsOCE_Propagates()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var timeoutId = Guid.NewGuid();
        var batch = new TimeoutsBatch
        {
            DueTimeouts = new List<TimeoutData>
            {
                new TimeoutData
                {
                    Id = timeoutId,
                    ProcessManagerId = Guid.NewGuid(),
                    Destination = "test-queue",
                    Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>(),
                }
            },
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.IsAny<SendOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        _mockFinder.Setup(f => f.ReleaseDispatchedTimeoutAsync(timeoutId, (Guid?)null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = CreateSut(_mockFinder.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.PollOnceAsync());
    }

    [Fact]
    public async Task StopAsyncAndDisposeAsync_ConcurrentlyRaced_DoesNotDoubleDisposeCts()
    {
        // L4: Both StopAsync and DisposeAsync take responsibility for _cts.Dispose(). Without
        // matching Interlocked.Exchange in DisposeAsync, a race can have both call Dispose on the
        // same CTS, raising ObjectDisposedException.
        // Note: the race window is narrow; RED phase may not fire on every run of unfixed code.
        for (var trial = 0; trial < 50; trial++)
        {
            var mockConfig = new Mock<IBusConfiguration>();
            mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);
            var mockFinder = new Mock<ITimeoutStore>();
            mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>()))
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
