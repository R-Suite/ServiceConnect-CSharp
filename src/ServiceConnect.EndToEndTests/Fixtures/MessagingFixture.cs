using Testcontainers.RabbitMq;
using Xunit;

namespace ServiceConnect.EndToEndTests.Fixtures;

public class MessagingFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim _initLock = new(1, 1);
    private static RabbitMqContainer? _container;
    private static int _refCount;

    private int _queueCounter;

    public string RabbitMqHostname => _container!.Hostname;

    public int RabbitMqPort => _container!.GetMappedPublicPort(5672);

    public string RabbitMqUsername => "guest";

    public string RabbitMqPassword => "guest";

    public string GetUniqueQueueName(string prefix = "test") =>
        $"{prefix}.{Interlocked.Increment(ref _queueCounter)}.{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            if (_container == null)
            {
                _container = new RabbitMqBuilder()
                    .WithUsername("guest")
                    .WithPassword("guest")
                    .Build();
                await _container.StartAsync();
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
            if (_refCount <= 0 && _container != null)
            {
                await _container.DisposeAsync();
                _container = null;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }
}
