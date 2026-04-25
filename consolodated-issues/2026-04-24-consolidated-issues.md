# Consolidated issues — 2026-04-24

Deduped, verified findings across the four reviews in `code-reviews/` (all dated 2026-04-24), checked against the current `v7-clean-architecture` tree (HEAD `cc3fd44a`).

## Sources and citation keys

| Key | Source file | Raw counts |
|---|---|---|
| **C** | `2026-04-24-src-bug-hunt-consolidated.md` | 12 Critical / 31 Important / 36 Minor |
| **N** | `2026-04-24-src-bug-hunt-new-issues.md` | 4 High / 17 Medium / 31 Low |
| **P** | `2026-04-24-src-bug-hunt-parallel.md` | 0 Critical / 18 Important / ~20 Minor |
| **R** | `2026-04-24-src-bug-review.md` | 3 findings (High / Medium / Low) |

Within each finding, bracketed tags cite the originating review(s), e.g. `[C#1, N.H1, P.Imp]`.

## Dedup & verification methodology

1. Built a cross-source mapping from all four reviews organised by subsystem (streaming, RabbitMQ transport, core bus, InMemory, MongoDb, telemetry, interfaces).
2. Merged overlaps — every distinct bug has exactly one entry with all citations.
3. Spot-verified each finding against current `src/` on HEAD.
4. Spawned four parallel clean-context verification subagents (one per subsystem cluster). After their returns, independently re-read source for findings where verdicts were surprising. Six subagent verdicts were overridden after that pass — agents misread code under speed pressure (S1, R1, R2, R16, P1, P2). The overrides are embedded below, not called out separately.
5. **Pass-2 opus verification (2026-04-24)** — eight parallel clean-context opus agents re-verified every item. Pass-1 (sonnet) and pass-2 (opus) verdicts agreed on ~68% of items; the remainder were arbitrated by pass-2. Verdict changes are annotated inline on each affected entry with a `**Pass-2 verdict**:` line. First-pass raw output preserved at [`code-reviews/2026-04-24-first-pass-verification.md`](../code-reviews/2026-04-24-first-pass-verification.md).
6. Preserved explicit rejections from source reviews and from both verification passes in the trailing "Rejected" section.

## Counts

| Severity | Raised | Confirmed / partial after pass-2 | Fixed | Rejected / reclassified after pass-2 |
|---|---|---|---|---|
| Critical | 10 | 8 | 5 (C-01, C-02, C-03, C-04, C-05) | 2 (C-07, C-10) |
| High | 22 | 11 | 5 (H-01, H-02, H-05, H-06, H-07) | 11 (H-03 cosmetic, H-04, H-08 latent, H-09, H-13, H-14, H-16, H-17, H-18, H-19, H-20 partial) |
| Medium | 32 | 25 | 0 | 7 (M-05, M-07, M-09, M-10, M-14, M-15, M-17) |
| Low | 72 | 52 | 0 | 23 (L-03, L-04, L-05, L-08, L-09, L-10, L-13, L-16, L-17 stale, L-19, L-23, L-24, L-30, L-31, L-32, L-34, L-36, L-54, L-60, L-61, L-65, L-66, L-74) |
| **Total** | **136** | **96** | **10** | **43** |

Pass-2 also upgraded several earlier partial/equivocal verdicts to CONFIRMED (C-03, C-06, C-09, H-10, H-22, M-04, M-13, M-19, M-32, L-06, L-43) — marked inline.

---

## Critical

Data-loss, silent saga corruption, double-dispatch, unbounded resource growth that compromises long-running instances.

