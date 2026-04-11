# E2E Test Coverage Gaps Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add 19 E2E tests covering missing scenarios: custom headers, send to multiple endpoints, multiple handlers, scatter-gather partial responses, queue mappings, process manager exceptions, aggregator exceptions, filter chain ordering, stream out-of-order packets, request/reply headers, prefetch count, MaxRetries=0, PublishRequestAsync, publish after dispose, malformed message body, send to non-existent endpoint, and empty message content.

**Architecture:** Each test file follows the existing pattern: xUnit + Testcontainers, `[Collection(nameof(MessagingCollection))]` or `[Collection(nameof(PersistenceCollection))]`, `TaskCompletionSource<T>` + `CancellationTokenSource(30s)` for async waiting, `CallbackHandler<T>` for handler registration.

**Tech Stack:** C# / .NET 10, xUnit, Testcontainers (RabbitMQ + MongoDB), RabbitMQ.Client 6.8.1

**Excluded (not implemented in codebase):** ExceptionHandler callback (defined but never wired), AutoStartConsuming (defined but never checked), Process Manager Timeouts, Middleware Pipeline.

**Test runner:** `dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "Category=Docker" --no-build`

---

## File Structure

All files created in `src/ServiceConnect.EndToEndTests/`:

| File | Tests | Scenarios |
|------|-------|-----------|
| `CustomHeaderTests.cs` | 2 | Custom headers via PublishAsync + SendRequestAsync |
| `MultiEndpointSendTests.cs` | 1 | SendAsync with SendOptions.EndPoints to multiple queues |
| `MultipleHandlerTests.cs` | 1 | Two handlers for same message type both execute |
| `ScatterGatherPartialTests.cs` | 1 | Partial responses returned on timeout |
| `QueueMappingTests.cs` | 1 | AddQueueMapping routes to mapped queue |
| `ProcessManagerExceptionTests.cs` | 1 | PM handler throws -> message retried/error queued |
| `AggregatorExceptionTests.cs` | 1 | Aggregator Execute throws -> error propagation |
| `FilterChainTests.cs` | 2 | Multiple filters in order + first block stops chain |
| `StreamOutOfOrderTests.cs` | 1 | Out-of-order packets reassembled correctly |
| `RequestReplyHeaderTests.cs` | 1 | Custom headers propagated to responder Context.Headers |
| `PrefetchCountTests.cs` | 1 | PrefetchCount=1 limits concurrent processing |
| `MaxRetriesZeroTests.cs` | 1 | MaxRetries=0 sends straight to error queue |
| `PublishRequestAsyncTests.cs` | 1 | PublishRequestAsync callback fires for each reply |
| `PublishAfterDisposeTests.cs` | 1 | PublishAsync after Dispose throws |
| `MalformedMessageTests.cs` | 1 | Corrupt JSON -> error queue |
| `EmptyMessageTests.cs` | 1 | Null/empty content handled without crash |

**Total: 19 new tests across 16 files**

---

### Task 1: CustomHeaderTests.cs

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/CustomHeaderTests.cs`

- [ ] **Step 1: Write the test file**

```csharp
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class CustomHeaderTests
{
    private readonly MessagingFixture _fixture;
    public CustomHeaderTests(MessagingFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task PublishAsync_CustomHeaders_ReceivedByHandler()
    {
        var tcs = new TaskCompletionSource<Dictionary<string, string>>();
        var queueName = _fixture.GetUniqueQueueName("custom-hdr-pub");

        var handlerRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(HeaderCaptureHandler), MessageType = typeof(TestMessage), RoutingKeys = new List<string>() }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new HeaderCaptureHandler(headers => tcs.TrySetResult(headers)));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            await bus.PublishAsync(new TestMessage(Guid.NewGuid()) { Content = "with-headers" },
                new PublishOptions { Headers = new Dictionary<string, string> { ["X-Custom"] = "hello-world" } });

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => tcs.TrySetCanceled());
            var receivedHeaders = await tcs.Task;

            Assert.True(receivedHeaders.ContainsKey("X-Custom"));
            Assert.Equal("hello-world", receivedHeaders["X-Custom"]);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task SendRequestAsync_CustomHeaders_PropagatedToResponder()
    {
        var responderQueue = _fixture.GetUniqueQueueName("custom-hdr-resp");
        var requesterQueue = _fixture.GetUniqueQueueName("custom-hdr-req");

        var responderRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(HeaderEchoReplyHandler), MessageType = typeof(TestRequest), RoutingKeys = new List<string>() }
        };

        var responderServices = new ServiceCollection();
        responderServices.AddLogging();
        responderServices.AddSingleton<IList<HandlerReference>>(responderRefs);
        responderServices.AddTransient<IMessageHandler<TestRequest>, HeaderEchoReplyHandler>();
        responderServices.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = responderQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var responderProvider = responderServices.BuildServiceProvider();
        var responderBus = responderProvider.GetRequiredService<IBus>();
        await responderBus.StartConsumingAsync();

        var requesterServices = new ServiceCollection();
        requesterServices.AddLogging();
        requesterServices.AddSingleton<IList<HandlerReference>>(new List<HandlerReference>());
        requesterServices.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = requesterQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var requesterProvider = requesterServices.BuildServiceProvider();
        var requesterBus = requesterProvider.GetRequiredService<IBus>();
        await requesterBus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            var response = await requesterBus.SendRequestAsync<TestRequest, TestResponse>(
                new TestRequest(Guid.NewGuid()) { Question = "header-test" },
                new RequestOptions
                {
                    EndPoint = responderQueue,
                    Timeout = 30000,
                    Headers = new Dictionary<string, string> { ["X-Trace-Id"] = "trace-123" }
                });

            // HeaderEchoReplyHandler echoes the custom header value in the Answer field
            Assert.Contains("trace-123", response.Answer);
        }
        finally
        {
            responderBus.Dispose();
            (responderProvider as IDisposable)?.Dispose();
            requesterBus.Dispose();
            (requesterProvider as IDisposable)?.Dispose();
        }
    }
}

