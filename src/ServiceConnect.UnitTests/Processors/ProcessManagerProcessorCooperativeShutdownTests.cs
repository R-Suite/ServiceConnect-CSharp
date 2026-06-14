using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class ProcessManagerProcessorCooperativeShutdownTests
{
    [Fact]
    public async Task ProcessAsync_HandlerObservesCooperativeCancel_DoesNotLogError()
    {
        // The handler observes the dispatcher's cancellation token and throws OCE.
        // Pre-fix this hit the generic catch and was logged at LogError. Post-fix the
        // explicit OCE catch with the IsCancellationRequested filter rethrows
        // without logging.
        var capturingLogger = new PmShutdownCapturingLogger();

        var (services, _, mockFinder) = CreateBaseServices();
        var handler = new CooperativeCancelHandler();
        services.AddSingleton<IProcessHandler<PmShutdownData, PmShutdownMessage>>(handler);
        var provider = services.BuildServiceProvider();

        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmShutdownMessage),
            HandlerType = typeof(CooperativeCancelHandler)
        });

        var accessor = new ConsumeScopeAccessor();
        using var _scope = accessor.Push(provider);

        // Make FindDataAsync return null (new saga) so the handler is invoked.
        mockFinder.Setup(f => f.FindDataAsync<PmShutdownData>(
            It.IsAny<IProcessManagerPropertyMapper>(), It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IPersistenceData<PmShutdownData>?)null);

        var processor = new ProcessManagerProcessor(
            registry, accessor, new Lazy<IBus>(() => new Mock<IBus>().Object),
            capturingLogger,
            new BusConfiguration(),
            new QueueConfiguration { QueueName = "q", ErrorQueueName = "errors", AuditQueueName = "audit" },
            new ConsumeContextPool(),
            new ConsumeContextAccessor());

        var msg = new PmShutdownMessage(Guid.NewGuid());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            processor.ProcessAsync(new byte[] { 1 }, typeof(PmShutdownMessage), msg,
                new Dictionary<string, object>(), new Envelope(), cts.Token));

        // No LogError entry was emitted for the cooperative shutdown.
        Assert.DoesNotContain(capturingLogger.Entries,
            e => e.Level == LogLevel.Error && e.Message.Contains("handler threw"));
    }

    [Fact]
    public async Task ProcessAsync_HandlerThrowsForeignOce_StillLogsError()
    {
        // A handler whose own internal timeout (NOT the dispatcher CT) fires throws OCE
        // — that's a real failure, not cooperative shutdown. The generic catch must
        // still log it at LogError because cancellationToken.IsCancellationRequested
        // is false.
        var capturingLogger = new PmShutdownCapturingLogger();

        var (services, _, mockFinder) = CreateBaseServices();
        var handler = new ForeignOceHandler();
        services.AddSingleton<IProcessHandler<PmShutdownData, PmShutdownMessage>>(handler);
        var provider = services.BuildServiceProvider();

        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmShutdownMessage),
            HandlerType = typeof(ForeignOceHandler)
        });

        var accessor = new ConsumeScopeAccessor();
        using var _scope = accessor.Push(provider);

        // Make FindDataAsync return null (new saga) so the handler is invoked.
        mockFinder.Setup(f => f.FindDataAsync<PmShutdownData>(
            It.IsAny<IProcessManagerPropertyMapper>(), It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IPersistenceData<PmShutdownData>?)null);

        var processor = new ProcessManagerProcessor(
            registry, accessor, new Lazy<IBus>(() => new Mock<IBus>().Object),
            capturingLogger,
            new BusConfiguration(),
            new QueueConfiguration { QueueName = "q", ErrorQueueName = "errors", AuditQueueName = "audit" },
            new ConsumeContextPool(),
            new ConsumeContextAccessor());

        var msg = new PmShutdownMessage(Guid.NewGuid());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            processor.ProcessAsync(new byte[] { 1 }, typeof(PmShutdownMessage), msg,
                new Dictionary<string, object>(), new Envelope(), CancellationToken.None));

        // Foreign-CT OCE is a real failure — must be logged at LogError.
        Assert.Contains(capturingLogger.Entries,
            e => e.Level == LogLevel.Error && e.Message.Contains("handler threw"));
    }

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

}

file sealed class CooperativeCancelHandler : IProcessHandler<PmShutdownData, PmShutdownMessage>
{
    public void ConfigureMapper(IProcessManagerPropertyMapper mapper) { }

    public Task HandleAsync(PmShutdownMessage message, PmShutdownData data, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        // Honour the dispatcher CT — same shape as a real long-running handler.
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

file sealed class ForeignOceHandler : IProcessHandler<PmShutdownData, PmShutdownMessage>
{
    public void ConfigureMapper(IProcessManagerPropertyMapper mapper) { }

    public Task HandleAsync(PmShutdownMessage message, PmShutdownData data, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        // Foreign cancellation source — NOT the dispatcher CT. This is a real failure.
        using var foreignCts = new CancellationTokenSource();
        foreignCts.Cancel();
        throw new OperationCanceledException(foreignCts.Token);
    }
}

file class PmShutdownMessage(Guid correlationId) : Message(correlationId)
{
}

file class PmShutdownData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
}

file sealed class PmShutdownCapturingLogger : ILogger<ProcessManagerProcessor>
{
    public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
    public List<LogEntry> Entries { get; } = [];

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;
    bool ILogger.IsEnabled(LogLevel logLevel) => true;

    void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}
