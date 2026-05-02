# Phase 07 — Bus, dispatcher, and request/reply Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Address 14 correctness defects across the runtime core: 2 Critical (C9 unresolved-type retry-loop, C10 RequestReplyManager counter underflow), 7 High (H14-H20 Bus lifecycle + RequestReply state machine + routing-slip validation), and 5 Smaller polish items.

**Architecture:** Three coordinated changes: (1) `MessageDispatcher` returns `Success=true, NotHandled=true` for unresolved types so the existing not-handled path handles them; (2) `RequestReplyManager` is redesigned around a single `_stateLock` (drops `SyncRoot`, eliminates `_inFlightReplies` underflow), with a new `RequestSendCancelledException : OperationCanceledException` for fail-fast send-time cancellation; (3) `Bus.DisposeAsync` and `Bus.StopConsumingCoreAsync` no longer dispose the DI-singleton transports (DI owns them), and routing-slip validation throws `ArgumentException` on null/empty/comma-containing destinations.

**Tech Stack:** .NET multi-target net8.0/net10.0, xUnit + Moq, Astro/Starlight for the website. No E2E tests; all unit-level.

**Spec:** [`docs/superpowers/specs/2026-04-29-phase-07-bus-dispatcher-requestreply.md`](../specs/2026-04-29-phase-07-bus-dispatcher-requestreply.md).

---

## Build/test safety

This machine has crashed when running unconstrained whole-solution `dotnet build` / `dotnet test` against this codebase (CLAUDE.md has the full incident analysis). The wrapper at `~/.local/bin/dotnet` re-execs every `dotnet` invocation under a systemd cgroup with `CPUQuota=800%`, `MemoryMax=8G`, `MemorySwapMax=0`, `TasksMax=200` and is the safety net. **Even with the wrapper, every command in this plan is per-csproj.** Add `-m:1` to `dotnet test` invocations to serialize MSBuild and stay within `TasksMax`. Never run whole-solution `dotnet build` / `dotnet test` / `dotnet format`.

---

## File structure

### Modified — production code

- `src/ServiceConnect/Services/MessageDispatcher.cs` — Task 7 (C9): two sites change `Success=false` → `Success=true, NotHandled=true` for unresolved types.
- `src/ServiceConnect/Services/RequestReplyManager.cs` — Tasks 9, 10, 11 (H18, H17, smaller XML doc): single-lock state machine, `RequestSendCancelledException` fail-fast paths, doc tightening.
- `src/ServiceConnect/Bus.cs` — Tasks 3, 4, 12, 13, 14, 15 (smaller capacity verification, CreateStream validation, transport dispose removal, OCE handling, lifecycle semaphore polish, routing-slip validation).
- `src/ServiceConnect/Configuration/QueueConfiguration.cs` — Task 5 (smaller perf): cache the `ReadOnlyDictionary` wrapper.
- `src/ServiceConnect/Services/SendMessagePipeline.cs` — Task 6 (smaller): lifetime validation covers DI-registered middleware.

### Created — production code

- `src/ServiceConnect.Interfaces/Exceptions/RequestSendCancelledException.cs` — Task 8 (H17 part 1): new public exception type.

### Created — tests

- `src/ServiceConnect.UnitTests/MessageDispatcherUnresolvedTypeTests.cs` — C9.
- `src/ServiceConnect.UnitTests/RequestReplyManagerInFlightCounterTests.cs` — C10 regression.
- `src/ServiceConnect.UnitTests/RequestReplyManagerSendCancelTests.cs` — H17.
- `src/ServiceConnect.UnitTests/RequestReplyManagerCallbackReentrancyTests.cs` — H18.
- `src/ServiceConnect.UnitTests/BusLifecycleCancellationTests.cs` — H14 (or extend `BusLifecycleTests.cs` if exists).
- `src/ServiceConnect.UnitTests/BusTransportLifecycleTests.cs` — H15.
- `src/ServiceConnect.UnitTests/BusRouteValidationTests.cs` — H19/H20.
- `src/ServiceConnect.UnitTests/BusCreateStreamValidationTests.cs` — smaller endpoint validation.
- `src/ServiceConnect.UnitTests/QueueConfigurationCachedMappingsTests.cs` — smaller wrapper caching.

### Modified — tests

- `src/ServiceConnect.UnitTests/SendMessagePipelineTests.cs` — extend with the DI-middleware lifetime check.

### Modified — website

- `website/src/content/docs/reference/bus/ibus.mdx` — Tasks 16/17.
- `website/src/content/docs/learn/messaging-patterns/routing-slip.mdx` — Tasks 17.
- `website/src/content/docs/learn/messaging-patterns/request-reply.mdx` — Task 17.
- `website/src/content/docs/learn/operations/error-handling.mdx` — Task 17.
- `website/src/content/docs/releases.mdx` — Task 16.

---

## Task 1: Spec

**Already shipped at commit `98fb1969`** (`docs(spec): phase 07 bus, dispatcher, request/reply`). Skip.

---

## Task 2: This plan

**This is the plan commit.** After writing this file, the implementer should:

```bash
git add docs/superpowers/plans/2026-04-29-phase-07-bus-dispatcher-requestreply.md
git commit -m "docs(plan): phase 07 implementation plan"
```

---

## Task 3: Smaller — Verify and (if needed) tighten `BuildHeadersDirect` capacity

**Files:**
- Verify: `src/ServiceConnect/Bus.cs:710-718`
- (Possibly modify): `src/ServiceConnect/Bus.cs`

This task is a **verification + small fix only if needed**. The phase doc said "+5 → +3" but the actual code uses `ReservedHeaders.Count + (snapshot?.Length ?? 0)`, which is a dynamic value. Phase 4's H21 reduced `ReservedHeaders` from 3 items (`MessageType`, `MessageId`, `CorrelationId`) to 2 (`MessageId`, `CorrelationId`).

- [ ] **Step 1: Read the current shape**

```bash
sed -n '710,720p' src/ServiceConnect/Bus.cs
```

Expected current code:

```csharp
private Dictionary<string, string> BuildHeadersDirect(Guid correlationId, IReadOnlyDictionary<string, string>? additionalHeaders)
{
    var snapshot = additionalHeaders?.ToArray();
    var capacity = ReservedHeaders.Count + (snapshot?.Length ?? 0);
    var headers = new Dictionary<string, string>(capacity, StringComparer.Ordinal);
    // ...
```

If `capacity` is computed dynamically from `ReservedHeaders.Count`, the spec's "+5 → +3" no longer applies (the code already uses the dynamic count). In that case **commit only a comment update** noting the post-H21 count for clarity.

- [ ] **Step 2: Read the ReservedHeaders set**

```bash
grep -A 6 "ReservedHeaders = new" src/ServiceConnect/Bus.cs
```

Expected: 2 entries (`HeaderKeys.MessageId`, `HeaderKeys.CorrelationId`).

- [ ] **Step 3: Tighten if needed**

If the code uses `ReservedHeaders.Count` dynamically, no change is needed — the capacity is already tight. Add a code comment explaining the post-Phase-4 invariant:

```csharp
private Dictionary<string, string> BuildHeadersDirect(Guid correlationId, IReadOnlyDictionary<string, string>? additionalHeaders)
{
    var snapshot = additionalHeaders?.ToArray();
    // Capacity tracks ReservedHeaders.Count (currently 2: MessageId + CorrelationId) plus the
    // caller's headers. Post-Phase-4 H21 removed MessageType from the reserved set, so this
    // is already tight; the dynamic count adjusts automatically if the set evolves.
    var capacity = ReservedHeaders.Count + (snapshot?.Length ?? 0);
    var headers = new Dictionary<string, string>(capacity, StringComparer.Ordinal);
    // ...
```

If instead the code has a literal `+ 5` somewhere, replace with `+ 3` (or better, `+ ReservedHeaders.Count`).

- [ ] **Step 4: Build + run existing Bus tests**

```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Bus" -m:1
```

Expected: build clean, all Bus tests pass (no behaviour change).

- [ ] **Step 5: Commit (only if a change was made)**

```bash
git add src/ServiceConnect/Bus.cs
git commit -m "perf(bus): document BuildHeadersDirect capacity invariant post-H21"
```

If no change was needed (dynamic count already correct), **skip Task 3 commit**, document the no-op in the implementer report, and move to Task 4.

---

## Task 4: Smaller — Validate `Bus.CreateStream<T>` endpoint

**Files:**
- Modify: `src/ServiceConnect/Bus.cs:359-368` — add endpoint validation.
- Create: `src/ServiceConnect.UnitTests/BusCreateStreamValidationTests.cs`.

- [ ] **Step 1: Locate the existing CreateStream method**

```bash
grep -A 8 "public IMessageBusWriteStream CreateStream" src/ServiceConnect/Bus.cs
```

Expected current code:

```csharp
public IMessageBusWriteStream CreateStream<T>(string endpoint) where T : Message
{
    ThrowIfDisposed();
    if (_producer == null)
    {
        throw new InvalidOperationException("No producer registered. Cannot create stream.");
    }

    return new MessageBusWriteStream(_producer, endpoint, typeof(T));
}
```

- [ ] **Step 2: Write the failing test**

Create `src/ServiceConnect.UnitTests/BusCreateStreamValidationTests.cs`:

```csharp
using ServiceConnect;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

public sealed class BusCreateStreamValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateStream_NullOrWhitespaceEndpoint_ThrowsArgumentException(string? endpoint)
    {
        // Build a Bus with the canonical Bus-test arrange harness. The arrange shape can be
        // copied from BusTests.cs's existing helper if there is one; otherwise minimal:
        var bus = BusTestsHarness.Build();  // existing helper or inline construction

        Assert.Throws<ArgumentException>(() => bus.CreateStream<TestMessage>(endpoint!));
    }

    [Fact]
    public void CreateStream_ValidEndpoint_DoesNotThrow()
    {
        var bus = BusTestsHarness.Build();
        var stream = bus.CreateStream<TestMessage>("valid.endpoint");
        Assert.NotNull(stream);
    }

    private sealed class TestMessage : Message { }
}
```