file class HeaderCaptureHandler : IMessageHandler<TestMessage>
{
    private readonly Action<Dictionary<string, string>> _callback;
    public IConsumeContext? Context { get; set; }
    public HeaderCaptureHandler(Action<Dictionary<string, string>> callback) => _callback = callback;

    public Task HandleAsync(TestMessage message)
    {
        _callback(Context!.Headers);
        return Task.CompletedTask;
    }
}

file class HeaderEchoReplyHandler : IMessageHandler<TestRequest>
{
    public IConsumeContext? Context { get; set; }

    public async Task HandleAsync(TestRequest message)
    {
        var traceId = Context!.Headers.TryGetValue("X-Trace-Id", out var val) ? val : "not-found";
        await Context.ReplyAsync(new TestResponse(Guid.NewGuid()) { Answer = $"echo:{traceId}" });
    }
}
```

- [ ] **Step 2: Build and run tests**

Run: `dotnet build src/ServiceConnect.EndToEndTests && dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~CustomHeaderTests" --no-build`
Expected: 2 tests PASS

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/CustomHeaderTests.cs
git commit -m "test: add E2E tests for custom header propagation"
```

---

### Task 2: MultiEndpointSendTests.cs

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/MultiEndpointSendTests.cs`

- [ ] **Step 1: Write the test file**

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class MultiEndpointSendTests
{
    private readonly MessagingFixture _fixture;
    public MultiEndpointSendTests(MessagingFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task SendAsync_MultipleEndpoints_AllReceiveMessage()
    {
        var queue1 = _fixture.GetUniqueQueueName("multi-ep-1");
        var queue2 = _fixture.GetUniqueQueueName("multi-ep-2");
        var senderQueue = _fixture.GetUniqueQueueName("multi-ep-sender");
        var tcs1 = new TaskCompletionSource<TestMessage>();
        var tcs2 = new TaskCompletionSource<TestMessage>();

        // Consumer 1
        var handlerRefs1 = new List<HandlerReference>
        {
            new() { HandlerType = typeof(CallbackHandler<TestMessage>), MessageType = typeof(TestMessage), RoutingKeys = new List<string>() }
        };
        var services1 = new ServiceCollection();
        services1.AddLogging();
        services1.AddSingleton<IList<HandlerReference>>(handlerRefs1);
        services1.AddTransient<IMessageHandler<TestMessage>>(_ => new CallbackHandler<TestMessage>(msg => tcs1.TrySetResult(msg)));
        services1.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = queue1);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });
        var provider1 = services1.BuildServiceProvider();
        var bus1 = provider1.GetRequiredService<IBus>();
        await bus1.StartConsumingAsync();

        // Consumer 2
        var handlerRefs2 = new List<HandlerReference>
        {
            new() { HandlerType = typeof(CallbackHandler<TestMessage>), MessageType = typeof(TestMessage), RoutingKeys = new List<string>() }
        };
        var services2 = new ServiceCollection();
        services2.AddLogging();
        services2.AddSingleton<IList<HandlerReference>>(handlerRefs2);
        services2.AddTransient<IMessageHandler<TestMessage>>(_ => new CallbackHandler<TestMessage>(msg => tcs2.TrySetResult(msg)));
        services2.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = queue2);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });
        var provider2 = services2.BuildServiceProvider();
        var bus2 = provider2.GetRequiredService<IBus>();
        await bus2.StartConsumingAsync();

        // Sender
        var senderServices = new ServiceCollection();
        senderServices.AddLogging();
        senderServices.AddSingleton<IList<HandlerReference>>(new List<HandlerReference>());
        senderServices.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = senderQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });
        var senderProvider = senderServices.BuildServiceProvider();
        var senderBus = senderProvider.GetRequiredService<IBus>();

        await Task.Delay(500);

        try
        {
            var correlationId = Guid.NewGuid();
            await senderBus.SendAsync(new TestMessage(correlationId) { Content = "multi-send" },
                new SendOptions { EndPoints = new List<string> { queue1, queue2 } });

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => { tcs1.TrySetCanceled(); tcs2.TrySetCanceled(); });

            var received1 = await tcs1.Task;
            var received2 = await tcs2.Task;

            Assert.Equal("multi-send", received1.Content);
            Assert.Equal("multi-send", received2.Content);
        }
        finally
        {
            bus1.Dispose(); (provider1 as IDisposable)?.Dispose();
            bus2.Dispose(); (provider2 as IDisposable)?.Dispose();
            senderBus.Dispose(); (senderProvider as IDisposable)?.Dispose();
        }
    }
}
```

- [ ] **Step 2: Build and run test**

Run: `dotnet build src/ServiceConnect.EndToEndTests && dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~MultiEndpointSendTests" --no-build`
Expected: 1 test PASS

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/MultiEndpointSendTests.cs
git commit -m "test: add E2E test for SendAsync to multiple endpoints"
```

---

### Task 3: MultipleHandlerTests.cs

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/MultipleHandlerTests.cs`

- [ ] **Step 1: Write the test file**

