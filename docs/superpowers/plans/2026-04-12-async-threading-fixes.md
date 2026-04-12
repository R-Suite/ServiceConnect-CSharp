# Async/Threading Critical Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix six critical async/threading issues (C-01 through C-06) across the RabbitMQ transport layer and core Bus.

**Architecture:** Delivered as four commits. Commit 1 replaces the entire IDisposable disposal chain with IAsyncDisposable (C-01, C-02, C-04). Commits 2-4 are independent mechanical fixes (C-03, C-05, C-06). The disposal chain change is a breaking API change — IDisposable is removed from IBus, IProducer, and IConsumer.

**Tech Stack:** .NET 8, C#, xUnit, Moq

---

## Task 1: Update Interfaces

**Files:**
- Modify: `src/ServiceConnect.Interfaces/IProducer.cs:6`
- Modify: `src/ServiceConnect.Interfaces/IConsumer.cs:6`
- Modify: `src/ServiceConnect.Interfaces/IBus.cs:8,56`
- Modify: `src/ServiceConnect.Client.RabbitMQ/IServiceConnectConnection.cs:5,9`

- [ ] **Step 1: Remove IDisposable from IProducer**

In `src/ServiceConnect.Interfaces/IProducer.cs`, change line 6 from:
```csharp
public interface IProducer : IAsyncDisposable, IDisposable
```
to:
```csharp
public interface IProducer : IAsyncDisposable
```

- [ ] **Step 2: Remove IDisposable from IConsumer**

In `src/ServiceConnect.Interfaces/IConsumer.cs`, change line 6 from:
```csharp
public interface IConsumer : IAsyncDisposable, IDisposable
```
to:
```csharp
public interface IConsumer : IAsyncDisposable
```

- [ ] **Step 3: Update IBus to IAsyncDisposable and make StopConsuming async**

In `src/ServiceConnect.Interfaces/IBus.cs`, change line 8 from:
```csharp
public interface IBus : IDisposable
```
to:
```csharp
public interface IBus : IAsyncDisposable
```

Change the StopConsuming signature (lines 53-56) from:
```csharp
    /// <summary>
    /// Stops consuming messages and disposes the consumer.
    /// </summary>
    void StopConsuming();
```
to:
```csharp
    /// <summary>
    /// Stops consuming messages and disposes the consumer.
    /// </summary>
    Task StopConsumingAsync();
```

- [ ] **Step 4: Update IServiceConnectConnection to IAsyncDisposable**

In `src/ServiceConnect.Client.RabbitMQ/IServiceConnectConnection.cs`, replace the entire file:

```csharp
using RabbitMQ.Client;

namespace ServiceConnect.Client.RabbitMQ;

public interface IServiceConnectConnection : IAsyncDisposable
{
    Task ConnectAsync();
    Task<IChannel> CreateChannelAsync();
    bool IsConnected();
}
```

This removes the `void Dispose()` method and adds `IAsyncDisposable` inheritance (which provides `ValueTask DisposeAsync()`).

---

## Task 2: Update Connection.cs — Remove Sync Dispose

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Connection.cs:91-128`

- [ ] **Step 1: Remove the Dispose() method and add ConfigureAwait to DisposeAsync**

In `src/ServiceConnect.Client.RabbitMQ/Connection.cs`, delete the entire `Dispose()` method (lines 109-128):

```csharp
    public void Dispose()
    {
        if (_connection == null) return;

        var conn = _connection;
        _connection = null;
        _ = Task.Run(async () =>
        {
            try
            {
                if (conn.IsOpen)
                    await conn.CloseAsync();
                conn.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Error closing connection during dispose");
            }
        });
    }
```

Also fix the missing `ConfigureAwait(false)` on line 100 in `DisposeAsync()`. Change:
```csharp
                await conn.CloseAsync();
```
to:
```csharp
                await conn.CloseAsync().ConfigureAwait(false);
```

---

## Task 3: Update Producer.cs — Remove Sync Dispose, Fix DisconnectAsync

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer.cs:9,183-199`