If no `BusTestsHarness.Build()` helper exists in the test project, use the canonical Bus arrange shape from `BusTests.cs`. The implementer copies whatever's there.

- [ ] **Step 3: Run the test pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~BusCreateStreamValidationTests" -m:1
```

Expected: the null/whitespace tests FAIL (the current code passes the bad endpoint into `MessageBusWriteStream` constructor without validating).

- [ ] **Step 4: Apply the fix**

In `src/ServiceConnect/Bus.cs`:

```csharp
// before
public IMessageBusWriteStream CreateStream<T>(string endpoint) where T : Message
{
    ThrowIfDisposed();
    if (_producer == null)
    {
        throw new InvalidOperationException("No producer registered. Cannot create stream.");
    }

    return new MessageBusWriteStream(_producer, endpoint, typeof(T));
}

// after
public IMessageBusWriteStream CreateStream<T>(string endpoint) where T : Message
{
    ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
    ThrowIfDisposed();
    if (_producer == null)
    {
        throw new InvalidOperationException("No producer registered. Cannot create stream.");
    }

    return new MessageBusWriteStream(_producer, endpoint, typeof(T));
}
```

- [ ] **Step 5: Run the test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~BusCreateStreamValidationTests" -m:1
```

Expected: 4/4 pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.UnitTests/BusCreateStreamValidationTests.cs \
        src/ServiceConnect/Bus.cs
git commit -m "fix(bus): validate CreateStream endpoint"
```

---

## Task 5: Smaller — Cache `QueueConfiguration.QueueMappings` wrapper

**Files:**
- Modify: `src/ServiceConnect/Configuration/QueueConfiguration.cs:40-41` (and any mutation site for `_mappings`).
- Create: `src/ServiceConnect.UnitTests/QueueConfigurationCachedMappingsTests.cs`.

- [ ] **Step 1: Read the current implementation**

```bash
cat src/ServiceConnect/Configuration/QueueConfiguration.cs
```

Identify:
- The `QueueMappings` property (around line 40).
- The `_mappings` backing field.
- Mutation sites (e.g., `AddQueueMapping`).

- [ ] **Step 2: Write the failing test**

Create `src/ServiceConnect.UnitTests/QueueConfigurationCachedMappingsTests.cs`:

```csharp
using ServiceConnect.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

public sealed class QueueConfigurationCachedMappingsTests
{
    [Fact]
    public void QueueMappings_TwoConsecutiveAccesses_ReturnSameReference()
    {
        var config = new QueueConfiguration();
        config.AddQueueMapping(typeof(string), "queue-1");

        var first = config.QueueMappings;
        var second = config.QueueMappings;

        // Pre-fix: each access allocates a new ReadOnlyDictionary wrapper → references differ.
        // Post-fix: the wrapper is cached → same reference.
        Assert.Same(first, second);
    }

    [Fact]
    public void QueueMappings_AfterMutation_ReturnsFreshWrapper()
    {
        var config = new QueueConfiguration();
        config.AddQueueMapping(typeof(string), "queue-1");
        var first = config.QueueMappings;

        config.AddQueueMapping(typeof(int), "queue-2");
        var second = config.QueueMappings;

        // After AddQueueMapping mutates _mappings, the cached wrapper is invalidated and a
        // fresh one is allocated. References differ; both reflect the current mappings.
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Count);
    }
}
```

If `AddQueueMapping` doesn't exist with that exact signature, adapt — read the actual class.

- [ ] **Step 3: Run the tests pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~QueueConfigurationCachedMappingsTests" -m:1
```

Expected: `QueueMappings_TwoConsecutiveAccesses_ReturnSameReference` FAILS (each access allocates a new wrapper). The other test passes (mutation always returns a fresh result).

- [ ] **Step 4: Apply the fix**

In `src/ServiceConnect/Configuration/QueueConfiguration.cs`:

```csharp
// before
public IReadOnlyDictionary<Type, IReadOnlyList<string>> QueueMappings =>
    new ReadOnlyDictionary<Type, IReadOnlyList<string>>(_mappings);

// after
private IReadOnlyDictionary<Type, IReadOnlyList<string>>? _mappingsView;
public IReadOnlyDictionary<Type, IReadOnlyList<string>> QueueMappings =>
    _mappingsView ??= new ReadOnlyDictionary<Type, IReadOnlyList<string>>(_mappings);
```

In every site that mutates `_mappings` (e.g., `AddQueueMapping`), invalidate the cached view AFTER the mutation:

```csharp
public void AddQueueMapping(Type messageType, string queueName)
{
    // ... existing mutation logic ...
    _mappingsView = null;  // invalidate cached wrapper
}
```

If there are multiple mutation sites, invalidate at each. Search:

```bash
grep -n "_mappings\." src/ServiceConnect/Configuration/QueueConfiguration.cs
```

- [ ] **Step 5: Run the tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~QueueConfiguration" -m:1
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.UnitTests/QueueConfigurationCachedMappingsTests.cs \
        src/ServiceConnect/Configuration/QueueConfiguration.cs
git commit -m "perf(queue-config): cache QueueMappings wrapper"
```

---

## Task 6: Smaller — `SendMessagePipeline` lifetime validation covers DI-registered middleware

**Files:**
- Modify: `src/ServiceConnect/Services/SendMessagePipeline.cs:23-39` (the validation logic).
- Modify: `src/ServiceConnect.UnitTests/SendMessagePipelineTests.cs` — extend with the new test.

- [ ] **Step 1: Read the current validation logic**

```bash
sed -n '1,80p' src/ServiceConnect/Services/SendMessagePipeline.cs
```

Identify how `SendMessagePipeline` validates middleware lifetimes today. The phase doc says it "only covers explicit pipeline registrations" — the fix walks the `IServiceProvider` registrations for `ISendMessageMiddleware` and validates their lifetimes too.

The exact validation policy depends on the existing code — preserve whatever lifetime rule it already enforces (likely "non-Transient"), just expand the scope.

- [ ] **Step 2: Write the failing test**

Extend `src/ServiceConnect.UnitTests/SendMessagePipelineTests.cs` (or create alongside if it doesn't exist):

```csharp
[Fact]
public void Validate_MiddlewareRegisteredViaDirectDi_LifetimeIsChecked()
{
    var services = new ServiceCollection();
    // Register middleware DIRECTLY via DI (bypassing the explicit AddSendMessageMiddleware<>):
    services.AddTransient<ISendMessageMiddleware, NoOpMiddleware>();

    // Build the pipeline; expect it to detect the Transient registration.
    var sp = services.BuildServiceProvider();

    // The exact API depends on SendMessagePipeline's existing shape — locate the validation
    // entry point (likely a constructor call or an explicit Validate() method) and assert the
    // expected exception type.
    Assert.Throws<InvalidOperationException>(() => new SendMessagePipeline(sp /* + other deps */));
}

private sealed class NoOpMiddleware : ISendMessageMiddleware
{
    public Task ExecuteAsync(SendContext context, Func<SendContext, CancellationToken, Task> next, CancellationToken ct) =>
        next(context, ct);
}
```

The exact constructor signature and exception type depend on `SendMessagePipeline.cs`'s current shape. Read first; adapt.

- [ ] **Step 3: Run the test pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~SendMessagePipelineTests" -m:1
```

Expected: the new test FAILS (validation today only catches explicit `AddSendMessageMiddleware<>` registrations).

- [ ] **Step 4: Apply the fix**

In `src/ServiceConnect/Services/SendMessagePipeline.cs`, extend the validation block to enumerate all `IServiceProvider` registrations of `ISendMessageMiddleware` and validate each. Walk pattern:

```csharp
// Inside the existing validation method/constructor:
var directRegistrations = services
    .Where(d => d.ServiceType == typeof(ISendMessageMiddleware))
    .ToList();

foreach (var descriptor in directRegistrations)
{
    if (descriptor.Lifetime == ServiceLifetime.Transient)
    {
        throw new InvalidOperationException(
            $"ISendMessageMiddleware '{descriptor.ImplementationType?.FullName ?? "<unknown>"}' " +
            $"is registered as Transient. Send-pipeline middleware must be Singleton or Scoped " +
            $"to participate correctly in the pipeline lifecycle.");
    }
}
```

The exact code depends on the existing validation pattern — preserve the existing rule, just expand its reach.

- [ ] **Step 5: Run the tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~SendMessagePipeline" -m:1
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.UnitTests/SendMessagePipelineTests.cs \
        src/ServiceConnect/Services/SendMessagePipeline.cs
git commit -m "fix(send-pipeline): lifetime validation covers DI-registered middleware"
```

---

## Task 7: C9 — Unresolved type → `Success=true, NotHandled=true`

**Files:**
- Modify: `src/ServiceConnect/Services/MessageDispatcher.cs:117-126,:147-150`.
- Create: `src/ServiceConnect.UnitTests/MessageDispatcherUnresolvedTypeTests.cs`.

**Background.** Returning `Success=false` for unresolved types causes the consumer host to nack-with-requeue → retries until max → error queue. An unregistered type is terminal. The fix reuses the existing not-handled path, which routes via `DeadLetterUnhandledMessages` (when enabled) OR ack-and-drop.

- [ ] **Step 1: Read the current dispatcher around the two sites**

```bash
sed -n '110,155p' src/ServiceConnect/Services/MessageDispatcher.cs
```

Confirm the two `Success=false` returns at lines ~122 and ~149. Identify the canonical arrange harness used by other tests in `MessageDispatcherTests.cs`:

```bash
grep -n "MakeDispatcher\|new MessageDispatcher\|TestDispatchHandler" src/ServiceConnect.UnitTests/MessageDispatcherTests.cs | head -10
```

- [ ] **Step 2: Write the failing tests**

Create `src/ServiceConnect.UnitTests/MessageDispatcherUnresolvedTypeTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests;

