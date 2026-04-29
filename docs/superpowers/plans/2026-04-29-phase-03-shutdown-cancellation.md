# Phase 03 — Shutdown / cancellation propagation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply a uniform cancellation discipline across `ServiceConnect` (core) and `ServiceConnect.Client.RabbitMQ`. Eight fixes (C8 + H12 + H13 + H23 + M2 + M5×2 + M13 + smaller) plus a single new website page documenting the cancellation contract for users writing filters / middleware.

**Architecture:** Three rules — (1) tokens propagate to every transitively-awaited call inside a method that accepts one; (2) catch blocks wrapping cancellable awaits include `catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }` ahead of generic catches; (3) dispose / teardown / observability publishes are deliberately fire-and-forget and run with `CancellationToken.None`. Each fix narrowly applies one of these rules at the listed site. M5 adds a typed catch for `RabbitMQ.Client.Exceptions.AlreadyClosedException` and `BrokerUnreachableException` so transient transport failures rethrow (broker redelivers) instead of silently dropping the message.

**Tech Stack:** .NET (multi-target net8.0/net10.0), xUnit + Moq for unit tests, `RabbitMQ.Client` for the consumer/producer plumbing, Astro/Starlight for the website docs page. Test runner: `dotnet test` with `--filter` (per-csproj only — see [build/test safety](#buildtest-safety) below).

**Spec:** [`docs/superpowers/specs/2026-04-29-phase-03-shutdown-cancellation.md`](../specs/2026-04-29-phase-03-shutdown-cancellation.md).

---

## Build/test safety

This machine has crashed when running unconstrained whole-solution `dotnet build` / `dotnet test` against this codebase (CLAUDE.md has the full incident analysis). The wrapper at `~/.local/bin/dotnet` re-execs every `dotnet` invocation under a systemd cgroup with `CPUQuota=800%`, `MemoryMax=8G`, `MemorySwapMax=0`, `TasksMax=200` and is the safety net. **Even with the wrapper, every command in this plan is per-csproj.** Add `-m:1` to `dotnet test` invocations to serialize MSBuild and stay within `TasksMax`. Never run whole-solution `dotnet build` / `dotnet test` / `dotnet format`.

---

## File structure

### Modified — production code

- `src/ServiceConnect/Services/MessageDispatcher.cs` — C8 OCE filter ahead of the catch at line 170.
- `src/ServiceConnect/Services/MessageBusWriteStream.cs` — H12 (line 195 token propagation) + H13 (widen try/catch from line 102 onwards).
- `src/ServiceConnect/Services/ProcessManagerTimeoutService.cs` — smaller (line 132 token propagation).
- `src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs` — H23 bounded wait + signature change to take `CancellationToken`.
- `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs` — M2 audit OCE filter + M5×2 transport-class discriminator at both retry-publish and terminal-failure-publish sites.
- `src/ServiceConnect.Client.RabbitMQ/Consumer/Retry.cs` — M13 OCE filter on `exceptionAction` callback.

### Modified — tests

- `src/ServiceConnect.UnitTests/MessageDispatcherTests.cs` — C8.
- `src/ServiceConnect.UnitTests/MessageBusWriteStreamTests.cs` — H12, H13.
- `src/ServiceConnect.UnitTests/Services/ProcessManagerTimeoutServiceTests.cs` — smaller.
- `src/ServiceConnect.UnitTests/RabbitMQ/MessageAuditPublisherCancellationTests.cs` (or sibling) — M2.
- `src/ServiceConnect.UnitTests/RabbitMQ/RetryOperationCanceledTests.cs` — M13.
- New: `src/ServiceConnect.UnitTests/RabbitMQ/ProducerConnectionDisposeTests.cs` — H23.
- New: `src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorTransportTests.cs` — M5×2.

### Modified — website + sidebar

- `website/src/content/docs/learn/operations/cancellation.mdx` — new page (the discipline doc).
- `website/astro.config.mjs` — sidebar entry for the new page, placed under `Operations` after `Idempotency`.

---

## Task 1: Cancellation-discipline website page

**Files:**
- Create: `website/src/content/docs/learn/operations/cancellation.mdx`
- Modify: `website/astro.config.mjs`

This task lands first so subsequent per-fix commits can reference the discipline page in code comments.

- [ ] **Step 1: Create the cancellation page**

Create `website/src/content/docs/learn/operations/cancellation.mdx` with:

```markdown
---
title: Cancellation
description: How ServiceConnect handles graceful shutdown and the cancellation contract for filter / middleware authors.
---

import { Aside } from '@astrojs/starlight/components';

ServiceConnect honours the host's shutdown token end-to-end. When `IHostApplicationLifetime` signals shutdown, the consumer host stops admitting new deliveries, waits for in-flight handlers to complete, and propagates the cancellation token to every async boundary inside the dispatch pipeline.

## What the bus promises

- The handler's `CancellationToken` flows from the consumer host's lifecycle. When shutdown begins, the dispatcher rethrows `OperationCanceledException` from the catch path; the broker leaves the message unacked for redelivery on the next start.
- After-consuming filters still run in the dispatcher's `finally` block on every path — even when the handler threw OCE during shutdown.
- Audit publishes and telemetry header injection are fire-and-forget — they don't block shutdown and don't surface OCE as application errors.

## What your code must do

Three rules:

### 1. Token propagation

Methods that accept a `CancellationToken cancellationToken` parameter must propagate it to every transitively-awaited call inside the method body. No `default`. No parameterless overloads.

```csharp
// ❌ Wrong — drops the token on the downstream await.
public async Task HandleAsync(MyMessage msg, CancellationToken cancellationToken = default)
{
    await _http.GetAsync("https://api.example.com/data");  // No token!
}

// ✅ Correct — forwards the token.
public async Task HandleAsync(MyMessage msg, CancellationToken cancellationToken = default)
{
    await _http.GetAsync("https://api.example.com/data", cancellationToken);
}
```

### 2. OCE filter discipline

Catch blocks that wrap a cancellable operation must include an OCE filter ahead of any generic `catch (Exception)`:

```csharp
// ❌ Wrong — turns shutdown into an application error.
public async Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
{
    try
    {
        await DoSomethingAsync(cancellationToken);
        return FilterAction.Continue;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Filter failed");
        return FilterAction.Stop;
    }
}

// ✅ Correct — shutdown propagates; application errors are caught.
public async Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
{
    try
    {
        await DoSomethingAsync(cancellationToken);
        return FilterAction.Continue;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Filter failed");
        return FilterAction.Stop;
    }
}
```

### 3. Fire-and-forget cleanup whitelist

Dispose / teardown paths and observability publishes (`DisposeAsync`, `CloseAsync` of resources we're tearing down, audit publish, telemetry inject) are deliberately not cancellable. They run with `CancellationToken.None` (or no token) so cancellation can't leave a half-disposed resource.

OCE handlers in these paths log at Debug, not Error — an OCE is expected in this regime, not an error.

The bus's whitelist (you don't extend this in your own code):

- `IBus.DisposeAsync` and the consumer/producer disposal cascade.
- Audit publish (handler succeeded; audit is observability metadata).
- Telemetry trace-context injection.

If you find yourself wanting a similar fire-and-forget block in your own code, your code probably has a cancellation bug.

## Worked example: middleware

```csharp
public sealed class MyMiddleware : IMessageProcessingMiddleware
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
        try
        {
            // Pre-handler work uses the token.
            await BeforeAsync(message, cancellationToken);

            // Forward the token to next.
            var result = await next(messageBytes, messageType, message, headers, envelope, cancellationToken);

            // Post-handler work uses the token.
            await AfterAsync(result, cancellationToken);

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cooperative shutdown — propagate so the dispatcher's outer finally still runs
            // AfterConsumingFilters but the message stays unacked for redelivery.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Middleware failed");
            throw;
        }
    }
}
```

## See also

- [`IFilter`](/ServiceConnect-CSharp/reference/filters/ifilter/) — pipeline contract.
- [`IMessageProcessingMiddleware`](/ServiceConnect-CSharp/reference/filters/imessageprocessingmiddleware/) — handler-wrapping middleware contract.
- [Hosting & Lifecycle](/ServiceConnect-CSharp/learn/operations/hosting/) — how the host coordinates shutdown.
```

