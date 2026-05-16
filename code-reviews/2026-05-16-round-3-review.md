# Pre-release code review — round 3

**Date**: 2026-05-16 (4 days after rounds 1 + 2)
**Branch**: `v7-clean-architecture`
**Method**: 5 parallel reviewers — (1) audit of round-2 fixes, (2) examples + test-design quality, (3) saga/aggregator/stream state-machine corner cases, (4) request/reply + retry + cancellation, (5) targeted rescan of `Bus.cs` + `Producer.cs`. The 5th planned agent (persistence + transport recovery) hit an API rate limit and was not run; the gap is noted at the bottom.
**Round 1+2 cross-check**: every agent was given the prior reports and instructed to skip duplicates.

## Top-line

Round 3 surfaced **2 Critical, 16 Important, ~12 Minor**. The Critical pair are both state-machine contract violations that the framework's per-key serialisation was supposed to prevent — and they survive both prior reviews because the surface that triggers them is documented elsewhere. The Important findings cluster around (a) **second-order regressions from round-2 fixes** — three of the round-2 Important fixes have visible holes when stressed against adjacent code paths, (b) **E2E coverage gaps** that mean a regression in connection recovery, broker-cancel, SIGTERM-grace, or cross-process aggregator flushes would not be caught by CI, and (c) **request/reply trust gaps** — replies are correlated by wire `ResponseMessageId` with no authentication that the reply came from the original responder. Incremental review value is dropping: the rescan of `Bus.cs` + `Producer.cs` found only 3 net-new issues across ~3500 lines of code, two of them direct follow-ons to round-2's I4 routing-key fix.

---

## Critical — must fix before release

