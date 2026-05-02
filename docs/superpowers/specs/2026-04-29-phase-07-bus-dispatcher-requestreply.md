# Phase 07 — Bus, dispatcher, and request/reply (spec)

**Phase doc:** [`consolidated-issues/phases/phase-07-bus-dispatcher-requestreply.md`](../../../consolidated-issues/phases/phase-07-bus-dispatcher-requestreply.md)

**Branch:** `v7-clean-architecture`.

## Goal

Address the cluster of correctness defects in `Bus`, `MessageDispatcher`, and `RequestReplyManager` — the heart of the runtime. 14 findings across 2 Critical, 7 High, 5 Smaller. The fixes cluster naturally into four file-bundles: a single-file `MessageDispatcher` change (C9), the `RequestReplyManager` state-machine redesign (C10 + H17 + H18), the `Bus` lifecycle cleanup (H14 + H15 + H16), and the `Bus` routing-slip validation tightening (H19 + H20). Five smaller polish items round out the phase.

> **Note on file-line anchors:** All line numbers below reference the *current* tree (post-Phase-6). Sequential commits will shift them. The plan re-grounds anchors per task; readers should `git grep` symbol names rather than chase line numbers across commits.

---

## Findings in scope

| ID | One-line | File |
| --- | --- | --- |
| **C9** | Unresolved type → `Success=true, NotHandled=true` (Option A) | [`MessageDispatcher.cs:117-126,:147-150`](../../../src/ServiceConnect/Services/MessageDispatcher.cs#L117) |
| **C10** | `EndReply()` only fires when `TryAcceptReply` returned true — falls out of H18 | [`RequestReplyManager.cs:321-379`](../../../src/ServiceConnect/Services/RequestReplyManager.cs#L321) |
| **H14** | `Bus.StopConsumingCoreAsync` rethrows OCE early; doesn't mutate state without the lifecycle semaphore | [`Bus.cs:484-576`](../../../src/ServiceConnect/Bus.cs#L484) |
| **H15** | Bus stops disposing `IConsumer`/`IProducer` (DI singletons own them) | [`Bus.cs:533, :544, :549, :590`](../../../src/ServiceConnect/Bus.cs#L533) |
| **H16** | Falls out of H15 once dispose is removed | [`Bus.cs:539`](../../../src/ServiceConnect/Bus.cs#L539) |
| **H17** | New `RequestSendCancelledException : OperationCanceledException`; fail fast on send-time cancellation | [`RequestReplyManager.cs:18-90, :92-201, :204-294`](../../../src/ServiceConnect/Services/RequestReplyManager.cs#L18), [`ServiceConnect.Interfaces/Exceptions/`](../../../src/ServiceConnect.Interfaces/Exceptions/) |
| **H18** | Drop `SyncRoot`; single `_stateLock` for the entire `TryProcessReply` critical section | [`RequestReplyManager.cs:314-380, :397-515`](../../../src/ServiceConnect/Services/RequestReplyManager.cs#L314) |
| **H19** | `Bus.RouteAsync` validates destinations (throw `ArgumentException` on null/empty/too-few) | [`Bus.cs:307-351`](../../../src/ServiceConnect/Bus.cs#L307) |
| **H20** | `Bus.BuildRoutingSlip` validates destinations (throw `ArgumentException` on comma) | [`Bus.cs:675-696`](../../../src/ServiceConnect/Bus.cs#L675) |
| **smaller — `BuildHeadersDirect` capacity** | Tighten `+5` to `+3` (post-H21 only 3 keys stamped) | [`Bus.cs:712-713`](../../../src/ServiceConnect/Bus.cs#L712) |
| **smaller — `Bus.DisposeAsync` lifecycle semaphore** | Stop disposing `_lifecycleSemaphore`; mirror Phase 6 H2 / Connection pattern | [`Bus.cs:574-589`](../../../src/ServiceConnect/Bus.cs#L574) |
| **smaller — `Bus.RouteAsync` IList not snapshotted** | Snapshot to array (folded into H19) | [`Bus.cs:316`](../../../src/ServiceConnect/Bus.cs#L316) |
| **smaller — `Bus.CreateStream<T>` no endpoint validation** | Add `ArgumentException.ThrowIfNullOrWhiteSpace(endpoint)` | [`Bus.cs:354`](../../../src/ServiceConnect/Bus.cs#L354) |
| **smaller — `RequestReplyManager` XML doc** | Tighten "synchronously disposed" wording (registration is async-disposed) | [`RequestReplyManager.cs:39-55`](../../../src/ServiceConnect/Services/RequestReplyManager.cs#L39) |
| **smaller — `SendMessagePipeline` lifetime check** | Cover middleware-via-DI registrations | [`SendMessagePipeline.cs:23-39`](../../../src/ServiceConnect/Services/SendMessagePipeline.cs#L23) |
| **smaller — `QueueConfiguration.QueueMappings`** | Cache the wrapper instead of allocating per access | [`QueueConfiguration.cs:40-41`](../../../src/ServiceConnect/Configuration/QueueConfiguration.cs#L40) |

## Out of scope (routed elsewhere)

- **C8** (`MessageDispatcher` shutdown OCE swallow) — landed in Phase 3.
- **H21** (`OutboundHeaderBuilder` MessageType overwrite) — landed in Phase 4.
- **H22** (`PublishWithTimeoutAsync` reconnect under lock) — landed in Phase 4.

---

## Decisions

### Q1 — C9: where should an unresolved message type land? → Option A (reuse `NotHandled`)

When the dispatcher can't resolve the message type from the registry, returning `Success=false` causes the consumer host to nack-with-requeue → retries until max → error queue. An unregistered type is **terminal** — retrying never resolves it. The consumer host already has a "not handled" path: `result.Success=true && result.NotHandled=true` routes either via terminal failure (when `DeadLetterUnhandledMessages` is enabled) OR ack-and-drop (when it's disabled). This was Phase 5's M6 territory.

**Option A (chosen):** Reuse the existing `NotHandled` semantic. Change `Success=false` to `Success=true, NotHandled=true` for unresolved types at lines 122 and 149. Operators who want unregistered messages dead-lettered enable `DeadLetterUnhandledMessages` (already documented); otherwise the message is acked and dropped.

Option B (introduce a new `UnregisteredType=true` flag) was rejected — adds public-API surface without a meaningful behaviour distinction. Option C (keep `Success=false`, document) was rejected — phase doc explicitly calls this out as broken.

### Q2 — H17: how should send-time cancellation surface to the caller? → Option A.2 (new typed exception)

When the request-reply send pipeline is cancelled by the linked CTS (timeout-fires-during-send), the catch swallows the OCE and falls through to await `tcs.Task`. The caller eventually sees a `RequestTimeoutException` — but they wait the full timeout for a request that was never sent.

**Option A.2 (chosen):** Track a `_sendCompleted` flag and throw a new `RequestSendCancelledException : OperationCanceledException` when the linked CTS fires before send completes AND the caller's token did not request cancellation. The typed exception lets callers distinguish "your code's cancellation token fired" (plain `OperationCanceledException`) from "the bus couldn't send" (a transport-layer failure). The `: OperationCanceledException` base means existing `catch (OperationCanceledException)` callers continue to work; callers wanting to react specifically to send-failure can `catch (RequestSendCancelledException)`.

Option A.1 (bare `OperationCanceledException`) was rejected — caller can't distinguish send failure from caller-supplied cancellation. Option B (race the send against the timeout register) was rejected — more failure modes than the typed-exception approach. Option C (don't change behaviour) was rejected — phase doc calls this out as broken.

### Q3 — H18: RequestState locking model → Option A (single `_stateLock`, drop `SyncRoot`)

`RequestState` today has two locks: `SyncRoot` (public property used externally by `TryProcessReply` to wrap the entire reply-handling block) and `_stateLock` (internal, protects `_closed`, `_inFlightReplies`, `_remainingReplies`, `_hasAcceptedReplies`, `_pendingCloseAction`). The two locks are independent — `SyncRoot` exists to serialise external `TryProcessReply` calls so the user's `onReply` callback isn't re-entered concurrently; `_stateLock` protects internal mutations. The design is fragile: future code that mutates state outside `TryProcessReply` won't be guarded by `SyncRoot`.

**Option A (chosen):** Drop `SyncRoot` entirely; route everything through `_stateLock`. Move the entire `TryProcessReply` body — including the deserialize, the OnReply callback invocation, and the close-action triggers — into a method on `RequestState` that holds `_stateLock` for the whole critical section. .NET's Monitor is re-entrant so re-entering the lock works. Single source of truth.

Option B (keep both locks, document the invariant) was rejected — doesn't fix the fragility. Option C (eliminate user-callback-under-lock; capture reply, release lock, then invoke `onReply`) was rejected — changes externally observable behaviour (concurrent callback re-entry).

---

## Fixes — behaviour spec

### `MessageDispatcher.cs`

#### C9 — Unresolved type returns `Success=true, NotHandled=true`

Two sites need updating.

**Site 1** at [`MessageDispatcher.cs:117-126`](../../../src/ServiceConnect/Services/MessageDispatcher.cs#L117-L126):

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

**Site 2** at [`MessageDispatcher.cs:147-150`](../../../src/ServiceConnect/Services/MessageDispatcher.cs#L147-L150):

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

### `RequestReplyManager.cs`

This file gets three coordinated changes (H18 → C10 → H17) plus one comment polish. Order matters: H18 first (single-lock redesign), then C10 falls out structurally, then H17 wires the new exception through the three send paths.

#### H18 — Single `_stateLock` for the entire `TryProcessReply` critical section

**Step 1.** `RequestState` (lines 397-515) gains an internal method that performs the entire reply-handling critical section under `_stateLock`:

```csharp
internal sealed class RequestState(/* params unchanged */)
{
    // _stateLock unchanged. SyncRoot property REMOVED.
    // _inFlightReplies and _pendingCloseAction REMOVED (no longer needed; see Close below).
    // ...

    /// <summary>
    /// Processes a deserialized reply under the state lock. Returns true if the reply was
    /// accepted; false if rejected (state already closed or reply budget exhausted). The
    /// user-supplied OnReply callback runs under the state lock so concurrent replies
    /// cannot re-enter the callback.
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
}
```

**Step 2.** `TryProcessReply` (lines 314-380) becomes:

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

**Step 3.** Remove now-unused members from `RequestState`:
- `public object SyncRoot { get; } = new();` (line 414).
- `public bool TryAcceptReply(out bool completesRequest)` (lines 484-514).
- `public void EndReply()` (lines 442-457).
- `private int _inFlightReplies;` (no longer needed).
- `private Action? _pendingCloseAction;` (no longer needed).

**Step 4.** Simplify `Close` to remove the queue-action-until-replies-finish branch (dead code under the new model — `TryHandleReply` holds the lock for the entire reply lifecycle):

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

`Close` becomes `internal` (was `public`); only `RequestReplyManager`'s timeout register paths call it.

**Step 5.** Net effect: completion work runs OUTSIDE the lock (`completionWork?.Invoke()` after the lock release). This matches today's pattern of running TCS continuations off the lock and avoids pinning the dispatch thread on a slow continuation.

#### C10 — `EndReply` underflow

Falls out of H18. `EndReply` is gone; `_inFlightReplies` is gone. The original bug — `EndReply()` running unconditionally in the `finally` even when `TryAcceptReply` returned false — is structurally impossible now. The C10 regression test (Section 3) locks in the new contract.

#### H17 — New `RequestSendCancelledException` and fail-fast on send-time cancellation

**Step 1.** New exception type at `src/ServiceConnect.Interfaces/Exceptions/RequestSendCancelledException.cs`:

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
    public Guid MessageId { get; } = messageId;
}
```

**Step 2.** `SendRequestAsync` (lines 18-90) — add `_sendCompleted` flag, replace catch block:

```csharp
// Add near tcs:
var sendCompleted = 0;

// Replace the current try block (lines 57-79):
try
{
    var endPoint = string.IsNullOrEmpty(options.EndPoint) ? null : options.EndPoint;
    var context = new SendContext { /* unchanged */ };
    await _sendPipeline.ExecuteSendMessagePipelineAsync(context, linkedCts.Token).ConfigureAwait(false);
    Interlocked.Exchange(ref sendCompleted, 1);
}
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    _pendingRequests.TryRemove(messageId, out _);
    throw;
}
catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && Volatile.Read(ref sendCompleted) == 0)
{
    // Send-layer cancellation, NOT caused by the caller's token. The send never completed.
    // Fail fast with a typed exception rather than waiting the full timeout for the
    // RequestTimeoutException path.
    _pendingRequests.TryRemove(messageId, out _);
    throw new RequestSendCancelledException(messageId,
        $"Request {messageId} send pipeline was cancelled before delivery.");
}
catch
{
    _pendingRequests.TryRemove(messageId, out _);
    throw;
}
```

**Step 3.** Apply the same pattern at `SendRequestMultiAsync` (lines 145-187) — set `sendCompleted = 1` after the entire `foreach` loop completes successfully (partial-loop cancellation should fail fast).

**Step 4.** `PublishRequestAsync` (lines 262-284) already has a `publishCompletedSuccessfully` flag — reuse it for the new catch logic. Existing flag becomes the `sendCompleted` equivalent.

**Step 5.** Update XML docs on `IRequestReplyManager.SendRequestAsync` / `SendRequestMultiAsync` / `PublishRequestAsync` (in `ServiceConnect.Interfaces`):

```xml
/// <exception cref="RequestSendCancelledException">
/// Thrown when the outbound send pipeline cancelled before the request reached the broker.
/// Distinct from a timeout (<see cref="RequestTimeoutException"/>) and from caller-token
/// cancellation (<see cref="OperationCanceledException"/>).
/// </exception>
```

#### Smaller — `RequestReplyManager` XML doc

[`RequestReplyManager.cs:39-55`](../../../src/ServiceConnect/Services/RequestReplyManager.cs#L39) — the XML doc on `SendRequestAsync` says the registration is "synchronously" disposed when the timeout fires; actually `await using var reg = ...` disposes asynchronously. Tighten the wording.

### `Bus.cs` — lifecycle (H14 + H15 + H16)

#### H15 — Remove transport disposes

Sites at [`Bus.cs:533, 544, 549`](../../../src/ServiceConnect/Bus.cs#L533) (`StopConsumingCoreAsync`) and [`:590`](../../../src/ServiceConnect/Bus.cs#L590) (`Bus.DisposeAsync`).

DI registers `IConsumer` and `IProducer` as `TryAddSingleton` (in `RabbitMQExtensions.cs`). The host's `IServiceProvider` disposes them on host shutdown. Today's Bus calling `DisposeAsync` is a double-dispose. The fix removes those calls.

#### Simplified `StopConsumingCoreAsync`

After H15, the method drops `localConsumer`, `WaitAsync` timeout, and the orphan-task concern. Final shape:

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

#### H14 — OCE handling

With H15 simplifying the method, H14's "mutate state without semaphore" disappears: the only path is via the semaphore now. The `WaitAsync` OCE propagates naturally out of the method (no swallow, no fall-through, no `pendingCancellation`). The H14 fix is implicit in the H15 simplification.

#### H16 — Disappears with H15

No more `WaitAsync(_disposeTimeout)` call to time out. The orphan-task concern is gone.

#### Simplified `Bus.DisposeAsync`

[`Bus.cs:579-594`](../../../src/ServiceConnect/Bus.cs#L579) becomes:

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

    // _lifecycleSemaphore is intentionally NOT Disposed:
    // SemaphoreSlim.Dispose only releases the lazily-allocated WaitHandle, and we never call
    // AvailableWaitHandle, so disposal is a functional no-op. A concurrent caller's Release()
    // on a disposed semaphore would throw ObjectDisposedException out of the unwind path,
    // which we cannot prevent without holding GC references to every caller. Mirrors
    // Connection / ProducerConnection / Producer pattern (Phases 4 + 6).
}
```

The catch around `StopConsumingCoreAsync` ensures dispose proceeds even if the stop step fails — preserves the v7 dispose-as-best-effort discipline. The lifecycle-semaphore-not-disposed pattern matches Phase 6's H2 fix.

#### Smaller — `Bus.DisposeAsync` doesn't await the lifecycle semaphore

Subsumed by the above — `StopConsumingCoreAsync` does the wait.

### `Bus.cs` — validation (H19 + H20)

#### H19 — `Bus.RouteAsync` validates destinations

[`Bus.cs:307-351`](../../../src/ServiceConnect/Bus.cs#L307):

```csharp
public async Task RouteAsync<TMessage>(TMessage message, IList<string> destinations, ...)
{
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
    // ... existing routing-slip body using `snapshot` ...
}
```

#### H20 — `Bus.BuildRoutingSlip` validates destinations

[`Bus.cs:675-696`](../../../src/ServiceConnect/Bus.cs#L675):

```csharp
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

#### Smaller — `Bus.RouteAsync` IList not snapshotted

Folded into H19 — the `snapshot = destinations.ToArray()` line addresses this.

### `Bus.cs` — small polish

#### Smaller — `BuildHeadersDirect` capacity

[`Bus.cs:712-713`](../../../src/ServiceConnect/Bus.cs#L712). The `+ 5` allocates room for 5 stamped keys but only 3 are stamped post-Phase-4 H21 (Bus stopped stamping `MessageType`). Tighten to `+ 3`.

#### Smaller — `Bus.CreateStream<T>` no endpoint validation

[`Bus.cs:354`](../../../src/ServiceConnect/Bus.cs#L354):

```csharp
public IBusWriteStream CreateStream<T>(string endpoint)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
    // ... existing body ...
}
```

### `SendMessagePipeline.cs` — middleware lifetime check

#### Smaller — middleware lifetime validation covers DI registrations

[`SendMessagePipeline.cs:23-39`](../../../src/ServiceConnect/Services/SendMessagePipeline.cs#L23). The current check only validates middleware registered explicitly via `AddSendMessageMiddleware<T>`. Middleware added directly to DI (`services.AddScoped<ISendMessageMiddleware, X>`) bypasses validation. Walk the DI registrations for `ISendMessageMiddleware` and validate their lifetimes too.

The exact extension point depends on `SendMessagePipeline.cs`'s existing validation shape — read it during implementation. The general approach: enumerate `IServiceProvider` registrations of `ISendMessageMiddleware`, apply the same lifetime check (`Singleton` / `Scoped` allowed; `Transient` rejected, or the policy currently encoded — preserve whatever the existing check enforces).

### `QueueConfiguration.cs` — wrapper allocation

#### Smaller — `QueueMappings` allocates per access

[`QueueConfiguration.cs:40-41`](../../../src/ServiceConnect/Configuration/QueueConfiguration.cs#L40):

```csharp
// before
public IReadOnlyDictionary<Type, IReadOnlyList<string>> QueueMappings =>
    new ReadOnlyDictionary<Type, IReadOnlyList<string>>(_mappings);

// after
private IReadOnlyDictionary<Type, IReadOnlyList<string>>? _mappingsView;
public IReadOnlyDictionary<Type, IReadOnlyList<string>> QueueMappings =>
    _mappingsView ??= new ReadOnlyDictionary<Type, IReadOnlyList<string>>(_mappings);
```

Invalidate `_mappingsView` (set to null) whenever `_mappings` is mutated (in `AddQueueMapping` and any other mutation site). The `_mappings` dict is the shared backing store; the view caches the wrapper only.

---

## Tests

Default location: `src/ServiceConnect.UnitTests/`. Reuse the canonical Mock<> patterns from prior phases.

### C9 — Unresolved type → not-handled (unit)

**File:** `src/ServiceConnect.UnitTests/MessageDispatcherUnresolvedTypeTests.cs` (new).

Two tests covering the early-return (non-reply) and late-return (reply with response-id) paths:

```csharp
[Fact]
public async Task DispatchAsync_UnresolvedType_NoResponseId_ReturnsNotHandled()
{
    // Arrange: type registry that doesn't resolve "Foo.Bar". Headers without ResponseMessageId.
    // Act: DispatchAsync.
    // Assert: result.Success == true, result.NotHandled == true. (Pre-fix: Success=false.)
}

[Fact]
public async Task DispatchAsync_UnresolvedType_WithResponseId_ButNoReplyHandlerMatched_ReturnsNotHandled()
{
    // Arrange: type registry that doesn't resolve. Headers with ResponseMessageId set, but
    // ReplyProcessor returns ProcessResult.NotHandled (no pending request matches).
    // Act: DispatchAsync.
    // Assert: result.Success == true, result.NotHandled == true. (Pre-fix: Success=false.)
}
```

Reuse existing `MessageDispatcherTests.cs` arrange harness; only the type-registry mock and the assertion change.

### C10 — `EndReply` underflow (now structurally fixed by H18) (unit)

**File:** `src/ServiceConnect.UnitTests/RequestReplyManagerInFlightCounterTests.cs` (new).

```csharp
[Fact]
public async Task SendRequestMultiAsync_DuplicateRepliesAfterClose_DoesNotInvokeOnReplyAfterClose()
{
    // Arrange: SendRequestMultiAsync with ExpectedReplyCount = 2 and an onReply that records calls.
    // Send 4 replies concurrently — only the first 2 should be accepted.
    // Assert: onReply called exactly 2 times. The TCS resolves with the 2 accepted replies.
    //         No spurious close-action firing.
}

[Fact]
public async Task SendRequestMultiAsync_ReplyAfterTimeout_DoesNotInvokeOnReply()
{
    // Arrange: SendRequestMultiAsync with very short timeout. After timeout fires, send a reply.
    // Assert: onReply was not called for the post-close reply.
}
```

### H17 — Send-time cancellation surfaces as `RequestSendCancelledException` (unit)

**File:** `src/ServiceConnect.UnitTests/RequestReplyManagerSendCancelTests.cs` (new).

Three tests covering each send path:

```csharp
[Fact]
public async Task SendRequestAsync_SendPipelineCancelled_NotByCallerToken_ThrowsRequestSendCancelled()
{
    // Arrange: ISendMessagePipeline mock whose ExecuteSendMessagePipelineAsync awaits a
    // TaskCompletionSource<bool> that the test cancels via the linked token (timeout) but
    // NOT via the caller's token.
    // Act: caller awaits SendRequestAsync.
    // Assert: caller observes RequestSendCancelledException IMMEDIATELY (within ~50ms),
    //         not after the full configured timeout. MessageId on the exception matches
    //         the request id stamped into the headers.
}

[Fact]
public async Task SendRequestAsync_CallerTokenCancelled_StillThrowsOperationCanceled_NotRequestSendCancelled()
{
    // Same arrange but cancel via the caller's token.
    // Assert: caller observes plain OperationCanceledException, not RequestSendCancelledException.
}

[Fact]
public async Task SendRequestAsync_TimeoutFires_AndSendCompletedFirst_ThrowsRequestTimeout()
{
    // Arrange: send pipeline completes quickly. No reply ever arrives. Timeout fires.
    // Assert: caller observes RequestTimeoutException (not RequestSendCancelled).
}
```

Mirror tests for `SendRequestMultiAsync` and `PublishRequestAsync`.

**Test methodology note:** `RequestSendCancelledException : OperationCanceledException`, so `Assert.Throws<OperationCanceledException>` would catch it too. Use `Assert.Throws<RequestSendCancelledException>` to assert the specific type.

### H18 — Single-lock state machine + callback re-entrancy (unit)

**File:** `src/ServiceConnect.UnitTests/RequestReplyManagerCallbackReentrancyTests.cs` (new).

```csharp
[Fact]
public async Task SendRequestMultiAsync_ConcurrentReplies_OnReplyNotInvokedConcurrently()
{
    // Arrange: SendRequestMultiAsync with ExpectedReplyCount = 5 and an onReply that
    // tracks concurrency by incrementing/decrementing a counter and asserting it never
    // exceeds 1.
    // Act: send 5 replies concurrently from multiple threads.
    // Assert: max concurrency observed in onReply == 1. All 5 replies eventually accepted.
}
```

The H17 + C10 tests above implicitly exercise the new locking model.

### H14 — Bus lifecycle locking under cancellation (unit)

**File:** `src/ServiceConnect.UnitTests/BusLifecycleCancellationTests.cs` (new or extend existing `BusLifecycleTests.cs`).

```csharp
[Fact]
public async Task StopConsumingAsync_TokenCancelledBeforeSemaphoreAcquired_ThrowsOceWithoutMutatingState()
{
    // Arrange: Bus with _consuming = true (start consumed). Acquire _lifecycleSemaphore from
    // another thread so the next StopConsumingAsync waits. Cancel the caller's token; the
    // wait throws OCE.
    // Assert: OCE propagates. _consuming is STILL true (no mutation occurred under cancellation).
    //         A subsequent StopConsumingAsync (uncontended) succeeds normally.
}
```

### H15/H16 — Bus doesn't dispose IConsumer/IProducer (unit)

**File:** `src/ServiceConnect.UnitTests/BusTransportLifecycleTests.cs` (new).

```csharp
[Fact]
public async Task DisposeAsync_DoesNotDisposeIConsumer()
{
    // Arrange: Mock<IConsumer> that tracks DisposeAsync invocation count.
    // Build a Bus with the mock. StartConsumingAsync, then DisposeAsync.
    // Assert: consumer.DisposeAsync called Times.Never. (DI owns the singleton; host disposes it.)
}

[Fact]
public async Task DisposeAsync_DoesNotDisposeIProducer()
{
    // Same shape for IProducer.
}

[Fact]
public async Task StopConsumingAsync_DoesNotDisposeIConsumer()
{
    // StartConsumingAsync, then StopConsumingAsync (without DisposeAsync).
    // Assert: consumer.DisposeAsync called Times.Never.
}
```

### H19 / H20 — Routing-slip validation (unit)

**File:** `src/ServiceConnect.UnitTests/BusRouteValidationTests.cs` (new).

Table-driven `[Theory]`:

```csharp
[Theory]
[InlineData(null,           "destinations")]
[InlineData(new string[0],  "at least one destination")]
[InlineData(new[] { "" },                  "null or whitespace")]
[InlineData(new[] { "  " },                "null or whitespace")]
[InlineData(new[] { "ok", null },          "null or whitespace")]
[InlineData(new[] { "ok", "with,comma" },  "comma")]
public async Task RouteAsync_InvalidDestinations_ThrowsArgumentException(
    string[]? destinations, string expectedMessageFragment)
{
    // Arrange: Bus.
    // Act + Assert: RouteAsync throws ArgumentException; message contains expectedMessageFragment.
}

[Fact]
public async Task RouteAsync_ValidDestinations_Succeeds()
{
    // Sanity: ["q1", "q2", "q3"] succeeds.
}
```

Mirror table-driven test for `BuildRoutingSlip` (called via internal seam or a `RoutingSlipProcessor` integration test).

### Smaller items (unit)

- **`Bus.CreateStream<T>` endpoint validation** — single test asserting `ArgumentException` for null / empty / whitespace endpoint.
- **`BuildHeadersDirect` capacity** — no test (perf-only).
- **`QueueConfiguration.QueueMappings` cached wrapper** — single test: two consecutive accesses return the same reference (`Assert.Same`); after a mutation, a new wrapper is returned.
- **`SendMessagePipeline` middleware lifetime check** — extend `SendMessagePipelineTests.cs` with a test that registers an `ISendMessageMiddleware` via `services.AddScoped<>` (bypassing the explicit `AddSendMessageMiddleware<>`), and asserts the lifetime validator catches it.
- **`RequestReplyManager` XML doc** — no test (doc-only).

### Build / test discipline

```bash
# Per-csproj only — never whole-solution.
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1
dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj -m:1

dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~Bus|FullyQualifiedName~Dispatcher|FullyQualifiedName~RequestReply|FullyQualifiedName~SendMessagePipeline|FullyQualifiedName~QueueConfiguration|FullyQualifiedName~RoutingSlip" \
    -m:1
```

E2E budget: zero. All tests are unit-level.

---

## Rollout

### Commit ordering

| # | Commit | Findings | Why this position |
| --- | --- | --- | --- |
| 1 | `docs(spec): phase 07 bus, dispatcher, request/reply` | — | Spec lands first. |
| 2 | `docs(plan): phase 07 implementation plan` | — | Plan after spec. |
| 3 | `fix(bus): tighten BuildHeadersDirect capacity` | smaller | Trivial; confirms pipeline. |
| 4 | `fix(bus): validate CreateStream endpoint` | smaller | Local one-liner. |
| 5 | `perf(queue-config): cache QueueMappings wrapper` | smaller | Local to `QueueConfiguration.cs`; includes wrapper-invalidation logic. |
| 6 | `fix(send-pipeline): lifetime validation covers DI-registered middleware` | smaller | Local to `SendMessagePipeline.cs`. |
| 7 | `fix(dispatcher): unresolved type → not-handled (no retry-loop burn)` | **C9** | Single-file MessageDispatcher fix. Lands before the RequestReply changes so the dispatcher path is stable when those tests run. |
| 8 | `feat(interfaces): add RequestSendCancelledException` | H17 (part) | New public type lands alone for clean blame. |
| 9 | `refactor(request-reply): single-lock state machine; drop SyncRoot` | **H18** + **C10** | Drops `SyncRoot`/`TryAcceptReply`/`EndReply`; introduces `TryHandleReply`. C10 falls out structurally. |
| 10 | `fix(request-reply): fail fast on send-time cancellation` | **H17** | Wires the new exception through three send paths. |
| 11 | `docs(request-reply): tighten dispose-synchrony XML doc` | smaller | Doc-only follow-up. |
| 12 | `refactor(bus): remove transport DisposeAsync; rely on DI` | **H15** + **H16** | Removes `_consumer.DisposeAsync()` and `_producer.DisposeAsync()`; H16 falls out. Simplifies `StopConsumingCoreAsync`. |
| 13 | `fix(bus): rethrow OCE without mutating state under cancellation` | **H14** | After H15 the method is much smaller; H14 is a clean fix. |
| 14 | `refactor(bus): keep _lifecycleSemaphore under GC; mirror Connection pattern` | smaller | Mirrors Phase 6 H2. Lands after H15's simplified DisposeAsync. |
| 15 | `fix(bus): validate routing-slip destinations` | **H19** + **H20** | `RouteAsync` and `BuildRoutingSlip` validation. Bundled because they share the same validation helper. |
| 16 | `docs(website): phase 07 release notes` | — | Releases page entry. |
| 17 | `docs(website): API reference + learn updates for routing-slip validation` | — | `reference/bus/`, `reference/handlers/`, `learn/messaging-patterns/` updates. |
| 18 | `test(phase-07): final regression gate` | — | Verification commit if any cross-cutting tests get added during review. May be empty. |

Each `fix(...)` commit lands TDD-style.

### Documentation updates

- `website/src/content/docs/reference/bus/ibus.mdx` — `RouteAsync` and `SendRequestAsync` / `SendRequestMultiAsync` / `PublishRequestAsync` docs. Document `ArgumentException` thrown for null/empty/comma destinations on `RouteAsync`. Document `RequestSendCancelledException` thrown from the three request-reply methods. Cross-link to the new type.
- `website/src/content/docs/reference/messages/exceptions.mdx` (or wherever existing exception types are documented) — add `RequestSendCancelledException`. If no such page exists, document inline on the bus reference.
- `website/src/content/docs/reference/handlers/iconsumecontext.mdx` — no change needed; the `NotHandled` semantic is already documented.
- `website/src/content/docs/learn/messaging-patterns/routing-slip.mdx` — note the validation tightening in a "What changed in v7" callout. Check worked examples for null/empty destinations.
- `website/src/content/docs/learn/messaging-patterns/request-reply.mdx` — note the new exception type.
- `website/src/content/docs/learn/operations/error-handling.mdx` — add a paragraph on what happens to messages with unregistered types (C9 user-visible: dead-letter or ack-and-drop, NOT retry-loop-then-error-queue).
- `website/src/content/docs/releases.mdx` — Phase 7 section sibling to Phase 6 (content drafted in Section 4 of the design discussion).

### Examples / READMEs

Verify `examples/RoutingSlip/`, `examples/RequestReply/`, and `examples/ScatterGather/` don't pass null/empty destinations or rely on `Success=false` for unregistered types. Update if so.

### Verification gate before final code review

1. Per-csproj build of `ServiceConnect`, `ServiceConnect.Interfaces`, `ServiceConnect.Client.RabbitMQ` clean.
2. `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Bus|FullyQualifiedName~Dispatcher|FullyQualifiedName~RequestReply|FullyQualifiedName~SendMessagePipeline|FullyQualifiedName~QueueConfiguration|FullyQualifiedName~RoutingSlip" -m:1` — all pass.
3. Astro build clean.
4. Grep verifications:
   - `grep -n "Success = false" src/ServiceConnect/Services/MessageDispatcher.cs` — only the legitimate handler-thrown-exception path; unresolved-type sites use `Success = true, NotHandled = true`.
   - `grep -n "_consumer\.DisposeAsync\|_producer\.DisposeAsync" src/ServiceConnect/Bus.cs` — zero hits.
   - `grep -n "SyncRoot" src/ServiceConnect/Services/RequestReplyManager.cs` — zero hits.
   - `grep -n "_lifecycleSemaphore\.Dispose" src/ServiceConnect/Bus.cs` — zero hits.
   - `grep -rn "RequestSendCancelledException" src/ServiceConnect.Interfaces/ src/ServiceConnect/` — at least 4 hits (declaration + 3 throw sites).
5. Final code review via `superpowers:code-reviewer` (opus) across all phase-07 commits.

### Branch

Stays on `v7-clean-architecture`. Single branch, sequential commits, mirrors Phases 1-6.
