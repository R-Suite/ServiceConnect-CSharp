using Testcontainers.MongoDb;
using Testcontainers.RabbitMq;
using Xunit;

namespace ServiceConnect.EndToEndTests.Fixtures;

public class PersistenceFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _rabbitMqContainer = new RabbitMqBuilder()
        .WithUsername("guest")
        .WithPassword("guest")
        .Build();

    private readonly MongoDbContainer _mongoDbContainer = new MongoDbBuilder()
        .Build();

    private int _dbCounter;
    private int _queueCounter;

    public string RabbitMqHostname => _rabbitMqContainer.Hostname;

    public int RabbitMqPort => _rabbitMqContainer.GetMappedPublicPort(5672);

    public string RabbitMqUsername => "guest";

    public string RabbitMqPassword => "guest";

    public string MongoDbConnectionString => _mongoDbContainer.GetConnectionString();

    public string GetUniqueDatabaseName(string prefix = "testdb") =>
        $"{prefix}_{Interlocked.Increment(ref _dbCounter)}_{Guid.NewGuid():N}";

    public string GetUniqueQueueName(string prefix = "test") =>
        $"{prefix}.{Interlocked.Increment(ref _queueCounter)}.{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(
            _rabbitMqContainer.StartAsync(),
            _mongoDbContainer.StartAsync()
        );
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(
            _rabbitMqContainer.DisposeAsync().AsTask(),
            _mongoDbContainer.DisposeAsync().AsTask()
        );
    }
}