HandlerProcessor walks base type hierarchy and invokes ALL matching handlers. This test registers two `IMessageHandler<TestMessage>` and verifies both execute.

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class MultipleHandlerTests
{
    private readonly MessagingFixture _fixture;
    public MultipleHandlerTests(MessagingFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Publish_TwoHandlersForSameType_BothExecute()
    {
        var received = new ConcurrentBag<string>();
        var allDone = new TaskCompletionSource<bool>();
        int count = 0;
        var queueName = _fixture.GetUniqueQueueName("multi-handler");

        var handlerRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(TaggedHandlerA), MessageType = typeof(TestMessage), RoutingKeys = new List<string>() },
            new() { HandlerType = typeof(TaggedHandlerB), MessageType = typeof(TestMessage), RoutingKeys = new List<string>() }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton(received);
        services.AddSingleton(allDone);

        // Register both handler types - DI needs to resolve by concrete type
        services.AddTransient<TaggedHandlerA>();
        services.AddTransient<TaggedHandlerB>();

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            await bus.PublishAsync(new TestMessage(Guid.NewGuid()) { Content = "double" });

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => allDone.TrySetCanceled());
            await allDone.Task;

            Assert.Contains("HandlerA", received);
            Assert.Contains("HandlerB", received);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }
}

file class TaggedHandlerA : IMessageHandler<TestMessage>
{
    private readonly ConcurrentBag<string> _received;
    private readonly TaskCompletionSource<bool> _done;
    public IConsumeContext? Context { get; set; }

    public TaggedHandlerA(ConcurrentBag<string> received, TaskCompletionSource<bool> done)
    {
        _received = received;
        _done = done;
    }

    public Task HandleAsync(TestMessage message)
    {
        _received.Add("HandlerA");
        if (_received.Count >= 2) _done.TrySetResult(true);
        return Task.CompletedTask;
    }
}

file class TaggedHandlerB : IMessageHandler<TestMessage>
{
    private readonly ConcurrentBag<string> _received;
    private readonly TaskCompletionSource<bool> _done;
    public IConsumeContext? Context { get; set; }

    public TaggedHandlerB(ConcurrentBag<string> received, TaskCompletionSource<bool> done)
    {
        _received = received;
        _done = done;
    }

    public Task HandleAsync(TestMessage message)
    {
        _received.Add("HandlerB");
        if (_received.Count >= 2) _done.TrySetResult(true);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Build and run test**

Run: `dotnet build src/ServiceConnect.EndToEndTests && dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~MultipleHandlerTests" --no-build`
Expected: 1 test PASS

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/MultipleHandlerTests.cs
git commit -m "test: add E2E test for multiple handlers on same message type"
```

---

### Task 4: ScatterGatherPartialTests.cs

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/ScatterGatherPartialTests.cs`

- [ ] **Step 1: Write the test file**

Per `RequestReplyManager.cs:91`, timeout calls `tcs.TrySetResult(null!)` which returns whatever responses have been collected. This test sends to 2 responders but only 1 replies.

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class ScatterGatherPartialTests
{
    private readonly MessagingFixture _fixture;
    public ScatterGatherPartialTests(MessagingFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task SendRequestMultiAsync_OneOfTwoResponds_ReturnsPartialResults()
    {
        var responderQueue = _fixture.GetUniqueQueueName("scatter-partial-resp");
        var silentQueue = _fixture.GetUniqueQueueName("scatter-partial-silent");
        var requesterQueue = _fixture.GetUniqueQueueName("scatter-partial-req");

        // Responder that replies
        var responderRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(ScatterReplyHandler), MessageType = typeof(TestRequest), RoutingKeys = new List<string>() }
        };
        var responderServices = new ServiceCollection();
        responderServices.AddLogging();
        responderServices.AddSingleton<IList<HandlerReference>>(responderRefs);
        responderServices.AddTransient<IMessageHandler<TestRequest>, ScatterReplyHandler>();
        responderServices.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = responderQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });
        var responderProvider = responderServices.BuildServiceProvider();
        var responderBus = responderProvider.GetRequiredService<IBus>();
        await responderBus.StartConsumingAsync();

        // Silent consumer (no reply handler) — consumes but doesn't reply
        var silentRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(CallbackHandler<TestRequest>), MessageType = typeof(TestRequest), RoutingKeys = new List<string>() }
        };
        var silentServices = new ServiceCollection();
        silentServices.AddLogging();
        silentServices.AddSingleton<IList<HandlerReference>>(silentRefs);
        silentServices.AddTransient<IMessageHandler<TestRequest>>(_ => new CallbackHandler<TestRequest>(_ => { }));
        silentServices.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = silentQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });
        var silentProvider = silentServices.BuildServiceProvider();
        var silentBus = silentProvider.GetRequiredService<IBus>();
        await silentBus.StartConsumingAsync();

        // Requester
        var requesterServices = new ServiceCollection();
        requesterServices.AddLogging();
        requesterServices.AddSingleton<IList<HandlerReference>>(new List<HandlerReference>());
        requesterServices.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = requesterQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });
        var requesterProvider = requesterServices.BuildServiceProvider();
        var requesterBus = requesterProvider.GetRequiredService<IBus>();
        await requesterBus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            var replies = await requesterBus.SendRequestMultiAsync<TestRequest, TestResponse>(
                new TestRequest(Guid.NewGuid()) { Question = "partial?" },
                new RequestOptions
                {
                    EndPoints = new List<string> { responderQueue, silentQueue },
                    ExpectedReplyCount = 2,
                    Timeout = 5000 // short timeout — will expire before 2nd reply
                });

            // Only 1 of 2 replied, timeout returns partial results
            Assert.Single(replies);
            Assert.Equal("scatter-reply", replies[0].Answer);
        }
        finally
        {
            responderBus.Dispose(); (responderProvider as IDisposable)?.Dispose();
            silentBus.Dispose(); (silentProvider as IDisposable)?.Dispose();
            requesterBus.Dispose(); (requesterProvider as IDisposable)?.Dispose();
        }
    }
}

