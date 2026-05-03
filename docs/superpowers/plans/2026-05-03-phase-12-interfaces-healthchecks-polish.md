# Phase 12 — Interfaces + HealthChecks + remaining polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Final cleanup pass for v8. 13 verified-real items: 2 Mediums (M29 JSON escape, M30 aggregator default), 2 Lows (L21 cancellation tokens, L23 producer lazy-connect), and 9 smaller interface / healthcheck items. Three v8 breaking changes: `HandleAsync` takes `IConsumeContext`; `Message.CorrelationId` is `init`; `TimeoutData.Headers` and `ConsumeEventArgs.Headers` become `IReadOnlyDictionary`.

**Architecture:** Three threads: (A) public-API breaking changes ship FIRST so subsequent tasks build on the new shape; (B) internal correctness fixes; (C) additive multi-bus health-check API. No new abstractions. All work in existing files.

**Tech Stack:** .NET multi-target net8.0/net10.0, LangVersion=14, xUnit + Moq, Astro/Starlight for docs.

**Spec:** [`docs/superpowers/specs/2026-05-03-phase-12-interfaces-healthchecks-polish-design.md`](../specs/2026-05-03-phase-12-interfaces-healthchecks-polish-design.md).

---

## Build/test safety

`~/.local/bin/dotnet` re-execs every `dotnet` invocation under a systemd cgroup (8 cores / 8 GiB / 200 tasks). Always per-csproj with `-m:1`. Test filter for this phase:

```
--filter "FullyQualifiedName~HealthCheck|RequestOptions|HeaderDecoder|Aggregator|RequestTimeoutException|MessageHandler|ProcessHandler|StreamHandler|TimeoutData|ConsumeEventArgs|RequestTimeout"
```

---

## File structure

### Modified — production code

- `src/ServiceConnect.Interfaces/Handlers/IMessageHandler.cs` — Task 3 (signature).
- `src/ServiceConnect.Interfaces/ProcessManagers/IProcessHandler.cs` — Task 3 (signature).
- `src/ServiceConnect.Interfaces/Handlers/IStreamHandler.cs` — Task 3 (signature).
- `src/ServiceConnect/Services/Processors/HandlerProcessor.cs` — Task 3 (drop SetContext, pass context to invocation).
- `src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs` — Task 3 (same).
- `src/ServiceConnect/Services/Processors/StreamProcessor.cs` — Task 3 (drop SetStream pattern; pass stream to ExecuteAsync? Verify).
- `src/ServiceConnect/Services/Descriptors/*HandlerDescriptor.cs` — Task 3 (drop SetContext, update InvokeHandleAsync).
- `src/ServiceConnect.Interfaces/Messages/Message.cs:16` — Task 4 (`init`).
- `src/ServiceConnect.Interfaces/Timeouts/TimeoutData.cs:31` — Task 5 (IReadOnlyDictionary).
- `src/ServiceConnect.Interfaces/Bus/ConsumeEventArgs.cs:21` — Task 5 (IReadOnlyDictionary).
- `src/ServiceConnect.Interfaces/Aggregation/Aggregator.cs:15-18` — Task 6 (sentinel).
- `src/ServiceConnect/Services/Processors/AggregatorProcessor.cs` — Task 6 (dispatcher gates on `> Zero`).
- `src/ServiceConnect.Interfaces/Headers/HeaderDecoder.cs:55-101` — Task 7 (escape) + Task 8 (depth).
- `src/ServiceConnect.HealthChecks/BusConsumingHealthCheck.cs:24-35` — Task 9.
- `src/ServiceConnect.HealthChecks/ConsumerConnectionHealthCheck.cs` — Task 9.
- `src/ServiceConnect.HealthChecks/ProducerConnectionHealthCheck.cs:29-40` — Task 9 + Task 10.
- `src/ServiceConnect.Interfaces/Bus/IProducer.cs` — Task 10 (add HasAttemptedConnection).
- `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs` + `ProducerConnection.cs` — Task 10 (impl).
- `src/ServiceConnect.Interfaces/Bus/IBus.cs:92-93` — Task 11 (Task.FromException).
- `src/ServiceConnect.Interfaces/Exceptions/RequestTimeoutException.cs:9` — Task 12 (invariant).
- `src/ServiceConnect.HealthChecks/HealthChecksBuilderExtensions.cs` — Task 13 (LazyInitializer) + Task 14 (factory + keyed overloads).

### Created — tests

- `src/ServiceConnect.UnitTests/Handlers/HandlerSignatureTests.cs` — Task 3.
- `src/ServiceConnect.UnitTests/Messages/MessageInitTests.cs` — Task 4.
- `src/ServiceConnect.UnitTests/Timeouts/TimeoutDataReadOnlyHeadersTests.cs` — Task 5.
- `src/ServiceConnect.UnitTests/Bus/ConsumeEventArgsReadOnlyHeadersTests.cs` — Task 5.
- `src/ServiceConnect.UnitTests/Aggregation/AggregatorTimeoutSentinelTests.cs` — Task 6.
- `src/ServiceConnect.UnitTests/Headers/HeaderDecoderEscapeTests.cs` — Task 7.
- `src/ServiceConnect.UnitTests/Headers/HeaderDecoderDepthTests.cs` — Task 8.
- `src/ServiceConnect.UnitTests/HealthChecks/HealthCheckCancellationTests.cs` — Task 9.
- `src/ServiceConnect.UnitTests/HealthChecks/ProducerLazyConnectTests.cs` — Task 10.
- `src/ServiceConnect.UnitTests/Bus/RequestTimeoutAsyncDimTests.cs` — Task 11.
- `src/ServiceConnect.UnitTests/Exceptions/RequestTimeoutExceptionCultureTests.cs` — Task 12.
- `src/ServiceConnect.UnitTests/HealthChecks/HealthCheckRegistrationCachingTests.cs` — Task 13.
- `src/ServiceConnect.UnitTests/HealthChecks/MultiBusHealthCheckTests.cs` — Task 14.

### Modified — website

- `website/src/content/docs/releases.mdx` — Task 15.
- `website/src/content/docs/reference/handlers/...` — Task 16.
- `website/src/content/docs/reference/messages/...` — Task 16.
- `website/src/content/docs/reference/configuration/...` — Task 16.
- `website/src/content/docs/reference/healthchecks/...` — Task 16.

### Modified — examples

- `examples/Aggregator/README.md` — Task 17.
- `examples/Filters/README.md` — Task 17.

---

## Task 1: Spec

**Already shipped at commit `d7b0bc3f`** (`docs(spec): phase 12 interfaces + healthchecks + remaining polish`). Skip.

---

## Task 2: This plan

```bash
git add docs/superpowers/plans/2026-05-03-phase-12-interfaces-healthchecks-polish.md
git commit -m "$(cat <<'EOF'
docs(plan): phase 12 implementation plan

18 tasks. Three v8 breaking changes: IMessageHandler/IProcessHandler/
IStreamHandler.HandleAsync signature; Message.CorrelationId init;
TimeoutData.Headers + ConsumeEventArgs.Headers become IReadOnlyDictionary.
Plus contract clarifications, internal correctness fixes, and additive
multi-bus health-check API.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: `HandleAsync(message, context, ct)` signature change

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Handlers/IMessageHandler.cs`.
- Modify: `src/ServiceConnect.Interfaces/ProcessManagers/IProcessHandler.cs`.
- Modify: `src/ServiceConnect.Interfaces/Handlers/IStreamHandler.cs`.
- Modify: `src/ServiceConnect/Services/Processors/HandlerProcessor.cs` (drop SetContext, pass context).
- Modify: `src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs` (drop SetContext, pass context).
- Modify: `src/ServiceConnect/Services/Processors/StreamProcessor.cs` (verify pattern — may use SetStream).
- Modify: `src/ServiceConnect/Services/Descriptors/*HandlerDescriptor.cs` (3 files).
- Migrate every handler implementation in `src/ServiceConnect.UnitTests/` and `examples/`.
- Create: `src/ServiceConnect.UnitTests/Handlers/HandlerSignatureTests.cs`.

**Background.** The handler interfaces expose `Context { get; set; }` for the framework to assign before `HandleAsync` runs. If a user registers their handler as a DI singleton, two concurrent dispatches both write `Context` — handlers see the wrong one. Fix: pass `context` as a parameter; remove the property.

This is the largest cascade in the phase. Build is the canonical "did I miss anything" check.

- [ ] **Step 1: Read every handler interface + descriptor + processor**

```bash
cat src/ServiceConnect.Interfaces/Handlers/IMessageHandler.cs
cat src/ServiceConnect.Interfaces/ProcessManagers/IProcessHandler.cs
cat src/ServiceConnect.Interfaces/Handlers/IStreamHandler.cs
grep -rn "SetContext\|SetStream\|InvokeHandleAsync\|InvokeExecuteAsync" src/ServiceConnect/Services/ --include="*.cs" | head -20
```

Note the existing patterns. For `IStreamHandler`, the "stream" property is `IMessageBusReadStream Stream { get; set; }` — a different concern from `IConsumeContext`. The plan handles them slightly differently:

- `IMessageHandler<T>` and `IProcessHandler<TData, TMessage>`: drop `Context` property; `HandleAsync(message, context, ct)`.
- `IStreamHandler<T>`: drop `Stream` property; `ExecuteAsync(message, stream, ct)` — same shape, different parameter name. (The existing method is named `ExecuteAsync`, not `HandleAsync`, per `IStreamHandler.cs:18`.)

- [ ] **Step 2: Write the failing test**

Create `src/ServiceConnect.UnitTests/Handlers/HandlerSignatureTests.cs`:

