# Transport Regression Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the RabbitMQ transport regressions in this branch so permanently invalid messages take a terminal error path, per-message publish/send regains retry-reconnect behavior, and consumer shutdown stops new work before channel teardown.

**Architecture:** Three sequential tasks. First, convert invalid-message handling from `nack + requeue` to `error publish + ack` by adding a dedicated terminal-failure path in `MessageRetryHandler` and routing the invalid cases in `RabbitMqConsumerHost` through it. Second, make `RabbitMqConsumerHost.DisposeAsync` cancel the consumer before waiting and close deterministically on timeout. Third, restore per-message retry/reconnect in `Producer` without changing `IProducer` or reverting the async refactor.

**Tech Stack:** C# / .NET, RabbitMQ.Client 7.x, xUnit, Moq, `Microsoft.Extensions.Time.Testing`, GitNexus CLI.

**Spec:** [docs/superpowers/specs/2026-04-18-branch-regression-remediation-design.md](../specs/2026-04-18-branch-regression-remediation-design.md)

---

## File Inventory

**Production:**
- `src/ServiceConnect.Client.RabbitMQ/MessageRetryHandler.cs` — add a terminal-failure helper and share the error-publish path
- `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs` — route permanently invalid inbound deliveries to the terminal-failure path and make shutdown deterministic
- `src/ServiceConnect.Client.RabbitMQ/Producer.cs` — restore per-message retry/reconnect around publish/send operations

**Unit tests:**
- `src/ServiceConnect.UnitTests/MessageRetryHandlerTests.cs` — add terminal-failure coverage
- `src/ServiceConnect.UnitTests/RabbitMqConsumerHostTests.cs` — flip invalid-message assertions to `publish + ack`, add missing-type and shutdown tests
- `src/ServiceConnect.UnitTests/ProducerLifecycleTests.cs` — retain current lifecycle coverage
- `src/ServiceConnect.UnitTests/ProducerRetryTests.cs` — create retry/reconnect coverage for transient publish/declare failures

**E2E tests:**
- `src/ServiceConnect.EndToEndTests/MalformedMessageTests.cs` — keep as the malformed-body regression test for terminal error routing
- `src/ServiceConnect.EndToEndTests/RetryAndErrorQueueTests.cs` — keep as the retry/error-queue regression test for handler failures

---

## Task 1: Route Permanently Invalid Inbound Messages To Error Exchange And Ack

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/MessageRetryHandler.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs`
- Modify: `src/ServiceConnect.UnitTests/MessageRetryHandlerTests.cs`
- Modify: `src/ServiceConnect.UnitTests/RabbitMqConsumerHostTests.cs`

- [ ] **Step 1: Add the failing terminal-failure tests for `MessageRetryHandler`**

In `src/ServiceConnect.UnitTests/MessageRetryHandlerTests.cs`, add these two tests below the existing max-retries coverage:

```csharp
    [Fact]
    public async Task HandleTerminalFailureAsync_PublishesToErrorExchange_WithoutIncrementingRetryCount()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var handler = new MessageRetryHandler(maxRetries: 3, errorExchange: "err", NullLogger.Instance);
        var args = MakeArgs();
        var headers = new Dictionary<string, object> { [HeaderKeys.RetryCount] = 2 };

        await handler.HandleTerminalFailureAsync(
            channel.Object,
            args,
            headers,
            new InvalidOperationException("invalid inbound message"));

        Assert.Equal(2, (int)headers[HeaderKeys.RetryCount]);
        channel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleTerminalFailureAsync_IncludesSanitizedExceptionPayload()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var handler = new MessageRetryHandler(maxRetries: 3, errorExchange: "err", NullLogger.Instance);
        var args = MakeArgs();
        var headers = new Dictionary<string, object>();

        await handler.HandleTerminalFailureAsync(
            channel.Object,
            args,
            headers,
            new InvalidOperationException("invalid inbound message"));

        var payload = JObject.Parse((string)headers[HeaderKeys.Exception]);
        Assert.Equal(typeof(InvalidOperationException).FullName, (string?)payload["ExceptionType"]);
        Assert.Contains("invalid inbound message", (string?)payload["Message"] ?? "");
        Assert.Null(payload["StackTrace"]);
    }
