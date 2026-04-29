using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Interfaces;
using ServiceConnect.Telemetry;
using Xunit;

namespace ServiceConnect.EndToEndTests.Telemetry;

/// <summary>
/// Minimal message type for trace-correlation end-to-end testing.
/// Declared here (not in Messages/) so it binds to a unique exchange
/// and does not cross-contaminate with TestMessage-based collections.
/// </summary>
public sealed class TraceTestMessage(Guid correlationId) : Message(correlationId);

file sealed class TraceTestHandler(TaskCompletionSource consumed) : IMessageHandler<TraceTestMessage>
{
    private readonly TaskCompletionSource _consumed = consumed;

    public IConsumeContext Context { get; set; } = null!;

    public Task HandleAsync(TraceTestMessage message, CancellationToken cancellationToken = default)
    {
        _consumed.TrySetResult();
        return Task.CompletedTask;
    }
}

[Collection(nameof(MessagingCollection))]
public class TelemetryE2ETests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Publish_then_consume_correlates_trace_ids_via_AddTelemetry()
    {
        var publishSpans = new List<Activity>();
        var consumeSpans = new List<Activity>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = static src => src.Name == ServiceConnectActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                if (a.Kind == ActivityKind.Producer)
                {
                    publishSpans.Add(a);
                }
                else if (a.Kind == ActivityKind.Consumer)
                {
                    consumeSpans.Add(a);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        var consumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueName = _fixture.GetUniqueQueueName("telemetry-e2e");

        var handlerReferences = new List<HandlerReference>
        {
            new() { HandlerType = typeof(TraceTestHandler), MessageType = typeof(TraceTestMessage) },
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerReferences);
        services.AddSingleton(consumed);
        services.AddTransient<IMessageHandler<TraceTestMessage>, TraceTestHandler>();

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
            builder.ConfigureTransport(t => t.MaxRetries = 0);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
            builder.AddTelemetry();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();

        try
        {
            await bus.PublishAsync(new TraceTestMessage(Guid.NewGuid()));
            await consumed.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Allow a brief delay so the consume Activity is Stop()'d before assertions.
            // ActivityStopped fires after the handler returns and the consume span is
            // closed by the pipeline; without this, consumeSpans may be empty even
            // though the TaskCompletionSource is already signalled.
            await Task.Delay(TimeSpan.FromMilliseconds(250));

            var publishSpan = Assert.Single(publishSpans);
            var consumeSpan = Assert.Single(consumeSpans);

            // The W3C traceparent injected into the publish headers carries both the
            // TraceId and the publish SpanId.  The consume middleware extracts them and
            // starts the consume span with the publish span as parent, so:
            //   consume.TraceId  == publish.TraceId
            //   consume.ParentSpanId == publish.SpanId
            Assert.Equal(publishSpan.TraceId, consumeSpan.TraceId);
            Assert.Equal(publishSpan.SpanId, consumeSpan.ParentSpanId);
        }
        finally
        {
            await bus.DisposeAsync();
            if (provider is IAsyncDisposable asyncProvider)
            {
                await asyncProvider.DisposeAsync();
            }
        }
    }
}