```csharp
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Handlers;

public class HandlerSignatureTests
{
    public sealed class TestMessage : Message
    {
        public TestMessage() : base(Guid.NewGuid()) { }
    }

    public sealed class TestHandler : IMessageHandler<TestMessage>
    {
        public IConsumeContext? CapturedContext { get; private set; }
        public TestMessage? CapturedMessage { get; private set; }

        public Task HandleAsync(TestMessage message, IConsumeContext context, CancellationToken cancellationToken = default)
        {
            CapturedMessage = message;
            CapturedContext = context;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task HandleAsync_ReceivesContextAsParameter()
    {
        var handler = new TestHandler();
        var message = new TestMessage();
        var context = Moq.Mock.Of<IConsumeContext>();

        await handler.HandleAsync(message, context, CancellationToken.None);

        Assert.Same(message, handler.CapturedMessage);
        Assert.Same(context, handler.CapturedContext);
    }

    [Fact]
    public void IMessageHandler_DoesNotExposeContextProperty()
    {
        // Compile-time guard: IMessageHandler<T> must NOT have a Context property.
        // The property's removal is the headline of the v8 handler change.
        var props = typeof(IMessageHandler<TestMessage>).GetProperties();
        Assert.DoesNotContain(props, p => p.Name == "Context");
    }
}
```

- [ ] **Step 3: Run test pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~HandlerSignatureTests" -m:1
```

Expected: compilation fails (`TestHandler.HandleAsync` has the new 3-parameter signature; the interface still has the 2-parameter signature).

- [ ] **Step 4: Update `IMessageHandler<T>`**

In `src/ServiceConnect.Interfaces/Handlers/IMessageHandler.cs`, replace the body with:

```csharp
using System.Threading;

namespace ServiceConnect.Interfaces;

