# Deferred E2E Tests Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the 5 unblocked deferred E2E tests: CompetingConsumers, PriorityQueues, ScatterGather, MessageDeduplication, and PolymorphicMessages.

**Architecture:** Each test follows the existing E2E pattern — `[Collection(nameof(MessagingCollection))]`, Docker trait, `MessagingFixture` for RabbitMQ container, `CallbackHandler<T>` for capturing messages. PolymorphicMessages requires a small enhancement to `MessageDispatcher` to walk the type hierarchy. MessageDeduplication uses a test-local `IFilter` (the existing filter project uses legacy `Common.Logging` and NuGet-referenced interfaces — not compatible).

**Tech Stack:** xUnit 2.9.2, Testcontainers.RabbitMq, ServiceConnect.Client.RabbitMQ, Moq 4.20.72

---

## File Structure

| File | Responsibility |
|------|---------------|
| `src/ServiceConnect.EndToEndTests/CompetingConsumersTests.cs` | E2E: two consumers on same queue, each message delivered to exactly one |
| `src/ServiceConnect.EndToEndTests/PriorityQueueTests.cs` | E2E: priority queue ordering with `x-max-priority` argument |
| `src/ServiceConnect.EndToEndTests/ScatterGatherTests.cs` | E2E: `SendRequestMultiAsync` with multiple responders |
| `src/ServiceConnect.EndToEndTests/MessageDeduplicationTests.cs` | E2E: dedup filter blocks redelivered duplicate messages |
| `src/ServiceConnect.EndToEndTests/PolymorphicMessageTests.cs` | E2E: handler for base type receives derived message |
| `src/ServiceConnect.EndToEndTests/Messages/TestMessage.cs` | Add `DerivedTestMessage` class |
| `src/ServiceConnect/Services/MessageDispatcher.cs` | Add type hierarchy walking for handler resolution |
| `src/ServiceConnect.UnitTests/MessageDispatcherTests.cs` | Unit tests for polymorphic dispatch |

---

### Task 1: Competing Consumers E2E Test

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/CompetingConsumersTests.cs`

Two bus instances consume from the same queue. Publish N messages. Verify the union of received messages across both consumers equals all N messages with no duplicates.

- [ ] **Step 1: Create the test file**

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using System.Collections.Concurrent;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class CompetingConsumersTests
{
    private readonly MessagingFixture _fixture;

    public CompetingConsumersTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Publish_TwoConsumersSameQueue_EachMessageDeliveredOnce()
    {
        // Arrange
        var queueName = _fixture.GetUniqueQueueName("competing");
        var allReceived = new ConcurrentBag<string>();
        var allDone = new TaskCompletionSource<bool>();
        const int messageCount = 10;
        int totalReceived = 0;

        IBus CreateConsumerBus()
        {
            var handlerRefs = new List<HandlerReference>
            {
                new() { HandlerType = typeof(CallbackHandler<TestMessage>), MessageType = typeof(TestMessage), RoutingKeys = new List<string>() }
            };

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IList<HandlerReference>>(handlerRefs);
            services.AddTransient<IMessageHandler<TestMessage>>(_ =>
                new CallbackHandler<TestMessage>(msg =>
                {
                    allReceived.Add(msg.Content);
                    if (Interlocked.Increment(ref totalReceived) >= messageCount)
                        allDone.TrySetResult(true);
                }));

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
            return provider.GetRequiredService<IBus>();
        }

        var bus1 = CreateConsumerBus();
        var bus2 = CreateConsumerBus();

        await bus1.StartConsumingAsync();
        await bus2.StartConsumingAsync();
        await Task.Delay(500);

        // We need a separate producer bus (its own queue) to send messages
        var producerServices = new ServiceCollection();
        producerServices.AddLogging();
        producerServices.AddSingleton<IList<HandlerReference>>(new List<HandlerReference>());
        producerServices.AddServiceConnect(builder =>
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
            builder.ConfigureQueues(q => q.QueueName = _fixture.GetUniqueQueueName("competing-producer"));
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });
        var producerProvider = producerServices.BuildServiceProvider();
        var producerBus = producerProvider.GetRequiredService<IBus>();

        try
        {
            // Act: send N messages to the shared queue
            for (int i = 0; i < messageCount; i++)
            {
                await producerBus.SendAsync(
                    new TestMessage(Guid.NewGuid()) { Content = $"msg-{i}" },
                    new SendOptions { EndPoint = queueName });
            }

            // Assert: wait for all messages to be received
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => allDone.TrySetCanceled());
            await allDone.Task;

            // All messages received exactly once (no duplicates, no missing)
            var sorted = allReceived.OrderBy(x => x).ToList();
            var expected = Enumerable.Range(0, messageCount).Select(i => $"msg-{i}").OrderBy(x => x).ToList();
            Assert.Equal(expected, sorted);
        }
        finally
        {
            bus1.Dispose();
            bus2.Dispose();
            producerBus.Dispose();
            (producerProvider as IDisposable)?.Dispose();
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it passes**

Run: `dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~CompetingConsumersTests" -v n`
Expected: 1 test PASS

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/CompetingConsumersTests.cs
git commit -m "test: add competing consumers E2E test"
```

---

### Task 2: Priority Queue E2E Test

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/PriorityQueueTests.cs`