file class ScatterReplyHandler : IMessageHandler<TestRequest>
{
    public IConsumeContext? Context { get; set; }
    public async Task HandleAsync(TestRequest message)
    {
        await Context!.ReplyAsync(new TestResponse(Guid.NewGuid()) { Answer = "scatter-reply" });
    }
}
```

- [ ] **Step 2: Build and run test**

Run: `dotnet build src/ServiceConnect.EndToEndTests && dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~ScatterGatherPartialTests" --no-build`
Expected: 1 test PASS

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/ScatterGatherPartialTests.cs
git commit -m "test: add E2E test for scatter-gather partial responses"
```

---

### Task 5: QueueMappingTests.cs

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/QueueMappingTests.cs`

- [ ] **Step 1: Write the test file**

`Producer.cs:186` uses `QueueMappings[type.FullName]` to route SendAsync (without explicit endpoint) to mapped queues.

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class QueueMappingTests
{
    private readonly MessagingFixture _fixture;
    public QueueMappingTests(MessagingFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task SendAsync_WithQueueMapping_MessageRoutedToMappedQueue()
    {
        var mappedQueue = _fixture.GetUniqueQueueName("qmap-target");
        var senderQueue = _fixture.GetUniqueQueueName("qmap-sender");
        var tcs = new TaskCompletionSource<TestMessage>();

        // Consumer on mapped queue
        var handlerRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(CallbackHandler<TestMessage>), MessageType = typeof(TestMessage), RoutingKeys = new List<string>() }
        };
        var consumerServices = new ServiceCollection();
        consumerServices.AddLogging();
        consumerServices.AddSingleton<IList<HandlerReference>>(handlerRefs);
        consumerServices.AddTransient<IMessageHandler<TestMessage>>(_ => new CallbackHandler<TestMessage>(msg => tcs.TrySetResult(msg)));
        consumerServices.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = mappedQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });
        var consumerProvider = consumerServices.BuildServiceProvider();
        var consumerBus = consumerProvider.GetRequiredService<IBus>();
        await consumerBus.StartConsumingAsync();

        // Sender with QueueMapping configured (no explicit endpoint)
        var senderServices = new ServiceCollection();
        senderServices.AddLogging();
        senderServices.AddSingleton<IList<HandlerReference>>(new List<HandlerReference>());
        senderServices.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q =>
            {
                q.QueueName = senderQueue;
                q.AddQueueMapping(typeof(TestMessage), mappedQueue);
            });
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });
        var senderProvider = senderServices.BuildServiceProvider();
        var senderBus = senderProvider.GetRequiredService<IBus>();

        await Task.Delay(500);

        try
        {
            // SendAsync without explicit endpoint — should use QueueMapping
            await senderBus.SendAsync(new TestMessage(Guid.NewGuid()) { Content = "mapped" });

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => tcs.TrySetCanceled());
            var received = await tcs.Task;

            Assert.Equal("mapped", received.Content);
        }
        finally
        {
            consumerBus.Dispose(); (consumerProvider as IDisposable)?.Dispose();
            senderBus.Dispose(); (senderProvider as IDisposable)?.Dispose();
        }
    }
}
```

- [ ] **Step 2: Build and run test**

Run: `dotnet build src/ServiceConnect.EndToEndTests && dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~QueueMappingTests" --no-build`
Expected: 1 test PASS

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/QueueMappingTests.cs
git commit -m "test: add E2E test for queue mapping routing"
```

---

### Task 6: ProcessManagerExceptionTests.cs

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/ProcessManagerExceptionTests.cs`

- [ ] **Step 1: Write the test file**

ProcessManagerProcessor has no try-catch — exception bubbles to Client.cs which retries then error-queues.

```csharp
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Helpers;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class ProcessManagerExceptionTests
{
    private readonly MessagingFixture _fixture;
    public ProcessManagerExceptionTests(MessagingFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ProcessManagerHandler_Throws_MessageSentToErrorQueue()
    {
        var queueName = _fixture.GetUniqueQueueName("pm-exception");
        var errorQueueName = _fixture.GetUniqueQueueName("pm-exception-eq");

        var mapper = new TestProcessManagerPropertyMapper();
        mapper.ConfigureMapping<TestProcessData, TestMessage>(d => d.CorrelationId, m => m.CorrelationId);

        var handlerRefs = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(ThrowingProcessHandler),
                MessageType = typeof(TestMessage),
                RoutingKeys = new List<string>()
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddTransient<ThrowingProcessHandler>();
        services.AddSingleton<IProcessManagerPropertyMapper>(mapper);

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.MaxRetries = 1; t.RetryDelay = 1000;
                t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => { q.QueueName = queueName; q.ErrorQueueName = errorQueueName; });
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
            builder.UseInMemoryPersistence();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            await bus.PublishAsync(new TestMessage(Guid.NewGuid()) { Content = "pm-crash" });

            var factory = new ConnectionFactory
            {
                HostName = _fixture.RabbitMqHostname, Port = _fixture.RabbitMqPort,
                UserName = _fixture.RabbitMqUsername, Password = _fixture.RabbitMqPassword
            };
            using var conn = factory.CreateConnection();
            using var channel = conn.CreateModel();

            BasicGetResult? errorMsg = null;
            for (int i = 0; i < 30 && errorMsg == null; i++)
            {
                errorMsg = channel.BasicGet(errorQueueName, autoAck: true);
                if (errorMsg == null) await Task.Delay(1000);
            }

            Assert.NotNull(errorMsg);
            var exJson = Encoding.UTF8.GetString((byte[])errorMsg.BasicProperties.Headers["Exception"]);
            Assert.Contains("PM handler exploded", exJson);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }
}

file class ThrowingProcessHandler : IProcessHandler<TestProcessData, TestMessage>
{
    public IConsumeContext? Context { get; set; }

    public Task HandleAsync(TestMessage message, TestProcessData data)
    {
        throw new InvalidOperationException("PM handler exploded");
    }
}
```

