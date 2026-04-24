# Phase 2 — End-of-Phase Minor Sweep

Items flagged by code-quality reviewers during per-group review that were **not** spec-gap blockers. Batch into a single cleanup commit after Group 7 lands, before the final holistic review.

---

## Group 4 — Consumer lifecycle (commits d42217b1, 735f1b10)

- **Log em-dash inconsistency** — `Consumer.cs:198` uses `—`; rest of file uses plain ASCII. Normalize at end of phase with a grep across all `Consumer.cs`-neighbouring files.
- **Missing structured-logging fields on dispose warning** — `Consumer.cs:198` — the M11 warning has no `{QueueName}` / host index. Consider promoting to `IConsumerHost` sub-interface exposing `ConsumerTag` so `_clients.OfType<IConsumerHost>()` can enrich the log. Debuggability regression on the common path.
- **M11 test is two-host** — `ConsumerTests.cs` — `ConcurrentBag` iteration order is unspecified; a 3-host test where the middle host throws would give stronger evidence of "continues past failure". Also consider asserting `hostA.DisposeAsync()` was invoked.
- **M12 test is white-box via reflection** — `ConsumerTests.cs` — setting `_started = 1` via reflection is close to tautological. A stronger test: mock `CreateChannelAsync` to return a never-completing Task, kick off first `StartConsumingAsync` without awaiting, yield once, then call again and assert `InvalidOperationException`.
- **Dispose → restart semantic is unusual** — `Consumer.cs:214` — a disposed object is normally expected to throw `ObjectDisposedException` on further calls. Consider adding a separate `_disposed` flag so `StartConsumingAsync` can throw `ObjectDisposedException` after dispose. If dispose-then-restart is **intended**, add an XML-doc note on `DisposeAsync`.
- **Race: concurrent `StartConsumingAsync` + `DisposeAsync`** — pre-existing but surfaced by M12. Not a regression; log as follow-up for Phase 3 or a later medium.
- **Comment at `Consumer.cs:169-171`** still says "tolerates half-started hosts" — could now mention M11 behavior too.

---

## Group 5 — Consumer input validation & broker observability (commits e2d2e7e2, 02ac2435)

- **Connection unsubscription depends on teardown ordering** — `RabbitMqConsumerHost.cs:143,592`. Subscribe captures `_connection.UnderlyingConnection` at start; unsubscribe re-reads it at dispose. If `Connection.DisposeAsync` runs first (sets `_connection = null` at `Connection.cs:104`), the three connection-level handlers are never detached. Benign today (the `IConnection` is being torn down anyway), but a latent leak the day a reconnect-on-failure feature lands. Fix: cache the `IConnection` reference in a host-level field at subscription time; unsubscribe from that cached reference.
- **Log-string coupling in E2E and unit tests** — `BrokerInitiatedCancelTests.cs:42`, `RabbitMqConsumerHostTests.cs:1434,1471,1505`. Substring match on "shutdown" is fragile. Consider exposing a testable signal (e.g. `internal TaskCompletionSource ShutdownObserved` visible via `InternalsVisibleTo`) and have tests subscribe to it.
- **Double-log on queue-delete path** — `RabbitMqConsumerHost.cs:484`. `basic.cancel` fires `UnregisteredAsync`; channel close often follows, firing `ChannelShutdownAsync` + consumer `ShutdownAsync`. Up to three Warning lines per shutdown. Cosmetic, but an `Interlocked.Exchange(ref _shutdownLogged, 1) == 0` gate would dedupe.
- **`IServiceConnectConnection.UnderlyingConnection` interface growth** — `IServiceConnectConnection.cs`. Defensible, but leaks `RabbitMQ.Client.IConnection` through the abstraction. Alternative shape: a `SubscribeConnectionEvents(handlers)` method on `Connection` that encapsulates the client type. Worth revisiting during the next abstraction-boundary pass; don't revert now.
- **`Task.Delay(50)` sleeps in handler-attachment tests** — `RabbitMqConsumerHostTests.cs:1467,1501`. Flaky on loaded CI. Replace with a TCS-signalled CapturingLogger and `.WaitAsync(TimeSpan)`.
- **M10 direct-parsing edge cases only covered indirectly** — `MessageRetryHandler.cs:49-50`. Out-of-range / `-1` paths validated via `HandleFailureAsync` observable behavior only. Minor gap; acceptable given the "no new internal helper" decision but worth a note.
- **E2E double-dispose of Connection** — `BrokerInitiatedCancelTests.cs:110-116`. `bus.DisposeAsync` then `provider.DisposeAsync` disposes Connection twice. Safe (idempotent via `_disposed`) but reverse order would be cleaner.

## Group 6 — Persistence correctness (commits 503f4271, 23b7f796, fcf2814d, 6501e3f7)

