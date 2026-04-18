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
            NextQueryTime = DateTimeOffset.UtcNow.AddSeconds(30)
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
        _mockFinder.Verify(f => f.RemoveDispatchedTimeoutAsync(timeoutId, It.IsAny<CancellationToken>()), Times.Once);
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
            NextQueryTime = DateTimeOffset.UtcNow.AddSeconds(30)
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.Is<SendOptions>(options => options.EndPoint == "test-queue"),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var sut = CreateSut(_mockFinder.Object);

        await sut.PollOnceAsync();

        _mockFinder.Verify(f => f.ReleaseDispatchedTimeoutAsync(timeoutId, It.IsAny<CancellationToken>()), Times.Once);
        _mockFinder.Verify(f => f.RemoveDispatchedTimeoutAsync(timeoutId, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_LeaseAwareStore_WithLockOwner_UsesOwnerAwareRemove()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var timeoutId = Guid.NewGuid();
        var lockOwner = Guid.NewGuid();
        var leaseAwareStore = new Mock<ITimeoutStore>();
        var leaseAwareView = leaseAwareStore.As<ILeaseAwareTimeoutStore>();
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
            NextQueryTime = DateTimeOffset.UtcNow.AddSeconds(30)
        };

        leaseAwareStore.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        leaseAwareView.Setup(f => f.RemoveDispatchedTimeoutAsync(timeoutId, lockOwner, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.Is<SendOptions>(options => options.EndPoint == "test-queue"),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut(leaseAwareStore.Object);

        await sut.PollOnceAsync();

        leaseAwareView.Verify(f => f.RemoveDispatchedTimeoutAsync(timeoutId, lockOwner, It.IsAny<CancellationToken>()), Times.Once);
        leaseAwareStore.Verify(f => f.RemoveDispatchedTimeoutAsync(timeoutId, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_LeaseAwareStore_WithoutLockOwner_FallsBackToLegacyRemove()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var timeoutId = Guid.NewGuid();
        var leaseAwareStore = new Mock<ITimeoutStore>();
        var leaseAwareView = leaseAwareStore.As<ILeaseAwareTimeoutStore>();
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
            NextQueryTime = DateTimeOffset.UtcNow.AddSeconds(30)
        };

        leaseAwareStore.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        leaseAwareStore.Setup(f => f.RemoveDispatchedTimeoutAsync(timeoutId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.Is<SendOptions>(options => options.EndPoint == "test-queue"),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut(leaseAwareStore.Object);

        await sut.PollOnceAsync();

        leaseAwareStore.Verify(f => f.RemoveDispatchedTimeoutAsync(timeoutId, It.IsAny<CancellationToken>()), Times.Once);
        leaseAwareView.Verify(f => f.RemoveDispatchedTimeoutAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_SendFails_WithLeaseAwareStoreAndLockOwner_UsesOwnerAwareRelease()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var timeoutId = Guid.NewGuid();
        var lockOwner = Guid.NewGuid();
        var leaseAwareStore = new Mock<ITimeoutStore>();
        var leaseAwareView = leaseAwareStore.As<ILeaseAwareTimeoutStore>();
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
            NextQueryTime = DateTimeOffset.UtcNow.AddSeconds(30)
        };

        leaseAwareStore.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        leaseAwareView.Setup(f => f.ReleaseDispatchedTimeoutAsync(timeoutId, lockOwner, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.Is<SendOptions>(options => options.EndPoint == "test-queue"),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var sut = CreateSut(leaseAwareStore.Object);

        await sut.PollOnceAsync();

        leaseAwareView.Verify(f => f.ReleaseDispatchedTimeoutAsync(timeoutId, lockOwner, It.IsAny<CancellationToken>()), Times.Once);
        leaseAwareStore.Verify(f => f.ReleaseDispatchedTimeoutAsync(timeoutId, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StopAsync_DisposesAndClearsCancellationSource()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);
        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TimeoutsBatch { DueTimeouts = [], NextQueryTime = DateTimeOffset.UtcNow.AddSeconds(30) });

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
            NextQueryTime = DateTimeOffset.UtcNow.AddSeconds(30),
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.IsAny<SendOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockFinder.Setup(f => f.RemoveDispatchedTimeoutAsync(timeoutId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = CreateSut(_mockFinder.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.PollOnceAsync());

        _mockFinder.Verify(f => f.ReleaseDispatchedTimeoutAsync(timeoutId, It.IsAny<CancellationToken>()), Times.Never);
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
            NextQueryTime = DateTimeOffset.UtcNow.AddSeconds(30),
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.IsAny<SendOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        _mockFinder.Setup(f => f.ReleaseDispatchedTimeoutAsync(timeoutId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = CreateSut(_mockFinder.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.PollOnceAsync());
    }
}