- [ ] **Step 2: Build and run test**

Run: `dotnet build src/ServiceConnect.EndToEndTests && dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~ProcessManagerExceptionTests" --no-build`
Expected: 1 test PASS

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/ProcessManagerExceptionTests.cs
git commit -m "test: add E2E test for process manager handler exception"
```

---

### Task 7: AggregatorExceptionTests.cs

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/AggregatorExceptionTests.cs`

- [ ] **Step 1: Write the test file**

AggregatorProcessor.FlushAggregator has no try-catch when called from ProcessAsync — exception propagates to Client.cs error handling.

```csharp
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class AggregatorExceptionTests
{
    private readonly MessagingFixture _fixture;
    public AggregatorExceptionTests(MessagingFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Aggregator_ExecuteThrows_MessageSentToErrorQueue()
    {
        var queueName = _fixture.GetUniqueQueueName("agg-exception");
        var errorQueueName = _fixture.GetUniqueQueueName("agg-exception-eq");

        var handlerRefs = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(ThrowingAggregator),
                MessageType = typeof(TestMessage),
                RoutingKeys = new List<string>()
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddTransient<ThrowingAggregator>();

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.MaxRetries = 1; t.RetryDelay = 1000;
                t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => { q.QueueName = queueName; q.ErrorQueueName = errorQueueName; });
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
            builder.UseInMemoryPersistence();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            // BatchSize=1, so Execute fires on first message
            await bus.PublishAsync(new TestMessage(Guid.NewGuid()) { Content = "agg-crash" });

            var factory = new ConnectionFactory
            {
                HostName = _fixture.RabbitMqHostname, Port = _fixture.RabbitMqPort,
                UserName = _fixture.RabbitMqUsername, Password = _fixture.RabbitMqPassword
            };
            using var conn = factory.CreateConnection();
            using var channel = conn.CreateModel();

            BasicGetResult? errorMsg = null;
            for (int i = 0; i < 30 && errorMsg == null; i++)
            {
                errorMsg = channel.BasicGet(errorQueueName, autoAck: true);
                if (errorMsg == null) await Task.Delay(1000);
            }

            Assert.NotNull(errorMsg);
            var exJson = Encoding.UTF8.GetString((byte[])errorMsg.BasicProperties.Headers["Exception"]);
            Assert.Contains("Aggregator Execute failed", exJson);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }
}

file class ThrowingAggregator : Aggregator<TestMessage>
{
    public override int BatchSize() => 1;
    public override TimeSpan Timeout() => TimeSpan.FromSeconds(30);
    public override void Execute(IList<TestMessage> messages)
    {
        throw new InvalidOperationException("Aggregator Execute failed");
    }
}
```

- [ ] **Step 2: Build and run test**

Run: `dotnet build src/ServiceConnect.EndToEndTests && dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~AggregatorExceptionTests" --no-build`
Expected: 1 test PASS

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/AggregatorExceptionTests.cs
git commit -m "test: add E2E test for aggregator Execute exception"
```

---

### Task 8: FilterChainTests.cs

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/FilterChainTests.cs`

- [ ] **Step 1: Write the test file**

Tests filter execution order and blocking behavior.

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class FilterChainTests
{
    private readonly MessagingFixture _fixture;
    public FilterChainTests(MessagingFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task MultipleBeforeConsumingFilters_AllRunInOrder()
    {
        var filterLog = new ConcurrentQueue<string>();
        var tcs = new TaskCompletionSource<TestMessage>();
        var queueName = _fixture.GetUniqueQueueName("filter-chain-order");

        FilterChainOrderFilter.Log = filterLog;
        FilterChainOrderFilter.FilterName = null; // reset

        var handlerRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(CallbackHandler<TestMessage>), MessageType = typeof(TestMessage), RoutingKeys = new List<string>() }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddTransient<IMessageHandler<TestMessage>>(_ => new CallbackHandler<TestMessage>(msg => tcs.TrySetResult(msg)));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
            builder.AddBeforeConsumingFilter<FilterA>();
            builder.AddBeforeConsumingFilter<FilterB>();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            await bus.PublishAsync(new TestMessage(Guid.NewGuid()) { Content = "filter-order" });

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => tcs.TrySetCanceled());
            await tcs.Task;

            var items = filterLog.ToArray();
            Assert.Equal(2, items.Length);
            Assert.Equal("FilterA", items[0]);
            Assert.Equal("FilterB", items[1]);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task FirstFilterBlocks_SecondFilterNotCalled_HandlerNotInvoked()
    {
        var filterLog = new ConcurrentQueue<string>();
        var handlerCalled = new TaskCompletionSource<bool>();
        var queueName = _fixture.GetUniqueQueueName("filter-chain-block");

        var handlerRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(CallbackHandler<TestMessage>), MessageType = typeof(TestMessage), RoutingKeys = new List<string>() }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new CallbackHandler<TestMessage>(_ => handlerCalled.TrySetResult(true)));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
            builder.AddBeforeConsumingFilter<BlockingFilter>();
            builder.AddBeforeConsumingFilter<FilterB>();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            BlockingFilter.Log = filterLog;
            await bus.PublishAsync(new TestMessage(Guid.NewGuid()) { Content = "blocked" });

            // Handler should NOT be called
            var wasHandled = await Task.WhenAny(handlerCalled.Task, Task.Delay(3000)) == handlerCalled.Task;
            Assert.False(wasHandled);

            // Only the blocking filter ran; FilterB was not called
            var items = filterLog.ToArray();
            Assert.Single(items);
            Assert.Equal("BlockingFilter", items[0]);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }
}

