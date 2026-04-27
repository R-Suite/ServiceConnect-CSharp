# Telemetry Extension Point Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the framework contract change (`SendContext`), the built-in telemetry middleware exposed by `builder.AddTelemetry()`, a runnable `examples/Telemetry/` sample, and matching website updates.

**Architecture:** Reshape `ISendMessageMiddleware` so the strongly-typed `Message` survives the send pipeline. Ship `TelemetrySendMiddleware`/`TelemetryProcessingMiddleware` plus an `AddTelemetry()` extension that registers them as singletons and inserts them at position 0 of the pipeline. The telemetry sample asserts cross-process W3C trace correlation via an in-process `ActivityListener`. Spec: [`docs/superpowers/specs/2026-04-27-telemetry-extension-point-design.md`](../specs/2026-04-27-telemetry-extension-point-design.md).

**Tech Stack:** .NET 8 / .NET 10, Moq, xUnit, OpenTelemetry, Astro/Starlight (website).

---

## File structure overview

**Created (framework contract):**
- `src/ServiceConnect.Interfaces/Pipelines/SendContext.cs`
- `src/ServiceConnect.Interfaces/Pipelines/SendOperation.cs`

**Modified (framework contract):**
- `src/ServiceConnect.Interfaces/Pipelines/ISendMessageMiddleware.cs`
- `src/ServiceConnect.Interfaces/Pipelines/SendMessageDelegate.cs`
- `src/ServiceConnect.Interfaces/Pipelines/ISendMessagePipeline.cs`
- `src/ServiceConnect/Services/SendMessagePipeline.cs`
- `src/ServiceConnect/Bus.cs` (PublishAsync, SendAsync only)
- `src/ServiceConnect.UnitTests/SendMessagePipelineTests.cs` (port `BlockingSendMiddleware`)
- `src/ServiceConnect.UnitTests/ServiceCollectionExtensionsTests.cs` (port `TestSendMiddleware`)
- `src/ServiceConnect.UnitTests/Services/MiddlewarePipelineTests.cs` (port `RecordingSendMiddleware`, `ShortCircuitSendMiddleware`)
- `src/ServiceConnect.EndToEndTests/Filters/MiddlewarePipelineE2ETests.cs` (port `HeaderAddingSendMiddleware`)

**Created (telemetry middleware + AddTelemetry):**
- `src/ServiceConnect.Telemetry/TelemetrySendMiddleware.cs`
- `src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs`
- `src/ServiceConnect.Telemetry/TelemetryBuilderExtensions.cs`
- `src/ServiceConnect.UnitTests/Telemetry/TelemetrySendMiddlewareTests.cs`
- `src/ServiceConnect.UnitTests/Telemetry/TelemetryProcessingMiddlewareTests.cs`
- `src/ServiceConnect.UnitTests/Telemetry/TelemetryBuilderExtensionsTests.cs`
- `src/ServiceConnect.EndToEndTests/Telemetry/TelemetryE2ETests.cs`

**Created (sample):**
- `examples/Telemetry/Telemetry.sln`
- `examples/Telemetry/run.sh`, `run.ps1`, `README.md`
- `examples/Telemetry/src/ServiceConnect.Examples.Telemetry.Contracts/OrderPlaced.cs` + csproj
- `examples/Telemetry/src/ServiceConnect.Examples.Telemetry.Publisher/{Program.cs, TelemetryConsoleListener.cs}` + csproj
- `examples/Telemetry/src/ServiceConnect.Examples.Telemetry.BillingSubscriber/{Program.cs, OrderPlacedHandler.cs, TelemetryConsoleListener.cs}` + csproj
- `examples/Telemetry/src/ServiceConnect.Examples.Telemetry.AnalyticsSubscriber/{...}` (mirror of Billing) + csproj

**Modified (website):**
- `website/src/content/docs/learn/operations/observability.mdx` (rewrite Tracing section)
- `website/src/content/docs/reference/handlers/event-args.mdx` (replace usage example)
- `website/src/content/docs/reference/filters/isendmessagemiddleware.mdx` (sync to new signature)
- `website/src/content/docs/reference/configuration/ipipelineconfiguration.mdx` (sync if it shows the signature)
- `website/src/content/docs/learn/messaging-patterns/filters.mdx` (sync if it shows the signature)
- `website/src/content/docs/samples.mdx` (add Telemetry entry)

---

## Phase 1 — Framework contract change

The contract change is **inherently atomic**: introducing a new `SendContext`-shaped middleware contract while leaving callers/implementations on the old shape will not compile. Tasks 1.1–1.4 land in **one commit**. Task 1.5 onwards build on top.

### Task 1.1: Add `SendOperation` enum and `SendContext` class (additive)

**Files:**
- Create: `src/ServiceConnect.Interfaces/Pipelines/SendOperation.cs`
- Create: `src/ServiceConnect.Interfaces/Pipelines/SendContext.cs`

These are additive — no existing code references them yet — so we add them first to keep diffs reviewable.

- [ ] **Step 1: Create `SendOperation.cs`**

```csharp
namespace ServiceConnect.Interfaces;

/// <summary>
/// Categorises the call site that produced a <see cref="SendContext"/>.
/// </summary>
public enum SendOperation
{
    /// <summary>Originated from <see cref="IBus.PublishAsync{T}"/>.</summary>
    Publish,

    /// <summary>Originated from <see cref="IBus.SendAsync{T}"/>.</summary>
    Send,

    /// <summary>
    /// Reserved for a future enhancement that routes <c>SendRequestAsync</c>,
    /// <c>SendRequestMultiAsync</c>, and <c>PublishRequestAsync</c> through the
    /// send pipeline. Not produced today.
    /// </summary>
    Request,
}
```

- [ ] **Step 2: Create `SendContext.cs`**

```csharp
namespace ServiceConnect.Interfaces;

/// <summary>
/// Carries the data threaded through the outgoing send pipeline so that
/// middleware authors see the strongly-typed <see cref="Message"/> alongside
/// the serialized payload, headers, and routing metadata.
/// </summary>
public sealed class SendContext
{
    /// <summary>The strongly-typed message instance the caller passed.</summary>
    public required Message Message { get; init; }

    /// <summary>The CLR type of <see cref="Message"/>.</summary>
    public required Type MessageType { get; init; }

    /// <summary>The serialized message body, exactly as the producer will send it.</summary>
    public required byte[] MessageBytes { get; init; }

    /// <summary>
    /// The outgoing transport headers. Mutable so middleware can stamp
    /// trace-context, idempotency keys, etc., before the producer sees them.
    /// </summary>
    public required IDictionary<string, string> Headers { get; init; }

    /// <summary>The destination endpoint when applicable; null for publish.</summary>
    public string? EndPoint { get; init; }

    /// <summary>The routing key when applicable; null otherwise.</summary>
    public string? RoutingKey { get; init; }

    /// <summary>The call site that produced this context.</summary>
    public required SendOperation Operation { get; init; }
}
```

- [ ] **Step 3: Build the Interfaces project to confirm both compile**

Run: `dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj`
Expected: Build succeeds with 0 errors. (No tests yet — these types are unused.)

- [ ] **Step 4: Commit (do NOT push)**

```bash
git add src/ServiceConnect.Interfaces/Pipelines/SendOperation.cs src/ServiceConnect.Interfaces/Pipelines/SendContext.cs
git commit -m "feat(interfaces): add SendContext and SendOperation for outgoing pipeline"
```

---

### Task 1.2: Reshape `ISendMessageMiddleware`, `SendMessageDelegate`, and `ISendMessagePipeline`

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Pipelines/SendMessageDelegate.cs`
- Modify: `src/ServiceConnect.Interfaces/Pipelines/ISendMessageMiddleware.cs`
- Modify: `src/ServiceConnect.Interfaces/Pipelines/ISendMessagePipeline.cs`

This is the breaking contract change. After this task the codebase will not compile until Tasks 1.3 and 1.4 also land. **Do not run tests or commit between 1.2 and 1.4** — these three tasks ship together in one commit (1.4's commit step).

- [ ] **Step 1: Replace `SendMessageDelegate.cs` content**

```csharp
namespace ServiceConnect.Interfaces;