/// <summary>
/// Implemented by classes that handle a specific message type. Multiple implementations
/// of <see cref="IMessageHandler{TMessage}"/> for the same message type may be registered
/// and will all be invoked in turn.
/// </summary>
/// <typeparam name="TMessage">The message contract handled by this implementation.</typeparam>
public interface IMessageHandler<in TMessage> where TMessage : Message
{
    /// <summary>
    /// Invoked with the deserialized message and the per-message consume context
    /// (bus handle, correlation id, reply helper). The <paramref name="cancellationToken"/>
    /// is sourced from the transport consume context and signals cooperative shutdown.
    /// </summary>
    /// <remarks>
    /// v8: <c>Context</c> moved from a property to this parameter. Pre-v8 the framework
    /// assigned <c>handler.Context</c> before calling <c>HandleAsync(message, ct)</c>; that
    /// shape was unsafe for singleton-registered handlers (concurrent dispatches both
    /// wrote the property). Migration: append <c>IConsumeContext context</c> to the method
    /// signature and replace <c>this.Context</c> reads with <c>context</c>.
    /// </remarks>
    Task HandleAsync(TMessage message, IConsumeContext context, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 5: Update `IProcessHandler<TData, TMessage>`**

In `src/ServiceConnect.Interfaces/ProcessManagers/IProcessHandler.cs`, replace the body with:

```csharp
using System.Threading;

namespace ServiceConnect.Interfaces;

/// <summary>
/// Process-manager handler: correlates incoming messages of type
/// <typeparamref name="TMessage"/> to a persisted <typeparamref name="TData"/>
/// instance keyed on <see cref="IProcessManagerData.CorrelationId"/>.
/// </summary>
/// <typeparam name="TData">Persisted state carried across messages in the saga.</typeparam>
/// <typeparam name="TMessage">Message contract routed into this handler.</typeparam>
public interface IProcessHandler<TData, TMessage>
    where TData : class, IProcessManagerData, new()
    where TMessage : Message
{
    /// <summary>
    /// Invoked with the deserialized message, the correlated persisted state, and the
    /// per-message consume context. Mutations to <paramref name="data"/> are persisted
    /// when the method returns. The <paramref name="cancellationToken"/> is sourced
    /// from the transport consume context and signals cooperative shutdown.
    /// </summary>
    /// <remarks>v8: same migration as <see cref="IMessageHandler{TMessage}.HandleAsync"/>.</remarks>
    Task HandleAsync(TMessage message, TData data, IConsumeContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Configures the correlation mapping between <typeparamref name="TMessage"/> and
    /// <typeparamref name="TData"/>. The default implementation maps on CorrelationId.
    /// </summary>
    void ConfigureMapper(IProcessManagerPropertyMapper mapper)
    {
        mapper.ConfigureMapping<TData, TMessage>(d => d.CorrelationId, m => m.CorrelationId);
    }
}
```

- [ ] **Step 6: Update `IStreamHandler<T>`**

In `src/ServiceConnect.Interfaces/Handlers/IStreamHandler.cs`, replace the body with:

```csharp
namespace ServiceConnect.Interfaces;

/// <summary>
/// Handler for byte-stream messages: large payloads are delivered as a sequence of
/// packets reassembled into <see cref="IMessageBusReadStream"/>, and
/// <see cref="ExecuteAsync"/> is called once the complete stream has arrived.
/// </summary>
/// <typeparam name="TMessage">Message contract associated with the stream.</typeparam>
public interface IStreamHandler<TMessage> where TMessage : Message
{
    /// <summary>
    /// Invoked once the full stream has been received and reassembled. Reads the
    /// assembled payload bytes from <paramref name="stream"/>.
    /// </summary>
    /// <remarks>v8: <c>Stream</c> moved from a property to this parameter (analogous to
    /// the <c>Context</c> change on <see cref="IMessageHandler{TMessage}"/>).</remarks>
    Task ExecuteAsync(TMessage message, IMessageBusReadStream stream, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 7: Update the descriptors**

```bash
rtk proxy find /home/tim/source/ServiceConnect-CSharp/src/ServiceConnect/Services -name "*HandlerDescriptor.cs"
```

There are 3 descriptors. Read each:

```bash
grep -n "SetContext\|SetStream\|InvokeHandleAsync\|InvokeExecuteAsync" \
  src/ServiceConnect/Services/Descriptors/MessageHandlerDescriptor.cs \
  src/ServiceConnect/Services/Descriptors/ProcessManagerHandlerDescriptor.cs \
  src/ServiceConnect/Services/Descriptors/StreamHandlerDescriptor.cs 2>/dev/null
```

For each descriptor:
1. Drop the `SetContext` / `SetStream` delegate (and its compiled-expression machinery).
2. Update `InvokeHandleAsync` / `InvokeExecuteAsync` to take the new parameter (`IConsumeContext` for handler/process, `IMessageBusReadStream` for stream) and pass it through to the compiled invocation delegate.

Read the existing compiled-expression building (`BuildInvokeHandleAsync` or similar) and update the signature: the compiled lambda now takes `(handler, message, context, ct)` for messages and `(handler, message, data, context, ct)` for process managers, or `(handler, message, stream, ct)` for streams.

The exact code shape depends on the existing implementation. Use `Expression.Parameter`, `Expression.Convert`, `Expression.Call` to build the new shape. Mirror what the existing code does, just with one additional parameter.

- [ ] **Step 8: Update `HandlerProcessor`**

In `src/ServiceConnect/Services/Processors/HandlerProcessor.cs` (around lines 80-100 — the dispatch loop), drop the `descriptor.SetContext(handler, context)` line and pass `context` to the invocation:

```csharp
// before
descriptor.SetContext(handler, context);
await descriptor.InvokeHandleAsync(handler, message, cancellationToken).ConfigureAwait(false);

// after
await descriptor.InvokeHandleAsync(handler, message, context, cancellationToken).ConfigureAwait(false);
```

Read the actual code first; the line numbers and the variable `context` may differ.

- [ ] **Step 9: Update `ProcessManagerProcessor`**

Similar change — drop SetHandlerContext-style call, pass context to invocation. Line ~115 in `ProcessManagerProcessor.cs`.

- [ ] **Step 10: Update `StreamProcessor`**

`StreamProcessor` calls `descriptor.SetStream(handler, state.Stream)` at line ~251. Drop this; pass the stream to `InvokeExecuteAsync`:

```csharp
// before
descriptor.SetStream(handler, state.Stream);
// ... InvokeHandlerAsync(descriptor, handler, originalMessage!, sequenceId, cancellationToken);

// after — the InvokeHandlerAsync helper itself takes the stream as a parameter
return InvokeHandlerAsync(descriptor, handler, originalMessage!, state.Stream, sequenceId, cancellationToken);
```

Then in the private `InvokeHandlerAsync` helper, accept `IMessageBusReadStream stream` and pass it to `descriptor.InvokeExecuteAsync(handler, originalMessage, stream, cancellationToken)`.

- [ ] **Step 11: Run the build to find unmigrated handlers**

```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1 2>&1 | grep -E "error CS" | head -30
```

This will list every test handler / example handler that doesn't compile against the new signatures. Expect ~30+ errors. Each error is a handler missing the new parameter or still using `Context = ...` / `this.Context`.

- [ ] **Step 12: Migrate the test handlers**

For each error, open the file and apply the mechanical migration:
- For `IMessageHandler<T>`: append `IConsumeContext context` parameter; remove `Context = null!` field/property; replace `this.Context` reads with `context`.
- For `IProcessHandler<TData, TMessage>`: same; the parameter goes between `data` and `cancellationToken`.
- For `IStreamHandler<T>`: append `IMessageBusReadStream stream` parameter; remove `Stream = null!`; replace `this.Stream` reads with `stream`.

Repeat the build until clean. Test handlers are typically small (1-5 lines per handler); migration is fast.

- [ ] **Step 13: Migrate example handlers**

```bash
grep -rn "IMessageHandler<\|IProcessHandler<\|IStreamHandler<" examples/ --include="*.cs"
```

Apply the same mechanical migration to each.

- [ ] **Step 14: Run focused tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~HandlerSignatureTests|MessageHandler|ProcessHandler|StreamHandler|HandlerProcessor|ProcessManagerProcessor|StreamProcessor" -m:1
```

Expected: 0 failures. The handler-signature tests pass; pre-existing handler-pipeline tests pass.

- [ ] **Step 15: Commit**

```bash
git add src/ServiceConnect.Interfaces/Handlers/IMessageHandler.cs \
        src/ServiceConnect.Interfaces/ProcessManagers/IProcessHandler.cs \
        src/ServiceConnect.Interfaces/Handlers/IStreamHandler.cs \
        src/ServiceConnect/Services/Processors/HandlerProcessor.cs \
        src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs \
        src/ServiceConnect/Services/Processors/StreamProcessor.cs \
        src/ServiceConnect/Services/Descriptors/ \
        src/ServiceConnect.UnitTests/ \
        examples/
git commit -m "$(cat <<'EOF'
feat(handlers)!: HandleAsync takes IConsumeContext parameter

Pre-v8 IMessageHandler/IProcessHandler exposed Context as a property the
framework assigned before each HandleAsync call. Two concurrent dispatches
on a singleton-registered handler raced on the property. Move Context to
a parameter; remove the property. Same shape for IStreamHandler.Stream.

Migration: append IConsumeContext context (or IMessageBusReadStream stream
for IStreamHandler) to the method signature; replace this.Context reads
with context.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

(`!` for breaking API change.)

---

## Task 4: `Message.CorrelationId` `init`

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Messages/Message.cs:16`.
- Create: `src/ServiceConnect.UnitTests/Messages/MessageInitTests.cs`.

- [ ] **Step 1: Verify no in-tree post-construction writes**

```bash
grep -rn "\.CorrelationId\s*=" src/ServiceConnect/ src/ServiceConnect.Client.RabbitMQ/ src/ServiceConnect.Persistence.MongoDb/ --include="*.cs" | head
```

The Bus / dispatch pipeline assigns `CorrelationId` only in object initializers (`new Message { CorrelationId = ... }`) — those compile against `init`. Post-construction writes (`existingMessage.CorrelationId = newId`) would break and need to be hoisted to ctor / object-initializer. If any are found, list them in your report and migrate.

- [ ] **Step 2: Write the failing test**

Create `src/ServiceConnect.UnitTests/Messages/MessageInitTests.cs`:

```csharp
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Messages;

public class MessageInitTests
{
    [Fact]
    public void CorrelationId_HasInitAccessor_NotPrivateSet()
    {
        // Compile-time guard: the setter must be init-only.
        var prop = typeof(Message).GetProperty(nameof(Message.CorrelationId));
        Assert.NotNull(prop);
        var setter = prop!.SetMethod!;
        Assert.True(setter.ReturnParameter.GetRequiredCustomModifiers()
            .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit"),
            "Message.CorrelationId setter must be init-only (System.Runtime.CompilerServices.IsExternalInit modreq).");
    }

    [Fact]
    public void Message_ConstructionAssignsCorrelationId()
    {
        var id = Guid.NewGuid();
        var msg = new Message(id);
        Assert.Equal(id, msg.CorrelationId);
    }
}
```

- [ ] **Step 3: Run pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageInitTests" -m:1
```

Expected: `CorrelationId_HasInitAccessor_NotPrivateSet` fails — the setter is `private set`, no `IsExternalInit` modreq.

- [ ] **Step 4: Apply the fix**

In `src/ServiceConnect.Interfaces/Messages/Message.cs:16`, change:

```csharp
public Guid CorrelationId { get; private set; } = correlationId;
```

to:

```csharp
public Guid CorrelationId { get; init; } = correlationId;
```

- [ ] **Step 5: Run tests + commit**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageInitTests" -m:1
git add src/ServiceConnect.Interfaces/Messages/Message.cs \
        src/ServiceConnect.UnitTests/Messages/MessageInitTests.cs
git commit -m "$(cat <<'EOF'
feat(messages)!: Message.CorrelationId is init-only

Pre-v8 the setter was private set, allowing in-class post-construction
writes. Make it init so the assignment is genuinely once-only. The Bus
already assigns via constructor / object initializer in every site.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

(`!` — accessor change is a binary-breaking change for any caller assembly that compiled against the private set shape.)

---

## Task 5: `TimeoutData.Headers` + `ConsumeEventArgs.Headers` → `IReadOnlyDictionary`

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Timeouts/TimeoutData.cs:31`.
- Modify: `src/ServiceConnect.Interfaces/Bus/ConsumeEventArgs.cs:21`.
- Audit and migrate any in-tree code that mutates either `Headers` post-construction.
- Create: `src/ServiceConnect.UnitTests/Timeouts/TimeoutDataReadOnlyHeadersTests.cs`.
- Create: `src/ServiceConnect.UnitTests/Bus/ConsumeEventArgsReadOnlyHeadersTests.cs`.

- [ ] **Step 1: Audit existing mutations**

```bash
grep -rn "TimeoutData.*Headers\.\(Add\|Remove\|Clear\)\|TimeoutData.*Headers\[" src/ --include="*.cs"
grep -rn "ConsumeEventArgs.*Headers\.\(Add\|Remove\|Clear\)\|ConsumeEventArgs.*Headers\[" src/ --include="*.cs"
```

If matches exist, the mutation site needs to construct a new dictionary and assign via init. Migrate inline.

- [ ] **Step 2: Write the failing tests**

Create `src/ServiceConnect.UnitTests/Timeouts/TimeoutDataReadOnlyHeadersTests.cs`:

```csharp
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Timeouts;

public class TimeoutDataReadOnlyHeadersTests
{
    [Fact]
    public void Headers_PropertyType_IsReadOnlyDictionary()
    {
        var prop = typeof(TimeoutData).GetProperty(nameof(TimeoutData.Headers));
        Assert.NotNull(prop);
        Assert.Equal(typeof(IReadOnlyDictionary<string, object>), prop!.PropertyType);
    }

    [Fact]
    public void Headers_ConstructsAndReadsBack()
    {
        var data = new TimeoutData
        {
            Headers = new Dictionary<string, object> { ["k"] = "v" }
        };
        Assert.Equal("v", data.Headers["k"]);
    }
}
```

Create `src/ServiceConnect.UnitTests/Bus/ConsumeEventArgsReadOnlyHeadersTests.cs`:

```csharp
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Bus;

public class ConsumeEventArgsReadOnlyHeadersTests
{
    [Fact]
    public void Headers_PropertyType_IsReadOnlyDictionary()
    {
        var prop = typeof(ConsumeEventArgs).GetProperty(nameof(ConsumeEventArgs.Headers));
        Assert.NotNull(prop);
        Assert.Equal(typeof(IReadOnlyDictionary<string, object>), prop!.PropertyType);
    }

    [Fact]
    public void Headers_ConstructsAndReadsBack()
    {
        var args = new ConsumeEventArgs
        {
            Headers = new Dictionary<string, object> { ["k"] = "v" }
        };
        Assert.Equal("v", args.Headers["k"]);
    }
}
```

- [ ] **Step 3: Run pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~TimeoutDataReadOnlyHeadersTests|ConsumeEventArgsReadOnlyHeadersTests" -m:1
```

Expected: the `_PropertyType_` assertions fail — the type is `IDictionary` not `IReadOnlyDictionary`.

- [ ] **Step 4: Apply the fix to `TimeoutData`**

In `src/ServiceConnect.Interfaces/Timeouts/TimeoutData.cs:31`, change:

```csharp
public IDictionary<string, object> Headers { get; set; } = new Dictionary<string, object>(StringComparer.Ordinal);
```

to:

```csharp
public IReadOnlyDictionary<string, object> Headers { get; init; } = new Dictionary<string, object>(StringComparer.Ordinal);
```

- [ ] **Step 5: Apply the fix to `ConsumeEventArgs`**

In `src/ServiceConnect.Interfaces/Bus/ConsumeEventArgs.cs:21`, change:

```csharp
public IDictionary<string, object> Headers { get; init; } = new Dictionary<string, object>(StringComparer.Ordinal);
```

to:

```csharp
public IReadOnlyDictionary<string, object> Headers { get; init; } = new Dictionary<string, object>(StringComparer.Ordinal);
```

(The `set; → init;` change was already done in some prior phase; verify by reading the current source. If the current declaration uses `init`, only the type changes.)

- [ ] **Step 6: Build to find downstream readers that mutate**

```bash
dotnet build src/ServiceConnect.csproj 2>&1 | grep -E "error CS" | head
```

Any compilation error pointing at `Headers.Add(...)` / `Headers[k] = v` is a downstream mutation site. Hoist the mutation into the constructor (build the dictionary first, then assign).

- [ ] **Step 7: Run focused tests + commit**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~TimeoutData|ConsumeEventArgs" -m:1
git add src/ServiceConnect.Interfaces/Timeouts/TimeoutData.cs \
        src/ServiceConnect.Interfaces/Bus/ConsumeEventArgs.cs \
        src/ServiceConnect.UnitTests/Timeouts/ \
        src/ServiceConnect.UnitTests/Bus/ \
        src/ # any downstream callers migrated
git commit -m "$(cat <<'EOF'
feat(interfaces)!: TimeoutData.Headers + ConsumeEventArgs.Headers are IReadOnlyDictionary

Header mutability post-construction was a silent-corruption vector — a
middleware that added a tracing header mutated the producer-side state.
Tighten to IReadOnlyDictionary<string,object> with init accessor;
producers construct via init, consumers read only.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

(`!` for breaking API change.)

---

## Task 6: M30 — `Aggregator<T>.Timeout()` default → `Timeout.InfiniteTimeSpan`

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Aggregation/Aggregator.cs:15-18`.
- Audit / verify dispatcher gating in `src/ServiceConnect/Services/Processors/AggregatorProcessor.cs`.
- Create: `src/ServiceConnect.UnitTests/Aggregation/AggregatorTimeoutSentinelTests.cs`.

- [ ] **Step 1: Read the current dispatcher gating**

```bash
grep -n "Timeout()\|TimeSpan\.Zero\|InfiniteTimeSpan" src/ServiceConnect/Services/Processors/AggregatorProcessor.cs | head
```

The dispatcher likely calls `aggregator.Timeout()` and gates scheduling on the result. Verify the gate is `> TimeSpan.Zero` (which excludes both `Zero` and the new `InfiniteTimeSpan` sentinel).

- [ ] **Step 2: Write the failing test**

Create `src/ServiceConnect.UnitTests/Aggregation/AggregatorTimeoutSentinelTests.cs`:

```csharp
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Aggregation;

public class AggregatorTimeoutSentinelTests
{
    public sealed class TestAggregator : Aggregator<Message>
    {
        public override Task ExecuteAsync(IList<Message> messages, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    [Fact]
    public void Timeout_DefaultBaseImplementation_ReturnsInfiniteTimeSpan()
    {
        var agg = new TestAggregator();
        Assert.Equal(Timeout.InfiniteTimeSpan, agg.Timeout());
    }
}
```

- [ ] **Step 3: Run pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~AggregatorTimeoutSentinelTests" -m:1
```

Expected: fail — current default returns `default` (= `TimeSpan.Zero`), not `Timeout.InfiniteTimeSpan`.

- [ ] **Step 4: Apply the fix**

In `src/ServiceConnect.Interfaces/Aggregation/Aggregator.cs:15-18`, change:

```csharp
public virtual TimeSpan Timeout()
{
    return default;
}
```

to:

```csharp
public virtual TimeSpan Timeout()
{
    // Timeout.InfiniteTimeSpan (-1ms) is the BCL convention for "no timeout".
    // Pre-v8 this returned default (= TimeSpan.Zero), which the dispatcher could
    // mistake for "fire immediately and once" rather than "disabled".
    return System.Threading.Timeout.InfiniteTimeSpan;
}
```

Update the XML doc:

```csharp
/// <returns>
/// The maximum aggregation window. Returning <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
/// (the default) disables the timeout-based flush.
/// </returns>
```

- [ ] **Step 5: Verify dispatcher gates on `> TimeSpan.Zero`**

```bash
grep -B2 -A2 "Timeout()" src/ServiceConnect/Services/Processors/AggregatorProcessor.cs
```

If the dispatcher checks `> TimeSpan.Zero`, no change needed — `InfiniteTimeSpan` (`-1ms`) is `< Zero`, so it's correctly excluded. If it checks `>= TimeSpan.Zero` or `!= TimeSpan.Zero`, change to `> TimeSpan.Zero`.

- [ ] **Step 6: Run tests + commit**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Aggregator" -m:1
git add src/ServiceConnect.Interfaces/Aggregation/Aggregator.cs \
        src/ServiceConnect/Services/Processors/AggregatorProcessor.cs \
        src/ServiceConnect.UnitTests/Aggregation/AggregatorTimeoutSentinelTests.cs
git commit -m "$(cat <<'EOF'
fix(aggregator): Timeout() default sentinel is InfiniteTimeSpan (M30)

Pre-fix the default returned default(TimeSpan) = Zero, which is ambiguous
(also a legal Timer.Change(Zero) "fire immediately and once" call).
Timeout.InfiniteTimeSpan is the BCL convention for "no timeout" and is
unambiguously < Zero, so the dispatcher's > Zero gate excludes it.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

(No `!` — source-compatible with all existing overrides that return positive durations.)

---

## Task 7: M29 — `HeaderDecoder.Render` complete JSON escaping

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Headers/HeaderDecoder.cs:55-101`.
- Create: `src/ServiceConnect.UnitTests/Headers/HeaderDecoderEscapeTests.cs`.

- [ ] **Step 1: Write the failing test**

Create `src/ServiceConnect.UnitTests/Headers/HeaderDecoderEscapeTests.cs`:

```csharp
using System.Text.Json.Nodes;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Headers;

public class HeaderDecoderEscapeTests
{
    [Theory]
    [InlineData("\\", "\\\\")]
    [InlineData("\n", "\\n")]
    [InlineData("\r", "\\r")]
    [InlineData("\t", "\\t")]
    [InlineData("\"", "\\\"")]
    [InlineData("\b", "\\b")]
    [InlineData("\f", "\\f")]
    [InlineData("\x01", "\\u0001")]
    public void Decode_StringWithSpecialChars_RoundTripsThroughJson(string input, string escaped)
    {
        var dict = new Dictionary<string, object> { ["k"] = input };
        var rendered = HeaderDecoder.Decode(dict);

        Assert.NotNull(rendered);
        // Rendered must be valid JSON.
        var parsed = JsonNode.Parse(rendered!);
        Assert.NotNull(parsed);
        Assert.Equal(input, parsed!["k"]!.GetValue<string>());
    }
}
```

- [ ] **Step 2: Run pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~HeaderDecoderEscapeTests" -m:1
```

Expected: all rows except `"\""` fail — `\`, `\n`, `\r`, `\t`, control chars are emitted raw, breaking `JsonNode.Parse`.

- [ ] **Step 3: Apply the fix**

In `src/ServiceConnect.Interfaces/Headers/HeaderDecoder.cs`, add a private `EscapeJsonString` helper at the bottom of the class:

```csharp
private static string EscapeJsonString(string s)
{
    var sb = new StringBuilder(s.Length + 2);
    foreach (var c in s)
    {
        switch (c)
        {
            case '\\': sb.Append("\\\\"); break;
            case '"':  sb.Append("\\\""); break;
            case '\b': sb.Append("\\b"); break;
            case '\f': sb.Append("\\f"); break;
            case '\n': sb.Append("\\n"); break;
            case '\r': sb.Append("\\r"); break;
            case '\t': sb.Append("\\t"); break;
            default:
                if (c < 0x20)
                {
                    sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append(c);
                }
                break;
        }
    }
    return sb.ToString();
}
```

Replace the inline `Replace("\"", "\\\"")` calls:

In `Render` (line 60-61):
```csharp
// before
byte[] bytes => "\"" + Encoding.UTF8.GetString(bytes).Replace("\"", "\\\"") + "\"",
string s => "\"" + s.Replace("\"", "\\\"") + "\"",

// after
byte[] bytes => "\"" + EscapeJsonString(Encoding.UTF8.GetString(bytes)) + "\"",
string s => "\"" + EscapeJsonString(s) + "\"",
```

In `RenderDictionary` (line 81):
```csharp
// before
sb.Append('"').Append(kv.Key.Replace("\"", "\\\"")).Append("\":").Append(Render(kv.Value));

// after
sb.Append('"').Append(EscapeJsonString(kv.Key)).Append("\":").Append(Render(kv.Value));
```

In `RenderNonGenericDictionary` (line 99):
```csharp
// before
sb.Append('"').Append(keyStr.Replace("\"", "\\\"")).Append("\":").Append(Render(kv.Value!));

// after
sb.Append('"').Append(EscapeJsonString(keyStr)).Append("\":").Append(Render(kv.Value!));
```

- [ ] **Step 4: Run tests + commit**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~HeaderDecoder" -m:1
git add src/ServiceConnect.Interfaces/Headers/HeaderDecoder.cs \
        src/ServiceConnect.UnitTests/Headers/HeaderDecoderEscapeTests.cs
git commit -m "$(cat <<'EOF'
fix(headers): HeaderDecoder.Render emits valid JSON for all chars (M29)

Pre-fix only " was escaped; \, \n, \r, \t, \b, \f, and U+0000-U+001F
control chars were emitted raw, breaking downstream JSON parsing. Add an
EscapeJsonString helper covering the full RFC 8259 escape table; use it
in Render, RenderDictionary, and RenderNonGenericDictionary.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

(No `!` — internal correctness fix, no API change.)

---

## Task 8: `HeaderDecoder.Render` depth limit

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Headers/HeaderDecoder.cs` (thread `depth` through `Render` / `RenderDictionary` / `RenderEnumerable` / `RenderNonGenericDictionary`).
- Create: `src/ServiceConnect.UnitTests/Headers/HeaderDecoderDepthTests.cs`.

- [ ] **Step 1: Write the failing test**

Create `src/ServiceConnect.UnitTests/Headers/HeaderDecoderDepthTests.cs`:

```csharp
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Headers;

public class HeaderDecoderDepthTests
{
    [Fact]
    public void Decode_NestedDictionaryExceedingDepthLimit_ThrowsCaughtAsTypeName()
    {
        // Build a 33-deep dictionary chain. Decode wraps Render in try/catch and
        // falls back to typeof().FullName on any exception, so the depth-limit
        // throw produces the type-name fallback rather than propagating.
        IDictionary<string, object> root = new Dictionary<string, object>();
        IDictionary<string, object> current = root;
        for (var i = 0; i < 33; i++)
        {
            var inner = new Dictionary<string, object>();
            current["nested"] = inner;
            current = inner;
        }

        var rendered = HeaderDecoder.Decode(root);
        // Decode's catch falls back to type FullName for the bad input.
        Assert.Equal(root.GetType().FullName, rendered);
    }

    [Fact]
    public void Decode_NestedDictionaryAtDepthLimit_RendersSuccessfully()
    {
        // 32-deep is at the boundary — should still render without throwing.
        IDictionary<string, object> root = new Dictionary<string, object>();
        IDictionary<string, object> current = root;
        for (var i = 0; i < 31; i++)
        {
            var inner = new Dictionary<string, object>();
            current["nested"] = inner;
            current = inner;
        }
        current["leaf"] = "value";

        var rendered = HeaderDecoder.Decode(root);
        Assert.NotNull(rendered);
        Assert.StartsWith("{", rendered);
    }
}
```

- [ ] **Step 2: Run pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~HeaderDecoderDepthTests" -m:1
```

Expected: pre-fix `Decode_NestedDictionaryExceedingDepthLimit_ThrowsCaughtAsTypeName` may pass (33 isn't deep enough to StackOverflow on most platforms) OR fail (the depth limit doesn't exist yet so the rendered output is the full nested JSON, not the type-name fallback). The post-fix behavior is deterministic.

- [ ] **Step 3: Apply the depth limit**

In `src/ServiceConnect.Interfaces/Headers/HeaderDecoder.cs`, add a depth constant and thread it through:

```csharp
private const int MaxDepth = 32;

private static string Render(object value, int depth = 0)
{
    if (depth >= MaxDepth)
    {
        throw new InvalidOperationException(
            $"Header value exceeds nesting depth {MaxDepth}.");
    }

    return value switch
    {
        null => "null",
        byte[] bytes => "\"" + EscapeJsonString(Encoding.UTF8.GetString(bytes)) + "\"",
        string s => "\"" + EscapeJsonString(s) + "\"",
        IDictionary<string, object> dict => RenderDictionary(dict, depth + 1),
        IDictionary nonGeneric => RenderNonGenericDictionary(nonGeneric, depth + 1),
        IEnumerable seq => RenderEnumerable(seq, depth + 1),
        _ => RenderScalar(value),
    };
}

private static string RenderDictionary(IDictionary<string, object> dict, int depth)
{
    var sb = new StringBuilder("{");
    bool first = true;
    foreach (var kv in dict)
    {
        if (!first) sb.Append(',');
        first = false;
        sb.Append('"').Append(EscapeJsonString(kv.Key)).Append("\":").Append(Render(kv.Value, depth));
    }
    return sb.Append('}').ToString();
}

private static string RenderNonGenericDictionary(IDictionary dict, int depth)
{
    var sb = new StringBuilder("{");
    bool first = true;
    foreach (DictionaryEntry kv in dict)
    {
        if (!first) sb.Append(',');
        first = false;
        var keyStr = kv.Key?.ToString() ?? "null";
        sb.Append('"').Append(EscapeJsonString(keyStr)).Append("\":").Append(Render(kv.Value!, depth));
    }
    return sb.Append('}').ToString();
}

private static string RenderEnumerable(IEnumerable seq, int depth)
{
    var sb = new StringBuilder("[");
    bool first = true;
    foreach (var item in seq)
    {
        if (!first) sb.Append(',');
        first = false;
        sb.Append(Render(item!, depth));
    }
    return sb.Append(']').ToString();
}
```

The `Render(value)` call from `Decode` (line 45) becomes `Render(value, 0)` (or omit the argument since the default is `0`).

- [ ] **Step 4: Run tests + commit**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~HeaderDecoder" -m:1
git add src/ServiceConnect.Interfaces/Headers/HeaderDecoder.cs \
        src/ServiceConnect.UnitTests/Headers/HeaderDecoderDepthTests.cs
git commit -m "$(cat <<'EOF'
fix(headers): HeaderDecoder.Render rejects header values nested >32 deep

Defense-in-depth: a pathologically nested header (or attack input) could
StackOverflow the consumer thread. Thread a depth counter through the
recursive Render chain; throw InvalidOperationException at depth 32. The
existing Decode catch swallows the throw and falls back to type FullName,
so the consumer gets a graceful "type unknown" header rather than a crash.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

(No `!` — defensive guard, no API change.)

---

## Task 9: L21 — Health-check cancellation tokens

**Files:**
- Modify: `src/ServiceConnect.HealthChecks/BusConsumingHealthCheck.cs:24-35`.
- Modify: `src/ServiceConnect.HealthChecks/ConsumerConnectionHealthCheck.cs`.
- Modify: `src/ServiceConnect.HealthChecks/ProducerConnectionHealthCheck.cs:29-40`.
- Create: `src/ServiceConnect.UnitTests/HealthChecks/HealthCheckCancellationTests.cs`.

- [ ] **Step 1: Write the failing tests**

Create `src/ServiceConnect.UnitTests/HealthChecks/HealthCheckCancellationTests.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class HealthCheckCancellationTests
{
    [Fact]
    public async Task BusConsumingHealthCheck_PreCancelledToken_Throws()
    {
        var bus = Mock.Of<IBus>(b => b.IsConsuming == true);
        var check = new BusConsumingHealthCheck(bus);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            check.CheckHealthAsync(new HealthCheckContext(), cts.Token));
    }

    [Fact]
    public async Task ConsumerConnectionHealthCheck_PreCancelledToken_Throws()
    {
        var consumer = Mock.Of<IConsumer>(c => c.IsConnected == true);
        var check = new ConsumerConnectionHealthCheck(consumer);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            check.CheckHealthAsync(new HealthCheckContext(), cts.Token));
    }