- [ ] **Step 1: Fix DisconnectAsync to call DisposeAsync instead of Dispose**

Change `DisconnectAsync()` (lines 183-188) from:
```csharp
    public Task DisconnectAsync()
    {
        _logger.LogDebug("In Producer.DisconnectAsync()");
        Dispose();
        return Task.CompletedTask;
    }
```
to:
```csharp
    public async Task DisconnectAsync()
    {
        _logger.LogDebug("In Producer.DisconnectAsync()");
        await DisposeAsync().ConfigureAwait(false);
    }
```

- [ ] **Step 2: Remove the Dispose() method**

Delete the entire `Dispose()` method (lines 190-199):
```csharp
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = Task.Run(async () =>
        {
            try { await DisposeAsyncCore().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Error during fire-and-forget dispose"); }
        });
    }
```

The existing `DisposeAsync()` (lines 201-206) and `DisposeAsyncCore()` (lines 208-212) remain unchanged — they already properly await cleanup.

---

## Task 4: Update Client.cs — Remove Sync Dispose, CloseChannel, StopConsuming

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Client.cs:11,262-287,314-329`

- [ ] **Step 1: Remove IDisposable from class declaration**

Change line 11 from:
```csharp
public sealed class Client : IDisposable, IAsyncDisposable
```
to:
```csharp
public sealed class Client : IAsyncDisposable
```

- [ ] **Step 2: Remove StopConsuming() method**

Delete the `StopConsuming()` method (lines 262-265):
```csharp
    public void StopConsuming()
    {
        Dispose();
    }
```

- [ ] **Step 3: Remove Dispose() method**

Delete the entire `Dispose()` method (lines 267-287):
```csharp
    public void Dispose()
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Volatile.Read(ref _messagesBeingProcessed) > 0 && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(50);
        }

        CloseChannel();

        if (_autoDelete && _model != null)
        {
            var model = _model;
            var queueName = _queueName;
            _ = Task.Run(async () =>
            {
                try { await model.QueueDeleteAsync(queueName + ".Retries").ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error deleting retry queue during dispose"); }
            });
        }
    }
```

The existing `DisposeAsync()` (lines 289-312) and `CloseChannelAsync()` (lines 331-346) remain — they already properly await all operations.

- [ ] **Step 4: Remove CloseChannel() sync method**

Delete the sync `CloseChannel()` method (lines 314-329):
```csharp
    private void CloseChannel()
    {
        if (_model == null) return;
        try
        {
            if (_model.IsOpen)
                _model.CloseAsync().GetAwaiter().GetResult();
            _model.Dispose();
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing channel during dispose");
        }
        _model = null;
    }
```

---

## Task 5: Update Consumer.cs — Replace Dispose with Proper DisposeAsync

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer.cs:118-134`

- [ ] **Step 1: Replace Dispose() and stub DisposeAsync() with proper async implementation**

Delete both `Dispose()` (lines 118-128) and the stub `DisposeAsync()` (lines 130-134):
```csharp
    public void Dispose()
    {
        foreach (Client consumer in _clients)
        {
            try { consumer.Dispose(); }
            catch (ObjectDisposedException) { }
        }

        _model = null;
        _connection?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
```

Replace with:
```csharp
    public async ValueTask DisposeAsync()
    {
        foreach (Client consumer in _clients)
        {
            try { await consumer.DisposeAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }

        _model = null;
        if (_connection != null)
            await _connection.DisposeAsync().ConfigureAwait(false);
    }
```

---

## Task 6: Update Bus.cs — Replace Dispose/StopConsuming with Async Versions

**Files:**
- Modify: `src/ServiceConnect/Bus.cs:191-219`

- [ ] **Step 1: Replace StopConsuming() with StopConsumingAsync()**

Replace `StopConsuming()` (lines 191-206) with:
```csharp
    public async Task StopConsumingAsync()
    {
        bool shouldDispose = false;
        lock (_stateLock)
        {
            _logger.LogInformation("Bus stopping message consumption.");
            if (_consuming)
            {
                _consuming = false;
                shouldDispose = true;
            }
        }
        // Dispose outside the lock to avoid deadlock with consumer callback chain
        if (shouldDispose && _consumer != null)
            await _consumer.DisposeAsync().ConfigureAwait(false);
    }
```

