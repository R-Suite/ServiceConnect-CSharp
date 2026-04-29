# Phase 03 — Shutdown / cancellation propagation (spec)

**Date:** 2026-04-29
**Branch:** `v7-clean-architecture` (Phase 3 lands on top of Phases 1 + 2)
**Phase doc:** [`consolidated-issues/phases/phase-03-shutdown-cancellation.md`](../../../consolidated-issues/phases/phase-03-shutdown-cancellation.md)
**Status:** approved by user, ready for implementation plan

## Background

Phase 3 of the consolidated bug backlog is the cross-cutting shutdown / cancellation cleanup. The codebase clearly *intends* to honour cancellation, but the propagation isn't airtight: catch-blocks swallow OCE during shutdown, methods accept tokens but don't pass them through, and one async-write path strands packet numbers on cancellation. Verification on the current branch (post-Phase-2 HEAD) confirmed every finding is still real, with two partial fixes that still leave meaningful gaps:

| ID | Defect | File / line | Status |
|---|---|---|---|
| C8 | `MessageDispatcher.DispatchAsync` swallows OCE during shutdown — turns shutdown into nack/requeue | [MessageDispatcher.cs:170-182](../../../src/ServiceConnect/Services/MessageDispatcher.cs#L170-L182) | Still real |
| H12 | `MessageBusWriteStream.CloseAsync` drops `cancellationToken` on the close-packet send | [MessageBusWriteStream.cs:195](../../../src/ServiceConnect/Services/MessageBusWriteStream.cs#L195) | Still real |
| H13 | `MessageBusWriteStream.WriteAsync` strands a packet number on cancellation between increment and send | [MessageBusWriteStream.cs:101-125](../../../src/ServiceConnect/Services/MessageBusWriteStream.cs#L101-L125) | Partially fixed (try/catch wraps the send but not the header-allocation window between Increment and the try) |
| H23 | `ProducerConnection.DisposeConnectionAsync` waits unbounded on the connection semaphore | [ProducerConnection.cs:228-239](../../../src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs#L228-L239) | Still real |
| M2 | `InboundMessageProcessor` audit-publish swallows OCE during shutdown | [InboundMessageProcessor.cs:198-205](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L198-L205) | Still real |
| M5 | `InboundMessageProcessor` retry-publish failure swallowed; transport-class failures hot-loop into ack-and-discard | [InboundMessageProcessor.cs:120-184](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L120-L184) | Partially fixed (shutdown-OCE filter present; transport-class discriminator missing) |
| M13 | `Retry.DoAsync` swallows OCE raised inside `exceptionAction` callback | [Retry.cs:99-113](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/Retry.cs#L99-L113) | Still real |
| smaller | `ProcessManagerTimeoutService.RemoveDispatchedTimeoutAsync` is called with `CancellationToken.None`; wedged store hangs `StopAsync` indefinitely | [ProcessManagerTimeoutService.cs:132](../../../src/ServiceConnect/Services/ProcessManagerTimeoutService.cs#L132) | Still real |

Static-analysis sweep of `await ... ConfigureAwait(false)` calls without a token, in methods that have one in scope, surfaced ~25 sites across `src/ServiceConnect/` and `src/ServiceConnect.Client.RabbitMQ/`. After triage, all but the listed findings are deliberate fire-and-forget cleanup paths (Dispose / Close / teardown). **No additional sites added.**

## Decisions (locked during brainstorming)

1. **Q1 — M5: option B (conservative discriminator).** Catch `RabbitMQ.Client.Exceptions.AlreadyClosedException` and `RabbitMQ.Client.Exceptions.BrokerUnreachableException` distinctly and rethrow them so the outer finally nacks-with-requeue. The existing generic catch keeps the swallow-and-ack behavior for poison-message-style failures. Same pattern at both publish sites (retry-publish + terminal-failure publish).
2. **Q2 — Discipline doc location: option B (website only).** Single new page at `website/src/content/docs/learn/operations/cancellation.mdx`. The contract is user-facing — filter / middleware authors writing extensions need to know the OCE rules. Maintainer-internal rules (every `await` forwards the token) live as code comments at the relevant sites.

## Scope

### In scope

- All 8 listed findings (C8, H12, H13, H23, M2, M5, M13, smaller).
- Single new website page documenting the cancellation contract.
- ~9 new unit tests, one per finding.

### Out of scope (deferred to other phases)

- **H17** (`RequestReplyManager.SendRequestAsync` send-time OCE swallow) — Phase 7 (request/reply state-machine).
- **R3 / Connection.DisposeAsync semaphore-after-release** — Phase 6 (connection lifecycle).
- **Bus.StopConsumingCoreAsync lifecycle-on-cancellation** — Phase 7.
- The dispose / teardown path fire-and-forget calls (Bus.DisposeAsync, Consumer.DisposeAsync, etc.). These are documented as the whitelist, not changed.

## The cancellation discipline

Three rules. The discipline page (Section 4 below) carries the user-facing prose; this section is the authoritative summary.

**Rule 1 — Token propagation.** Methods that accept a `CancellationToken cancellationToken` parameter must propagate it to every transitively-awaited call inside the method body. No `default`. No parameterless overloads. Exceptions: the fire-and-forget whitelist below.

**Rule 2 — OCE filter discipline.** Catch blocks that wrap a cancellable operation must include `catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }` ahead of any generic `catch (Exception)`. The shutdown OCE distinguishes itself from application failures.

**Rule 3 — Fire-and-forget cleanup whitelist.** Dispose / teardown paths and observability publishes (`DisposeAsync`, `CloseAsync` of resources we're tearing down, audit publish, telemetry inject) are deliberately not cancellable. They run with `CancellationToken.None` (or no token) so cancellation can't leave a half-disposed resource. OCE handlers in these paths log at Debug, not Error.

## Per-finding fix detail

### C8 — `MessageDispatcher` shutdown OCE filter

**File:** [src/ServiceConnect/Services/MessageDispatcher.cs](../../../src/ServiceConnect/Services/MessageDispatcher.cs)

The `catch (Exception ex)` block at lines 170-182 has no OCE filter. During graceful shutdown the consumer's delivery token is cancelled, handlers throw OCE, dispatcher catches it, returns `Success=false` with the OCE in `result.Exception`. The retry pipeline then treats cooperative shutdown as an application failure (retry-counter incremented, exception header attached, retry-queue publish on a cancelled token).

**Fix:** add an OCE filter before the generic catch:

```csharp
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    // Cooperative shutdown — propagate so the outer finally leaves the message unacked
    // for broker redelivery on next start. Not an application error.
    throw;
}
catch (Exception ex)
{
    // ... existing block unchanged ...
}
```

The existing `finally` block invoking `ExecuteAfterConsumingFiltersAsync` is unchanged — `AfterConsumingFilters` still runs on every path including the shutdown-OCE-rethrow path.

### H12 — `MessageBusWriteStream.CloseAsync` token propagation

**File:** [src/ServiceConnect/Services/MessageBusWriteStream.cs:195](../../../src/ServiceConnect/Services/MessageBusWriteStream.cs#L195)

The line:
```csharp
await _producer.SendBytesAsync(_endpoint, _messageType, [], headers).ConfigureAwait(false);
```
becomes:
```csharp
await _producer.SendBytesAsync(_endpoint, _messageType, [], headers, cancellationToken).ConfigureAwait(false);
```

Rest of `CloseAsync` is unchanged.

### H13 — `WriteAsync` widened fault-flag wrap

**File:** [src/ServiceConnect/Services/MessageBusWriteStream.cs:98-130](../../../src/ServiceConnect/Services/MessageBusWriteStream.cs#L98-L130)

Today the try/catch covers only `SendBytesAsync`. The window between `Interlocked.Increment(ref _packetNumber) - 1` (line 101) and the `try {` (line 114) — i.e., the header dictionary allocation and copy at lines 103-113 — is unprotected. A throw there (e.g. OOM during dictionary allocation under memory pressure, or any synchronous throw injected by an instrumented test) strands the packet number without setting `_faulted`.

**Fix:** widen the try to begin immediately after `Interlocked.Increment`:

```csharp
var packetNum = Interlocked.Increment(ref _packetNumber) - 1;

try
{
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
    Volatile.Write(ref _faulted, 1);
    throw;
}
```

Any throw in the post-Increment region now sets the fault flag, including the dictionary allocation / copy / key-set path.

### H23 — `DisposeConnectionAsync` bounded wait

**File:** [src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs:228-239](../../../src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs#L228-L239)

`DisposeConnectionAsync` is called from `ReconnectAsync` (line 134), which has a `CancellationToken cancellationToken` parameter. The publish path's cancellation token is dropped at the call site today.

The canonical bounded-wait pattern in this file is `CloseAsync(TimeSpan timeoutBudget)` at lines 150-182 (best-effort teardown, semaphore release if held, log Warning on timeout). The fix mirrors that pattern.

**Fix:**

1. Change `DisposeConnectionAsync` signature to take a `CancellationToken cancellationToken` parameter.

2. Acquire the semaphore with `WaitAsync(TimeSpan, CancellationToken)` — give it a defensive 30-second ceiling AND honour the publish path's cancellation token. The 30s ceiling matches the implicit deadline in the surrounding reconnect loop (`Retry.DoAsync` uses `60×10s = ~10 minutes` worst-case in tests, but a per-call dispose should fail fast within 30s rather than blocking the next publish for 10 minutes).

3. Best-effort teardown in `finally`, whether or not the lock was acquired (matching `CloseAsync` shape).

4. Update the single caller (`ReconnectAsync` at line 134) to pass `cancellationToken`.

```csharp
private async Task DisposeConnectionAsync(CancellationToken cancellationToken)
{
    var connectionLockAcquired = false;
    try
    {
        // Bound the wait: a wedged in-flight (re)connect cannot stall this dispose.
        // Token honours the publish path's cancellation; 30s ceiling keeps callers that
        // pass a never-cancelled token from blocking indefinitely.
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

Caller change (line 134 inside `ReconnectAsync`):

```csharp
await DisposeConnectionAsync(cancellationToken).ConfigureAwait(false);
```

A teardown that proceeds without the lock can race with a concurrent connect, but that race is already accepted by `CloseAsync` (existing code pattern); the worst case is a doubly-disposed channel (which is idempotent at the RabbitMQ.Client level).

### M2 — Audit-publish OCE filter

**File:** [src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs:198-205](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L198-L205)

Current:
```csharp
try
{
    await _auditPublisher.PublishAuditIfEnabledAsync(publishChannel, args, headers, shutdownToken).ConfigureAwait(false);
}
catch (Exception ex)
{
    _logger.LogError(ex, "Failed to publish audit message for delivery {DeliveryTag}; continuing to ack the original message", args.DeliveryTag);
}
```

**Fix:** add an OCE-during-shutdown filter; log at Debug (audit is fire-and-forget, an OCE during shutdown isn't an error):

```csharp
try
{
    await _auditPublisher.PublishAuditIfEnabledAsync(publishChannel, args, headers, shutdownToken).ConfigureAwait(false);
}
catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
{
    // Audit is fire-and-forget; shutdown cancellation is expected, not an error.
    _logger.LogDebug("Audit publish cancelled by shutdown for delivery {DeliveryTag}; continuing to ack the original message", args.DeliveryTag);
}
catch (Exception ex)
{
    _logger.LogError(ex, "Failed to publish audit message for delivery {DeliveryTag}; continuing to ack the original message", args.DeliveryTag);
}
```

The Debug-level log keeps audit-publish-during-shutdown out of the Error stream while still leaving a trace for diagnostics.

### M5 — Transport-class discriminator on retry-publish + terminal-failure publish

**File:** [src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs:120-184](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L120-L184)

The two catch blocks (retry-publish at lines 130-145, terminal-failure publish at lines 172-184) currently:

1. Catch `OperationCanceledException` when `shutdownToken.IsCancellationRequested` and rethrow (correct — preserves shutdown semantics).
2. Catch any other `Exception`, log Error, swallow, and ack the original message (prevents hot-loop on poison messages).

The gap: a transient transport failure (e.g., the publish channel was closed mid-send by a connection drop) falls into the generic catch and the message is silently dropped. The broker would have redelivered the message on reconnect if we'd nacked-with-requeue — but we ack'd, so the message is gone.

**Fix:** add a typed catch for the two RabbitMQ.Client transport exception types ahead of the generic catch. Rethrow them so the outer finally nacks-with-requeue (broker redelivers after reconnect).

For the retry-publish site (lines 130-145):

```csharp
catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
{
    throw;
}
catch (RabbitMQ.Client.Exceptions.AlreadyClosedException)
{
    // Transport-class failure — the publish channel was closed mid-send. Rethrow so
    // the outer finally nacks-with-requeue and the broker redelivers after reconnect.
    throw;
}
catch (RabbitMQ.Client.Exceptions.BrokerUnreachableException)
{
    // Transport-class failure — the broker became unreachable. Rethrow so the outer
    // finally nacks-with-requeue and the broker redelivers after reconnect.
    throw;
}
catch (Exception retryEx)
{
    // Existing generic catch (poison-message protection): log + ack to prevent hot-loop.
    // ... existing body unchanged ...
}
```

Same shape applied to the terminal-failure-publish site (lines 172-184). Both new typed catches are placed AFTER the OCE filter (already correct) and BEFORE the generic catch.

The two new catches share a single comment style; the comment on each is short ("transport-class failure — rethrow for broker redelivery") so the diff stays compact.

### M13 — `Retry.DoAsync` exceptionAction OCE filter

**File:** [src/ServiceConnect.Client.RabbitMQ/Consumer/Retry.cs:105-113](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/Retry.cs#L105-L113)

Current:
```csharp
try
{
    await exceptionAction(ex).ConfigureAwait(false);
}
catch (Exception callbackEx)
{
    (exceptions ??= []).Add(callbackEx);
}
```

The `action()` catch at lines 94-97 already handles OCE correctly (rethrows). The `exceptionAction` callback's catch swallows everything including OCE — meaning a cancellation token firing during the reconnect callback (which is what `exceptionAction` typically wraps) is added to the exceptions list and silently treated as a regular failure to retry.

**Fix:** add an OCE filter ahead of the generic callback-catch, mirroring the action-side OCE handling:

```csharp
try
{
    await exceptionAction(ex).ConfigureAwait(false);
}
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    // Cooperative cancellation — propagate so callers can distinguish shutdown from
    // a callback failure that should be retried.
    throw;
}
catch (Exception callbackEx)
{
    (exceptions ??= []).Add(callbackEx);
}
```

### Smaller — `ProcessManagerTimeoutService.RemoveDispatchedTimeoutAsync` token

**File:** [src/ServiceConnect/Services/ProcessManagerTimeoutService.cs:132](../../../src/ServiceConnect/Services/ProcessManagerTimeoutService.cs#L132)

Current:
```csharp
await _finder.RemoveDispatchedTimeoutAsync(timeout.Id, lockOwner, CancellationToken.None).ConfigureAwait(false);
```

**Fix:** propagate the existing `cancellationToken`:

```csharp
await _finder.RemoveDispatchedTimeoutAsync(timeout.Id, lockOwner, cancellationToken).ConfigureAwait(false);
```

`StopAsync` becomes bounded by the lifecycle token's natural deadline. A cancel-during-remove leaves the timeout "dispatched but not removed in store" — next poll picks it up and the existing at-least-once semantics handle the redelivery (consistent with the M16 finding's framing in Phase 11). Pretending `CancellationToken.None` makes Remove "more reliable" was illusory; it traded one failure mode for another.

## Cancellation-discipline website page

**Path:** `website/src/content/docs/learn/operations/cancellation.mdx`

**Sidebar:** under `Operations` after `Idempotency` (matching existing sidebar ordering in `astro.config.mjs`).

**Sections:**

1. **Why cancellation matters** — graceful shutdown is the canonical example; RabbitMQ disconnect another. ServiceConnect honours `IHostApplicationLifetime`'s shutdown token end-to-end.
2. **What the bus promises** — handler `CancellationToken` flows from the consumer host's lifecycle. When shutdown begins, the dispatcher's catch blocks rethrow OCE; the broker leaves the message unacked for redelivery on next start. Audit publishes are fire-and-forget — they don't block shutdown.
3. **What your code must do** — the three rules (Token propagation / OCE filter discipline / Fire-and-forget cleanup whitelist). Concrete examples: `IFilter.ProcessAsync` must rethrow OCE on shutdown; `IMessageHandler.HandleAsync` must propagate the token; `IMessageProcessingMiddleware.ProcessAsync` must wrap `next(...)` with the same OCE filter.
4. **Fire-and-forget whitelist** — explicit list of bus-internal sites that intentionally don't honour cancellation: dispose paths, audit publish, telemetry inject. User code shouldn't write its own; if you find yourself wanting to, your code probably has a cancellation bug.
5. **Worked examples** — three before/after snippets:
   - A filter that swallows OCE today (broken — turns shutdown into application error) vs. correct (rethrows on `cancellationToken.IsCancellationRequested`).
   - A handler that drops the token on a downstream `await` (broken — shutdown can't propagate) vs. correct (forwards every token).
   - Middleware that wraps `next(...)` correctly (uses `try { ... } catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }`).

The page is referenced from per-site code comments where the OCE filter or whitelist applies. Code comments stay terse ("see learn/operations/cancellation"); the website carries the authoritative prose.

## Test strategy

Approximately 9 new unit tests, one per finding. All in unit-test territory (no Docker / Testcontainers needed). Test discipline matches existing fixtures — Moq + xUnit, fixture-level helper fields where appropriate.

### C8 — `MessageDispatcher` shutdown OCE rethrow

In `MessageDispatcherTests.cs`. Handler throws `OperationCanceledException` linked to the cancellation token. Assert:
- `result.Success == false`.
- `result.Exception` is the OCE itself (not wrapped).
- `_config.ExceptionHandler` was NOT invoked (shutdown is not an application error).
- The existing `AfterConsumingFilters` finally still runs.

### H12 — `MessageBusWriteStream.CloseAsync` token propagation

In `MessageBusWriteStreamTests.cs`. Cancel the token mid-`CloseAsync` after the drain succeeds but before the close-packet send returns. Use a fake `IProducer` whose `SendBytesAsync` blocks on a manual signal; cancel the token; assert `OperationCanceledException` propagates from `CloseAsync`.

### H13 — `WriteAsync` widened fault-flag wrap

In `MessageBusWriteStreamTests.cs`. Cancel the token between `Increment` and the try (in practice the test cancels the token before calling `WriteAsync`, so the synchronous `cancellationToken.ThrowIfCancellationRequested()` at line 68 fires first — instead the test uses an `IProducer` whose `SendBytesAsync` throws synchronously for some inputs to simulate a header-allocation throw). Assert `_faulted == 1` after the throw.

Pragmatic alternative: assert "any throw from the post-Increment region sets `_faulted`" via two tests:
- A `SendBytesAsync` throw → `_faulted == 1` (already covered by existing tests).
- A header-allocation throw (via fake `IProducer` that asserts on the dictionary contents and throws synchronously, simulating any pre-send throw) → `_faulted == 1`.

### H23 — `DisposeConnectionAsync` bounded wait

In `ProducerLifecycleTests.cs` (or wherever the existing producer-lifecycle tests live). Set up a fake that holds the connection semaphore for longer than `_disposeLockTimeout`. Call `DisposeAsync` (which routes through `DisposeConnectionAsync`). Assert:
- The dispose returns within the timeout window (test asserts elapsed < `_disposeLockTimeout * 2`).
- A Warning log is emitted about the missed teardown.
- The dispose doesn't hang.

### M2 — Audit-publish OCE filter

In `InboundMessageProcessorTests.cs` (or similar). Handler returns `Success=true`, audit publish is configured, but the shutdown token fires during the audit publish. Assert:
- The OCE propagates out of `ProcessAsync` (so the outer finally leaves the message unacked).
- No Error log is emitted (Debug instead — verify via a test `ILogger` capturing log levels).

### M5 — Transport-class discriminator on retry-publish + terminal-failure publish

Two new tests per site (4 total).

Retry-publish site:
- (a) `_retryHandler.HandleFailureAsync` throws `RabbitMQ.Client.Exceptions.AlreadyClosedException` → assert OCE-style propagation: `ProcessAsync` returns false / throws (verify via the return value or a fake's nack-recording behaviour).
- (b) `_retryHandler.HandleFailureAsync` throws `InvalidOperationException("poison")` → assert the existing swallow-and-ack behavior (Error log, return `processed=true`).

Terminal-failure-publish site (NotHandled path):
- (c) `_retryHandler.HandleTerminalFailureAsync` throws `BrokerUnreachableException` → assert propagation.
- (d) `_retryHandler.HandleTerminalFailureAsync` throws `InvalidOperationException` → assert swallow-and-ack.

### M13 — `Retry.DoAsync` exceptionAction OCE filter

In `RetryTests.cs`. Pass an `exceptionAction` that throws OCE when the supplied cancellation token has fired. Assert:
- The OCE propagates out of `Retry.DoAsync` (matching the `action()` OCE handling at lines 94-97).
- The OCE is not swallowed into the `exceptions` list.

### Smaller — `ProcessManagerTimeoutService` token propagation

In the existing `ProcessManagerTimeoutService` test surface. Mock `_finder.RemoveDispatchedTimeoutAsync` to capture the cancellation token argument. Assert the supplied `cancellationToken` is forwarded (not `CancellationToken.None`).

### Build / test safety

Per CLAUDE.md, use only per-csproj commands. `-m:1` on `dotnet test` invocations. Never whole-solution.

```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj
dotnet test  src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Cancellation|FullyQualifiedName~Shutdown|FullyQualifiedName~Dispose|FullyQualifiedName~MessageBusWriteStream|FullyQualifiedName~MessageDispatcher|FullyQualifiedName~InboundMessageProcessor|FullyQualifiedName~Retry|FullyQualifiedName~ProcessManagerTimeoutService|FullyQualifiedName~ProducerLifecycle" -m:1
```

## Suggested commit order

1. **Discipline doc** (`docs(website): document cancellation contract`) — lands first so per-fix commits can reference it.
2. **C8** (`fix(dispatcher): shutdown OCE rethrows ahead of generic catch`).
3. **H12** (`fix(stream): pass cancellationToken to close-packet send`).
4. **H13** (`fix(stream): widen WriteAsync fault-flag wrap to cover header allocation`).
5. **Smaller** (`fix(timeout-service): propagate cancellation token to RemoveDispatchedTimeoutAsync`).
6. **H23** (`fix(producer-connection): bound DisposeConnectionAsync wait on _disposeLockTimeout`).
7. **M2** (`fix(consumer): audit-publish OCE filter ahead of generic catch`).
8. **M5a** (`fix(consumer): rethrow transport-class exceptions from retry-publish for broker redelivery`).
9. **M5b** (`fix(consumer): rethrow transport-class exceptions from terminal-failure publish`).
10. **M13** (`fix(retry): propagate OCE from exceptionAction callback`).

Each commit pairs production code + tests in TDD shape. The phase doc's suggested two-PR split (`ServiceConnect` core then `ServiceConnect.Client.RabbitMQ`) is preserved by the commit ordering — anyone reading the diff can step through it that way.

## Verification gate before merge

1. All 9 new tests pass; the filter `FullyQualifiedName~Cancellation|FullyQualifiedName~Shutdown|FullyQualifiedName~Dispose|FullyQualifiedName~MessageBusWriteStream|FullyQualifiedName~MessageDispatcher|FullyQualifiedName~InboundMessageProcessor|FullyQualifiedName~Retry|FullyQualifiedName~ProcessManagerTimeoutService|FullyQualifiedName~ProducerLifecycle` passes cleanly.
2. Per-csproj build of `ServiceConnect`, `ServiceConnect.Client.RabbitMQ`, `ServiceConnect.UnitTests` succeeds.
3. Repo-wide grep finds no remaining `catch (Exception` blocks wrapping cancellable awaits without an OCE filter ahead — best-effort, not exhaustive (some catches are intentional fire-and-forget per the whitelist; the verification asks the reviewer to read the list of remaining hits and tag each one as discipline-compliant).
4. Astro site builds (`npm --prefix website run build`).
5. Optional: `superpowers:requesting-code-review` against the cumulative diff.

## Out of scope

- Phase 7 work (RequestReplyManager, Bus.StopConsumingCoreAsync).
- Phase 6 work (Connection.DisposeAsync semaphore race).
- Comprehensive token-propagation refactor of dispose / teardown paths — those are deliberately fire-and-forget per the discipline.
- Wrapping `AlreadyClosedException` / `BrokerUnreachableException` in a custom exception type — rethrown directly per Q1: B.

## Open questions

None at spec time. All design questions were settled during brainstorming.
