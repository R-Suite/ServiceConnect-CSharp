using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class ProcessManagerTimeoutServiceLifecycleTests
{
    [Fact]
    public async Task StartAsync_StartupTokenCancelledAfterReturn_DoesNotKillPollLoop()
    {
        // The loop's _stoppingCts must be independent of the startup token: cancelling
        // the startup token after StartAsync returns must not stop polling. Linking the
        // two would silently kill the loop once the host startup window closed.
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
        // An OCE thrown by a non-loop CT (e.g., timeout-store internal cancellation)
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
    public async Task PollLoop_RemoveFailsTransiently_CatchUpLoopContinuesDrainingBacklog()
    {
        // When the store's Remove consistently fails, the catch-up loop must keep re-polling
        // because sentCount (not a remove-derived count) drives the loop signal. If the loop
        // signal were derived from successful removes, a degraded store would stall the loop
        // after a single batch, reducing drain rate from full-batch-per-inner-iteration to
        // one-batch-per-tick.
        //
        // Scenario: first 3 calls return a non-empty batch; 4th call returns empty.
        // The catch-up loop should call GetTimeoutsBatchAsync 4 times across 3 inner
        // iterations (3 × 1 sent, loop stops on the empty batch).
        var fakeTime = new FakeTimeProvider();
        var bus = new Mock<IBus>();
        bus.Setup(b => b.SendAsync(It.IsAny<TimeoutMessage>(), It.IsAny<SendOptions>(), It.IsAny<CancellationToken>()))
           .Returns(Task.CompletedTask);

        var callCount = 0;
        var finder = new Mock<ITimeoutStore>();
        finder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(() =>
              {
                  callCount++;
                  // First 3 calls return one due timeout; 4th returns empty, ending the loop.
                  if (callCount <= 3)
                  {
                      return new TimeoutsBatch
                      {
                          DueTimeouts =
                          [
                              new TimeoutData
                              {
                                  Id = Guid.NewGuid(),
                                  ProcessManagerId = Guid.NewGuid(),
                                  Destination = "q",
                                  Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                                  Headers = new Dictionary<string, object>()
                              }
                          ]
                      };
                  }

                  return new TimeoutsBatch { DueTimeouts = [] };
              });

        // Every remove throws — simulates a degraded store while sends are healthy.
        finder.Setup(f => f.RemoveDispatchedTimeoutAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("store unavailable"));

        var config = new Mock<IBusConfiguration>();
        config.SetupGet(c => c.EnableProcessManagerTimeouts).Returns(true);
        config.SetupGet(c => c.ProcessManagerTimeoutPollInterval).Returns(TimeSpan.FromSeconds(30));

        var svc = new ProcessManagerTimeoutService(
            config.Object,
            new Lazy<IBus>(() => bus.Object),
            finder.Object,
            NullLogger<ProcessManagerTimeoutService>.Instance,
            fakeTime);

        await svc.StartAsync(CancellationToken.None);

        // Advance the fake clock to fire one timer tick; the catch-up loop runs within it.
        fakeTime.Advance(TimeSpan.FromSeconds(31));
        // Allow async continuations to settle after the tick.
        await Task.Delay(200);

        await svc.StopAsync(CancellationToken.None);

        // The catch-up loop must have polled 4 times in one tick (3 non-empty + 1 empty stop).
        finder.Verify(f => f.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(4));

        // SendAsync must have been called once for each of the 3 non-empty batches.
        bus.Verify(b => b.SendAsync(It.IsAny<TimeoutMessage>(), It.IsAny<SendOptions>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(3));

        // Release must NOT be called — remove failures after a successful send are not
        // send failures and must not trigger release (which would re-queue the message).
        finder.Verify(f => f.ReleaseDispatchedTimeoutAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PollLoop_FakeTimeProviderDrivesPolls()
    {
        // PeriodicTimer must use the injected TimeProvider so FakeTimeProvider can drive ticks.
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