Configure a queue with `x-max-priority` argument. Send messages with different priorities. Consume them and verify higher-priority messages are consumed before lower-priority ones.

**Important context:** RabbitMQ priority queues only guarantee ordering when messages are queued before consumption begins. The test publishes all messages first, then starts consuming.

- [ ] **Step 1: Create the test file**

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using System.Collections.Concurrent;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class PriorityQueueTests
{
    private readonly MessagingFixture _fixture;

    public PriorityQueueTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Send_PriorityMessages_HigherPriorityConsumedFirst()
    {
        // Arrange
        var queueName = _fixture.GetUniqueQueueName("priority");
        var received = new ConcurrentQueue<int>();
        var allDone = new TaskCompletionSource<bool>();
        const int messageCount = 6;
        int totalReceived = 0;

        // Step 1: publish messages with different priorities BEFORE starting consumer
        // This ensures they queue up and RabbitMQ can order by priority.
        var producerServices = new ServiceCollection();
        producerServices.AddLogging();
        producerServices.AddSingleton<IList<HandlerReference>>(new List<HandlerReference>());
        producerServices.AddServiceConnect(builder =>
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
            builder.ConfigureQueues(q => q.QueueName = _fixture.GetUniqueQueueName("priority-producer"));
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });
        var producerProvider = producerServices.BuildServiceProvider();
        var producerBus = producerProvider.GetRequiredService<IBus>();

        // Declare the priority queue manually before sending
        // The consumer will also declare it, but we need it to exist for sends
        var connFactory = new global::RabbitMQ.Client.ConnectionFactory
        {
            HostName = _fixture.RabbitMqHostname,
            Port = _fixture.RabbitMqPort,
            UserName = _fixture.RabbitMqUsername,
            Password = _fixture.RabbitMqPassword
        };
        using (var conn = connFactory.CreateConnection())
        using (var model = conn.CreateModel())
        {
            model.QueueDeclare(queueName, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object> { { "x-max-priority", 10 } });
        }

        // Send messages with alternating priorities: low (1), high (10)
        // Interleave so that without priority, they'd arrive in send order
        var sendOrder = new[] { 1, 10, 1, 10, 1, 10 }; // priorities
        for (int i = 0; i < messageCount; i++)
        {
            var headers = new Dictionary<string, string> { ["Priority"] = sendOrder[i].ToString() };
            await producerBus.SendAsync(
                new PriorityMessage(Guid.NewGuid()) { Priority = sendOrder[i], Order = i },
                new SendOptions { EndPoint = queueName, Headers = headers });
        }

        // Step 2: now start consumer — it should get high-priority messages first
        var handlerRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(CallbackHandler<PriorityMessage>), MessageType = typeof(PriorityMessage), RoutingKeys = new List<string>() }
        };

        var consumerServices = new ServiceCollection();
        consumerServices.AddLogging();
        consumerServices.AddSingleton<IList<HandlerReference>>(handlerRefs);
        consumerServices.AddTransient<IMessageHandler<PriorityMessage>>(_ =>
            new CallbackHandler<PriorityMessage>(msg =>
            {
                received.Enqueue(msg.Priority);
                if (Interlocked.Increment(ref totalReceived) >= messageCount)
                    allDone.TrySetResult(true);
            }));

        consumerServices.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
                // Must declare with same x-max-priority argument
                t.ClientSettings["Arguments"] = new Dictionary<string, object> { { "x-max-priority", 10 } };
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var consumerProvider = consumerServices.BuildServiceProvider();
        var consumerBus = consumerProvider.GetRequiredService<IBus>();

        try
        {
            await consumerBus.StartConsumingAsync();

            // Wait for all messages
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => allDone.TrySetCanceled());
            await allDone.Task;

            // Assert: high priority (10) messages should come before low priority (1)
            var receivedList = received.ToList();
            Assert.Equal(messageCount, receivedList.Count);

            // First 3 should be priority 10, last 3 should be priority 1
            Assert.All(receivedList.Take(3), p => Assert.Equal(10, p));
            Assert.All(receivedList.Skip(3), p => Assert.Equal(1, p));
        }
        finally
        {
            consumerBus.Dispose();
            producerBus.Dispose();
            (consumerProvider as IDisposable)?.Dispose();
            (producerProvider as IDisposable)?.Dispose();
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it passes**

Run: `dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~PriorityQueueTests" -v n`
Expected: 1 test PASS

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/PriorityQueueTests.cs
git commit -m "test: add priority queue E2E test"
```

---

### Task 3: Scatter/Gather E2E Test

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/ScatterGatherTests.cs`

Two responder bus instances consume `TestRequest` from different queues. The requester uses `SendRequestMultiAsync` to send to both, then collects both replies.

- [ ] **Step 1: Create the test file**

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class ScatterGatherTests
{
    private readonly MessagingFixture _fixture;

    public ScatterGatherTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task SendRequestMulti_TwoResponders_BothRepliesCollected()
    {
        // Arrange
        var responder1Queue = _fixture.GetUniqueQueueName("scatter-resp1");
        var responder2Queue = _fixture.GetUniqueQueueName("scatter-resp2");
        var requesterQueue = _fixture.GetUniqueQueueName("scatter-requester");

        // Helper to create a responder bus
        (IBus bus, ServiceProvider provider) CreateResponder(string queueName, string answerPrefix)
        {
            var handlerRefs = new List<HandlerReference>
            {
                new() { HandlerType = typeof(ScatterReplyHandler), MessageType = typeof(TestRequest), RoutingKeys = new List<string>() }
            };

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IList<HandlerReference>>(handlerRefs);
            services.AddTransient<IMessageHandler<TestRequest>>(_ =>
                new ScatterReplyHandler(answerPrefix));

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
            return (provider.GetRequiredService<IBus>(), provider);
        }

        var (responder1Bus, responder1Provider) = CreateResponder(responder1Queue, "Resp1");
        var (responder2Bus, responder2Provider) = CreateResponder(responder2Queue, "Resp2");

        await responder1Bus.StartConsumingAsync();
        await responder2Bus.StartConsumingAsync();

        // Requester bus
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
            // Act
            var request = new TestRequest(Guid.NewGuid()) { Question = "scatter-question" };
            var replies = await requesterBus.SendRequestMultiAsync<TestRequest, TestResponse>(
                request,
                new RequestOptions
                {
                    EndPoints = new List<string> { responder1Queue, responder2Queue },
                    ExpectedReplyCount = 2,
                    Timeout = 30000
                });

            // Assert
            Assert.Equal(2, replies.Count);
            var answers = replies.Select(r => r.Answer).OrderBy(a => a).ToList();
            Assert.Contains("Resp1: scatter-question", answers);
            Assert.Contains("Resp2: scatter-question", answers);
        }
        finally
        {
            requesterBus.Dispose();
            responder1Bus.Dispose();
            responder2Bus.Dispose();
            (requesterProvider as IDisposable)?.Dispose();
            (responder1Provider as IDisposable)?.Dispose();
            (responder2Provider as IDisposable)?.Dispose();
        }
    }
}