- [ ] **Step 2: Replace Dispose() with DisposeAsync()**

Replace `Dispose()` (lines 208-219) with:
```csharp
    public async ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        await StopConsumingAsync().ConfigureAwait(false);
        _sendPipeline.Dispose();
        if (_producer != null)
            await _producer.DisposeAsync().ConfigureAwait(false);
    }
```

Note: `_sendPipeline.Dispose()` stays synchronous because `ISendMessagePipeline` only implements `IDisposable`.

---

## Task 7: Update BusHostedService

**Files:**
- Modify: `src/ServiceConnect/Services/BusHostedService.cs:29-33`

- [ ] **Step 1: Await StopConsumingAsync in StopAsync**

Change `StopAsync` (lines 29-33) from:
```csharp
    public Task StopAsync(CancellationToken cancellationToken)
    {
        bus.StopConsuming();
        return Task.CompletedTask;
    }
```
to:
```csharp
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await bus.StopConsumingAsync().ConfigureAwait(false);
    }
```

---

## Task 8: Update Unit Tests

**Files:**
- Modify: `src/ServiceConnect.UnitTests/BusTests.cs:94-107`
- Modify: `src/ServiceConnect.UnitTests/Services/BusHostedServiceTests.cs:55-62`

- [ ] **Step 1: Update BusTests — StopConsuming and Dispose tests**

Change the `StopConsuming_ShouldSetIsConnectedToFalse` test (lines 94-100) from:
```csharp
        [Fact]
        public void StopConsuming_ShouldSetIsConnectedToFalse()
        {
            // StopConsuming can be called even without starting (no consumer needed)
            _bus.StopConsuming();
            Assert.False(_bus.IsConnected);
        }
```
to:
```csharp
        [Fact]
        public async Task StopConsumingAsync_ShouldSetIsConnectedToFalse()
        {
            // StopConsuming can be called even without starting (no consumer needed)
            await _bus.StopConsumingAsync();
            Assert.False(_bus.IsConnected);
        }
```

Change the `Dispose_ShouldSetIsConnectedToFalse` test (lines 102-107) from:
```csharp
        [Fact]
        public void Dispose_ShouldSetIsConnectedToFalse()
        {
            _bus.Dispose();
            Assert.False(_bus.IsConnected);
        }
```
to:
```csharp
        [Fact]
        public async Task DisposeAsync_ShouldSetIsConnectedToFalse()
        {
            await _bus.DisposeAsync();
            Assert.False(_bus.IsConnected);
        }
```

- [ ] **Step 2: Update BusHostedServiceTests — StopAsync verification**

Change the `StopAsync_CallsStopConsuming` test (lines 55-62) from:
```csharp
    [Fact]
    public async Task StopAsync_CallsStopConsuming()
    {
        var sut = CreateSut();
        await sut.StopAsync(CancellationToken.None);

        _mockBus.Verify(b => b.StopConsuming(), Times.Once);
    }
```
to:
```csharp
    [Fact]
    public async Task StopAsync_CallsStopConsumingAsync()
    {
        _mockBus.Setup(b => b.StopConsumingAsync()).Returns(Task.CompletedTask);
        var sut = CreateSut();
        await sut.StopAsync(CancellationToken.None);

        _mockBus.Verify(b => b.StopConsumingAsync(), Times.Once);
    }
```

---

## Task 9: Update BusLifecycleTests

**Files:**
- Modify: `src/ServiceConnect.EndToEndTests/BusLifecycleTests.cs`

- [ ] **Step 1: Replace entire BusLifecycleTests file**

Replace the file contents with:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

