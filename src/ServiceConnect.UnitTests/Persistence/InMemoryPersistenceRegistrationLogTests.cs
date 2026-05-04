using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using ServiceConnect;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class InMemoryPersistenceRegistrationLogTests
{
    [Fact]
    public void UseInMemoryPersistence_OnFirstStateResolution_LogsWarningExactlyOnce()
    {
        var builder = new ServiceConnectBuilder();
        builder.UseInMemoryPersistence();

        var fakeLogger = new FakeLogger<InMemoryPersistenceState>();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<ILogger<InMemoryPersistenceState>>(fakeLogger);
        builder.AdditionalRegistrations[0](services);

        using var sp = services.BuildServiceProvider();
        var first = sp.GetRequiredService<InMemoryPersistenceState>();
        var second = sp.GetRequiredService<InMemoryPersistenceState>();

        Assert.Same(first, second);

        var records = fakeLogger.Collector.GetSnapshot();
        Assert.Single(records);
        Assert.Equal(LogLevel.Warning, records[0].Level);
        Assert.Equal(InMemoryPersistenceLog.InMemoryPersistenceRegisteredEventId, records[0].Id.Id);
        Assert.Contains("In-memory persistence is registered", records[0].Message);
        Assert.Contains("development and tests", records[0].Message);
        Assert.Contains("MongoDB", records[0].Message);
    }
}
