using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Helpers;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class ProcessManagerTimeoutTests
{
    private readonly MessagingFixture _fixture;

    public ProcessManagerTimeoutTests(MessagingFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ProcessManagerTimeout_SchedulesAndHandlesTimeoutMessage()
    {
        var queueName = _fixture.GetUniqueQueueName("pm-timeout");
        var correlationId = Guid.NewGuid();
        var timeoutHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var handlerRefs = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(TimeoutProcessHandler),
                MessageType = typeof(TestMessage)
            },
            new()
            {
                HandlerType = typeof(TimeoutProcessHandler),
                MessageType = typeof(TimeoutMessage)
            }
        };

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton<IList<HandlerReference>>(handlerRefs);
                services.AddSingleton(timeoutHandled);
                services.AddTransient<IProcessHandler<TimeoutProcessData, TestMessage>, TimeoutProcessHandler>();
                services.AddTransient<IProcessHandler<TimeoutProcessData, TimeoutMessage>, TimeoutProcessHandler>();

                services.AddServiceConnect(builder =>
                {
                    builder.UseRabbitMQ(t =>
                    {
                        t.Host = _fixture.RabbitMqHostname;
                        t.Username = _fixture.RabbitMqUsername;
                        t.Password = _fixture.RabbitMqPassword;
                        t.SetClientSetting("Port", _fixture.RabbitMqPort);
                        t.SetClientSetting("RetryCount", 3);
                        t.SetClientSetting("RetrySeconds", 1);
                    });
                    builder.ConfigureQueues(q => q.QueueName = queueName);
                    builder.ConfigureBus(b =>
                    {
                        b.ScanForMessageHandlers = false;
                        b.EnableProcessManagerTimeouts = true;
                        b.ProcessManagerTimeoutPollInterval = TimeSpan.FromMilliseconds(100);
                    });
                    builder.UseInMemoryPersistence();
                });
            })
            .Build();

        try
        {
            await host.StartAsync();

            var bus = host.Services.GetRequiredService<IBus>();
            var initial = new TestMessage(correlationId) { Content = "schedule-timeout" };
            await bus.SendAsync(initial, new SendOptions { EndPoint = queueName });

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            cts.Token.Register(() => timeoutHandled.TrySetCanceled());
            var signalled = await timeoutHandled.Task;

            Assert.True(signalled);

            await Task.Delay(500);

            var finder = host.Services.GetRequiredService<IProcessManagerFinder>();
            var mapper = new TestProcessManagerPropertyMapper();
            mapper.ConfigureMapping<TimeoutProcessData, TestMessage>(d => d.CorrelationId, m => m.CorrelationId);
            var result = await finder.FindDataAsync<TimeoutProcessData>(mapper, new TestMessage(correlationId));

            Assert.NotNull(result);
            Assert.True(result!.Data.TimeoutHandled);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}

file class TimeoutProcessData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
    public bool TimeoutRequested { get; set; }
    public bool TimeoutHandled { get; set; }
}

file class TimeoutProcessHandler :
    IProcessHandler<TimeoutProcessData, TestMessage>,
    IProcessHandler<TimeoutProcessData, TimeoutMessage>
{
    private readonly TaskCompletionSource<bool> _timeoutHandled;

    public TimeoutProcessHandler(TaskCompletionSource<bool> timeoutHandled) => _timeoutHandled = timeoutHandled;

    public IConsumeContext Context { get; set; } = null!;

    public async Task HandleAsync(TestMessage message, TimeoutProcessData data)
    {
        data.CorrelationId = message.CorrelationId;
        data.TimeoutRequested = true;
        await Context!.Bus.RequestTimeoutAsync(data.CorrelationId, TimeSpan.FromMilliseconds(500));
    }

    public Task HandleAsync(TimeoutMessage message, TimeoutProcessData data)
    {
        data.TimeoutHandled = true;
        _timeoutHandled.TrySetResult(true);
        return Task.CompletedTask;
    }
}