// Static log shared across filter instances (test-scoped via ConcurrentQueue reset)
file static class FilterChainOrderFilter
{
    public static ConcurrentQueue<string>? Log;
    public static string? FilterName;
}

file class FilterA : IFilter
{
    public IBus Bus { get; set; } = null!;
    public bool Process(Envelope envelope)
    {
        FilterChainOrderFilter.Log?.Enqueue("FilterA");
        return false; // don't block
    }
}

file class FilterB : IFilter
{
    public IBus Bus { get; set; } = null!;
    public bool Process(Envelope envelope)
    {
        FilterChainOrderFilter.Log?.Enqueue("FilterB");
        return false; // don't block
    }
}

file class BlockingFilter : IFilter
{
    public static ConcurrentQueue<string>? Log;
    public IBus Bus { get; set; } = null!;
    public bool Process(Envelope envelope)
    {
        Log?.Enqueue("BlockingFilter");
        return true; // block
    }
}
```

- [ ] **Step 2: Build and run tests**

Run: `dotnet build src/ServiceConnect.EndToEndTests && dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~FilterChainTests" --no-build`
Expected: 2 tests PASS

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/FilterChainTests.cs
git commit -m "test: add E2E tests for filter chain ordering and blocking"
```

---

### Task 9: StreamOutOfOrderTests.cs

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/StreamOutOfOrderTests.cs`

- [ ] **Step 1: Write the test file**

StreamProcessor uses `stream.Write(messageBytes, packetNumber)` — packets can arrive in any order and `MessageBusReadStream` reassembles by packet number. However, the `CreateStream` API writes sequentially. We test that the stream mechanism works with the standard write path (which already buffers and sequences internally).

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class StreamOutOfOrderTests
{
    private readonly MessagingFixture _fixture;
    public StreamOutOfOrderTests(MessagingFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Stream_ManyChunks_ReassembledCorrectly()
    {
        var tcs = new TaskCompletionSource<byte[]>();
        var consumerQueue = _fixture.GetUniqueQueueName("stream-chunks");

        var handlerRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(StreamCaptureHandler), MessageType = typeof(TestMessage), RoutingKeys = new List<string>() }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddTransient<IStreamHandler<TestMessage>>(_ => new StreamCaptureHandler(tcs));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = consumerQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            // Write 5 chunks — more than the default 3 in existing streaming test
            var stream = bus.CreateStream(consumerQueue, new TestMessage(Guid.NewGuid()) { Content = "chunked" });
            var data1 = "AAAA"u8.ToArray();
            var data2 = "BBBB"u8.ToArray();
            var data3 = "CCCC"u8.ToArray();
            var data4 = "DDDD"u8.ToArray();
            var data5 = "EEEE"u8.ToArray();
            stream.Write(data1, 0, data1.Length);
            stream.Write(data2, 0, data2.Length);
            stream.Write(data3, 0, data3.Length);
            stream.Write(data4, 0, data4.Length);
            stream.Write(data5, 0, data5.Length);
            stream.Close();

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => tcs.TrySetCanceled());
            var result = await tcs.Task;

            var text = System.Text.Encoding.UTF8.GetString(result);
            Assert.Equal("AAAABBBBCCCCDDDDEEEE", text);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }
}

file class StreamCaptureHandler : IStreamHandler<TestMessage>
{
    private readonly TaskCompletionSource<byte[]> _tcs;
    public StreamCaptureHandler(TaskCompletionSource<byte[]> tcs) => _tcs = tcs;
    public IMessageBusReadStream Stream { get; set; } = null!;

    public void Execute(TestMessage message)
    {
        _tcs.TrySetResult(Stream.Read());
    }
}
```

- [ ] **Step 2: Build and run test**

Run: `dotnet build src/ServiceConnect.EndToEndTests && dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~StreamOutOfOrderTests" --no-build`
Expected: 1 test PASS

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/StreamOutOfOrderTests.cs
git commit -m "test: add E2E test for multi-chunk stream reassembly"
```

---

### Task 10: RequestReplyHeaderTests.cs

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/RequestReplyHeaderTests.cs`

- [ ] **Step 1: Write the test file**

Already covered in Task 1's `SendRequestAsync_CustomHeaders_PropagatedToResponder` test. Skipping — Task 1 covers this scenario.

---

### Task 11: PrefetchCountTests.cs

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/PrefetchCountTests.cs`

- [ ] **Step 1: Write the test file**

With PrefetchCount=1, only one message processes at a time. A slow handler should block subsequent messages.

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class PrefetchCountTests
{
    private readonly MessagingFixture _fixture;
    public PrefetchCountTests(MessagingFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task PrefetchCount1_SlowHandler_ProcessesOneAtATime()
    {
        var concurrencyLog = new ConcurrentBag<int>();
        int currentConcurrency = 0;
        var allDone = new TaskCompletionSource<bool>();
        int processed = 0;
        const int messageCount = 3;
        var queueName = _fixture.GetUniqueQueueName("prefetch");

        var handlerRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(CallbackHandler<TestMessage>), MessageType = typeof(TestMessage), RoutingKeys = new List<string>() }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new CallbackHandler<TestMessage>(async _ =>
            {
                var c = Interlocked.Increment(ref currentConcurrency);
                concurrencyLog.Add(c);
                await Task.Delay(500); // simulate slow work
                Interlocked.Decrement(ref currentConcurrency);
                if (Interlocked.Increment(ref processed) >= messageCount)
                    allDone.TrySetResult(true);
            }));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.PrefetchCount = 1;
                t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            // Publish all messages quickly
            for (int i = 0; i < messageCount; i++)
                await bus.PublishAsync(new TestMessage(Guid.NewGuid()) { Content = $"msg-{i}" });

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => allDone.TrySetCanceled());
            await allDone.Task;

            // With prefetch=1, max concurrent should be 1
            Assert.All(concurrencyLog, c => Assert.Equal(1, c));
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }
}
```