/// <summary>
/// Represents the next step in the outgoing send/publish middleware chain.
/// </summary>
/// <param name="context">The send context threaded through the pipeline.</param>
/// <param name="cancellationToken">A token that cancels the operation.</param>
public delegate Task SendMessageDelegate(
    SendContext context,
    CancellationToken cancellationToken);
```

- [ ] **Step 2: Replace `ISendMessageMiddleware.cs` content**

```csharp
namespace ServiceConnect.Interfaces;

/// <summary>
/// Middleware that wraps outgoing send and publish operations. Implementations
/// receive a <see cref="SendContext"/> exposing the strongly-typed message,
/// serialized bytes, headers, and routing metadata.
/// </summary>
public interface ISendMessageMiddleware
{
    /// <summary>
    /// Processes an outgoing message and optionally delegates to the next middleware.
    /// </summary>
    /// <param name="context">The send context.</param>
    /// <param name="next">The next delegate in the chain.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task ProcessAsync(
        SendContext context,
        SendMessageDelegate next,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 3: Replace `ISendMessagePipeline.cs` content**

```csharp
namespace ServiceConnect.Interfaces;

/// <summary>
/// Executes the configured outgoing message pipeline.
/// </summary>
public interface ISendMessagePipeline : IAsyncDisposable
{
    /// <summary>
    /// Executes the publish pipeline for an outgoing message.
    /// </summary>
    /// <param name="context">The send context for the publish.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task ExecutePublishMessagePipelineAsync(
        SendContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the send pipeline for an outgoing message.
    /// </summary>
    /// <param name="context">The send context for the send.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task ExecuteSendMessagePipelineAsync(
        SendContext context,
        CancellationToken cancellationToken = default);
}
```

(No build/test/commit yet — proceed to 1.3.)

---

### Task 1.3: Update `SendMessagePipeline` implementation

**Files:**
- Modify: `src/ServiceConnect/Services/SendMessagePipeline.cs`

- [ ] **Step 1: Replace the file content**

The new chain accepts and threads a single `SendContext` through middleware; the terminal still invokes `IProducer.PublishAsync`/`SendAsync` with the existing tuple shape (the producer interface is unchanged).

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Services;

/// <summary>
/// Default implementation of ISendMessagePipeline that delegates directly to IProducer,
/// optionally wrapping calls in a middleware chain from IPipelineConfiguration.
/// Chains are built once (lazily) and cached rather than rebuilt per message.
/// </summary>
/// <remarks>
/// Because the chain caches middleware instances captured at first use,
/// <see cref="ISendMessageMiddleware"/> implementations MUST be registered as
/// singletons. Scoped or transient registrations will be silently promoted to
/// singleton lifetime, which can cause cross-request state leaks.
/// </remarks>
public sealed class SendMessagePipeline : ISendMessagePipeline
{
    private readonly IProducer _producer;
    private readonly IPipelineConfiguration _pipelineConfig;
    private readonly IServiceProvider _serviceProvider;
    private readonly Lazy<SendMessageDelegate> _publishChain;
    private readonly Lazy<SendMessageDelegate> _sendChain;
    private volatile bool _disposed;

    public SendMessagePipeline(IProducer producer, IPipelineConfiguration pipelineConfig, IServiceProvider serviceProvider)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        _pipelineConfig = pipelineConfig ?? throw new ArgumentNullException(nameof(pipelineConfig));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _publishChain = new Lazy<SendMessageDelegate>(BuildPublishChain, isThreadSafe: true);
        _sendChain = new Lazy<SendMessageDelegate>(BuildSendChain, isThreadSafe: true);
    }

    public Task ExecutePublishMessagePipelineAsync(SendContext context, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        return _publishChain.Value(context, cancellationToken);
    }

    public Task ExecuteSendMessagePipelineAsync(SendContext context, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        return _sendChain.Value(context, cancellationToken);
    }

    private SendMessageDelegate BuildPublishChain()
    {
        var producer = _producer;
        Task terminal(SendContext ctx, CancellationToken ct) =>
            producer.PublishAsync(ctx.MessageType, ctx.MessageBytes, ctx.Headers, ct);
        return WrapMiddleware(terminal);
    }

    private SendMessageDelegate BuildSendChain()
    {
        var producer = _producer;
        Task terminal(SendContext ctx, CancellationToken ct) =>
            !string.IsNullOrEmpty(ctx.EndPoint)
                ? producer.SendAsync(ctx.EndPoint, ctx.MessageType, ctx.MessageBytes, ctx.Headers, ct)
                : producer.SendAsync(ctx.MessageType, ctx.MessageBytes, ctx.Headers, ct);
        return WrapMiddleware(terminal);
    }