- [ ] **Step 2: Add sidebar entry**

In `website/astro.config.mjs`, in the `Operations` items array (currently lines ~64-72), add a new entry after `Idempotency`:

```js
{ label: 'Cancellation', link: '/learn/operations/cancellation/' },
```

The full block becomes (inserting one line):

```js
            {
              label: 'Operations',
              items: [
                { label: 'Configuration', link: '/learn/operations/configuration/' },
                { label: 'Hosting & Lifecycle', link: '/learn/operations/hosting/' },
                { label: 'Error Handling', link: '/learn/operations/error-handling/' },
                { label: 'Idempotency', link: '/learn/operations/idempotency/' },
                { label: 'Cancellation', link: '/learn/operations/cancellation/' },
                { label: 'Observability', link: '/learn/operations/observability/' },
              ],
            },
```

- [ ] **Step 3: Build the website**

```bash
npm --prefix website install
npm --prefix website run build
```

Expected: succeeds. The new page should appear in the build output. No new broken-link warnings.

- [ ] **Step 4: Commit**

```bash
git add website/src/content/docs/learn/operations/cancellation.mdx \
        website/astro.config.mjs
git commit -m "docs(website): document cancellation contract for filter / middleware authors"
```

---

## Task 2: C8 — `MessageDispatcher` shutdown OCE rethrow

**Files:**
- Modify: `src/ServiceConnect/Services/MessageDispatcher.cs:170-182`
- Modify: `src/ServiceConnect.UnitTests/MessageDispatcherTests.cs`

- [ ] **Step 1: Inspect the existing dispatcher test fixture**

```bash
grep -nE "throwOnHandle|CreateDispatcher|TestDispatchHandler" src/ServiceConnect.UnitTests/MessageDispatcherTests.cs | head -20
```

Identify the canonical "handler throws" arrange shape (the `TestDispatchHandler` already supports `throwOnHandle: someException`). Reuse it.

- [ ] **Step 2: Write the failing test**

Append to the `MessageDispatcherTests` class:

```csharp
    [Fact]
    public async Task DispatchAsync_HandlerThrowsOceDuringShutdown_PropagatesOce_AndDoesNotInvokeExceptionHandler()
    {
        var thrown = new OperationCanceledException("co-op shutdown");

        var handler = new TestDispatchHandler(throwOnHandle: thrown);
        // [Reuse the same arrange block used by an existing 'handler throws' test
        // such as Dispatch_HandlerThrows_ReturnsFailure — copy it verbatim, then
        // pre-cancel the cancellationToken so the OCE filter ahead of the catch
        // can fire when the handler re-throws under the cancelled token.]

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var exceptionHandlerInvocations = 0;
        var config = new BusConfiguration
        {
            ExceptionHandler = _ => exceptionHandlerInvocations++,
        };

        // ... build dispatcher exactly like the existing handler-throws test, but pass
        // `config` through and use `cts.Token` as the cancellation argument ...

        var thrownFromDispatch = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            dispatcher.DispatchAsync(messageBytes, "FakeMessage1", headers, cts.Token).AsTask());

        Assert.Same(thrown, thrownFromDispatch);
        Assert.Equal(0, exceptionHandlerInvocations);

        // The existing finally still runs AfterConsumingFilters once.
        _mockFilterPipeline.Verify(
            f => f.ExecuteAfterConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
```

The implementer copies the canonical happy-path / handler-throws arrange block verbatim from an existing test in the same fixture. Don't invent a new harness shape — `MessageDispatcherTests.cs` is large; reuse what's there.