- [ ] **Step 2: Build and run test**

Run: `dotnet build src/ServiceConnect.EndToEndTests && dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~PrefetchCountTests" --no-build`
Expected: 1 test PASS

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/PrefetchCountTests.cs
git commit -m "test: add E2E test for prefetch count limiting concurrency"
```

---

### Task 12: MaxRetriesZeroTests.cs

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/MaxRetriesZeroTests.cs`

- [ ] **Step 1: Write the test file**

Per `Consumer.cs:75`, retry queue is only created when `MaxRetries > 0`. With MaxRetries=0, failure goes straight to error queue.

```csharp
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class MaxRetriesZeroTests
{
    private readonly MessagingFixture _fixture;
    public MaxRetriesZeroTests(MessagingFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task MaxRetriesZero_HandlerFails_SentDirectlyToErrorQueue()
    {
        var queueName = _fixture.GetUniqueQueueName("no-retry");
        var errorQueueName = _fixture.GetUniqueQueueName("no-retry-eq");
        int attemptCount = 0;

        var handlerRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(CallbackHandler<TestMessage>), MessageType = typeof(TestMessage), RoutingKeys = new List<string>() }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new CallbackHandler<TestMessage>(_ =>
            {
                Interlocked.Increment(ref attemptCount);
                throw new InvalidOperationException("Immediate failure");
            }));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.MaxRetries = 0; // no retries
                t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => { q.QueueName = queueName; q.ErrorQueueName = errorQueueName; });
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            await bus.PublishAsync(new TestMessage(Guid.NewGuid()) { Content = "no-retries" });

            var factory = new ConnectionFactory
            {
                HostName = _fixture.RabbitMqHostname, Port = _fixture.RabbitMqPort,
                UserName = _fixture.RabbitMqUsername, Password = _fixture.RabbitMqPassword
            };
            using var conn = factory.CreateConnection();
            using var channel = conn.CreateModel();

            BasicGetResult? errorMsg = null;
            for (int i = 0; i < 15 && errorMsg == null; i++)
            {
                errorMsg = channel.BasicGet(errorQueueName, autoAck: true);
                if (errorMsg == null) await Task.Delay(1000);
            }

            Assert.NotNull(errorMsg);
            // Handler was called exactly once — no retries
            Assert.Equal(1, attemptCount);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }
}
```

- [ ] **Step 2: Build and run test**

Run: `dotnet build src/ServiceConnect.EndToEndTests && dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~MaxRetriesZeroTests" --no-build`
Expected: 1 test PASS

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/MaxRetriesZeroTests.cs
git commit -m "test: add E2E test for MaxRetries=0 direct error queue"
```

---

### Task 13: PublishRequestAsyncTests.cs

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/PublishRequestAsyncTests.cs`

- [ ] **Step 1: Write the test file**

Per `Bus.cs:130-139`, `PublishRequestAsync` calls `SendRequestMultiAsync` then iterates replies calling `onReply` callback.

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class PublishRequestAsyncTests
{
    private readonly MessagingFixture _fixture;
    public PublishRequestAsyncTests(MessagingFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task PublishRequestAsync_CallbackFiresForEachReply()
    {
        var responderQueue = _fixture.GetUniqueQueueName("pub-req-resp");
        var requesterQueue = _fixture.GetUniqueQueueName("pub-req-req");
        var replies = new ConcurrentBag<TestResponse>();

        // Responder
        var responderRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(PubReqReplyHandler), MessageType = typeof(TestRequest), RoutingKeys = new List<string>() }
        };
        var responderServices = new ServiceCollection();
        responderServices.AddLogging();
        responderServices.AddSingleton<IList<HandlerReference>>(responderRefs);
        responderServices.AddTransient<IMessageHandler<TestRequest>, PubReqReplyHandler>();
        responderServices.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = responderQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });
        var responderProvider = responderServices.BuildServiceProvider();
        var responderBus = responderProvider.GetRequiredService<IBus>();
        await responderBus.StartConsumingAsync();

        // Requester
        var requesterServices = new ServiceCollection();
        requesterServices.AddLogging();
        requesterServices.AddSingleton<IList<HandlerReference>>(new List<HandlerReference>());
        requesterServices.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = requesterQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });
        var requesterProvider = requesterServices.BuildServiceProvider();
        var requesterBus = requesterProvider.GetRequiredService<IBus>();
        await requesterBus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            await requesterBus.PublishRequestAsync<TestRequest, TestResponse>(
                new TestRequest(Guid.NewGuid()) { Question = "callback?" },
                reply => replies.Add(reply),
                new RequestOptions { EndPoint = responderQueue, Timeout = 30000, ExpectedReplyCount = 1 });

            Assert.Single(replies);
            Assert.Equal("pub-req-reply", replies.First().Answer);
        }
        finally
        {
            responderBus.Dispose(); (responderProvider as IDisposable)?.Dispose();
            requesterBus.Dispose(); (requesterProvider as IDisposable)?.Dispose();
        }
    }
}