public sealed class MessageDispatcherUnresolvedTypeTests
{
    [Fact]
    public async Task DispatchAsync_UnresolvedType_NoResponseId_ReturnsNotHandled()
    {
        // Reuse the canonical MessageDispatcherTests arrange shape. The crucial mocks:
        //   - IMessageTypeRegistry mock returns false from TryResolve for the test type.
        //   - Headers do NOT contain ResponseMessageId.

        var typeRegistry = new Mock<IMessageTypeRegistry>();
        // TryResolve(string, out Type) returns false for any type name.
        Type? outType;
        typeRegistry
            .Setup(r => r.TryResolve(It.IsAny<string>(), out outType))
            .Returns(false);

        var dispatcher = BuildDispatcher(typeRegistry.Object);
        // [Reuse the canonical helper that builds a MessageDispatcher with the supplied registry.]

        var headers = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [HeaderKeys.FullTypeName] = "Foo.UnregisteredType",
            // No ResponseMessageId — drives the no-reply branch.
        };

        var result = await dispatcher.DispatchAsync(
            new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }),
            "Foo.UnregisteredType",
            headers,
            CancellationToken.None).ConfigureAwait(false);

        // Pre-fix: result.Success == false, result.NotHandled == false.
        // Post-fix: result.Success == true, result.NotHandled == true.
        Assert.True(result.Success);
        Assert.True(result.NotHandled);
    }

    [Fact]
    public async Task DispatchAsync_UnresolvedType_WithResponseId_ButNoReplyHandlerMatched_ReturnsNotHandled()
    {
        // ResponseMessageId is set, but no pending request matches → the late return path
        // at MessageDispatcher.cs:147 fires.

        var typeRegistry = new Mock<IMessageTypeRegistry>();
        Type? outType;
        typeRegistry.Setup(r => r.TryResolve(It.IsAny<string>(), out outType)).Returns(false);

        // ReplyProcessor mock returns NotHandled (no pending request).
        var replyProcessor = new Mock<IReplyProcessor>();
        replyProcessor
            .Setup(p => p.ProcessAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<Type>(),
                It.IsAny<object>(), It.IsAny<IDictionary<string, object>>(), It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProcessResult.NotHandled);

        var dispatcher = BuildDispatcher(typeRegistry.Object, replyProcessor.Object);

        var headers = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [HeaderKeys.FullTypeName] = "Foo.UnregisteredType",
            [HeaderKeys.ResponseMessageId] = Guid.NewGuid().ToString(),
        };

        var result = await dispatcher.DispatchAsync(
            new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }),
            "Foo.UnregisteredType",
            headers,
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(result.Success);
        Assert.True(result.NotHandled);
    }

    private static MessageDispatcher BuildDispatcher(IMessageTypeRegistry registry, IReplyProcessor? replyProcessor = null)
    {
        // [Adapt to the actual MessageDispatcher constructor shape — read the existing tests
        //  in MessageDispatcherTests.cs for the canonical pattern.]
        throw new NotImplementedException("Implementer copies the harness from MessageDispatcherTests.cs");
    }
}
```

The implementer fills in `BuildDispatcher` from the existing test arrange shape. Existing tests in `MessageDispatcherTests.cs` do this work — copy their helper.

- [ ] **Step 3: Run the tests pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageDispatcherUnresolvedTypeTests" -m:1
```

Expected: 2/2 FAIL — the existing code returns `Success=false`, the test asserts `Success=true && NotHandled=true`.

- [ ] **Step 4: Apply the C9 fix at site 1**

In `src/ServiceConnect/Services/MessageDispatcher.cs` around line 117-126:

```csharp
// before
if (!typeResolvedFromRegistry)
{
    if (!hasResponseMessageId)
    {
        _logger.LogWarning("Unregistered message type '{TypeName}'. Rejecting", fullTypeName);
        return new ConsumeEventResult { Success = false };
    }

    type = typeof(Message);
}

// after
if (!typeResolvedFromRegistry)
{
    if (!hasResponseMessageId)
    {
        // Unregistered type is a TERMINAL failure — retrying never resolves it.
        // Reuse the existing not-handled path so the consumer host either dead-letters
        // (when DeadLetterUnhandledMessages is enabled) or ack-and-drops, instead of
        // burning the full retry budget through Success=false.
        _logger.LogWarning("Unregistered message type '{TypeName}'. Routing as not-handled.", fullTypeName);
        return new ConsumeEventResult { Success = true, NotHandled = true };
    }

    type = typeof(Message);
}
```

- [ ] **Step 5: Apply the C9 fix at site 2**

In the same file around line 147-150:

```csharp
// before
if (!typeResolvedFromRegistry)
{
    return new ConsumeEventResult { Success = false };
}

// after
if (!typeResolvedFromRegistry)
{
    // Same rationale as the earlier unresolved-type branch: terminal failure, route
    // as not-handled rather than driving nack/requeue → retry → DLQ.
    return new ConsumeEventResult { Success = true, NotHandled = true };
}
```

- [ ] **Step 6: Run tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageDispatcher" -m:1
```

Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.UnitTests/MessageDispatcherUnresolvedTypeTests.cs \
        src/ServiceConnect/Services/MessageDispatcher.cs
git commit -m "fix(dispatcher): unresolved type → not-handled (no retry-loop burn)"
```

---

## Task 8: H17 part 1 — Add `RequestSendCancelledException`

**Files:**
- Create: `src/ServiceConnect.Interfaces/Exceptions/RequestSendCancelledException.cs`.

This task lands the new public type alone for clean blame.

- [ ] **Step 1: Verify the existing exceptions directory**

```bash
ls src/ServiceConnect.Interfaces/Exceptions/
```

Expected: `RequestTimeoutException.cs`, `ServiceConnectException.cs`, etc. Read `RequestTimeoutException.cs` for the canonical exception pattern.

- [ ] **Step 2: Create the new exception type**

Create `src/ServiceConnect.Interfaces/Exceptions/RequestSendCancelledException.cs`:

```csharp
namespace ServiceConnect.Interfaces.Exceptions;

/// <summary>
/// Thrown by <see cref="IRequestReplyManager.SendRequestAsync{TRequest, TReply}"/>,
/// <see cref="IRequestReplyManager.SendRequestMultiAsync{TRequest, TReply}"/>, and
/// <see cref="IRequestReplyManager.PublishRequestAsync{TRequest, TReply}"/> when the
/// outbound send was cancelled before delivery — distinct from the caller's own
/// cancellation token firing (which surfaces as a plain <see cref="OperationCanceledException"/>)
/// and from a request timeout (which surfaces as <see cref="RequestTimeoutException"/>).
/// Inherits from <see cref="OperationCanceledException"/> so existing
/// <c>catch (OperationCanceledException)</c> handlers continue to catch it; callers can
/// catch this type specifically to react to send-layer failures.
/// </summary>
public sealed class RequestSendCancelledException(Guid messageId, string message)
    : OperationCanceledException(message)
{
    /// <summary>
    /// Gets the request id of the send that was cancelled.
    /// </summary>
    public Guid MessageId { get; } = messageId;
}
```

- [ ] **Step 3: Build to verify**

```bash
dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj -m:1
```

Expected: clean build, 0 errors, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Interfaces/Exceptions/RequestSendCancelledException.cs
git commit -m "feat(interfaces): add RequestSendCancelledException"
```

---

## Task 9: H18 + C10 — Single-lock state machine in RequestReplyManager

**Files:**
- Modify: `src/ServiceConnect/Services/RequestReplyManager.cs:314-380, :397-515`.
- Create: `src/ServiceConnect.UnitTests/RequestReplyManagerInFlightCounterTests.cs` (C10 regression).
- Create: `src/ServiceConnect.UnitTests/RequestReplyManagerCallbackReentrancyTests.cs` (H18 contract).

**Background.** Drop `SyncRoot`. Move the entire `TryProcessReply` body — deserialize, OnReply callback, close-action triggers — into an internal `RequestState.TryHandleReply` method that holds `_stateLock` for the whole critical section. C10's `EndReply` underflow is structurally fixed (no more `_inFlightReplies`).

The change is large enough to warrant careful step-by-step:

### Step 9.1: Write the failing C10 regression test

Create `src/ServiceConnect.UnitTests/RequestReplyManagerInFlightCounterTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

