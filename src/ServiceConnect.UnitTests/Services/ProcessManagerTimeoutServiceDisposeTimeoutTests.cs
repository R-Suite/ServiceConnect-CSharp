using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

/// <summary>
/// Verifies that DisposeAsync is bounded by IBusConfiguration.DisposeTimeout
/// even when the polling task is blocked in a non-cooperative ITimeoutStore call.
/// </summary>
public class ProcessManagerTimeoutServiceDisposeTimeoutTests
{
    [Fact]
    public async Task DisposeAsync_PollingTaskWedgedInTimeoutStore_CompletesWithinDisposeTimeout()
    {
        // Arrange: ITimeoutStore whose GetTimeoutsBatchAsync never yields — it calls
        // Task.Delay(Infinite, CancellationToken.None) so the polling loop's own
        // cancellation token cannot interrupt it. This simulates a non-cooperative
        // store (sync-over-async wedge, network hang, etc.).
        var loggerMock = new Mock<ILogger<ProcessManagerTimeoutService>>();
        var fakeTime = new FakeTimeProvider();

        var store = new Mock<ITimeoutStore>();
        store.Setup(s => s.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
             .Returns(() => Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None)
                               .ContinueWith(_ => new TimeoutsBatch { DueTimeouts = [] },
                                             TaskContinuationOptions.None));

        var config = new Mock<IBusConfiguration>();
        config.SetupGet(c => c.EnableProcessManagerTimeouts).Returns(true);
        config.SetupGet(c => c.ProcessManagerTimeoutPollInterval).Returns(TimeSpan.FromMilliseconds(10));
        // Short DisposeTimeout so the test completes quickly.
        config.SetupGet(c => c.DisposeTimeout).Returns(TimeSpan.FromMilliseconds(50));

        var bus = new Lazy<IBus>(() => new Mock<IBus>().Object);
        var svc = new ProcessManagerTimeoutService(config.Object, bus, store.Object, loggerMock.Object, fakeTime);

        // Start the service so the polling task is running.
        await svc.StartAsync(CancellationToken.None);

        // Drive a FakeTimeProvider tick so the poll loop enters GetTimeoutsBatchAsync
        // and gets stuck before we call DisposeAsync.
        fakeTime.Advance(TimeSpan.FromMilliseconds(20));
        await Task.Delay(50); // allow the async poll to enter GetTimeoutsBatchAsync

        // Act: DisposeAsync must return within a reasonable window even though the
        // polling task is wedged. Allow 10× the DisposeTimeout as a safety margin.
        using var testTimeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var disposeTask = svc.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(disposeTask, Task.Delay(Timeout.InfiniteTimeSpan, testTimeoutCts.Token));

        Assert.True(completed == disposeTask,
            "DisposeAsync did not complete within 500 ms; it is likely wedged waiting for the polling task.");

        // Re-await to propagate any unexpected exceptions.
        await disposeTask;

        // Assert: the warning about the polling task not completing within the timeout was logged.
        loggerMock.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("did not complete within")),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }
}