file class ScatterReplyHandler : IMessageHandler<TestRequest>
{
    private readonly string _prefix;

    public ScatterReplyHandler(string prefix) => _prefix = prefix;

    public IConsumeContext? Context { get; set; }

    public async Task HandleAsync(TestRequest message)
    {
        await Context!.ReplyAsync(new TestResponse(Guid.NewGuid())
        {
            Answer = $"{_prefix}: {message.Question}"
        });
    }
}
```

- [ ] **Step 2: Run the test to verify it passes**

Run: `dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~ScatterGatherTests" -v n`
Expected: 1 test PASS

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/ScatterGatherTests.cs
git commit -m "test: add scatter/gather E2E test with SendRequestMultiAsync"
```

---

### Task 4: Polymorphic Message Dispatch (Code + Tests)

**Files:**
- Modify: `src/ServiceConnect/Services/MessageDispatcher.cs:71-92`
- Modify: `src/ServiceConnect.EndToEndTests/Messages/TestMessage.cs`
- Create: `src/ServiceConnect.EndToEndTests/PolymorphicMessageTests.cs`
- Modify: `src/ServiceConnect.UnitTests/MessageDispatcherTests.cs`

The `MessageDispatcher` currently only resolves handlers for the exact message type. This task adds type hierarchy walking: after checking the exact type, walk up `BaseType` (stopping before `Message` and `object`) and also check implemented interfaces that extend `Message`.

- [ ] **Step 1: Add `DerivedTestMessage` to the messages file**

In `src/ServiceConnect.EndToEndTests/Messages/TestMessage.cs`, add after the `StepMessage` class:

```csharp
public class DerivedTestMessage : TestMessage
{
    public DerivedTestMessage(Guid correlationId) : base(correlationId) { }
    public string Extra { get; set; } = string.Empty;
}
```

- [ ] **Step 2: Write unit test for polymorphic dispatch**

In `src/ServiceConnect.UnitTests/MessageDispatcherTests.cs`, add this test. You'll need to look at the existing tests in that file to match the setup pattern (they use Moq to mock `IServiceProvider`, `IMessageSerializer`, `IFilterPipeline`, `IRequestReplyManager`).

**Important:** The unit test project does NOT reference the E2E test project, so you need to define local test message types. Add these file-scoped classes at the bottom of the test file:

```csharp
file class PolyBaseMessage : Message
{
    public PolyBaseMessage(Guid correlationId) : base(correlationId) { }
    public string Content { get; set; } = string.Empty;
}