public class BusLifecycleTests
{
    private IBus CreateBus(bool withConsumer = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProducer>(new Mock<IProducer>().Object);

        if (withConsumer)
        {
            var mockConsumer = new Mock<IConsumer>();
            mockConsumer.Setup(x => x.StartConsumingAsync(It.IsAny<string>(), It.IsAny<IList<string>>(), It.IsAny<ConsumerEventHandler>()))
                .Returns(Task.CompletedTask);
            services.AddSingleton<IConsumer>(mockConsumer.Object);
        }

        services.AddServiceConnect(_ => { });

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IBus>();
    }

    [Fact]
    public async Task Bus_StartsAndStopsConsuming_WithConsumer()
    {
        var bus = CreateBus(withConsumer: true);

        Assert.False(bus.IsConnected);

        await bus.StartConsumingAsync();
        Assert.True(bus.IsConnected);

        await bus.StopConsumingAsync();
        Assert.False(bus.IsConnected);
    }

    [Fact]
    public async Task Bus_StartConsuming_ThrowsWithoutConsumer()
    {
        var bus = CreateBus(withConsumer: false);

        Assert.False(bus.IsConnected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bus.StartConsumingAsync());
    }

    [Fact]
    public async Task Bus_DisposesCleanly()
    {
        var bus = CreateBus(withConsumer: true);

        await bus.StartConsumingAsync();
        Assert.True(bus.IsConnected);

        await bus.DisposeAsync();
        Assert.False(bus.IsConnected);
    }