    [Fact]
    public async Task ProducerConnectionHealthCheck_PreCancelledToken_Throws()
    {
        var producer = Mock.Of<IProducer>(p => p.IsHealthy == true);
        var check = new ProducerConnectionHealthCheck(producer);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            check.CheckHealthAsync(new HealthCheckContext(), cts.Token));
    }
}
```

- [ ] **Step 2: Run pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~HealthCheckCancellationTests" -m:1
```

Expected: all 3 fail — pre-fix the checks ignore the token and return `Healthy`.

- [ ] **Step 3: Apply the fix to all three health checks**

In each of `BusConsumingHealthCheck.cs`, `ConsumerConnectionHealthCheck.cs`, and `ProducerConnectionHealthCheck.cs`, add `cancellationToken.ThrowIfCancellationRequested()` as the first line of `CheckHealthAsync`. Example for `BusConsumingHealthCheck`:

```csharp
public Task<HealthCheckResult> CheckHealthAsync(
    HealthCheckContext context,
    CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();

    if (_bus.IsConsuming)
    {
        return Task.FromResult(HealthCheckResult.Healthy("Bus is consuming."));
    }

    var failureStatus = context.Registration?.FailureStatus ?? HealthStatus.Unhealthy;
    return Task.FromResult(new HealthCheckResult(failureStatus, "Bus is not consuming."));
}
```