file class PolyDerivedMessage : PolyBaseMessage
{
    public PolyDerivedMessage(Guid correlationId) : base(correlationId) { }
    public string Extra { get; set; } = string.Empty;
}

file class PolyBaseHandler : IMessageHandler<PolyBaseMessage>
{
    public bool Invoked { get; private set; }
    public IConsumeContext? Context { get; set; }
    public Task HandleAsync(PolyBaseMessage message) { Invoked = true; return Task.CompletedTask; }
}
```

The new test should:
1. Register a handler for `PolyBaseMessage` (the base type)
2. Dispatch a message whose `FullTypeName` header is `PolyDerivedMessage`
3. Assert the base type handler was invoked

```csharp
[Fact]
public async Task Dispatch_DerivedMessageType_InvokesBaseTypeHandler()
{
    // Arrange — same mock pattern as existing tests
    var handler = new PolyBaseHandler();

    var mockServiceProvider = new Mock<IServiceProvider>();
    var mockSerializer = new Mock<IMessageSerializer>();
    var mockFilterPipeline = new Mock<IFilterPipeline>();
    var mockReplyManager = new Mock<IRequestReplyManager>();
    var mockLogger = new Mock<ILogger<MessageDispatcher>>();
    var mockBus = new Mock<IBus>();

    // Return the handler when resolving IMessageHandler<PolyBaseMessage>
    var baseHandlerType = typeof(IMessageHandler<PolyBaseMessage>);
    mockServiceProvider
        .Setup(sp => sp.GetService(typeof(IEnumerable<>).MakeGenericType(baseHandlerType)))
        .Returns(new[] { handler });

    // Return empty for IMessageHandler<PolyDerivedMessage> (no exact match)
    var derivedHandlerType = typeof(IMessageHandler<PolyDerivedMessage>);
    mockServiceProvider
        .Setup(sp => sp.GetService(typeof(IEnumerable<>).MakeGenericType(derivedHandlerType)))
        .Returns(Array.Empty<object>());

    mockServiceProvider
        .Setup(sp => sp.GetService(typeof(IBus)))
        .Returns(mockBus.Object);

    var derivedMessage = new PolyDerivedMessage(Guid.NewGuid()) { Content = "derived", Extra = "extra" };
    var serializedBytes = System.Text.Encoding.UTF8.GetBytes("{}");

    mockSerializer
        .Setup(s => s.Deserialize(serializedBytes, typeof(PolyDerivedMessage)))
        .Returns(derivedMessage);

    mockFilterPipeline
        .Setup(f => f.ExecuteBeforeConsumingFilters(It.IsAny<Envelope>()))
        .Returns(false);

    var headers = new Dictionary<string, object>
    {
        [HeaderKeys.FullTypeName] = typeof(PolyDerivedMessage).AssemblyQualifiedName!
    };

    var dispatcher = new MessageDispatcher(
        mockServiceProvider.Object,
        mockSerializer.Object,
        mockFilterPipeline.Object,
        mockReplyManager.Object,
        mockLogger.Object);

    // Act
    var result = await dispatcher.Dispatch(serializedBytes, "PolyDerivedMessage", headers);

    // Assert
    Assert.True(result.Success);
    Assert.True(handler.Invoked, "Handler for base type PolyBaseMessage should have been invoked");
}
```

- [ ] **Step 3: Run the unit test to verify it fails**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~Dispatch_DerivedMessageType" -v n`
Expected: FAIL — handler not invoked because dispatcher only checks exact type

