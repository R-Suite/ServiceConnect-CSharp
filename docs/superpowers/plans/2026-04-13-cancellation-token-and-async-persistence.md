# CancellationToken End-to-End + Async Persistence + R-034 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Thread `CancellationToken` through the ServiceConnect messaging framework end-to-end (public IBus APIs, middleware, processors, transport, persistence), fix the R-034 race condition in `Bus.StartConsumingAsync`/`StopConsumingAsync`, expose CT to user handlers via `IConsumeContext`, and migrate core persistence interfaces to async.

**Architecture:** Add `CancellationToken cancellationToken = default` as the last parameter on every framework async method (BCL convention). Bus owns `SemaphoreSlim(1,1)` for lifecycle serialization. `RequestReplyManager` uses `CreateLinkedTokenSource` to combine caller CT with internal timeout. Persistence interfaces (`IProcessManagerFinder`, `IAggregatorPersistor`, `ITimeoutStore`) convert to fully async. `OperationCanceledException` propagates untouched. User message handlers are **unchanged** -- they read CT from `Context.CancellationToken`.

**Tech Stack:** .NET 10, xUnit, Moq, `CancellationTokenSource.CreateLinkedTokenSource`, `SemaphoreSlim.WaitAsync(CancellationToken)`, MongoDB.Driver native async API.

**Related spec:** `docs/superpowers/specs/2026-04-13-cancellation-token-and-async-persistence-design.md`

**Branch:** `improvements-and-fixes`

**Critical note for all tasks:** Every task must leave the build green and all existing unit + E2E tests passing. Interface changes and their implementations must commit together.

**Build + test commands (reference):**
- Build: `dotnet build src/ServiceConnect.sln`
- Unit tests: `dotnet test src/ServiceConnect.UnitTests --filter "Category!=Docker" -v quiet`
- Filter unit tests: `dotnet test filters/ServiceConnect.Filters.MessageDeduplication/ServiceConnect.Filters.MessageDeduplication.UnitTests --filter "Category!=Docker" -v quiet`
- E2E (requires Docker): `sg docker -c "dotnet test src/ServiceConnect.EndToEndTests -v quiet"`

---

## Task 1: Add CancellationToken to IBus + Bus (public surface plumbing)

**Files:**
- Modify: `src/ServiceConnect.Interfaces/IBus.cs`
- Modify: `src/ServiceConnect/Bus.cs`

**Scope:** Add `CancellationToken cancellationToken = default` as the last parameter on every async method in `IBus` and the matching `Bus` implementations. Pass CT down to already-CT-aware callees (`Task.Run`, `SemaphoreSlim.WaitAsync` -- none yet). Where internal interfaces don't yet accept CT (Consumer, Producer, RequestReplyManager, Dispatcher), drop CT at the boundary for now. Later tasks thread it further.

- [ ] **Step 1: Update `IBus.cs` signatures**

Replace each async method declaration with CT-accepting form. Example -- full file after edit:

```csharp
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Interfaces;

public interface IBus : IAsyncDisposable
{
    Task PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : Message;
    Task SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message;
    Task<TReply> SendRequestAsync<T, TReply>(T message, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where T : Message where TReply : Message;
    Task<IList<TReply>> SendRequestMultiAsync<T, TReply>(T message, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where T : Message where TReply : Message;
    Task PublishRequestAsync<TRequest, TReply>(TRequest message, Action<TReply> onReply, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where TRequest : Message where TReply : Message;
    Task RouteAsync<T>(T message, IList<string> destinations, CancellationToken cancellationToken = default) where T : Message;
    IMessageBusWriteStream CreateStream<T>(string endpoint, T message) where T : Message;
    Task StartConsumingAsync(CancellationToken cancellationToken = default);
    Task StopConsumingAsync(CancellationToken cancellationToken = default);
    bool IsConnected { get; }
}
```

Preserve existing XML doc comments.

- [ ] **Step 2: Update `Bus.cs` method signatures to match**

Each async method in `Bus.cs` gains `CancellationToken cancellationToken = default` at the end. For this task, only the signature changes -- the method body forwards to existing internals (CT is accepted but not yet used). Exception: pass the CT to the existing `cancellationToken.ThrowIfCancellationRequested()` call at the top of each method (new line at the top of each public async method body).

Example for `PublishAsync`:

```csharp
public async Task PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : Message
{
    ThrowIfDisposed();
    cancellationToken.ThrowIfCancellationRequested();
    // ... existing body unchanged
}
```

Apply the same pattern to `SendAsync`, `SendRequestAsync`, `SendRequestMultiAsync`, `PublishRequestAsync`, `RouteAsync`, `StartConsumingAsync`, `StopConsumingAsync`.

- [ ] **Step 3: Build**

Run: `dotnet build src/ServiceConnect.sln`
Expected: build succeeds (any callers of IBus methods still compile because CT is optional with default).

- [ ] **Step 4: Run unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "Category!=Docker" -v quiet`
Expected: all passing (same count as before).

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Interfaces/IBus.cs src/ServiceConnect/Bus.cs
git commit -m "$(cat <<'EOF'
feat: add CancellationToken parameter to IBus async methods (R-016)

Add optional CT parameter (default) as last parameter on all IBus async
methods and matching Bus implementations. Each method throws OCE up-front
via cancellationToken.ThrowIfCancellationRequested(). Internal callees
(Consumer, Producer, RequestReplyManager) still sync at this point; later
tasks thread CT further.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Fix R-034 race with SemaphoreSlim in Bus

**Files:**
- Modify: `src/ServiceConnect/Bus.cs`
- Modify: `src/ServiceConnect.UnitTests/BusTests.cs` (or create if not present)

**Scope:** Serialize `StartConsumingAsync` and `StopConsumingAsync` with a `SemaphoreSlim(1,1)` so concurrent callers cannot interleave the `_consuming` state. `WaitAsync(ct)` respects the caller's token.

- [ ] **Step 1: Write failing test for lifecycle serialization**

Add to `src/ServiceConnect.UnitTests/BusTests.cs`:

```csharp
[Fact]
public async Task StartConsumingAsync_ConcurrentWithStop_SerializesState()
{
    // Mock IConsumer that blocks on StartConsumingAsync until released
    var consumerStarted = new TaskCompletionSource();
    var releaseStart = new TaskCompletionSource();
    var mockConsumer = new Mock<IConsumer>();
    mockConsumer.Setup(c => c.StartConsumingAsync(It.IsAny<string>(), It.IsAny<IList<string>>(), It.IsAny<ConsumerEventHandler>()))
        .Returns(async () =>
        {
            consumerStarted.SetResult();
            await releaseStart.Task.ConfigureAwait(false);
        });

    using var bus = CreateBusWithConsumer(mockConsumer.Object);

    var startTask = bus.StartConsumingAsync();
    await consumerStarted.Task.ConfigureAwait(false);
    var stopTask = bus.StopConsumingAsync();

    // Stop must not complete before Start releases the semaphore
    await Task.Delay(50).ConfigureAwait(false);
    Assert.False(stopTask.IsCompleted);

    releaseStart.SetResult();
    await startTask.ConfigureAwait(false);
    await stopTask.ConfigureAwait(false);

    Assert.False(bus.IsConnected);
}

