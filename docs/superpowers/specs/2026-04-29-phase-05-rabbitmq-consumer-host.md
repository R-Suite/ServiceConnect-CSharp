# Phase 05 — RabbitMQ consumer + host (spec)

**Phase doc:** [`consolidated-issues/phases/phase-05-rabbitmq-consumer-host.md`](../../../consolidated-issues/phases/phase-05-rabbitmq-consumer-host.md)

**Branch:** `v7-clean-architecture`.

## Goal

Tighten consumer-side correctness across 14 findings: the `_messagesBeingProcessed` counter race (H3), broker-initiated `basic.cancel` blindness (H24), unroutable retry/error publishes silently dropping (M3), and a cluster of `InboundMessageProcessor` / `RabbitMqConsumerHost` fit-and-finish issues. Scoped to `ServiceConnect.Client.RabbitMQ` plus a small touch to `ServiceConnect.HealthChecks.BusConsumingHealthCheck` for H24.

> **Note on file-line anchors:** All line numbers below reference the *current* tree (post-Phase-4, pre-Phase-5). Sequential commits will shift them. The plan re-grounds anchors per task; readers should `git grep` symbol names rather than chase line numbers across commits.

---

## Findings in scope

| ID | One-line | Site |
| --- | --- | --- |
| **H3** | Atomic `_messagesBeingProcessed` discipline (Interlocked everywhere) | [RabbitMqConsumerHost.cs:210, :347, :461](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L210) |
| **H24** | Broker `basic.cancel` → flag + `BusConsumingHealthCheck` reports unhealthy | [RabbitMqConsumerHost.cs:386-393](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L386-L393) |
| **M3** | Retry/error publishes use `mandatory: true` | [MessageRetryHandler.cs:69, :124](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs#L69) |
| **M4** | Null `_consumerEventHandler` attaches synthetic `InvalidOperationException` | [InboundMessageProcessor.cs:97](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L97) |
| **M6** | Not-handled type-name fallback also triggers on null value | [InboundMessageProcessor.cs:170-173](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L170-L173) |
| **M7** | Ack/nack on null channel: Debug log + `IsOpen` pre-check | [RabbitMqConsumerHost.cs:296-311](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L296-L311) |
| **M8** | `_messageProcessor` defensive null-check (no `!` deref) | [RabbitMqConsumerHost.cs:284](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L284) |
| **M9** | Bind queues before `BasicConsume` (split `StartConsumingAsync`) | [Consumer.cs:181-185](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs#L181-L185), [RabbitMqConsumerHost.cs:182-189](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L182-L189) |
| **M14** | Format Retry.cs:104 — split collapsed statements | [Retry.cs:104](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/Retry.cs#L104) |
| **M15** | Header-size guard also bounds `string` header values (UTF-8 byte count) | [RabbitMqConsumerHost.cs:264-282](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L264-L282) |
| **smaller — consumer-tag refresh** | Subscribe RabbitMQ.Client recovery callback to refresh `_consumerTag` after auto-recovery | [RabbitMqConsumerHost.cs:182](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L182) |
| **smaller — copy-ctor exception** | `MessageRetryHandler` BasicProperties copy: explicit-field copy avoids copy-ctor throws | [MessageRetryHandler.cs:65, :120](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs#L65) |
| **smaller — header comparer** | Verify `StringComparer.Ordinal` choice on the inbound headers dict (no-op or change) | [InboundMessageProcessor.cs:55-57](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L55-L57) |
| **M1 (smaller)** | `MessageRetryHandler` upper bound `> _maxRetries` (off-by-one) | [MessageRetryHandler.cs:47](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs#L47) |

## Out of scope (routed elsewhere)

- **M2 / M5 / M13** (audit-publish OCE swallow, retry-publish failure swallow, Retry callback OCE) — landed in Phase 3.
- **H2** (`Connection.DisposeAsync` semaphore race) — Phase 6.
- **M10** (consumer-host connection-event handler subscribe vs `Connection` teardown ordering) — Phase 6.

---

## Decisions

### Q1 — H24: how should the consumer respond to broker-initiated `basic.cancel`? → Option A (mark, report unhealthy)

When the broker cancels the consumer (queue deleted, queue policy expired, mirror promoted, etc.), [`OnConsumerUnregisteredAsync`](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L386-L393) fires once. Today it logs a Warning and returns. The consumer is dead but the bus continues to run; `BusConsumingHealthCheck` keeps reporting healthy. From the operator's perspective the system has gone dark with no actionable signal.

**Option A (chosen):** Mark "consuming stopped"; report unhealthy. Set an internal flag (`_consumerCancelledByBroker`); update `BusConsumingHealthCheck` so it reports `Unhealthy` when the flag is set. Operator-driven recovery: someone restarts the host or fixes the queue and triggers a redeploy.

Option B (re-issue `BasicConsumeAsync` after re-declaring topology) was rejected: the queue-was-intentionally-deleted edge case is a real foot-gun (auto-recreating a queue an admin deliberately deleted defeats the deletion). Option C (surface a `ConsumerStopped` event) was rejected: new public API surface; most callers won't subscribe and end up worse off than Option A.

The v7 architecture has invested in the health-check pipeline as the canonical "is this bus working" signal. Routing this finding through that signal is the smallest, most operator-aligned change.

### Q2 — M3: how should unroutable retry/error publishes be handled? → Option A (mandatory:true + existing catch-and-ack)

Today the retry/error publishes use `mandatory: false` ([`MessageRetryHandler.cs:69`](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs#L69), [:124](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs#L124)). If the target queue/exchange is missing, the broker silently drops the message — no log, no notification, no signal.

**Option A (chosen):** Set `mandatory: true` on both call sites. The publish channel already has `publisherConfirmationsEnabled: true` ([RabbitMqConsumerHost.cs:130-132](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L130-L132)), so an unroutable mandatory publish raises `RabbitMQ.Client.Exceptions.PublishException`. The existing catches at [`InboundMessageProcessor.cs:150-159`](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L150-L159) and [`:205-211`](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L205-L211) already log Error and ack-to-break-the-loop. No other code change is needed beyond the two single-character flips.

Option B (auto-redeclare on unroutable) was rejected: same foot-gun as H24's Option B. Option C (nack-with-requeue on unroutable) was rejected: re-introduces the hot loop the catch-and-ack pattern was specifically designed to prevent.

---

## Fixes — behaviour spec

### H3 — Atomic `_messagesBeingProcessed` discipline

In [`RabbitMqConsumerHost.cs`](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs), replace the lock-protected `++` at line 210 with `Interlocked.Increment(ref _messagesBeingProcessed)`. The `lock (_callbackAdmissionGate)` block stays — its purpose is gating `_shutdownStarted` admission, not counter mutation:

```csharp
lock (_callbackAdmissionGate)
{
    if (_shutdownStarted)
    {
        return;
    }
    callbackAdmitted = true;
}
Interlocked.Increment(ref _messagesBeingProcessed);
```

The decrement at line 347 (`Interlocked.Decrement`) and the read at line 461 (`Volatile.Read`) stay as-is. Net effect: every mutation of `_messagesBeingProcessed` is now atomic with respect to every other mutation, regardless of whether the caller holds the admission lock.

### H24 — Broker `basic.cancel` → unhealthy

Three coordinated changes:

**1. `RabbitMqConsumerHost`** — add the cancellation flag (mirroring Phase 4's `_resetRequired` shape):

```csharp
private int _consumerCancelledByBroker;

internal bool IsCancelledByBroker => Volatile.Read(ref _consumerCancelledByBroker) != 0;
```

In [`OnConsumerUnregisteredAsync`](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L386-L393), set the flag synchronously alongside the existing log:

```csharp
private Task OnConsumerUnregisteredAsync(object? sender, ConsumerEventArgs args)
{
    Interlocked.Exchange(ref _consumerCancelledByBroker, 1);
    _logger.LogWarning(
        "AMQP consumer '{ConsumerTag}' unregistered by broker (broker-initiated shutdown) on queue '{Queue}'; reporting unhealthy via BusConsumingHealthCheck",
        _consumerTag, _queueName);
    return Task.CompletedTask;
}
```

**2. `Consumer`** — aggregate the flag across N hosts. Add to `IConsumer` (or a new `IConsumerHealthProbe` extension interface, see decision note below):

```csharp
bool IsCancelledByBroker { get; }
```

`Consumer.IsCancelledByBroker` returns `true` if any owned host reports it. Implementation: `_clients.Any(c => c.IsCancelledByBroker)`.

**Interface placement decision.** `IConsumer` lives in `ServiceConnect.Interfaces`. If the interface is part of a stable public extension-point contract that external implementations are expected to provide (check `extension-points/` website docs first), prefer adding a new `IConsumerHealthProbe` interface that the framework's `Consumer` implements — external implementations stay source-compatible. If `IConsumer` is internal-grade (only the framework's `Consumer` implements it), add the property directly. The plan re-verifies before writing.

**3. `BusConsumingHealthCheck`** — when probing, check the flag and return `Unhealthy` when true. Mirror the existing `IsConnected` and `IsHealthy` checks:

```csharp
if (consumer.IsCancelledByBroker)
{
    return HealthCheckResult.Unhealthy(
        "Consumer cancelled by broker (queue deleted, policy expired, or mirror promoted). Operator action required.",
        data: new Dictionary<string, object> { ["reason"] = "broker-cancel" });
}
```

The existing health-check shape (return early on first failure) is preserved.

### M3 — `mandatory: true` on retry/error publishes

In [`MessageRetryHandler.cs`](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs), two single-character edits:

```csharp
// Line 69 (retry publish):
await channel.BasicPublishAsync(string.Empty, retryQueueName, true, props, args.Body, cancellationToken).ConfigureAwait(false);
//                                              ^^^^ mandatory: false → true

// Line 124 (error publish):
await channel.BasicPublishAsync(_errorExchange, string.Empty, true, errorProps, args.Body, cancellationToken).ConfigureAwait(false);
//                                                            ^^^^ mandatory: false → true
```

Add a brief comment at each site explaining the contract:

```csharp
// mandatory:true so publisher confirms surface unroutable returns as PublishException;
// otherwise the broker silently drops the message and we lose the failure signal.
// The catch in InboundMessageProcessor logs Error and acks-to-break-the-loop on PublishException.
```

The existing catches at `InboundMessageProcessor.cs:150-159` and `:205-211` need a comment update to note that `PublishException` (the new failure mode) is now handled by the catch.

### M4 — Synthetic exception when handler is null

At [`InboundMessageProcessor.cs:96-98`](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L96-L98):

```csharp
if (_consumerEventHandler == null)
{
    _logger.LogError("Consumer event handler not set — message will be nacked for redelivery. Queue: {Queue}", _queueConfiguration.QueueName);
    result = new ConsumeEventResult
    {
        Success = false,
        Exception = new InvalidOperationException("Consumer event handler not set; message could not be dispatched."),
    };
}
```

Net effect: the retry handler now receives a real `Exception` instance to stamp into the error-queue header, instead of writing a JSON object with a `null` exception.

### M6 — Null-aware fallback in not-handled path

At [`InboundMessageProcessor.cs:170-173`](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L170-L173):

```csharp
// before
if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var typeNameRaw))
{
    headers.TryGetValue(HeaderKeys.TypeName, out typeNameRaw);
}

// after — mirrors the pattern at lines 87-90
if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var typeNameRaw) || typeNameRaw is null)
{
    headers.TryGetValue(HeaderKeys.TypeName, out typeNameRaw);
}
```

### M7 — Channel-state log levels + `IsOpen` pre-check

At [`RabbitMqConsumerHost.cs:296-311`](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L296-L311), restructure the finally block:

```csharp
if (callbackAdmitted)
{
    if (model == null)
    {
        // Channel was nulled by concurrent DisposeAsync. Expected during teardown;
        // broker will redeliver unacked messages on next consumer start.
        _logger.LogDebug("Channel was null during ack/nack — message {DeliveryTag} may be redelivered", args.DeliveryTag);
    }
    else if (!model.IsOpen)
    {
        // Channel closed concurrently. Expected during teardown / connection drop.
        _logger.LogDebug("Channel was closed during ack/nack — message {DeliveryTag} may be redelivered", args.DeliveryTag);
    }
    else if (Volatile.Read(ref _shutdownTimedOut) != 0)
    {
        _logger.LogDebug("Shutdown grace window expired before finishing message {DeliveryTag}; leaving unacked for broker redelivery", args.DeliveryTag);
    }
    else if (processed)
    {
        await model.BasicAckAsync(args.DeliveryTag, false).ConfigureAwait(false);
    }
    else
    {
        await model.BasicNackAsync(args.DeliveryTag, false, true).ConfigureAwait(false);
    }
}
```

The existing `AlreadyClosedException` and `ObjectDisposedException` catches stay as defence in depth (the IsOpen check is racy with concurrent teardown).

### M8 — Defensive null-check on `_messageProcessor`

At [`RabbitMqConsumerHost.cs:284`](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L284):

```csharp
// before
processed = await _messageProcessor!.ProcessAsync(publishChannel!, args, cancellationToken).ConfigureAwait(false);

// after
var messageProcessor = _messageProcessor;
if (messageProcessor == null)
{
    _logger.LogWarning("Message processor not initialised — message {DeliveryTag} will be nacked for redelivery", args.DeliveryTag);
    return;  // processed stays false; finally nacks-with-requeue
}
processed = await messageProcessor.ProcessAsync(publishChannel!, args, cancellationToken).ConfigureAwait(false);
```

`publishChannel!` keeps its null-forgiving operator because `publishChannel` is captured at the top of `EventAsync` before any `await` and is null-safe by construction. Only `_messageProcessor` needs the defensive check.

### M9 — Bind queues before `BasicConsume`

The current orchestration in [`Consumer.cs:181-185`](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs#L181-L185) calls `StartConsumingAsync` (which issues `BasicConsumeAsync`) BEFORE `ConsumeMessageTypeAsync` (which issues `QueueBindAsync`). RabbitMQ.Client requires per-channel serialisation; running a bind on a channel that already has an in-flight delivery callback violates that contract.

Split [`RabbitMqConsumerHost.StartConsumingAsync`](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L111) into three methods:

```csharp
public async Task PrepareAsync(ConsumerEventHandler messageReceived, string queueName, bool? autoDelete = null, CancellationToken cancellationToken = default)
{
    // Sets up _model, _publishChannel, _consumerEventHandler, _queueName, _retryQueueName,
    // _messageProcessor, _consumer, and the broker-event subscriptions. Does NOT call BasicConsumeAsync.
}

public async Task ConsumeMessageTypeAsync(string messageTypeName, CancellationToken cancellationToken = default)
{
    // Existing — runs on the consumer channel BEFORE BasicConsume has been issued, so the
    // documented per-channel-serialisation contract is respected.
}

public async Task BeginConsumingAsync(CancellationToken cancellationToken = default)
{
    // Issues BasicConsumeAsync. Must be called AFTER all binds are complete.
    _consumerTag = await _model!.BasicConsumeAsync(_queueName, false, "", false, false, null, _consumer!, cancellationToken).ConfigureAwait(false);
    _logger.LogDebug("Started consuming on {QueueName}, tag={ConsumerTag}", _queueName, _consumerTag);
}
```

Update [`Consumer.cs:181-185`](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs#L181-L185) to use the new shape:

```csharp
await client.PrepareAsync(eventHandler, queueName, cancellationToken: cancellationToken).ConfigureAwait(false);
foreach (string messageType in messageTypes)
{
    await client.ConsumeMessageTypeAsync(messageType, cancellationToken).ConfigureAwait(false);
}
await client.BeginConsumingAsync(cancellationToken).ConfigureAwait(false);
```

If `StartConsumingAsync` is referenced by tests / external callers, retain it as a thin wrapper that calls all three methods in order (with no message types to bind). Verify by `grep -rn "StartConsumingAsync" src/ examples/` before finalising the public surface.

### M14 — Format Retry.cs:104

At [`Retry.cs:104`](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/Retry.cs#L104):

```csharp
// before
                    throw;
                } (exceptions ??= []).Add(ex);

// after
                    throw;
                }

                (exceptions ??= []).Add(ex);
```

### M15 — Bound `string` header values too

At [`RabbitMqConsumerHost.cs:264-282`](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L264-L282):

```csharp
foreach (var kvp in inboundHeaders)
{
    int byteSize;
    if (kvp.Value is byte[] bytes)
    {
        byteSize = bytes.Length;
    }
    else if (kvp.Value is string s)
    {
        // string headers stamped by ServiceConnect (TypeName, FullTypeName, etc.) need
        // bounding too — a buggy producer could send a 100MB string and exhaust memory
        // on every consumer in the system. UTF-8 byte count matches the on-wire size.
        byteSize = System.Text.Encoding.UTF8.GetByteCount(s);
    }
    else
    {
        // Non-string, non-byte-array headers (int, bool, etc.) are size-bounded by their type.
        continue;
    }

    if (byteSize > DefaultMaxHeaderValueBytes)
    {
        await _retryHandler.HandleTerminalFailureAsync(
            publishChannel!,
            args,
            CopyInboundHeaders(args),
            new InvalidOperationException(
                $"Inbound header '{kvp.Key}' size {byteSize} bytes exceeds configured limit {DefaultMaxHeaderValueBytes} bytes."),
            GetShutdownPublishToken())
            .ConfigureAwait(false);
        processed = true;
        return;
    }
}
```

### Smaller — consumer-tag refresh on auto-recovery

RabbitMQ.Client v7's auto-recovery re-issues the consumer tag on reconnect. The cached `_consumerTag` would be stale, breaking later `BasicCancelAsync` calls during `DisposeAsync`.

In `StartConsumingAsync` / `BeginConsumingAsync` (post-M9 split), subscribe to the recovery callback. The exact API name in RabbitMQ.Client v7 needs verification — check `IConnection.RecoverySucceededAsync` first, then `IChannel.RecoveringAsync` / `RecoveredAsync`. The plan's first task verifies the API and adapts.

Sketch (verify event name before writing):

```csharp
if (_connection.UnderlyingConnection is IRecoverable recoverable)
{
    recoverable.RecoverySucceeded += (sender, args) =>
    {
        // After auto-recovery the broker has re-issued our consumer; refresh the cached tag.
        // RabbitMQ.Client tracks the tag-to-consumer mapping internally; we read it back.
        _consumerTag = _consumer?.ConsumerTags.FirstOrDefault();
        _logger.LogDebug("Consumer tag refreshed after auto-recovery: {ConsumerTag}", _consumerTag);
    };
}
```

If the API doesn't expose this in a usable shape, document the limitation in a code comment and skip the fix; the consumer-tag staleness manifests only on `DisposeAsync` after auto-recovery, which is a narrow window.

### Smaller — `MessageRetryHandler` BasicProperties copy

At [`MessageRetryHandler.cs:65, :120`](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs#L65), replace the copy-constructor with explicit field copying. The exact field list depends on `RabbitMQ.Client.BasicProperties` in v7; verify by reading the type before writing. Standard AMQP BASIC properties:

```csharp
var props = new BasicProperties
{
    ContentType = args.BasicProperties.ContentType,
    ContentEncoding = args.BasicProperties.ContentEncoding,
    DeliveryMode = args.BasicProperties.DeliveryMode,
    Priority = args.BasicProperties.Priority,
    CorrelationId = args.BasicProperties.CorrelationId,
    ReplyTo = args.BasicProperties.ReplyTo,
    Expiration = args.BasicProperties.Expiration,
    MessageId = args.BasicProperties.MessageId,
    Timestamp = args.BasicProperties.Timestamp,
    Type = args.BasicProperties.Type,
    UserId = args.BasicProperties.UserId,
    AppId = args.BasicProperties.AppId,
    Headers = HeaderHelpers.ToNullableHeaders(headers),
};
```

If `BasicProperties` exposes `IsXxxPresent` flags, the explicit copy can be conditional to avoid stamping fields that weren't set on the source. The plan verifies the v7 type shape before writing.

### Smaller — header-comparer verification

At [`InboundMessageProcessor.cs:55-57`](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L55-L57) the headers dict uses `StringComparer.Ordinal`. The phase doc flags this as potentially mismatching case-insensitive HTTP-style consumers downstream. AMQP header keys are case-sensitive on the wire, but the question is whether downstream filter / middleware code expects ordinal lookup.

**Plan task:** read all callers / consumers of the dispatch headers dict (handler delegates, filters, middleware, outgoing-filter pipelines) and answer:
- Are any of them doing case-insensitive lookup that depends on the dict's comparer?
- If yes: change to `StringComparer.OrdinalIgnoreCase` and add a regression test.
- If no: keep `StringComparer.Ordinal`, add a code comment justifying the choice.

This is a verification-then-decision task, not a mechanical fix. The plan documents both possible outcomes.

### M1 (smaller) — Off-by-one in retry-count validation

At [`MessageRetryHandler.cs:47`](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs#L47):

```csharp
// before
if (candidate < 0 || candidate > _maxRetries + 1)

// after
if (candidate < 0 || candidate > _maxRetries)
```

The legitimate range for `RetryCount` in inbound headers is `[0, _maxRetries]`. A value of `_maxRetries + 1` is malformed (we never stamp it) and should route via the existing "Malformed or out-of-range" warning path.

---

## Tests

Default unit-test location: `src/ServiceConnect.UnitTests/RabbitMQ/`. Default E2E location: `src/ServiceConnect.EndToEndTests/RabbitMq/`. Cross-package Bus / health-check tests under `src/ServiceConnect.UnitTests/Bus/` or `src/ServiceConnect.UnitTests/HealthChecks/`.

### H3 — Concurrent counter discipline (unit)

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostInflightCounterTests.cs` (new).

Build a `RabbitMqConsumerHost` with a fake `IServiceConnectConnection` that yields a Moq `IChannel`. Either drive deliveries through the actual `BasicConsumeAsync` → `_consumer.HandleBasicDeliverAsync` path, or expose an internal `RaiseDeliveryForTests` seam.

Test: `EventAsync_ConcurrentDeliveriesAndDrains_CounterReachesZero_NeverNegative`:
- 8 concurrent tasks, each pushing 100 deliveries.
- Handler delegate yields (`await Task.Yield()`) before completing to maximise interleaving.
- Background poller samples `Volatile.Read(ref _messagesBeingProcessed)` every 1ms; if any sample is `< 0`, fail immediately.
- After all tasks complete and a brief settle window, assert the counter reads exactly 0.

Pre-fix: counter goes negative or overshoots intermittently. Post-fix: deterministically zero at drain.

### H24 — Broker `basic.cancel` propagates to health (unit + E2E)

**Unit file (host):** `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostBrokerCancelTests.cs` (new).
- `OnUnregistered_SetsCancelledFlag` — invoke the unregistered handler (via internal seam or reflection) with synthetic `ConsumerEventArgs`. Assert `host.IsCancelledByBroker == true`.
- `BeforeUnregistered_FlagIsFalse` — belt-and-braces guard.

**Unit file (health check):** `src/ServiceConnect.UnitTests/HealthChecks/BusConsumingHealthCheckTests.cs` (extend if exists, else new).
- `Probe_ConsumerCancelledByBroker_ReportsUnhealthy` — mock `IConsumer` with `IsCancelledByBroker = true`. Assert `HealthCheckResult.Status == Unhealthy`. The result description should mention "broker-cancel" or equivalent so operators can grep.
- `Probe_NoConsumersCancelled_ConsumerHealthy_ReportsHealthy` — happy path.

**E2E file:** `src/ServiceConnect.EndToEndTests/RabbitMq/ConsumerBrokerCancelE2ETests.cs` (new).
1. Start a `Bus` with a consumer on a Testcontainers RabbitMQ broker.
2. Open a separate management channel, delete the consumer's queue (`channel.QueueDeleteAsync(queueName, ifUnused: false, ifEmpty: false)`).
3. Wait up to 5s, polling `BusConsumingHealthCheck`.
4. Assert: `HealthCheckResult.Status == Unhealthy` within 5s.

### M3 — `mandatory: true` (unit + E2E)

**Unit file:** `src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerMandatoryTests.cs` (new).
- Capture `mandatory` argument via Moq Callback. Assertions:
  - `HandleFailureAsync_RetryPath_PublishesMandatoryTrue`
  - `HandleFailureAsync_MaxRetriesPath_PublishesMandatoryTrue`
  - `HandleTerminalFailureAsync_PublishesMandatoryTrue`

**E2E file:** `src/ServiceConnect.EndToEndTests/RabbitMq/UnroutableRetryPublishE2ETests.cs` (new).
1. Set up retry topology (via `RabbitMqTopologyProvisioner`).
2. Delete the retry queue out of band.
3. Force a handler failure on the next delivery.
4. Assert: a `PublishException` was logged at Error level (capture via `LoggerStub` or `XunitLoggerProvider`). The original message was acked (no broker redelivery within 2s of the handler failure). No silent drop, no infinite loop.

### M4 — Null handler attaches synthetic exception (unit)

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorNullHandlerTests.cs` (new).
- Build `InboundMessageProcessor` with `consumerEventHandler: null`.
- Mock `MessageRetryHandler.HandleFailureAsync`, capturing the `Exception` argument.
- Process a delivery. Assert: captured exception is `InvalidOperationException` with message containing `"Consumer event handler not set"`.

### M6 — Not-handled fallback handles null value (unit)

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorNotHandledFallbackTests.cs` (new).
- Build `InboundMessageProcessor` with `deadLetterUnhandledMessages: true`, `errorsDisabled: false`.
- Process a delivery with `FullTypeName = null` (key present, value null) and `TypeName = "Foo.Bar"`.
- Mock `MessageRetryHandler.HandleTerminalFailureAsync`, capture exception.
- Assert: exception message contains `"Foo.Bar"`. Pre-fix: contains `"<unknown>"`.

### M7 — Channel-state log levels (unit)

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostAckNackTests.cs` (extend or new).
- `EventAsync_ChannelNullDuringFinally_LogsAtDebug` — set `_model = null` after admission via reflection. Assert log captured at `LogLevel.Debug`, not Warning.
- `EventAsync_ChannelClosedDuringFinally_LogsAtDebug_NoAckCalled` — set `model.IsOpen` to return `false`. Assert no ack/nack call (channel.Verify), Debug log emitted.

### M8 — Null `_messageProcessor` defensive path (unit)

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostMessageProcessorNullTests.cs` (new).
- Drive the host through `EventAsync` with `_messageProcessor = null` (via reflection).
- Assert: no NRE; Warning log emitted; the message follows the nack-with-requeue path in the finally.

### M9 — Bind-before-consume ordering (unit)

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostBindOrderingTests.cs` (new).
- Mock `IChannel`. Track call order: every `QueueBindAsync` call records sequence ID N; every `BasicConsumeAsync` call records sequence ID N.
- Drive through the new `Consumer.StartConsumingAsync` orchestration (Prepare → Bind → BeginConsuming).
- Assert: every `QueueBindAsync` sequence ID < the `BasicConsumeAsync` sequence ID.

### M14 — Formatting

No test (mechanical). The existing `Retry.cs` test suite continues to pass.

### M15 — String-header size guard (unit)

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostHeaderSizeTests.cs` (new or extend).
- `EventAsync_LargeStringHeader_RoutesToTerminalFailure` — pass `Headers = { ["Foo"] = new string('x', 9000) }`. Assert `HandleTerminalFailureAsync` invoked with exception containing `"Foo"` and the actual byte count.
- `EventAsync_SmallStringHeader_PassesAdmission` — pass `Headers = { ["Foo"] = "small" }`. Assert: not routed to terminal failure.
- Pre-existing byte-array test stays unchanged.

### Smaller — consumer-tag refresh (unit)

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostRecoveryTests.cs` (new).

If RabbitMQ.Client v7 exposes `RecoverySucceeded` or equivalent in a testable shape: build a host with a fake recoverable connection, fire the recovery event, assert `_consumerTag` is refreshed to the new value via reflection.

If the API isn't testable in unit-test isolation: skip the test, add an integration-class test (or rely on manual broker-restart verification) and document the limitation in a code comment.

### Smaller — Explicit-field BasicProperties copy (unit)

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerCopyPropsTests.cs` (new).

Build a source `BasicProperties` with all relevant fields populated. Call `HandleFailureAsync`. Capture the `BasicProperties` passed to `BasicPublishAsync`. Assert: every populated field on the source is preserved on the copy. (Locks in field-by-field copy correctness; protects against silent field drops when `BasicProperties` shape evolves.)

### Smaller — Header-comparer verification

No test (verification task in plan). The plan documents the conclusion (Ordinal vs OrdinalIgnoreCase) and either adds a code-comment justification (if Ordinal) or a regression test (if OrdinalIgnoreCase).

### M1 (smaller) — Off-by-one retry-count

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerRetryCountValidationTests.cs` (new or extend).
- `RetryCount_EqualsMaxRetries_RoutesToErrorPublish` — header `RetryCount = _maxRetries`. Assert: routed via the legitimate error-publish path (not "malformed").
- `RetryCount_GreaterThanMaxRetries_RoutedToErrorAsMalformed` — header `RetryCount = _maxRetries + 1`. Assert: routed via the "Malformed or out-of-range" warning path. Pre-fix: passed validation.

### Build / test discipline

```bash
# Per-csproj only — never whole-solution.
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1

dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~Consumer|FullyQualifiedName~Retry|FullyQualifiedName~Inbound|FullyQualifiedName~Bus|FullyQualifiedName~HealthCheck" \
    -m:1

dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj \
    --filter "FullyQualifiedName~ConsumerBrokerCancel|FullyQualifiedName~UnroutableRetryPublish" \
    -m:1
```

E2E budget: two new E2E tests. ~5-10s Testcontainers spin-up each. Total E2E delta < 30s.

---

## Rollout

### Commit ordering

| # | Commit | Findings | Why this position |
| --- | --- | --- | --- |
| 1 | `docs(spec): phase 05 RabbitMQ consumer + host` | — | Spec lands first. |
| 2 | `docs(plan): phase 05 implementation plan` | — | Plan after spec. |
| 3 | `style(retry): split collapsed statements at Retry.cs:104` | M14 | Trivial; confirms pipeline before substantive changes. |
| 4 | `fix(retry): reject RetryCount > maxRetries (off-by-one)` | M1 (smaller) | Local to `MessageRetryHandler`. |
| 5 | `fix(retry): explicit-field BasicProperties copy` | smaller | Local to `MessageRetryHandler`. |
| 6 | `fix(processor): null-aware FullTypeName fallback in not-handled path` | M6 | Local to `InboundMessageProcessor`. |
| 7 | `fix(processor): attach synthetic exception when consumer event handler is null` | M4 | Local to `InboundMessageProcessor`. |
| 8 | `fix(consumer-host): bound string header values too` | M15 | Local to `RabbitMqConsumerHost`. |
| 9 | `fix(consumer-host): demote ack/nack-on-null log to Debug; add IsOpen pre-check` | M7 | Local to `RabbitMqConsumerHost`. |
| 10 | `fix(consumer-host): defensive null-check on _messageProcessor` | M8 | Local to `RabbitMqConsumerHost`. |
| 11 | `fix(consumer-host): atomic _messagesBeingProcessed discipline` | **H3** | Concurrency fix. Lands alone for reviewer focus. |
| 12 | `fix(retry): mandatory:true on retry/error publishes` | **M3** | Spans `MessageRetryHandler` + tests. |
| 13 | `fix(consumer): bind queues before BasicConsume` | **M9** | Restructures `StartConsumingAsync` into Prepare/Bind/Begin. |
| 14 | `fix(consumer-host): refresh consumer tag on auto-recovery` | smaller | Localised. |
| 15 | `verify(processor): document StringComparer.Ordinal rationale` | smaller | Either no-op + comment or comparer change with regression test. |
| 16 | `feat(consumer-host): expose IsCancelledByBroker; report unhealthy on broker basic.cancel` | **H24** | Cross-package change (consumer host + `BusConsumingHealthCheck`). Riskiest commit; lands second-to-last. |
| 17 | `test(consumer): E2E coverage for broker cancel + unroutable retry publish` | — | Two new E2E tests. |
| 18 | `docs(website): phase 05 doc sweep — consumer recovery, broker cancel, retry topology` | — | Doc-only sweep. |
| 19 | `test(consumer): final regression gate` | — | Verification commit if any cross-cutting tests get added during review. May be empty. |

Each `fix(...)` commit lands TDD-style: failing test → minimal fix → passing test → commit.

### Documentation updates (commit 18)

- `website/src/content/docs/learn/operations/observability.mdx` — document the broker-cancel signal: when the broker cancels the consumer, the bus reports `Unhealthy` via `BusConsumingHealthCheck`. Operator action: investigate, fix the queue, restart the host.
- `website/src/content/docs/reference/healthchecks/` — `BusConsumingHealthCheck` reference: add the `Consumer cancelled by broker` failure mode to the documented set of unhealthy reasons.
- `website/src/content/docs/learn/operations/error-handling.mdx` — clarify retry-publish behaviour when topology is missing: with `mandatory:true`, an unroutable publish is logged at Error and the message is acked to break the loop.
- `website/src/content/docs/releases.mdx` — v7 release notes: "RabbitMQ consumer-host hardening" section covering: (1) atomic in-flight counter; (2) broker-cancel reports unhealthy (behaviour change worth a callout); (3) retry/error publishes now `mandatory:true`; (4) bind-before-consume; (5) consumer-tag refresh; (6) the eight smaller items as a single bullet.

### Examples / READMEs verification

```bash
grep -rn "consumer.*healthy\|broker.*cancel\|consumer recovery\|retry queue\|mandatory" examples/ README.md 2>/dev/null
```

If any sample asserts "consumer is healthy ⇒ messages are flowing" without acknowledging broker-cancel, fix in place. Otherwise no edits.

### Verification gate before final code review

1. Per-csproj build of `ServiceConnect.Client.RabbitMQ` + `ServiceConnect` clean.
2. `dotnet test src/ServiceConnect.UnitTests/ --filter "..." -m:1` — all pass.
3. `dotnet test src/ServiceConnect.EndToEndTests/ --filter "..." -m:1` — all pass against Testcontainers RabbitMQ.
4. Astro build clean.
5. Grep verification:
   - `grep -n "_messagesBeingProcessed++" src/ServiceConnect.Client.RabbitMQ/` — zero hits (H3).
   - `grep -n "BasicPublishAsync.*false" src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs` — zero hits in retry/error sites (M3).
   - `grep -n "IsCancelledByBroker" src/ServiceConnect.Client.RabbitMQ/ src/ServiceConnect/` — at least 3 hits (H24).
6. Final code review via `superpowers:code-reviewer` (opus) across all phase 05 commits.

### Branch

Stays on `v7-clean-architecture`. Single branch, sequential commits, mirrors Phases 1-4.