    [Fact]
    public async Task Bus_DoubleDispose_DoesNotThrow()
    {
        var bus = CreateBus(withConsumer: true);

        await bus.StartConsumingAsync();

        var exception = await Record.ExceptionAsync(async () =>
        {
            await bus.DisposeAsync();
            await bus.DisposeAsync();
        });

        Assert.Null(exception);
    }
}
```

---

## Task 10: Update E2E Tests — Replace bus.Dispose() with await bus.DisposeAsync()

**Files (all in `src/ServiceConnect.EndToEndTests/`):**

Every E2E test that calls `bus.Dispose()` in a `finally` block must change to `await bus.DisposeAsync()`. The provider disposal `(provider as IDisposable)?.Dispose()` stays unchanged.

The pattern in every file is the same. Change:
```csharp
finally
{
    bus.Dispose();
    (provider as IDisposable)?.Dispose();
}
```
to:
```csharp
finally
{
    await bus.DisposeAsync();
    (provider as IDisposable)?.Dispose();
}
```

For tests with multiple buses (e.g. `bus1`, `bus2`, `producerBus`, `consumerBus`, `responderBus`, `requesterBus`, etc.), apply the same change to each: `xxxBus.Dispose()` → `await xxxBus.DisposeAsync()`.

- [ ] **Step 1: Update single-bus tests**

Apply the pattern to these files:
- `PublishSubscribeTests.cs` (line 103)
- `ContentRoutingTests.cs` (line 99)
- `EmptyMessageTests.cs` (line 85)
- `FilterChainTests.cs` (lines 133, 202)
- `CustomErrorQueueTests.cs` (line 111)
- `MaxRetriesZeroTests.cs` (line 108)
- `CustomHeaderTests.cs` (line 90)
- `MiddlewarePipelineE2ETests.cs` (line 122)
- `MessageDeduplicationTests.cs` (line 150)
- `PrefetchCountTests.cs` (line 91)
- `ConsumerCountE2ETests.cs` (line 90)
- `MultipleHandlerTests.cs` (line 94)
- `PublisherConfirmsTests.cs` (line 84)
- `PolymorphicMessageTests.cs` (line 104)
- `DisableErrorsTests.cs` (line 117)
- `QueuePurgeTests.cs` (line 123)
- `ProcessManagerMongoDbTests.cs` (line 104)
- `AggregatorMongoDbTests.cs` (line 91)
- `ProcessManagerExceptionTests.cs` (line 117)
- `AuditingTests.cs` (lines 113, 209)
- `FilterPipelineConsumerTests.cs` (lines 103, 169)
- `AggregatorTests.cs` (lines 87, 157)
- `AggregatorExceptionTests.cs` (line 111)
- `MalformedMessageTests.cs` (line 122)
- `ExceptionHandlerE2ETests.cs` (line 103)
- `RetryAndErrorQueueTests.cs` (lines 123, 221)
- `ProcessManagerTests.cs` (line 100)
- `PointToPointTests.cs` (lines 61, 81)

- [ ] **Step 2: Update multi-bus tests**

Apply the same pattern — every `xxxBus.Dispose()` becomes `await xxxBus.DisposeAsync()`:

- `PriorityQueueTests.cs` (lines 150-151: `consumerBus`, `producerBus`)
- `CompetingConsumersTests.cs` (lines 120-122: `bus1`, `bus2`, `producerBus`)
- `StreamingTests.cs` (lines 119-120: `consumerBus`, `producerBus`)
- `StreamOutOfOrderTests.cs` (lines 123-124: `consumerBus`, `producerBus`)
- `RoutingSlipForwardingTests.cs` (lines 128-129: `step1Bus`, `step2Bus`)
- `QueueMappingTests.cs` (line 85-86: `consumerBus`, `senderBus`)
- `MultiEndpointSendTests.cs` (lines 160-164: `consumer1Bus`, `consumer2Bus`, `senderBus`)
- `CustomHeaderTests.cs` (lines 185-187: `responderBus`, `requesterBus`)
- `ConsumeContextReplyTests.cs` (lines 121-123: `responderBus`, `requesterBus`)
- `RequestReplyE2ETests.cs` (lines 107-109: `responderBus`, `requesterBus`)
- `PublishRequestAsyncTests.cs` (lines 120-122: `responderBus`, `requesterBus`)
- `ScatterGatherTests.cs` (lines 152-156: `responder1Bus`, `responder2Bus`, `requesterBus`)
- `ScatterGatherPartialTests.cs` (lines 149-153: `responderBus`, `silentBus`, `requesterBus`)

- [ ] **Step 3: Handle AutoStartConsumingE2ETests**

`AutoStartConsumingE2ETests.cs` (line 94) uses `host.Dispose()` — this is an `IHost`, not `IBus`. Leave it unchanged.

---

## Task 11: Build and Verify Commit 1

- [ ] **Step 1: Build the solution**

Run: `dotnet build`
Expected: 0 errors. All Dispose → DisposeAsync and StopConsuming → StopConsumingAsync references resolved.

- [ ] **Step 2: Run unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ --no-build`
Expected: All tests pass.

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.Interfaces/IProducer.cs \
  src/ServiceConnect.Interfaces/IConsumer.cs \
  src/ServiceConnect.Interfaces/IBus.cs \
  src/ServiceConnect.Client.RabbitMQ/IServiceConnectConnection.cs \
  src/ServiceConnect.Client.RabbitMQ/Connection.cs \
  src/ServiceConnect.Client.RabbitMQ/Producer.cs \
  src/ServiceConnect.Client.RabbitMQ/Client.cs \
  src/ServiceConnect.Client.RabbitMQ/Consumer.cs \
  src/ServiceConnect/Bus.cs \
  src/ServiceConnect/Services/BusHostedService.cs \
  src/ServiceConnect.UnitTests/BusTests.cs \
  src/ServiceConnect.UnitTests/Services/BusHostedServiceTests.cs \
  src/ServiceConnect.EndToEndTests/
git commit -m "fix: replace IDisposable with IAsyncDisposable across disposal chain (C-01, C-02, C-04)"
```

---

## Task 12: Add Null Handler Guard (C-03)

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Client.cs:122`

- [ ] **Step 1: Add null check before handler invocation**

In `Client.cs`, in the `ProcessMessage` method, change line 122 from:
```csharp
            result = await _consumerEventHandler!(args.Body.ToArray(), typeName, headers);
```
to:
```csharp
            if (_consumerEventHandler == null)
            {
                _logger.LogError("Consumer event handler not set — message will be nacked for redelivery. Queue: {Queue}", _queueConfiguration.QueueName);
                result = new ConsumeEventResult { Success = false };
            }
            else
            {
                result = await _consumerEventHandler(args.Body.ToArray(), typeName, headers);
            }
```