```

- [ ] **Step 2: Flip the host tests so invalid messages expect `error publish + ack`**

In `src/ServiceConnect.UnitTests/RabbitMqConsumerHostTests.cs`, update these existing assertions:

Replace the verification block in `EventAsync_OversizedMessage_IsNacked_AndHandlerNotInvoked`:

```csharp
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), false, true, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
```

with:

```csharp
        channel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
```

Make the same assertion replacement in:
- `EventAsync_ExcessiveHeaderCount_IsNacked_AndHandlerNotInvoked`
- `EventAsync_OversizedHeaderValue_IsNacked_AndHandlerNotInvoked`

Then add these two tests near the invalid-message section:

```csharp
    [Fact]
    public async Task EventAsync_MissingTypeHeaders_PublishesToErrorExchange_Acks_AndHandlerNotInvoked()
    {
        var (conn, channel) = MockConnection();
        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfg().Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(MakeQueueCfg().Object),
            NullLogger.Instance);

        bool handlerInvoked = false;
        await host.StartConsumingAsync(
            (_, _, _, _) =>
            {
                handlerInvoked = true;
                return Task.FromResult(new ConsumeEventResult { Success = true });
            },
            "q");

        await DeliverMessageAsync(host, new byte[] { 1, 2, 3 }, headers: null);

        Assert.False(handlerInvoked);
        channel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EventAsync_InvalidMessage_WhenErrorPublishFails_IsNackedForRedelivery()
    {
        var (conn, channel) = MockConnection();
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broker unavailable"));

        var qcfg = MakeQueueCfg();
        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfgWithMaxSize(10).Object,
            qcfg.Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(qcfg.Object),
            NullLogger.Instance);

        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");
        await DeliverMessageAsync(host, new byte[11], new Dictionary<string, object> { [HeaderKeys.TypeName] = "SomeType" });

        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), false, true, It.IsAny<CancellationToken>()), Times.Once);
    }
```

- [ ] **Step 3: Run the new/changed tests and confirm they fail first**

Run:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageRetryHandlerTests|FullyQualifiedName~RabbitMqConsumerHostTests"
```

Expected before the production changes:
- terminal-failure tests fail because `HandleTerminalFailureAsync` does not exist
- host tests fail because invalid-message handling still does `BasicNack(..., requeue: true)`

- [ ] **Step 4: Implement `HandleTerminalFailureAsync` and share the error-publish path**

In `src/ServiceConnect.Client.RabbitMQ/MessageRetryHandler.cs`, refactor the class so the max-retries path and the new terminal-invalid path share one private error-publish method.

Replace the existing class body with this version:

