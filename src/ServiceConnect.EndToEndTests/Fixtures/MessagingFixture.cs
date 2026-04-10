using Testcontainers.RabbitMq;
using Xunit;

namespace ServiceConnect.EndToEndTests.Fixtures;

public class MessagingFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _rabbitMqContainer = new RabbitMqBuilder()
        .WithUsername("guest")
        .WithPassword("guest")
        .Build();

    private int _queueCounter;

    public string RabbitMqHostname => _rabbitMqContainer.Hostname;

    public int RabbitMqPort => _rabbitMqContainer.GetMappedPublicPort(5672);

    public string RabbitMqUsername => "guest";

    public string RabbitMqPassword => "guest";

    public string GetUniqueQueueName(string prefix = "test") =>
        $"{prefix}.{Interlocked.Increment(ref _queueCounter)}.{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        await _rabbitMqContainer.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _rabbitMqContainer.DisposeAsync();
    }
}