[Fact]
public async Task StartConsumingAsync_PreCancelledToken_ThrowsOCE()
{
    var mockConsumer = new Mock<IConsumer>();
    using var bus = CreateBusWithConsumer(mockConsumer.Object);
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    await Assert.ThrowsAsync<OperationCanceledException>(
        () => bus.StartConsumingAsync(cts.Token));
    mockConsumer.Verify(c => c.StartConsumingAsync(
        It.IsAny<string>(), It.IsAny<IList<string>>(), It.IsAny<ConsumerEventHandler>()),
        Times.Never);
}
```

(If `CreateBusWithConsumer` helper does not exist, add one that uses the existing Bus constructor with mocks for logger, configuration, and registers the mock consumer the same way existing BusTests do.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "StartConsumingAsync_ConcurrentWithStop_SerializesState|StartConsumingAsync_PreCancelledToken_ThrowsOCE" -v quiet`
Expected: both FAIL. The first because Stop completes before Start (lock is released). The second because OCE is thrown at the entry guard (already added in Task 1) -- this one may PASS already. If so, mark step as "passes already" and continue.

- [ ] **Step 3: Add `SemaphoreSlim _lifecycleSemaphore` field to Bus**

In `src/ServiceConnect/Bus.cs`, add near the other private fields (below the existing `_stateLock`):

```csharp
private readonly SemaphoreSlim _lifecycleSemaphore = new(1, 1);
```

- [ ] **Step 4: Rewrite `StartConsumingAsync` and `StopConsumingAsync` to use the semaphore**

Replace the current `StartConsumingAsync` method:

```csharp
public async Task StartConsumingAsync(CancellationToken cancellationToken = default)
{
    ThrowIfDisposed();
    await _lifecycleSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        IConsumer localConsumer;
        List<string> messageTypeNames;

        lock (_stateLock)
        {
            if (_consumer == null)
                throw new InvalidOperationException("No consumer registered. Call UseRabbitMQ() or register an IConsumer.");

            messageTypeNames =
            [
                .. _handlerReferences
                    .Select(h => h.MessageType.FullName!.Replace(".", string.Empty))
                    .Distinct()
            ];

            localConsumer = _consumer;
        }

        _logger.LogInformation("Bus starting to consume on queue {QueueName} for {Count} message types.",
            _queueConfig.QueueName, messageTypeNames.Count);

        await localConsumer.StartConsumingAsync(_queueConfig.QueueName, messageTypeNames, _dispatcher.Dispatch).ConfigureAwait(false);

        lock (_stateLock) { _consuming = true; }
    }
    finally
    {
        _lifecycleSemaphore.Release();
    }
}
```

Replace the current `StopConsumingAsync`:

```csharp
public async Task StopConsumingAsync(CancellationToken cancellationToken = default)
{
    ThrowIfDisposed();
    await _lifecycleSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        IConsumer? localConsumer = null;
        lock (_stateLock)
        {
            _logger.LogInformation("Bus stopping message consumption.");
            if (_consuming)
            {
                _consuming = false;
                localConsumer = _consumer;
            }
        }
        if (localConsumer != null)
            await localConsumer.DisposeAsync().ConfigureAwait(false);
    }
    finally
    {
        _lifecycleSemaphore.Release();
    }
}
```

Update `DisposeAsync` to dispose the semaphore after the stop:

`DisposeAsync` sets `_disposed = true` before calling stop, so the public `StopConsumingAsync` (which calls `ThrowIfDisposed()`) cannot be used here -- it would throw `ObjectDisposedException` and prevent clean shutdown. Three options: (a) suppress the disposed check in `StopConsumingAsync`; (b) extract a private core helper that omits the guard; (c) inline the stop logic in `DisposeAsync`. **Chosen approach: option (b)** -- extract a private helper `StopConsumingCoreAsync` that contains the semaphore work but omits `ThrowIfDisposed()`. `StopConsumingAsync` calls `ThrowIfDisposed()` then delegates. `DisposeAsync` calls the core helper directly.

```csharp
public async ValueTask DisposeAsync()
{
    lock (_stateLock)
    {
        if (_disposed) return;
        _disposed = true;
    }

    // Call the core helper, not the public method, to skip ThrowIfDisposed.
    await StopConsumingCoreAsync().ConfigureAwait(false);
    _sendPipeline.Dispose();
    if (_producer != null)
        await _producer.DisposeAsync().ConfigureAwait(false);
    _lifecycleSemaphore.Dispose();
}
```

Remove the old "Dispose outside the lock to avoid deadlock with consumer callback chain" comment -- no longer applicable; the semaphore handles ordering.

- [ ] **Step 5: Run the new tests**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "StartConsumingAsync_ConcurrentWithStop_SerializesState|StartConsumingAsync_PreCancelledToken_ThrowsOCE|StopConsumingAsync_WhileStartInFlight_WaitsForStartToComplete" -v quiet`
Expected: all three PASS.

- [ ] **Step 6: Run the full unit test suite**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "Category!=Docker" -v quiet`
Expected: all tests pass. No regressions.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect/Bus.cs src/ServiceConnect.UnitTests/BusTests.cs
git commit -m "$(cat <<'EOF'
fix: serialize Bus lifecycle with SemaphoreSlim, fix R-034 race

Replace lock-then-release-before-await pattern with SemaphoreSlim(1,1)
around StartConsumingAsync and StopConsumingAsync. Prevents concurrent
callers from setting _consuming inconsistently. SemaphoreSlim.WaitAsync(ct)
allows the caller to abort its wait without affecting the in-flight
operation.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Add CancellationToken to IConsumeContext and ConsumeContext