- [ ] **Step 3: Run test to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~DispatchAsync_HandlerThrowsOceDuringShutdown_PropagatesOce_AndDoesNotInvokeExceptionHandler" -m:1
```

Expected: FAIL — today the catch swallows the OCE, returns `Success=false` with the OCE attached, and the assertion that the OCE escapes fails.

- [ ] **Step 4: Apply the C8 fix**

In `src/ServiceConnect/Services/MessageDispatcher.cs` at lines 170-182, insert an OCE filter before the existing `catch (Exception ex)`:

```csharp
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cooperative shutdown — propagate so the outer finally leaves the message unacked
            // for broker redelivery on next start. Not an application error.
            // See learn/operations/cancellation for the full contract.
            throw;
        }
        catch (Exception ex)
        {
            // ... existing block unchanged ...
        }
```

The existing `finally` block (currently at line 184) is unchanged.

- [ ] **Step 5: Run test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~DispatchAsync_HandlerThrowsOceDuringShutdown_PropagatesOce_AndDoesNotInvokeExceptionHandler" -m:1
```

Expected: 1 passed. Re-run the broader filter to confirm no regression:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageDispatcher" -m:1
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect/Services/MessageDispatcher.cs \
        src/ServiceConnect.UnitTests/MessageDispatcherTests.cs
git commit -m "fix(dispatcher): shutdown OCE rethrows ahead of generic catch"
```

---

## Task 3: H12 — `MessageBusWriteStream.CloseAsync` token propagation

**Files:**
- Modify: `src/ServiceConnect/Services/MessageBusWriteStream.cs:195`
- Modify: `src/ServiceConnect.UnitTests/MessageBusWriteStreamTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `MessageBusWriteStreamTests`:

```csharp
    [Fact]
    public async Task CloseAsync_TokenCancelledDuringClosePacketSend_PropagatesOce()
    {
        var sendStarted = new TaskCompletionSource();
        var sendBlock = new TaskCompletionSource();

        var producer = new Mock<IProducer>();
        // Drain check passes (no in-flight writes), so CloseAsync proceeds to the close-packet send.
        producer
            .Setup(p => p.SendBytesAsync(
                It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<byte[]>(),
                It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Returns(async (string ep, Type t, byte[] body, IDictionary<string, string> h, CancellationToken ct) =>
            {
                sendStarted.SetResult();
                await sendBlock.Task.WaitAsync(ct).ConfigureAwait(false);
            });

        var stream = new MessageBusWriteStream(producer.Object, "queue", typeof(string));
        var cts = new CancellationTokenSource();

        var closeTask = stream.CloseAsync(cts.Token);
        await sendStarted.Task;

        cts.Cancel();
        sendBlock.SetException(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(() => closeTask);
    }
```

- [ ] **Step 2: Run test to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~CloseAsync_TokenCancelledDuringClosePacketSend_PropagatesOce" -m:1
```

Expected: FAIL — today line 195 invokes `SendBytesAsync` without forwarding the token, so the cancellation doesn't propagate (the test will hang or time out before the OCE assertion).

- [ ] **Step 3: Apply the H12 fix**

In `src/ServiceConnect/Services/MessageBusWriteStream.cs` line 195, change:

```csharp
        await _producer.SendBytesAsync(_endpoint, _messageType, [], headers).ConfigureAwait(false);