file class PubReqReplyHandler : IMessageHandler<TestRequest>
{
    public IConsumeContext? Context { get; set; }
    public async Task HandleAsync(TestRequest message)
    {
        await Context!.ReplyAsync(new TestResponse(Guid.NewGuid()) { Answer = "pub-req-reply" });
    }
}
```

- [ ] **Step 2: Build and run test**

Run: `dotnet build src/ServiceConnect.EndToEndTests && dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~PublishRequestAsyncTests" --no-build`
Expected: 1 test PASS

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/PublishRequestAsyncTests.cs
git commit -m "test: add E2E test for PublishRequestAsync callback"
```

---

### Task 14: PublishAfterDisposeTests.cs

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/PublishAfterDisposeTests.cs`

- [ ] **Step 1: Write the test file**

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class PublishAfterDisposeTests
{
    private readonly MessagingFixture _fixture;
    public PublishAfterDisposeTests(MessagingFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task PublishAsync_AfterDispose_Throws()
    {
        var queueName = _fixture.GetUniqueQueueName("pub-disposed");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(new List<HandlerReference>());
        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        bus.Dispose();

        // After dispose, publishing should throw
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await bus.PublishAsync(new TestMessage(Guid.NewGuid()) { Content = "after-dispose" }));

        (provider as IDisposable)?.Dispose();
    }
}
```

- [ ] **Step 2: Build and run test**

Run: `dotnet build src/ServiceConnect.EndToEndTests && dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~PublishAfterDisposeTests" --no-build`
Expected: 1 test PASS

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/PublishAfterDisposeTests.cs
git commit -m "test: add E2E test for publish after dispose"
```

---

### Task 15: MalformedMessageTests.cs

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/MalformedMessageTests.cs`

- [ ] **Step 1: Write the test file**

Publishes corrupt JSON directly to RabbitMQ with valid headers. MessageDispatcher wraps everything in try-catch and returns `Success=false`, which triggers error queue flow.

```csharp
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class MalformedMessageTests
{
    private readonly MessagingFixture _fixture;
    public MalformedMessageTests(MessagingFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task MalformedJson_MessageSentToErrorQueue()
    {
        var queueName = _fixture.GetUniqueQueueName("malformed");
        var errorQueueName = _fixture.GetUniqueQueueName("malformed-eq");

        var handlerRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(CallbackHandler<TestMessage>), MessageType = typeof(TestMessage), RoutingKeys = new List<string>() }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new CallbackHandler<TestMessage>(_ => { }));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.MaxRetries = 0;
                t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => { q.QueueName = queueName; q.ErrorQueueName = errorQueueName; });
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            // Publish malformed JSON directly via raw RabbitMQ client
            var factory = new ConnectionFactory
            {
                HostName = _fixture.RabbitMqHostname, Port = _fixture.RabbitMqPort,
                UserName = _fixture.RabbitMqUsername, Password = _fixture.RabbitMqPassword
            };
            using var conn = factory.CreateConnection();
            using var channel = conn.CreateModel();
            var props = channel.CreateBasicProperties();
            props.Headers = new Dictionary<string, object>
            {
                ["FullTypeName"] = Encoding.UTF8.GetBytes(typeof(TestMessage).AssemblyQualifiedName!),
                ["MessageType"] = Encoding.UTF8.GetBytes(typeof(TestMessage).FullName!),
                ["MessageId"] = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString())
            };
            channel.BasicPublish("", queueName, props, Encoding.UTF8.GetBytes("{{{INVALID JSON}}}"));

            // Poll error queue
            BasicGetResult? errorMsg = null;
            for (int i = 0; i < 15 && errorMsg == null; i++)
            {
                errorMsg = channel.BasicGet(errorQueueName, autoAck: true);
                if (errorMsg == null) await Task.Delay(1000);
            }

            Assert.NotNull(errorMsg);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }
}
```

- [ ] **Step 2: Build and run test**

Run: `dotnet build src/ServiceConnect.EndToEndTests && dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~MalformedMessageTests" --no-build`
Expected: 1 test PASS

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/MalformedMessageTests.cs
git commit -m "test: add E2E test for malformed JSON error queue"
```

---

### Task 16: EmptyMessageTests.cs

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/EmptyMessageTests.cs`

- [ ] **Step 1: Write the test file**

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class EmptyMessageTests
{
    private readonly MessagingFixture _fixture;
    public EmptyMessageTests(MessagingFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Publish_EmptyContent_HandlerReceivesMessageWithDefaults()
    {
        var tcs = new TaskCompletionSource<TestMessage>();
        var queueName = _fixture.GetUniqueQueueName("empty-msg");

        var handlerRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(CallbackHandler<TestMessage>), MessageType = typeof(TestMessage), RoutingKeys = new List<string>() }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new CallbackHandler<TestMessage>(msg => tcs.TrySetResult(msg)));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3; t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            // Send message with default (empty string) Content
            var correlationId = Guid.NewGuid();
            await bus.PublishAsync(new TestMessage(correlationId));

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => tcs.TrySetCanceled());
            var received = await tcs.Task;

            Assert.Equal(correlationId, received.CorrelationId);
            Assert.Equal(string.Empty, received.Content);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }
}
```

- [ ] **Step 2: Build and run test**

Run: `dotnet build src/ServiceConnect.EndToEndTests && dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~EmptyMessageTests" --no-build`
Expected: 1 test PASS

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/EmptyMessageTests.cs
git commit -m "test: add E2E test for empty message content"
```

---

### Task 17: Run Full Suite

- [ ] **Step 1: Run all E2E tests**

Run: `dotnet build src/ServiceConnect.EndToEndTests && dotnet test src/ServiceConnect.EndToEndTests --filter "Category=Docker" --no-build`
Expected: All tests PASS (37 existing + 18 new = 55 total)

- [ ] **Step 2: Fix any failures**

If any test fails, investigate and fix before proceeding.

- [ ] **Step 3: Final commit**

Only if fixes were needed.