### C1. Aggregator successful `ExecuteAsync` + failed `RemoveSnapshotAsync` → duplicate dispatch on redelivery
**File**: [src/ServiceConnect/Services/Processors/AggregatorProcessor.cs:332-333](src/ServiceConnect/Services/Processors/AggregatorProcessor.cs#L332-L333)
**What**: `await descriptor.InvokeExecuteAsync(...)` runs the handler. If the subsequent `await persistor.RemoveSnapshotAsync(...)` throws (transient Mongo error, connection drop), the exception propagates → broker NACKs → redelivery → next flush calls `GetSnapshotAsync` which mints a NEW `LeaseSessionId` → claims the still-present rows → `InvokeExecuteAsync` is called AGAIN with identical messages. The Mongo lease defends against cross-worker concurrent dispatch but not against same-worker retry of the same rows.
**Why it matters**: At-least-once is the documented contract, but the duplicate-dispatch window widens from "lease rotation across workers" to "any transient remove failure" — which is the FAR more common production failure mode. Aggregator handlers with non-idempotent side effects (HTTP POST, file write, downstream Publish) will replay them.
**Fix**: Catch `RemoveSnapshotAsync` exceptions and log at Warning while still acking the broker; the next flush will re-attempt the remove via the lease-claim filter (rows still match `LockedBy == sessionId` until the lease expires). Or document explicitly that aggregator handlers must be idempotent across retries — current docs imply per-batch-dispatch idempotence.

### C2. Handler-driven `IProcessManagerFinder.UpdateDataAsync` mid-handler → `PersistAsync` `ConcurrencyException` → handler re-runs with side-effect replay
**File**: [src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs:336-376](src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs#L336-L376)
**What**: `PersistAsync`'s re-find pattern catches handler-driven DELETE (`freshFind == null` → skip update). A handler-driven UPDATE leaves the row at a newer Version than the captured `persistenceData`; `PersistAsync`'s `UpdateData(finder, persistenceData!, ...)` filter matches zero rows → `ConcurrencyException` on the success path (NOT inside the existing best-effort try/catch — it's on the path that runs only when the handler didn't throw). The exception escapes `RunPipelineOnceAsync`, broker NACKs, redelivery re-runs the handler.
**Why it matters**: Handlers that call `finder.UpdateDataAsync` directly (an advanced but legal pattern — the xmldoc for `DeleteDataAsync` references the same finder) trigger duplicate handler invocation with all side effects replayed (`bus.SendAsync`, HTTP calls). This is exactly the failure the per-saga-key `SemaphoreSlim` was supposed to prevent.
**Fix**: Re-find before the success-path Update; when `freshFind` is non-null AND `freshFind.Version != persistenceData.Version`, skip the Update (handler already persisted via its own UpdateData call). Or document loudly that handlers must NOT call `UpdateDataAsync` directly.

---

## Important — fix before release (16)

### Second-order regressions from round-2 fixes (4)

#### R1. `ProcessManagerTimeoutService.DisposeAsync` still disposes `_stoppingCts` on the timeout path
**File**: [src/ServiceConnect/Services/ProcessManagerTimeoutService.cs:283-320](src/ServiceConnect/Services/ProcessManagerTimeoutService.cs#L283-L320)
**Introduced by**: Round-2 I2 fix applied to `StopAsync` only — `DisposeAsync` wasn't updated to match.
**What**: On the `TimeoutException` branch (line 307) the polling task is abandoned, but `cts?.Dispose()` at line 320 still runs. The abandoned task's next `cts.Token` read inside `PollLoop` throws `ObjectDisposedException`.
**Fix**: Mirror StopAsync — only dispose CTS on the `pollingCompleted` branch.

#### R2. `Consumer.StartConsumingAsync` catch-path race vs concurrent `StartConsumingAsync`
**File**: [src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs:240-283](src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs#L240-L283)
**Introduced by**: Round-2 I8 fix (early-release of `_lifecycleSemaphore` before per-host recovery loop).
**What**: Catch resets `_started=0`, releases `_lifecycleSemaphore`, then iterates `_clients.TryTake` and finally disposes the owned `_connection`. A racing `StartConsumingAsync` can CAS `_started` 0→1, acquire the semaphore, observe `_connection != null`, skip re-creation, and proceed into topology declares on the same connection the catch is about to dispose. The I8 comment justifies the release against a concurrent **dispose**, not a concurrent **start**.
**Fix**: Snapshot `_connection` to a local, null it (and set `_ownsConnection = false`) BEFORE releasing the semaphore, then dispose the snapshot outside the semaphore window.

#### R3. Outgoing-filter mutation of `HeaderKeys.RoutingKey` still doesn't reach the wire
**File**: [src/ServiceConnect/Bus.cs:148-162](src/ServiceConnect/Bus.cs#L148-L162) + [SendMessagePipeline.cs:68-72](src/ServiceConnect/Services/SendMessagePipeline.cs#L68-L72)
**Introduced by**: Round-2 I4 fix (plumbed `ctx.RoutingKey` from `options?.RoutingKey` only).
**What**: An outgoing `IFilter` that writes `envelope.Headers[HeaderKeys.RoutingKey] = "foo.bar"` lands in the wire headers via `ExtractHeaders` but never influences `ctx.RoutingKey`. The pipeline's `IsNullOrEmpty(ctx.RoutingKey)` branch picks the no-routing-key 4-arg overload, and the broker does fanout dispatch.
**Fix**: After `ExtractHeaders`, re-stamp `ctx.RoutingKey` from `headers[HeaderKeys.RoutingKey]` when a filter wrote it.

#### R4. `MessageTypeRegistry.Register` pre-validate is not atomic
**File**: [src/ServiceConnect/Services/MessageTypeRegistry.cs:76-99](src/ServiceConnect/Services/MessageTypeRegistry.cs#L76-L99)
**Introduced by**: Round-2 I10 fix.
**What**: The two `TryGetValue` pre-checks are not atomic with the two subsequent `AddOrReject` calls. A racing `Register` for a colliding FullName landing between the FullName pre-check and `AddOrReject(FullName)` leaves the AQN entry committed while the FullName entry throws — the exact half-committed state I10 claimed to prevent. The cache invalidation (`Volatile.Write _types = null`) also never runs, so a stale snapshot can miss the committed AQN entry.
**Fix**: Wrap the whole method in a lock, OR after `AddOrReject(FullName)` throws roll back the AQN entry via `TryRemove`.

### State-machine corner cases (5)

#### S1. `FlushAggregatorAsync` strands the snapshot lease for the full lease duration on handler throw
**File**: [src/ServiceConnect/Services/Processors/AggregatorProcessor.cs:294-333](src/ServiceConnect/Services/Processors/AggregatorProcessor.cs#L294-L333)
**What**: On handler exception, the catch block doesn't release the Mongo `LockedBy`/`LockExpiresAt` lease. Rows sit leased for 5 minutes. Redelivery's `GetSnapshotAsync` filter rejects all still-leased rows → empty snapshot → message acked successfully without dispatch → broker stops redelivering. The handler effectively fails silently for 5 minutes per aggregator name per failure.
**Fix**: Add a best-effort lease-release in the catch path of `FlushAggregatorAsync` for the held sessionId.

#### S2. Aggregator BatchSize threshold check uses `CountResolvedAsync` but flush competes for the lease → sub-batch dispatch possible
**File**: [src/ServiceConnect/Services/Processors/AggregatorProcessor.cs:110-112, 294-305](src/ServiceConnect/Services/Processors/AggregatorProcessor.cs#L110-L112)
**What**: `ProcessAsync` gates on `CountResolvedAsync >= BatchSize`, then `FlushAggregatorAsync.GetSnapshotAsync` competes with peer flushers for the lease. If a peer holds the lease, our snapshot returns < BatchSize rows but the handler still fires with the sub-batch.
**Fix**: After `GetSnapshotAsync`, re-check `snapshot.ResolvedMessages.Count >= BatchSize` (or `>= 1` for the timer path) before dispatching; release the lease and skip if short.

#### S3. `MessageBusWriteStream` user-cancellation permanently faults the stream; close packet never sent → receiver wedges for 5 minutes
**File**: [src/ServiceConnect/Services/MessageBusWriteStream.cs:123-132, 186-190](src/ServiceConnect/Services/MessageBusWriteStream.cs#L123-L132)
**What**: `WriteAsync`'s bare `catch { _faulted = 1; throw; }` latches faulted on `OperationCanceledException`. Subsequent `CloseAsync` sees `_faulted` and skips the close packet. Receiver's `MessageBusReadStream` never receives `LastPacketNumber` → `IsComplete` stays false → eviction sweep reclaims after 5 minutes.
**Fix**: `catch (Exception ex) when (ex is not OperationCanceledException) { _faulted = 1; ... }` so user cancellation doesn't poison the stream.

#### S4. `StreamProcessor` accepts attacker-supplied `SequenceId` — two senders sharing one merge into the same `MessageBusReadStream`
**File**: [src/ServiceConnect/Services/Processors/StreamProcessor.cs:101-108](src/ServiceConnect/Services/Processors/StreamProcessor.cs#L101-L108)
**What**: `SequenceId` is validated only as `Guid.TryParse`. Two senders that explicitly use the same `Guid` write into the same dictionary entry. `TryAdd` "first wins, second silently dropped" — sender B's PacketNumber-collision payloads are lost. A malicious peer can pre-empt a legitimate stream by collision.
**Fix**: Include the producer's `MessageId` or a per-stream nonce in the admission key so two senders cannot merge. Or document this as a known limitation.

#### S5. `BuildLockKey` falls back to `msg.CorrelationId` — `Guid.Empty` from a misbehaving producer creates a global pseudo-lock
**File**: [src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs:153-188](src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs#L153-L188)
**What**: When the mapping's `MessageProp.Invoke` returns null or throws, the fallback is `new SagaLockKey(descriptor.DataType, msg.CorrelationId)`. Any message with `CorrelationId == Guid.Empty` (a producer that forgot to stamp) keys against `(DataType, Guid.Empty)`, blocking every other empty-correlation message of that data type.
**Fix**: When the fallback fires AND `msg.CorrelationId == Guid.Empty`, key against `msg.MessageId` instead; or refuse to dispatch with a typed exception.

### Request/reply trust gaps (3)

#### Q1. `_pendingRequests` has no upper bound — unbounded growth from infinite-timeout requests
**File**: [src/ServiceConnect/Services/RequestReplyManager.cs:13](src/ServiceConnect/Services/RequestReplyManager.cs#L13)
**What**: `_pendingRequests` is a plain `ConcurrentDictionary<Guid, RequestState>` with no cap. A caller in a hot loop with `Timeout.Infinite` (or just a large Timeout) can hold an unlimited number of `RequestState` objects each pinning a `CancellationTokenSource`, `Timer`, `TaskCompletionSource`, and the closure captured by `Register(...)`.
**Fix**: Add a configurable ceiling (`BusConfiguration.MaxInflightRequests`); back-pressure or throw once exceeded.

#### Q2. Reply correlation trusts the wire `ResponseMessageId` with no authentication
**File**: [src/ServiceConnect/Services/Processors/ReplyProcessor.cs:19-39](src/ServiceConnect/Services/Processors/ReplyProcessor.cs#L19-L39) + [RequestReplyManager.cs:520-549](src/ServiceConnect/Services/RequestReplyManager.cs#L520-L549)
**What**: `TryProcessReply` accepts any inbound message whose `ResponseMessageId` header parses as a `Guid` and collides with a `_pendingRequests` key. No verification that the reply originated from the original request's endpoint, that the reply's `CorrelationId` matches the request's, or that the reply queue is the one this bus's request used. Anyone with publish rights to this bus's queue can complete a pending request with attacker-controlled payload by stamping `ResponseMessageId` to a guessable id. The `ReplyType` is captured at request time so arbitrary-type deserialisation is blocked — but the caller still receives a payload never produced by the intended responder.
**Fix**: Bind reply by additionally verifying `CorrelationId == request.CorrelationId` AND require the responder to echo a per-request HMAC nonce (or 128-bit random in a non-routing header) that the manager checks before completing the TCS.

#### Q3. Per-saga-key `SemaphoreSlim` is per-process, not cluster-wide
**File**: [src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs:45](src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs#L45)
**What**: The lock dictionary is an instance field. Two workers with the same saga type and correlation key both pass their local lock and run handler concurrently. Optimistic concurrency at persist catches the loser, but BOTH handlers' side effects fire — the exact failure mode the comment at line 25 claims to prevent ("both run the user's HandleAsync (with side effects: bus.Send, HTTP, etc.)").
**Fix**: Document explicitly that ServiceConnect's saga concurrency is "at most one side-effect set per delivery", not "at most one side-effect set per saga step". For clustered topologies, recommend wrapping handlers in a distributed lock per CorrelationId.

### Hop / routing semantics (2)

#### H1. `Bus.RouteAsync` stamps `RoutingSlipHopsCompleted` BEFORE the send middleware runs
**File**: [src/ServiceConnect/Bus.cs:493-526](src/ServiceConnect/Bus.cs#L493-L526)
**What**: The hop counter is stamped at ~line 514, then `_sendPipeline.ExecuteSendMessagePipelineAsync` runs at line 526. `ISendMessageMiddleware` has full read/write access to `context.Headers` and can reset the hop count to 0 or 1, silently disabling the cross-service amplification defence.
**Fix**: Stamp the hop counter AFTER the send middleware, in `OutboundHeaderBuilder` or `Producer`. Add to `OverwrittenHeaderKeys` so caller-supplied / middleware-mutated values are dropped.

#### H2. `Producer.PublishAsync` hard-codes `ExchangeType.Fanout` — round-2 I4 is cosmetic
**File**: [src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs:311](src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L311)
**What**: The producer now correctly propagates `resolvedRoutingKey` to `BasicPublishAsync`, but the surrounding `EnsureExchangeDeclaredAsync(exchangeName, ExchangeType.Fanout, ct)` forces the exchange to fanout. A fanout exchange ignores the routing key at the broker. So `PublishOptions.RoutingKey = "foo.bar"` now reaches the wire and is silently dropped by the broker instead of by the producer — same end state, different layer.
**Fix**: Either make the exchange type configurable per message type (topic/direct), or reject non-empty `PublishOptions.RoutingKey` at the Bus surface as round-2 I4 originally proposed as an alternative.

### Disposal ordering / lifetime (2)

#### D1. `Bus.DisposeAsync` unguarded `_sendPipeline.DisposeAsync()` between two catch-wrapped disposals
**File**: [src/ServiceConnect/Bus.cs:846](src/ServiceConnect/Bus.cs#L846)
**What**: `await StopConsumingCoreAsync(...)` and `await disposableReplyManager.DisposeAsync()` are both wrapped in try/catch that downgrades exceptions to LogWarning. The intermediate `await _sendPipeline.DisposeAsync()` is NOT wrapped. A throw skips the reply-manager dispose, which faults every in-flight `SendRequestAsync` TCS — callers awaiting with `Timeout.Infinite` would never wake.
**Fix**: Wrap line 846 in the same try/catch shape as its neighbours.

#### D2. `ProducerConnection.TearDownChannelAndConnectionAsync` — `connection.CloseAsync()` has no time budget
**File**: [src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs:546](src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs#L546)
**What**: `Producer.DisposeAsync` and `ProducerConnection.CloseAsync` share a stopwatch budget for the semaphore wait, but the actual broker close inside `TearDownChannelAndConnectionAsync` has no timeout. Same shape round-1 R7 raised for `Connection.cs` — fixed there but not propagated to the sibling producer connection.
**Fix**: Pass the remaining budget to `TearDownChannelAndConnectionAsync` and wrap with `CancellationTokenSource.CancelAfter(remaining)`.

---

## E2E + test design (the test suite has gaps that mask real bug classes)

### Critical (2)

- **PointToPoint/run.sh has no success assertion** ([examples/PointToPoint/run.sh:26-31](examples/PointToPoint/run.sh#L26)) — returns 0 even if the framework is silently broken on point-to-point send. Mirror `CompetingConsumers/run.sh`'s `wait_for_ready`/`wait_for_success` pattern.
- **RequestReply/run.sh doesn't verify the reply was received** ([examples/RequestReply/run.sh:40-50](examples/RequestReply/run.sh#L40)) — waits on the requester PID with no `wait_for_success` grep. Exits 0 even on internal timeout.

### Important — coverage gaps (4)

- **No E2E test for cross-process aggregator flush** (the round-1 M1 concern). `AggregatorFlushRaceE2ETests` is in-process only; the Mongo lease semantics are only exercised at the persistor layer.
- **No E2E test for connection loss + reconnect during publish.** Grep across the E2E suite returns zero hits for `reconnect`, `RecoverAsync`, `ConnectionShutdown`. Heavily-modified `Producer.cs` / `ProducerConnection.cs` reconnect path has no exercise.
- **No E2E test for broker queue deletion mid-consume.** `IsCancelledByBroker` detection has no test exercising `QueueDeleteAsync` via a sidecar connection.
- **No E2E test for SIGTERM grace-period elapsing with in-flight handlers.** The documented `GracefulShutdownTimeoutMilliseconds` contract is unexercised in CI.

### Important — flaky/weak assertions (5)

- **`PoisonMessageRedeliveryTests`** uses unconditional `Task.Delay(10s)` as the only sampling window. A regression with delayed redelivery would pass; CI slowness causes false negatives. Fix by polling on attempt count with `TestPolling.WaitForAsync`.
- **`DisableErrorsTests` + `AuditingTests`** use `Task.Delay(2000)` instead of polling the audit queue.
- **`TelemetryE2ETests`** reads `consumeSpans` after `Task.Delay(250)`.
- **`StreamRedeliveryIdempotencyE2ETests`** waits 2s for duplicate dispatch; a 3s-delayed regression would pass.
- **Heavy reflection-based private-field probing** across 12+ test files (`_clients`, `_inFlight`, `_lifecycleSemaphore`, `_flushLocks`, `_activeFlushes`). Couples tests to private layout; raises refactor cost. Use `internal` + `InternalsVisibleTo` where the invariant matters.

### Important — examples (2)

- **4 of 14 example run scripts miss `trap cleanup EXIT`** — `PolymorphicMessages`, `PublishSubscribe`, `RequestReply`, `Telemetry`. Leaks `dotnet run` processes in CI on script-level error.
- **`PolymorphicMessages` and `PublishSubscribe` reuse a fixed queue name across runs** — second run hits `inequivalent_arg` if topology drifted.

### Important — serialization corpus (2)

- **No `null` byte-array, no empty collections, no `Guid.Empty`** in the corpus. Newtonsoft and STJ historically diverge on `null` vs missing properties.
- **Polymorphic corpus deliberately skips abstract-base/derived** with a TODO comment. This is the most likely v7→v8 wire-format regression point.

---

## Minor (the long tail)

- `MessageDispatcher` allocates a `Popper`-style class per dispatch on the I5 push path — could be a struct.
- `IProducer.PublishAsync` DIM third-party silent-drop documented but not detected at runtime.
- `ReplyProcessor.ProcessAsync` ignores `cancellationToken` after entry check — slow `OnReply` callback can't be interrupted on shutdown.
- `IsRetriablePublishException` is opt-out-by-type — programmer errors (`ArgumentException`, `InvalidOperationException`) get the full 60-attempt retry budget. Invert to opt-in (`BrokerUnreachableException`, `AlreadyClosedException`, `IOException`).
- `RequestTimeoutAsync` doesn't link the saga timeout dispatch CT to the underlying `SendAsync` — duplicate timeout dispatch is possible during graceful shutdown.
- Top-level `examples/README.md` is missing `PolymorphicMessages` and `Telemetry`.
- Stale `examples/CompetingConsumers/manual-output.log` committed; add to `.gitignore`.
- `Aggregator.Timeout() == Timeout.InfiniteTimeSpan` default fails registry validation — round-1 minor, still open.
- Various Mongo persistor edge cases: `SanitizeCollectionName` doesn't strip `$`/`\0`; `UpdateDataAsync` xmldoc not cross-linked at `IProcessManagerFinder`.
- Aggregator tests have no explicit `MaxRetries` set — default policy regression would silently change retry behaviour.
- `BusLifecycleCancellationTests` injects a held semaphore via reflection — couples to private field name.
- `Producer.SendAsync` per-endpoint OCE filter loses partial-publish state for the inflight endpoint.
- `IsComplete()` purely count-based — silent partial reads on PacketNumber gaps.
- `InMemoryProcessManagerFinder.FindMatchingItem` O(N) scan across all saga types.

---

## Coverage gap this round

The 5th planned agent (persistence + transport recovery edge cases) hit the API rate limit before producing a report. Most of its planned ground is covered by C1/S1 (aggregator persistor failure modes) and Agent 3's persistor section. The unaddressed angles:

- Mongo index creation during cluster failover
- `IMongoClient` connection-pool saturation under heavy bus load
- Whether saga + aggregator writes should be wrapped in transactions
- Bulk-write splitting for snapshot deletes exceeding Mongo's 100k-doc limit
- `channel.flow=false` flow-control handling on the producer
- Heartbeat tuning (default value, reconnect storm jitter)

Recommend running that agent before final release sign-off, or accepting that these areas are exercised mainly through E2E tests under Testcontainers.

---

## Themes worth a session

1. **State-machine non-idempotent failure recovery** (C1, C2, S1, S2). The framework assumes "handler runs once per logical message" semantics in user docs but in practice serves "at-least-once with at-least-once dispatch under transient persistor failure." The two Critical findings + S1 + S2 are all instances of the same gap. Either tighten the contract (catch-and-log on persist failures so the broker still acks) or document the gap clearly.

2. **Round-2 fix completeness audit.** R1-R4 are all places where round-2's fix landed in one location and missed an adjacent one (`StopAsync` vs `DisposeAsync`, success path vs failure path, scalar config vs sub-config, narrow pre-check vs full atomicity). Worth a focused pass on every round-2 fix to ask "is there a sibling code path that needs the same change?"

3. **Reply correlation security model** (Q1, Q2). The framework currently authenticates replies by `Guid` match, which is meant as a correlation token not a security token. Either commit to the "trusts the wire" model (and document it loudly so operators know to firewall the bus) or add a per-request nonce.

4. **E2E coverage of the production-failure scenarios** (connection drop, broker cancel, SIGTERM-grace). These are exactly the scenarios where round-2 work landed; CI should catch regressions in them.

---

## Recommended fix order

1. **C1, C2** — Critical state-machine corner cases. Both have a documented contract that the framework already claims to honour.
2. **R1, R2, R3, R4** — Round-2 fix completeness. Mechanical follow-ons to existing fixes.
3. **S1, S2** — Aggregator lease-release on throw + sub-batch check. Both have the same shape as C1.
4. **Q1, Q2, Q3** — Request/reply trust + DoS surface. The `_pendingRequests` cap is easy; reply authentication needs design.
5. **H1, H2, D1, D2** — Hop counter + disposal ordering. All are 5-line fixes.
6. **E2E coverage gaps** — add tests for the round-2 fix areas (connection recovery, broker cancel, SIGTERM-grace, cross-process aggregator).
7. **Example smoke-test gaps** — `PointToPoint/run.sh` and `RequestReply/run.sh` success-assertions are release blockers for the docs.

---

## Diminishing returns signal

The targeted Bus.cs + Producer.cs rescan (Agent 5) found only 3 net-new issues across two of the largest files in the repo. Agent 5 explicitly noted "incremental review value has dropped to near zero on these two files." This is the expected pattern: each round shrinks the surface of undiscovered bugs. After this round, additional reviews will likely surface only Minor / cosmetic items unless behaviour changes substantively.

**Suggested gate**: fix the 2 Critical + the ~8 highest-impact Important findings (R1-R4, S1, Q1, Q2, D1) before release. Defer the rest to v7.1 with documented release notes acknowledging the limitations (reply correlation security model, cross-process saga side-effect replay, etc.).