```

to:

```csharp
        await _producer.SendBytesAsync(_endpoint, _messageType, [], headers, cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 4: Run test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageBusWriteStream" -m:1
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/MessageBusWriteStream.cs \
        src/ServiceConnect.UnitTests/MessageBusWriteStreamTests.cs
git commit -m "fix(stream): pass cancellationToken to close-packet send"
```

---

## Task 4: H13 — Widen `WriteAsync` fault-flag wrap

**Files:**
- Modify: `src/ServiceConnect/Services/MessageBusWriteStream.cs:98-130`
- Modify: `src/ServiceConnect.UnitTests/MessageBusWriteStreamTests.cs`

- [ ] **Step 1: Write the failing test**

The defect window is between `Interlocked.Increment` (line 101) and the existing `try {` (line 114) — the header dictionary allocation/copy at lines 103-113. To exercise this, simulate a synchronous throw in the producer's `SendBytesAsync` setup that fires *before* the existing try would catch (we use a guard on `IDictionary<string, string>` access to inject the throw).

A pragmatic test that asserts the broader invariant — *any throw in the post-Increment region sets `_faulted`* — is sufficient. We'll use a producer that asserts on the headers dictionary (which is constructed inside the post-Increment region) and have it throw synchronously inside the call.

```csharp
    [Fact]
    public async Task WriteAsync_HeaderAllocationOrSendThrow_SetsFaultedFlag()
    {
        var producer = new Mock<IProducer>();
        // Throw synchronously regardless of the headers — simulating any throw between
        // Interlocked.Increment and the await. With the fix, this throw is now wrapped
        // and Volatile.Write(ref _faulted, 1) fires before the throw escapes.
        var sendException = new InvalidOperationException("simulated post-increment throw");
        producer
            .Setup(p => p.SendBytesAsync(
                It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<byte[]>(),
                It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(sendException);

        var stream = new MessageBusWriteStream(producer.Object, "queue", typeof(string));

        // First write fails — assertion: the faulted flag is set, future writes refuse.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stream.WriteAsync(new byte[] { 1, 2, 3 }, 0, 3, CancellationToken.None));

        // Second write should now refuse with the "stream faulted" message.
        var secondAttempt = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stream.WriteAsync(new byte[] { 4, 5, 6 }, 0, 3, CancellationToken.None));
        Assert.Contains("faulted", secondAttempt.Message, StringComparison.OrdinalIgnoreCase);
    }
```

This test passes today (the existing send-failure path already sets `_faulted`). To prove the **window** is closed, we need a test that exercises a throw INSIDE the header-allocation region. Since synchronously throwing during a `Dictionary<>.Add` is hard without instrumentation, the practical assertion is the regression test above — it lock in the contract that "any throw from `WriteAsync` after `Increment` sets `_faulted`," and the implementer's structural change (widening the try) is what brings the header-allocation region under that contract. The plan acknowledges this is a structural fix without a direct allocator-throw test.

If the implementer can devise a more targeted test (e.g., inject a custom `_baseHeaders` whose enumeration throws), they may add it; otherwise the structural assertion above plus careful code review of the widened try is the bound.

- [ ] **Step 2: Run the test (already passing pre-fix)**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~WriteAsync_HeaderAllocationOrSendThrow_SetsFaultedFlag" -m:1
```

Expected: PASS (the existing narrow try already covers send-failure; the test locks in the contract).

- [ ] **Step 3: Apply the H13 fix — widen the try/catch**

In `src/ServiceConnect/Services/MessageBusWriteStream.cs`, modify `WriteAsync` (lines 98-126). Move the `try {` to immediately after `Interlocked.Increment`, so the dictionary allocation + copy + key-set are all covered:

```csharp
            var packet = new byte[count];
            Array.Copy(buffer, offset, packet, 0, count);

            var packetNum = Interlocked.Increment(ref _packetNumber) - 1;

            try
            {
                // Pre-size the dict to avoid rehash during the copy. A separate dict
                // per packet is required because the producer may mutate / enqueue the
                // dictionary asynchronously, so reuse would race with concurrent writes.
                var headers = new Dictionary<string, string>(_baseHeaders.Count + 1, StringComparer.Ordinal);
                foreach (var kvp in _baseHeaders)
                {
                    headers[kvp.Key] = kvp.Value;
                }

                headers[HeaderKeys.PacketNumber] = FormatInt64(packetNum);

                await _producer.SendBytesAsync(_endpoint, _messageType, packet, headers, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The reserved packet number is now stranded — there is no safe way for the
                // caller to retry without producing a permanent gap, so refuse all further
                // writes. The exception still propagates so the caller learns the send failed.
                // See learn/operations/cancellation: any throw between Increment and SendBytesAsync,
                // including OOM during dict alloc, must trigger the fault flag.
                Volatile.Write(ref _faulted, 1);
                throw;
            }
```

The change is structural — the existing catch body and `Volatile.Write(ref _faulted, 1)` line are unchanged; only the try's opening brace moves up.

- [ ] **Step 4: Run tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageBusWriteStream" -m:1
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/MessageBusWriteStream.cs \
        src/ServiceConnect.UnitTests/MessageBusWriteStreamTests.cs
git commit -m "fix(stream): widen WriteAsync fault-flag wrap to cover header allocation"
```

---

## Task 5: Smaller — `ProcessManagerTimeoutService` token propagation

**Files:**
- Modify: `src/ServiceConnect/Services/ProcessManagerTimeoutService.cs:132`
- Modify: `src/ServiceConnect.UnitTests/Services/ProcessManagerTimeoutServiceTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `ProcessManagerTimeoutServiceTests`:

```csharp
    [Fact]
    public async Task PollOnceAsync_RemoveDispatchedTimeout_ReceivesPropagatedCancellationToken()
    {
        // Arrange — minimal poll that produces one due timeout, dispatches it, and removes.
        // Reuse the existing fixture's mock setup for _bus and _finder; configure _finder
        // to capture the CancellationToken argument observed by RemoveDispatchedTimeoutAsync.

        CancellationToken? observedRemoveToken = null;

        // [Reuse the existing fixture's _finder mock — configure it to:
        //  - Return one due TimeoutData from GetTimeoutsBatchAsync.
        //  - Capture the CT supplied to RemoveDispatchedTimeoutAsync into observedRemoveToken.
        //  See existing tests in the same file for the canonical setup.]

        using var cts = new CancellationTokenSource();
        // Don't cancel — just want to verify the supplied token is the SAME token PollOnceAsync received.

        await /* invoke PollOnceAsync via the existing test seam, passing cts.Token */;

        Assert.NotNull(observedRemoveToken);
        Assert.Equal(cts.Token, observedRemoveToken.Value);
    }
```

The implementer reuses the existing fixture's mock setup verbatim; the new assertion is the captured-token equality.

- [ ] **Step 2: Run test to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~PollOnceAsync_RemoveDispatchedTimeout_ReceivesPropagatedCancellationToken" -m:1
```

Expected: FAIL — today line 132 passes `CancellationToken.None`, so the assertion `Assert.Equal(cts.Token, observedRemoveToken.Value)` fails (`CancellationToken.None != cts.Token`).

- [ ] **Step 3: Apply the smaller fix**

In `src/ServiceConnect/Services/ProcessManagerTimeoutService.cs` line 132, change:

```csharp
                    await _finder.RemoveDispatchedTimeoutAsync(timeout.Id, lockOwner, CancellationToken.None).ConfigureAwait(false);
```

to:

```csharp
                    await _finder.RemoveDispatchedTimeoutAsync(timeout.Id, lockOwner, cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 4: Run tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ProcessManagerTimeoutService" -m:1
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/ProcessManagerTimeoutService.cs \
        src/ServiceConnect.UnitTests/Services/ProcessManagerTimeoutServiceTests.cs
git commit -m "fix(timeout-service): propagate cancellation token to RemoveDispatchedTimeoutAsync"
```

---

## Task 6: H23 — `DisposeConnectionAsync` bounded wait

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs:228-239`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs:134` (caller)
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/ProducerConnectionDisposeTests.cs`

- [ ] **Step 1: Inspect existing producer-test fixture conventions**

```bash
ls src/ServiceConnect.UnitTests/RabbitMQ/Producer*Tests.cs
head -40 src/ServiceConnect.UnitTests/RabbitMQ/ProducerDisposeTests.cs
```

Note the constructor pattern, the way `ProducerConnection` is constructed in tests, and any helper for fake / mock connections.

- [ ] **Step 2: Write the failing test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/ProducerConnectionDisposeTests.cs`:

```csharp
using System.Diagnostics;
using ServiceConnect.Client.RabbitMQ;
// Adjust the using imports to match the patterns in sibling tests.
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class ProducerConnectionDisposeTests
{
    [Fact]
    public async Task ReconnectAsync_CancellationTokenCancelled_DisposeCompletesWithinBoundedTime()
    {
        // Arrange — construct a ProducerConnection where the connection semaphore is held
        // (e.g., have a background task acquire it before the test invokes ReconnectAsync).
        // The test asserts: ReconnectAsync's call to DisposeConnectionAsync completes within
        // the 30s budget OR the 2x window — never blocks indefinitely.

        var producerConnection = /* construct via the same helper used by ProducerDisposeTests */;

        // Acquire the semaphore in a background task and hold it for longer than the budget
        // would allow — but cancel the test's token to prove the cancellation path also returns
        // promptly.
        using var semaphoreHeld = new ManualResetEventSlim(false);
        using var releaseSemaphore = new ManualResetEventSlim(false);
        var holderTask = Task.Run(async () =>
        {
            // [Use reflection or a test-only accessor to acquire _connectionSemaphore, mark
            //  semaphoreHeld, then wait on releaseSemaphore before releasing. The fixture
            //  may already expose this — check sibling test files for the canonical pattern.]
        });

        semaphoreHeld.Wait(TimeSpan.FromSeconds(2));

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));   // Cancel quickly.

        var sw = Stopwatch.StartNew();
        // Invoke ReconnectAsync via a public path; it routes through DisposeConnectionAsync.
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            /* call the public method that triggers ReconnectAsync(cts.Token) */);
        sw.Stop();

        // Released after the test completes so the semaphore-holder task can exit cleanly.
        releaseSemaphore.Set();
        await holderTask;

        // The dispose path under cancellation must return quickly — well within the 30s ceiling.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"DisposeConnectionAsync took {sw.Elapsed} — should have returned within ~50ms after cancellation.");
    }
}
```

The implementer adapts the test's "construct producer connection" and "acquire semaphore in background" pieces to match the fixture conventions in `ProducerDisposeTests.cs` and sibling files. The shape of the assertion (cancellation returns within bounded time) is the load-bearing piece.

- [ ] **Step 3: Run test to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ReconnectAsync_CancellationTokenCancelled_DisposeCompletesWithinBoundedTime" -m:1
```

Expected: FAIL or HANG — today the unbounded `WaitAsync` blocks forever; the test's stopwatch assertion would either time out (if xUnit kills the test) or report ~30s+.

- [ ] **Step 4: Apply the H23 fix**

In `src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs`:

**4a:** change the `DisposeConnectionAsync` signature to take a token and use a bounded wait:

```csharp
    private async Task DisposeConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionLockAcquired = false;
        try
        {
            // Bound the wait: a wedged in-flight (re)connect cannot stall this dispose.
            // Token honours the publish path's cancellation; 30s ceiling keeps callers that
            // pass a never-cancelled token from blocking indefinitely.
            // See learn/operations/cancellation for the discipline.
            connectionLockAcquired = await _connectionSemaphore
                .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
            if (!connectionLockAcquired)
            {
                _logger.LogWarning(
                    "ProducerConnection.DisposeConnectionAsync timed out waiting for the connection semaphore; forcing teardown.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(
                "ProducerConnection.DisposeConnectionAsync cancelled while waiting for the semaphore; forcing teardown.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ProducerConnection.DisposeConnectionAsync semaphore-wait failed; forcing teardown");
        }
        finally
        {
            // Best-effort teardown ALWAYS runs, whether or not we held the lock — matches CloseAsync.
            try { await TearDownChannelAndConnectionAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "ProducerConnection teardown after dispose failed"); }

            if (connectionLockAcquired)
            {
                _connectionSemaphore.Release();
            }
        }
    }
```

**4b:** Update the single caller in `ReconnectAsync` (line 134) to pass the token:

```csharp
        await DisposeConnectionAsync(cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 5: Run test + regression**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Producer" -m:1
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/ProducerConnectionDisposeTests.cs
git commit -m "fix(producer-connection): bound DisposeConnectionAsync wait + propagate cancellation token"
```

---

## Task 7: M2 — Audit-publish OCE filter

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs:198-205`
- Modify: `src/ServiceConnect.UnitTests/RabbitMQ/MessageAuditPublisherCancellationTests.cs` (or sibling) — see Step 1.

- [ ] **Step 1: Inspect existing audit-publish test surface**

```bash
head -60 src/ServiceConnect.UnitTests/RabbitMQ/MessageAuditPublisherCancellationTests.cs
```

The existing file probably tests `MessageAuditPublisher` directly. The M2 fix is in `InboundMessageProcessor`, which calls `_auditPublisher.PublishAuditIfEnabledAsync(...)` and catches the result. To unit-test the M2 fix, we exercise `InboundMessageProcessor.ProcessAsync` with an audit publisher that throws OCE during shutdown.

If `InboundMessageProcessor` is `internal`, the test project already has `InternalsVisibleTo` (Phase 2 tests like `TelemetryProcessingMiddleware` test internals). Verify with:

```bash
grep -rn "InternalsVisibleTo" src/ServiceConnect.Client.RabbitMQ/
```

- [ ] **Step 2: Write the failing test**

The test exercises the audit-publish branch (Success=true, NotHandled=false, errors not disabled). The audit publisher throws OCE while the shutdown token is cancelled. Assert: the OCE propagates out of `ProcessAsync` (so the caller's outer finally leaves the message unacked) AND no Error log is emitted (Debug instead).

Append to `MessageAuditPublisherCancellationTests.cs` (or create a new file under `RabbitMQ/`):

```csharp
    [Fact]
    public async Task ProcessAsync_AuditPublishOceDuringShutdown_PropagatesOceAndLogsAtDebug()
    {
        // Arrange — InboundMessageProcessor configured for the audit branch:
        //  - _consumerEventHandler returns Success=true, NotHandled=false.
        //  - errors not disabled (so audit publish is invoked).
        //  - shutdownTimedOut returns false.
        //  - shutdownPublishToken returns a cancelled token (simulating shutdown grace expired).
        //  - _auditPublisher.PublishAuditIfEnabledAsync throws OperationCanceledException.

        var shutdownCts = new CancellationTokenSource();
        shutdownCts.Cancel();

        var capturingLogger = new RecordingLogger();   // simple ILogger that buckets by level

        // [Construct InboundMessageProcessor with the above setup. Reuse the existing fixture's
        //  helper if one exists; otherwise build the constructor arguments directly. The
        //  delegate parameters (shutdownTimedOut, shutdownPublishToken) are the load-bearing
        //  setup — point them at lambdas that capture the local state.]

        var processor = /* ... */;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            processor.ProcessAsync(/* args */));

        // No Error log — the OCE during shutdown should be logged at Debug.
        Assert.Empty(capturingLogger.GetEntriesAtLevel(LogLevel.Error));
        Assert.Single(capturingLogger.GetEntriesAtLevel(LogLevel.Debug)
            .Where(e => e.Message.Contains("Audit publish cancelled by shutdown")));
    }
