using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class ProcessManagerTimeoutServiceLifecycleTests
{
    [Fact]
    public async Task StartAsync_StartupTokenCancelledAfterReturn_DoesNotKillPollLoop()
    {
        // M21: pre-fix the loop's _cts was linked to the startup token; cancelling that
        // token after StartAsync returned silently killed the loop. Post-fix the loop
        // uses an independent _stoppingCts cancelled only by StopAsync.
        using var startupCts = new CancellationTokenSource();
        var fakeTime = new FakeTimeProvider();
        var (svc, finder) = BuildService(fakeTime);

        await svc.StartAsync(startupCts.Token);
        await startupCts.CancelAsync(); // simulate host startup token being cancelled

        // Drive at least one poll tick. FakeTimeProvider.Advance synchronously fires
        // PeriodicTimer ticks; a small delay lets the awaited async path observe the
        // resulting GetTimeoutsBatchAsync call.
        fakeTime.Advance(TimeSpan.FromSeconds(31));
        await Task.Delay(100);

        finder.Verify(f => f.GetTimeoutsBatchAsync(
            It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PollOnceAsync_NonShutdownOceFromTimeoutStore_LogsWarning()
    {
        // M22: an OCE thrown by a non-loop CT (e.g., timeout-store internal cancellation)
        // must surface as a Warning log, not silently terminate the loop.
        var loggerMock = new Mock<ILogger<ProcessManagerTimeoutService>>();
        var fakeTime = new FakeTimeProvider();
        var foreignCt = new CancellationToken(canceled: true);

        var finder = new Mock<ITimeoutStore>();
        finder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new OperationCanceledException("foreign", foreignCt));

        var svc = BuildServiceWithOverrides(fakeTime, finder.Object, loggerMock.Object);
        await svc.StartAsync(CancellationToken.None);

        fakeTime.Advance(TimeSpan.FromSeconds(31));
        await Task.Delay(150);

        loggerMock.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<OperationCanceledException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PollLoop_FakeTimeProviderDrivesPolls()
    {
        // M23: PeriodicTimer must use the injected TimeProvider so FakeTimeProvider can drive ticks.
        var fakeTime = new FakeTimeProvider();
        var (svc, finder) = BuildService(fakeTime);

        await svc.StartAsync(CancellationToken.None);
        // No real time passes; only FakeTimeProvider advances drive ticks.
        fakeTime.Advance(TimeSpan.FromSeconds(31));
        await Task.Delay(100);

        finder.Verify(f => f.GetTimeoutsBatchAsync(
            It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        await svc.StopAsync(CancellationToken.None);
    }

    private static (ProcessManagerTimeoutService svc, Mock<ITimeoutStore> finder) BuildService(
        FakeTimeProvider time)
    {
        var finder = new Mock<ITimeoutStore>();
        finder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new TimeoutsBatch { DueTimeouts = [] });

        var svc = BuildServiceWithOverrides(time, finder.Object, NullLogger<ProcessManagerTimeoutService>.Instance);
        return (svc, finder);
    }

    private static ProcessManagerTimeoutService BuildServiceWithOverrides(
        FakeTimeProvider time,
        ITimeoutStore finder,
        ILogger<ProcessManagerTimeoutService> logger)
    {
        var config = new Mock<IBusConfiguration>();
        config.SetupGet(c => c.EnableProcessManagerTimeouts).Returns(true);
        config.SetupGet(c => c.ProcessManagerTimeoutPollInterval).Returns(TimeSpan.FromSeconds(30));

        var bus = new Lazy<IBus>(() => new Mock<IBus>().Object);
        return new ProcessManagerTimeoutService(
            config.Object,
            bus,
            finder,
            logger,
            time);
    }
}