- **Commit message rationale correction needed on 6501e3f7** — The message justifies `ConcurrencyException` by claiming `ProcessManagerProcessor` "retries on" it; actually the loop was removed (see `ProcessManagerProcessor.cs:63-69` — `ConcurrencyException` propagates to transport-level retry). Exception choice is still correct (matches sibling version-mismatch throws) but the rationale is misleading to future readers. Either amend the message or drop a note in the tracker next to M18.
- **`MongoDbProcessManagerFinder.cs:20` — `SemaphoreSlim` never disposed.** `IProcessManagerFinder` doesn't extend `IDisposable`; the class is `sealed`. Low-impact (`SemaphoreSlim` without `AvailableWaitHandle` holds no kernel handle; finders are process-lifetime). If the interface ever gains `IAsyncDisposable`, clean this up.
- **`MongoDbProcessManagerFinder.cs:57` — client-level WriteConcern detection is theoretically incomplete.** `mongoClient.Settings.WriteConcern.IsAcknowledged` ignores per-database and per-collection overrides. Current code paths don't override, so detection is correct today. Add a defensive comment noting the assumption, or read `collection.WriteConcern` lazily.
- **`src/ServiceConnect.EndToEndTests/ProcessManagers/MongoDbProcessManagerFinderTests.cs:277` — test name `UpdateDataAsync_WithW0_DoesNotSilentlySwallowResult` mis-titled.** The body only asserts "no throw"; silent-swallow isn't verified because Mongo can't report it under w:0. Either inject a capturing `ILogger` and assert the one-time Warning was emitted, or rename to `UpdateDataAsync_WithW0_DoesNotThrow`.
- **`src/ServiceConnect.UnitTests/InMemoryProcessManagerFinderTests.cs:635` — M18 race test is timing-probabilistic.** 200×200 scheduler-dependent race may never hit the `Keys()/Get()` interleave, so false-pass is possible. The follow-up's Mock-based approach (6501e3f7) is deterministic; consider replacing this one or leaving it alongside as a stress guard.
- **`InMemoryProcessManagerFinderTests.cs:638,640` — dead locals** (`provider`, `state`) unused before the "Simplest reliable approach" rewrite takes over. Remove.
- **Narrative comment on lines 642-649** describing considered-and-discarded approaches. Delete the narrative; keep only the chosen approach.
- **Test Arrange boilerplate duplication** between the two new M18 follow-up tests. A `CreateFinderWithMockProvider()` helper would DRY them and make a third case (e.g. a Mock-driven `FindDataAsync` test replacing the probabilistic one) cheap.
- **`InMemoryPersistenceState.cs:24` comment** says "caller owns lifetime" — good, but doesn't explain *why* the internal test-seam ctor exists. One line pointing to the M18 regression test would help future archaeology.
- **`ICacheProvider` interface growth** — added `Add<TKey,TValue>(TKey, TValue, CacheItemPriority)` overload. Binary-break for any external implementers (none known; only `sealed CacheProvider` implements it in-tree). Could have avoided by seaming through `CacheProvider` directly, but the additive interface change is defensible on its own merit.

## Group 7 — Telemetry trace-context & status (commits 952e3ac5, 6e14295c)

- **`ServiceConnectActivitySource.cs:214` — net8 `exception.stacktrace` uses `exception.ToString()`.** This prepends type+message to the stack trace. `AddException` on net9+ does the same in the current BCL, so the two branches align — but for strict OTel semconv conformance `exception.StackTrace` (nullable for never-thrown exceptions) is more literal. Awareness-only.
- **`ServiceConnectActivitySourceTests.cs:138-152` — `Publish_WhenTelemetryDisabled_DoesNotTouchHeaders` test name overstates the invariant.** It only passes because no ambient `Activity.Current` is set; with an ambient span, headers ARE touched (per the new M20 test at line 351). Rename to `Publish_WhenTelemetryDisabled_AndNoAmbientActivity_DoesNotTouchHeaders` or add a comment pointing to the M20 companion test.
- **Missing regression test: nested SC activities.** A handler that republishes from inside a `Consume` span is the real-world case where double-inject must guarantee the inner span's traceparent wins. Add: start `Consume` activity, call `Publish` inside it, assert published `traceparent` matches `Publish` activity's `SpanId`.
- **`SetError` test coverage is shallow.** Only asserts `exception.type`. Extend to assert `exception.message`, `exception.stacktrace`, and `activity.StatusDescription == ex.Message` to guard the net8 fallback branch against silent drift.
- **`SetError` XML doc** — add a note encouraging callers to invoke it from inside the catch block (after `throw` re-raise) so the status description reflects the real cause. Also note that downstream consumers (Bus/Producer/RabbitMqConsumerHost) don't currently call it — it's a public integration point awaiting wire-up.
- **Activity/span `SetStatus(Error, exception.Message)`** includes raw exception text in traces. Standard OTel pattern, but worth a comment: messages may contain connection strings / user data; user trace-sanitisation is the caller's responsibility.