```

The `RecordingLogger` is a small helper for the test fixture — if a sibling test already declares one, reuse it; otherwise add a private nested class.

- [ ] **Step 3: Run test to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ProcessAsync_AuditPublishOceDuringShutdown_PropagatesOceAndLogsAtDebug" -m:1
```

Expected: FAIL — today the catch swallows the OCE and logs Error.

- [ ] **Step 4: Apply the M2 fix**

In `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs` lines 198-205, replace the existing catch:

```csharp
            try
            {
                await _auditPublisher.PublishAuditIfEnabledAsync(publishChannel, args, headers, shutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                // Audit is fire-and-forget; shutdown cancellation is expected, not an error.
                // See learn/operations/cancellation: dispose / observability paths log Debug, not Error.
                _logger.LogDebug(
                    "Audit publish cancelled by shutdown for delivery {DeliveryTag}; continuing to ack the original message",
                    args.DeliveryTag);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish audit message for delivery {DeliveryTag}; continuing to ack the original message", args.DeliveryTag);
            }
```

Note the OCE branch logs at Debug AND `throw`s — propagating the OCE so the outer caller's finally leaves the message unacked. The original code swallowed the OCE; the fix surfaces it.

- [ ] **Step 5: Run tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageAuditPublisher|FullyQualifiedName~InboundMessageProcessor" -m:1
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/MessageAuditPublisherCancellationTests.cs
git commit -m "fix(consumer): audit-publish OCE filter ahead of generic catch"
```

(Adjust the `git add` path if the test was added to a new file under `RabbitMQ/` — name it `InboundMessageProcessorAuditCancellationTests.cs`.)

---

## Task 8: M5a — Retry-publish transport-class discriminator

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs:120-145`
- Create or extend: `src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorTransportTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorTransportTests.cs`:

```csharp
using RabbitMQ.Client.Exceptions;
using ServiceConnect.Client.RabbitMQ;
using Xunit;
// Adjust imports to match sibling RabbitMQ tests.

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class InboundMessageProcessorTransportTests
{
    [Fact]
    public async Task ProcessAsync_RetryPublishThrowsAlreadyClosed_RethrowsForBrokerRedelivery()
    {
        // Arrange — InboundMessageProcessor for the retry-publish branch:
        //  - _consumerEventHandler returns Success=false (handler failed).
        //  - shutdownTimedOut returns false.
        //  - _retryHandler.HandleFailureAsync throws AlreadyClosedException.

        var transportException = new AlreadyClosedException(new global::RabbitMQ.Client.ShutdownEventArgs(
            global::RabbitMQ.Client.ShutdownInitiator.Peer, 0, "test"));

        var processor = /* construct with the above setup */;

        var thrown = await Assert.ThrowsAsync<AlreadyClosedException>(() =>
            processor.ProcessAsync(/* args */));

        Assert.Same(transportException, thrown);
    }

    [Fact]
    public async Task ProcessAsync_RetryPublishThrowsBrokerUnreachable_RethrowsForBrokerRedelivery()
    {
        var transportException = new BrokerUnreachableException(new Exception("inner"));

        var processor = /* same setup, BrokerUnreachableException instead */;

        var thrown = await Assert.ThrowsAsync<BrokerUnreachableException>(() =>
            processor.ProcessAsync(/* args */));

        Assert.Same(transportException, thrown);
    }

    [Fact]
    public async Task ProcessAsync_RetryPublishThrowsPoisonException_SwallowsAndAcks()
    {
        // The poison-message-style exception (anything not transport-class, not OCE) keeps
        // the existing swallow-and-ack behaviour to prevent hot-loop.
        var poisonException = new InvalidOperationException("poison message");

        var processor = /* construct with retryHandler throwing poisonException */;

        // Returns true (acks) — does not throw.
        var processed = await processor.ProcessAsync(/* args */);
        Assert.True(processed);
    }
}
```

The implementer reuses the `InboundMessageProcessor` construction pattern from Task 7 (M2). All three tests in this file use the same fixture-style setup; consolidate the constructor builder into a private helper if the duplication is costly.