public sealed class RequestReplyManagerInFlightCounterTests
{
    [Fact]
    public async Task SendRequestMultiAsync_DuplicateRepliesAfterClose_DoesNotInvokeOnReplyAfterClose()
    {
        var serializer = new Mock<IMessageSerializer>();
        serializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<Type>()))
            .Returns((ReadOnlyMemory<byte> bytes, Type t) => Activator.CreateInstance(t)!);
        serializer.Setup(s => s.Serialize(It.IsAny<object>())).Returns(new byte[] { 1, 2, 3 });

        var pipeline = new Mock<ISendMessagePipeline>();

        var manager = new RequestReplyManager(serializer.Object, pipeline.Object);

        // [Use SendRequestMultiAsync with ExpectedReplyCount = 2.
        //  Send 4 replies concurrently — only the first 2 should be accepted.
        //  Track onReply invocations.]

        // The exact arrange depends on RequestReplyManager's API surface; preserve TaskCompletionSource
        // wiring so the test can assert deterministically. See RequestReplyManagerTests.cs for the
        // canonical helper.

        // After the test:
        //   Assert: onReply called exactly 2 times.
        //   Assert: TCS resolved (or threw) cleanly without spurious close-action firing.
    }
}
```

The implementer fleshes out the test using the existing `RequestReplyManagerTests.cs` arrange shape.

- [ ] **Step 2: Read the current state machine**

```bash
sed -n '314,520p' src/ServiceConnect/Services/RequestReplyManager.cs
```

Identify:
- `TryProcessReply` at line 314.
- `RequestState` private class at line 397.
- `SyncRoot`, `TryAcceptReply`, `EndReply`, `Close` members.

### Step 9.2: Add the new `TryHandleReply` method to `RequestState`

In `src/ServiceConnect/Services/RequestReplyManager.cs`, inside the `RequestState` class (after `Close` and before the closing brace), add:

```csharp
/// <summary>
/// Processes a deserialized reply under the state lock. Returns true if the reply was
/// accepted (state was open and reply-count budget allowed); false if rejected (state
/// already closed or reply budget exhausted). The user-supplied OnReply callback runs
/// under the state lock so concurrent replies cannot re-enter the callback.
/// </summary>
internal bool TryHandleReply(
    Func<Type, object> deserialize,
    out bool requestCompleted,
    out Action? completionWork)
{
    completionWork = null;
    requestCompleted = false;

    lock (_stateLock)
    {
        if (_closed)
        {
            return false;
        }

        // Replay TryAcceptReply's logic inline so we can guard everything with one lock.
        bool acceptedAndCompletes;
        if (ExpectedCount <= 0)
        {
            _hasAcceptedReplies = true;
            acceptedAndCompletes = false;
        }
        else
        {
            if (_remainingReplies <= 0) return false;
            _remainingReplies--;
            _hasAcceptedReplies = true;
            acceptedAndCompletes = (_remainingReplies == 0);
        }

        // Deserialize INSIDE the lock so a corrupted-payload exception can be
        // attributed to this reply without leaking partial state mutations.
        object reply;
        try
        {
            reply = deserialize(ReplyType);
        }
        catch (Exception ex)
        {
            _closed = true;
            requestCompleted = true;
            completionWork = () => Tcs.TrySetException(ex);
            return true;
        }

        if (OnReply is not null)
        {
            try
            {
                OnReply(reply);
            }
            catch (Exception ex)
            {
                _closed = true;
                requestCompleted = true;
                completionWork = () => Tcs.TrySetException(ex);
                return true;
            }

            if (acceptedAndCompletes)
            {
                _closed = true;
                requestCompleted = true;
                completionWork = () => Tcs.TrySetResult(null!);
            }
        }
        else
        {
            _closed = true;
            requestCompleted = true;
            completionWork = () => Tcs.TrySetResult(reply);
        }

        return true;
    }
}
```

### Step 9.3: Replace `TryProcessReply` to use `TryHandleReply`

In the same file, replace the `TryProcessReply` body (currently lines 314-380):

```csharp
public bool TryProcessReply(string messageId, ReadOnlyMemory<byte> messageBytes, Type type)
{
    if (!Guid.TryParse(messageId, out var requestId) || !_pendingRequests.TryGetValue(requestId, out var state))
    {
        return false;
    }

    if (!state.TryHandleReply(
        deserialize: replyType => _serializer.Deserialize(messageBytes, replyType),
        out var requestCompleted,
        out var completionWork))
    {
        return false;
    }

    if (requestCompleted)
    {
        _pendingRequests.TryRemove(requestId, out _);
    }

    completionWork?.Invoke();
    return true;
}
```

### Step 9.4: Remove now-unused `RequestState` members

From `RequestState`:
- Remove `public object SyncRoot { get; } = new();` (line 414).
- Remove `public bool TryAcceptReply(out bool completesRequest)` (lines 484-514).
- Remove `public void EndReply()` (lines 442-457).
- Remove `private int _inFlightReplies;` (no longer used).
- Remove `private Action? _pendingCloseAction;` (no longer used).

### Step 9.5: Simplify `Close` and make it `internal`

Replace the existing `Close` method:

```csharp
internal void Close(Action? onClose = null)
{
    Action? closeAction = null;
    lock (_stateLock)
    {
        if (_closed) return;
        _closed = true;
        closeAction = onClose;
    }
    closeAction?.Invoke();
}
```

The `_pendingCloseAction` queueing branch is gone — under the new locking model, `_inFlightReplies` is never positive at lock release.

### Step 9.6: Update callers of `Close`

The callers in `SendRequestAsync`/`SendRequestMultiAsync`/`PublishRequestAsync` invoke `state.Close(...)` from the linkedCts-token-register lambdas (lines 44, 132, 234). They continue to work — `Close` is still callable, just `internal` now. Verify nothing external calls `Close` (it was `public` before):

```bash
grep -rn "\.Close(" src/ServiceConnect/Services/RequestReplyManager.cs | grep -v "// "
```

All callers should be inside `RequestReplyManager`. If anything external used `state.Close` (unlikely; `RequestState` is `private sealed` inside `RequestReplyManager`), surface as a finding.

### Step 9.7: Write the H18 callback re-entrancy test

Create `src/ServiceConnect.UnitTests/RequestReplyManagerCallbackReentrancyTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

public sealed class RequestReplyManagerCallbackReentrancyTests
{
    [Fact]
    public async Task SendRequestMultiAsync_ConcurrentReplies_OnReplyNotInvokedConcurrently()
    {
        var serializer = new Mock<IMessageSerializer>();
        serializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<Type>()))
            .Returns((ReadOnlyMemory<byte> bytes, Type t) => Activator.CreateInstance(t)!);
        serializer.Setup(s => s.Serialize(It.IsAny<object>())).Returns(new byte[] { 1, 2, 3 });

        var pipeline = new Mock<ISendMessagePipeline>();
        var manager = new RequestReplyManager(serializer.Object, pipeline.Object);

        int concurrency = 0;
        int maxConcurrency = 0;

        // Send a request expecting 5 replies. The onReply callback tracks max concurrency.
        // [Build the request via SendRequestMultiAsync. Push 5 replies concurrently from
        //  multiple threads via TryProcessReply.]
        // Use Interlocked.Increment/Decrement on `concurrency` and update `maxConcurrency`
        // atomically.

        // Assert: maxConcurrency == 1. All 5 replies eventually accepted.
        Assert.Equal(1, maxConcurrency);
    }
}
```

Implementer fleshes this out using the existing test arrange harness.

### Step 9.8: Run tests pre-fix vs post-fix

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RequestReplyManager" -m:1
```

Expected post-fix: all pass (existing + 2 new tests). The pre-fix vs post-fix story is harder for these — the C10 underflow may not reproduce reliably without careful timing. Document in commit message.

### Step 9.9: Commit

```bash
git add src/ServiceConnect.UnitTests/RequestReplyManagerInFlightCounterTests.cs \
        src/ServiceConnect.UnitTests/RequestReplyManagerCallbackReentrancyTests.cs \
        src/ServiceConnect/Services/RequestReplyManager.cs
git commit -m "$(cat <<'EOF'
refactor(request-reply): single-lock state machine; drop SyncRoot

Drops RequestState.SyncRoot, TryAcceptReply, EndReply, _inFlightReplies, and
_pendingCloseAction. Introduces RequestState.TryHandleReply, which holds
_stateLock for the entire reply lifecycle (deserialize, OnReply callback,
close-action triggers). User-callback re-entrancy is now structurally
prevented by the single lock.

C10 falls out: _inFlightReplies underflow is structurally impossible — the
counter is gone.

Completion work (TCS continuations) runs OUTSIDE the lock to avoid pinning
the dispatch thread on slow continuations.
EOF
)"
```

---

## Task 10: H17 — Fail fast on send-time cancellation

**Files:**
- Modify: `src/ServiceConnect/Services/RequestReplyManager.cs:18-90, :92-201, :204-294` — three send paths.
- Create: `src/ServiceConnect.UnitTests/RequestReplyManagerSendCancelTests.cs`.

**Background.** Track `_sendCompleted` flag in each send path; throw `RequestSendCancelledException` (from Task 8) when the linked CTS fires before send completes AND the caller's token didn't fire.

### Step 10.1: Write the failing tests