Same shape for the consumer + producer checks.

- [ ] **Step 4: Run tests + commit**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~HealthCheck" -m:1
git add src/ServiceConnect.HealthChecks/ \
        src/ServiceConnect.UnitTests/HealthChecks/HealthCheckCancellationTests.cs
git commit -m "$(cat <<'EOF'
fix(healthchecks): honour cancellationToken (L21)

CheckHealthAsync now calls ThrowIfCancellationRequested() at entry. The
checks are still O(1) bool reads, so cancellation only fires when the
caller hands in a pre-cancelled token (rare today; future-proofs the
checks against any latency they grow later).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

(No `!` — internal correctness fix.)

---

## Task 10: L23 — `ProducerConnectionHealthCheck` lazy-connect

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Bus/IProducer.cs` (add `HasAttemptedConnection`).
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs` (impl).
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs` (state tracking).
- Modify: `src/ServiceConnect.HealthChecks/ProducerConnectionHealthCheck.cs:29-40`.
- Create: `src/ServiceConnect.UnitTests/HealthChecks/ProducerLazyConnectTests.cs`.

**Background.** `IProducer.IsHealthy` is true when connected, false otherwise — but doesn't distinguish "haven't tried yet" from "tried and failed". Add `HasAttemptedConnection` to the interface; the check returns `Healthy` for the not-yet-tried state.

- [ ] **Step 1: Add `HasAttemptedConnection` to `IProducer`**

In `src/ServiceConnect.Interfaces/Bus/IProducer.cs`, after the `IsHealthy` property (around line 43), add:

```csharp
/// <summary>
/// Gets whether the producer has attempted at least one connection to the broker.
/// </summary>
/// <remarks>
/// Returns <see langword="false"/> for a freshly-constructed producer that has not
/// yet been asked to publish or send. Once a publish/send call begins (whether or
/// not it succeeds), this becomes <see langword="true"/> and stays <see langword="true"/>
/// for the producer's lifetime. The producer health check uses this to distinguish
/// "lazy, not yet tried" (Healthy) from "tried and currently disconnected" (Unhealthy).
/// </remarks>
bool HasAttemptedConnection { get; }
```

- [ ] **Step 2: Implement in `ProducerConnection`**

In `src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs`, near the `IsHealthy()` method (line ~83), add a state flag and a getter. Find where `EnsureConnectedAsync` is called for the first time and set the flag:

```csharp
private int _hasAttemptedConnection;

public bool HasAttemptedConnection => Volatile.Read(ref _hasAttemptedConnection) != 0;