- [ ] **Step 2: Run tests to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InboundMessageProcessorTransportTests" -m:1
```

Expected:
- `RetryPublishThrowsAlreadyClosed_RethrowsForBrokerRedelivery` FAILS — today the AlreadyClosedException is swallowed; the test expects it to escape.
- `RetryPublishThrowsBrokerUnreachable_RethrowsForBrokerRedelivery` FAILS — same.
- `RetryPublishThrowsPoisonException_SwallowsAndAcks` PASSES — current behaviour matches the expectation.

- [ ] **Step 3: Apply the M5a fix**

In `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs` lines 130-145 (the retry-publish catch), insert two typed catches between the OCE catch and the generic catch:

```csharp
            try
            {
                await _retryHandler.HandleFailureAsync(
                    publishChannel,
                    _retryQueueName,
                    args,
                    headers,
                    result.Exception,
                    shutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                // Existing — unchanged.
                throw;
            }
            catch (RabbitMQ.Client.Exceptions.AlreadyClosedException)
            {
                // Transport-class failure — rethrow so the outer finally nacks-with-requeue
                // and the broker redelivers after reconnect.
                // See learn/operations/cancellation: transient transport failures must NOT
                // ack-and-drop messages.
                throw;
            }
            catch (RabbitMQ.Client.Exceptions.BrokerUnreachableException)
            {
                // Transport-class failure — rethrow so the outer finally nacks-with-requeue
                // and the broker redelivers after reconnect.
                throw;
            }
            catch (Exception retryEx)
            {
                _logger.LogError(retryEx,
                    "Retry publish failed for MessageId {MessageId} (DeliveryTag {DeliveryTag}) on queue {Queue}; dropping to prevent unbounded redelivery loop.",
                    args.BasicProperties.MessageId, args.DeliveryTag, _queueConfiguration.QueueName);
                // Existing — unchanged.
            }
```

The two new catches are AFTER the OCE catch and BEFORE the generic catch.

- [ ] **Step 4: Run tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InboundMessageProcessorTransportTests" -m:1
```

Expected: all 3 pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorTransportTests.cs
git commit -m "fix(consumer): rethrow transport-class exceptions from retry-publish for broker redelivery"
```

---

## Task 9: M5b — Terminal-failure-publish transport-class discriminator

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs:172-184`
- Modify: `src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorTransportTests.cs`

Same shape as Task 8, applied to the second site (terminal-failure / NotHandled path).

- [ ] **Step 1: Write the failing tests**

Append to `InboundMessageProcessorTransportTests.cs`:

```csharp
    [Fact]
    public async Task ProcessAsync_TerminalFailurePublishThrowsAlreadyClosed_RethrowsForBrokerRedelivery()
    {
        // Arrange — NotHandled branch: result.Success=true, result.NotHandled=true,
        // _deadLetterUnhandledMessages=true, _errorsDisabled=false.
        // _retryHandler.HandleTerminalFailureAsync throws AlreadyClosedException.

        var transportException = new AlreadyClosedException(new global::RabbitMQ.Client.ShutdownEventArgs(
            global::RabbitMQ.Client.ShutdownInitiator.Peer, 0, "test"));

        var processor = /* construct with the above setup */;

        var thrown = await Assert.ThrowsAsync<AlreadyClosedException>(() =>
            processor.ProcessAsync(/* args */));

        Assert.Same(transportException, thrown);
    }

    [Fact]
    public async Task ProcessAsync_TerminalFailurePublishThrowsPoisonException_SwallowsAndAcks()
    {
        var poisonException = new InvalidOperationException("poison");

        var processor = /* same NotHandled setup, HandleTerminalFailureAsync throws poisonException */;

        var processed = await processor.ProcessAsync(/* args */);
        Assert.True(processed);
    }
```

(`BrokerUnreachableException` coverage on this site is implicitly covered by the same code path — the typed catches are identical to Task 8's; one test for each named type at one site is sufficient regression coverage.)

- [ ] **Step 2: Run tests to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ProcessAsync_TerminalFailurePublishThrowsAlreadyClosed_RethrowsForBrokerRedelivery|FullyQualifiedName~ProcessAsync_TerminalFailurePublishThrowsPoisonException_SwallowsAndAcks" -m:1
```

Expected: AlreadyClosed test FAILS; poison test PASSES.

- [ ] **Step 3: Apply the M5b fix**

In `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs` lines 172-184 (the terminal-failure-publish catch), insert two typed catches matching Task 8:

```csharp
            try
            {
                await _retryHandler.HandleTerminalFailureAsync(
                    publishChannel,
                    args,
                    headers,
                    new InvalidOperationException($"No processor handled message of type '{typeName}'."),
                    shutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                throw;
            }
            catch (RabbitMQ.Client.Exceptions.AlreadyClosedException)
            {
                // Transport-class failure — rethrow so the outer finally nacks-with-requeue
                // and the broker redelivers after reconnect.
                // See learn/operations/cancellation.
                throw;
            }
            catch (RabbitMQ.Client.Exceptions.BrokerUnreachableException)
            {
                throw;
            }
            catch (Exception terminalEx)
            {
                _logger.LogError(terminalEx,
                    "Terminal-failure publish failed for MessageId {MessageId} (DeliveryTag {DeliveryTag}) on queue {Queue}; dropping to prevent unbounded redelivery loop.",
                    args.BasicProperties.MessageId, args.DeliveryTag, _queueConfiguration.QueueName);
            }
```

- [ ] **Step 4: Run tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InboundMessageProcessor" -m:1
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorTransportTests.cs
git commit -m "fix(consumer): rethrow transport-class exceptions from terminal-failure publish"
```

---

## Task 10: M13 — `Retry.DoAsync` exceptionAction OCE filter

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/Retry.cs:105-113`
- Modify: `src/ServiceConnect.UnitTests/RabbitMQ/RetryOperationCanceledTests.cs`

- [ ] **Step 1: Inspect existing Retry test fixture**

```bash
head -60 src/ServiceConnect.UnitTests/RabbitMQ/RetryOperationCanceledTests.cs
```

Note the canonical setup — the existing OCE-from-action test already exercises the `action()` OCE path. The new test exercises the `exceptionAction` callback's OCE path.

- [ ] **Step 2: Write the failing test**

Append to `RetryOperationCanceledTests.cs`:

```csharp
    [Fact]
    public async Task DoAsync_ExceptionActionThrowsOceUnderCancelledToken_PropagatesOce()
    {
        using var cts = new CancellationTokenSource();
        var initialFailure = new InvalidOperationException("first attempt failed");
        var oceFromCallback = new OperationCanceledException(cts.Token);

        var actionInvocations = 0;
        var callbackInvocations = 0;

        Func<Task<bool>> action = () =>
        {
            actionInvocations++;
            // First invocation throws a regular exception (triggers exceptionAction).
            throw initialFailure;
        };

        Func<Exception, Task> exceptionAction = (ex) =>
        {
            callbackInvocations++;
            // Cancel the token from inside the callback to simulate the reconnect-callback
            // firing during shutdown, then throw OCE.
            cts.Cancel();
            throw oceFromCallback;
        };

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Retry.DoAsync(action, exceptionAction, retryCount: 3, retryInterval: TimeSpan.Zero, cts.Token));

        Assert.Same(oceFromCallback, thrown);
        Assert.Equal(1, actionInvocations);
        Assert.Equal(1, callbackInvocations);
    }
```

- [ ] **Step 3: Run test to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~DoAsync_ExceptionActionThrowsOceUnderCancelledToken_PropagatesOce" -m:1
```

Expected: FAIL — today the OCE is added to the `exceptions` list and control falls through to the next iteration's `Task.Delay(..., cancellationToken)` which then throws OCE — but it's the wrong OCE. The test asserts the SAME OCE instance escapes; current behaviour produces a different OCE from `Task.Delay`.

- [ ] **Step 4: Apply the M13 fix**

In `src/ServiceConnect.Client.RabbitMQ/Consumer/Retry.cs` lines 105-113, insert an OCE filter ahead of the existing generic callback-catch:

```csharp
                try
                {
                    await exceptionAction(ex).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Cooperative cancellation — propagate so callers can distinguish shutdown
                    // from a callback failure that should be retried. Mirrors the action-side
                    // OCE handling at lines 94-97.
                    // See learn/operations/cancellation.
                    throw;
                }
                catch (Exception callbackEx)
                {
                    (exceptions ??= []).Add(callbackEx);
                }
```

- [ ] **Step 5: Run test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Retry" -m:1
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/Retry.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/RetryOperationCanceledTests.cs
git commit -m "fix(retry): propagate OCE from exceptionAction callback"
```

---

## Task 11: Final verification

**Files:**
- (none modified — verification only)

- [ ] **Step 1: Per-csproj build of every relevant project**

```bash
dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj
dotnet build src/ServiceConnect/ServiceConnect.csproj
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj
dotnet build src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj
```

Expected: all succeed. If `dotnet build` for the unit-tests csproj hits MSBuild Copy task OOM, add `-m:1`.

- [ ] **Step 2: Run the relevant tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Cancellation|FullyQualifiedName~Shutdown|FullyQualifiedName~Dispose|FullyQualifiedName~MessageBusWriteStream|FullyQualifiedName~MessageDispatcher|FullyQualifiedName~InboundMessageProcessor|FullyQualifiedName~Retry|FullyQualifiedName~ProcessManagerTimeoutService|FullyQualifiedName~ProducerLifecycle|FullyQualifiedName~MessageAuditPublisher" -m:1
```

Expected: all pass; the new tests added across Tasks 2-10 are included.

- [ ] **Step 3: Astro build**

```bash
npm --prefix website run build
```

Expected: succeeds. The new `learn/operations/cancellation.mdx` page builds; sidebar entry shows up.

- [ ] **Step 4: Repo-wide grep for stale anti-patterns**

```bash
grep -rn "catch (Exception" src/ServiceConnect/ src/ServiceConnect.Client.RabbitMQ/ --include="*.cs" | head -50
```

Manually triage each hit. Each `catch (Exception ...)` block must either:
- Be wrapped by an OCE filter (`catch (OperationCanceledException) when (...)`) ahead of it, OR
- Be on the documented fire-and-forget whitelist (Dispose/Close/teardown paths), OR
- Have a comment explaining why OCE handling is unnecessary at this site.

This is best-effort — false positives are expected (some catches don't await cancellable operations). Document any flagged hits in the commit message of a follow-up if non-trivial. The reviewer of the cumulative PR can use this list to spot-check the discipline.

- [ ] **Step 5: Code review (optional)**

Per the phase doc's general guidance, invoke `superpowers:requesting-code-review` against the diff range for Phase 3 before merging. The reviewer should confirm:
- Discipline doc accurately describes what the code now does.
- All 9 (or so) new tests pass.
- Each fix's site has an explanatory comment that references `learn/operations/cancellation`.
- No new sites accept a `CancellationToken` parameter and drop it on a transitively-awaited call (the smaller-item / H12 patterns).

---

## Self-review

**Spec coverage check.** Walking through `docs/superpowers/specs/2026-04-29-phase-03-shutdown-cancellation.md`:

- C8 — Task 2.
- H12 — Task 3.
- H13 — Task 4.
- H23 — Task 6.
- M2 — Task 7.
- M5 (retry-publish + terminal-failure-publish) — Tasks 8, 9.
- M13 — Task 10.
- Smaller item — Task 5.
- Cancellation discipline website page — Task 1.
- Verification gate — Task 11.

Every spec requirement maps to a task.

**Placeholder scan.** Two places use existing-fixture arrange-block reuse (Tasks 5 and 7-9 direct the implementer to reuse existing fixture mocks rather than reproducing 100+ lines inline). This is calibrated guidance — the existing fixtures are large and reproducing them inline would inflate the plan beyond its purpose. The implementer is told exactly which existing test pattern to copy. Task 4 (H13) explicitly acknowledges that a direct allocator-throw test is hard to construct and the test is the structural regression bound.

**Type / signature consistency.**
- `DisposeConnectionAsync(CancellationToken cancellationToken)` — same signature in Task 6, called from `ReconnectAsync(_, cancellationToken)`.
- The discipline doc's three rules are referenced in code comments at every fix site (search for `learn/operations/cancellation`).
- The two new typed catches (`AlreadyClosedException`, `BrokerUnreachableException`) appear in Tasks 8 and 9 with identical shape — both reference the same RabbitMQ.Client.Exceptions namespace.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-04-29-phase-03-shutdown-cancellation.md`. Two execution options:

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?