**Files:**
- Modify: `src/ServiceConnect.Interfaces/IConsumeContext.cs`
- Modify: `src/ServiceConnect/Services/ConsumeContext.cs`

**Scope:** Add a `CancellationToken` property (settable) and a CT parameter to `ReplyAsync`. User handlers read `Context.CancellationToken` -- no handler signature change required.

- [ ] **Step 1: Update `IConsumeContext` interface**

Replace file contents:

```csharp
namespace ServiceConnect.Interfaces;

public interface IConsumeContext
{
    IBus Bus { get; }
    IDictionary<string, object> Headers { get; }
    string? MessageId { get; }
    Guid CorrelationId { get; }
    CancellationToken CancellationToken { get; set; }
    Task ReplyAsync<TReply>(TReply message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default) where TReply : Message;
}
```

- [ ] **Step 2: Update `ConsumeContext` class**

Replace `src/ServiceConnect/Services/ConsumeContext.cs`:

```csharp
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Services;

public sealed class ConsumeContext(IBus bus, IDictionary<string, object> headers) : IConsumeContext
{
    public IBus Bus { get; } = bus;
    public IDictionary<string, object> Headers { get; } = headers;
    public CancellationToken CancellationToken { get; set; }

    public string? MessageId =>
        Headers.TryGetValue(HeaderKeys.MessageId, out var value) ? HeaderDecoder.Decode(value) : null;

    public Guid CorrelationId =>
        Headers.TryGetValue(HeaderKeys.CorrelationId, out var value) && Guid.TryParse(HeaderDecoder.Decode(value), out var id)
            ? id : Guid.Empty;

    public async Task ReplyAsync<TReply>(TReply message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default) where TReply : Message
    {
        var sourceAddress = Headers.TryGetValue(HeaderKeys.SourceAddress, out var sa) ? HeaderDecoder.Decode(sa) : null;
        if (string.IsNullOrEmpty(sourceAddress))
            throw new InvalidOperationException("Cannot reply: incoming message has no SourceAddress header.");

        var requestMessageId = Headers.TryGetValue(HeaderKeys.RequestMessageId, out var rmi) ? HeaderDecoder.Decode(rmi) : null;

        var replyHeaders = headers ?? [];
        if (!string.IsNullOrEmpty(requestMessageId))
            replyHeaders[HeaderKeys.ResponseMessageId] = requestMessageId;

        var options = new SendOptions { EndPoint = sourceAddress, Headers = replyHeaders };
        await Bus.SendAsync(message, options, cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/ServiceConnect.sln`
Expected: succeeds. Any other `IConsumeContext` implementations (in unit tests) must add the `CancellationToken` property -- fix compile errors by adding `public CancellationToken CancellationToken { get; set; }` to each.

- [ ] **Step 4: Run unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "Category!=Docker" -v quiet`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Interfaces/IConsumeContext.cs src/ServiceConnect/Services/ConsumeContext.cs src/ServiceConnect.UnitTests
git commit -m "$(cat <<'EOF'
feat: add CancellationToken property to IConsumeContext (R-016)

Handlers read CT via Context.CancellationToken. ReplyAsync gains optional
CT parameter. User handler signatures (IMessageHandler<T>, IStreamHandler<T>,
Aggregator<T>) are unchanged.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Add CancellationToken to IConsumer + RabbitMQ Consumer

**Files:**
- Modify: `src/ServiceConnect.Interfaces/IConsumer.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer.cs`
- Modify: `src/ServiceConnect/Bus.cs` (pass CT down from StartConsumingAsync)

**Scope:** `IConsumer.StartConsumingAsync` gains CT. RabbitMQ `Consumer.cs` accepts it, stores it on the instance for the internal dispatch loop, and passes it to any RabbitMQ client call that supports CT (`BasicConsumeAsync` etc.).

- [ ] **Step 1: Update `IConsumer`**

Replace `src/ServiceConnect.Interfaces/IConsumer.cs`:

```csharp
namespace ServiceConnect.Interfaces;