Create `src/ServiceConnect.UnitTests/RequestReplyManagerSendCancelTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

public sealed class RequestReplyManagerSendCancelTests
{
    [Fact]
    public async Task SendRequestAsync_SendPipelineCancelled_NotByCallerToken_ThrowsRequestSendCancelled()
    {
        var serializer = new Mock<IMessageSerializer>();
        serializer.Setup(s => s.Serialize(It.IsAny<object>())).Returns(new byte[] { 1, 2, 3 });

        // The pipeline awaits a TCS that will be cancelled by the linked token (timeout)
        // but NOT by the caller's token.
        var sendBlock = new TaskCompletionSource<bool>();
        var pipeline = new Mock<ISendMessagePipeline>();
        pipeline
            .Setup(p => p.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Returns(async (SendContext ctx, CancellationToken ct) =>
            {
                await sendBlock.Task.WaitAsync(ct).ConfigureAwait(false);
            });

        var manager = new RequestReplyManager(serializer.Object, pipeline.Object);
        var options = new RequestOptions { Timeout = 200, EndPoint = "test-endpoint" };
        var headers = new Dictionary<string, string>();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<RequestSendCancelledException>(() =>
            manager.SendRequestAsync<TestMessage, TestReply>(new TestMessage(), headers, options));
        sw.Stop();

        // Pre-fix: caller waits the full 200ms timeout and observes RequestTimeoutException.
        // Post-fix: caller observes RequestSendCancelledException IMMEDIATELY (within ~50ms).
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Expected fail-fast (< 500ms); got {sw.ElapsedMilliseconds}ms.");
        Assert.NotEqual(Guid.Empty, ex.MessageId);
    }

    [Fact]
    public async Task SendRequestAsync_CallerTokenCancelled_StillThrowsOperationCanceled_NotRequestSendCancelled()
    {
        var serializer = new Mock<IMessageSerializer>();
        serializer.Setup(s => s.Serialize(It.IsAny<object>())).Returns(new byte[] { 1, 2, 3 });

        var sendBlock = new TaskCompletionSource<bool>();
        var pipeline = new Mock<ISendMessagePipeline>();
        pipeline
            .Setup(p => p.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Returns(async (SendContext ctx, CancellationToken ct) =>
            {
                await sendBlock.Task.WaitAsync(ct).ConfigureAwait(false);
            });

        var manager = new RequestReplyManager(serializer.Object, pipeline.Object);
        var options = new RequestOptions { Timeout = 60_000, EndPoint = "test-endpoint" };  // long timeout
        var headers = new Dictionary<string, string>();

        using var callerCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            manager.SendRequestAsync<TestMessage, TestReply>(new TestMessage(), headers, options, callerCts.Token));

        // The exception type is OperationCanceledException — NOT RequestSendCancelledException.
        // (RequestSendCancelledException derives from OCE, but the test should see the bare type.)
        Assert.IsNotType<RequestSendCancelledException>(ex);
    }

    [Fact]
    public async Task SendRequestAsync_TimeoutFires_AndSendCompletedFirst_ThrowsRequestTimeout()
    {
        var serializer = new Mock<IMessageSerializer>();
        serializer.Setup(s => s.Serialize(It.IsAny<object>())).Returns(new byte[] { 1, 2, 3 });

        // Pipeline completes immediately.
        var pipeline = new Mock<ISendMessagePipeline>();
        pipeline
            .Setup(p => p.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var manager = new RequestReplyManager(serializer.Object, pipeline.Object);
        var options = new RequestOptions { Timeout = 100, EndPoint = "test-endpoint" };
        var headers = new Dictionary<string, string>();

        // No reply ever arrives. Timeout fires.
        await Assert.ThrowsAsync<RequestTimeoutException>(() =>
            manager.SendRequestAsync<TestMessage, TestReply>(new TestMessage(), headers, options));
    }

    private sealed class TestMessage : Message { }
    private sealed class TestReply : Message { }
}
```

Mirror tests for `SendRequestMultiAsync` and `PublishRequestAsync` (same shape, different entry points).

### Step 10.2: Run tests pre-fix

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RequestReplyManagerSendCancelTests" -m:1
```

Expected: the FIRST test FAILS — pre-fix the swallowed OCE makes the caller wait the full 200ms timeout. The second and third tests should pass (caller-token cancellation already propagates correctly; timeout-after-send-completed already fires `RequestTimeoutException`).

### Step 10.3: Apply the H17 fix to `SendRequestAsync`

In `src/ServiceConnect/Services/RequestReplyManager.cs`, find `SendRequestAsync` (line 18). Add the `_sendCompleted` flag and update the catch block:

```csharp
public async Task<TReply> SendRequestAsync<TRequest, TReply>(
    TRequest message,
    IDictionary<string, string> headers,
    RequestOptions options,
    CancellationToken cancellationToken = default)
    where TRequest : Message
    where TReply : Message
{
    ValidateOptions(options);
    cancellationToken.ThrowIfCancellationRequested();

    var messageBytes = _serializer.Serialize(message);
    var messageId = Guid.NewGuid();
    var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
    var state = new RequestState(tcs, 1, typeof(TReply));
    _pendingRequests[messageId] = state;

    headers[HeaderKeys.RequestMessageId] = messageId.ToString();

    // Track whether the send pipeline ran to completion. If the linked CTS cancels the send
    // BEFORE this is set, the cancellation is from the timeout (not from the caller) and we
    // fail fast with RequestSendCancelledException rather than waiting on the TCS.
    var sendCompleted = 0;

    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    linkedCts.CancelAfter(options.Timeout);

    await using var reg = linkedCts.Token.Register(() =>
    {
        state.Close(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
            else
            {
                tcs.TrySetException(new RequestTimeoutException(messageId, TimeSpan.FromMilliseconds(options.Timeout)));
            }
        });
    }).ConfigureAwait(false);

    try
    {
        var endPoint = string.IsNullOrEmpty(options.EndPoint) ? null : options.EndPoint;
        var context = new SendContext
        {
            Message = message,
            MessageType = typeof(TRequest),
            MessageBytes = messageBytes,
            Headers = headers,
            EndPoint = endPoint,
            RoutingKey = null,
            Operation = SendOperation.Request,
        };
        await _sendPipeline.ExecuteSendMessagePipelineAsync(context, linkedCts.Token).ConfigureAwait(false);
        Interlocked.Exchange(ref sendCompleted, 1);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // Caller's own cancellation. Remove the pending request and let the OCE propagate.
        _pendingRequests.TryRemove(messageId, out _);
        throw;
    }
    catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && Volatile.Read(ref sendCompleted) == 0)
    {
        // Send-layer cancellation NOT caused by the caller's token. The send never completed.
        // Fail fast with a typed exception; do not wait on the TCS for the timeout register
        // to set RequestTimeoutException.
        _pendingRequests.TryRemove(messageId, out _);
        throw new RequestSendCancelledException(messageId,
            $"Request {messageId} send pipeline was cancelled before delivery.");
    }
    catch
    {
        _pendingRequests.TryRemove(messageId, out _);
        throw;
    }

    try
    {
        var result = await tcs.Task.ConfigureAwait(false);
        return (TReply)result;
    }
    finally
    {
        _pendingRequests.TryRemove(messageId, out _);
    }
}
```

Note the `using ServiceConnect.Interfaces.Exceptions;` import at the top of the file may need to be added. Check first.

### Step 10.4: Apply the H17 fix to `SendRequestMultiAsync`

Same pattern (line 92). Set `Interlocked.Exchange(ref sendCompleted, 1)` AFTER the entire `foreach` loop completes:

```csharp
public async Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(...)
{
    // ... existing setup ...
    var sendCompleted = 0;

    // ... linkedCts + register block unchanged ...

    try
    {
        if (options.EndPoints is { Count: > 0 })
        {
            foreach (string endPoint in options.EndPoints)
            {
                var context = new SendContext { /* ... */ };
                await _sendPipeline.ExecuteSendMessagePipelineAsync(context, linkedCts.Token).ConfigureAwait(false);
            }
        }
        else
        {
            // ... single-endpoint path ...
        }
        Interlocked.Exchange(ref sendCompleted, 1);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        _pendingRequests.TryRemove(messageId, out _);
        throw;
    }
    catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && Volatile.Read(ref sendCompleted) == 0)
    {
        _pendingRequests.TryRemove(messageId, out _);
        throw new RequestSendCancelledException(messageId,
            $"Request {messageId} send pipeline was cancelled before delivery.");
    }
    catch
    {
        _pendingRequests.TryRemove(messageId, out _);
        throw;
    }

    // ... rest unchanged ...
}
```

### Step 10.5: Apply the H17 fix to `PublishRequestAsync`

`PublishRequestAsync` (line 204) already has a `publishCompletedSuccessfully` flag (line 222). Reuse it as the `sendCompleted` equivalent. Update the catch block to add the new typed-cancellation case:

```csharp
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    _pendingRequests.TryRemove(messageId, out _);
    throw;
}
catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && Volatile.Read(ref publishCompletedSuccessfully) == 0)
{
    _pendingRequests.TryRemove(messageId, out _);
    throw new RequestSendCancelledException(messageId,
        $"Publish {messageId} send pipeline was cancelled before delivery.");
}
catch
{
    _pendingRequests.TryRemove(messageId, out _);
    throw;
}
```

### Step 10.6: Update XML docs on `IRequestReplyManager`

Find the XML docs in `src/ServiceConnect.Interfaces/Bus/IRequestReplyManager.cs` (or wherever the interface lives). Add the new exception to the three method docs:

```xml
/// <exception cref="RequestSendCancelledException">
/// Thrown when the outbound send pipeline cancelled before the request reached the broker.
/// Distinct from a timeout (<see cref="RequestTimeoutException"/>) and from caller-token
/// cancellation (<see cref="OperationCanceledException"/>).
/// </exception>
```

### Step 10.7: Run tests to verify pass

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RequestReply" -m:1
```

Expected: all pass.

### Step 10.8: Commit

```bash
git add src/ServiceConnect.UnitTests/RequestReplyManagerSendCancelTests.cs \
        src/ServiceConnect/Services/RequestReplyManager.cs \
        src/ServiceConnect.Interfaces/Bus/IRequestReplyManager.cs
git commit -m "fix(request-reply): fail fast on send-time cancellation"
```