public async Task EnsureConnectedAsync(CancellationToken cancellationToken)
{
    Interlocked.Exchange(ref _hasAttemptedConnection, 1);
    // ... existing body ...
}
```

The `Interlocked.Exchange` is at the top of `EnsureConnectedAsync` so even a connection attempt that fails flips the flag to `true`.

- [ ] **Step 3: Surface in `Producer`**

In `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs:427` (or near `IsHealthy`), add:

```csharp
public bool HasAttemptedConnection => _producerConnection.HasAttemptedConnection;
```

- [ ] **Step 4: Write the failing test**

Create `src/ServiceConnect.UnitTests/HealthChecks/ProducerLazyConnectTests.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class ProducerLazyConnectTests
{
    [Fact]
    public async Task NotYetAttempted_ReturnsHealthy()
    {
        var producer = Mock.Of<IProducer>(p =>
            p.IsHealthy == false &&
            p.HasAttemptedConnection == false);
        var check = new ProducerConnectionHealthCheck(producer);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("not yet attempted", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AttemptedAndDisconnected_ReturnsUnhealthy()
    {
        var producer = Mock.Of<IProducer>(p =>
            p.IsHealthy == false &&
            p.HasAttemptedConnection == true);
        var check = new ProducerConnectionHealthCheck(producer);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Connected_ReturnsHealthy()
    {
        var producer = Mock.Of<IProducer>(p =>
            p.IsHealthy == true &&
            p.HasAttemptedConnection == true);
        var check = new ProducerConnectionHealthCheck(producer);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}
```

- [ ] **Step 5: Run pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ProducerLazyConnectTests" -m:1
```

Expected: `NotYetAttempted_ReturnsHealthy` fails — pre-fix the check returns Unhealthy regardless of `HasAttemptedConnection`.

- [ ] **Step 6: Apply the check fix**

In `src/ServiceConnect.HealthChecks/ProducerConnectionHealthCheck.cs:29-40`, replace `CheckHealthAsync` body:

```csharp
public Task<HealthCheckResult> CheckHealthAsync(
    HealthCheckContext context,
    CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();

    if (_producer.IsHealthy)
    {
        return Task.FromResult(HealthCheckResult.Healthy("Producer connection is open."));
    }

    if (!_producer.HasAttemptedConnection)
    {
        // Producer connects lazily on the first publish/send. Until that happens,
        // "no connection" is the expected state, not a fault — readiness probes
        // shouldn't crash-loop pods that haven't published yet.
        return Task.FromResult(HealthCheckResult.Healthy(
            "Producer has not yet attempted connection (lazy)."));
    }

    var failureStatus = context.Registration?.FailureStatus ?? HealthStatus.Unhealthy;
    return Task.FromResult(new HealthCheckResult(failureStatus, "Producer connection is closed."));
}
```

Also update the class XML doc to reflect the new behavior:

```csharp
/// <summary>
/// Reports Healthy when <see cref="IProducer.IsHealthy"/> is <see langword="true"/>,
/// OR when the producer has not yet attempted any connection (lazy-connect state).
/// O(1), allocation-light, side-effect-free — does not perform broker I/O.
/// </summary>
/// <remarks>
/// The producer connects lazily on the first publish/send call. Pre-v8 this check
/// returned Unhealthy in that pre-publish window, which crash-looped readiness probes.
/// v8: NotYetAttempted is treated as Healthy; once a publish is attempted and fails,
/// transitions to <see cref="HealthStatus.Unhealthy"/>.
/// </remarks>
```

- [ ] **Step 7: Run tests + commit**

```bash
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ProducerLazyConnectTests|HealthCheck" -m:1
git add src/ServiceConnect.Interfaces/Bus/IProducer.cs \
        src/ServiceConnect.Client.RabbitMQ/Producer/ \
        src/ServiceConnect.HealthChecks/ProducerConnectionHealthCheck.cs \
        src/ServiceConnect.UnitTests/HealthChecks/ProducerLazyConnectTests.cs
git commit -m "$(cat <<'EOF'
fix(healthchecks): producer lazy-connect treated as Healthy (L23)

Pre-fix the producer health check returned Unhealthy until the first
publish, crash-looping readiness probes on pods that haven't published
yet. Add IProducer.HasAttemptedConnection to distinguish lazy state from
"tried and failed"; the check returns Healthy for lazy state, Unhealthy
once a connection attempt has occurred and failed.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

(No `!` — `IProducer` interface gains a member; existing implementations need to add it but the addition is a compile-time signal, not a behavioral surprise. If a stricter classification is wanted, mark it as `!` and document in release notes.)

---

## Task 11: `IBus.RequestTimeoutAsync` DIM → `Task.FromException`

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Bus/IBus.cs:92-93`.
- Create: `src/ServiceConnect.UnitTests/Bus/RequestTimeoutAsyncDimTests.cs`.

- [ ] **Step 1: Write the failing test**

Create `src/ServiceConnect.UnitTests/Bus/RequestTimeoutAsyncDimTests.cs`:

```csharp
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.UnitTests.Bus;

public class RequestTimeoutAsyncDimTests
{
    private sealed class StubBus : IBus
    {
        public Task PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : Message => Task.CompletedTask;
        public Task SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message => Task.CompletedTask;
        public Task<TReply> SendRequestAsync<TRequest, TReply>(TRequest message, RequestOptions? options = null, CancellationToken cancellationToken = default)
            where TRequest : Message where TReply : Message => Task.FromResult<TReply>(default!);
        public Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(TRequest message, RequestOptions? options = null, CancellationToken cancellationToken = default)
            where TRequest : Message where TReply : Message => Task.FromResult<IList<TReply>>([]);
        public Task PublishRequestAsync<TRequest, TReply>(TRequest message, Action<TReply> onReply, RequestOptions? options = null, CancellationToken cancellationToken = default)
            where TRequest : Message where TReply : Message => Task.CompletedTask;
        public Task RouteAsync<T>(T message, IList<string> destinations, CancellationToken cancellationToken = default) where T : Message => Task.CompletedTask;
        public IMessageBusWriteStream CreateStream<T>(string endpoint) where T : Message => throw new NotImplementedException();
        public Task StartConsumingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopConsumingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool IsConsuming => false;
        public ValueTask DisposeAsync() => default;
        // RequestTimeoutAsync NOT overridden — falls through to the DIM.
    }

    [Fact]
    public async Task RequestTimeoutAsync_DimNotOverridden_DefersExceptionUntilAwait()
    {
        IBus bus = new StubBus();

        // Pre-fix: throws synchronously on the call line.
        // Post-fix: returns a faulted Task; exception observed at await.
        Task task = bus.RequestTimeoutAsync(Guid.NewGuid(), TimeSpan.FromSeconds(1));
        Assert.NotNull(task);  // synchronous throw would prevent reaching here

        await Assert.ThrowsAsync<NotSupportedException>(async () => await task);
    }
}
```

- [ ] **Step 2: Run pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RequestTimeoutAsyncDimTests" -m:1
```

Expected: fails — pre-fix the call throws synchronously before assigning to `task`.

- [ ] **Step 3: Apply the fix**

In `src/ServiceConnect.Interfaces/Bus/IBus.cs:92-93`, change:

```csharp
Task RequestTimeoutAsync(Guid correlationId, TimeSpan delay, CancellationToken cancellationToken = default)
    => throw new NotSupportedException("This IBus implementation does not support scheduling timeouts.");
```

to:

```csharp
Task RequestTimeoutAsync(Guid correlationId, TimeSpan delay, CancellationToken cancellationToken = default)
    => Task.FromException(new NotSupportedException("This IBus implementation does not support scheduling timeouts."));
```

- [ ] **Step 4: Run tests + commit**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RequestTimeoutAsyncDimTests" -m:1
git add src/ServiceConnect.Interfaces/Bus/IBus.cs \
        src/ServiceConnect.UnitTests/Bus/RequestTimeoutAsyncDimTests.cs
git commit -m "$(cat <<'EOF'
fix(bus): RequestTimeoutAsync DIM defers exception via Task.FromException

Pre-fix the not-supported throw was synchronous on the call line, breaking
symmetry with the success path (which returns a Task observed at await).
Task.FromException defers the exception to the await, matching the rest
of the async surface.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

(No `!` — same exception, deferred timing.)

---

## Task 12: `RequestTimeoutException` invariant culture

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Exceptions/RequestTimeoutException.cs:9`.
- Create: `src/ServiceConnect.UnitTests/Exceptions/RequestTimeoutExceptionCultureTests.cs`.

- [ ] **Step 1: Write the failing test**

Create `src/ServiceConnect.UnitTests/Exceptions/RequestTimeoutExceptionCultureTests.cs`:

```csharp
using System.Globalization;
using ServiceConnect.Interfaces.Exceptions;
using Xunit;

namespace ServiceConnect.UnitTests.Exceptions;

public class RequestTimeoutExceptionCultureTests
{
    [Fact]
    public void Message_UsesInvariantCultureForElapsedFormatting()
    {
        // Save and restore the thread culture to avoid leaking state to sibling tests.
        var prev = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var ex = new RequestTimeoutException(Guid.NewGuid(), TimeSpan.FromMilliseconds(123.456));
            // de-DE uses ',' as decimal separator. Invariant uses '.'.
            // The message must NOT contain a comma in the milliseconds value.
            Assert.DoesNotContain(",", ex.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
        }
    }
}
```

- [ ] **Step 2: Run pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RequestTimeoutExceptionCultureTests" -m:1
```

Expected: fails — pre-fix the message contains "123,456" in de-DE.

- [ ] **Step 3: Apply the fix**

In `src/ServiceConnect.Interfaces/Exceptions/RequestTimeoutException.cs:8-9`, change:

```csharp
public sealed class RequestTimeoutException(Guid correlationId, TimeSpan elapsed)
    : ServiceConnectException($"Request {correlationId} timed out after {elapsed.TotalMilliseconds}ms")
```

to:

```csharp
public sealed class RequestTimeoutException(Guid correlationId, TimeSpan elapsed)
    : ServiceConnectException(System.FormattableString.Invariant(
        $"Request {correlationId} timed out after {elapsed.TotalMilliseconds}ms"))
```

- [ ] **Step 4: Run tests + commit**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RequestTimeoutException" -m:1
git add src/ServiceConnect.Interfaces/Exceptions/RequestTimeoutException.cs \
        src/ServiceConnect.UnitTests/Exceptions/RequestTimeoutExceptionCultureTests.cs
git commit -m "$(cat <<'EOF'
fix(exceptions): RequestTimeoutException message uses invariant culture

Pre-fix the elapsed.TotalMilliseconds interpolation used the current
culture; de-DE / fr-FR / etc. produced messages with comma decimals,
breaking log-aggregation patterns that grep for the timeout value.
Wrap in FormattableString.Invariant so the format is wire-stable.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

(No `!` — internal correctness fix.)

---

## Task 13: `ActivatorUtilities` per-probe → `LazyInitializer`

**Files:**
- Modify: `src/ServiceConnect.HealthChecks/HealthChecksBuilderExtensions.cs:29, 49, 72`.
- Create: `src/ServiceConnect.UnitTests/HealthChecks/HealthCheckRegistrationCachingTests.cs`.

- [ ] **Step 1: Write the failing test**

Create `src/ServiceConnect.UnitTests/HealthChecks/HealthCheckRegistrationCachingTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class HealthCheckRegistrationCachingTests
{
    [Fact]
    public void AddServiceConnectBus_TwoProbesShareTheSameInstance()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IBus>(b => b.IsConsuming == true));
        services.AddHealthChecks().AddServiceConnectBus("test");

        var sp = services.BuildServiceProvider();
        var registration = sp.GetServices<HealthCheckRegistration>()
            .Single(r => r.Name == "test");

        var first = registration.Factory(sp);
        var second = registration.Factory(sp);

        Assert.Same(first, second);
    }

    [Fact]
    public void AddServiceConnectConsumer_TwoProbesShareTheSameInstance()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IConsumer>(c => c.IsConnected == true));
        services.AddHealthChecks().AddServiceConnectConsumer("test");

        var sp = services.BuildServiceProvider();
        var registration = sp.GetServices<HealthCheckRegistration>()
            .Single(r => r.Name == "test");

        var first = registration.Factory(sp);
        var second = registration.Factory(sp);

        Assert.Same(first, second);
    }

    [Fact]
    public void AddServiceConnectProducer_TwoProbesShareTheSameInstance()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IProducer>(p => p.IsHealthy == true));
        services.AddHealthChecks().AddServiceConnectProducer("test");

        var sp = services.BuildServiceProvider();
        var registration = sp.GetServices<HealthCheckRegistration>()
            .Single(r => r.Name == "test");

        var first = registration.Factory(sp);
        var second = registration.Factory(sp);

        Assert.Same(first, second);
    }
}
```

- [ ] **Step 2: Run pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~HealthCheckRegistrationCachingTests" -m:1
```