public interface IConsumer : IAsyncDisposable
{
    bool IsConnected { get; }
    Task StartConsumingAsync(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Update RabbitMQ `Consumer.cs`**

Add a private field `private CancellationToken _consumingCt;`. Update `StartConsumingAsync` signature to accept CT and store it:

```csharp
public async Task StartConsumingAsync(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler, CancellationToken cancellationToken = default)
{
    _consumingCt = cancellationToken;
    cancellationToken.ThrowIfCancellationRequested();
    // ... rest of existing body, passing cancellationToken to any RabbitMQ.Client async call that supports it (BasicConsumeAsync, QueueDeclareAsync, ExchangeDeclareAsync, etc.)
}
```

For each RabbitMQ.Client API that supports CT in the installed version, pass the token. Where the driver does not accept CT, leave the call unchanged.

- [ ] **Step 3: Update `Bus.StartConsumingAsync` to pass CT down**

In `src/ServiceConnect/Bus.cs`, change the consumer start line to pass the token:

```csharp
await localConsumer.StartConsumingAsync(_queueConfig.QueueName, messageTypeNames, _dispatcher.Dispatch, cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 4: Build and test**

Run: `dotnet build src/ServiceConnect.sln`
Run: `dotnet test src/ServiceConnect.UnitTests --filter "Category!=Docker" -v quiet`
Expected: build succeeds, unit tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Interfaces/IConsumer.cs src/ServiceConnect.Client.RabbitMQ/Consumer.cs src/ServiceConnect/Bus.cs
git commit -m "$(cat <<'EOF'
feat: add CancellationToken to IConsumer.StartConsumingAsync (R-016)

Consumer stores the token for observation by its internal dispatch loop
and forwards to RabbitMQ.Client async APIs where supported. Bus passes
the token down from its StartConsumingAsync.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Add CancellationToken to IProducer + RabbitMQ Producer

**Files:**
- Modify: `src/ServiceConnect.Interfaces/IProducer.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer.cs`

**Scope:** Every async method on `IProducer` gains CT. RabbitMQ `Producer.cs` accepts and forwards to driver calls where supported.

- [ ] **Step 1: Update `IProducer`**

Replace file:

```csharp
namespace ServiceConnect.Interfaces;

public interface IProducer : IAsyncDisposable
{
    Task PublishAsync(Type type, byte[] message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default);
    Task SendAsync(Type type, byte[] message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default);
    Task SendAsync(string endPoint, Type type, byte[] message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default);
    Task SendBytesAsync(string endPoint, byte[] packet, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default);
    long MaximumMessageSize { get; }
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
```

Preserve XML doc comments.

- [ ] **Step 2: Update RabbitMQ `Producer.cs`**

Each async method gains the CT parameter and calls `cancellationToken.ThrowIfCancellationRequested()` at method entry. Forward CT to any RabbitMQ.Client async call that accepts it (e.g., `BasicPublishAsync`, `CloseAsync`).

- [ ] **Step 3: Build and test**

Run: `dotnet build src/ServiceConnect.sln`
Run: `dotnet test src/ServiceConnect.UnitTests --filter "Category!=Docker" -v quiet`
Expected: build succeeds, unit tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Interfaces/IProducer.cs src/ServiceConnect.Client.RabbitMQ/Producer.cs
git commit -m "$(cat <<'EOF'
feat: add CancellationToken to IProducer async methods (R-016)

All IProducer async methods accept optional CT. RabbitMQ producer forwards
tokens to driver APIs where supported.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Add CancellationToken to IMessageProcessor and all implementations

**Files:**
- Modify: `src/ServiceConnect.Interfaces/IMessageProcessor.cs`
- Modify: `src/ServiceConnect/Processors/HandlerProcessor.cs`
- Modify: `src/ServiceConnect/Processors/ProcessManagerProcessor.cs`
- Modify: `src/ServiceConnect/Processors/AggregatorProcessor.cs`
- Modify: `src/ServiceConnect/Processors/StreamProcessor.cs`
- Modify: `src/ServiceConnect/Processors/ReplyProcessor.cs` (if it implements IMessageProcessor)
- Modify: `src/ServiceConnect.UnitTests/**` any processor test doubles

**Scope:** `IMessageProcessor.ProcessAsync` gains `CancellationToken cancellationToken = default`. Each processor threads CT into: (a) `IConsumeContext.CancellationToken = ct` before invoking the handler; (b) any async call it makes. At this task, persistors are still sync -- skip wiring CT to persistors (done in Task 12).

- [ ] **Step 1: Update `IMessageProcessor`**

Replace `src/ServiceConnect.Interfaces/IMessageProcessor.cs`:

```csharp
namespace ServiceConnect.Interfaces;

public enum ProcessResult { Handled, NotHandled }

public interface IMessageProcessor
{
    bool RunBeforeDeserialization => false;

    Task<ProcessResult> ProcessAsync(
        byte[] messageBytes,
        Type messageType,
        object? message,
        IDictionary<string, object> headers,
        Envelope envelope,
        CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Update each processor's `ProcessAsync` signature and body**

For each of `HandlerProcessor`, `ProcessManagerProcessor`, `AggregatorProcessor`, `StreamProcessor`, `ReplyProcessor`:
- Add `CancellationToken cancellationToken = default` as last parameter
- After constructing/acquiring `IConsumeContext`, set `context.CancellationToken = cancellationToken;` before invoking any handler
- Call `cancellationToken.ThrowIfCancellationRequested()` at method entry

Example for `HandlerProcessor` (adapt pattern for others):

```csharp
public async Task<ProcessResult> ProcessAsync(
    byte[] messageBytes,
    Type messageType,
    object? message,
    IDictionary<string, object> headers,
    Envelope envelope,
    CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    // ... existing body; when creating the IConsumeContext, set context.CancellationToken = cancellationToken
    // before invoking handler.HandleAsync(message)
}
```

- [ ] **Step 3: Build and fix test-double compile errors**

Run: `dotnet build src/ServiceConnect.sln`
Expected: build succeeds. Any test double that implements `IMessageProcessor` needs the new parameter. Fix compile errors by adding `CancellationToken cancellationToken = default` to each test-double signature.

- [ ] **Step 4: Run unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "Category!=Docker" -v quiet`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Interfaces/IMessageProcessor.cs src/ServiceConnect/Processors src/ServiceConnect.UnitTests
git commit -m "$(cat <<'EOF'
feat: add CancellationToken to IMessageProcessor and implementations (R-016)

Processors set IConsumeContext.CancellationToken before invoking user
handlers, so handlers can access it via Context.CancellationToken.
Persistor wiring is deferred until Task 12 when persistors become async.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: Add CancellationToken to middleware delegates + MessageDispatcher + SendMessagePipeline

**Files:**
- Modify: `src/ServiceConnect.Interfaces/IMessageProcessingMiddleware.cs`
- Modify: `src/ServiceConnect.Interfaces/ISendMessageMiddleware.cs`
- Modify: `src/ServiceConnect/Services/MessageDispatcher.cs`
- Modify: `src/ServiceConnect/Services/SendMessagePipeline.cs`
- Modify: `src/ServiceConnect/Bus.cs` (pass CT into pipelines)
- Modify: any user-written middleware classes in the codebase (none expected in repo)

**Scope:** Middleware delegates gain CT, pipelines thread CT through their chain, Bus passes CT into pipelines.

- [ ] **Step 1: Update delegate types and interfaces**

Replace `src/ServiceConnect.Interfaces/IMessageProcessingMiddleware.cs`:

```csharp
namespace ServiceConnect.Interfaces;

public delegate Task<ConsumeEventResult> MessageProcessingDelegate(
    byte[] messageBytes, Type messageType, object message,
    IDictionary<string, object> headers, Envelope envelope,
    CancellationToken cancellationToken);

public interface IMessageProcessingMiddleware
{
    Task<ConsumeEventResult> Process(
        byte[] messageBytes, Type messageType, object message,
        IDictionary<string, object> headers, Envelope envelope,
        MessageProcessingDelegate next,
        CancellationToken cancellationToken);
}
```

Replace `src/ServiceConnect.Interfaces/ISendMessageMiddleware.cs`:

```csharp
namespace ServiceConnect.Interfaces;

public delegate Task SendMessageDelegate(
    Type typeObject, byte[] messageBytes,
    Dictionary<string, string> headers, string? endPoint,
    CancellationToken cancellationToken);

public interface ISendMessageMiddleware
{
    Task Process(Type typeObject, byte[] messageBytes,
        Dictionary<string, string> headers, string? endPoint,
        SendMessageDelegate next,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Update `MessageDispatcher.cs` and `SendMessagePipeline.cs`**

Each pipeline's entry method (`Dispatch`, `ExecutePublishMessagePipelineAsync`, `ExecuteSendMessagePipelineAsync`) adds CT as the last parameter. The pipeline-building code (likely `.Aggregate(...)` over a list of middleware) passes CT through each delegate invocation. Use the existing code as a template -- the change is mechanical: every call to `next(...)` gains `, cancellationToken` and every invocation of a middleware's `Process` gets `cancellationToken` passed through.

- [ ] **Step 3: Update `Bus.cs` to pass CT into pipelines**

Every place Bus invokes `_sendPipeline.ExecutePublishMessagePipelineAsync(...)` or `_sendPipeline.ExecuteSendMessagePipelineAsync(...)` passes `cancellationToken`. The existing pipeline invocations are in `PublishAsync`, `SendAsync`, `SendRequestAsync`, `SendRequestMultiAsync`, `PublishRequestAsync`, `RouteAsync`.

Similarly, `_dispatcher.Dispatch` (used as consumer callback) -- `IConsumer.StartConsumingAsync` passes a `ConsumerEventHandler` delegate. Update `ConsumerEventHandler` delegate signature (in `src/ServiceConnect.Interfaces/ConsumerEventHandler.cs` -- find it first) to accept CT, and update `MessageDispatcher.Dispatch` to match.

- [ ] **Step 4: Build and test**

Run: `dotnet build src/ServiceConnect.sln`
Expected: build succeeds; any user middleware in tests needs CT added -- fix as they surface.
Run: `dotnet test src/ServiceConnect.UnitTests --filter "Category!=Docker" -v quiet`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Interfaces/IMessageProcessingMiddleware.cs src/ServiceConnect.Interfaces/ISendMessageMiddleware.cs src/ServiceConnect/Services src/ServiceConnect/Bus.cs src/ServiceConnect.Interfaces/ConsumerEventHandler.cs src/ServiceConnect.UnitTests
git commit -m "$(cat <<'EOF'
feat: thread CancellationToken through middleware pipelines (R-016)

MessageProcessingDelegate and SendMessageDelegate gain CT parameter.
Middleware interfaces Process() methods accept CT. MessageDispatcher and
SendMessagePipeline thread CT through delegate chains. Bus passes CT into
pipeline entry points.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: Add CancellationToken to IRequestReplyManager with linked CTS

**Files:**
- Modify: `src/ServiceConnect.Interfaces/IRequestReplyManager.cs`
- Modify: `src/ServiceConnect/Services/RequestReplyManager.cs`
- Modify: `src/ServiceConnect.UnitTests/RequestReplyManagerTests.cs` (create if missing)
- Modify: `src/ServiceConnect/Bus.cs` (pass CT to RRM calls)

**Scope:** External CT is linked with internal timeout CTS via `CreateLinkedTokenSource`. On trigger, distinguish external cancel (throw `OperationCanceledException`) from timeout (throw existing `RequestTimeoutException`). TDD: write tests first.

- [ ] **Step 1: Update `IRequestReplyManager` interface**

```csharp
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Interfaces;

public interface IRequestReplyManager
{
    Task<TReply> SendRequestAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        Func<Type, byte[], Dictionary<string, string>, string?, CancellationToken, Task> sendAction,
        RequestOptions options,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message;

    Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        Func<Type, byte[], Dictionary<string, string>, string?, CancellationToken, Task> sendAction,
        RequestOptions options,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message;

    void ProcessReply(string messageId, byte[] messageBytes, Type type);
}
```

(Note: `sendAction` gains CT parameter so pipelines see it.)

- [ ] **Step 2: Write failing tests**

Add to `src/ServiceConnect.UnitTests/RequestReplyManagerTests.cs` (create file if it does not exist):

```csharp
[Fact]
public async Task SendRequestAsync_ExternalCancel_ThrowsOCE_NotRequestTimeoutException()
{
    var rrm = new RequestReplyManager(Mock.Of<ILogger<RequestReplyManager>>(), Mock.Of<IMessageTypeRegistry>(), Mock.Of<ISerializer>());
    using var externalCts = new CancellationTokenSource();
    var options = new RequestOptions { Timeout = TimeSpan.FromMinutes(5) };
    var task = rrm.SendRequestAsync<TestRequest, TestReply>(
        [0], new Dictionary<string, string>(),
        (type, bytes, headers, endpoint, ct) => Task.CompletedTask,
        options,
        externalCts.Token);

    externalCts.Cancel();

    await Assert.ThrowsAsync<OperationCanceledException>(() => task);
}

[Fact]
public async Task SendRequestAsync_Timeout_ThrowsRequestTimeoutException()
{
    var rrm = new RequestReplyManager(Mock.Of<ILogger<RequestReplyManager>>(), Mock.Of<IMessageTypeRegistry>(), Mock.Of<ISerializer>());
    var options = new RequestOptions { Timeout = TimeSpan.FromMilliseconds(50) };
    var task = rrm.SendRequestAsync<TestRequest, TestReply>(
        [0], new Dictionary<string, string>(),
        (type, bytes, headers, endpoint, ct) => Task.CompletedTask,
        options,
        CancellationToken.None);

    await Assert.ThrowsAsync<RequestTimeoutException>(() => task);
}

[Fact]
public async Task SendRequestAsync_ExternalCancelBeforeSend_ThrowsImmediately()
{
    var rrm = new RequestReplyManager(Mock.Of<ILogger<RequestReplyManager>>(), Mock.Of<IMessageTypeRegistry>(), Mock.Of<ISerializer>());
    using var externalCts = new CancellationTokenSource();
    externalCts.Cancel();
    var options = new RequestOptions { Timeout = TimeSpan.FromMinutes(5) };

    await Assert.ThrowsAsync<OperationCanceledException>(() =>
        rrm.SendRequestAsync<TestRequest, TestReply>(
            [0], new Dictionary<string, string>(),
            (type, bytes, headers, endpoint, ct) => Task.CompletedTask,
            options, externalCts.Token));
}

// TestRequest and TestReply are simple Message subclasses used only in this test file
public class TestRequest : Message { public TestRequest() : base(Guid.NewGuid()) {} }
public class TestReply : Message { public TestReply() : base(Guid.NewGuid()) {} }
```

- [ ] **Step 3: Run tests to verify they fail to compile/run**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "SendRequestAsync" -v quiet`
Expected: FAIL (compile error or wrong exception type).

- [ ] **Step 4: Implement linked CTS in `RequestReplyManager.cs`**

Rewrite `SendRequestAsync` (and mirror for `SendRequestMultiAsync`) to use:

```csharp
public async Task<TReply> SendRequestAsync<TRequest, TReply>(
    byte[] messageBytes,
    Dictionary<string, string> headers,
    Func<Type, byte[], Dictionary<string, string>, string?, CancellationToken, Task> sendAction,
    RequestOptions options,
    CancellationToken cancellationToken = default)
    where TRequest : Message
    where TReply : Message
{
    cancellationToken.ThrowIfCancellationRequested();

    var messageId = Guid.NewGuid().ToString();
    headers[HeaderKeys.MessageId] = messageId;
    var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
    _pending[messageId] = (tcs, typeof(TReply));

    using var timeoutCts = new CancellationTokenSource(options.Timeout);
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

    using var registration = linkedCts.Token.Register(() =>
    {
        _pending.TryRemove(messageId, out _);
        if (cancellationToken.IsCancellationRequested)
            tcs.TrySetCanceled(cancellationToken);
        else
            tcs.TrySetException(new RequestTimeoutException(messageId, options.Timeout));
    });

    try
    {
        await sendAction(typeof(TRequest), messageBytes, headers, options.EndPoint, cancellationToken).ConfigureAwait(false);
        var replyBytes = await tcs.Task.ConfigureAwait(false);
        return (TReply)_serializer.Deserialize(replyBytes, typeof(TReply))!;
    }
    finally
    {
        _pending.TryRemove(messageId, out _);
    }
}
```

Adjust details to match existing `_pending` dictionary shape and serializer usage in the current file. The key additions are: `cancellationToken` parameter, the `linkedCts` construction, and the `Register` callback distinguishing cancel vs timeout.

`SendRequestMultiAsync` uses the same linked-CTS pattern. On timeout, yield whatever replies were collected (existing behavior); on external cancel, throw OCE.

- [ ] **Step 5: Update `Bus.cs` request/reply callers**

Pass CT to RRM calls in `SendRequestAsync`, `SendRequestMultiAsync`, `PublishRequestAsync`:

```csharp
return await _requestReplyManager.SendRequestAsync<T, TReply>(
    bytes, headers, _sendPipeline.ExecuteSendMessagePipelineAsync, options, cancellationToken).ConfigureAwait(false);
```

The `sendAction` delegate type now takes CT, so the pipeline method signature must match (already done in Task 7).

- [ ] **Step 6: Run tests**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "SendRequestAsync" -v quiet`
Expected: all three new tests pass.
Run: `dotnet test src/ServiceConnect.UnitTests --filter "Category!=Docker" -v quiet`
Expected: all tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Interfaces/IRequestReplyManager.cs src/ServiceConnect/Services/RequestReplyManager.cs src/ServiceConnect/Bus.cs src/ServiceConnect.UnitTests/RequestReplyManagerTests.cs
git commit -m "$(cat <<'EOF'
feat: add CancellationToken to RequestReplyManager with linked timeout CTS

External CT is linked with internal timeout CTS via CreateLinkedTokenSource.
External cancel throws OperationCanceledException; timeout preserves
existing RequestTimeoutException. Bus passes CT through to RRM.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: Migrate IProcessManagerFinder + ITimeoutStore to async

**Files:**
- Modify: `src/ServiceConnect.Interfaces/IProcessManagerFinder.cs`
- Modify: `src/ServiceConnect.Interfaces/ITimeoutStore.cs`
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs` (implements both)
- Modify: `src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs` (implements both)
- Modify: `src/ServiceConnect/Services/ProcessManagerTimeoutService.cs` (uses ITimeoutStore)
- Modify: `src/ServiceConnect/Processors/ProcessManagerProcessor.cs` (uses IProcessManagerFinder)
- Modify: `src/ServiceConnect.UnitTests/InMemoryProcessManagerFinderTests.cs`
- Modify: `src/ServiceConnect.UnitTests/Services/ProcessManagerTimeoutServiceTests.cs`

**Scope:** Both interfaces go fully async with CT. They're implemented on the same class per backend, so one file update covers both interfaces per backend.

- [ ] **Step 1: Update `IProcessManagerFinder`**

Replace:

```csharp
namespace ServiceConnect.Interfaces;

public delegate void TimeoutInsertedDelegate(DateTime timeoutTime);

public interface IProcessManagerFinder
{
    Task<IPersistenceData<T>?> FindDataAsync<T>(IProcessManagerPropertyMapper mapper, Message message, CancellationToken cancellationToken = default) where T : class, IProcessManagerData;
    Task InsertDataAsync(IProcessManagerData data, CancellationToken cancellationToken = default);
    Task UpdateDataAsync<T>(IPersistenceData<T> data, CancellationToken cancellationToken = default) where T : class, IProcessManagerData;
    Task DeleteDataAsync<T>(IPersistenceData<T> data, CancellationToken cancellationToken = default) where T : class, IProcessManagerData;
}
```

- [ ] **Step 2: Update `ITimeoutStore`**

Replace:

```csharp
namespace ServiceConnect.Interfaces;

public interface ITimeoutStore
{
    event TimeoutInsertedDelegate? TimeoutInserted;
    Task InsertTimeoutAsync(TimeoutData timeoutData, CancellationToken cancellationToken = default);
    Task<TimeoutsBatch> GetTimeoutsBatchAsync(CancellationToken cancellationToken = default);
    Task RemoveDispatchedTimeoutAsync(Guid id, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 3: Update `InMemoryProcessManagerFinder.cs`**

For each method, convert to async signature and wrap sync body in `Task.FromResult`/`Task.CompletedTask`. Call `cancellationToken.ThrowIfCancellationRequested()` at entry.

Example:

```csharp
public Task<IPersistenceData<T>?> FindDataAsync<T>(IProcessManagerPropertyMapper mapper, Message message, CancellationToken cancellationToken = default) where T : class, IProcessManagerData
{
    cancellationToken.ThrowIfCancellationRequested();
    // ... existing body with `return data;` replaced by `return Task.FromResult<IPersistenceData<T>?>(data);`
}

public Task InsertDataAsync(IProcessManagerData data, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    // ... existing body
    return Task.CompletedTask;
}
```

Apply the same pattern to `UpdateDataAsync`, `DeleteDataAsync`, `InsertTimeoutAsync`, `GetTimeoutsBatchAsync`, `RemoveDispatchedTimeoutAsync`.

- [ ] **Step 4: Update `MongoDbProcessManagerFinder.cs`**

Replace each blocking MongoDB call with its native async counterpart, passing CT:
- `.Find(...).FirstOrDefault()` → `await collection.Find(...).FirstOrDefaultAsync(cancellationToken)`
- `.InsertOne(doc)` → `await collection.InsertOneAsync(doc, cancellationToken: cancellationToken)`
- `.UpdateOne(filter, update)` → `await collection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken)`
- `.DeleteOne(filter)` → `await collection.DeleteOneAsync(filter, cancellationToken)`
- `.Find(...).ToList()` → `await collection.Find(...).ToListAsync(cancellationToken)`

Mark methods `async Task` / `async Task<T>` accordingly.

- [ ] **Step 5: Update `ProcessManagerTimeoutService.cs`**

In its BackgroundService `ExecuteAsync(CancellationToken stoppingToken)` loop, update calls to use `await _timeoutStore.GetTimeoutsBatchAsync(stoppingToken)`, `await _timeoutStore.RemoveDispatchedTimeoutAsync(id, stoppingToken)`, etc.

- [ ] **Step 6: Update `ProcessManagerProcessor.cs`**

Change `_finder.FindData<T>(mapper, message)` to `await _finder.FindDataAsync<T>(mapper, message, cancellationToken)`. Likewise for `InsertData`, `UpdateData`, `DeleteData` calls. The method is already async; add `await` and pass the CT.

- [ ] **Step 7: Update unit tests**

In `InMemoryProcessManagerFinderTests.cs` and `ProcessManagerTimeoutServiceTests.cs`, update callers to `await` the new async methods. Pattern: each `finder.FindData(...)` becomes `await finder.FindDataAsync(..., CancellationToken.None)`. Any test method body that calls the finder must be `async Task`.

- [ ] **Step 8: Add new pre-cancelled-token test to InMemoryProcessManagerFinderTests**

```csharp
[Fact]
public async Task FindDataAsync_PreCancelledToken_ThrowsOCE()
{
    var finder = new InMemoryProcessManagerFinder();
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var mapper = new Mock<IProcessManagerPropertyMapper>().Object;
    await Assert.ThrowsAsync<OperationCanceledException>(
        () => finder.FindDataAsync<TestProcessManagerData>(mapper, new TestMessage(), cts.Token));
}

// Similarly for InsertDataAsync, UpdateDataAsync, DeleteDataAsync,
// InsertTimeoutAsync, GetTimeoutsBatchAsync, RemoveDispatchedTimeoutAsync
```

- [ ] **Step 9: Build and test**

Run: `dotnet build src/ServiceConnect.sln`
Run: `dotnet test src/ServiceConnect.UnitTests --filter "Category!=Docker" -v quiet`
Expected: all tests pass.

- [ ] **Step 10: Commit**

```bash
git add src/ServiceConnect.Interfaces/IProcessManagerFinder.cs src/ServiceConnect.Interfaces/ITimeoutStore.cs src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs src/ServiceConnect/Services/ProcessManagerTimeoutService.cs src/ServiceConnect/Processors/ProcessManagerProcessor.cs src/ServiceConnect.UnitTests
git commit -m "$(cat <<'EOF'
refactor: migrate IProcessManagerFinder and ITimeoutStore to async (R-016)

Both interfaces fully async with CancellationToken. InMemory wraps sync
bodies with ThrowIfCancellationRequested + Task.FromResult. MongoDB uses
driver-native async APIs. ProcessManagerProcessor and TimeoutService
updated to await.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: Migrate IAggregatorPersistor to async

**Files:**
- Modify: `src/ServiceConnect.Interfaces/IAggregatorPersistor.cs`
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryAggregatorPersistor.cs`
- Modify: `src/ServiceConnect.Persistence.MongoDb/MongoDbAggregatorPersistor.cs`
- Modify: `src/ServiceConnect/Processors/AggregatorProcessor.cs`
- Modify: `src/ServiceConnect.UnitTests/InMemoryAggregatorPersistorTests.cs`

**Scope:** Same pattern as Task 9 but for aggregator persistor.

- [ ] **Step 1: Update `IAggregatorPersistor`**

```csharp
namespace ServiceConnect.Interfaces;

public interface IAggregatorPersistor
{
    Task InsertDataAsync(object data, string name, CancellationToken cancellationToken = default);
    Task<IList<object>> GetDataAsync(string name, CancellationToken cancellationToken = default);
    Task RemoveDataAsync(string name, Guid correlationId, CancellationToken cancellationToken = default);
    Task<int> CountAsync(string name, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Update `InMemoryAggregatorPersistor.cs`**

Same wrap-sync-in-Task pattern as Task 9, with `cancellationToken.ThrowIfCancellationRequested()` at each method entry.

- [ ] **Step 3: Update `MongoDbAggregatorPersistor.cs`**

Use MongoDB driver async APIs, passing CT.

- [ ] **Step 4: Update `AggregatorProcessor.cs`**

Each `_persistor.InsertData(...)` → `await _persistor.InsertDataAsync(..., cancellationToken)`. Same for `GetData`, `RemoveData`, `Count`.

- [ ] **Step 5: Update `InMemoryAggregatorPersistorTests.cs`**

`async Task` test methods; `await` the async calls. Add one `PreCancelledToken_ThrowsOCE` test per method.

- [ ] **Step 6: Build and test**

Run: `dotnet build src/ServiceConnect.sln`
Run: `dotnet test src/ServiceConnect.UnitTests --filter "Category!=Docker" -v quiet`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Interfaces/IAggregatorPersistor.cs src/ServiceConnect.Persistence.InMemory/InMemoryAggregatorPersistor.cs src/ServiceConnect.Persistence.MongoDb/MongoDbAggregatorPersistor.cs src/ServiceConnect/Processors/AggregatorProcessor.cs src/ServiceConnect.UnitTests/InMemoryAggregatorPersistorTests.cs
git commit -m "$(cat <<'EOF'
refactor: migrate IAggregatorPersistor to async (R-016)

Interface fully async with CancellationToken. AggregatorProcessor awaits
persistor calls. InMemory + MongoDB implementations converted to native
async patterns.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 11: New CancellationE2ETests

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/CancellationE2ETests.cs`

**Scope:** Two E2E scenarios to validate cancellation works across the transport stack.

- [ ] **Step 1: Create the test file with two E2E cases**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Trait("Category", "Docker")]
public class CancellationE2ETests : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddServiceConnect(c => c
            .UseRabbitMQ("amqp://guest:guest@localhost:5672")
            .SetQueueName($"cancellation-e2e-{Guid.NewGuid():N}"));
        _host = builder.Build();
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task StartConsumingAsync_WithPreCancelledToken_DoesNotHang()
    {
        var bus = _host.Services.GetRequiredService<IBus>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => bus.StartConsumingAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SendRequestAsync_ExternalCancel_ThrowsOCE_DoesNotLeakPendingRequest()
    {
        var bus = _host.Services.GetRequiredService<IBus>();
        using var cts = new CancellationTokenSource();
        var options = new RequestOptions { Timeout = TimeSpan.FromMinutes(5) };
        var task = bus.SendRequestAsync<CancellationTestRequest, CancellationTestReply>(
            new CancellationTestRequest(), options, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        // Future work: assert RequestReplyManager internal state is clean (no leaked entries) --
        // currently internal; covered by RequestReplyManagerTests unit tests via the try/finally removal.
    }
}

public class CancellationTestRequest : Message { public CancellationTestRequest() : base(Guid.NewGuid()) {} }
public class CancellationTestReply : Message { public CancellationTestReply() : base(Guid.NewGuid()) {} }
```

(Adjust the `AddServiceConnect` configuration helper call to match this project's actual extension method name -- check one of the existing E2E test files for reference.)

- [ ] **Step 2: Build**

Run: `dotnet build src/ServiceConnect.EndToEndTests`
Expected: succeeds.

- [ ] **Step 3: Commit (do not yet run -- Docker run happens in Task 13)**

```bash
git add src/ServiceConnect.EndToEndTests/CancellationE2ETests.cs
git commit -m "$(cat <<'EOF'
test: add E2E cancellation tests for Bus and request/reply

Covers StartConsumingAsync with pre-cancelled token and SendRequestAsync
external cancellation path. Marked [Trait("Category", "Docker")].

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 12: Update remaining-issues.md

**Files:**
- Modify: `docs/remaining-issues.md`

**Scope:** Mark R-016/B-01 (CancellationToken) and R-034 (race condition) as **Done (Group C-1)**. Leave other deferred items untouched.

- [ ] **Step 1: Edit `docs/remaining-issues.md`**

Update the relevant rows in both tables so the status shows "Done (Group C-1)":

| B-01 row | Change `Notes` column to read: `**Done** (Group C-1) -- optional CT parameter on all IBus async methods` |
| R-016 row | Change `Scope` column to read: `**Done** (Group C-1) -- completed with B-01` |
| R-034 row | Change `Scope` column to read: `**Done** (Group C-1) -- SemaphoreSlim lifecycle serialization in Bus` |

Keep R-009, R-017/R-018, R-020/R-021, R-028, R-032 rows unchanged (still deferred).

Add an entry note at the top of the "Tackle after" sentence to reflect the completion: `Issues verified against source code on 2026-04-12. R-016/B-01 and R-034 completed in Group C-1 on 2026-04-13.`

- [ ] **Step 2: Commit**

```bash
git add docs/remaining-issues.md
git commit -m "$(cat <<'EOF'
docs: mark R-016/B-01 and R-034 as done in remaining issues tracker

Completed in Group C-1 (CancellationToken end-to-end + async persistence).
R-017/R-018, R-009, R-020/R-021, R-028, R-032 remain deferred.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 13: Run end-to-end tests with sg docker

**Files:** none (execution only)

**Scope:** Validate the whole stack against RabbitMQ + MongoDB in Docker.

- [ ] **Step 1: Run the E2E suite in the background**

Run (as a background command; monitor with Monitor/TaskOutput tools):

```bash
sg docker -c "cd /home/tim/source/ServiceConnect-CSharp && dotnet test src/ServiceConnect.EndToEndTests -v quiet"
```

Expected: all E2E tests pass including the new `CancellationE2ETests` (2 tests) + the existing 71 tests. Total should be 73 passing.

- [ ] **Step 2: If any test fails, diagnose and fix**

Common failure patterns:
- Persistor async conversion missed a caller → `NullReferenceException` or hung task
- RabbitMQ client CT forwarding wrong API name → compile fails before test runs; fix pointed API
- E2E test environment not started (Docker containers) → restart `sg docker -c "docker compose up -d"` first

Make fixes, commit them as separate `fix:` commits, re-run E2E until green.

- [ ] **Step 3: Final verification**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "Category!=Docker" -v quiet`
Expected: all unit tests pass.

Run: `sg docker -c "dotnet test src/ServiceConnect.EndToEndTests -v quiet"`
Expected: 73/73 pass.

No commit needed unless fixes were made.

---

## Summary

**Tasks completed:**
1. IBus + Bus CT plumbing (source-compatible)
2. R-034 fix with SemaphoreSlim (TDD)
3. IConsumeContext CT property
4. IConsumer + RabbitMQ Consumer CT
5. IProducer + RabbitMQ Producer CT
6. IMessageProcessor + 4 processor implementations CT
7. Middleware delegates + pipelines CT
8. IRequestReplyManager + linked CTS (TDD)
9. IProcessManagerFinder + ITimeoutStore async migration
10. IAggregatorPersistor async migration
11. CancellationE2ETests
12. remaining-issues.md updated
13. E2E suite passes under Docker

**What this achieves:**
- R-016 / B-01: CancellationToken on all IBus async methods (covered in Tasks 1, 3-10)
- R-034: Bus lifecycle race condition fixed (Task 2)
- Core persistence layer fully async with CT (Tasks 9-10)
- Handler CT access via `Context.CancellationToken` (Task 6 set on context, Task 3 added property)
- No user-code handler signature changes

**Still deferred (for later Group C spec):**
- R-017 / R-018 (silent exception swallowing in dedup filter persistors) -- needs `IFilter` async
- R-009 (service locator DI migration)
- R-020 / R-021 (Processor / Client SRP)
- R-028 (test coverage)
- R-032 (DeduplicationFilterSettings singleton) -- depends on R-009