```csharp
internal sealed class MessageRetryHandler
{
    private readonly int _maxRetries;
    private readonly string _errorExchange;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public MessageRetryHandler(int maxRetries, string errorExchange, ILogger logger, TimeProvider? timeProvider = null)
    {
        _maxRetries = maxRetries;
        _errorExchange = errorExchange ?? throw new ArgumentNullException(nameof(errorExchange));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task HandleFailureAsync(
        IChannel channel,
        string retryQueueName,
        BasicDeliverEventArgs args,
        Dictionary<string, object> headers,
        Exception? ex)
    {
        int retryCount = 0;
        if (headers.TryGetValue(HeaderKeys.RetryCount, out var raw))
        {
            int candidate = raw is int i ? i
                : (raw is not null && int.TryParse(raw.ToString(), out var parsed) ? parsed : -1);
            if (candidate >= 0 && candidate <= _maxRetries + 1)
                retryCount = candidate;
        }

        if (retryCount < _maxRetries)
        {
            retryCount++;
            HeaderHelpers.SetHeader(headers, HeaderKeys.RetryCount, retryCount);
            var props = new BasicProperties(args.BasicProperties)
            {
                Headers = HeaderHelpers.ToNullableHeaders(headers)
            };
            await channel.BasicPublishAsync(string.Empty, retryQueueName, false, props, args.Body).ConfigureAwait(false);
            return;
        }

        await PublishErrorAsync(channel, args, headers, ex, logAsMaxRetries: true).ConfigureAwait(false);
    }

    public Task HandleTerminalFailureAsync(
        IChannel channel,
        BasicDeliverEventArgs args,
        Dictionary<string, object> headers,
        Exception ex)
    {
        return PublishErrorAsync(channel, args, headers, ex, logAsMaxRetries: false);
    }

    private async Task PublishErrorAsync(
        IChannel channel,
        BasicDeliverEventArgs args,
        Dictionary<string, object> headers,
        Exception? ex,
        bool logAsMaxRetries)
    {
        if (ex != null)
        {
            HeaderHelpers.SetHeader(headers, HeaderKeys.Exception, JsonConvert.SerializeObject(new
            {
                TimeStamp = _timeProvider.GetUtcNow().UtcDateTime,
                ExceptionType = ex.GetType().FullName,
                Message = HeaderHelpers.GetErrorMessage(ex)
            }));
        }

        if (logAsMaxRetries)
        {
            if (ex != null)
                _logger.LogError(ex, "Max retries exceeded for MessageId {MessageId}", args.BasicProperties.MessageId);
            else
                _logger.LogError("Max retries exceeded for MessageId {MessageId}", args.BasicProperties.MessageId);
        }
        else
        {
            _logger.LogError(ex, "Rejecting permanently invalid inbound message with MessageId {MessageId}", args.BasicProperties.MessageId);
        }

        var errorProps = new BasicProperties(args.BasicProperties)
        {
            Headers = HeaderHelpers.ToNullableHeaders(headers)
        };
        await channel.BasicPublishAsync(_errorExchange, string.Empty, false, errorProps, args.Body).ConfigureAwait(false);
    }
}
```

- [ ] **Step 5: Route invalid inbound messages through the terminal-failure path**

In `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs`:

1. Add this private helper just above `ProcessMessageAsync`:

```csharp
    private static Dictionary<string, object> CopyInboundHeaders(BasicDeliverEventArgs args)
    {
        var sourceHeaders = args.BasicProperties.Headers;
        var headers = new Dictionary<string, object>((sourceHeaders?.Count ?? 0) + 1);
        if (sourceHeaders != null)
        {
            foreach (var kvp in sourceHeaders)
            {
                if (kvp.Value is not null)
                    headers[kvp.Key] = kvp.Value;
            }
        }

        return headers;
    }
```

2. In `EventAsync`, replace the missing-type block:

```csharp
            if (args.BasicProperties.Headers == null ||
                (!args.BasicProperties.Headers.ContainsKey(HeaderKeys.TypeName) &&
                 !args.BasicProperties.Headers.ContainsKey(HeaderKeys.FullTypeName)))
            {
                _logger.LogError("Error processing message, Message headers must contain type name.");
                processed = true; // no retry possible for malformed messages, ack to discard
                return;
            }
```

with:

```csharp
            if (args.BasicProperties.Headers == null ||
                (!args.BasicProperties.Headers.ContainsKey(HeaderKeys.TypeName) &&
                 !args.BasicProperties.Headers.ContainsKey(HeaderKeys.FullTypeName)))
            {
                await _retryHandler.HandleTerminalFailureAsync(
                    model!,
                    args,
                    CopyInboundHeaders(args),
                    new InvalidOperationException("Message headers must contain type name.")).ConfigureAwait(false);
                processed = true;
                return;
            }
```

3. Replace the oversize/body/header early-return sections so each one does the same pattern, for example the body-size branch becomes:

```csharp
            if (args.Body.Length > _maxInboundMessageSize)
            {
                await _retryHandler.HandleTerminalFailureAsync(
                    model!,
                    args,
                    CopyInboundHeaders(args),
                    new InvalidOperationException(
                        $"Inbound message size {args.Body.Length} bytes exceeds configured limit {_maxInboundMessageSize} bytes."))
                    .ConfigureAwait(false);
                processed = true;
                return;
            }
```

