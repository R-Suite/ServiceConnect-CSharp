using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

/// <summary>
/// End-to-end guard that DisposeAsync is safe to call while concurrent publish loops
/// are still in flight. Dispose must not produce SemaphoreFullException, unhandled
/// task exceptions, or crash the process. ObjectDisposedException and
/// OperationCanceledException on in-flight publishes are acceptable but must be
/// collected rather than escape unobserved.
/// </summary>
[Collection(nameof(MessagingCollection))]
public class ProducerDisposeConcurrencyE2ETests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ConcurrentPublishLoops_FollowedByDispose_NoUnhandledExceptions()
    {
        const int publisherCount = 8;

        var queueName = _fixture.GetUniqueQueueName("dispose-concurrency");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>([]);

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
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        var strayExceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var cts = new CancellationTokenSource();

        // Register an unhandled exception trap so the test process does not crash
        void OnUnhandled(object? sender, UnhandledExceptionEventArgs args)
        {
            if (args.ExceptionObject is Exception ex)
            {
                strayExceptions.Add(ex);
            }
        }
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;

        var publisherTasks = Enumerable.Range(0, publisherCount).Select(i => Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await bus.PublishAsync(
                        new TestMessage(Guid.NewGuid()) { Content = $"pub-{i}" },
                        cancellationToken: cts.Token);
                }
                catch (ObjectDisposedException) { return; }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    strayExceptions.Add(ex);
                    return;
                }
            }
        })).ToList();

        // Let publishers run for ~200 ms then dispose
        await Task.Delay(200);
        await cts.CancelAsync();
        await bus.DisposeAsync();

        // Wait for all publisher loops to finish
        await Task.WhenAll(publisherTasks);

        AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;

        if (provider is IAsyncDisposable ap)
        {
            await ap.DisposeAsync();
        }

        // Filter out acceptable exceptions (ObjectDisposedException / OperationCanceledException)
        var unexpectedExceptions = strayExceptions
            .Where(ex => ex is not ObjectDisposedException and not OperationCanceledException)
            .ToList();

        Assert.Empty(unexpectedExceptions);

        // Explicit failure-mode checks:
        // - SemaphoreFullException would mean a publisher's Release() ran on a
        //   semaphore that DisposeAsync had already disposed.
        // - NullReferenceException would mean a publisher reached _model after
        //   DisposeAsync nulled it (post-WaitAsync race).
        Assert.DoesNotContain(strayExceptions, ex => ex is SemaphoreFullException);
        Assert.DoesNotContain(strayExceptions, ex => ex is NullReferenceException);
    }
}
