using Testcontainers.MongoDb;
using Testcontainers.RabbitMq;
using Xunit;

namespace ServiceConnect.EndToEndTests.Fixtures;

public class PersistenceFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim _initLock = new(1, 1);
    private static RabbitMqContainer? _rabbitMqContainer;
    private static MongoDbContainer? _mongoDbContainer;
    private static int _refCount;

    private int _dbCounter;
    private int _queueCounter;

    public string RabbitMqHostname => _rabbitMqContainer!.Hostname;

    public int RabbitMqPort => _rabbitMqContainer!.GetMappedPublicPort(5672);

    public string RabbitMqUsername => "guest";

    public string RabbitMqPassword => "guest";

    public string MongoDbConnectionString => _mongoDbContainer!.GetConnectionString();

    public string GetUniqueDatabaseName(string prefix = "testdb") =>
        $"{prefix}_{Interlocked.Increment(ref _dbCounter)}_{Guid.NewGuid():N}";

    public string GetUniqueQueueName(string prefix = "test") =>
        $"{prefix}.{Interlocked.Increment(ref _queueCounter)}.{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            if (_rabbitMqContainer == null)
            {
                _rabbitMqContainer = new RabbitMqBuilder()
                    .WithUsername("guest")
                    .WithPassword("guest")
                    .Build();
                _mongoDbContainer = new MongoDbBuilder()
                    .Build();
                await Task.WhenAll(
                    _rabbitMqContainer.StartAsync(),
                    _mongoDbContainer.StartAsync()
                );
            }
            _refCount++;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task DisposeAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            _refCount--;
            if (_refCount <= 0 && _rabbitMqContainer != null)
            {
                await Task.WhenAll(
                    _rabbitMqContainer.DisposeAsync().AsTask(),
                    _mongoDbContainer!.DisposeAsync().AsTask()
                );
                _rabbitMqContainer = null;
                _mongoDbContainer = null;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }
}
