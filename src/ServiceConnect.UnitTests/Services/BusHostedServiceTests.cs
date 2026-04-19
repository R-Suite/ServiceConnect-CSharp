using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class BusHostedServiceTests
{
    private readonly Mock<IBus> _mockBus = new();
    private readonly Mock<IBusConfiguration> _mockConfig = new();
    private readonly ILogger<BusHostedService> _logger = new Mock<ILogger<BusHostedService>>().Object;

    private BusHostedService CreateSut() =>
        new BusHostedService(_mockBus.Object, _mockConfig.Object, _logger);

    [Fact]
    public async Task StartAsync_AutoStartTrue_CallsStartConsuming()
    {
        _mockConfig.Setup(c => c.AutoStartConsuming).Returns(true);
        _mockBus.Setup(b => b.StartConsumingAsync()).Returns(Task.CompletedTask);

        var sut = CreateSut();
        await sut.StartAsync(CancellationToken.None);

        _mockBus.Verify(b => b.StartConsumingAsync(), Times.Once);
    }

    [Fact]
    public async Task StartAsync_AutoStartFalse_DoesNotCallStartConsuming()
    {
        _mockConfig.Setup(c => c.AutoStartConsuming).Returns(false);

        var sut = CreateSut();
        await sut.StartAsync(CancellationToken.None);

        _mockBus.Verify(b => b.StartConsumingAsync(), Times.Never);
    }

    [Fact]
    public async Task StartAsync_NoConsumerRegistered_PropagatesException()
    {
        // The hosted service must surface startup failures to the host rather
        // than log a warning and silently report success.
        _mockConfig.Setup(c => c.AutoStartConsuming).Returns(true);
        _mockBus.Setup(b => b.StartConsumingAsync())
            .ThrowsAsync(new InvalidOperationException("No consumer registered."));

        var sut = CreateSut();
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StopAsync_CallsStopConsumingAsync()
    {
        _mockBus.Setup(b => b.StopConsumingAsync()).Returns(Task.CompletedTask);
        var sut = CreateSut();
        await sut.StopAsync(CancellationToken.None);

        _mockBus.Verify(b => b.StopConsumingAsync(), Times.Once);
    }
}