Expected: 3 fails — pre-fix `ActivatorUtilities.CreateInstance<T>(sp)` allocates a fresh instance every probe.

- [ ] **Step 3: Apply the fix**

In `src/ServiceConnect.HealthChecks/HealthChecksBuilderExtensions.cs`, add the `using System.Threading;` import (for `LazyInitializer`) at the top. Then replace each of the three `sp => ActivatorUtilities.CreateInstance<T>(sp)` lambdas with a `LazyInitializer.EnsureInitialized` pattern:

```csharp
public static IHealthChecksBuilder AddServiceConnectBus(
    this IHealthChecksBuilder builder,
    string name = "serviceconnect-bus",
    HealthStatus failureStatus = HealthStatus.Unhealthy,
    IEnumerable<string>? tags = null,
    TimeSpan? timeout = null)
{
    ArgumentNullException.ThrowIfNull(builder);
    BusConsumingHealthCheck? cached = null;
    return builder.Add(new HealthCheckRegistration(
        name,
        sp => LazyInitializer.EnsureInitialized(ref cached,
            () => ActivatorUtilities.CreateInstance<BusConsumingHealthCheck>(sp)),
        failureStatus,
        tags,
        timeout));
}
```

Same shape for `AddServiceConnectConsumer` (with `ConsumerConnectionHealthCheck`) and `AddServiceConnectProducer` (with `ProducerConnectionHealthCheck`). The `cached` local is per-extension-call, so each registration has its own cache.

- [ ] **Step 4: Run tests + commit**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~HealthCheck" -m:1
git add src/ServiceConnect.HealthChecks/HealthChecksBuilderExtensions.cs \
        src/ServiceConnect.UnitTests/HealthChecks/HealthCheckRegistrationCachingTests.cs
git commit -m "$(cat <<'EOF'
perf(healthchecks): cache check instance via LazyInitializer

Pre-fix every probe allocated a fresh check via ActivatorUtilities.
LazyInitializer.EnsureInitialized atomicizes the first-probe construction;
subsequent probes reuse the cached instance. Closure captures sp from the
first probe (root provider lifetime is consistent across probes).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

(No `!` — internal allocation fix.)

---

## Task 14: Multi-bus health-check API (keyed + factory overloads)

**Files:**
- Modify: `src/ServiceConnect.HealthChecks/HealthChecksBuilderExtensions.cs`.
- Create: `src/ServiceConnect.UnitTests/HealthChecks/MultiBusHealthCheckTests.cs`.

- [ ] **Step 1: Write the failing tests**

Create `src/ServiceConnect.UnitTests/HealthChecks/MultiBusHealthCheckTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class MultiBusHealthCheckTests
{
    [Fact]
    public async Task FactoryOverload_ResolvesViaFactory()
    {
        var bus = Mock.Of<IBus>(b => b.IsConsuming == true);
        var services = new ServiceCollection();
        services.AddHealthChecks().AddServiceConnectBus("custom",
            sp => bus,
            HealthStatus.Unhealthy);

        var sp = services.BuildServiceProvider();
        var registration = sp.GetServices<HealthCheckRegistration>()
            .Single(r => r.Name == "custom");
        var check = (BusConsumingHealthCheck)registration.Factory(sp);
        var result = await check.CheckHealthAsync(new HealthCheckContext { Registration = registration });

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task KeyedOverload_ResolvesByKey_DistinctBuses()
    {
        var busA = Mock.Of<IBus>(b => b.IsConsuming == true);
        var busB = Mock.Of<IBus>(b => b.IsConsuming == false);

        var services = new ServiceCollection();
        services.AddKeyedSingleton("A", busA);
        services.AddKeyedSingleton("B", busB);
        services.AddHealthChecks()
            .AddServiceConnectBus("checkA", "A", HealthStatus.Unhealthy)
            .AddServiceConnectBus("checkB", "B", HealthStatus.Unhealthy);

        var sp = services.BuildServiceProvider();
        var regA = sp.GetServices<HealthCheckRegistration>().Single(r => r.Name == "checkA");
        var regB = sp.GetServices<HealthCheckRegistration>().Single(r => r.Name == "checkB");

        var resultA = await ((BusConsumingHealthCheck)regA.Factory(sp))
            .CheckHealthAsync(new HealthCheckContext { Registration = regA });
        var resultB = await ((BusConsumingHealthCheck)regB.Factory(sp))
            .CheckHealthAsync(new HealthCheckContext { Registration = regB });

        Assert.Equal(HealthStatus.Healthy, resultA.Status);
        Assert.Equal(HealthStatus.Unhealthy, resultB.Status);
    }
}
```

- [ ] **Step 2: Run pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MultiBusHealthCheckTests" -m:1
```

Expected: compilation fails — the factory and keyed overloads don't exist yet.

- [ ] **Step 3: Add the factory and keyed overloads**

In `src/ServiceConnect.HealthChecks/HealthChecksBuilderExtensions.cs`, add per existing extension. Example for `Bus`:

```csharp
/// <summary>
/// Registers a bus-consuming health check resolving the bus via a factory function.
/// Use this for non-DI-resolved buses or for custom keyed-services patterns.
/// </summary>
public static IHealthChecksBuilder AddServiceConnectBus(
    this IHealthChecksBuilder builder,
    string name,
    Func<IServiceProvider, IBus> busFactory,
    HealthStatus failureStatus = HealthStatus.Unhealthy,
    IEnumerable<string>? tags = null,
    TimeSpan? timeout = null)
{
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentNullException.ThrowIfNull(busFactory);
    BusConsumingHealthCheck? cached = null;
    return builder.Add(new HealthCheckRegistration(
        name,
        sp => LazyInitializer.EnsureInitialized(ref cached,
            () => new BusConsumingHealthCheck(busFactory(sp))),
        failureStatus,
        tags,
        timeout));
}

/// <summary>
/// Registers a bus-consuming health check resolving the bus via a keyed-services key.
/// Convenience wrapper over the factory overload for hosts using
/// <see cref="ServiceProviderKeyedServiceExtensions"/>.
/// </summary>
public static IHealthChecksBuilder AddServiceConnectBus(
    this IHealthChecksBuilder builder,
    string name,
    object serviceKey,
    HealthStatus failureStatus = HealthStatus.Unhealthy,
    IEnumerable<string>? tags = null,
    TimeSpan? timeout = null)
    => builder.AddServiceConnectBus(name,
        sp => sp.GetRequiredKeyedService<IBus>(serviceKey),
        failureStatus, tags, timeout);
```

The existing parameterless overload (with default `name`) stays as a wrapper:

```csharp
public static IHealthChecksBuilder AddServiceConnectBus(
    this IHealthChecksBuilder builder,
    string name = "serviceconnect-bus",
    HealthStatus failureStatus = HealthStatus.Unhealthy,
    IEnumerable<string>? tags = null,
    TimeSpan? timeout = null)
    => builder.AddServiceConnectBus(name,
        sp => sp.GetRequiredService<IBus>(),
        failureStatus, tags, timeout);
```

Apply the same three-overload pattern to `AddServiceConnectConsumer` (factory of `IConsumer`, key resolves `IConsumer`) and `AddServiceConnectProducer` (factory of `IProducer`).

- [ ] **Step 4: Run tests + commit**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~HealthCheck" -m:1
git add src/ServiceConnect.HealthChecks/HealthChecksBuilderExtensions.cs \
        src/ServiceConnect.UnitTests/HealthChecks/MultiBusHealthCheckTests.cs
git commit -m "$(cat <<'EOF'
feat(healthchecks): keyed + factory overloads for multi-bus support

Multi-bus deployments (one process running multiple IBus instances) had no
way to register a health check per instance. Add a factory overload taking
Func<IServiceProvider, IBus> and a keyed convenience overload that
delegates via GetRequiredKeyedService. The existing parameterless overload
becomes a wrapper that delegates to the factory with GetRequiredService.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

(No `!` — additive API.)

---

## Task 15: Phase 12 release notes

**File:** `website/src/content/docs/releases.mdx`.

- [ ] **Step 1: Insert the section**

Find the prior phase entry (`### Processor cleanups`); insert immediately after:

```mdx
### Interfaces + health checks + remaining polish

**v8 breaking changes**

- **`IMessageHandler` / `IProcessHandler` / `IStreamHandler` signatures.** `HandleAsync` (and `IStreamHandler.ExecuteAsync`) now take the per-message context as a parameter; the `Context` (or `Stream`) property is removed. Pre-v8 the framework wrote `handler.Context = ...` before each call, which was unsafe for singleton-registered handlers (concurrent dispatches raced). Migration:

  ```csharp
  // before (v7)
  public class OrderHandler : IMessageHandler<OrderPlaced>
  {
      public IConsumeContext Context { get; set; } = null!;
      public Task HandleAsync(OrderPlaced msg, CancellationToken ct = default)
      {
          var bus = Context.Bus;
          // ...
      }
  }

  // after (v8)
  public class OrderHandler : IMessageHandler<OrderPlaced>
  {
      public Task HandleAsync(OrderPlaced msg, IConsumeContext context, CancellationToken ct = default)
      {
          var bus = context.Bus;
          // ...
      }
  }
  ```

- **`Message.CorrelationId`** is now `init`-only (was `private set`). Object initializer syntax still works; in-class post-construction reassignment no longer compiles.

- **`TimeoutData.Headers`** and **`ConsumeEventArgs.Headers`** are now `IReadOnlyDictionary<string, object>`. Producers construct via init; consumers read only. Middleware that mutates these headers post-receive must construct a new instance with the modified dictionary.

**v8 contract clarifications (non-breaking)**

- **`Aggregator<T>.Timeout()`** default returns `Timeout.InfiniteTimeSpan` (was `default(TimeSpan)` = `TimeSpan.Zero`). The dispatcher gates scheduling on `> TimeSpan.Zero`, so `InfiniteTimeSpan` cleanly disables the timeout-based flush. Subclasses that overrode `Timeout()` to return positive durations are unaffected.

- **`ProducerConnectionHealthCheck`** returns `Healthy` for the lazy-not-yet-tried state. Once the producer attempts a publish/send and fails, it transitions to `Unhealthy`. Readiness probes no longer crash-loop pods that haven't published at startup. New API: `IProducer.HasAttemptedConnection`.

- **`HeaderDecoder.Render`** throws `InvalidOperationException` for header values nested deeper than 32 levels. The existing `Decode` catch swallows the throw and falls back to `type.FullName`, so a pathological header degrades gracefully.

- **`HeaderDecoder.Render`** now emits valid JSON for all input. Pre-v8 only `"` was escaped; `\`, `\n`, `\r`, `\t`, and U+0000-U+001F control characters were emitted raw, breaking downstream JSON parsing.

