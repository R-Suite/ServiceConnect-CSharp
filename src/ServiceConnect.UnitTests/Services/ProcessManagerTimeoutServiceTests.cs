using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class ProcessManagerTimeoutServiceTests
{
    private readonly Mock<IBusConfiguration> _mockConfig = new();
    private readonly Mock<IProcessManagerFinder> _mockFinder = new();
    private readonly ILogger<ProcessManagerTimeoutService> _logger =
        new Mock<ILogger<ProcessManagerTimeoutService>>().Object;

    private ProcessManagerTimeoutService CreateSut(bool registerFinder = true)
    {
        var services = new ServiceCollection();
        if (registerFinder)
            services.AddSingleton(_mockFinder.Object);
        var sp = services.BuildServiceProvider();
        return new ProcessManagerTimeoutService(_mockConfig.Object, sp, _logger);
    }

    [Fact]
    public async Task StartAsync_TimeoutsDisabled_DoesNotPoll()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(false);
        var sut = CreateSut();

        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        _mockFinder.Verify(f => f.GetTimeoutsBatch(), Times.Never);
    }

    [Fact]
    public async Task StartAsync_NoFinderRegistered_DoesNotThrow()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);
        var sut = CreateSut(registerFinder: false);

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
                    Time = DateTime.UtcNow.AddMinutes(-1),
                    Headers = new Dictionary<string, object>(),
                    Locked = false
                }
            },
            NextQueryTime = DateTime.UtcNow.AddSeconds(30)
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatch()).Returns(batch);

        var sut = CreateSut(registerFinder: true);

        await sut.PollOnceAsync();

        _mockFinder.Verify(f => f.RemoveDispatchedTimeout(timeoutId), Times.Once);
    }
}