(Adjust the file list if `IRequestReplyManager.cs` doesn't exist — the XML docs may be on the interface in a different file.)

---

## Task 11: Smaller — Tighten `RequestReplyManager` XML doc

**Files:**
- Modify: `src/ServiceConnect/Services/RequestReplyManager.cs:39-55`.

The XML doc on `SendRequestAsync` says the registration is "synchronously disposed" when the timeout fires — actually `await using var reg = ...` disposes asynchronously.

- [ ] **Step 1: Read the current doc**

```bash
sed -n '17,55p' src/ServiceConnect/Services/RequestReplyManager.cs
```

Identify the imprecise wording.

- [ ] **Step 2: Apply the fix**

Update the affected XML comments. Example wording to use:

```csharp
/// <summary>
/// Sends a request and asynchronously waits for a single reply. Returns the reply payload
/// when one matches the request id; throws <see cref="RequestTimeoutException"/> if the
/// configured timeout elapses; throws <see cref="OperationCanceledException"/> if the caller
/// cancels the supplied token; throws <see cref="RequestSendCancelledException"/> if the
/// outbound send pipeline cancels before delivery. The internal cancellation registration
/// is asynchronously disposed when the request completes (success, timeout, or cancellation),
/// matching the <c>await using</c> pattern in the implementation.
/// </summary>
```

Adjust to match the existing comment shape; just remove "synchronously" and replace with "asynchronously" + the matching await-using framing.

- [ ] **Step 3: Build and run tests**

```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RequestReply" -m:1
```

Expected: clean.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect/Services/RequestReplyManager.cs
git commit -m "docs(request-reply): tighten dispose-synchrony XML doc"
```

---

## Task 12: H15 + H16 — Remove transport `DisposeAsync` from Bus

**Files:**
- Modify: `src/ServiceConnect/Bus.cs:533, :544, :549, :590` — remove `_consumer.DisposeAsync()` and `_producer.DisposeAsync()` calls; simplify `StopConsumingCoreAsync` and `Bus.DisposeAsync`.
- Create: `src/ServiceConnect.UnitTests/BusTransportLifecycleTests.cs`.

**Background.** DI registers `IConsumer`/`IProducer` as `TryAddSingleton`. The host's `IServiceProvider` disposes them on host shutdown — the Bus's `DisposeAsync` calls are double-disposing. Removing them simplifies `StopConsumingCoreAsync` (H14 falls out: no orphan-task concern) and `Bus.DisposeAsync` (H16 disappears: no `WaitAsync` timeout to mask).

### Step 12.1: Write the failing tests

Create `src/ServiceConnect.UnitTests/BusTransportLifecycleTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

public sealed class BusTransportLifecycleTests
{
    [Fact]
    public async Task DisposeAsync_DoesNotDisposeIConsumer()
    {
        var consumer = new Mock<IConsumer>();
        consumer.SetupGet(c => c.IsConnected).Returns(true);
        consumer.SetupGet(c => c.IsCancelledByBroker).Returns(false);
        // Track DisposeAsync invocations.
        int disposeCount = 0;
        consumer.Setup(c => c.DisposeAsync())
            .Callback(() => Interlocked.Increment(ref disposeCount))
            .Returns(ValueTask.CompletedTask);

        var bus = BuildBus(consumer.Object, producer: null);

        await bus.StartConsumingAsync();
        await bus.DisposeAsync();

        Assert.Equal(0, disposeCount);
    }

    [Fact]
    public async Task DisposeAsync_DoesNotDisposeIProducer()
    {
        var producer = new Mock<IProducer>();
        int disposeCount = 0;
        producer.Setup(p => p.DisposeAsync())
            .Callback(() => Interlocked.Increment(ref disposeCount))
            .Returns(ValueTask.CompletedTask);

        var bus = BuildBus(consumer: null, producer.Object);

        await bus.DisposeAsync();

        Assert.Equal(0, disposeCount);
    }

    [Fact]
    public async Task StopConsumingAsync_DoesNotDisposeIConsumer()
    {
        var consumer = new Mock<IConsumer>();
        consumer.SetupGet(c => c.IsConnected).Returns(true);
        consumer.SetupGet(c => c.IsCancelledByBroker).Returns(false);
        int disposeCount = 0;
        consumer.Setup(c => c.DisposeAsync())
            .Callback(() => Interlocked.Increment(ref disposeCount))
            .Returns(ValueTask.CompletedTask);

        var bus = BuildBus(consumer.Object, producer: null);

        await bus.StartConsumingAsync();
        await bus.StopConsumingAsync();

        Assert.Equal(0, disposeCount);
    }

    private static IBus BuildBus(IConsumer? consumer, IProducer? producer)
    {
        // [Adapt to the canonical Bus arrange shape from BusTests.cs.]
        throw new NotImplementedException("Implementer copies the harness from BusTests.cs");
    }
}
```

The implementer fleshes out `BuildBus` from the existing `BusTests.cs` arrange harness.

### Step 12.2: Run tests pre-fix

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~BusTransportLifecycleTests" -m:1
```

Expected: all 3 FAIL — pre-fix, Bus calls `DisposeAsync` on the transports.

### Step 12.3: Simplify `Bus.StopConsumingCoreAsync`

In `src/ServiceConnect/Bus.cs`, replace the entire `StopConsumingCoreAsync` method (currently lines 484-576) with:

```csharp
private async Task StopConsumingCoreAsync(CancellationToken cancellationToken = default)
{
    bool semaphoreAcquired = false;
    try
    {
        await _lifecycleSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        semaphoreAcquired = true;

        lock (_stateLock)
        {
            _logger.LogInformation("Bus stopping message consumption.");
            if (_consuming)
            {
                _consuming = false;
                _stopped = true;
            }
        }
        // _consumer.DisposeAsync() is NOT called here. The DI container disposes the IConsumer
        // singleton on host shutdown. Bus.IsConsuming is now false; the consumer host's own
        // dispose semantics drain in-flight messages until DI tears it down.
    }
    catch (ObjectDisposedException)
    {
        throw new ObjectDisposedException(typeof(Bus).FullName);
    }
    finally
    {
        if (semaphoreAcquired)
        {
            try { _lifecycleSemaphore.Release(); }
            catch (ObjectDisposedException) { }
        }
    }
}
```

### Step 12.4: Simplify `Bus.DisposeAsync`

Replace `Bus.DisposeAsync` (currently lines 579-594):

```csharp
public async ValueTask DisposeAsync()
{
    if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

    // Stop consuming under the lifecycle semaphore. _consumer and _producer are DI singletons;
    // the host's IServiceProvider disposes them when the host shuts down — Bus.DisposeAsync
    // does not double-dispose them.
    try { await StopConsumingCoreAsync().ConfigureAwait(false); }
    catch (Exception ex) { _logger.LogWarning(ex, "Bus.StopConsumingCoreAsync failed during dispose."); }

    await _sendPipeline.DisposeAsync().ConfigureAwait(false);

    // Note: _lifecycleSemaphore is intentionally NOT Disposed here (see Task 14 for the
    // rationale; the dispose call is currently still present but will be removed in Task 14).
    _lifecycleSemaphore.Dispose();
}
```

The `_lifecycleSemaphore.Dispose()` removal lands in Task 14. For now, keep it.

### Step 12.5: Run tests to verify pass

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Bus" -m:1
```

Expected: all pass — both new tests + any pre-existing Bus tests. **Note**: pre-existing tests that asserted the Bus disposes the transport may now fail. Surface any such test changes; they were locking in the pre-fix bug.

### Step 12.6: Commit

```bash
git add src/ServiceConnect.UnitTests/BusTransportLifecycleTests.cs \
        src/ServiceConnect/Bus.cs
git commit -m "$(cat <<'EOF'
refactor(bus): remove transport DisposeAsync; rely on DI

DI registers IConsumer and IProducer as TryAddSingleton; the host's
IServiceProvider disposes them on host shutdown. Bus.DisposeAsync was
double-disposing them. Removing the calls simplifies StopConsumingCoreAsync
(H14: no orphan-task concern), eliminates the WaitAsync(_disposeTimeout)
timeout-mask path (H16), and aligns the Bus with v7's DI-owns-transports
architecture.

Pre-existing tests that asserted the Bus disposes the transport were
updated to assert the new contract (no dispose).
EOF
)"
```

---

## Task 13: H14 — Bus.StopConsumingCoreAsync rethrows OCE without mutating state

**Files:**
- Verify: `src/ServiceConnect/Bus.cs:484-...` (post-Task-12 simplified shape).
- Create: `src/ServiceConnect.UnitTests/BusLifecycleCancellationTests.cs`.

**Background.** Task 12's H15 fix already simplified `StopConsumingCoreAsync` to a clean shape. H14's "mutate state without semaphore" is now structurally fixed (the only path is via the semaphore). This task verifies and adds a regression test.

- [ ] **Step 1: Verify the post-Task-12 shape**

```bash
grep -A 30 "private async Task StopConsumingCoreAsync" src/ServiceConnect/Bus.cs
```

Confirm the OCE from `WaitAsync` propagates naturally (no `try/catch (OperationCanceledException)` swallow, no `pendingCancellation` field). If Task 12's diff matches the spec, no production-code change is needed for H14.

- [ ] **Step 2: Write the regression test**

Create `src/ServiceConnect.UnitTests/BusLifecycleCancellationTests.cs`:

```csharp
using System.Reflection;
using Moq;
using ServiceConnect;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

public sealed class BusLifecycleCancellationTests
{
    [Fact]
    public async Task StopConsumingAsync_TokenCancelledBeforeSemaphoreAcquired_ThrowsOceWithoutMutatingState()
    {
        var bus = BuildBus();  // canonical helper from BusTests.cs
        await bus.StartConsumingAsync();

        // Acquire _lifecycleSemaphore from another thread so the next StopConsumingAsync waits.
        var semField = typeof(Bus).GetField("_lifecycleSemaphore",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var sem = (SemaphoreSlim)semField!.GetValue(bus)!;
        await sem.WaitAsync();

        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();  // cancel BEFORE the wait completes

            await Assert.ThrowsAsync<OperationCanceledException>(() => bus.StopConsumingAsync(cts.Token));

            // _consuming is still true (no mutation occurred under cancellation).
            Assert.True(bus.IsConsuming);  // pre-fix: might be false; post-fix: still true
        }
        finally
        {
            sem.Release();
        }

        // A subsequent uncontested StopConsumingAsync succeeds.
        await bus.StopConsumingAsync();
        Assert.False(bus.IsConsuming);
    }

    private static IBus BuildBus()
    {
        // [Canonical Bus arrange harness — adapt from BusTests.cs.]
        throw new NotImplementedException("Implementer copies from BusTests.cs");
    }
}
```

- [ ] **Step 3: Run the test**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~BusLifecycleCancellationTests" -m:1
```