Apply the same `HandleTerminalFailureAsync(...); processed = true; return;` pattern to:
- excessive header count
- oversized header value

Leave the existing `catch`/`finally` logic intact so a failure to publish to the error exchange still leaves `processed == false` and therefore `BasicNack(..., requeue: true)` happens in the `finally` block.

- [ ] **Step 6: Re-run the focused transport-invalid tests and confirm they pass**

Run:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageRetryHandlerTests|FullyQualifiedName~RabbitMqConsumerHostTests"
```

Expected:
- all `MessageRetryHandlerTests` pass
- all `RabbitMqConsumerHostTests` pass

## Task 2: Make Consumer Shutdown Deterministic

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs`
- Modify: `src/ServiceConnect.UnitTests/RabbitMqConsumerHostTests.cs`

- [ ] **Step 1: Add the failing shutdown tests**

In `src/ServiceConnect.UnitTests/RabbitMqConsumerHostTests.cs`, first extend `MockConnection()` with `BasicCancelAsync` support:

```csharp
        channel.Setup(c => c.BasicCancelAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
```

Then add these tests near the dispose coverage:

```csharp
    [Fact]
    public async Task DisposeAsync_CancelsConsumerBeforeClosingChannel()
    {
        var (conn, channel) = MockConnection();
        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfg().Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(MakeQueueCfg().Object),
            NullLogger.Instance);

        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");
        await host.DisposeAsync();

        channel.Verify(c => c.BasicCancelAsync("tag", false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_CompletesGracefully_WhenHandlerFinishesWithinGraceWindow()
    {
        var (conn, channel) = MockConnection();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var transport = MakeTransportCfg();
        transport.SetupGet(t => t.GracefulShutdownTimeoutMilliseconds).Returns(500);

        var host = new RabbitMqConsumerHost(
            conn.Object,
            transport.Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(MakeQueueCfg().Object),
            NullLogger.Instance);

        await host.StartConsumingAsync(async (_, _, _, _) =>
        {
            started.SetResult();
            await release.Task;
            return new ConsumeEventResult { Success = true };
        }, "q");

        _ = DeliverMessageAsync(host, new byte[] { 1 }, new Dictionary<string, object> { [HeaderKeys.TypeName] = "SomeType" });
        await started.Task;

        var disposeTask = host.DisposeAsync().AsTask();
        await Task.Delay(50);
        release.SetResult();
        await disposeTask;

        channel.Verify(c => c.BasicCancelAsync("tag", false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_WhenGraceWindowExpires_ClosesChannelDeterministically()
    {
        var (conn, channel) = MockConnection();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var transport = MakeTransportCfg();
        transport.SetupGet(t => t.GracefulShutdownTimeoutMilliseconds).Returns(50);

        var host = new RabbitMqConsumerHost(
            conn.Object,
            transport.Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(MakeQueueCfg().Object),
            NullLogger.Instance);

        await host.StartConsumingAsync(async (_, _, _, _) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return new ConsumeEventResult { Success = true };
        }, "q");

        _ = DeliverMessageAsync(host, new byte[] { 1 }, new Dictionary<string, object> { [HeaderKeys.TypeName] = "SomeType" });
        await started.Task;

        await host.DisposeAsync();

        channel.Verify(c => c.BasicCancelAsync("tag", false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }
```

- [ ] **Step 2: Run the shutdown tests to confirm they fail first**

Run:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMqConsumerHostTests.DisposeAsync"
```

Expected before production changes:
- the cancel verification fails because `RabbitMqConsumerHost` does not store or cancel the consumer tag yet

- [ ] **Step 3: Capture the consumer tag and cancel before draining**

In `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs`:

1. Add a field near `_consumer`:

```csharp
    private string? _consumerTag;