When `Success = false` and `Exception` is null, the existing retry logic (lines 135-175) will increment the retry counter and eventually route the message to the error queue — which is the correct behavior for an unhandled message.

- [ ] **Step 2: Build**

Run: `dotnet build src/ServiceConnect.Client.RabbitMQ/`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Client.cs
git commit -m "fix: add null guard for consumer event handler to prevent NRE (C-03)"
```

---

## Task 13: Add ConfigureAwait(false) to All Await Calls in Client.cs (C-05)

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Client.cs`

Note: Line numbers below are approximate after Tasks 4 and 12 have changed the file. Match on code content, not line numbers.

- [ ] **Step 1: Add ConfigureAwait(false) to all missing await calls**

Find and update each of these `await` calls that are missing `.ConfigureAwait(false)`:

1. In `Event()` method:
   - `await ProcessMessage(args);` → `await ProcessMessage(args).ConfigureAwait(false);`
   - `await _model!.BasicAckAsync(args.DeliveryTag, false);` → `await _model!.BasicAckAsync(args.DeliveryTag, false).ConfigureAwait(false);`
   - `await _model!.BasicNackAsync(args.DeliveryTag, false, true);` → `await _model!.BasicNackAsync(args.DeliveryTag, false, true).ConfigureAwait(false);`

2. In `ProcessMessage()` method:
   - `result = await _consumerEventHandler(args.Body.ToArray(), typeName, headers);` → `result = await _consumerEventHandler(args.Body.ToArray(), typeName, headers).ConfigureAwait(false);`
   - `await _model!.BasicPublishAsync(string.Empty, _retryQueueName, mandatory: false, retryProps, args.Body);` → add `.ConfigureAwait(false)`
   - `await _model!.BasicPublishAsync(_errorExchange, string.Empty, mandatory: false, errorProps, args.Body);` → add `.ConfigureAwait(false)`
   - `await _model!.BasicPublishAsync(_auditExchange, string.Empty, mandatory: false, auditProps, args.Body);` → add `.ConfigureAwait(false)`

3. In `CreateConsumerAsync()` method:
   - `_model = await _connection.CreateChannelAsync();` → `_model = await _connection.CreateChannelAsync().ConfigureAwait(false);`
   - `await _model.BasicQosAsync(0, _prefetchCount, false);` → add `.ConfigureAwait(false)`
   - `var consumerTag = await _model.BasicConsumeAsync(_queueName, false, _consumer);` → add `.ConfigureAwait(false)`

4. In `ConsumeMessageTypeAsync()` method:
   - `await _model!.QueueBindAsync(_queueName, messageTypeName, string.Empty, _queueArguments);` → add `.ConfigureAwait(false)`

- [ ] **Step 2: Build**

Run: `dotnet build src/ServiceConnect.Client.RabbitMQ/`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Client.cs
git commit -m "chore: add ConfigureAwait(false) to all async calls in Client (C-05)"
```

---

## Task 14: Use Random.Shared for Thread-Safe Jitter (C-06)

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Retry.cs:5,71`

- [ ] **Step 1: Remove the static Random field**

Delete line 5:
```csharp
    private static readonly Random Jitter = new();
```

- [ ] **Step 2: Replace Jitter.Next with Random.Shared.Next**

Change line 71 from:
```csharp
        var jitterMs = Jitter.Next(0, (int)Math.Min(baseInterval.TotalMilliseconds, 1000));
```
to:
```csharp
        var jitterMs = Random.Shared.Next(0, (int)Math.Min(baseInterval.TotalMilliseconds, 1000));
```

- [ ] **Step 3: Build**

Run: `dotnet build src/ServiceConnect.Client.RabbitMQ/`
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Retry.cs
git commit -m "fix: use Random.Shared for thread-safe jitter in Retry (C-06)"
```

---

## Verification Phase

After all commits:

- [ ] Run `dotnet build` — must compile with zero errors
- [ ] Run `dotnet test src/ServiceConnect.UnitTests/` — all unit tests must pass