- [ ] **Step 4: Modify `MessageDispatcher.Dispatch` to walk type hierarchy**

In `src/ServiceConnect/Services/MessageDispatcher.cs`, replace lines 71-92 (the handler resolution and dispatch block) with:

```csharp
            // 5. Resolve handlers — check exact type, then walk up base types
            var allHandlers = new List<object>();
            var checkedType = type;
            while (checkedType != null && checkedType != typeof(Message) && checkedType != typeof(object))
            {
                var handlerInterfaceType = typeof(IMessageHandler<>).MakeGenericType(checkedType);
                var handlers = _serviceProvider.GetServices(handlerInterfaceType);
                foreach (var h in handlers)
                {
                    if (h != null)
                        allHandlers.Add(h);
                }
                checkedType = checkedType.BaseType;
            }

            if (allHandlers.Count == 0)
            {
                _logger.LogWarning("No handlers found for message type {MessageType}", type.FullName);
                return new ConsumeEventResult { Success = true };
            }

            // 6. Create ConsumeContext and dispatch to each handler
            var bus = _serviceProvider.GetRequiredService<IBus>();
            var context = new ConsumeContext(bus, headers);

            foreach (var handler in allHandlers)
            {
                var handlerType = handler.GetType();
                // Find the IMessageHandler<T> interface this handler implements
                var messageHandlerInterface = handlerType.GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IMessageHandler<>));

                if (messageHandlerInterface == null) continue;

                var contextProperty = messageHandlerInterface.GetProperty("Context");
                var handleAsyncMethod = messageHandlerInterface.GetMethod("HandleAsync");

                contextProperty?.SetValue(handler, context);

                var task = (Task?)handleAsyncMethod?.Invoke(handler, new[] { message });
                if (task != null)
                    await task;
            }
```

