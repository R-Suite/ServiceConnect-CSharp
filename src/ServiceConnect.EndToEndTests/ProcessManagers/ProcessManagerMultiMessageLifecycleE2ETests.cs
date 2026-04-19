using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Helpers;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(IsolatedCollection))]
public class ProcessManagerMultiMessageLifecycleE2ETests
{
    private readonly MessagingFixture _fixture;

    public ProcessManagerMultiMessageLifecycleE2ETests(MessagingFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ProcessManager_MultipleMessageTypes_SameLifecycle_StatePersistsAcrossStartResumeFinish()
    {
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueName = _fixture.GetUniqueQueueName("pm-lifecycle");
        var correlationId = Guid.NewGuid();

        var handlerRefs = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(LifecycleProcessHandler),
                MessageType = typeof(ProcessStartedMessage)
            },
            new()
            {
                HandlerType = typeof(LifecycleProcessHandler),
                MessageType = typeof(ProcessResumedMessage)
            },
            new()
            {
                HandlerType = typeof(LifecycleProcessHandler),
                MessageType = typeof(ProcessFinishedMessage)
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton(finished);
        services.AddTransient<IProcessHandler<LifecycleProcessData, ProcessStartedMessage>, LifecycleProcessHandler>();
        services.AddTransient<IProcessHandler<LifecycleProcessData, ProcessResumedMessage>, LifecycleProcessHandler>();
        services.AddTransient<IProcessHandler<LifecycleProcessData, ProcessFinishedMessage>, LifecycleProcessHandler>();

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
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
            builder.UseInMemoryPersistence();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();

        try
        {
            await bus.SendAsync(new ProcessStartedMessage(correlationId) { StepName = "start" }, new SendOptions { EndPoint = queueName });
            await bus.SendAsync(new ProcessResumedMessage(correlationId) { StepName = "resume" }, new SendOptions { EndPoint = queueName });
            await bus.SendAsync(new ProcessFinishedMessage(correlationId) { StepName = "finish" }, new SendOptions { EndPoint = queueName });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => finished.TrySetCanceled());
            await finished.Task;

            await Task.Delay(500);

            var finder = provider.GetRequiredService<IProcessManagerFinder>();
            var mapper = new TestProcessManagerPropertyMapper();
            mapper.ConfigureMapping<LifecycleProcessData, ProcessStartedMessage>(d => d.CorrelationId, m => m.CorrelationId);
            mapper.ConfigureMapping<LifecycleProcessData, ProcessResumedMessage>(d => d.CorrelationId, m => m.CorrelationId);
            mapper.ConfigureMapping<LifecycleProcessData, ProcessFinishedMessage>(d => d.CorrelationId, m => m.CorrelationId);
            var result = await finder.FindDataAsync<LifecycleProcessData>(mapper, new ProcessFinishedMessage(correlationId));

            Assert.NotNull(result);
            Assert.Equal(3, result!.Data.HandledCount);
            Assert.Equal("start", result.Data.StartStep);
            Assert.Equal("resume", result.Data.ResumeStep);
            Assert.Equal("finish", result.Data.FinishStep);
            Assert.True(result.Data.IsFinished);
        }
        finally
        {
            await bus.DisposeAsync();
            if (provider is IAsyncDisposable asyncProvider) await asyncProvider.DisposeAsync();
        }
    }
}

file class LifecycleProcessData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
    public int HandledCount { get; set; }
    public string StartStep { get; set; } = string.Empty;
    public string ResumeStep { get; set; } = string.Empty;
    public string FinishStep { get; set; } = string.Empty;
    public bool IsFinished { get; set; }
}

file sealed class ProcessStartedMessage(Guid correlationId) : Message(correlationId)
{
    public string StepName { get; set; } = string.Empty;
}

file sealed class ProcessResumedMessage(Guid correlationId) : Message(correlationId)
{
    public string StepName { get; set; } = string.Empty;
}

file sealed class ProcessFinishedMessage(Guid correlationId) : Message(correlationId)
{
    public string StepName { get; set; } = string.Empty;
}

file class LifecycleProcessHandler :
    IProcessHandler<LifecycleProcessData, ProcessStartedMessage>,
    IProcessHandler<LifecycleProcessData, ProcessResumedMessage>,
    IProcessHandler<LifecycleProcessData, ProcessFinishedMessage>
{
    private readonly TaskCompletionSource<bool> _finished;

    public LifecycleProcessHandler(TaskCompletionSource<bool> finished) => _finished = finished;

    public IConsumeContext? Context { get; set; }

    public Task HandleAsync(ProcessStartedMessage message, LifecycleProcessData data)
    {
        data.HandledCount++;
        data.StartStep = message.StepName;
        return Task.CompletedTask;
    }

    public Task HandleAsync(ProcessResumedMessage message, LifecycleProcessData data)
    {
        data.HandledCount++;
        data.ResumeStep = message.StepName;
        return Task.CompletedTask;
    }

    public Task HandleAsync(ProcessFinishedMessage message, LifecycleProcessData data)
    {
        data.HandledCount++;
        data.FinishStep = message.StepName;
        data.IsFinished = true;
        _finished.TrySetResult(true);
        return Task.CompletedTask;
    }
}