```

2. In `StartConsumingAsync`, replace:

```csharp
        var consumerTag = await _model.BasicConsumeAsync(_queueName, false, "", false, false, null, _consumer).ConfigureAwait(false);
        _logger.LogDebug("Started consuming on {QueueName}, tag={ConsumerTag}", _queueName, consumerTag);
```

with:

```csharp
        _consumerTag = await _model.BasicConsumeAsync(_queueName, false, "", false, false, null, _consumer).ConfigureAwait(false);
        _logger.LogDebug("Started consuming on {QueueName}, tag={ConsumerTag}", _queueName, _consumerTag);
```

3. Replace the body of `DisposeAsync()` with:

```csharp
    public async ValueTask DisposeAsync()
    {
        if (_model != null && !string.IsNullOrEmpty(_consumerTag))
        {
            try
            {
                await _model.BasicCancelAsync(_consumerTag, false).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cancelling consumer during dispose");
            }
        }

        var deadline = Environment.TickCount64 + _gracefulShutdownTimeoutMs;
        while (Volatile.Read(ref _messagesBeingProcessed) > 0 && Environment.TickCount64 < deadline)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        if (_autoDelete && _model != null)
        {
            try
            {
                _logger.LogDebug("Deleting retry queue");
                await _model.QueueDeleteAsync(_retryQueueName, false, false, false).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error deleting retry queue");
            }
        }

        await CloseChannelAsync().ConfigureAwait(false);
    }
```

This is the minimal fix: stop new deliveries first, then drain with the existing grace-window loop, then close.

- [ ] **Step 4: Re-run the host tests and confirm they pass**

Run:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMqConsumerHostTests"
```

Expected: all `RabbitMqConsumerHostTests` pass.

## Task 3: Restore Per-Message Publish/Send Retry And Reconnect In `Producer`

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer.cs`
- Create: `src/ServiceConnect.UnitTests/ProducerRetryTests.cs`

- [ ] **Step 1: Create concrete failing retry tests for publish, declare, and send-by-endpoint**

Create `src/ServiceConnect.UnitTests/ProducerRetryTests.cs` with this content:

```csharp
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

public class ProducerRetryTests
{
    private static Producer CreateProducer()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.RetryCount] = (ushort)2,
            [RabbitMQSettingKeys.RetrySeconds] = (ushort)0,
        });

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        return new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance);
    }

    private static void SetField<T>(Producer producer, string fieldName, T value)
    {
        typeof(Producer)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(producer, value);
    }

    private static T GetField<T>(Producer producer, string fieldName)
    {
        return (T)typeof(Producer)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(producer)!;
    }

    [Fact]
    public async Task PublishAsync_WhenFirstPublishFails_ReconnectsAndRetriesSuccessfully()
    {
        var producer = CreateProducer();
        var firstChannel = new Mock<IChannel>();
        var secondChannel = new Mock<IChannel>();
        var declaredExchanges = GetField<ConcurrentDictionary<string, bool>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = true;

        firstChannel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("first publish failed"));

        secondChannel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        SetField(producer, "_model", firstChannel.Object);
        SetField(producer, "_connected", true);
        producer.ReconnectForTests = () =>
        {
            SetField(producer, "_model", secondChannel.Object);
            SetField(producer, "_connected", true);
            return Task.CompletedTask;
        };

        await producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 });

        firstChannel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        secondChannel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_WhenExchangeDeclareFails_ReconnectsAndRetriesSuccessfully()
    {
        var producer = CreateProducer();
        var firstChannel = new Mock<IChannel>();
        var secondChannel = new Mock<IChannel>();

        firstChannel.Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object>?>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("declare failed"));

        secondChannel.Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object>?>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        secondChannel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        SetField(producer, "_model", firstChannel.Object);
        SetField(producer, "_connected", true);
        producer.ReconnectForTests = () =>
        {
            SetField(producer, "_model", secondChannel.Object);
            SetField(producer, "_connected", true);
            return Task.CompletedTask;
        };

        await producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 });

        secondChannel.Verify(c => c.ExchangeDeclareAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<IDictionary<string, object>?>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Once);
        secondChannel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendAsync_ByEndpoint_WhenFirstPublishFails_ReconnectsAndRetriesSuccessfully()
    {
        var producer = CreateProducer();
        var firstChannel = new Mock<IChannel>();
        var secondChannel = new Mock<IChannel>();

        firstChannel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("first publish failed"));

        secondChannel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        SetField(producer, "_model", firstChannel.Object);
        SetField(producer, "_connected", true);
        producer.ReconnectForTests = () =>
        {
            SetField(producer, "_model", secondChannel.Object);
            SetField(producer, "_connected", true);
            return Task.CompletedTask;
        };

        await producer.SendAsync("endpoint", typeof(object), new byte[] { 1, 2, 3 });

        secondChannel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