Expected post-Task-12: PASS (the simplified `StopConsumingCoreAsync` shape correctly rethrows OCE without mutating state).

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.UnitTests/BusLifecycleCancellationTests.cs
git commit -m "fix(bus): rethrow OCE without mutating state under cancellation"
```

If a production-code change is needed (Task 12 didn't fully simplify), surface it and add the change to this commit.

---

## Task 14: Smaller — Keep `_lifecycleSemaphore` under GC; mirror Connection pattern

**Files:**
- Modify: `src/ServiceConnect/Bus.cs:594` (remove `_lifecycleSemaphore.Dispose()` and add comment).

Mirrors Phase 6's H2 fix on `Connection.cs` and Phase 4's H22 fix on `ProducerConnection.cs`.

- [ ] **Step 1: Read the current DisposeAsync**

```bash
grep -B 1 -A 20 "public async ValueTask DisposeAsync" src/ServiceConnect/Bus.cs
```

Locate the `_lifecycleSemaphore.Dispose()` call.

- [ ] **Step 2: Replace the dispose call with the rationale comment**

In `src/ServiceConnect/Bus.cs`:

```csharp
// before
await _sendPipeline.DisposeAsync().ConfigureAwait(false);

// Note: _lifecycleSemaphore is intentionally NOT Disposed here (...).
_lifecycleSemaphore.Dispose();

// after
await _sendPipeline.DisposeAsync().ConfigureAwait(false);

// _lifecycleSemaphore is intentionally NOT Disposed:
// SemaphoreSlim.Dispose only releases the lazily-allocated WaitHandle, and we never call
// AvailableWaitHandle, so disposal is a functional no-op. A concurrent caller's Release()
// on a disposed semaphore would throw ObjectDisposedException out of the unwind path,
// which we cannot prevent without holding GC references to every caller. Mirrors the
// Connection / ProducerConnection / Producer pattern (Phases 4 + 6).
```

- [ ] **Step 3: Run tests to verify no regression**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Bus" -m:1
```

Expected: all pass. If any test asserted the semaphore was disposed (locking in the pre-fix bug), invert it to assert the post-fix contract.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect/Bus.cs
git commit -m "refactor(bus): keep _lifecycleSemaphore under GC; mirror Connection pattern"
```

---

## Task 15: H19 + H20 — Routing-slip validation

**Files:**
- Modify: `src/ServiceConnect/Bus.cs:312-356` (`RouteAsync`) and `:680-700` (`BuildRoutingSlip`).
- Create: `src/ServiceConnect.UnitTests/BusRouteValidationTests.cs`.

### Step 15.1: Write the failing tests

Create `src/ServiceConnect.UnitTests/BusRouteValidationTests.cs`:

```csharp
using ServiceConnect;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests;

public sealed class BusRouteValidationTests
{
    public static IEnumerable<object?[]> InvalidDestinations() => new[]
    {
        new object?[] { (string[]?)null,                "destinations" },
        new object?[] { Array.Empty<string>(),          "at least one destination" },
        new object?[] { new[] { "" },                   "null or whitespace" },
        new object?[] { new[] { "  " },                 "null or whitespace" },
        new object?[] { new string?[] { "ok", null },   "null or whitespace" },
        new object?[] { new[] { "ok", "with,comma" },   "comma" },
    };

    [Theory]
    [MemberData(nameof(InvalidDestinations))]
    public async Task RouteAsync_InvalidDestinations_ThrowsArgumentException(
        string[]? destinations, string expectedMessageFragment)
    {
        var bus = BuildBus();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            bus.RouteAsync(new TestMessage(), destinations!));