### C-01 — StreamProcessor final-packet double-dispatch
- **Location**: `src/ServiceConnect/Services/Processors/StreamProcessor.cs:120-155`
- **Bug**: When a sequence completes, `_activeStreams.TryRemove(new KeyValuePair<string, ActiveStreamState>(sequenceId, state))` discards its return value. Two concurrent final-packet deliveries can both observe `IsComplete()`, both call TryRemove, and both proceed to `InvokeHandlerAsync` — the handler fires twice for the same stream.
- **Fix sketch**: Gate on the TryRemove boolean; only the thread that actually removed the KVP may dispatch.
- **Sources**: [C#1]
- **Status**: fixed in c9d24bb5

### C-02 — StreamProcessor: failed Write/SetLastPacketNumber wedges sequence until 5-min sweep
- **Location**: `src/ServiceConnect/Services/Processors/StreamProcessor.cs:99-117`
- **Bug**: If `WriteAsync` or `SetLastPacketNumber` throws, the packet is neither recorded nor nacked — the state entry persists but completion is unreachable until the inactivity sweep expires it (~5 min). Senders retry silently against a permanently stuck reader.
- **Fix sketch**: On exception, evict the entry and surface the fault so the sender can recover, rather than relying on timeout sweeping.
- **Sources**: [P.Imp]
- **Status**: fixed in 5400117c

### C-03 — MessageBusWriteStream increments `_packetNumber` before send; failure gap hangs reader
- **Location**: `src/ServiceConnect/Services/MessageBusWriteStream.cs:66-82`
- **Bug**: Packet number is incremented *before* `SendBytesAsync`. If the send fails, the sender retries with the next number — the reader sees a gap it will never fill, stalls, and relies on the inactivity sweep to clean up.
- **Fix sketch**: Only increment on successful send, or include a resend identifier so the reader can reconcile.
- **Pass-2 verdict**: CONFIRMED. Data-loss hazard real; precise mechanism is that caller-side retry after a transient send failure produces a permanent sequence gap the reader cannot close.
- **Sources**: [N.H2]
- **Status**: fixed in 2e8c9d76

### C-04 — RabbitMQ Consumer: Dispose→Start holds stale `_connection` reference
- **Location**: `src/ServiceConnect.Client.RabbitMQ/Consumer.cs:88-92, 211-215`
- **Bug**: `DisposeAsync` disposes `_connection` (when owned) but never nulls the field. `StartConsumingAsync` guards recreation with `if (_connection is null)` — which is false after dispose. On restart, the consumer tries to use a disposed RabbitMQ connection.
- **Fix sketch**: Set `_connection = null` after dispose; likewise for any other fields the start-path uses `is null` to detect.
- **Sources**: [C#2, N.M3, P.Imp]
- **Status**: fixed in 7e3a261b

### C-05 — RabbitMQ Consumer: `_clients` bag grows unbounded across Stop/Start cycles
- **Location**: `src/ServiceConnect.Client.RabbitMQ/Consumer.cs:187`
- **Bug**: `_clients` is a `ConcurrentBag<ConsumerClient>` that accumulates across every start/stop cycle without ever being cleared. Long-lived buses that restart consumers (e.g. transport reconnects, topology refresh) leak a ConsumerClient per cycle.
- **Fix sketch**: Clear or replace the bag at the end of each stop sequence, or move the collection's lifetime to the start path.
- **Sources**: [C#3]
- **Status**: fixed in 21eae3b2

### C-06 — `ProcessManagerProcessor` static `MapperCache` pins the first handler + root-provider scope bypass
- **Location**: `src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs:41-65`
- **Bug**: `MapperCache` is a static `Lazy<...>` — the mapper it resolves is captured from the very first resolution context and reused forever. Combined with resolving from the root `IServiceProvider` rather than a per-message scope, scoped dependencies (DbContext, unit-of-work, tenant context) are leaked or cross-fed between messages.
- **Fix sketch**: Build the mapper per consume-scope, resolve `IProcessManagerFinder` / saga mappers from the per-message `IServiceScope`, or require the scanner to register static mapper instances.
- **Pass-2 verdict**: CONFIRMED both sub-claims. Mapper cache is process-static and `ProcessManagerProcessor` is registered Singleton in `ServiceCollectionExtensions.cs:156`; both the mapper and the resolved finder outlive any per-message scope.
- **Sources**: [C.Imp, N.M5]

### C-07 — `TimeoutData.Destination` defaults to empty; dispatcher silently skips → zombie sagas
- **Location**: `src/ServiceConnect.Interfaces/Timeouts/TimeoutData.cs:16` + `src/ServiceConnect/Services/ProcessManagerTimeoutService.cs:110`
- **Bug**: `Destination` defaults to `""`; dispatcher guards with `if (!string.IsNullOrEmpty(timeout.Destination))`. Any row persisted with an unset Destination is polled forever but never dispatched — no log, no status update, no retry.
- **Fix sketch**: Throw `ArgumentException` at the `InsertTimeoutAsync` boundary on both persistors; in the dispatcher, log+delete-or-mark-failed so storage doesn't grow indefinitely.
- **Pass-2 verdict**: REJECTED. Removal call (`RemoveDispatchedTimeoutAsync`) runs unconditionally *after* the dispatch guard — a row with empty destination is deleted, not polled forever. Claim's mechanism is wrong; the empty-Destination case fails loud in a different way (receiver side) but does not produce zombie growth. Moved to Rejected.
- **Sources**: [R.High, N.L4]

### C-08 — `InMemoryTimeoutStore` id-only Remove/Release ignores lease ownership
- **Location**: `src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs:139-158, 163-183`
- **Bug**: The id-only `RemoveDispatchedTimeoutAsync(Guid)` / `ReleaseDispatchedTimeoutAsync(Guid)` overloads unconditionally mutate state. The lease-aware overloads (186-244) correctly enforce lease ownership. Mongo's id-only path silently no-ops on leased rows. Callers following the Mongo contract against InMemory will stomp an active peer's lease. Partially remediated (lease-aware overloads are correct) — the id-only paths still diverge.
- **Fix sketch**: Either have the id-only overloads delegate to the lease-aware overloads with a sentinel that means "no ownership check" and forbid them for normal use, or teach them to respect current lease state (wins: behavioural parity with Mongo).
- **Sources**: [C#5, N.H3, N.H4]

### C-09 — MongoDb `GetTimeoutsBatchAsync` nextPipeline returns a value that is never consumed (dead code smell)
- **Location**: `src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs:144-185`
- **Bug (original)**: Claimed nextPipeline misses leased rows whose lease expires before next wake.
- **Pass-2 verdict**: RECLASSIFIED — the nextPipeline's `NextQueryTime` is **never read** by `ProcessManagerTimeoutService`, which uses a fixed `PeriodicTimer` interval. The original correctness claim is moot *because the value is never consumed*, but the query is still executed every batch — this is vestigial dead code, not a runtime bug. Severity reduced to hygiene/perf; leaving entry under Critical until the dead code is removed.
- **Fix sketch**: Delete the nextPipeline computation entirely, or wire `NextQueryTime` through to the dispatcher's wake schedule (and then the original concern must be addressed).
- **Sources**: [C#7, N.M13]

### C-10 — MongoDb `GetTimeoutsBatchAsync` stale `utcNow` in UpdateMany allows double-lease
- **Location**: `src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs:129-145`
- **Bug**: `utcNow` is captured before the filter+update. If the server clock or GC pause causes the call to lag, the filter `LockExpiresAt < utcNow` matches rows whose lease is already renewed by a peer, producing a second active lease on the same row and a double dispatch.
- **Fix sketch**: Use `$$NOW` in the aggregation / apply a CAS on the old `LockExpiresAt` value read in the pre-select pipeline.
- **Pass-2 verdict**: REJECTED. `utcNow` snapshot is intentional; `batchFilter` re-applies the same `dueUnlockedFilter` on the update so a peer-renewed lease is excluded by CAS — double-lease is not reachable. Moved to Rejected.
- **Sources**: [C#8]

---

## High

Functional bugs that occur on normal shutdown/restart paths, divergent contracts with silent consequences, and correctness issues in common paths.

### H-01 — Duplicate packet throws InvalidOperationException → poison loop
- **Location**: `src/ServiceConnect/Services/Processors/StreamProcessor.cs:99-101` + `src/ServiceConnect/Services/MessageBusReadStream.cs:85-90`
- **Bug**: Legitimate RabbitMQ re-delivery of the same packet hits `_packets.TryAdd` returning false and throws. The delivery is then nacked-with-requeue, and the broker redelivers indefinitely.
- **Fix**: Treat duplicate packet as idempotent-ack.
- **Sources**: [N.H1]
- **Status**: fixed in 0569dc65

### H-02 — StreamProcessor eviction TryRemove-by-KVP races with `LastSeenUtc` mutation
- **Location**: `src/ServiceConnect/Services/Processors/StreamProcessor.cs:99, 161-172`
- **Bug**: The eviction sweep uses `TryRemove(KeyValuePair<...>)`, which matches on value reference equality. The same `ActiveStreamState` instance is being mutated (via `LastSeenUtc` writes) on the dispatch path, so between the sweep's read-for-check and TryRemove the entry's touched state can diverge. Result: stream state orphaned in memory while being treated as swept.
- **Fix**: Use key-based CAS (compare against a version counter) or snapshot outside the entry.
- **Sources**: [N.M6]
- **Status**: fixed in 64217763

### H-03 — RabbitMQ `QueueBindAsync` receives queue-declaration args as binding args
- **Location**: `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs:161` + `src/ServiceConnect.Client.RabbitMQ/RabbitMqTopologyProvisioner.cs:125, 164`
- **Bug**: The same args dictionary used for `QueueDeclareAsync` is passed to `QueueBindAsync`. Binding args and queue args are distinct in AMQP; this produces unintended binding semantics (e.g. TTL-like args misinterpreted by the binding machinery).
- **Fix**: Separate argument sets per RabbitMQ call.
- **Pass-2 verdict**: RECLASSIFIED — COSMETIC ONLY. The broker silently ignores binding args on non-`headers` exchanges (direct/topic/fanout), so the queue-declare args passed to `QueueBindAsync` have no runtime effect on any exchange type currently in use. Still a code smell worth fixing, but no observable behaviour change today. Severity reduced to Low hygiene.
- **Sources**: [C#4]

### H-04 — Producer NRE loop after reconnect-inside-retry
- **Location**: `src/ServiceConnect.Client.RabbitMQ/Producer.cs:201-211`
- **Bug**: On retry, reconnect resets `_model`/`_connection`; the retry loop reads `_model` without re-acquiring the lock and can NRE when another thread disposed it between checks.
- **Fix**: Read refs under the same lock as the connect-check; bail if null.
- **Pass-2 verdict**: REJECTED. `_model` is `volatile`; `_publishLock` is held for the duration of the retry loop and reconnect acquires `_connectionSemaphore` under that outer lock. There is no observable NRE window. Moved to Rejected.
- **Sources**: [P.Imp]

### H-05 — Producer `DisposeAsync` disposes semaphores held by in-flight publishers
- **Location**: `src/ServiceConnect.Client.RabbitMQ/Producer.cs:384-424`
- **Bug**: Dispose releases and disposes `_publishLock`/`_connectionSemaphore` even while other publisher tasks may still be awaiting them. In-flight publishers throw `ObjectDisposedException` instead of receiving a clean shutdown signal.
- **Fix**: Quiesce with a drain token before disposing semaphores.
- **Sources**: [P.Imp]
- **Status**: fixed in 049c00a8

### H-06 — Producer Dispose can publish/create state after teardown, leaks
- **Location**: `src/ServiceConnect.Client.RabbitMQ/Producer.cs:384-424`
- **Bug**: Teardown does not set a "disposing" flag that the publish path checks, so a concurrent `PublishAsync` can re-create `_model`/`_connection` after Dispose has already closed the previous pair. The newly created resources are leaked.
- **Fix**: Set `_disposing` before acquiring locks; make publish a no-op once set.
- **Sources**: [C.Imp]
- **Status**: fixed in a4791a97

### H-07 — Producer Dispose worst-case 60s (two sequential 30s waits)
- **Location**: `src/ServiceConnect.Client.RabbitMQ/Producer.cs:391-397`
- **Bug**: `publishLockAcquired = await _publishLock.WaitAsync(disposeTimeout)` then `connectionLockAcquired = await _connectionSemaphore.WaitAsync(disposeTimeout)` — two 30-second budgets back-to-back, not a shared budget. Worst case 60s, not the documented 30.
- **Fix**: Share a single stopwatch-derived budget across both waits.
- **Sources**: [C.Imp]
- **Status**: fixed in 107ab1c7

### H-08 — `Connection.DisposeAsync` uses unbounded `_connectionLock.WaitAsync`
- **Location**: `src/ServiceConnect.Client.RabbitMQ/Connection.cs:93-126`
- **Bug**: Dispose awaits the connection lock with no timeout. Any stuck connect/reconnect op holds it indefinitely, so Dispose never completes and the bus can't shut down cleanly.
- **Fix**: Bounded WaitAsync with a dispose timeout; force-close the underlying connection on wait failure.
- **Pass-2 verdict**: RECLASSIFIED — LATENT LATENCY, not a deadlock. `ConnectAsync`'s `finally` always releases the lock; "unbounded" only matters if an in-flight RabbitMQ network op hangs. Not a shutdown hazard in practice but worth a bounded wait for cooperative shutdown SLAs. Severity reduced to Low hygiene.
- **Sources**: [N.M2]

### H-09 — `Retry.CalculateDelay` throws on negative `baseInterval`
- **Location**: `src/ServiceConnect.Client.RabbitMQ/Retry.cs:132`
- **Bug**: `Random.Shared.Next(0, -N)` throws `ArgumentOutOfRangeException` when jitter produces a negative value. A caller who passes (or computes) a negative base takes down the retry loop with an exception that bypasses the policy.
- **Fix**: Clamp `baseInterval` at the boundary; assert `> 0`.
- **Pass-2 verdict**: REJECTED. The upper bound is `Math.Min(..., 1000)`, so the arg can only be negative if the caller passes a negative `baseInterval` directly — no internal call-site does. Boundary assertion is defensive hardening, not a live bug. Moved to Rejected.
- **Sources**: [C.Imp]

### H-10 — `AggregatorProcessor` batch-path races with Dispose
- **Location**: `src/ServiceConnect/Services/Processors/AggregatorProcessor.cs:45-74, 211-246`
- **Bug**: Dispose tears down timers/locks while `ConsumeAsync` or timer-fired `FlushAsync` is running. Flush sees partially-torn-down state and can observe null/disposed resources.
- **Fix**: Quiesce via a "disposing" flag and wait for in-flight consume/flush tasks before disposing.
- **Pass-2 verdict**: CONFIRMED (narrow). `_activeFlushes` is drained and `ObjectDisposedException` is caught for the narrow shutdown race, but a snapshot-after-ProcessAsync-entry race exists: ProcessAsync can observe running state, Dispose can then proceed, and the flush path continues using disposed resources. Real race; narrow trigger.
- **Sources**: [C.Imp]

### H-11 — `AggregatorProcessor.ResetTimer` `AddOrUpdate` creates duplicate Timers under contention
- **Location**: `src/ServiceConnect/Services/Processors/AggregatorProcessor.cs:83-99`
- **Bug**: AddOrUpdate's factory can run twice under contention, leaving one of the Timer instances orphaned and still firing. Flush fires more than once or at the wrong schedule; timer leaks on every contention event.
- **Fix**: Use GetOrAdd with a Lazy, or dispose the loser timer inside the update factory.
- **Sources**: [N.M4]

### H-12 — StreamProcessor / AggregatorProcessor resolve dependencies from the root provider
- **Location**: `src/ServiceConnect/Services/Processors/StreamProcessor.cs:143, AggregatorProcessor.cs:187`
- **Bug**: Both processors resolve their handler collaborators from `IServiceProvider` (root) rather than a per-message scope. Scoped dependencies either leak or are resolved as singletons relative to the bus lifetime.
- **Fix**: Pull dependencies from the `IServiceScope` the processor creates for the current batch/stream.
- **Sources**: [C.Imp]

### H-13 — `ProcessManagerTimeoutService` Release uses shutdown token
- **Location**: `src/ServiceConnect/Services/ProcessManagerTimeoutService.cs:119-140`
- **Bug**: The release call plumbs the host-shutdown token, so on shutdown the release short-circuits with OperationCanceledException — the lease is not released, and the next pickup waits for lease expiry.
- **Fix**: Use `CancellationToken.None` (or a bounded shutdown grace token) for lease release.
- **Pass-2 verdict**: REJECTED. Intentional lease model: the shutdown-OCE propagates to the outer `while` loop's `catch (OCE) { break; }`. On crash or force-shutdown a peer reaps the lease via `ReapStaleLeasesAsync`; on graceful shutdown the lease expiry is bounded by the lease duration. Not a defect. Moved to Rejected.
- **Sources**: [C.Imp]

### H-14 — `NewtonsoftJsonMessageSerializer` shares a single `JsonSerializer` across threads
- **Location**: `src/ServiceConnect/Services/Serialization/NewtonsoftJsonMessageSerializer.cs:12-13, 43, 59, 132`
- **Bug**: `JsonSerializer` is not safe for concurrent use when invoked via `Populate`/`Deserialize` on different readers simultaneously without a settings lock — state on internal `DefaultContractResolver` caches is shared.
- **Fix**: Build a new `JsonSerializer` per serialise/deserialise call, or pool them per-thread.
- **Pass-2 verdict**: REJECTED. Standard Newtonsoft singleton pattern: `DefaultContractResolver` is documented as thread-safe once constructed; `JsonSerializer.Deserialize`/`Populate` on *different* reader instances do not share reader state. Claim is a repeated myth from older Newtonsoft versions. Moved to Rejected.
- **Sources**: [N.M7, P.Min]

### H-15 — `CacheProvider.PurgeNormalPriorities` key-only TryRemove drops concurrently-upgraded High entry
- **Location**: `src/ServiceConnect.Persistence.InMemory/CacheProvider.cs:165-182`
- **Bug**: Uses `_cache.TryRemove(cacheItem.Key, out _)` rather than the KVP overload. If another thread has upgraded the entry's `Priority` from Normal to High between the scan and the remove, the purge deletes the High entry anyway.
- **Fix**: Use `TryRemove(KeyValuePair<,>)` with the specific cache-item reference observed during the scan, or CAS on priority.
- **Sources**: [C#6, N.L11]

### H-16 — `CacheProvider.StartObserving` re-arm after concurrent Remove orphans timer
- **Location**: `src/ServiceConnect.Persistence.InMemory/CacheProvider.cs:247-274`
- **Bug**: A concurrent `Remove` that sets `_timers` to empty for a key can race with `StartObserving` re-arming the timer for the same key. The re-armed Timer is never tracked in `_timers` and fires without a matching entry.
- **Fix**: Re-check presence in `_cache` under the lock before installing; dispose the just-created Timer on loss.
- **Pass-2 verdict**: REJECTED. An orphan timer self-resolves on next tick — it fires once, observes no cache entry, and is a no-op thereafter. Single spurious fire is not observable state corruption. Moved to Rejected.
- **Sources**: [C.Imp]

### H-17 — `DeepClone` silently fails on non-default-ctor types
- **Location**: `src/ServiceConnect.Persistence.InMemory/DeepClone.cs:30-38`
- **Bug**: Relies on `Activator.CreateInstance(T)` — throws for records/sealed types without a parameterless ctor. Called from saga/aggregator InMemory paths; any saga data type without a default ctor is silently broken on InMemory.
- **Fix**: Either document "InMemory requires a default ctor" and throw a diagnostic on insert, or use formatter-less deep copy (e.g. System.Text.Json round-trip with preserve-references).
- **Pass-2 verdict**: REJECTED. Current `DeepClone` uses `JsonConvert` (Newtonsoft) round-trip, not `Activator.CreateInstance` — claim's mechanism is wrong. Newtonsoft handles record ctors. Moved to Rejected.
- **Sources**: [C.Imp]

### H-18 — MongoDb duplicate-CorrelationId surface is `PersistenceException`, not `ConcurrencyException`
- **Location**: `src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs:180-184`
- **Bug**: `MongoWriteException` with error code 11000 is wrapped as `PersistenceException`. Callers trying to disambiguate a concurrency collision from a genuine persistence failure can't.
- **Fix**: Map 11000 → `ConcurrencyException` (aligns with the declared contract).
- **Pass-2 verdict**: REJECTED. Both persistors deviate consistently — the `ConcurrencyException` contract is reserved for *optimistic-concurrency* (version mismatch), and duplicate-CorrelationId-on-insert is explicitly documented as `PersistenceException` in the contract-parity matrix below. Consistent, intentional behaviour. Moved to Rejected.
- **Sources**: [C.Imp]

### H-19 — MongoDb `UpdateDataAsync` reference-shares `Data` with caller during BSON serialize
- **Location**: `src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs:216-221`
- **Bug**: The caller's data object is handed to the Mongo driver unwrapped; serialize happens on the driver thread. If the caller mutates the object while serialization is in flight, BSON output can be torn.
- **Fix**: Clone (or require the caller hand over exclusive ownership) before handing to the driver.
- **Pass-2 verdict**: REJECTED. BSON serialization in the C# driver is synchronous within the send path — the document is serialized on the calling thread before the async wire op begins. Caller cannot mutate mid-serialize. Moved to Rejected.
- **Sources**: [C.Imp]

### H-20 — MongoDb id-only Remove/Release silent no-op on leased rows (contract side)
- **Location**: `src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs:213-247`
- **Bug**: The id-only overloads refuse to touch a row whose lease is still active, returning no indication. Callers that intended "force-remove" get no result and believe the row is clear.
- **Fix**: Return a result discriminator (Removed/NotFound/Leased) or require the lease-aware overload for leased rows.
- **Pass-2 verdict**: PARTIAL. Silent no-op is the documented safety property, so this is NOT-A-BUG for the canonical lease-aware call path. But third-party callers using the id-only overload (e.g. admin scripts cleaning up zombie rows) have no way to detect refusal without a discriminator — real UX gap. Kept under High as a contract-clarity issue rather than a runtime defect.
- **Sources**: [C.Imp] (paired with C-08)

### H-21 — Telemetry: `messaging.destination.name` set to routing key, not exchange
- **Location**: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs:66-69`
- **Bug**: OTel messaging semconv says `messaging.destination.name` is the broker-side destination (exchange name for RabbitMQ). Current code writes the routing key, breaking dashboards keyed on destination.
- **Fix**: Write exchange name; add `messaging.rabbitmq.routing_key` for the routing key.
- **Sources**: [C#12]

### H-22 — `IFilter` vs `IFilterPipeline` inverted semantics
- **Location**: `src/ServiceConnect.Interfaces/IFilter.cs:17`, `src/ServiceConnect.Interfaces/IFilterPipeline.cs:10-22`
- **Bug**: `IFilter.ProcessAsync` returns `true` meaning "continue processing". `IFilterPipeline.ProcessAsync` returns `true` meaning "blocked" — i.e. stop. Easy to call a filter from a pipeline impl and invert the logic.
- **Fix**: Unify to a single convention (prefer explicit enum `Continue`/`Stop`).
- **Pass-2 verdict**: CONFIRMED (API-UX smell). XML docs document the asymmetry explicitly and the test names reflect the confusion (`BlockedAsync_returns_true_when_filter_returns_false`). Not a hidden defect — a documented API smell that regularly causes mistakes. Kept at High.
- **Sources**: [C.Imp, P.Imp]

---

## Medium

Observable bugs in narrow paths, hygiene issues that mask real bugs, or contract gaps.

### M-01 — `MaxActiveStreams` TOCTOU on `Count >= N` then `GetOrAdd`
- **Location**: `src/ServiceConnect/Services/Processors/StreamProcessor.cs:84-88`
- **Sources**: [C.Min, N.L3, P.Imp]

### M-02 — `LastSeenUtc` torn read (DateTimeOffset non-atomic, 10 bytes)
- **Location**: `src/ServiceConnect/Services/Processors/StreamProcessor.cs:101`
- **Sources**: [P.Imp]

### M-03 — `MessageBusReadStream` size-cap transient overshoot on duplicate retry
- **Location**: `src/ServiceConnect/Services/MessageBusReadStream.cs:79-92`
- **Sources**: [C.Imp]

### M-04 — `Consumer.StartConsumingAsync` finally's `CloseAsync` masks original exception
- **Location**: `src/ServiceConnect.Client.RabbitMQ/Consumer.cs:145-153`
- **Pass-2 verdict**: CONFIRMED. The `finally { await channel.CloseAsync(); }` can throw if the channel is already in a faulted state, and in that case the original setup exception is overwritten. Small diagnostic hazard — wrap with try/catch and log.
- **Sources**: [C.Imp]

### M-05 — RabbitMQ `EventAsync` admission-path `HandleTerminalFailureAsync` publishes unprotected
- **Location**: `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs:173-259`
- **Bug**: The terminal-failure publish path doesn't check the shutdown/dispose flags and can publish on a disposing producer.
- **Pass-2 verdict**: REJECTED. All terminal-failure publishes use `GetShutdownPublishToken()`, which is cancelled on shutdown; the publisher observes the cancellation and bails. Moved to Rejected.
- **Sources**: [N.M1]

### M-06 — `BasicConsumeAsync` does not pass `cancellationToken`
- **Location**: `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs:155`
- **Sources**: [P.Imp]

### M-07 — `Task.Delay` can receive a negative `TimeSpan` on last loop iteration
- **Location**: `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs:554-567`
- **Pass-2 verdict**: REJECTED. `remaining <= TimeSpan.Zero` guard exits the loop before `Task.Delay` is called — negative never reaches Delay. Moved to Rejected.
- **Sources**: [P.Imp]

### M-08 — Fire-and-forget cancel helper throws `ObjectDisposedException`/`ArgumentOutOfRangeException` on disposed CTS (unobserved)
- **Location**: `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs:534, 613-620`
- **Pass-2 verdict**: PARTIAL (theoretical). `ObjectDisposedException` is caught; `ArgumentOutOfRangeException` is only reachable if the clock moves backward. Defensive hardening, not a live bug.
- **Sources**: [C.Imp, P.Imp]

### M-09 — `ProcessMessageAsync` shutdown-OCE rethrow races with `_shutdownTimedOut` flag
- **Location**: `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs:256, 270, 391, 429`
- **Bug**: Rethrow condition (`_shutdownPublishCts.IsCancellationRequested`) and the finally condition (`_shutdownTimedOut != 0`) are two separate signals. A narrow shutdown race can cause the finally to `BasicNackAsync(..., requeue: true)` rather than leaving the message unacked.
- **Fix**: Add a guarded outer re-catch that matches the rethrow, or collapse the two signals to one.
- **Pass-2 verdict**: REJECTED. The two signals fire at different shutdown phases; the interleaving required for the claimed divergence is not reachable because `_shutdownTimedOut` is only set after `_shutdownPublishCts` is cancelled, and the finally path already treats both as equivalent "do not requeue". Moved to Rejected.
- **Sources**: [R.Med]

### M-10 — `BasicProperties` copy-ctor reference-shares `Headers` (latent)
- **Location**: `src/ServiceConnect.Client.RabbitMQ/MessageRetryHandler.cs:73-76, 124-127` + `MessageAuditPublisher.cs:56`
- **Pass-2 verdict**: REJECTED. `Headers` is immediately overwritten via object-initializer in all call sites; the copy-ctor's shared reference is never read. Moved to Rejected.
- **Sources**: [C.Imp]

### M-11 — `AggregatorProcessor.FlushAggregatorAsync` "remove-before-execute" drops batch on non-cancellation exception
- **Location**: `src/ServiceConnect/Services/Processors/AggregatorProcessor.cs:190-198`
- **Sources**: [N.M9] (overlaps with C.Min 196-198)

### M-12 — `HandlerProcessor`: first handler exception skips remaining handlers
- **Location**: `src/ServiceConnect/Services/Processors/HandlerProcessor.cs:70-77`
- **Bug**: Per-message multi-handler processing short-circuits on the first throw. Downstream handlers for the same message are skipped even though they are independent.
- **Fix**: Collect exceptions into an `AggregateException` (or decide on a documented short-circuit semantic) rather than leaking the first exception.
- **Sources**: [N.M8]

### M-13 — `Bus.DisposeAsync` disposes `_lifecycleSemaphore` while racing `StartConsumingAsync`
- **Location**: `src/ServiceConnect/Bus.cs:458`
- **Pass-2 verdict**: CONFIRMED (minor). Race exists; it produces a well-typed `ObjectDisposedException` rather than corruption, which is acceptable but ugly. Cheap to fix by guarding Dispose with an `if (Interlocked.Exchange(ref _disposed, 1) == 0)` gate before touching the semaphore.
- **Sources**: [P.Imp]

### M-14 — `ReapStaleLeasesAsync` has no batch cap
- **Location**: `src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs:321-343`
- **Bug**: Reap runs over every expired lease in one UpdateMany; a mass-expiry event can stall the transaction window.
- **Pass-2 verdict**: REJECTED. Server-side `UpdateMany` doesn't buffer documents client-side; it's a single atomic server op with no round-trip-per-row cost. Batching is a design tradeoff, not a bug. Moved to Rejected.
- **Sources**: [C.Imp]

### M-15 — `_indexCreationSemaphore` leaks (class not `IDisposable`)
- **Location**: `src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs:17-20`
- **Pass-2 verdict**: REJECTED. `SemaphoreSlim.AvailableWaitHandle` is never accessed, so no unmanaged `WaitHandle` is ever allocated. Pure managed state, GC-collectible — no leak. Moved to Rejected.
- **Sources**: [C.Imp]

### M-16 — Index-ensure flag cached across DB drop
- **Location**: `src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs:374-415`
- **Bug**: After an admin drops the database, the cached `indexesEnsured` flag prevents re-creation of indexes on next start.
- **Pass-2 verdict**: PARTIAL. The claim is valid in the DB-drop-without-process-restart scenario only (process restart resets the static flag). That scenario is a legitimate ops/test pattern, so the flag should invalidate on write errors. Kept at Medium.
- **Sources**: [C.Imp]

### M-17 — Aggregator index-ensure lacks bounded retry
- **Location**: `src/ServiceConnect.Persistence.MongoDb/MongoDbAggregatorPersistor.cs:230-255`
- **Pass-2 verdict**: REJECTED. No bounded-retry contract is declared for index-ensure; the first failure surfaces directly to the caller, which is the correct fail-fast behaviour. Moved to Rejected.
- **Sources**: [C.Imp]

### M-18 — `InMemoryAggregatorPersistor.RemoveDataAsync` requires `is Message`
- **Location**: `src/ServiceConnect.Persistence.InMemory/InMemoryAggregatorPersistor.cs:95-123`
- **Bug**: Third-party/DTO messages without `: Message` inheritance can't be aggregated on InMemory (Mongo accepts them).
- **Sources**: [C.Imp]

### M-19 — `InMemoryTimeoutStore` has no batch-size cap
- **Location**: `src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs:59-110`
- **Pass-2 verdict**: CONFIRMED — parity divergence. MongoDb persistor applies a caller-provided batch cap; InMemory ignores it. Third-party callers relying on cap-based back-pressure see unbounded returns on InMemory. Contract-parity issue, not a runtime fault.
- **Sources**: [N.M10]

### M-20 — `InMemoryTimeoutStore` shallow header clone for non-`byte[]` values
- **Location**: `src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs:112-134`
- **Sources**: [N.M11]

### M-21 — `InMemoryProcessManagerFinder.UpdateDataAsync` doesn't bump caller's `Version`
- **Location**: `src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs:243-249`
- **Bug**: Version numbers the store increments are not reflected back to the caller's object; subsequent updates with the caller's stale Version fail.
- **Sources**: [P.Imp]

### M-22 — Persistors bypass `EnsureGuidSerializerRegistered` when ctor'd directly
- **Location**: `MongoDbAggregatorPersistor.cs:150-153`, `MongoDbProcessManagerFinder.cs:39-66`, `MongoDbTimeoutStore.cs:38-67`
- **Bug**: `EnsureGuidSerializerRegistered` is called from DI factories only. Direct `new` construction (tests, custom composition) silently leaves Guid serialization on the driver's default (binary, subtype 3) — incompatible with the canonical serializer used elsewhere.
- **Fix**: Call `EnsureGuidSerializerRegistered` from each class's static ctor.
- **Sources**: [C#9, N.M12]

### M-23 — Telemetry Send uses invalid `"send"` for `messaging.operation`
- **Location**: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs:154-164`
- **Bug**: OTel semconv defines the values; `"send"` isn't one (it's `publish`).
- **Sources**: [C#11, P.Imp]

### M-24 — `TryEnrich` swallows `OperationCanceledException` + writes `ex.Message` (PII leak)
- **Location**: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs:341-363`
- **Sources**: [C.Imp]

### M-25 — `MessageConversationId` on publish-side only; missing on consume-side
- **Location**: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs:62, 95-140`
- **Sources**: [C.Imp]

### M-26 — `linkedContext` param is actually parent, not OTel link
- **Location**: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs:46, 146`
- **Bug**: Naming/semantic mismatch: the "linked" context is wired as parent context, so consume spans show as direct child of producer instead of a linked span.
- **Sources**: [C#10]

### M-27 — `ConsumeEventArgs.Headers` getter mutates `_headers` via `??=` (race)
- **Location**: `src/ServiceConnect.Interfaces/Events/ConsumeEventArgs.cs:22-29`
- **Sources**: [C.Imp, N.L21, P.Min]

### M-28 — `IMessageHandler`/`IProcessHandler.HandleAsync` omit `CancellationToken`
- **Location**: `src/ServiceConnect.Interfaces/IMessageHandler.cs:20`, `IProcessHandler.cs:26`
- **Note**: P agent rejected this claiming token is exposed via `IConsumeContext`. That's true for the common case but not a complete substitute — the token signature on the handler is still missing, making external handler implementations awkward.
- **Sources**: [C.Imp]

### M-29 — `IMessageBusWriteStream` methods lack `CancellationToken`
- **Location**: `src/ServiceConnect.Interfaces/IMessageBusWriteStream.cs:14, 19`
- **Sources**: [C.Imp, P.Min]

### M-30 — `RequestOptions` is a mutable sealed class (PublishOptions/SendOptions are records)
- **Location**: `src/ServiceConnect.Interfaces/Options/RequestOptions.cs`
- **Sources**: [C.Imp, N.L23]

### M-31 — `SendOptions.EndPoints` is mutable `IList<>` (vs readonly Headers)
- **Location**: `src/ServiceConnect.Interfaces/Options/SendOptions.cs:13, 23`
- **Sources**: [C.Imp, P.Imp]

### M-32 — `HeaderDecoder.Decode` returns type-name string for nested tables/arrays
- **Location**: `src/ServiceConnect.Interfaces/Headers/HeaderDecoder.cs:22-28`
- **Note**: A broken test already asserts the current broken behaviour — documented in N.M14.
- **Pass-2 verdict**: CONFIRMED — harmful fallback. XML doc claims "fallback to object.ToString for non-native types," but for tables/arrays this produces a useless `"System.Collections.Generic.Dictionary..."` string instead of recursively decoding the table/array, silently losing nested header data. Existing test lock-in is part of the bug. Kept at Medium.
- **Sources**: [N.M14]

---

## Low

Dead code, minor nullability/comment quirks, hygiene items, and hard-to-trigger edges. Listed compactly.

### Core / bus / processors
- **L-01** `AggregatorProcessor` Timer-fired flush can install new `_flushLocks` entry after Clear — `AggregatorProcessor.cs:101-156, 160, 240-243` — [C.Min, P.Min]
- **L-02** `Bus.DisposeAsync` skips `_lifecycleSemaphore.Dispose` if `StopConsumingCoreAsync` throws — `Bus.cs:446-459` — [N.L1] — **Pass-2: CONFIRMED** (latent; StopConsumingCoreAsync unlikely to throw but possible)
- **L-03** `ConsumeContextPool.Release` doesn't null-out refs — `ConsumeContextPool.cs:202-208` — [N.L2] — **Pass-2: REJECTED** (token-guard architecture makes null-out unnecessary; refs overwritten on next Initialize)
- **L-04** `ConsumeContextPool` Count-based soft cap race — `ConsumeContextPool.cs:31-38` — [C.Min] — **Pass-2: REJECTED** (race is documented in code as acceptable soft cap)
- **L-05** `HandlerScanner` only catches `ReflectionTypeLoadException` — `HandlerScanner.cs:33-48` — [N.L5] — **Pass-2: REJECTED** (narrow catch is intentional by design)
- **L-06** `MessageTypeRegistry.Register` partial-failure leaves orphan AQN mapping — `MessageTypeRegistry.cs:54-72` — [N.L6] — **Pass-2: CONFIRMED** (latent: rare trigger but real orphan)
- **L-07** `MessageDispatcher` missing `ConfigureAwait(false)` — `MessageDispatcher.cs:110, 157, 193` — [C.Min, N.L7, P.Min]
- **L-08** `ReadOnlyMemoryStream`/`ReadOnlySequenceStream` `Read` skip standard argument validation — [N.L8] — **Pass-2: REJECTED** (internal-only streams; caller guarantees hold)
- **L-09** `ConsumeContextAccessor.Scope.Dispose` not Interlocked-guarded — `ConsumeContextAccessor.cs:22-31` — [N.L9, P.Min] — **Pass-2: REJECTED** (AsyncLocal write is idempotent)
- **L-10** `RequestReplyManager` swallows send-time cancellation, resolves via TCS timeout — `RequestReplyManager.cs:56-58, 137-139` — [N.L10] — **Pass-2: REJECTED** (caller's own CT propagates correctly; only internal-timeout path uses TCS)
- **L-11** `RequestReplyManager.CancelAfter` doesn't validate `options.Timeout` — `RequestReplyManager.cs:35-36, 107-108, 182-183` — [C.Min]
- **L-12** `MessageBusWriteStream` uses `DateTime.UtcNow` instead of `TimeProvider` — `MessageBusWriteStream.cs:93-99` — [P.Min]
- **L-13** `ConsumeContext` lazy `_messageIdCached` pair without memory barrier — `ConsumeContext.cs:71-100` — [P.Min] — **Pass-2: REJECTED** (ConsumeContext is per-invocation, not shared; no memory-barrier concern)
- **L-14** `Bus.IsConsuming` reads `_consuming` without lock — `Bus.cs:76` — [P.Min]

### RabbitMQ transport
- **L-15** `ConsumeMessageTypeAsync` lacks CancellationToken — `RabbitMqConsumerHost.cs:159-162` — [C.Min]
- **L-16** On shutdown timeout, delivery left unacked, prefetch slots occupied — `RabbitMqConsumerHost.cs:270-273` — [P.Min] — **Pass-2: REJECTED** (explicitly documented tradeoff with log statement)
- **L-17** Event handlers subscribed to `UnderlyingConnection` relies on facade-preserves-subs — `RabbitMqConsumerHost.cs:145-153` — [P.Min] — **Pass-2: STALE** (facade code path has been refactored; architectural risk no longer triggered)
- **L-18** `_connected` not a reliable liveness signal — `Producer.cs:96` — [C.Min]
- **L-19** `BasicPublishAsync` timeout masks caller cancellation in reconnect path — `Producer.cs:513-549` — [C.Min] — **Pass-2: REJECTED** (`when` clause excludes caller cancellation; only timeout CTS is remapped)
- **L-20** Exchange-name cache keyed on `AssemblyQualifiedName` (version-churn dup entries) — `Producer.cs:235` — [P.Min]
- **L-21** Non-matching `OperationCanceledException` falls into generic retry — `Retry.cs:90` — [P.Min]
- **L-22** `MessageAuditPublisher` fast-path doesn't check CT — `MessageAuditPublisher.cs:46-54` — [C.Min]

### InMemory persistence
- **L-23** `CacheProvider` `Keys<>()` lazy-snapshot comment misleading — `CacheProvider.cs:139-146` — [C.Min] — **Pass-2: REJECTED** (doc inaccuracy, not code defect)
- **L-24** `CacheProvider.Remove` not inside `_addLock` — `CacheProvider.cs:225-260` — [P.Min] — **Pass-2: REJECTED** (design intentionally accepts this race per ConcurrentDictionary semantics)
- **L-25** `CacheProvider` no disposed-guard on public mutators — `CacheProvider.cs:39-205, 214` — [R.Low]
- **L-26** `InMemoryProcessManagerFinder.FindMatchingItem` DeepClones every candidate — `InMemoryProcessManagerFinder.cs:90-125` — [P.Min]
- **L-27** Dead `DefaultNextQueryInterval` field — `InMemoryProcessManagerFinder.cs:34` — [C.Min]
- **L-28** `GetSnapshotAsync.UnresolvedCount` hardcoded 0 — `InMemoryAggregatorPersistor.cs:88` — [C.Min]
- **L-29** `CacheItem.Value` nullability inconsistency — `CacheItem.cs:9, 17` — [C.Min] — **Pass-2: PARTIAL** (nullability inconsistency, not runtime defect — annotation tightening)

### MongoDb persistence
- **L-30** `RemoveDataAsync` tie-break semantics differ (Mongo arbitrary vs InMemory oldest) — `MongoDbAggregatorPersistor.cs:150-156` vs `InMemoryAggregatorPersistor.cs:104-112` — [P.Imp] — **Pass-2: REJECTED** (both delete by CorrelationId match; no observable tie-break divergence in the actual code path)
- **L-31** `_concurrencyGuardsEnabled` evaluated once, never re-checked — `MongoDbProcessManagerFinder.cs:57-65` — [C.Min] — **Pass-2: REJECTED** (WriteConcern is immutable after client creation; re-check pointless)
- **L-32** `CountAsync` silently truncates at `int.MaxValue` — `MongoDbAggregatorPersistor.cs:217` — [C.Min] — **Pass-2: REJECTED** (explicit clamp to int.MaxValue is documented behaviour, not silent)
- **L-33** No precondition check `Id != Guid.Empty` on `InsertTimeoutAsync` — `MongoDbTimeoutStore.cs:70-85` — [C.Min]
- **L-34** `AggregatorSnapshot.ResolvedIds` typed concrete `List<Guid>` — [C.Min] — **Pass-2: REJECTED** (surface is `IReadOnlyList<Guid>` interface, not `List<Guid>`)
- **L-35** `MongoClientFactory.ClearCertificateCache` race with concurrent `Create` (test-only) — `MongoClientFactory.cs:103-110` — [C.Min]
- **L-36** `MongoDbData<T>` relies on Id convention (add `[BsonId]`) — `MongoDbData.cs:16` — [C.Min] — **Pass-2: REJECTED** (driver's `IdMemberConvention` maps `Id` property by convention; attribute is redundant)
- **L-37** `AggregatorDocument` missing `[BsonIgnoreExtraElements]` — [C.Min]
- **L-38** `TimeoutData.Time`/`LockExpiresAt` stored as `DateTimeOffset` sub-doc (queries don't index cleanly) — [C.Min]
- **L-39** `MongoClientFactory` certificate load leaks on `GetOrAdd` factory re-entry — `MongoClientFactory.cs:77-96` — [N.L12]
- **L-40** `MongoDbAggregatorPersistor.GetSnapshotAsync` `BsonSerializationException` leaks past `MongoException` catch — `MongoDbAggregatorPersistor.cs:104-142` — [N.L13]
- **L-41** `UpdateDataAsync` checks `ModifiedCount == 0` instead of `MatchedCount == 0` — `MongoDbProcessManagerFinder.cs:232-242` — [N.L14]
- **L-42** `MongoDbAggregatorPersistor.InsertDataAsync` no null-check on data — `MongoDbAggregatorPersistor.cs:68-88` — [P.Min]
- **L-43** `MongoDbProcessManagerFinder` `catch(TargetInvocationException)` dead code — `MongoDbProcessManagerFinder.cs:176-179` — [P.Min] — **Pass-2: CONFIRMED** (dead code — CLR doesn't wrap in `TargetInvocationException` for compiled lambda expressions, only for `MethodInfo.Invoke`)

### Telemetry
- **L-44** Consume span never gets `SetError` wired — `ServiceConnectActivitySource.cs:95-140, 224-225` — [C.Min, N.M16]
- **L-45** `MessagingOperation` uses deprecated `messaging.operation` key — `MessagingAttributes.cs:22` — [C.Min]
- **L-46** Options / `MessagingSystemAttributes` mutable statics — `ServiceConnectActivitySource.cs:16, 20` — [C.Min, N.L20]
- **L-47** `StartActivity` sets messaging-semconv tags AFTER start (sampler impact) — `ServiceConnectActivitySource.cs:313-334` — [N.L15]
- **L-48** `InjectHeader` matches concrete `Dictionary`; extractor matches interface (asymmetric) — `ServiceConnectActivitySource.cs:307-311` — [N.L16]
- **L-49** `SetError` stacktrace uses `exception.ToString()` duplicates info — `ServiceConnectActivitySource.cs:236-241` — [N.L17]
- **L-50** `SetError` doesn't unwrap `AggregateException.InnerExceptions` — `ServiceConnectActivitySource.cs:227-243` — [N.L18]
- **L-51** Publish/Send emits `Guid.Empty` as `messaging.message.conversation_id` — `ServiceConnectActivitySource.cs:62, 209` — [N.L19]
- **L-52** `ActivityContext.TryParse` return value discarded; malformed tracestate lost — `ServiceConnectActivitySource.cs:97-98, 261` — [N.L28, P.Min]
- **L-53** `TryEnrich` writes `enrichment.exception` as tag, not ActivityEvent (overwrites, non-semconv) — `ServiceConnectActivitySource.cs:341-348, 355-363` — [N.L29]
- **L-54** `ExtractTraceIdAndState` never populates values out-parameter — `ServiceConnectActivitySource.cs:274-292` — [N.L27] — **Pass-2: REJECTED** (`values` out-param intentionally set to `default` for single-value path; correct W3C propagator pattern)
- **L-55** `InjectTraceContext` no-op when no ambient activity AND telemetry disabled — `ServiceConnectActivitySource.cs:48-50, 301-305` — [N.L26]
- **L-56** `TryGetExistingContext` parameter nullability inconsistent with body — `ServiceConnectActivitySource.cs:250` — [C.Min]
- **L-57** `InjectTraceContext` doesn't null-check headers — `ServiceConnectActivitySource.cs:301` — [P.Min]

### Interfaces
- **L-58** `RequestOptions.Default` allocates on every access — `RequestOptions.cs:17` — [C.Imp]
- **L-59** `HeaderDecoder.Decode` doesn't trap `ToString` exceptions — `HeaderDecoder.cs:22-28` — [C.Min]
- **L-60** `HeaderDecoder` UTF-8 decoder not defensively constructed (global `Encoding.UTF8`) — `HeaderDecoder.cs:25` — [N.M15] — **Pass-2: REJECTED** (uses BCL's `Encoding.UTF8` singleton — no construction involved, no defence needed)
- **L-61** `Message` primary-ctor deserialization ambiguity across JSON libs — `Message.cs:16` — [N.M17] — **Pass-2: REJECTED** (Newtonsoft handles primary ctors via `ConstructorHandling.AllowNonPublicDefaultConstructor`; STJ also supports them)
- **L-62** `ProcessResult.Handled == 0` default silently means stop — `IMessageProcessor.cs:6-17` — [C.Min]
- **L-63** Exceptions not `[Serializable]` — various — [C.Min]
- **L-64** `TimeoutData` no equality — `TimeoutData.cs` — [C.Min]
- **L-65** `IConsumeContext.CorrelationId` has no absent representation — `IConsumeContext.cs:20` — [C.Min] — **Pass-2: REJECTED** (`Guid.Empty` is the conventional sentinel across the codebase)
- **L-66** Aggregator `BatchSize`/`Timeout` no input validation — [C.Min] — **Pass-2: REJECTED** (default/0 values have explicit documented semantics — "no cap" / "no timeout")
- **L-67** `AggregatorSnapshot.Empty` record-equality pitfall on `IReadOnlyList<>` — [C.Min]
- **L-68** `ITransportConfiguration.Host` non-nullable but not `required` — `ITransportConfiguration.cs:15` — [C.Min]
- **L-69** `Envelope.Headers` / `TimeoutData.Headers` eagerly allocate — `Envelope.cs:9`, `TimeoutData.cs:31` — [C.Min, N.L30] — **Pass-2: PARTIAL** (eager alloc is real but these types always need headers; trivial overhead)
- **L-70** `IProducer.MaximumMessageSize` units/sentinel undocumented — `IProducer.cs:33` — [C.Min]
- **L-71** `HandlerReference` / `ProcessManagerToMessageMap` no value-equality — [N.L22]
- **L-72** `IBus.RequestTimeoutAsync` default-interface-method throws `NotSupportedException` — `IBus.cs:87-88` — [N.L24]
- **L-73** `ILeaseAwareTimeoutStore` doesn't extend `ITimeoutStore` — `ILeaseAwareTimeoutStore.cs:12` — [N.L25]
- **L-74** `HeaderKeys` are transport-literal (RabbitMQ casing) — `HeaderKeys.cs` — [N.L31] — **Pass-2: REJECTED** (PascalCase is ServiceConnect's own convention, not RabbitMQ's — mischaracterised)
- **L-75** `IMessageDispatcher`/`IMessageProcessingMiddleware`/`ISendMessageMiddleware` Task-returning methods without `Async` suffix — [P.Imp]

---

## Contract-parity matrix

| Contract | InMemory | MongoDb | Status |
|---|---|---|---|
| PM InsertDataAsync duplicate | `PersistenceException` | `PersistenceException` (wraps 11000) | **Consistent (intentional)** — `PersistenceException` for dup-insert is the intended contract; `ConcurrencyException` is reserved for version mismatch. Pass-2 resolved H-18 as NOT-A-BUG. |
| PM UpdateDataAsync mutation hygiene | Deep-clone | Reference-share (but serialize is synchronous) | **Implementation differs but no observable divergence** (pass-2 resolved H-19 as NOT-A-BUG) |
| TimeoutStore.RemoveDispatchedTimeoutAsync(id) on leased row | Removes | Silent no-op | **Divergent** (see C-08, H-20 — silent no-op is documented safety for canonical callers; 3rd-party UX gap remains) |
| TimeoutStore.ReleaseDispatchedTimeoutAsync(id) on leased row | Releases | Silent no-op | **Divergent** (see C-08, H-20) |
| TimeoutStore batch-size cap | Ignored | Applied | **Divergent** (see M-19) |
| Lease-aware Remove on stale lease | `ConcurrencyException` | `ConcurrencyException` | Aligned |
| AggregatorPersistor.RemoveDataAsync on missing | `ConcurrencyException` | `ConcurrencyException` | Aligned |
| Wake-up when peer holds due lease | Wakes at lease expiry | 1-minute fallback | **Divergent** |

---

## Rejected / non-findings

Preserved so future reviews don't re-litigate them.

### Rejected in the source reviews
- **InMemoryTimeoutStore SortedSet mutation during iteration** (R) — sort key (`Time,Id`) is on the outer record; mutations are on `entry.Data`, so the `SortedSet` ordering is not invalidated.
- **InMemoryAggregatorPersistor GetOrCreateEntries KV-race** (R) — `_provider` here is a private `new CacheProvider(...)`, not the shared `IKeyValueStore`. All access is under `_memoryCacheLock`.
- **CacheProvider.Update unbounded retry spin** (R) — textbook optimistic-concurrency CAS. Bounded by key removal (`TryGetValue` fails → loop exits).
- **ServiceConnectActivitySource mutable static Options** (P) — intentional single-set-at-startup pattern.
- **Consumer.cs:148 setupChannel.CloseAsync no CT** (P) — intentional cleanup on cancelled startup.
- **Publish double-inject of trace context** (P) — intentional (W3C context re-inject on retry).

### Rejected during my verification pass
None beyond the above; all other agent-flagged items were either confirmed or merged into existing entries.

### Rejected during pass-2 opus verification (2026-04-24)
Items retain their original IDs in the severity sections above — see inline `**Pass-2 verdict**:` on each entry for detail. Grouped here by reason.

**Mechanism wrong or not reachable**
- **C-07** Destination-empty zombie loop — removal runs unconditionally after dispatch guard; row is deleted, not polled forever.
- **C-10** stale utcNow double-lease — CAS via re-applied `dueUnlockedFilter` on UpdateMany excludes peer-renewed lease.
- **H-04** Producer NRE after reconnect — `_model` is volatile and `_publishLock` is held across retry; no observable NRE window.
- **H-09** `Retry.CalculateDelay` negative `baseInterval` — upper bound `Math.Min(..., 1000)` caps the arg; internal call-sites never pass negative.
- **H-14** Newtonsoft shared `JsonSerializer` — `DefaultContractResolver` is documented thread-safe once constructed; repeated myth from older versions.
- **H-16** `CacheProvider` orphan timer — fires once, observes no cache entry, no-ops thereafter; not state corruption.
- **H-17** `DeepClone` default-ctor requirement — code uses `JsonConvert` round-trip, not `Activator.CreateInstance`; claim's mechanism wrong.
- **H-18** Mongo duplicate-CorrelationId exception type — `ConcurrencyException` is reserved for version-mismatch; `PersistenceException` for dup-insert is consistent across both persistors.
- **H-19** Mongo reference-share during serialize — BSON serialization is synchronous in send path; mutation mid-serialize not reachable.
- **M-05** Terminal-failure publish unprotected — uses `GetShutdownPublishToken()` which is cancelled on shutdown.
- **M-07** `Task.Delay` negative TimeSpan — `remaining <= TimeSpan.Zero` guard exits loop before Delay.
- **M-09** Shutdown-OCE rethrow race — signals don't interleave in the claimed order; finally path treats both signals equivalently.
- **M-10** `BasicProperties` copy-ctor shared Headers — `Headers` is immediately overwritten via object-initializer at all call-sites.
- **M-14** `ReapStaleLeasesAsync` batch cap — `UpdateMany` is single atomic server op, no client-side buffering.
- **M-15** `_indexCreationSemaphore` leak — `AvailableWaitHandle` never accessed; pure managed state, GC-collectible.
- **M-17** Aggregator index-ensure retry — no bounded-retry contract declared; first-failure surfacing is intentional.
- **L-10** `RequestReplyManager` swallows cancellation — caller's CT propagates correctly; only internal timeout CTS is remapped.
- **L-13** `ConsumeContext` memory-barrier — ConsumeContext is per-invocation, not shared across threads.
- **L-19** `BasicPublishAsync` cancellation masking — `when` clause excludes caller cancellation; only timeout CTS is remapped.
- **L-30** Mongo/InMemory `RemoveDataAsync` tie-break — both delete by CorrelationId match; no observable divergence.
- **L-34** `AggregatorSnapshot.ResolvedIds` concrete type — public surface is `IReadOnlyList<Guid>`, not `List<Guid>`.
- **L-36** `MongoDbData<T>` missing `[BsonId]` — driver's `IdMemberConvention` maps `Id` property by convention.
- **L-54** `ExtractTraceIdAndState` out-param — `values = default` for single-value path is correct W3C propagator pattern.
- **L-60** `HeaderDecoder` UTF-8 — uses BCL `Encoding.UTF8` singleton, no defensive construction needed.
- **L-61** `Message` primary-ctor deserialization — Newtonsoft and STJ both support primary ctors in current versions.

**Intentional design / documented behaviour**
- **H-13** `ProcessManagerTimeoutService` shutdown-token on Release — intentional lease model; peer reaps or lease expires.
- **H-20** Mongo id-only Remove silent no-op — documented safety property for canonical callers (retained as PARTIAL for 3rd-party call-site UX gap).
- **L-03** `ConsumeContextPool.Release` no null-out — token-guard architecture; refs overwritten on next Initialize.
- **L-04** Pool count-based soft cap race — documented in code comment as acceptable.
- **L-05** `HandlerScanner` narrow catch — intentional narrow scope.
- **L-08** Stream-argument validation skip — internal-only types; caller guarantees hold.
- **L-09** AsyncLocal Dispose not Interlocked — write is idempotent.
- **L-16** Shutdown-timeout unacked delivery — explicitly documented tradeoff with log statement.
- **L-23** `Keys<>()` lazy-snapshot comment — doc accuracy, not code defect.
- **L-24** `CacheProvider.Remove` not under add-lock — intentional per ConcurrentDictionary semantics.
- **L-31** `_concurrencyGuardsEnabled` one-shot evaluation — WriteConcern is immutable after client creation.
- **L-32** `CountAsync` int.MaxValue clamp — explicit documented behaviour.
- **L-65** `IConsumeContext.CorrelationId` absent-representation — `Guid.Empty` is conventional sentinel.
- **L-66** Aggregator `BatchSize`/`Timeout` validation — default/0 have explicit documented semantics.
- **L-74** `HeaderKeys` casing — ServiceConnect's own PascalCase convention, not RabbitMQ's native casing.

**Reclassified (kept but re-scoped)**
- **C-09** Mongo nextPipeline — RECLASSIFIED to dead code smell (NextQueryTime never consumed by caller).
- **H-03** `QueueBindAsync` arg sharing — RECLASSIFIED to cosmetic only (broker ignores binding args on non-`headers` exchanges).
- **H-08** `Connection.DisposeAsync` unbounded wait — RECLASSIFIED to latent latency (lock always released on successful `ConnectAsync`; only stuck-network scenarios matter).
- **L-17** UnderlyingConnection event handlers — STALE (facade refactor has removed the risk surface).

**Pass-2 upgrades to CONFIRMED** (were equivocal in pass-1)
- **C-03** write-stream packet-number increment gap — CONFIRMED.
- **C-06** `ProcessManagerProcessor` MapperCache + root-provider — CONFIRMED both sub-claims.
- **H-10** `AggregatorProcessor` Dispose race — CONFIRMED (narrow snapshot-after-ProcessAsync).
- **H-22** `IFilter` vs `IFilterPipeline` inverted semantics — CONFIRMED API-UX smell.
- **M-04** `Consumer.StartConsumingAsync` finally masking — CONFIRMED.
- **M-13** `Bus.DisposeAsync` semaphore race — CONFIRMED (produces ObjectDisposedException, not corruption).
- **M-19** `InMemoryTimeoutStore` batch cap — CONFIRMED (contract-parity divergence with Mongo).
- **M-32** `HeaderDecoder` nested table fallback — CONFIRMED harmful behaviour, existing test lock-in part of the bug.
- **L-02** `_lifecycleSemaphore` skip-on-throw — CONFIRMED latent.
- **L-06** `MessageTypeRegistry` orphan AQN mapping — CONFIRMED latent.
- **L-43** `catch(TargetInvocationException)` — CONFIRMED dead code (compiled expressions don't wrap).

---

## Notes

- Reviews reference `consolodated-issues/2026-04-22-consolidated-issues.md` as the "already remediated" tracker. Items matching that tracker were excluded upstream; this document is the delta.
- Severity mapping across reviews was not uniform — C "Important" ≈ N "High"/"Medium" ≈ P "Important" ≈ Mid-to-High on this document. Assignments here are calibrated on blast radius and detectability, not averaged across reviewers.
- Each finding carries source-review citations in brackets so contributors can trace the original wording and tradeoffs.