- [ ] **Step 2: Run the new producer retry tests and confirm they fail first**

Run:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ProducerRetryTests"
```

Expected before the production change:
- all three tests fail because `Producer` does not retry or reconnect per message yet

- [ ] **Step 3: Add a minimal internal reconnect seam and retry helpers to `Producer`**

In `src/ServiceConnect.Client.RabbitMQ/Producer.cs`:

1. Add this field near the other instance fields:

```csharp
    internal Func<Task>? ReconnectForTests;
```

2. Add these helpers below `CreateConnectionAsync()`:

```csharp
    private Task ExecuteWithConnectionRetryAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        return Retry.DoAsync(
            action,
            ex => ReconnectAfterPublishFailureAsync(ex),
            TimeSpan.FromSeconds(_retryTimeInSeconds),
            _retryCount,
            cancellationToken);
    }

    private async Task ReconnectAfterPublishFailureAsync(Exception ex)
    {
        _logger.LogError(ex, "Error publishing message");

        if (ReconnectForTests != null)
        {
            await ReconnectForTests().ConfigureAwait(false);
            return;
        }

        await DisposeConnectionAsync().ConfigureAwait(false);
        _declaredExchanges.Clear();
        await EnsureConnectedAsync().ConfigureAwait(false);
    }
```

3. Replace `ConfigureExchangeAsync` with the fail-fast version below so exchange-declare failures flow into the retry wrapper:

```csharp
    private async Task ConfigureExchangeAsync(string exchangeName, string type)
    {
        await _model!.ExchangeDeclareAsync(exchangeName, type, true, false, null).ConfigureAwait(false);
        _declaredExchanges[exchangeName] = true;
    }
```

- [ ] **Step 4: Wrap the publish/send operations in the retry helper**

In `src/ServiceConnect.Client.RabbitMQ/Producer.cs`, make these exact method-body changes.

In `PublishAsync`, replace the body of the `try` block:

```csharp
            var messageHeaders = GetHeaders(type, headers, _queueConfiguration.QueueName, "Publish");
            var basicProperties = CreateBasicProperties(messageHeaders);

            string exchangeName = _exchangeNameCache.GetOrAdd(type.FullName!, static fn => fn.Replace(".", string.Empty));
            if (!_declaredExchanges.ContainsKey(exchangeName))
                await ConfigureExchangeAsync(exchangeName, ExchangeType.Fanout).ConfigureAwait(false);
            await PublishWithRetryAsync(exchangeName, "", basicProperties, message, cancellationToken).ConfigureAwait(false);