        Assert.Contains(expectedMessageFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RouteAsync_ValidDestinations_Succeeds()
    {
        var bus = BuildBus();
        await bus.RouteAsync(new TestMessage(), new[] { "q1", "q2", "q3" });
        // No exception → success.
    }

    private static IBus BuildBus()
    {
        // [Canonical Bus arrange harness with a no-op send pipeline.]
        throw new NotImplementedException("Implementer copies from BusTests.cs");
    }

    private sealed class TestMessage : Message { }
}
```

### Step 15.2: Run the tests pre-fix

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~BusRouteValidationTests" -m:1
```

Expected: most FAIL — the current code only checks `destinations == null || destinations.Count == 0`. Empty-string, whitespace, mid-list-null, and comma-containing destinations all pass through.

### Step 15.3: Apply the H19 fix to `RouteAsync`

In `src/ServiceConnect/Bus.cs`, replace the validation block at the top of `RouteAsync`:

```csharp
public async Task RouteAsync<T>(T message, IList<string> destinations, CancellationToken cancellationToken = default) where T : Message
{
    ThrowIfDisposed();
    cancellationToken.ThrowIfCancellationRequested();
    ArgumentNullException.ThrowIfNull(message);
    ArgumentNullException.ThrowIfNull(destinations);

    // Snapshot to defend against caller mutation between validation and use.
    var snapshot = destinations.ToArray();
    if (snapshot.Length == 0)
    {
        throw new ArgumentException(
            "RouteAsync requires at least one destination.",
            nameof(destinations));
    }
    for (int i = 0; i < snapshot.Length; i++)
    {
        if (string.IsNullOrWhiteSpace(snapshot[i]))
        {
            throw new ArgumentException(
                $"Destination at index {i} is null or whitespace; routing requires a non-empty queue name.",
                nameof(destinations));
        }
        if (snapshot[i].Contains(','))
        {
            throw new ArgumentException(
                $"Destination at index {i} contains a comma ('{snapshot[i]}'); commas are reserved as the routing-slip separator.",
                nameof(destinations));
        }
    }

    var firstDestination = snapshot[0];
    var messageBytes = _serializer.Serialize(message);
    Dictionary<string, string> headers;

    if (_hasOutgoingFilters)
    {
        var envelope = CreateEnvelope(messageBytes, message.CorrelationId);
        if (await RunOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false) == FilterAction.Stop)
        {
            return;
        }

        headers = ExtractHeaders(envelope);
    }
    else
    {
        headers = BuildHeadersDirect(message.CorrelationId, null);
    }

    if (snapshot.Length > 1)
    {
        headers[HeaderKeys.RoutingSlip] = BuildRoutingSlip(snapshot);
    }

    var context = new SendContext
    {
        Message = message,
        MessageType = typeof(T),
        MessageBytes = messageBytes,
        Headers = headers,
        EndPoint = firstDestination,
        RoutingKey = null,
        Operation = SendOperation.Send,
    };
    await _sendPipeline.ExecuteSendMessagePipelineAsync(context, cancellationToken).ConfigureAwait(false);
}
```

The crucial changes: (1) added `ArgumentNullException.ThrowIfNull(message)`/`destinations`; (2) snapshot to an array; (3) per-element validation; (4) use `snapshot` everywhere (replaces `destinations`).

### Step 15.4: Apply the H20 fix to `BuildRoutingSlip`

In the same file:

```csharp
// before
private static string BuildRoutingSlip(IList<string> destinations)
{
    if (destinations.Count <= 1)
    {
        return string.Empty;
    }
    return string.Join(',', destinations.Skip(1));
}

// after
private static string BuildRoutingSlip(IList<string> destinations)
{
    if (destinations.Count <= 1)
    {
        return string.Empty;
    }

    // RouteAsync's caller-validation already screened these, but BuildRoutingSlip is
    // also reachable from internal paths (RoutingSlipProcessor); revalidate for defence
    // in depth. The comma split is non-recoverable on the receiving side.
    for (int i = 0; i < destinations.Count; i++)
    {
        if (string.IsNullOrWhiteSpace(destinations[i]))
        {
            throw new ArgumentException(
                $"Destination at index {i} is null or whitespace.",
                nameof(destinations));
        }
        if (destinations[i].Contains(','))
        {
            throw new ArgumentException(
                $"Destination at index {i} contains a comma; commas are reserved as the routing-slip separator.",
                nameof(destinations));
        }
    }

    return string.Join(',', destinations.Skip(1));
}
```

### Step 15.5: Run tests to verify pass

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~BusRoute|FullyQualifiedName~RoutingSlip" -m:1
```

Expected: all pass.

### Step 15.6: Commit

```bash
git add src/ServiceConnect.UnitTests/BusRouteValidationTests.cs \
        src/ServiceConnect/Bus.cs
git commit -m "fix(bus): validate routing-slip destinations"
```

---

## Task 16: Documentation — Phase 7 release notes

**Files:**
- Modify: `website/src/content/docs/releases.mdx`.

- [ ] **Step 1: Locate the existing v7 release-notes structure**

```bash
grep -n "Phase\|RabbitMQ\|consumer-host hardening\|Connection-lifecycle hygiene" website/src/content/docs/releases.mdx | head
```

- [ ] **Step 2: Insert the Phase 7 section as a sibling**

Add this section. Match the existing chronological ordering (Phase 4 → 5 → 6 → 7).

```mdx
### Bus, dispatcher, and request-reply correctness

**Bug fixes**

- **Unresolved message types no longer drive retry loops.** `MessageDispatcher` previously returned `Success=false` when the inbound type was not in the registry, causing the consumer host to nack-with-requeue → exhaust the retry budget → land in the error queue. Unresolved types are terminal — retrying never resolves them. They now route via the existing not-handled path: dead-letter when `DeadLetterUnhandledMessages` is enabled, otherwise ack-and-drop.
- **Request-reply state-machine race.** The `_inFlightReplies` counter could underflow when concurrent or duplicate replies arrived after a request closed, causing the close action to fire spuriously or leak. The state machine has been redesigned around a single internal lock; the underflow is structurally impossible.
- **Send-time cancellation distinguishable from timeout.** When the request-reply send pipeline cancels before the request reaches the broker, callers now receive `RequestSendCancelledException` immediately rather than waiting the full timeout. Inherits from `OperationCanceledException` so existing `catch (OperationCanceledException)` handlers continue to work.
- **Bus stops disposing transport singletons.** `Bus.DisposeAsync` and `Bus.StopConsumingCoreAsync` no longer call `IConsumer.DisposeAsync` / `IProducer.DisposeAsync`. The DI container owns those singletons and disposes them on host shutdown; the previous double-dispose path was harmless but architecturally wrong.
- **Bus lifecycle cancellation hardened.** `Bus.StopConsumingCoreAsync` now propagates cancellation cleanly without mutating lifecycle state under partial-acquisition.
- **Routing-slip validation.** `IBus.RouteAsync` and `BuildRoutingSlip` now throw `ArgumentException` for null, empty/whitespace, or comma-containing destinations. Pre-v7.x these inputs silently corrupted the routing slip or demoted the route to a single-destination send.
- **Internal hygiene.** `Bus._lifecycleSemaphore` no longer disposed (mirrors Connection pattern); `Bus.CreateStream<T>` validates endpoint; `QueueConfiguration.QueueMappings` caches the wrapper instead of allocating per access; `SendMessagePipeline` lifetime validation now covers middleware registered directly via DI.

**Behaviour changes**

- `IBus.RouteAsync(destinations: ["", "queue-2"])` previously routed silently (or corrupted the slip); now throws `ArgumentException`. Operators relying on the old lenient behaviour should validate destinations at the call site.
- Request-reply callers can now catch `RequestSendCancelledException` to react specifically to send-layer cancellation. Existing `catch (OperationCanceledException)` handlers continue to catch it (the new type derives from `OperationCanceledException`).
- `IConsumer` / `IProducer` lifetime is now solely owned by the DI container. External implementations that previously expected `Bus.DisposeAsync` to call their `DisposeAsync` should register them as singletons in DI (or verify the DI container disposes them).
```

- [ ] **Step 3: Build the website**

```bash
npm --prefix website run build
```

Expected: clean.

- [ ] **Step 4: Commit**

```bash
git add website/src/content/docs/releases.mdx
git commit -m "docs(website): phase 07 release notes"
```

---

## Task 17: Documentation — API reference + learn updates

**Files:**
- Modify: `website/src/content/docs/reference/bus/ibus.mdx` — `RouteAsync` and `SendRequestAsync` / `SendRequestMultiAsync` / `PublishRequestAsync`.
- Modify: `website/src/content/docs/learn/messaging-patterns/routing-slip.mdx`.
- Modify: `website/src/content/docs/learn/messaging-patterns/request-reply.mdx`.
- Modify: `website/src/content/docs/learn/operations/error-handling.mdx`.

- [ ] **Step 1: Locate the existing reference pages**

```bash
ls website/src/content/docs/reference/bus/
ls website/src/content/docs/learn/messaging-patterns/
grep -rln "RouteAsync\|SendRequestAsync\|RequestSendCancelled\|RoutingSlip\|DeadLetterUnhandledMessages" website/src/content/docs/ | head -20
```

- [ ] **Step 2: Update `ibus.mdx`**

Find the `RouteAsync` entry. Add an `Aside` (or matching style) noting the validation:

```mdx
:::caution
Throws `ArgumentException` if `destinations` is null, empty, or contains any null/whitespace/comma-containing entries. Commas are reserved as the routing-slip separator.
:::
```

For `SendRequestAsync`/`SendRequestMultiAsync`/`PublishRequestAsync`, add the new exception:

```mdx
**Exceptions**

- `RequestTimeoutException` — request-side timeout fired before a matching reply arrived.
- `RequestSendCancelledException` — the outbound send pipeline cancelled before the request reached the broker. Distinct from caller-token cancellation (`OperationCanceledException`).
- `OperationCanceledException` — the caller's cancellation token fired.
```

- [ ] **Step 3: Update `routing-slip.mdx`**

Add a "What changed in v7" callout:

```mdx
:::note
v7 tightens routing-slip validation: `IBus.RouteAsync` now throws `ArgumentException` if any destination is null, empty/whitespace, or contains a comma. Pre-v7.x these inputs silently corrupted the slip or demoted the route to a single send.
:::
```

- [ ] **Step 4: Update `request-reply.mdx`**

Add a "What changed in v7" callout:

```mdx
:::note
v7 introduces `RequestSendCancelledException` (inheriting from `OperationCanceledException`) thrown by `SendRequestAsync` / `SendRequestMultiAsync` / `PublishRequestAsync` when the outbound send pipeline cancels before the request reaches the broker. Distinct from a timeout (`RequestTimeoutException`) and from caller-token cancellation (`OperationCanceledException`).
:::
```

- [ ] **Step 5: Update `error-handling.mdx`**

Add a paragraph on what happens to messages with unregistered types:

```mdx
### Unregistered message types

When a message arrives with a type that isn't in the dispatch registry, ServiceConnect treats it as a **terminal failure** — retrying never resolves the type. The message is routed via the not-handled path:

- If `DeadLetterUnhandledMessages` is enabled, the message goes directly to the error queue.
- Otherwise the message is acked and dropped.

Pre-v7.x ServiceConnect returned `Success=false` from the dispatcher, causing the consumer host to nack-with-requeue and burn the full retry budget before landing the message in the error queue. The new behaviour is faster and more accurate.
```

- [ ] **Step 6: Build the website**

```bash
npm --prefix website run build
```

Expected: clean. No new broken-link warnings.

- [ ] **Step 7: Examples / READMEs verification**

```bash
grep -rn "RouteAsync\|SendRequestAsync\|RequestSendCancelled\|RoutingSlip" examples/ README.md 2>/dev/null | grep -v "obj/\|bin/" | head
```

If any sample passes null/empty destinations or relies on `Success=false` for unregistered types, update in place.

- [ ] **Step 8: Commit**

```bash
git add website/src/content/docs/reference/bus/ibus.mdx \
        website/src/content/docs/learn/messaging-patterns/routing-slip.mdx \
        website/src/content/docs/learn/messaging-patterns/request-reply.mdx \
        website/src/content/docs/learn/operations/error-handling.mdx
# Also add examples/ updates if any were turned up.
git commit -m "docs(website): API reference + learn updates for routing-slip validation and RequestSendCancelledException"
```

---

## Task 18: Final verification gate + code review

This task is mechanical. Runs all verification commands the spec calls out before final code review.

- [ ] **Step 1: Per-csproj builds clean**

```bash
dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj -m:1
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1
```

Expected: all succeed, 0 errors, 0 warnings.

- [ ] **Step 2: Unit-test pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~Bus|FullyQualifiedName~Dispatcher|FullyQualifiedName~RequestReply|FullyQualifiedName~SendMessagePipeline|FullyQualifiedName~QueueConfiguration|FullyQualifiedName~RoutingSlip" \
    -m:1
```

Expected: all pass.

- [ ] **Step 3: Astro build**

```bash
npm --prefix website run build
```

Expected: clean.

- [ ] **Step 4: Grep verifications**

```bash
# C9: Success=false in MessageDispatcher only on the legitimate handler-thrown path
grep -n "Success = false" src/ServiceConnect/Services/MessageDispatcher.cs

# H15: Bus no longer disposes transports
grep -n "_consumer\.DisposeAsync\|_producer\.DisposeAsync" src/ServiceConnect/Bus.cs
# Expected: zero hits.

# H18: SyncRoot gone
grep -n "SyncRoot" src/ServiceConnect/Services/RequestReplyManager.cs
# Expected: zero hits.

# Smaller — _lifecycleSemaphore not disposed
grep -n "_lifecycleSemaphore\.Dispose" src/ServiceConnect/Bus.cs
# Expected: zero hits.

# H17: RequestSendCancelledException present
grep -rn "RequestSendCancelledException" src/ServiceConnect.Interfaces/ src/ServiceConnect/
# Expected: at least 4 hits (declaration + 3 throw sites).
```

- [ ] **Step 5: Final code review**

Dispatch `superpowers:code-reviewer` (model: opus) over all Phase 7 commits (from `98fb1969` — the spec commit — through HEAD). Use the briefing pattern from previous phases:

```
Review Phase 7 commits (98fb1969..HEAD) against
docs/superpowers/specs/2026-04-29-phase-07-bus-dispatcher-requestreply.md.

Focus on:
- C9: unresolved type now routes via NotHandled; existing tests still cover handler-thrown
  exceptions returning Success=false (the legitimate Success=false path).
- C10 + H18: single-lock state machine; verify no remaining SyncRoot reference;
  verify the user callback runs under the lock; verify completion work runs OFF the lock.
- H17: RequestSendCancelledException derives from OperationCanceledException; existing
  catch (OperationCanceledException) handlers still catch it; new specific catches work.
- H14 + H15 + H16: Bus no longer disposes transports; H14's "mutate state without
  semaphore" disappears as a result.
- H19 + H20: routing-slip validation throws ArgumentException; existing routing-slip tests
  still pass.

Verify the smaller items: BuildHeadersDirect capacity, Bus.CreateStream<T> endpoint
validation, _lifecycleSemaphore not disposed, QueueConfiguration.QueueMappings cached,
SendMessagePipeline middleware lifetime validation expanded.

Flag any test that locks in pre-fix behaviour, any race condition the spec did not
anticipate (especially in the new RequestState.TryHandleReply lock semantics), and any
public-API surface change beyond RequestSendCancelledException.
```

- [ ] **Step 6: Cleanup commit (only if review surfaced issues)**

If Steps 4 or 5 found anything, fix in a follow-up commit:

```bash
git add <only-cleanup-files>
git commit -m "cleanup(phase-07): address final-review findings"
```

If nothing needed, skip — Phase 7 is done.

---

## Phase 7 done

All 14 findings closed. The phase ships:
- C9 unresolved-type routing fix.
- C10 in-flight counter underflow (structurally fixed by H18).
- H17 `RequestSendCancelledException` for fail-fast send-time cancellation.
- H18 single-lock state machine (drops `SyncRoot`).
- H14 + H15 + H16 Bus lifecycle cleanup (transports owned by DI).
- H19 + H20 routing-slip validation.
- Plus 5 smaller polish items.

Move to writing the closing summary; the user's standard pattern is "phase complete; continue to phase N+1?".