- [ ] **Step 5: Run the unit test to verify it passes**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~Dispatch_DerivedMessageType" -v n`
Expected: PASS

- [ ] **Step 6: Run ALL existing unit tests to verify no regressions**

Run: `dotnet test src/ServiceConnect.UnitTests -v n`
Expected: All tests PASS

- [ ] **Step 7: Create the E2E test**

Create `src/ServiceConnect.EndToEndTests/PolymorphicMessageTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class PolymorphicMessageTests
{
    private readonly MessagingFixture _fixture;

    public PolymorphicMessageTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Publish_DerivedMessage_BaseTypeHandlerReceivesIt()
    {
        // Arrange
        var tcs = new TaskCompletionSource<TestMessage>();
        var queueName = _fixture.GetUniqueQueueName("polymorphic");

        // Register handler for BASE type TestMessage, not DerivedTestMessage
        var handlerRefs = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(CallbackHandler<TestMessage>),
                MessageType = typeof(TestMessage),
                RoutingKeys = new List<string>()
            }
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
            // Act: publish a DERIVED message type
            var sent = new DerivedTestMessage(Guid.NewGuid())
            {
                Content = "polymorphic-test",
                Extra = "extra-data"
            };
            await bus.PublishAsync(sent);

            // Assert: base type handler receives the derived message
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => tcs.TrySetCanceled());

            var received = await tcs.Task;

            Assert.Equal("polymorphic-test", received.Content);
            Assert.IsType<DerivedTestMessage>(received);
            Assert.Equal("extra-data", ((DerivedTestMessage)received).Extra);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }
}
```

- [ ] **Step 8: Run the E2E test**

Run: `dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~PolymorphicMessageTests" -v n`
Expected: 1 test PASS

- [ ] **Step 9: Commit**

```bash
git add src/ServiceConnect/Services/MessageDispatcher.cs \
        src/ServiceConnect.EndToEndTests/Messages/TestMessage.cs \
        src/ServiceConnect.EndToEndTests/PolymorphicMessageTests.cs \
        src/ServiceConnect.UnitTests/MessageDispatcherTests.cs
git commit -m "feat: add polymorphic message dispatch with type hierarchy walking"
```

---