```

with:

```csharp
            var messageHeaders = GetHeaders(type, headers, _queueConfiguration.QueueName, "Publish");
            var basicProperties = CreateBasicProperties(messageHeaders);
            string exchangeName = _exchangeNameCache.GetOrAdd(type.FullName!, static fn => fn.Replace(".", string.Empty));

            await ExecuteWithConnectionRetryAsync(async () =>
            {
                if (!_declaredExchanges.ContainsKey(exchangeName))
                    await ConfigureExchangeAsync(exchangeName, ExchangeType.Fanout).ConfigureAwait(false);

                await _model!.BasicPublishAsync(
                    exchangeName,
                    string.Empty,
                    false,
                    basicProperties,
                    (ReadOnlyMemory<byte>)message,
                    cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
```

In `SendAsync(Type, ...)`, replace the body of the `foreach (string endPoint in endPoints)` loop:

```csharp
                baseHeaders[HeaderKeys.DestinationAddress] = endPoint;
                var basicProperties = CreateBasicProperties(baseHeaders);
                await PublishWithRetryAsync(string.Empty, endPoint, basicProperties, message, cancellationToken).ConfigureAwait(false);
```

with:

```csharp
                baseHeaders[HeaderKeys.DestinationAddress] = endPoint;
                var basicProperties = CreateBasicProperties(baseHeaders);
                await ExecuteWithConnectionRetryAsync(
                    () => _model!.BasicPublishAsync(
                        string.Empty,
                        endPoint,
                        false,
                        basicProperties,
                        (ReadOnlyMemory<byte>)message,
                        cancellationToken).AsTask(),
                    cancellationToken).ConfigureAwait(false);
```

In `SendAsync(string endPoint, ...)`, replace:

```csharp
            var messageHeaders = GetHeaders(type, headers, endPoint, "Send");
            var basicProperties = CreateBasicProperties(messageHeaders);
            await PublishWithRetryAsync(string.Empty, endPoint, basicProperties, message, cancellationToken).ConfigureAwait(false);
```

with:

```csharp
            var messageHeaders = GetHeaders(type, headers, endPoint, "Send");
            var basicProperties = CreateBasicProperties(messageHeaders);
            await ExecuteWithConnectionRetryAsync(
                () => _model!.BasicPublishAsync(
                    string.Empty,
                    endPoint,
                    false,
                    basicProperties,
                    (ReadOnlyMemory<byte>)message,
                    cancellationToken).AsTask(),
                cancellationToken).ConfigureAwait(false);
```

In `SendBytesAsync`, replace:

```csharp
            var messageHeaders = GetHeaders(typeof(byte[]), headers, endPoint, HeaderKeys.ByteStream);
            var basicProperties = CreateBasicProperties(messageHeaders);
            await PublishWithRetryAsync(string.Empty, endPoint, basicProperties, packet, cancellationToken).ConfigureAwait(false);
```

with:

```csharp
            var messageHeaders = GetHeaders(typeof(byte[]), headers, endPoint, HeaderKeys.ByteStream);
            var basicProperties = CreateBasicProperties(messageHeaders);
            await ExecuteWithConnectionRetryAsync(
                () => _model!.BasicPublishAsync(
                    string.Empty,
                    endPoint,
                    false,
                    basicProperties,
                    (ReadOnlyMemory<byte>)packet,
                    cancellationToken).AsTask(),
                cancellationToken).ConfigureAwait(false);
```

Then delete the old `PublishWithRetryAsync` helper entirely.

- [ ] **Step 5: Run the focused producer/transport unit suite and confirm it passes**

Run:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ProducerRetryTests|FullyQualifiedName~ProducerLifecycleTests|FullyQualifiedName~RabbitMqConsumerHostTests|FullyQualifiedName~MessageRetryHandlerTests"
```

Expected: all listed tests pass.

- [ ] **Step 6: Run the transport-related E2E tests**

Run:

```bash
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "FullyQualifiedName~MalformedMessageTests|FullyQualifiedName~RetryAndErrorQueueTests"
```

Expected: all transport E2E regressions pass.

- [ ] **Step 7: Run GitNexus impact verification before commit**

Run:

```bash
rtk gitnexus impact MessageRetryHandler --include-tests
rtk gitnexus impact RabbitMqConsumerHost --include-tests
rtk gitnexus impact Producer --include-tests
```

Expected:
- no unexpected direct callers beyond the RabbitMQ transport path and its tests

Then inspect changed scope:

```bash
rtk git diff --stat
```

Expected touched files are limited to:
- `src/ServiceConnect.Client.RabbitMQ/MessageRetryHandler.cs`
- `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs`
- `src/ServiceConnect.Client.RabbitMQ/Producer.cs`
- transport-focused unit/E2E tests