- **`IBus.RequestTimeoutAsync`** default-interface-method now defers the `NotSupportedException` via `Task.FromException` (was synchronous throw).

- **`RequestTimeoutException`** message uses invariant culture for the elapsed-milliseconds value (was current culture).

**Bug fixes**

- **Health checks honour `cancellationToken`** — `BusConsumingHealthCheck`, `ConsumerConnectionHealthCheck`, `ProducerConnectionHealthCheck` all call `ThrowIfCancellationRequested()` at entry.

- **Health-check instances cached per registration** — `LazyInitializer.EnsureInitialized` ensures the check object is constructed once per `AddServiceConnect*` call, not per probe.

**New API**

- **Multi-bus health-check overloads.** `AddServiceConnectBus`, `AddServiceConnectConsumer`, `AddServiceConnectProducer` each gain:
  - A factory overload: `(string name, Func<IServiceProvider, IBus> busFactory, ...)`.
  - A keyed-services convenience overload: `(string name, object serviceKey, ...)` resolving via `GetRequiredKeyedService<IBus>(serviceKey)`.

- **`IProducer.HasAttemptedConnection`** — `true` once a publish/send call has begun (whether or not it succeeded), `false` for a freshly-constructed producer. Used by `ProducerConnectionHealthCheck` to distinguish lazy-state from failure.
```

- [ ] **Step 2: Build the website**

```bash
npm --prefix website run build 2>&1 | tail -3
```

Expected: clean.

- [ ] **Step 3: Commit**

```bash
git add website/src/content/docs/releases.mdx
git commit -m "$(cat <<'EOF'
docs(website): phase 12 release notes — interfaces + health checks + polish

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 16: Reference + learn doc updates

**Files:** various pages under `website/src/content/docs/reference/`.

- [ ] **Step 1: Locate relevant pages**

```bash
grep -rln "IMessageHandler\|IProcessHandler\|IStreamHandler\|HandleAsync\|RequestTimeoutAsync\|Aggregator.Timeout\|ProducerConnectionHealth\|HeaderDecoder\|TimeoutData\|ConsumeEventArgs" website/src/content/docs/ 2>/dev/null
```

- [ ] **Step 2: Update handler reference pages**

For pages documenting `IMessageHandler` / `IProcessHandler` / `IStreamHandler`: update sample code to the new signature; add a "v8 migration" subsection with the before/after shim from Task 15's release notes.

- [ ] **Step 3: Update `Message` reference page**

If the page documents `Message.CorrelationId`, note the `init` accessor change.

- [ ] **Step 4: Update `Aggregator` configuration page**

Note that `Timeout()` returning `Timeout.InfiniteTimeSpan` (the new default) disables timeout-based flush; positive durations enable it.

- [ ] **Step 5: Update health-checks reference page**

Document:
- The cancellation-token contract.
- The producer lazy-connect Healthy semantics + `IProducer.HasAttemptedConnection`.
- The new factory and keyed overloads for multi-bus support, with code samples.

- [ ] **Step 6: Build + commit**

```bash
npm --prefix website run build 2>&1 | tail -3
git add website/src/content/docs/
git commit -m "$(cat <<'EOF'
docs(website): phase 12 reference + learn page updates

Handler signature change. Message.CorrelationId init. Aggregator timeout
sentinel. Producer lazy-connect health-check semantics. Multi-bus
health-check overloads. Header decoder JSON correctness + depth guard.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 17: Examples README updates

**Files:**
- `examples/Aggregator/README.md` — note `Timeout.InfiniteTimeSpan` is the new "disabled" sentinel.
- `examples/Filters/README.md` — note middleware sees the new `HandleAsync(message, context, ct)` signature.
- Any `examples/*/README.md` that documents handler shape — update to the new signature if needed.

- [ ] **Step 1: Audit each examples README**

```bash
for f in examples/*/README.md; do
    echo "=== $f ==="
    grep -l "HandleAsync\|Context\|Aggregator.Timeout\|IMessageHandler" "$f" 2>/dev/null
done
```

- [ ] **Step 2: Update each affected README**

Append a "v8 contract" subsection. Suggested wording for handler-using examples:

```mdx
**v8 handler signature.** Handlers now take the per-message `IConsumeContext` as a parameter to `HandleAsync` (or `ExecuteAsync` for stream handlers). Pre-v8 the framework set a `Context` property before each call, which was unsafe for singleton-registered handlers. Migration is mechanical: append `IConsumeContext context` to the method signature; replace `this.Context` reads with `context`.
```

For `examples/Aggregator/README.md`:

```mdx
**v8 timeout sentinel.** `Aggregator<T>.Timeout()` default now returns `Timeout.InfiniteTimeSpan` (was `TimeSpan.Zero`). Override the method to return a positive `TimeSpan` to enable timeout-based flush. The default disables it.
```

- [ ] **Step 3: Commit**

```bash
git add examples/
git commit -m "$(cat <<'EOF'
docs(examples): phase 12 README updates

Handler signature change. Aggregator timeout sentinel.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 18: Final verification gate

- [ ] **Step 1: Per-csproj build clean**

```bash
dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj -m:1
dotnet build src/ServiceConnect.HealthChecks/ServiceConnect.HealthChecks.csproj -m:1
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1
```

Expected: 0 errors, 0 warnings on each.

- [ ] **Step 2: Focused unit-test pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
  --filter "FullyQualifiedName~HealthCheck|RequestOptions|HeaderDecoder|Aggregator|RequestTimeoutException|MessageHandler|ProcessHandler|StreamHandler|TimeoutData|ConsumeEventArgs|RequestTimeout|MessageInit|HandlerSignature" -m:1
```

Expected: all pass.

- [ ] **Step 3: Astro build**

```bash
npm --prefix website run build 2>&1 | tail -3
```

Expected: clean.

- [ ] **Step 4: Grep verifications**

```bash
# Handler context/stream property removed
grep -n "IConsumeContext Context { get; set; }\|IMessageBusReadStream Stream { get; set; }" \
    src/ServiceConnect.Interfaces/Handlers/ src/ServiceConnect.Interfaces/ProcessManagers/
# Expected: zero hits.

# Message.CorrelationId is init
grep -n "CorrelationId" src/ServiceConnect.Interfaces/Messages/Message.cs
# Expected: contains "init", not "private set".

# TimeoutData.Headers + ConsumeEventArgs.Headers are IReadOnlyDictionary
grep -n "Headers" src/ServiceConnect.Interfaces/Timeouts/TimeoutData.cs src/ServiceConnect.Interfaces/Bus/ConsumeEventArgs.cs
# Expected: IReadOnlyDictionary<string, object>.

# Aggregator default returns InfiniteTimeSpan
grep -n "Timeout.InfiniteTimeSpan\|return default" src/ServiceConnect.Interfaces/Aggregation/Aggregator.cs
# Expected: contains InfiniteTimeSpan, NOT "return default".

# HeaderDecoder escape + depth
grep -n "EscapeJsonString\|MaxDepth\|nesting depth" src/ServiceConnect.Interfaces/Headers/HeaderDecoder.cs
# Expected: at least 3 hits.

# Health checks honour token
grep -n "ThrowIfCancellationRequested" src/ServiceConnect.HealthChecks/
# Expected: 3 hits (one per check).

# Producer HasAttemptedConnection
grep -n "HasAttemptedConnection" src/ServiceConnect.Interfaces/Bus/IProducer.cs
# Expected: at least 1 hit.

# RequestTimeoutAsync DIM uses Task.FromException
grep -n "Task.FromException" src/ServiceConnect.Interfaces/Bus/IBus.cs
# Expected: 1 hit.

# RequestTimeoutException invariant
grep -n "FormattableString.Invariant\|CultureInfo.InvariantCulture" src/ServiceConnect.Interfaces/Exceptions/RequestTimeoutException.cs
# Expected: 1 hit.

# LazyInitializer + factory overloads
grep -n "LazyInitializer\|GetRequiredKeyedService" src/ServiceConnect.HealthChecks/HealthChecksBuilderExtensions.cs
# Expected: at least 4 hits (LazyInitializer × 3 + GetRequiredKeyedService × 3 = 6).
```

- [ ] **Step 5: Commit count**

```bash
git log --oneline d7b0bc3f..HEAD | wc -l
git log --oneline d7b0bc3f..HEAD
```

Expected: 16-17 commits (1 plan, 12-13 fixes, 3 docs commits).

- [ ] **Step 6: Final code review (optional)**

If running the final review: dispatch `superpowers:code-reviewer` over Phase 12 commits (from `d7b0bc3f` through HEAD).

---

## Phase 12 done

All findings closed. The phase ships:

- Three v8 breaking changes: handler signatures, `Message.CorrelationId` `init`, `TimeoutData`/`ConsumeEventArgs` headers `IReadOnlyDictionary`.
- Aggregator timeout sentinel; producer lazy-connect health; header decoder JSON correctness + depth guard.
- Health-check cancellation tokens; per-registration check caching; multi-bus factory + keyed overloads; `IProducer.HasAttemptedConnection`.
- DIM `Task.FromException` symmetry; `RequestTimeoutException` invariant culture.
- Release notes + reference + examples README updates.

**Phase 12 closes the bug-fix sweep.** v8 is feature-complete after this phase ships.