### Task 5: Message Deduplication E2E Test

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/MessageDeduplicationTests.cs`

The existing dedup filter project (`filters/`) uses legacy `Common.Logging` and a NuGet-referenced `ServiceConnect.Interfaces` v4.0.1 — it's not compatible with the current project. Instead, create a simple test-local `IFilter` implementation that checks the `Redelivered` header and tracks seen message IDs in a `ConcurrentDictionary`. Wire it as a `BeforeConsumingFilter` via `builder.AddBeforeConsumingFilter<T>()`.

The filter needs to be registered in DI so `FilterPipeline` can resolve it via `_serviceProvider.GetRequiredService(filterType)`.

- [ ] **Step 1: Create the test file with inline filter**

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using System.Collections.Concurrent;
using System.Text;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class MessageDeduplicationTests
{
    private readonly MessagingFixture _fixture;

    public MessageDeduplicationTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Consume_RedeliveredDuplicate_HandlerInvokedOnlyOnce()
    {
        // Arrange
        var receivedMessages = new ConcurrentBag<string>();
        var firstReceived = new TaskCompletionSource<bool>();
        var queueName = _fixture.GetUniqueQueueName("dedup");

        // Shared dedup filter instance — must be singleton so state persists across messages
        var dedupFilter = new TestDeduplicationFilter();

        var handlerRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(CallbackHandler<TestMessage>), MessageType = typeof(TestMessage), RoutingKeys = new List<string>() }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new CallbackHandler<TestMessage>(msg =>
            {
                receivedMessages.Add(msg.Content);
                firstReceived.TrySetResult(true);
            }));

        // Register the dedup filter as a singleton so it retains state
        services.AddSingleton<TestDeduplicationFilter>(dedupFilter);

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
            builder.AddBeforeConsumingFilter<TestDeduplicationFilter>();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            // Act: send first message (no Redelivered header — should process normally)
            var messageId = Guid.NewGuid().ToString();
            var msg1 = new TestMessage(Guid.NewGuid()) { Content = "dedup-test" };
            await bus.SendAsync(msg1, new SendOptions
            {
                EndPoint = queueName,
                Headers = new Dictionary<string, string> { ["MessageId"] = messageId }
            });

            // Wait for first message to be processed
            var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts1.Token.Register(() => firstReceived.TrySetCanceled());
            await firstReceived.Task;

            // Send same message again WITH Redelivered header (simulates redelivery)
            var msg2 = new TestMessage(Guid.NewGuid()) { Content = "dedup-test-duplicate" };
            await bus.SendAsync(msg2, new SendOptions
            {
                EndPoint = queueName,
                Headers = new Dictionary<string, string>
                {
                    ["MessageId"] = messageId,
                    ["Redelivered"] = "True"
                }
            });

            // Wait a bit for the second message to be (potentially) processed
            await Task.Delay(2000);

            // Assert: handler should only have been invoked once
            Assert.Single(receivedMessages);
            Assert.Equal("dedup-test", receivedMessages.First());
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }
}

/// <summary>
/// Simple test-only deduplication filter. Tracks seen MessageIds.
/// When a message arrives with Redelivered=true and a previously-seen MessageId,
/// the filter returns false (blocks processing).
/// </summary>
file class TestDeduplicationFilter : IFilter
{
    private readonly ConcurrentDictionary<string, byte> _seenMessageIds = new();

    public IBus Bus { get; set; } = null!;

    public bool Process(Envelope envelope)
    {
        // Extract MessageId from headers
        string? messageId = null;
        if (envelope.Headers.TryGetValue("MessageId", out var raw))
        {
            messageId = raw is byte[] bytes ? Encoding.UTF8.GetString(bytes) : raw?.ToString();
        }

        if (string.IsNullOrEmpty(messageId))
            return true; // no ID, can't deduplicate — allow processing

        // First time seeing this ID — record it and allow processing
        if (_seenMessageIds.TryAdd(messageId, 0))
            return true;

        // We've seen this ID before. Check if it's a redelivery.
        bool isRedelivered = false;
        if (envelope.Headers.TryGetValue("Redelivered", out var redeliveredRaw))
        {
            var redeliveredStr = redeliveredRaw is byte[] rb ? Encoding.UTF8.GetString(rb) : redeliveredRaw?.ToString();
            bool.TryParse(redeliveredStr, out isRedelivered);
        }

        // If redelivered duplicate — block processing
        return !isRedelivered;
    }
}
```

- [ ] **Step 2: Run the test to verify it passes**

Run: `dotnet test src/ServiceConnect.EndToEndTests --filter "FullyQualifiedName~MessageDeduplicationTests" -v n`
Expected: 1 test PASS

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/MessageDeduplicationTests.cs
git commit -m "test: add message deduplication E2E test"
```

---

## Post-Implementation

After all 5 tasks are complete:

1. Run all tests: `dotnet test -v n`
2. Update `docs/superpowers/notes/deferred-e2e-tests.md` — mark CompetingConsumers, PriorityQueues, ScatterGather, MessageDeduplication, and PolymorphicMessages as **DONE**. The remaining deferred items (Streaming, ProcessManager, Aggregator) stay as-is.