    private SendMessageDelegate WrapMiddleware(SendMessageDelegate terminal)
    {
        var middlewareTypes = _pipelineConfig.SendMessageMiddleware;
        if (middlewareTypes.Count == 0)
        {
            return terminal;
        }

        var chain = terminal;
        for (int i = middlewareTypes.Count - 1; i >= 0; i--)
        {
            var mw = (ISendMessageMiddleware)_serviceProvider.GetRequiredService(middlewareTypes[i]);
            var next = chain;
            chain = (ctx, ct) => mw.ProcessAsync(ctx, next, ct);
        }
        return chain;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
```

(No build/test/commit yet — proceed to 1.4.)

---

### Task 1.4: Update `Bus.cs` call sites and migrate test fixtures (atomic commit)

**Files:**
- Modify: `src/ServiceConnect/Bus.cs` — `PublishAsync` and `SendAsync` only
- Modify: `src/ServiceConnect.UnitTests/SendMessagePipelineTests.cs` (the `BlockingSendMiddleware` fixture and the test bodies that drive `ExecutePublish/SendMessagePipelineAsync`)
- Modify: `src/ServiceConnect.UnitTests/ServiceCollectionExtensionsTests.cs` (`TestSendMiddleware` only)
- Modify: `src/ServiceConnect.UnitTests/Services/MiddlewarePipelineTests.cs` (`RecordingSendMiddleware`, `ShortCircuitSendMiddleware` and the test bodies that exercise them)
- Modify: `src/ServiceConnect.EndToEndTests/Filters/MiddlewarePipelineE2ETests.cs` (`HeaderAddingSendMiddleware` and the test bodies that exercise it)

**Important:** Do NOT touch `Bus.SendRequestAsync`/`SendRequestMultiAsync`/`PublishRequestAsync`. They bypass `_sendPipeline` entirely (going through `_requestReplyManager`); they have nothing to migrate. Verify by `grep -n "_sendPipeline\." src/ServiceConnect/Bus.cs` returning hits only inside `PublishAsync` and `SendAsync`.

- [ ] **Step 1: Update `Bus.PublishAsync<T>`**

Replace the trailing pipeline call. Before:

```csharp
        if (options?.RoutingKey is { } routingKey)
        {
            headers[HeaderKeys.RoutingKey] = routingKey;
        }

        await _sendPipeline.ExecutePublishMessagePipelineAsync(typeof(T), messageBytes, headers, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
```

After:

```csharp
        if (options?.RoutingKey is { } routingKey)
        {
            headers[HeaderKeys.RoutingKey] = routingKey;
        }

        var context = new SendContext
        {
            Message = message,
            MessageType = typeof(T),
            MessageBytes = messageBytes,
            Headers = headers,
            EndPoint = null,
            RoutingKey = options?.RoutingKey,
            Operation = SendOperation.Publish,
        };
        await _sendPipeline.ExecutePublishMessagePipelineAsync(context, cancellationToken).ConfigureAwait(false);
    }
```

- [ ] **Step 2: Update `Bus.SendAsync<T>`**

Replace the multi/single-endpoint dispatch tail. Before:

```csharp
        if (options?.EndPoints is { Count: > 0 } endpoints)
        {
            foreach (var endpoint in endpoints)
            {
                await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(T), messageBytes, headers, endpoint, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(T), messageBytes, headers, options?.EndPoint, cancellationToken).ConfigureAwait(false);
        }
    }
```

After:

```csharp
        if (options?.EndPoints is { Count: > 0 } endpoints)
        {
            foreach (var endpoint in endpoints)
            {
                var context = new SendContext
                {
                    Message = message,
                    MessageType = typeof(T),
                    MessageBytes = messageBytes,
                    Headers = headers,
                    EndPoint = endpoint,
                    RoutingKey = null,
                    Operation = SendOperation.Send,
                };
                await _sendPipeline.ExecuteSendMessagePipelineAsync(context, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            var context = new SendContext
            {
                Message = message,
                MessageType = typeof(T),
                MessageBytes = messageBytes,
                Headers = headers,
                EndPoint = options?.EndPoint,
                RoutingKey = null,
                Operation = SendOperation.Send,
            };
            await _sendPipeline.ExecuteSendMessagePipelineAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }
```

Headers built earlier in the method (`BuildHeadersDirect`/`ExtractHeaders`) flow into `Headers` as-is. Multi-endpoint expansion still produces N independent contexts.

- [ ] **Step 3: Port `BlockingSendMiddleware` (SendMessagePipelineTests.cs:151)**

Open the file, find `file sealed class BlockingSendMiddleware : ISendMessageMiddleware` and replace its `ProcessAsync` to the new shape. Pattern (apply to whatever the existing internal state is — TaskCompletionSource etc.):

```csharp
public Task ProcessAsync(SendContext context, SendMessageDelegate next, CancellationToken cancellationToken)
{
    // existing instrumentation: e.g. _started.TrySetResult();
    return _block.Task.ContinueWith(
        _ => next(context, cancellationToken),
        cancellationToken,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default).Unwrap();
}
```

Then update test bodies in the same file that called `ExecutePublishMessagePipelineAsync(typeof(X), bytes, headers, …)` to build a `SendContext` instead — e.g.:

```csharp
var context = new SendContext
{
    Message = new TestMessage(),
    MessageType = typeof(TestMessage),
    MessageBytes = Array.Empty<byte>(),
    Headers = new Dictionary<string, string>(StringComparer.Ordinal),
    Operation = SendOperation.Publish,
};
await pipeline.ExecutePublishMessagePipelineAsync(context, ct);
```

If the test does not have a real `Message` available, define a minimal record at file scope: `file sealed record TestMessage : Message { public TestMessage() : base(Guid.NewGuid()) { } }`.

- [ ] **Step 4: Port `TestSendMiddleware` (ServiceCollectionExtensionsTests.cs:443)**

Replace its `ProcessAsync` signature. The test only verifies registration plumbing — pass-through is fine:

```csharp
public Task ProcessAsync(SendContext context, SendMessageDelegate next, CancellationToken cancellationToken)
{
    Invocations++;
    return next(context, cancellationToken);
}
```

Search the same file for any direct calls to `ExecutePublish/SendMessagePipelineAsync` and migrate them with the same pattern as Step 3.

- [ ] **Step 5: Port `RecordingSendMiddleware` and `ShortCircuitSendMiddleware` (Services/MiddlewarePipelineTests.cs:21,33)**

```csharp
file class RecordingSendMiddleware(List<string> log) : ISendMessageMiddleware
{
    public async Task ProcessAsync(SendContext context, SendMessageDelegate next, CancellationToken cancellationToken)
    {
        log.Add($"send-before:{context.MessageType.Name}");
        await next(context, cancellationToken).ConfigureAwait(false);
        log.Add($"send-after:{context.MessageType.Name}");
    }
}

file class ShortCircuitSendMiddleware : ISendMessageMiddleware
{
    public Task ProcessAsync(SendContext context, SendMessageDelegate next, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
```

Migrate test bodies that drive these middlewares with the `SendContext` pattern from Step 3.

- [ ] **Step 6: Port `HeaderAddingSendMiddleware` (EndToEndTests/Filters/MiddlewarePipelineE2ETests.cs:10)**

```csharp
file sealed class HeaderAddingSendMiddleware : ISendMessageMiddleware
{
    public Task ProcessAsync(SendContext context, SendMessageDelegate next, CancellationToken cancellationToken)
    {
        context.Headers["x-pipeline-test"] = "send";
        return next(context, cancellationToken);
    }
}
```

The E2E test exercises the live `Bus`, so call sites that construct `SendContext` directly are not needed — `Bus` will build them.

- [ ] **Step 7: Build the whole solution**

Run: `dnb` (the project's build helper — see `feedback_dotnet_build_safety.md`). If unavailable, fall back to building only the affected projects:

```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj
dotnet build src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj
dotnet build src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj
```

Expected: 0 errors. If any errors come from a `ISendMessageMiddleware` implementation we missed, port it with the same pattern as Steps 3–6.

- [ ] **Step 8: Run the unit tests**

Run: `dnt src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj` (the project's test helper). If unavailable: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj`.

Expected: All tests pass. Existing tests should still cover the same behavior; the only thing that changed is the contract shape.

- [ ] **Step 9: Commit**

```bash
git add src/ServiceConnect.Interfaces/Pipelines/ISendMessageMiddleware.cs \
        src/ServiceConnect.Interfaces/Pipelines/SendMessageDelegate.cs \
        src/ServiceConnect.Interfaces/Pipelines/ISendMessagePipeline.cs \
        src/ServiceConnect/Services/SendMessagePipeline.cs \
        src/ServiceConnect/Bus.cs \
        src/ServiceConnect.UnitTests/SendMessagePipelineTests.cs \
        src/ServiceConnect.UnitTests/ServiceCollectionExtensionsTests.cs \
        src/ServiceConnect.UnitTests/Services/MiddlewarePipelineTests.cs \
        src/ServiceConnect.EndToEndTests/Filters/MiddlewarePipelineE2ETests.cs
git commit -m "feat!: thread SendContext through the outgoing send pipeline

ISendMessageMiddleware.ProcessAsync now takes (SendContext, next, ct).
The strongly-typed Message survives the pipeline so observability
middleware can build full PublishEventArgs/SendEventArgs without
re-deserializing. Bus.PublishAsync and SendAsync construct the
SendContext at their existing call sites; request/reply continues
to bypass the send pipeline."
```

---

### Task 1.5: Add `SendContext`-threading regression test

**Files:**
- Modify: `src/ServiceConnect.UnitTests/SendMessagePipelineTests.cs`

Belt-and-braces test: the `Bus` must pass the *same* `Message` instance into the pipeline that the caller passed in, with the right `Operation`/`EndPoint`/`RoutingKey`.

- [ ] **Step 1: Add the test**

Append to the test class (use an existing capturing middleware, or add a `file sealed class CapturingSendMiddleware` if needed):

```csharp
[Fact]
public async Task PublishAsync_threads_SendContext_with_publish_metadata()
{
    SendContext? captured = null;
    var middleware = new CapturingSendMiddleware(ctx => captured = ctx);

    await using var pipeline = BuildPipelineWith(middleware);

    var msg = new TestMessage();
    var context = new SendContext
    {
        Message = msg,
        MessageType = typeof(TestMessage),
        MessageBytes = new byte[] { 1, 2, 3 },
        Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["k"] = "v" },
        RoutingKey = "rk",
        Operation = SendOperation.Publish,
    };

    await pipeline.ExecutePublishMessagePipelineAsync(context, CancellationToken.None);

    Assert.Same(msg, captured?.Message);
    Assert.Equal(typeof(TestMessage), captured?.MessageType);
    Assert.Equal("rk", captured?.RoutingKey);
    Assert.Equal(SendOperation.Publish, captured?.Operation);
    Assert.Null(captured?.EndPoint);
}
```

`BuildPipelineWith` is the existing helper used by other tests in the file; if there isn't one, mirror the construction pattern of the test directly above. `CapturingSendMiddleware`:

```csharp
file sealed class CapturingSendMiddleware(Action<SendContext> capture) : ISendMessageMiddleware
{
    public Task ProcessAsync(SendContext context, SendMessageDelegate next, CancellationToken ct)
    {
        capture(context);
        return next(context, ct);
    }
}
```

- [ ] **Step 2: Run the test**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter PublishAsync_threads_SendContext_with_publish_metadata`
Expected: PASS.

- [ ] **Step 3: Add the symmetric `SendAsync` test**

Mirror with `SendOperation.Send`, an `EndPoint`, and `RoutingKey = null`. Calls `ExecuteSendMessagePipelineAsync`.

- [ ] **Step 4: Run both tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~SendContext"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.UnitTests/SendMessagePipelineTests.cs
git commit -m "test: assert SendContext metadata is threaded for publish and send"
```

---

## Phase 2 — Built-in telemetry middleware and `AddTelemetry()`

### Task 2.1: `TelemetrySendMiddleware`

**Files:**
- Create: `src/ServiceConnect.Telemetry/TelemetrySendMiddleware.cs`
- Create: `src/ServiceConnect.UnitTests/Telemetry/TelemetrySendMiddlewareTests.cs`

- [ ] **Step 1: Write the failing test (Publish path)**

Create `src/ServiceConnect.UnitTests/Telemetry/TelemetrySendMiddlewareTests.cs`:

```csharp
using System.Diagnostics;
using ServiceConnect.Interfaces;
using ServiceConnect.Telemetry;

namespace ServiceConnect.UnitTests.Telemetry;

public sealed class TelemetrySendMiddlewareTests : IDisposable
{
    private readonly List<Activity> _activities = new();
    private readonly ActivityListener _listener;

    public TelemetrySendMiddlewareTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name.StartsWith("ServiceConnect.Bus.", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => _activities.Add(a),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task ProcessAsync_publish_creates_publish_activity_and_invokes_next()
    {
        var sut = new TelemetrySendMiddleware(new ServiceConnectInstrumentationOptions());
        var nextCalled = false;
        SendMessageDelegate next = (_, _) => { nextCalled = true; return Task.CompletedTask; };

        var context = new SendContext
        {
            Message = new SampleMessage(),
            MessageType = typeof(SampleMessage),
            MessageBytes = new byte[] { 1 },
            Headers = new Dictionary<string, string>(StringComparer.Ordinal),
            RoutingKey = "rk",
            Operation = SendOperation.Publish,
        };

        await sut.ProcessAsync(context, next, CancellationToken.None);

        Assert.True(nextCalled);
        var span = Assert.Single(_activities);
        Assert.Equal(ServiceConnectActivitySource.PublishActivitySourceName, span.Source.Name);
    }

    private sealed record SampleMessage() : Message(Guid.NewGuid());
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter ProcessAsync_publish_creates_publish_activity_and_invokes_next`
Expected: FAIL — `TelemetrySendMiddleware` does not exist.

- [ ] **Step 3: Implement `TelemetrySendMiddleware`**

Create `src/ServiceConnect.Telemetry/TelemetrySendMiddleware.cs`:

```csharp
using System.Diagnostics;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Telemetry;

/// <summary>
/// Built-in <see cref="ISendMessageMiddleware"/> that emits one
/// publish or send activity per outgoing message via
/// <see cref="ServiceConnectActivitySource"/>.
/// </summary>
internal sealed class TelemetrySendMiddleware(ServiceConnectInstrumentationOptions options) : ISendMessageMiddleware
{
    private readonly ServiceConnectInstrumentationOptions _options = options;

    public async Task ProcessAsync(SendContext context, SendMessageDelegate next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        Activity? activity = context.Operation switch
        {
            SendOperation.Publish => ServiceConnectActivitySource.Publish(new PublishEventArgs
            {
                Message = context.Message,
                Headers = context.Headers,
                RoutingKey = context.RoutingKey ?? string.Empty,
            }),
            SendOperation.Send or SendOperation.Request => ServiceConnectActivitySource.Send(new SendEventArgs
            {
                Message = context.Message,
                Headers = context.Headers,
                EndPoint = context.EndPoint ?? string.Empty,
            }),
            _ => null,
        };

        try
        {
            await next(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ServiceConnectActivitySource.SetError(activity, ex);
            throw;
        }
        finally
        {
            activity?.Dispose();
        }
    }
}
```

Note: `Send` and `Request` both bridge to the Send source for now. The spec leaves room for a future request/reply source.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter ProcessAsync_publish_creates_publish_activity_and_invokes_next`
Expected: PASS.

- [ ] **Step 5: Add the Send-path test**

Add to `TelemetrySendMiddlewareTests.cs`:

```csharp
[Fact]
public async Task ProcessAsync_send_creates_send_activity()
{
    var sut = new TelemetrySendMiddleware(new ServiceConnectInstrumentationOptions());
    SendMessageDelegate next = (_, _) => Task.CompletedTask;

    var context = new SendContext
    {
        Message = new SampleMessage(),
        MessageType = typeof(SampleMessage),
        MessageBytes = Array.Empty<byte>(),
        Headers = new Dictionary<string, string>(StringComparer.Ordinal),
        EndPoint = "queue.target",
        Operation = SendOperation.Send,
    };

    await sut.ProcessAsync(context, next, CancellationToken.None);

    var span = Assert.Single(_activities);
    Assert.Equal(ServiceConnectActivitySource.SendActivitySourceName, span.Source.Name);
}
```

- [ ] **Step 6: Add the exception path test**

```csharp
[Fact]
public async Task ProcessAsync_records_exception_and_rethrows()
{
    var sut = new TelemetrySendMiddleware(new ServiceConnectInstrumentationOptions());
    var boom = new InvalidOperationException("boom");
    SendMessageDelegate next = (_, _) => throw boom;

    var context = new SendContext
    {
        Message = new SampleMessage(),
        MessageType = typeof(SampleMessage),
        MessageBytes = Array.Empty<byte>(),
        Headers = new Dictionary<string, string>(StringComparer.Ordinal),
        Operation = SendOperation.Publish,
    };

    var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
        () => sut.ProcessAsync(context, next, CancellationToken.None));
    Assert.Same(boom, thrown);

    var span = Assert.Single(_activities);
    Assert.Equal(ActivityStatusCode.Error, span.Status);
}
```

- [ ] **Step 7: Run all three tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~TelemetrySendMiddlewareTests"`
Expected: 3 PASS.

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect.Telemetry/TelemetrySendMiddleware.cs src/ServiceConnect.UnitTests/Telemetry/TelemetrySendMiddlewareTests.cs
git commit -m "feat(telemetry): add TelemetrySendMiddleware"
```

---

### Task 2.2: `TelemetryProcessingMiddleware`

**Files:**
- Create: `src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs`
- Create: `src/ServiceConnect.UnitTests/Telemetry/TelemetryProcessingMiddlewareTests.cs`

- [ ] **Step 1: Write the failing test (success path)**

```csharp
using System.Diagnostics;
using ServiceConnect.Interfaces;
using ServiceConnect.Telemetry;

namespace ServiceConnect.UnitTests.Telemetry;

public sealed class TelemetryProcessingMiddlewareTests : IDisposable
{
    private readonly List<Activity> _activities = new();
    private readonly ActivityListener _listener;

    public TelemetryProcessingMiddlewareTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == ServiceConnectActivitySource.ConsumeActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => _activities.Add(a),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task ProcessAsync_creates_consume_activity_on_success()
    {
        var sut = new TelemetryProcessingMiddleware();
        var headers = new Dictionary<string, object>(StringComparer.Ordinal);
        var envelope = new Envelope(new byte[] { 1 }, headers);
        MessageProcessingDelegate next = (_, _, _, _, _, _) =>
            Task.FromResult(new ConsumeEventResult { Success = true });

        await sut.ProcessAsync(
            new ReadOnlyMemory<byte>(new byte[] { 1 }),
            typeof(SampleMessage),
            new SampleMessage(),
            headers,
            envelope,
            next,
            CancellationToken.None);

        Assert.Single(_activities);
    }

    private sealed record SampleMessage() : Message(Guid.NewGuid());
}
```

(Adapt `Envelope` constructor to whatever the production type accepts — read [Envelope.cs](../../src/ServiceConnect.Interfaces/Messages/Envelope.cs) before writing the test.)

- [ ] **Step 2: Run the test to verify it fails**

Expected: FAIL — `TelemetryProcessingMiddleware` does not exist.

- [ ] **Step 3: Implement `TelemetryProcessingMiddleware`**

```csharp
using System.Diagnostics;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Messages;

namespace ServiceConnect.Telemetry;

/// <summary>
/// Built-in <see cref="IMessageProcessingMiddleware"/> that emits one consume
/// activity per inbound message via <see cref="ServiceConnectActivitySource"/>.
/// </summary>
internal sealed class TelemetryProcessingMiddleware : IMessageProcessingMiddleware
{
    public async Task<ConsumeEventResult> ProcessAsync(
        ReadOnlyMemory<byte> messageBytes,
        Type messageType,
        object message,
        IDictionary<string, object> headers,
        Envelope envelope,
        MessageProcessingDelegate next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        var args = new ConsumeEventArgs
        {
            Message = envelope.Body.ToArray(),
            Type = messageType.FullName ?? string.Empty,
            Headers = headers,
        };

        Activity? activity = ServiceConnectActivitySource.Consume(args);

        try
        {
            var result = await next(messageBytes, messageType, message, headers, envelope, cancellationToken).ConfigureAwait(false);
            if (!result.Success && result.Exception is not null)
            {
                ServiceConnectActivitySource.SetError(activity, result.Exception);
            }
            return result;
        }
        catch (Exception ex)
        {
            ServiceConnectActivitySource.SetError(activity, ex);
            throw;
        }
        finally
        {
            activity?.Dispose();
        }
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Expected: PASS.

- [ ] **Step 5: Add failure-path tests (result.Success=false with exception, thrown exception)**

Mirror the same pattern. Both should produce one activity with `ActivityStatusCode.Error`.

- [ ] **Step 6: Run all tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~TelemetryProcessingMiddlewareTests"`
Expected: All PASS.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs src/ServiceConnect.UnitTests/Telemetry/TelemetryProcessingMiddlewareTests.cs
git commit -m "feat(telemetry): add TelemetryProcessingMiddleware"
```

---

### Task 2.3: `builder.AddTelemetry()` extension

**Files:**
- Create: `src/ServiceConnect.Telemetry/TelemetryBuilderExtensions.cs`
- Create: `src/ServiceConnect.UnitTests/Telemetry/TelemetryBuilderExtensionsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Configuration;
using ServiceConnect.Telemetry;

namespace ServiceConnect.UnitTests.Telemetry;

public sealed class TelemetryBuilderExtensionsTests
{
    [Fact]
    public void AddTelemetry_registers_middleware_at_position_zero_in_both_pipelines()
    {
        var services = new ServiceCollection();
        var pipeline = new PipelineConfiguration();
        // Pre-existing middleware to verify telemetry is inserted *before* it.
        pipeline.SendMessageMiddleware.Add(typeof(DummySend));
        pipeline.MessageProcessingMiddleware.Add(typeof(DummyProcess));

        var builder = TestBuilders.ServiceConnectBuilder(services, pipeline);
        builder.AddTelemetry();

        Assert.Equal(typeof(TelemetrySendMiddleware), pipeline.SendMessageMiddleware[0]);
        Assert.Equal(typeof(TelemetryProcessingMiddleware), pipeline.MessageProcessingMiddleware[0]);

        // Singleton lifetime is required — see SendMessagePipeline remarks.
        var provider = services.BuildServiceProvider();
        Assert.Same(provider.GetRequiredService<TelemetrySendMiddleware>(),
                    provider.GetRequiredService<TelemetrySendMiddleware>());
        Assert.Same(provider.GetRequiredService<TelemetryProcessingMiddleware>(),
                    provider.GetRequiredService<TelemetryProcessingMiddleware>());
    }

    private sealed class DummySend : ISendMessageMiddleware
    {
        public Task ProcessAsync(SendContext context, SendMessageDelegate next, CancellationToken ct) => next(context, ct);
    }

    private sealed class DummyProcess : IMessageProcessingMiddleware
    {
        public Task<ConsumeEventResult> ProcessAsync(ReadOnlyMemory<byte> b, Type t, object m, IDictionary<string, object> h, Envelope e, MessageProcessingDelegate next, CancellationToken ct)
            => next(b, t, m, h, e, ct);
    }
}
```

`TestBuilders.ServiceConnectBuilder(services, pipeline)` is a small helper. If one already exists in the test project (search `class TestBuilders` or `static.*ServiceConnectBuilder Create`), reuse it. Otherwise add:

```csharp
internal static class TestBuilders
{
    public static ServiceConnectBuilder ServiceConnectBuilder(IServiceCollection services, PipelineConfiguration pipeline)
    {
        // Mirror the production wiring used by AddServiceConnect: register the
        // PipelineConfiguration as the IPipelineConfiguration, then build the
        // builder via the public ctor or whatever the production code uses.
        // Inspect src/ServiceConnect/ServiceCollectionExtensions.cs for the
        // canonical flow and replicate the minimum.
    }
}
```

(Read [ServiceCollectionExtensions.cs](../../src/ServiceConnect/ServiceCollectionExtensions.cs) before writing the helper — the goal is the smallest setup that makes `AddRegistration` and `ConfigurePipeline` observably work.)

- [ ] **Step 2: Run the test to verify it fails**

Expected: FAIL — `AddTelemetry` does not exist.

- [ ] **Step 3: Implement `TelemetryBuilderExtensions`**

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Telemetry;

/// <summary>
/// Wires the built-in <see cref="TelemetrySendMiddleware"/> and
/// <see cref="TelemetryProcessingMiddleware"/> into a <see cref="ServiceConnectBuilder"/>.
/// </summary>
public static class TelemetryBuilderExtensions
{
    /// <summary>
    /// Registers the built-in telemetry middleware as the outermost
    /// middleware on both the send and processing pipelines, and configures
    /// <see cref="ServiceConnectActivitySource"/> with the supplied options.
    /// </summary>
    public static ServiceConnectBuilder AddTelemetry(
        this ServiceConnectBuilder builder,
        Action<ServiceConnectInstrumentationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new ServiceConnectInstrumentationOptions();
        configure?.Invoke(options);

        builder.AddRegistration(services =>
        {
            services.AddSingleton(options);
            services.AddSingleton<TelemetrySendMiddleware>();
            services.AddSingleton<TelemetryProcessingMiddleware>();
        });

        builder.ConfigurePipeline(p =>
        {
            p.SendMessageMiddleware.Insert(0, typeof(TelemetrySendMiddleware));
            p.MessageProcessingMiddleware.Insert(0, typeof(TelemetryProcessingMiddleware));
        });

        ServiceConnectActivitySource.Options = options;

        return builder;
    }
}
```

(`ServiceConnectActivitySource.Options` is in the same assembly — internal setter is reachable directly.)

- [ ] **Step 4: Run the test to verify it passes**

Expected: PASS.

- [ ] **Step 5: Add a configure-callback test**

```csharp
[Fact]
public void AddTelemetry_invokes_configure_callback_on_options()
{
    var services = new ServiceCollection();
    var pipeline = new PipelineConfiguration();
    var builder = TestBuilders.ServiceConnectBuilder(services, pipeline);

    builder.AddTelemetry(opts => opts.EnablePublishTelemetry = false);

    var provider = services.BuildServiceProvider();
    var resolved = provider.GetRequiredService<ServiceConnectInstrumentationOptions>();
    Assert.False(resolved.EnablePublishTelemetry);
}
```

- [ ] **Step 6: Run both tests**

Expected: 2 PASS.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Telemetry/TelemetryBuilderExtensions.cs src/ServiceConnect.UnitTests/Telemetry/TelemetryBuilderExtensionsTests.cs
git commit -m "feat(telemetry): add builder.AddTelemetry() extension"
```

---

### Task 2.4: End-to-end telemetry test

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/Telemetry/TelemetryE2ETests.cs`

This is the framework-level analog of the sample's `run.sh` assertion: spin up an in-process bus with `AddTelemetry()`, register an `ActivityListener`, publish/consume one message, assert trace correlation.

- [ ] **Step 1: Write the failing test**

Read [`MiddlewarePipelineE2ETests.cs`](../../src/ServiceConnect.EndToEndTests/Filters/MiddlewarePipelineE2ETests.cs) first to understand the fixture conventions (RabbitMQ Testcontainer, `BusFixture`, etc.). Mirror that fixture; add:

```csharp
[Fact]
public async Task Publish_then_consume_correlates_trace_ids_via_AddTelemetry()
{
    var publishSpans = new List<Activity>();
    var consumeSpans = new List<Activity>();

    using var listener = new ActivityListener
    {
        ShouldListenTo = src => src.Name.StartsWith("ServiceConnect.Bus.", StringComparison.Ordinal),
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        ActivityStopped = a =>
        {
            if (a.Source.Name == ServiceConnectActivitySource.PublishActivitySourceName) publishSpans.Add(a);
            else if (a.Source.Name == ServiceConnectActivitySource.ConsumeActivitySourceName) consumeSpans.Add(a);
        },
    };
    ActivitySource.AddActivityListener(listener);

    await using var fixture = await TelemetryBusFixture.CreateAsync(
        configureBuilder: builder => builder.AddTelemetry());

    var consumed = new TaskCompletionSource();
    fixture.OnConsumed = () => consumed.TrySetResult();

    await fixture.Bus.PublishAsync(new TraceTestMessage());
    await consumed.Task.WaitAsync(TimeSpan.FromSeconds(10));

    var publishSpan = Assert.Single(publishSpans);
    var consumeSpan = Assert.Single(consumeSpans);
    Assert.Equal(publishSpan.TraceId, consumeSpan.TraceId);
    Assert.Equal(publishSpan.SpanId, consumeSpan.ParentSpanId);
}
```

(`TelemetryBusFixture` is a small helper inside this test file that mirrors the existing E2E fixture pattern.)

- [ ] **Step 2: Run the test to verify it fails meaningfully**

Run: `dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter Publish_then_consume_correlates_trace_ids_via_AddTelemetry`
Expected: FAIL — most likely on `consumeSpan.ParentSpanId` not matching, since traceparent injection is the thing under test.

- [ ] **Step 3: Verify the test passes**

If it passes already, the in-place `ServiceConnectActivitySource.Publish/Consume` already does W3C injection. If it fails, this is the place to add a trace-context propagator (the existing `ServiceConnectActivitySource` likely already handles this — investigate `Publish` and `Consume` implementations before adding propagation code; the spec's intent is that the existing helpers are correct and only the wiring is new).

Expected after green: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/Telemetry/TelemetryE2ETests.cs
git commit -m "test(e2e): assert AddTelemetry correlates publish and consume trace IDs"
```

---

## Phase 3 — `examples/Telemetry/` sample

### Task 3.1: Contracts project

**Files:**
- Create: `examples/Telemetry/src/ServiceConnect.Examples.Telemetry.Contracts/OrderPlaced.cs`
- Create: `examples/Telemetry/src/ServiceConnect.Examples.Telemetry.Contracts/ServiceConnect.Examples.Telemetry.Contracts.csproj`

- [ ] **Step 1: Read [`PublishSubscribe.Contracts/OrderPlaced.cs`](../../examples/PublishSubscribe/src/ServiceConnect.Examples.PublishSubscribe.Contracts/OrderPlaced.cs) and `ServiceConnect.Examples.PublishSubscribe.Contracts.csproj`**

Mirror the layout exactly. The csproj references `ServiceConnect.Interfaces`.

- [ ] **Step 2: Create `OrderPlaced.cs`** (mirror the existing `PublishSubscribe.Contracts/OrderPlaced.cs` class+primary-ctor shape)

```csharp
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.Telemetry.Contracts;

public sealed class OrderPlaced(Guid correlationId) : Message(correlationId)
{
    public string OrderId { get; init; } = string.Empty;
    public decimal Total { get; init; }
}
```

- [ ] **Step 3: Create the csproj**

Mirror the PublishSubscribe.Contracts csproj verbatim, swapping the AssemblyName/RootNamespace.

- [ ] **Step 4: Build**

Run: `dotnet build examples/Telemetry/src/ServiceConnect.Examples.Telemetry.Contracts/ServiceConnect.Examples.Telemetry.Contracts.csproj`
Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add examples/Telemetry/src/ServiceConnect.Examples.Telemetry.Contracts/
git commit -m "feat(examples/telemetry): add Contracts project with OrderPlaced"
```

---

### Task 3.2: Publisher project

**Files:**
- Create: `examples/Telemetry/src/ServiceConnect.Examples.Telemetry.Publisher/Program.cs`
- Create: `examples/Telemetry/src/ServiceConnect.Examples.Telemetry.Publisher/TelemetryConsoleListener.cs`
- Create: `examples/Telemetry/src/ServiceConnect.Examples.Telemetry.Publisher/ServiceConnect.Examples.Telemetry.Publisher.csproj`

- [ ] **Step 1: Read [`PublishSubscribe.Publisher/Program.cs`](../../examples/PublishSubscribe/src/ServiceConnect.Examples.PublishSubscribe.Publisher/Program.cs) and the csproj**

Match its shape: `READY:`/`SUCCESS:` lines, settings loader, dependency waiter, `AddExampleBus`.

- [ ] **Step 2: Create `TelemetryConsoleListener.cs`**

```csharp
using System.Diagnostics;

namespace ServiceConnect.Examples.Telemetry.Publisher;

internal static class TelemetryConsoleListener
{
    public static void Register(string endpoint)
    {
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = src => src.Name.StartsWith("ServiceConnect.Bus.", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => Console.WriteLine(
                $"TRACE:{endpoint}:{a.OperationName}:{a.TraceId}:{a.SpanId}:{a.ParentSpanId}"),
        });
    }
}
```

- [ ] **Step 3: Create `Program.cs`** (matches the namespace splits in `PublishSubscribe.Publisher/Program.cs`; `AddExampleBus` already exposes a `configureBuilder` parameter at [ExampleBusFactory.cs:19](../../examples/ExampleSupport/Bootstrap/ExampleBusFactory.cs#L19))

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Examples.Telemetry.Contracts;
using ServiceConnect.Examples.Telemetry.Publisher;
using ServiceConnect.Interfaces;
using ServiceConnect.Telemetry;

TelemetryConsoleListener.Register("telemetry-publisher");

var settings = ExampleSettingsLoader.Load();
await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var services = new ServiceCollection();
services.AddExampleBus(settings, "telemetry-publisher",
    configureBuilder: builder => builder.AddTelemetry());

// To export to a real OTel pipeline, replace the listener registration above with:
// services.AddOpenTelemetry().WithTracing(t => t
//     .AddSource(ServiceConnectActivitySource.PublishActivitySourceName)
//     .AddSource(ServiceConnectActivitySource.SendActivitySourceName)
//     .AddSource(ServiceConnectActivitySource.ConsumeActivitySourceName)
//     .AddConsoleExporter());

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();

ConsoleStatus.Ready("telemetry-publisher");

await bus.PublishAsync(new OrderPlaced(Guid.NewGuid())
{
    OrderId = Guid.NewGuid().ToString(),
    Total = 42.50m,
});
ConsoleStatus.Success("telemetry-publisher");
```

(If the publisher must linger so the broker can deliver before the process exits, mirror PublishSubscribe.Publisher's tail exactly — read it before finalising.)

- [ ] **Step 4: Create the csproj**

Mirror PublishSubscribe.Publisher.csproj. Add `<ProjectReference Include="../ServiceConnect.Examples.Telemetry.Contracts/...csproj" />`, `<ProjectReference>` to `ServiceConnect.Telemetry`, and `<ProjectReference>` to `ServiceConnect.Examples.Support`.

- [ ] **Step 5: Build**

Run: `dotnet build examples/Telemetry/src/ServiceConnect.Examples.Telemetry.Publisher/ServiceConnect.Examples.Telemetry.Publisher.csproj`
Expected: 0 errors.

- [ ] **Step 6: Commit**

```bash
git add examples/Telemetry/src/ServiceConnect.Examples.Telemetry.Publisher/
git commit -m "feat(examples/telemetry): add Publisher project with ActivityListener"
```

---

### Task 3.3: BillingSubscriber project

**Files:**
- Create: `examples/Telemetry/src/ServiceConnect.Examples.Telemetry.BillingSubscriber/{Program.cs, OrderPlacedHandler.cs, TelemetryConsoleListener.cs}`
- Create: csproj

- [ ] **Step 1: Read [`PublishSubscribe.BillingSubscriber`](../../examples/PublishSubscribe/src/ServiceConnect.Examples.PublishSubscribe.BillingSubscriber/) for the shape**

- [ ] **Step 2: Create `OrderPlacedHandler.cs`** (the existing `IMessageHandler<T>` shape — `Context` is a property and the entrypoint is `HandleAsync`; mirror `PublishSubscribe.BillingSubscriber/OrderPlacedHandler.cs` exactly)

```csharp
using ServiceConnect.Examples.Telemetry.Contracts;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.Telemetry.BillingSubscriber;

public sealed class OrderPlacedHandler : IMessageHandler<OrderPlaced>
{
    public IConsumeContext Context { get; set; } = null!;

    public Task HandleAsync(OrderPlaced message, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"BILLING:received:{message.OrderId}");
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 3: Create `TelemetryConsoleListener.cs`**

Identical to the Publisher's, just with the file's namespace adjusted to `BillingSubscriber`.

- [ ] **Step 4: Create `Program.cs`**

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Examples.Telemetry.BillingSubscriber;
using ServiceConnect.Examples.Telemetry.Contracts;
using ServiceConnect.Interfaces;
using ServiceConnect.Telemetry;

TelemetryConsoleListener.Register("billing-subscriber");

var settings = ExampleSettingsLoader.Load();
await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var handlerReferences = new List<HandlerReference>
{
    new() { HandlerType = typeof(OrderPlacedHandler), MessageType = typeof(OrderPlaced) }
};

var services = new ServiceCollection();
services.AddSingleton<IList<HandlerReference>>(handlerReferences);
services.AddTransient<IMessageHandler<OrderPlaced>, OrderPlacedHandler>();
services.AddExampleBus(settings, "billing-subscriber",
    configureBuilder: builder => builder.AddTelemetry());

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
ConsoleStatus.Ready("billing-subscriber");
await Task.Delay(Timeout.InfiniteTimeSpan);
```

- [ ] **Step 5: Create the csproj**

Mirror PublishSubscribe.BillingSubscriber.csproj. Add references to Contracts, Telemetry, Support.

- [ ] **Step 6: Build**

Expected: 0 errors.

- [ ] **Step 7: Commit**

```bash
git add examples/Telemetry/src/ServiceConnect.Examples.Telemetry.BillingSubscriber/
git commit -m "feat(examples/telemetry): add BillingSubscriber project"
```

---

### Task 3.4: AnalyticsSubscriber project

**Files:**
- Create: `examples/Telemetry/src/ServiceConnect.Examples.Telemetry.AnalyticsSubscriber/{Program.cs, OrderPlacedHandler.cs, TelemetryConsoleListener.cs}` + csproj

- [ ] **Step 1: Mirror BillingSubscriber exactly, swapping `billing` → `analytics` and the handler's `BILLING:received:` → `ANALYTICS:received:`**

- [ ] **Step 2: Build**

Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add examples/Telemetry/src/ServiceConnect.Examples.Telemetry.AnalyticsSubscriber/
git commit -m "feat(examples/telemetry): add AnalyticsSubscriber project"
```

---

### Task 3.5: Solution file and run scripts

**Files:**
- Create: `examples/Telemetry/Telemetry.sln`
- Create: `examples/Telemetry/run.sh`
- Create: `examples/Telemetry/run.ps1`

- [ ] **Step 1: Generate the solution**

Mirror the existing PublishSubscribe.sln structure: PublishSubscribe.sln does NOT include `ServiceConnect.Examples.Support.csproj` directly — each project just adds it via `<ProjectReference>` in its csproj. Match that.

```bash
cd examples/Telemetry
dotnet new sln -n Telemetry
dotnet sln Telemetry.sln add \
    src/ServiceConnect.Examples.Telemetry.Contracts/ServiceConnect.Examples.Telemetry.Contracts.csproj \
    src/ServiceConnect.Examples.Telemetry.Publisher/ServiceConnect.Examples.Telemetry.Publisher.csproj \
    src/ServiceConnect.Examples.Telemetry.BillingSubscriber/ServiceConnect.Examples.Telemetry.BillingSubscriber.csproj \
    src/ServiceConnect.Examples.Telemetry.AnalyticsSubscriber/ServiceConnect.Examples.Telemetry.AnalyticsSubscriber.csproj
cd ../..
```

- [ ] **Step 2: Read [`examples/PublishSubscribe/run.sh`](../../examples/PublishSubscribe/run.sh)**

Note the structure: starts each subscriber, waits for `READY:`, starts publisher, waits for `SUCCESS:`, kills children, exits 0.

- [ ] **Step 3: Create `examples/Telemetry/run.sh`**

Start with a literal copy of PublishSubscribe/run.sh. Then append the trace-correlation assertions before the cleanup/exit:

```bash
# Trace-correlation assertions ----------------------------------------
PUB_LINE=$(grep -E '^TRACE:telemetry-publisher:[a-z.]+:' "$LOG" | head -n1)
BILL_LINE=$(grep -E '^TRACE:billing-subscriber:[a-z.]+:' "$LOG" | head -n1)
ANALYTICS_LINE=$(grep -E '^TRACE:analytics-subscriber:[a-z.]+:' "$LOG" | head -n1)

if [ -z "$PUB_LINE" ] || [ -z "$BILL_LINE" ] || [ -z "$ANALYTICS_LINE" ]; then
    echo "FAIL: missing TRACE: line for one or more processes" >&2
    exit 1
fi

# TRACE:<endpoint>:<op>:<trace>:<span>:<parent>
PUB_TRACE=$(echo "$PUB_LINE" | awk -F: '{print $4}')
PUB_SPAN=$(echo "$PUB_LINE" | awk -F: '{print $5}')
BILL_TRACE=$(echo "$BILL_LINE" | awk -F: '{print $4}')
BILL_PARENT=$(echo "$BILL_LINE" | awk -F: '{print $6}')
ANALYTICS_TRACE=$(echo "$ANALYTICS_LINE" | awk -F: '{print $4}')
ANALYTICS_PARENT=$(echo "$ANALYTICS_LINE" | awk -F: '{print $6}')

if [ "$PUB_TRACE" != "$BILL_TRACE" ] || [ "$PUB_TRACE" != "$ANALYTICS_TRACE" ]; then
    echo "FAIL: TraceId mismatch (pub=$PUB_TRACE bill=$BILL_TRACE analytics=$ANALYTICS_TRACE)" >&2
    exit 1
fi
if [ "$BILL_PARENT" != "$PUB_SPAN" ] || [ "$ANALYTICS_PARENT" != "$PUB_SPAN" ]; then
    echo "FAIL: ParentSpanId mismatch (pub=$PUB_SPAN bill=$BILL_PARENT analytics=$ANALYTICS_PARENT)" >&2
    exit 1
fi
echo "OK: trace-id correlated across publisher and both subscribers"
```

(`$LOG` is whatever variable PublishSubscribe/run.sh uses for its output log; use the exact same name for consistency.)

- [ ] **Step 4: Create `run.ps1`**

Mirror `examples/PublishSubscribe/run.ps1`, then append the same assertions in PowerShell idiom.

- [ ] **Step 5: Run the sample**

```bash
cd examples/Telemetry
chmod +x run.sh
./run.sh
```

Expected: clean exit (0), `OK: trace-id correlated...` near the end, `output.log` contains TRACE: lines from all three processes with matching TraceIds. If RabbitMQ isn't running, run via the existing harness (e.g. `make run-examples` or whatever the repo uses; check `examples/README.md`).

- [ ] **Step 6: Commit**

```bash
git add examples/Telemetry/Telemetry.sln examples/Telemetry/run.sh examples/Telemetry/run.ps1
git commit -m "feat(examples/telemetry): add solution and run scripts with trace assertions"
```

---

### Task 3.6: README

**Files:**
- Create: `examples/Telemetry/README.md`

- [ ] **Step 1: Read [`examples/PublishSubscribe/README.md`](../../examples/PublishSubscribe/README.md) for shape**

Sections: Overview / Participants / Sequence diagram / Prerequisites / Run This Example / Run Manually / Expected Output / What To Notice.

- [ ] **Step 2: Write `README.md`**

Content (paraphrase, follow the existing prose voice):

- **Overview** — explains the sample demonstrates W3C trace propagation across the broker.
- **Participants** — Publisher, BillingSubscriber, AnalyticsSubscriber, Contracts.
- **Mermaid sequence diagram** — Publisher publishes → both subscribers consume, all on one Trace.
- **Prerequisites** — RabbitMQ on `amqp://localhost:5672` (or via the repo's docker-compose).
- **Run This Example** — `./run.sh`.
- **Run Manually** — three terminals.
- **Expected Output** — TRACE: lines and `OK:` correlation message.
- **What To Notice** — explicitly call out the single TraceId and parent-span linkage. Point at `observability.mdx` for the conceptual story.

- [ ] **Step 3: Commit**

```bash
git add examples/Telemetry/README.md
git commit -m "docs(examples/telemetry): add README with sample walkthrough"
```

---

## Phase 4 — Website updates

### Task 4.1: `samples.mdx` entry

**Files:**
- Modify: `website/src/content/docs/samples.mdx`

- [ ] **Step 1: Add the Telemetry entry alongside Filters / MessageDeduplication**

Insert (in the section ordering that matches the page's existing alphabetisation):

```markdown
### Telemetry

End-to-end OpenTelemetry tracing across publish → consume, demonstrating
W3C trace-context propagation through the broker. Three processes share
one TraceId; each subscriber's span is a direct child of the publisher's.

- Tracing reference: [Observability — Tracing](/ServiceConnect-CSharp/learn/operations/observability/#tracing-opentelemetry)
- Source: [`examples/Telemetry`](https://github.com/R-Suite/ServiceConnect-CSharp/tree/master/examples/Telemetry)
```

- [ ] **Step 2: Build the website to confirm no broken-link errors**

Run: `cd website && npm run build`
Expected: build succeeds. (The link to `examples/Telemetry/` on GitHub won't be checked because it's external; the local anchor `#tracing-opentelemetry` is.)

- [ ] **Step 3: Commit**

```bash
git add website/src/content/docs/samples.mdx
git commit -m "docs(website): add Telemetry sample entry to samples index"
```

---

### Task 4.2: Rewrite `observability.mdx` Tracing section

**Files:**
- Modify: `website/src/content/docs/learn/operations/observability.mdx`

- [ ] **Step 1: Read lines 60–110 to confirm the current Tracing section**

The existing section (lines 74–106 per the spec) describes wiring "via a filter or custom outgoing hook". This is exactly the gap we're closing.

- [ ] **Step 2: Replace the Tracing section**

The section must contain:

1. One-paragraph intro of the three sources (`ServiceConnect.Bus.Publish/Send/Consume`) and the OTel messaging tags.
2. **Wiring** subsection with a single fenced code block:

   ```csharp
   services.AddServiceConnect(builder =>
   {
       builder.UseRabbitMQ(/* ... */);
       builder.AddTelemetry(opts => { /* optional enrichment */ });
   });

   services.AddOpenTelemetry()
       .WithTracing(tracing => tracing
           .AddSource(ServiceConnectActivitySource.PublishActivitySourceName)
           .AddSource(ServiceConnectActivitySource.SendActivitySourceName)
           .AddSource(ServiceConnectActivitySource.ConsumeActivitySourceName)
           .AddOtlpExporter());
   ```

3. **Enrichment** subsection — show `EnrichWithMessage`, preserve the existing security-warning callout verbatim.
4. **Disabling specific sources** — `opts.EnablePublishTelemetry = false` etc.
5. **Propagation** paragraph — traceparent injected on outgoing, extracted on incoming, link to `examples/Telemetry/` sample.

Sections "No health checks" and "Putting it together" below this section are unchanged.

- [ ] **Step 3: Build the website**

Run: `cd website && npm run build`
Expected: build succeeds, no broken anchors.

- [ ] **Step 4: Commit**

```bash
git add website/src/content/docs/learn/operations/observability.mdx
git commit -m "docs(website): rewrite Tracing section around builder.AddTelemetry()"
```

---

### Task 4.3: Update `event-args.mdx` usage example

**Files:**
- Modify: `website/src/content/docs/reference/handlers/event-args.mdx`

- [ ] **Step 1: Read lines 146–212 (the current usage section)**

- [ ] **Step 2: Replace the "Usage" section**

Structure:

- **Recommended: use the `ServiceConnect.Telemetry` package** — show the one-line `builder.AddTelemetry()`, link to `observability.mdx`.
- **If you need a custom middleware** — keep the existing `OpenTelemetryConsumeMiddleware` example for the consume side, and add a parallel send-side example using the new `SendContext` shape:

  ```csharp
  public sealed class OpenTelemetrySendMiddleware : ISendMessageMiddleware
  {
      private static readonly ActivitySource Source = new("ServiceConnect");

      public async Task ProcessAsync(
          SendContext context,
          SendMessageDelegate next,
          CancellationToken cancellationToken)
      {
          using var activity = Source.StartActivity(
              $"publish {context.MessageType.Name}",
              ActivityKind.Producer);

          activity?.SetTag("servicebus.message.type", context.MessageType.FullName);
          activity?.SetTag("servicebus.message.size", context.MessageBytes.Length);
          if (context.RoutingKey is not null)
          {
              activity?.SetTag("servicebus.routing_key", context.RoutingKey);
          }
          if (context.EndPoint is not null)
          {
              activity?.SetTag("servicebus.endpoint", context.EndPoint);
          }

          try
          {
              await next(context, cancellationToken);
          }
          catch (Exception ex)
          {
              activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
              throw;
          }
      }
  }
  ```

- [ ] **Step 3: Build the website**

Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add website/src/content/docs/reference/handlers/event-args.mdx
git commit -m "docs(website): update event-args example to AddTelemetry + SendContext"
```

---

### Task 4.4: Sync signature pages to the new contract

**Files:**
- Modify: `website/src/content/docs/reference/filters/isendmessagemiddleware.mdx`
- Modify: `website/src/content/docs/reference/configuration/ipipelineconfiguration.mdx` (only if it shows the signature)
- Modify: `website/src/content/docs/learn/messaging-patterns/filters.mdx` (only if it shows the signature)

- [ ] **Step 1: Open `isendmessagemiddleware.mdx`**

Find every code block that shows `Task ProcessAsync(Type, byte[], IDictionary<string,string>, string?, …)` and rewrite to `Task ProcessAsync(SendContext, SendMessageDelegate, CancellationToken)`. Update prose around the parameters to describe `SendContext` properties instead. Add a one-paragraph "Why SendContext?" note pointing at the rich data it exposes (Message, RoutingKey, Operation).

- [ ] **Step 2: Spot-check `ipipelineconfiguration.mdx` and `filters.mdx`**

Search for `ProcessAsync(Type` and `byte[] messageBytes` in each file. If the old signature appears, rewrite the same way.

- [ ] **Step 3: Build the website**

Run: `cd website && npm run build`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add website/src/content/docs/reference/filters/isendmessagemiddleware.mdx \
        website/src/content/docs/reference/configuration/ipipelineconfiguration.mdx \
        website/src/content/docs/learn/messaging-patterns/filters.mdx
git commit -m "docs(website): sync ISendMessageMiddleware signature to SendContext"
```

(If the latter two files don't reference the signature, omit them from the `git add`.)

---

## Phase 5 — Final verification

### Task 5.1: Whole-solution sanity sweep

- [ ] **Step 1: Build everything**

Run: `dnb` (or piecewise per `feedback_dotnet_build_safety.md`).
Expected: 0 errors, 0 new warnings.

- [ ] **Step 2: Run all unit tests**

Run: `dnt src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj`
Expected: All PASS.

- [ ] **Step 3: Run end-to-end tests (requires Testcontainers / docker)**

Run: `dnt src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj`
Expected: All PASS, including the new `Publish_then_consume_correlates_trace_ids_via_AddTelemetry`.

- [ ] **Step 4: Run the sample end-to-end**

```bash
cd examples/Telemetry && ./run.sh && cd ../..
```

Expected: exit 0, `OK: trace-id correlated...` printed.

- [ ] **Step 5: Build the website**

Run: `cd website && npm run build && cd ..`
Expected: build succeeds.

- [ ] **Step 6: Final review pass**

Skim the full diff (`git diff master...HEAD --stat`) and look for:
- Files touched outside the file-structure overview at the top of this plan (would indicate scope creep).
- Any leftover use of the old `ISendMessageMiddleware.ProcessAsync(Type, byte[], …)` signature anywhere.
- Any new `using` directives that look unused.

If clean, the work is ready for review.
