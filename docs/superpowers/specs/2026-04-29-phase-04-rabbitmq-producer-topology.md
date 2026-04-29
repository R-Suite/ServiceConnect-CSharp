# Phase 04 — RabbitMQ producer + topology (spec)

**Phase doc:** [`consolidated-issues/phases/phase-04-rabbitmq-producer-topology.md`](../../../consolidated-issues/phases/phase-04-rabbitmq-producer-topology.md)

**Branch:** `v7-clean-architecture`.

## Goal

Stop silent message loss on the producer + topology side and remove the publish-lock latency hazard. Two Critical findings (multi-endpoint `MessageId` reuse, retry-DLX `autoDelete` mismatch), three High findings (retry-arg crash, `MessageType` header overwrite, reconnect-under-publish-lock), two Medium findings, two Smaller items, scoped to `ServiceConnect.Client.RabbitMQ` with one cross-package decision in `ServiceConnect.Bus`.

---

> **Note on file-line anchors:** All line numbers below reference the *current* tree (post-Phase-3, pre-Phase-4). Sequential commits will shift them. The plan re-grounds anchors per task; readers should `git grep` symbol names rather than chase line numbers across commits.

## Findings in scope

| ID | One-line | Site |
| --- | --- | --- |
| **C11** | Multi-endpoint `SendAsync` reuses `MessageId`/`TimeSent` per delivery | [Producer.cs:220-235](../../../src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L220-L235) |
| **C12** | Retry DLX `autoDelete` mismatch silently drops messages | [RabbitMqTopologyProvisioner.cs:164,196](../../../src/ServiceConnect.Client.RabbitMQ/Topology/RabbitMqTopologyProvisioner.cs#L164) |
| **H1** | Retry-queue arg merge throws on caller-supplied AMQP keys | [RabbitMqTopologyProvisioner.cs:188-192](../../../src/ServiceConnect.Client.RabbitMQ/Topology/RabbitMqTopologyProvisioner.cs#L188-L192) |
| **H21** | `MessageType` header semantics — Option B (operation-name authoritative) | [Bus.cs:617,651,730](../../../src/ServiceConnect/Bus.cs#L617) |
| **H22** | Reconnect under `_publishLock` — Option A (mark + defer) | [Producer.cs:447](../../../src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L447) |
| **M11** | Null guards on `Producer` public publish/send methods | [Producer.cs:153,202,257,302](../../../src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L153) |
| **M12** | `OutboundHeaderBuilder.Priority` swallows conversion failures with insufficient context | [OutboundHeaderBuilder.cs:96-105](../../../src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs#L96-L105) |
| **smaller** | `OutboundHeaderBuilder` overwrites caller-supplied reserved headers silently | [OutboundHeaderBuilder.cs:41-47](../../../src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs#L41-L47) |
| **smaller** | `ConnectionFactoryBuilder` `Convert.ToInt32` opaque error | [ConnectionFactoryBuilder.cs:30-32,86](../../../src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs#L30-L32) |

## Out of scope (routed elsewhere)

- **H23** (`DisposeConnectionAsync` unbounded wait) — landed in Phase 3.
- **R3 / H2** (`Connection.DisposeAsync` race) — Phase 6.
- **R7** (`EnsureExchangeDeclaredAsync` race with channel teardown) — currently single-threaded behind `_publishLock`. **H22 makes this load-bearing**: with Option A, `EnsureConnectedAsync` may now reconnect concurrent with another publisher entering the lock. The fix stays in Phase 6, but this phase ships an E2E regression test (see Section 3, "H22 supplementary E2E") that exercises the worst-case interleaving so any new race surfaces fast.
- **M2 / M5** (audit/retry OCE handling) — landed in Phase 3.
- **R10** (`MessageRetryHandler` `BasicProperties` copy) — Phase 11; small enough to fold here only if a reviewer flags it as touching the same files. Don't pull in proactively.

---

## Decisions

### Q1 — H21: `MessageType` header semantics → Option B (operation-name authoritative)

The header is currently stamped twice: Bus stamps `type.FullName` ([Bus.cs:651](../../../src/ServiceConnect/Bus.cs#L651), [:730](../../../src/ServiceConnect/Bus.cs#L730)); `OutboundHeaderBuilder` ([:57](../../../src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs#L57)) overwrites with `"Publish"|"Send"|"ByteStream"`. The Bus's stamp is dead code — never observed on the wire. Consumers ([StreamProcessor.cs:81](../../../src/ServiceConnect/Services/Processors/StreamProcessor.cs#L81), [MessageAuditPublisher.cs:59](../../../src/ServiceConnect.Client.RabbitMQ/Audit/MessageAuditPublisher.cs#L59)) compare `MessageType == "ByteStream"`; they already operate on operation-name semantics today.

**Option B (chosen):** Bus stops stamping `MessageType` and removes it from the reserved-headers set. The header is documented as "operation name". Type info lives in `TypeName` (FullName) and `FullTypeName` (AQN). No consumer-side code change. No new header. No wire-format change.

Option A (Producer stops stamping; `MessageType = FullName`; new `ServiceConnect.Operation` header for the operation flag) was rejected: bigger blast radius, breaks any external auditor matching on the on-wire `"ByteStream"` value, no compensating benefit.

### Q2 — H22: how to stop the publish lock from being held during reconnect → Option A (mark, don't reconnect under lock)

Today `PublishWithTimeoutAsync` calls `ReconnectAsync` from inside its OCE catch ([Producer.cs:447](../../../src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L447)) while `_publishLock` is held by `PublishAsync`/`SendAsync`/`SendBytesAsync`. With defaults (`RetryCount=60`, `RetrySeconds=10`), worst case is ~10 minutes of blocked publishers per timeout.

**Option A (chosen):** add a `_resetRequired` flag to `ProducerConnection` set synchronously by `MarkResetRequired()` from inside `PublishWithTimeoutAsync`'s catch. `EnsureConnectedAsync` (always called *before* `_publishLock.WaitAsync`) consumes the flag and drives the reconnect off-lock. The `TimeoutException` returns to the caller as fast as the timeout itself; the next publish absorbs the reconnect cost — but does so off-lock, so concurrent publishers aren't blocked.

Option B (release/re-acquire `_publishLock` around reconnect) was rejected: inverts the lock invariant; subtle and easy to break later. Option C (fire-and-forget reset task) was rejected: races with `DisposeAsync` and concurrent publishers may grab the still-open old channel before the reset lands.

---

## Fixes — behaviour spec

### C11 — re-mint per-delivery identity in multi-endpoint Send

In `Producer.SendAsync(Type, byte[], headers, ct)` ([Producer.cs:220-235](../../../src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L220-L235)), keep the single `BuildHeaders` call outside the loop (preserves type / source / consumer-type stamps). Inside the `foreach (string endPoint in endPoints)` loop, after assigning `DestinationAddress`, overwrite `MessageId` and `TimeSent` *before* `BuildBasicProperties` runs:

```csharp
foreach (string endPoint in endPoints)
{
    baseHeaders[HeaderKeys.DestinationAddress] = endPoint;
    baseHeaders[HeaderKeys.MessageId] = Guid.NewGuid().ToString();
    baseHeaders[HeaderKeys.TimeSent] = OutboundHeaderBuilder.FormatTimestamp(_timeProvider.GetUtcNow().UtcDateTime);
    var basicProperties = _headerBuilder.BuildBasicProperties(baseHeaders);
    // ... existing ExecuteWithConnectionRetryAsync/PublishWithTimeoutAsync ...
}
```

`FormatTimestamp` is currently `private static`; promote to `internal static` (the assembly already exposes internals to `ServiceConnect.UnitTests`).

`CorrelationId` (Bus-stamped) stays constant — it's the logical correlation across the fan-out. `TypeName` / `FullTypeName` / `MessageType` / `SourceAddress` / `SourceMachine` / `ConsumerType` / `Language` stay constant. Each delivery gets a distinct on-wire `MessageId` and `TimeSent`.

### C12 — DLX always `autoDelete: false`

In [`RabbitMqTopologyProvisioner.ConfigureRetryTopologyAsync`](../../../src/ServiceConnect.Client.RabbitMQ/Topology/RabbitMqTopologyProvisioner.cs#L149), change line 164 from:

```csharp
await channel.ExchangeDeclareAsync(retryDeadLetterExchangeName, ExchangeType.Direct, durable, autoDelete, null, cancellationToken: cancellationToken)
```

to:

```csharp
// Retry DLX is always autoDelete:false: it must outlive any individual queue lifecycle so
// retried messages always have somewhere to land. The caller-supplied `autoDelete` parameter
// continues to govern the main queue (declared elsewhere) but the retry DLX is invariant.
await channel.ExchangeDeclareAsync(retryDeadLetterExchangeName, ExchangeType.Direct, durable, autoDelete: false, null, cancellationToken: cancellationToken)
```

The retry queue at line 196 is already `autoDelete: false`; no change there.

### H1 — argument-merge: framework wins, caller never crashes

[`RabbitMqTopologyProvisioner.cs:188-192`](../../../src/ServiceConnect.Client.RabbitMQ/Topology/RabbitMqTopologyProvisioner.cs#L188-L192): replace the collection-initializer block with explicit indexer assignment:

```csharp
Dictionary<string, object?> arguments = new(retryQueueArguments, StringComparer.Ordinal);

// Framework values for these two keys are non-negotiable: they wire the retry queue to the
// retry DLX with the configured TTL. Any caller-supplied value is overridden silently except
// for a Debug log so config drift surfaces without polluting Information.
LogIfOverriding(RabbitMqQueueNaming.XDeadLetterExchangeArgument, arguments, retryDeadLetterExchangeName);
LogIfOverriding(RabbitMqQueueNaming.XMessageTtlArgument, arguments, retryDelayMs);

arguments[RabbitMqQueueNaming.XDeadLetterExchangeArgument] = retryDeadLetterExchangeName;
arguments[RabbitMqQueueNaming.XMessageTtlArgument] = retryDelayMs;
```

Helper:

```csharp
private void LogIfOverriding<T>(string key, IReadOnlyDictionary<string, object?> existing, T frameworkValue)
{
    if (existing.TryGetValue(key, out var existingValue) && !Equals(existingValue, frameworkValue))
    {
        _logger.LogDebug(
            "Overriding caller-supplied retry-queue argument {Key} (was '{ExistingValue}') with framework value '{FrameworkValue}'",
            key, existingValue, frameworkValue);
    }
}
```

`Add` semantics never throw on these two keys again. Caller-supplied values for *other* AMQP keys (e.g. `x-max-length`, `x-queue-mode`) flow through unchanged.

### H21 — Bus drops the dead `MessageType` stamp (Option B)

Three site changes in `Bus.cs`:

1. [Bus.cs:617](../../../src/ServiceConnect/Bus.cs#L617): remove `HeaderKeys.MessageType` from the `ReservedHeaders` set.
2. [Bus.cs:651](../../../src/ServiceConnect/Bus.cs#L651): delete the `envelope.Headers[HeaderKeys.MessageType] = messageType.FullName ?? messageType.Name;` line in `CreateEnvelope`.
3. [Bus.cs:730](../../../src/ServiceConnect/Bus.cs#L730): delete the equivalent line in the `SendWithMiddleware` path.

Update the comment at [Bus.cs:650](../../../src/ServiceConnect/Bus.cs#L650) ("Outgoing filters and middleware rely on MessageId / CorrelationId / MessageType being present") to drop `MessageType` from the rely-on list.

`OutboundHeaderBuilder.cs:57` keeps stamping operation name as today. The reserved-headers set retains `MessageId` and `CorrelationId`. No consumer-side code change.

Doc updates land in commit 11 (Section 4):
- `website/src/content/docs/reference/messages/` header-keys page: reposition `MessageType` semantics from "type FullName" to "operation name (`Publish`|`Send`|`ByteStream`)"; cross-link to `TypeName` / `FullTypeName` for type info.
- v7 release notes: behaviour-changes section noting "no observable wire change; downstream auditors who expected `MessageType` to be a FullName based on out-of-date docs should switch to `FullTypeName`."

### H22 — Mark, don't reconnect under lock (Option A)

Three coordinated changes:

**1. `ProducerConnection`** — add an internal reset-required flag:

```csharp
// Set by Producer.PublishWithTimeoutAsync when a publish times out (broker confirm did not
// arrive within the publish budget). The next EnsureConnectedAsync drives the reconnect off
// the publish lock so concurrent publishers are not blocked behind a worst-case retry budget.
private int _resetRequired;

internal void MarkResetRequired() => Interlocked.Exchange(ref _resetRequired, 1);

public async Task EnsureConnectedAsync(CancellationToken cancellationToken)
{
    // ... existing connect-if-not-connected logic ...

    // Atomically consume the flag so concurrent publishers don't double-reconnect. The
    // ReconnectAsync call below already serialises internally via _connectionLock.
    if (Interlocked.Exchange(ref _resetRequired, 0) == 1)
    {
        await ReconnectAsync(
            new InvalidOperationException("Channel reset required after publish timeout"),
            cancellationToken).ConfigureAwait(false);
    }
}
```

**2. `Producer.PublishWithTimeoutAsync`** — replace the in-catch reconnect at [Producer.cs:447](../../../src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L447):

```csharp
// Mark the channel for reset on the next publish. The reset runs inside EnsureConnectedAsync,
// which is called BEFORE _publishLock.WaitAsync, so concurrent publishers are not blocked
// behind it. We do NOT reconnect here: doing so would hold _publishLock for up to
// retryCount * retrySeconds (default 60 * 10s = 10 minutes) blocking every other publisher.
_producerConnection.MarkResetRequired();

throw new TimeoutException(
    $"BasicPublishAsync exceeded the configured publish timeout of {_publishTimeout.TotalSeconds:0.###}s " +
    $"(exchange='{exchange}', routingKey='{routingKey}', messageId='{basicProperties.MessageId ?? "<none>"}'). " +
    "The broker may be stalled or the connection may be half-open.");
```

The `try { await _producerConnection.ReconnectAsync(...) } catch { _logger.LogError(...) }` block at [Producer.cs:447-448](../../../src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L447) is removed entirely — `MarkResetRequired` cannot fail.

**3. Call-site contract** — `EnsureConnectedAsync` is already invoked at the top of every public publish/send method *before* `_publishLock.WaitAsync` ([Producer.cs:159](../../../src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L159), [:208](../../../src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L208), [:263](../../../src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L263), [:308](../../../src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L308)). No call-site changes. Net effect: a publish timeout returns to the caller as fast as the timeout itself; the next publish (any caller) drives the reset off-lock.

R7 exposure: `EnsureExchangeDeclaredAsync` is now more frequently called in close proximity to a reconnect (since the next publish reconnects then declares). The race surface is unchanged in code but exercised more often. Ship the H22 supplementary E2E test (Section 3) as a regression catch; the proper fix stays in Phase 6.

### M11 — null guards on public publish/send entry points

Add `ArgumentNullException.ThrowIfNull` for `type` and `message` (or `packet`) at the head of each public method:

```csharp
public async Task PublishAsync(Type type, byte[] message, IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(type);
    ArgumentNullException.ThrowIfNull(message);
    // ... existing body ...
}
```

Apply at [Producer.cs:150](../../../src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L150) (`PublishAsync`), [:199](../../../src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L199) (`SendAsync(Type, byte[], ...)`), [:249](../../../src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L249) (`SendAsync(string, Type, byte[], ...)`), [:294](../../../src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L294) (`SendBytesAsync`). Existing `string.IsNullOrWhiteSpace(endPoint)` guards stay unchanged. Order: type first, then message — matches parameter order.

### M12 — Priority conversion: log enough context, narrow the catch

[`OutboundHeaderBuilder.cs:96-105`](../../../src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs#L96-L105): replace:

```csharp
if (messageHeaders.TryGetValue(HeaderKeys.Priority, out var priority))
{
    try
    {
        basicProperties.Priority = Convert.ToByte(priority, System.Globalization.CultureInfo.InvariantCulture);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error setting message priority");
    }
}
```

with:

```csharp
if (messageHeaders.TryGetValue(HeaderKeys.Priority, out var priority))
{
    try
    {
        basicProperties.Priority = Convert.ToByte(priority, System.Globalization.CultureInfo.InvariantCulture);
    }
    // RabbitMQ priorities are advisory — failing the publish over a misconfigured priority is
    // the wrong default. Soft-drop with enough context that the operator can see which value
    // was bad and why. Catch only the conversion exceptions; anything else propagates.
    catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
    {
        _logger.LogError(
            ex,
            "Could not set message priority from value '{Value}' (type '{ValueType}'); priority must be convertible to byte (0..255). Continuing without priority.",
            priority,
            priority?.GetType().FullName ?? "<null>");
    }
}
```

### Smaller — caller-supplied reserved-header overwrite gets a warning

`OutboundHeaderBuilder.BuildHeaders` ([:33](../../../src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs#L33)). Add a static `OverwrittenHeaderKeys` set listing the keys this builder stamps after copying caller headers in:

```csharp
// Producer-stamped keys: callers cannot override these (the framework owns them). MessageId is
// deliberately NOT in this set — caller-supplied MessageId (e.g. Bus's authoritative stamp) is
// preserved by the !ContainsKey check in BuildHeaders.
private static readonly HashSet<string> OverwrittenHeaderKeys = new(StringComparer.Ordinal)
{
    HeaderKeys.DestinationAddress,
    HeaderKeys.MessageType,
    HeaderKeys.SourceAddress,
    HeaderKeys.TimeSent,
    HeaderKeys.SourceMachine,
    HeaderKeys.TypeName,
    HeaderKeys.FullTypeName,
    HeaderKeys.ConsumerType,
    HeaderKeys.Language,
};
```

Inside the caller-headers copy loop (lines 41-47), check each key:

```csharp
if (headers is not null)
{
    foreach (var kvp in headers)
    {
        if (OverwrittenHeaderKeys.Contains(kvp.Key))
        {
            _logger.LogWarning(
                "Caller-supplied reserved header '{Key}' will be overwritten by the framework",
                kvp.Key);
            continue;
        }
        result[kvp.Key] = kvp.Value;
    }
}
```

Wording mirrors [Bus.cs:642](../../../src/ServiceConnect/Bus.cs#L642) for cross-package consistency.

### Smaller — `ConnectionFactoryBuilder` opaque conversion errors

[`ConnectionFactoryBuilder.cs`](../../../src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs): add a private helper and apply at both conversion sites.

```csharp
private static int ConvertSettingToInt32(string key, object? value)
{
    try
    {
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }
    catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
    {
        throw new InvalidOperationException(
            $"Setting '{key}' must be convertible to Int32; got value '{value}' of type '{value?.GetType().FullName ?? "<null>"}'.",
            ex);
    }
}
```

Replace [line 32](../../../src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs#L32):
```csharp
var port = explicitPortConfigured
    ? ConvertSettingToInt32(RabbitMQSettingKeys.Port, portVal)
    : AmqpTcpEndpoint.UseDefaultPort;
```

Replace [line 86](../../../src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs#L86):
```csharp
return TimeSpan.FromSeconds(ConvertSettingToInt32(RabbitMQSettingKeys.HeartbeatTime, timeRaw));
```

---

## Tests

Default unit-test location: `src/ServiceConnect.UnitTests/RabbitMQ/`. Default E2E location: `src/ServiceConnect.EndToEndTests/RabbitMq/`. Cross-package Bus tests under `src/ServiceConnect.UnitTests/Bus/`.

### C11 — multi-endpoint MessageId distinctness (unit)

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/ProducerMultiEndpointSendTests.cs` (new).

Build a `Producer` with a queue config that maps a fake message type to three endpoints. Replace `_producerConnection.Channel` with a Moq `IChannel` whose `BasicPublishAsync` captures the `BasicProperties` argument list. Use `FakeTimeProvider` and advance the clock by 1 ms per `BasicPublishAsync` invocation via `.Callback(() => fakeClock.Advance(...))` so `TimeSent` differs across iterations.

Key test: `SendAsync_FanOutToThreeEndpoints_EachDeliveryHasDistinctMessageId`:

- Three captured calls.
- `captures.Select(p => p.MessageId).Distinct().Count() == 3`.
- `captures.Select(p => p.Headers[HeaderKeys.TimeSent]).Distinct().Count() == 3`.
- `captures.Select(p => p.Headers[HeaderKeys.CorrelationId]).Distinct().Count() == 1` (correlation stays constant).

Pre-existing `Publish`/`Send` tests for single-delivery `MessageId` keep their current shape; we don't regress them.

### C12 — DLX outlives auto-deleted main queue (E2E)

**File:** `src/ServiceConnect.EndToEndTests/RabbitMq/RetryTopologyAutoDeleteE2ETests.cs` (new). Load-bearing for C12 — the bug is observable as message loss, not as an argument value.

Steps:
1. Spin up RabbitMQ via Testcontainers (`docker` direct, no `sg docker -c`).
2. Provision retry topology with `autoDelete: true` for the main queue.
3. Publish a message that the consumer nacks-with-no-requeue → message lands in retry queue (TTL 1 s).
4. Tear down the consumer; verify main queue is auto-deleted (`channel.QueueDeclarePassiveAsync` throws `OperationInterruptedException` reply code 404).
5. Wait for retry queue TTL to expire.
6. Re-declare the main queue (with consumer attached) and re-bind it to the DLX.
7. Verify the message redelivers to the new main queue (i.e. DLX survived). Pre-fix: this fails. Post-fix: passes.

### C12 — supplementary unit (cheap regression catch)

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqTopologyProvisionerRetryDlxAutoDeleteTests.cs` (new).

Capture `ExchangeDeclareAsync` arguments via Moq, assert `autoDelete == false` regardless of `ConfigureRetryTopologyAsync(autoDelete: true | false)`. Two scenarios — caller-passed `true`, caller-passed `false` — both produce DLX `autoDelete:false`.

### H1 — caller-supplied retry-queue arg doesn't crash (unit)

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqTopologyProvisionerRetryArgumentsTests.cs` (new).

Pass `retryQueueArguments = new Dictionary<string, object?> { ["x-dead-letter-exchange"] = "user-supplied-dlx", ["x-message-ttl"] = 999 }`. Capture the dict passed to `QueueDeclareAsync` via Moq.

Assertions:
- No exception thrown.
- Framework values win: `x-dead-letter-exchange == "<queueName>.Retries.DeadLetter"`, `x-message-ttl == retryDelayMs`.
- Debug log emitted twice (once per overridden key) — verify against a `LoggerStub` / `XunitLoggerProvider`.
- Caller-supplied non-conflicting keys flow through (add `["x-max-length"] = 1000` to the input; assert it survives).

### H21 — Bus stops stamping `MessageType` (unit)

**File 1:** `src/ServiceConnect.UnitTests/Bus/BusReservedHeadersTests.cs` (modify or new). Find the existing test asserting caller-supplied `MessageType` is rejected — invert the assertion. Caller-supplied `MessageType` is now passed through to the producer (which then overwrites it — that's the producer's responsibility, not the Bus's).

**File 2 (new):** `BusEnvelopeMessageTypeTests.cs`. `Bus.PublishAsync` and `Bus.SendAsync` produce envelopes with no `HeaderKeys.MessageType` key:

```csharp
Assert.False(envelope.Headers.ContainsKey(HeaderKeys.MessageType));
```

**File 3 (new or extend existing OutboundHeaderBuilder tests):** `OutboundHeaderBuilderOperationNameTests.cs`. Operation name `"Publish"`/`"Send"`/`"ByteStream"` ends up in `MessageType` after `BuildHeaders` runs — explicit assertion (covers the post-Bus-removal contract that the Producer is now solely responsible for `MessageType`).

### H22 — reset deferred to next publish, lock not held during reconnect (unit)

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/ProducerPublishTimeoutResetTests.cs` (new — separate from existing `ProducerPublishTimeoutTests`).

**Test 1 — `PublishTimeout_Throws_DoesNotReconnect_FlagsResetRequired`:**
- Configure `_publishTimeout` very short (e.g. 50 ms).
- Mock `IChannel.BasicPublishAsync` to delay longer than the timeout (`await Task.Delay(500, ct)`).
- Replace `_producerConnection.ReconnectForTests` with a probe that increments a counter on each call.
- Assert: `TimeoutException` thrown; reconnect probe **call count == 0** during the timed-out publish; the `_resetRequired` flag is set on `_producerConnection` (verify via internal accessor exposed only to unit tests).

**Test 2 — `NextPublishAfterTimeout_DrivesReset_BeforeAcquiringLock`:**
- Same setup; first publish times out and sets the reset flag.
- Second publish: assert the reset probe is invoked **before** `_publishLock.WaitAsync` returns. Use a `BlockingChannel` mock that records lock state on each call to verify ordering.

**Test 3 — `MarkResetRequired_IsIdempotent_ConcurrentTimeouts`:**
- Two concurrent timed-out publishes both call `MarkResetRequired`. Next publish drives **one** reset (assert reset probe call count == 1). Tests the `Interlocked.Exchange(_, 0)` consume-once semantic.

Test methodology note: do **not** assert via timing alone (e.g. "second publish completes within X seconds"). The assertion must be against an ordering probe (lock state at reset call) so it can't pass spuriously on a fast machine before the fix lands.

### H22 — supplementary E2E (R7 surface check)

**File:** `src/ServiceConnect.EndToEndTests/RabbitMq/ProducerPublishTimeoutE2ETests.cs` (existing — extend, don't add).

New test: `ConcurrentPublishersUnderTimeout_NoneBlockedBeyondPublishTimeout`. Force a publish timeout (broker artificially slowed via toxiproxy or a deliberately tiny `_publishTimeout`), with 10 concurrent publishers. Assert:
- No publisher's elapsed wall time exceeds `2 × _publishTimeout` (one timeout + one normal publish).
- All publishers eventually complete (post-reset).

This is the R7-exposure regression catch referenced in Section 1's "Out of scope" list.

### M11 — null guards (unit)

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/ProducerNullArgumentTests.cs` (new). Theory test parameterised by entry point × null-arg combinations:

| Method | Null arg | Expected `paramName` |
| --- | --- | --- |
| `PublishAsync` | `type` | `type` |
| `PublishAsync` | `message` | `message` |
| `SendAsync(Type, byte[], ...)` | `type` | `type` |
| `SendAsync(Type, byte[], ...)` | `message` | `message` |
| `SendAsync(string, Type, byte[], ...)` | `type` | `type` |
| `SendAsync(string, Type, byte[], ...)` | `message` | `message` |
| `SendBytesAsync` | `type` | `type` |
| `SendBytesAsync` | `packet` | `packet` |

Cheap, mechanical. Use `[Theory]` + `[MemberData]` for the parameterisation.

### M12 — Priority conversion logs context (unit)

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/OutboundHeaderBuilderPriorityTests.cs` (new). Use `LoggerStub` to capture log entries.

| Scenario | Input `Priority` header value | Expected `properties.Priority` | Expected log |
| --- | --- | --- | --- |
| Valid byte | `(byte)5` | `5` | (none) |
| Out-of-range int | `300` | unset | Error log includes `"300"` and `"Int32"` |
| Non-numeric string | `"abc"` | unset | Error log includes `"abc"` and `"System.String"` |

Assert the log includes the value and source type — not just a generic "Error setting message priority".

### Smaller — caller-supplied reserved header gets a warning (unit)

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/OutboundHeaderBuilderReservedHeaderWarningTests.cs` (new).

Pass `headers = new Dictionary<string, string> { ["DestinationAddress"] = "user-supplied", ["TypeName"] = "user.spoof", ["custom"] = "ok" }`. Assertions:
- `result["DestinationAddress"]` is the framework value (not "user-supplied").
- `result["TypeName"]` is the framework value (the type's FullName, not "user.spoof").
- `result["custom"] == "ok"` (non-reserved keys flow through).
- Two Warning log entries — one per overwritten reserved key — each containing the key name.

### Smaller — `ConnectionFactoryBuilder` clear conversion error (unit)

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/ConnectionFactoryBuilderConversionErrorTests.cs` (new).

| Scenario | Input | Expected exception | Message contains |
| --- | --- | --- | --- |
| Bad port string | `ClientSettings[Port] = "not-a-port"` | `InvalidOperationException` | `"Port"`, `"not-a-port"`, `"System.String"` |
| Bad heartbeat string | `ClientSettings[HeartbeatTime] = "abc"` | `InvalidOperationException` | `"HeartbeatTime"`, `"abc"`, `"System.String"` |
| Overflow | `ClientSettings[Port] = long.MaxValue` | `InvalidOperationException` | `"Port"`, `"OverflowException"` (inner) |

The `InnerException` should be the original `FormatException` / `InvalidCastException` / `OverflowException`.

### Build / test discipline

Mirrors Phase 3:

- **Per-csproj only.** `dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj` and `dotnet build src/ServiceConnect/ServiceConnect.csproj`.
- **Unit tests:** `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMQ or FullyQualifiedName~Bus" -m:1`.
- **E2E tests:** `dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "FullyQualifiedName~RetryTopologyAutoDelete or FullyQualifiedName~ProducerPublishTimeout" -m:1`. Testcontainers — user is in `docker` group, call `docker` directly.
- **Avoid** whole-solution `dotnet test`. The cgroup wrapper is the safety net.

E2E budget: two new E2E tests (C12 retry-DLX + H22 timeout-under-load) + one extension to existing `ProducerPublishTimeoutE2ETests`. Each Testcontainer spin-up is ~5-10 s. Total E2E delta < 60 s.

---

## Rollout

### Commit ordering

| # | Commit | Findings | Why this position |
| --- | --- | --- | --- |
| 1 | `docs(spec): phase 04 RabbitMQ producer + topology` | — | Spec lands first — mirrors Phase 1/2/3 cadence. |
| 2 | `docs(plan): phase 04 implementation plan` | — | Plan after spec. |
| 3 | `fix(producer): null-guard public publish/send entry points` | M11 | Mechanical; no behaviour change for valid args. |
| 4 | `fix(producer): log full context on priority conversion failure` | M12 | Local to `OutboundHeaderBuilder`. |
| 5 | `fix(producer): warn on caller-supplied reserved header overwrite` | smaller | Local to `OutboundHeaderBuilder`. |
| 6 | `fix(connection): wrap setting conversions with key context` | smaller | Local to `ConnectionFactoryBuilder`. |
| 7 | `fix(producer): re-mint MessageId/TimeSent per delivery in multi-endpoint Send` | **C11** | Critical, isolated to `Producer.SendAsync(Type, ...)`. |
| 8 | `fix(topology): retry DLX always durable; framework retry args win` | **C12 + H1** | Bundled — both edits in `ConfigureRetryTopologyAsync`; phase doc explicitly suggests this grouping. |
| 9 | `refactor(bus): drop dead MessageType stamp; keep operation-name on the wire` | **H21** | Cross-package surgery. Lands alone for clean revert / blame. Includes website header-reference rewrite. |
| 10 | `fix(producer): defer connection reset off the publish lock` | **H22** | Riskiest change in this phase; lands alone. R7 exposure note in commit body. |
| 11 | `docs(website): repoint MessageType reference, retry-topology guide` | — | Doc-only sweep across `learn/operations/`, `reference/messages/`, `reference/configuration/`, `reference/bus/`, `releases/`. |
| 12 | `test(producer/topology): final regression gate` | — | Verification commit if any cross-cutting tests get added during review. May be empty if the reviewer finds nothing. |

Each `fix(...)` commit lands TDD-style: failing test → minimal fix → passing test → commit.

### Documentation updates (commit 11)

- `website/src/content/docs/reference/messages/` (header-keys page): `MessageType` semantics rewritten to "operation name (`Publish`/`Send`/`ByteStream`)"; cross-link to `TypeName` (FullName) and `FullTypeName` (AQN) for type info.
- `website/src/content/docs/learn/operations/` (search for "retry topology" / "DLX" / "dead letter"): document the DLX-outlives-queue invariant; document the retry-arg "framework wins" rule with a worked example (caller passes `x-message-ttl=999` → framework's `retryDelayMs` wins, Debug log emitted).
- `website/src/content/docs/reference/configuration/` (`RetryQueueArguments`): note that `x-dead-letter-exchange` and `x-message-ttl` are framework-managed and any caller value is overridden.
- `website/src/content/docs/reference/bus/` (`IBus.Send` multi-endpoint section): "each delivery has a distinct `MessageId`; correlate via `CorrelationId`" callout.
- `website/src/content/docs/releases/` (v7 release notes, behaviour-changes section):
  1. Per-delivery `MessageId` for multi-endpoint Send.
  2. `MessageType` header semantics formalised as operation name (no observable wire change; downstream auditors who expected FullName based on out-of-date docs should switch to `FullTypeName`).
  3. Retry DLX always durable.
  4. Caller-supplied retry args overridden with framework values (Debug log on override).
  5. Publish timeout no longer holds publish lock for reconnect duration.

### Examples / READMEs

- `examples/PointToPoint/`, `examples/PublishSubscribe/`, `examples/CompetingConsumers/` — verify no test assertion that multi-endpoint Send produces a single `MessageId`.
- `examples/RoutingSlip/README.md` — re-read for per-hop semantics framing; routing slips depend on per-hop identity.
- Top-level `README.md` — only update if `MessageType` / `MessageId` / topology semantics are explicitly described; otherwise skip.

### Verification gate before final code review

1. Per-csproj build of `ServiceConnect.Client.RabbitMQ` + `ServiceConnect` clean.
2. `dotnet test src/ServiceConnect.UnitTests/ --filter "FullyQualifiedName~RabbitMQ or FullyQualifiedName~Bus" -m:1` — all pass.
3. `dotnet test src/ServiceConnect.EndToEndTests/ --filter "FullyQualifiedName~RetryTopologyAutoDelete or FullyQualifiedName~ProducerPublishTimeout" -m:1` — all pass against Testcontainers RabbitMQ.
4. Astro build clean (website doc updates compile).
5. Grep verification:
   - `grep -rn "envelope.Headers\[HeaderKeys.MessageType\] *=" src/ServiceConnect/` returns zero hits (post-H21).
   - `grep -rn "ExchangeDeclareAsync.*autoDelete" src/ServiceConnect.Client.RabbitMQ/Topology/` shows DLX hard-coded `false` (C12 verification).
   - `grep -n "MarkResetRequired\|ReconnectAsync" src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs` shows the timeout path uses `MarkResetRequired`, not `ReconnectAsync` (H22 verification).
6. Final code review via `superpowers:code-reviewer` (opus) across all phase-04 commits.

### Branch

Stays on `v7-clean-architecture`. Single branch, sequential commits, mirrors Phase 1/2/3.
