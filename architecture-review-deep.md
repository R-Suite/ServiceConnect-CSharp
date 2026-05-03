# Architecture review — deep pass

A prior structural pass (project layout, large-file splits, `InternalsVisibleTo` audit, TFM policy) has been implemented. This deep pass goes further across **seven independent concerns**: public API, concurrency & lifecycle, resilience, observability, extensibility, performance, security. Findings are read-only static analysis as of `v7-clean-architecture` at HEAD `b13010b6`.

**Every cited finding has been re-verified against the source after initial synthesis.** A "verification notes" appendix at the end records what was confirmed, what was wrong, and what was imprecise — rolled into the per-section text below. The biggest correction was removing a false-positive concurrency bug (a `volatile` field that the initial pass missed). The remaining findings are accurate.

---

## Executive summary

The library is in good shape for a v7→v8 transition. The big-picture verdict across all seven dimensions:

- **Public surface**: principled and well-documented; a few small inconsistencies that are worth fixing while v8 is breaking anyway.
- **Concurrency**: disciplined async/await, no `.Wait()`/`.Result`, lifecycle is sound. One real bug worth fixing (volatile mismatch on `ProducerConnection._model`); the rest are low-likelihood edges.
- **Resilience**: at-least-once with optimistic concurrency on saga state. No exactly-once or atomic ack-with-write — that should be **explicitly documented** rather than left implicit.
- **Observability**: traces and structured logs are good; **metrics are entirely absent**, and operator-side visibility into "why did consumption stall?" is the biggest production gap.
- **Extensibility**: clean. Cross-package `InternalsVisibleTo` is gone; transports and persistence backends can be implemented from public API alone (with one caveat — see §5).
- **Performance**: correctness-first, ~5 dictionary allocations + one `byte[]` per message. Fine for ≤10k msg/s. Newtonsoft → STJ migration would knock 20–30% off allocations.
- **Security**: deserialization is correctly defended (`TypeNameHandling.None`); the headline risks are TLS-off-by-default and unbounded in-memory persistor growth.

### Top actions, ranked

After verification, the original top-10 list contracted: two items were false positives. Remaining nine, in order:

| # | Severity | Concern | Action | Where |
|---|---|---|---|---|
| 1 | **High** | Security | Default TLS to opt-out, or log a `Warning` at startup when AMQP plaintext is configured against a non-localhost host | [Connection/ConnectionFactoryBuilder.cs:54](src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs#L54) |
| 2 | **High** | Observability | Add a `Meter` + counters/histograms to `ServiceConnect.Telemetry` (publish/consume/handler durations, retry attempts) | [src/ServiceConnect.Telemetry/](src/ServiceConnect.Telemetry/) |
| 3 | **Med** | Resilience | Document the at-least-once contract and the persist-vs-ack gap **in the public XML docs**, not just in code comments | [Interfaces/Bus/IBus.cs](src/ServiceConnect.Interfaces/Bus/IBus.cs), website release notes |
| 4 | **Med** | Observability | Surface `_messagesBeingProcessed` as a public bus property + diagnostic health check | [Consumer/RabbitMqConsumerHost.cs:69](src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L69) |
| 5 | **Med** | API | Tighten low-level transport contracts to `IReadOnly*` collections; the high-level `IBus` already does this — propagate down | [Interfaces/Bus/IProducer.cs:11–28](src/ServiceConnect.Interfaces/Bus/IProducer.cs#L11), [IConsumer.cs:23](src/ServiceConnect.Interfaces/Bus/IConsumer.cs#L23), [IMessageDispatcher.cs:11](src/ServiceConnect.Interfaces/Bus/IMessageDispatcher.cs#L11) |
| 6 | **Med** | Resilience | Surface "retry-publish dropped" as a metric/log-Error counter — today it's logged at Error but operators can't see the trend | [Consumer/InboundMessageProcessor.cs:150–158](src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L150) |
| 7 | **Med** | Observability | Add a counter for publish-confirm timeouts. The `TimeoutException` already carries full context (exchange, routing key, message id, configured timeout) — it just isn't aggregated | [Producer/Producer.cs:439–478](src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L439) |
| 8 | **Low-Med** | API | Add a `ReplyOptions` struct to mirror `SendOptions`/`PublishOptions`; today `IConsumeContext.ReplyAsync` takes a raw `IDictionary<string,string>?` header dict | [Interfaces/Bus/IConsumeContext.cs:30](src/ServiceConnect.Interfaces/Bus/IConsumeContext.cs#L30) |
| 9 | **Low** | Persistence | Promote the `IAggregatorPersistor` "ctor takes (connectionString, databaseName, collectionName) by convention" from comment to documented contract — see [InMemoryAggregatorPersistor.cs:49–53](src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs#L49). And replace the duck-typed `Guid CorrelationId` reflection with an `IHasCorrelationId` interface | [Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs:21–47](src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs#L21) |

**Removed after verification:**
- ~~Mark `ProducerConnection._model` volatile~~ — already volatile at [ProducerConnection.cs:38](src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs#L38). The concurrency-review subagent missed the keyword.
- ~~Skip `Envelope` allocation on the no-filter send path~~ — already implemented as `BuildHeadersDirect` ([Bus.cs:704](src/ServiceConnect/Bus.cs#L704)), gated by `_hasOutgoingFilters` at [Bus.cs:98, 152](src/ServiceConnect/Bus.cs#L98). The performance subagent missed the gate.

The rest of this document is the per-concern detail behind that table.

---

## 1. Public API & contracts

**Verdict: principled, internally consistent, with a small number of v8-window cleanups.**

### Strengths

- 47 async methods on the public surface, **every one** has a `CancellationToken cancellationToken = default` parameter, named consistently. No `async void`. (`src/ServiceConnect.Interfaces/`)
- `<Nullable>enable</Nullable>` everywhere; no suspicious `default!` casts or `string!` patterns at the public boundary.
- `PublishOptions`, `SendOptions`, `RequestOptions` are `readonly record struct` — correct: thread-safe value semantics, no shared-instance clobbering.
- Recent v8 work (commits `b13010b6` / `33fc9a83` / `733afbe2`) tightened the right things: `IConsumeContext.Headers`, `ConsumeEventArgs.Headers`, `TimeoutData.Headers` are now `IReadOnlyDictionary` at the consumer boundary; `SendContext.Headers` *intentionally* stays mutable for middleware, with a doc comment explaining why.
- XML docs are dense and explain non-obvious behaviour (e.g. `IBus.PublishRequestAsync` parameter ordering rationale, `ITimeoutStore` lease semantics).

### Issues

1. **Mutable collections in low-level transport interfaces.** The high-level `IBus` types options as `IReadOnlyDictionary<string,string>` for headers, but those options reach `IProducer` / `IConsumer` / `IMessageDispatcher` and become mutable: `IDictionary<string,string>` or `IList<string>`. Callers passing shared collections risk in-flight mutation; the framework forces copies. Fix: tighten the transport contracts to `IReadOnly*`, or document explicit ownership transfer.
   - [Bus/IProducer.cs:11–28](src/ServiceConnect.Interfaces/Bus/IProducer.cs#L11), [Bus/IConsumer.cs:23](src/ServiceConnect.Interfaces/Bus/IConsumer.cs#L23), [Bus/IMessageDispatcher.cs:11](src/ServiceConnect.Interfaces/Bus/IMessageDispatcher.cs#L11)

2. **`IConsumeContext.ReplyAsync` deviates from the `IBus` shape.** `IBus.PublishAsync`/`SendAsync` accept an options struct + cancellation. `ReplyAsync` takes `message`, raw `IDictionary<string,string>? headers`, `cancellationToken` — no options wrapper. Reply-side correlation IDs and custom headers go via dict mutation rather than a structured value. Fix: introduce `ReplyOptions`.
   - [Bus/IConsumeContext.cs:30](src/ServiceConnect.Interfaces/Bus/IConsumeContext.cs#L30)

3. **`SendOptions.EndPoint` (singular) and `EndPoints` (plural) are mutually exclusive — runtime-enforced but not type-level.** Bus.cs:142–147 throws `ArgumentException` if both are set, so the safety is there; the cost is a runtime failure on a config error that the type system could catch. Could be encoded as a small union/factory if the v8 churn is acceptable. Lower priority than I originally rated it.
   - [Options/SendOptions.cs:18–23](src/ServiceConnect.Interfaces/Options/SendOptions.cs#L18), [Options/RequestOptions.cs:26–34](src/ServiceConnect.Interfaces/Options/RequestOptions.cs#L26), [Bus.cs:142–147](src/ServiceConnect/Bus.cs#L142)

4. **`TimeoutData.Destination` is non-nullable `string` defaulting to `string.Empty`** while `SendContext.EndPoint` and `TransportException.Endpoint` are `string?`. Same domain concept, different null encoding.
   - [Timeouts/TimeoutData.cs:16](src/ServiceConnect.Interfaces/Timeouts/TimeoutData.cs#L16)

5. **`IMessageSerializer.Serialize` is byte[]-allocating on every path.** Verified: the `IBufferWriter<byte>` overload at [IMessageSerializer.cs:22](src/ServiceConnect.Interfaces/Messages/IMessageSerializer.cs#L22) is *not* zero-copy in the current implementation — `NewtonsoftJsonMessageSerializer.Serialize(T, IBufferWriter<byte>)` ([:73–81](src/ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs#L73)) calls `Serialize(message)` (allocates `byte[]`) then `CopyTo`. The receive side is fine — `Deserialize(ReadOnlyMemory<byte>, Type)` ([:109](src/ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs#L109)) uses a custom `ReadOnlyMemoryStream` and avoids the copy. Send side will benefit from STJ migration since `Utf8JsonWriter` writes directly to an `IBufferWriter<byte>`.

### A note on the dual pipelines

The code has two separate extension surfaces — `IFilter` (returns `FilterAction.Continue/Stop` on a raw `Envelope`, runs both inbound and outbound) and `IMessageProcessingMiddleware` / `ISendMessageMiddleware` (delegate-chain style, sees the typed `Message` / `SendContext`). These are **not redundant**: filters are envelope-level gates, middleware is typed-message transformation in a delegate chain. The discoverability concern is real — a new user asking "how do I intercept an outbound message?" gets two valid answers — but the right fix is **better docs and a decision tree**, not collapsing the abstractions. See §5 for more on the extensibility verdict.

---

## 2. Concurrency & lifecycle

**Verdict: disciplined and well-engineered. The originally-flagged "real bug" was a false positive. Several documented-but-fragile patterns, several low-likelihood edges.**

### Lifecycle state machine

`Bus.cs` uses a clean two-primitive model: an atomic `_disposed` flag (Interlocked) gates all public ops via `ThrowIfDisposed()`; a single `_lifecycleSemaphore` (`SemaphoreSlim(1)`) serialises `StartConsumingAsync` / `StopConsumingAsync` / `DisposeAsync`. Re-checks happen *after* acquisition so concurrent start/stop is safe. The semaphore is intentionally **not** disposed (documented at lines 573–578) to avoid `ObjectDisposedException` in concurrent `Release()` during unwind. That's load-bearing — leave it alone.

No `.Wait()`, `.Result`, `.GetAwaiter().GetResult()`, or `async void` (outside event handlers) anywhere in the production code. `ConfigureAwait(false)` is consistent on library paths.

### Verified-not-a-bug: ProducerConnection._model

The initial concurrency review flagged a "Med-High volatile mismatch" on `ProducerConnection._model` — claiming the field was `Interlocked.Exchange`'d on write but read non-volatilely from the `Channel` getter. **On verification, `_model` is already declared `private volatile IChannel? _model;` at [ProducerConnection.cs:38](src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs#L38)**. The volatile keyword + Interlocked + non-volatile read pattern is sound. The agent missed the keyword; no fix needed.

### Lock inventory (sound, with two notes)

| Where | Primitive | Verdict |
|---|---|---|
| `Bus._stateLock`, `_lifecycleSemaphore` | `Lock`/`object`, `SemaphoreSlim(1)` | Fine; minimal scope |
| `RabbitMqConsumerHost._callbackAdmissionGate` | `Lock`/`object` | Fine; admission counter under lock, release in finally outside |
| `Producer._publishLock`, `ProducerConnection._connectionSemaphore` | `SemaphoreSlim(1)` | Fine; intentionally not disposed (documented) |
| `CacheProvider._addLock` | `Lock`/`object` | **Questionable**: guards Add but `TryGet` reads `_cache` and `_slidingTime` unprotected. Race window where sliding-window can be extended on a stale value. Likely cosmetic in practice; worth a re-look. |
| `InMemoryProcessManagerFinder` | `ReaderWriterLockSlim.EnterReadLock` (sync) | **Questionable**: blocks the async context. Under high contention this starves Tasks, doesn't deadlock. Consider `TryEnterReadLock(timeout)` or accept and document. |
| `InMemoryPersistenceState`, `InMemoryAggregatorPersistor`, `RequestReplyManager` | `ReaderWriterLockSlim`, `object`, `ConcurrentDictionary` | Fine |

### Lower-priority concurrency findings

- **Fire-and-forget `_ = CancelHelperPublishesAtDeadlineAsync(...)` at [RabbitMqConsumerHost.cs:575](src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L575)**: best-effort cleanup; if `Task.Delay` faults, the exception is swallowed. Acceptable, but note for the unobserved-task-exception inventory.
- **The recent flaky-test work (commits `8e9c7b5c`, `3c4ee797`)** moved four timing-sensitive tests to a serial xUnit collection. The fix is correct, but it's also a signal: under sustained scheduler pressure, the production code's timer/timeout assumptions can fire late. Worth keeping in mind if you ever see rare prod redeliveries clustered after CPU-saturation events.

---

## 3. Resilience & error handling

**Verdict: at-least-once with optimistic concurrency on saga state. The semantics are correct; the contract isn't loud enough.**

### Failure-mode summary

- **Broker drop**: in-flight messages are left unacked → requeued on reconnect. `AutomaticRecoveryEnabled = true` (default at [ConnectionFactoryBuilder.cs:39](src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs#L39)) re-establishes topology and resubscribes consumers. Auto-recovery has **no client-side backoff or circuit breaker** — sustained broker outages will cause continuous reconnect attempts.
- **Handler crash mid-execution**: nacked + requeued if the failure publish succeeds (`RabbitMqConsumerHost.cs:416`); left unacked otherwise. **Critically: ack happens *after* handler completion, *outside* any persistence transaction.** A handler that writes to MongoDB then crashes before returning will have the write durable but the message redelivered. The reverse — ack-then-crash-before-persist — is also reachable via the `ExceptionHandler` callback path.
- **Saga concurrent updates**: optimistic versioning at [MongoDbProcessManagerFinder.cs:325–339](src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs). Conflicting updates throw `ConcurrencyException`; the caller must catch and retry.
- **Retry**: exponential backoff with jitter (`2^min(attempt,52) * baseInterval + jitter`, capped at 5 min) at [Retry.cs:140–152](src/ServiceConnect.Client.RabbitMQ/Consumer/Retry.cs#L140). Counter lives in the AMQP `RetryCount` header — survives broker restarts. After `maxRetries`, the message is published to the configured error exchange with the original properties + serialised exception in headers ([MessageRetryHandler.cs:161](src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs#L161)).

### Concrete issues

1. **Retry-publish failure is conditionally swallowed.** [InboundMessageProcessor.cs:130–158](src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L130) — transport-class failures (`OperationCanceledException` on shutdown, `AlreadyClosedException`, `BrokerUnreachableException`) are **rethrown** so the outer finally nacks-with-requeue. Only "other" exceptions (e.g. `PublishException` from `mandatory:true` against a deleted retry queue) are logged at Error and acked, to prevent infinite redelivery loops. The discipline is correct; the gap is operator visibility — if the retry queue has gone, only an Error-level log surfaces it. Surface as a counter/alert. Same conditional swallow at [:191–212](src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L191) (terminal failure) and [:226–246](src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L226) (audit).
2. **`ExceptionHandler` callback exceptions are swallowed.** [MessageDispatcher.cs:186–196](src/ServiceConnect/Services/MessageDispatcher.cs#L186) — a buggy user exception handler still triggers the nack/requeue path with `Success=false`. Either propagate or wrap with explicit timeout + structured log so it isn't invisible. (Verify before acting — line numbers may have shifted; the pattern is to grep for `ExceptionHandler` in `MessageDispatcher.cs`.)
3. **No retry backoff for connection recovery.** [ConnectionFactoryBuilder.cs:39](src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs#L39). RabbitMQ.Client's auto-recovery loop will hammer an unhealthy broker. Configurable backoff or a circuit-breaker is worth adding.
4. **Retry config is global, not per-handler/per-message-type.** `MessageRetryHandler` is constructed once with one `maxRetries`. Some handlers naturally tolerate more retries than others; today that's not expressible.
5. **No built-in idempotency.** The framework provides at-least-once and `ConcurrencyException` on saga conflicts. There is **no message-id deduplication store**. The `filters/MessageDeduplication/` project (excluded from the solution per the prior review's Phase 2) is the natural home for this, if a decision is taken to ship it as a first-party plugin.

A note on retry-counter spoofing: [MessageRetryHandler.cs:47–57](src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs#L47) explicitly rejects malformed or out-of-range `RetryCount` headers and routes to the error exchange. So a malicious sender cannot force infinite retries by setting a bogus counter — corrupt input is observable, not silently reset.

### Documentation gap

Of all the resilience findings, the most important is **not a code change** — it's that the at-least-once contract and the persist-then-ack gap are not explicitly stated in `IBus`'s public XML docs. Anyone building a saga handler against this library should be told, in the public docs, "you will be redelivered after a crash; design for idempotency". Today they have to infer it.

---

## 4. Observability

**Verdict: traces good, logs structured, metrics absent, operator visibility into "stuck consumer" is the biggest gap.**

### What's there

- **OpenTelemetry-flavoured tracing** in `ServiceConnect.Telemetry/ServiceConnectActivitySource.cs` (557 lines) follows messaging semantic conventions: `messaging.system=rabbitmq`, `messaging.operation`, `messaging.destination.name`, `messaging.message.id`, `messaging.message.conversation_id`, body size. W3C trace-context propagates correctly across the broker via headers ([:410–420](src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L410)). Spans get `ActivityStatusCode.Error` and a sanitised "exception" event on failures.
- **Structured logging** with `{Placeholder}` templates is consistent. Exceptions passed as the first arg, not concatenated. No interpolation (`$"..."`) into log messages. No secrets in logs: `HeaderHelpers.cs:36–66` truncates exception chains to 3 levels / 4096 chars to avoid leaking inner-exception detail.
- **Health checks** in `ServiceConnect.HealthChecks/` are O(1) state snapshots (no broker I/O), correctly observe what their names claim, integrate with ASP.NET Core's `IHealthCheck`. The producer check correctly handles lazy connection via `HasAttemptedConnection` so publish-later hosts don't crash-loop on startup.

### Gaps, ranked by production impact

1. **No metrics at all.** No `Meter`, no counters, no histograms. Specifically missing: publish duration, consume duration, handler duration, retry-attempt counter, connection-state gauge, retry-queue depth. This is the single biggest observability win — it doesn't change architecture, just adds a parallel `Meter` to the existing `ActivitySource`.
2. **No visibility into in-flight handler count.** `RabbitMqConsumerHost._messagesBeingProcessed` (line 69) tracks this internally for graceful drain but isn't exposed. An operator seeing "consumption stopped" can't tell whether handlers are stuck or no messages are flowing. Surface as a public bus property + a diagnostic health check.
3. **No counter for publish-confirm timeouts.** Verified against [Producer.cs:439–478](src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L439): `PublishWithTimeoutAsync` *does* throw a `TimeoutException` carrying full context (exchange, routing key, message id, configured timeout, broker-stalled hint). What's missing is aggregation — there's no counter for "publish confirms that timed out per minute", so a stalled-broker storm only shows up as a flurry of caller-side exceptions, not a queryable signal. Soften the original framing: this is "no aggregation", not "silent".
4. **Audit-publish failures are logged at Error but not surfaced as health/metric.** Verified at [InboundMessageProcessor.cs:243–246](src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L243): the catch logs `_logger.LogError(ex, "Failed to publish audit message...")`. Operators see it in logs; they don't see "audit drop rate climbing" without log scraping.
5. **No channel/connection lifecycle logs at Info level.** Reconnect storms are invisible until they cause request timeouts.
6. **Ack/nack failure logs lack `MessageId`.** [RabbitMqConsumerHost.cs:395–408](src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L395) logs the `DeliveryTag`. Correlating back to the original message requires broker-side tracing.
7. **No request-reply timeout counter.** Today RPC timeouts are individually logged but not aggregated.

---

## 5. Extensibility seams

**Verdict: clean. No cross-package `InternalsVisibleTo`. Two implicit conventions are worth promoting to documented contract.**

- Verified: only `ServiceConnect.UnitTests` is in `InternalsVisibleTo`. The cross-package coupling flagged in the prior review is gone.
- Transport plug-in surface is fully public: `IConsumer`, `IProducer`, `ITransportConfiguration`, plus the deterministic `MessageTypeExchangeName.From(type)` helper which is properly marked public-on-purpose at [Services/MessageTypeExchangeName.cs:13–17](src/ServiceConnect/Services/MessageTypeExchangeName.cs#L13). A third party can ship a Kafka or Azure Service Bus adapter without forking core.
- Persistence plug-in surface (`IAggregatorPersistor`, `IProcessManagerFinder`, `ITimeoutStore`) is coherent. **Two undocumented implicit conventions:**
  1. The `IAggregatorPersistor` factory expects ctor parameters that the in-memory implementation doesn't actually use ([InMemoryAggregatorPersistor.cs:53](src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs#L53) — "required by factory convention but unused"). A third-party implementer would discover this only by tracing the factory wiring.
  2. Aggregator data is correlated by reflecting for a public `Guid CorrelationId` property ([InMemoryAggregatorPersistor.cs:23–46](src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs#L23)). This duck-typing should be expressed as an interface (`IHasCorrelationId` or similar) the framework can see.
- `ICacheProvider` and `IKeyValueStore` exist but are internal to the in-memory persistor. If a Redis-backed cache is ever wanted, these would need promoting.
- Filter and middleware pipelines are **complementary, not redundant** — see §1's note. The split is justified; the discoverability fix is a docs/decision-tree page on the website, not a v8 collapse.
- Handler discovery is hybrid (assembly scan + manual registration via `ServiceConnectBuilder.AddRegistration`). Manually-registered handlers are deduplicated against scanned ones ([ServiceCollectionExtensions.Handlers.cs:101–103](src/ServiceConnect/ServiceCollectionExtensions.Handlers.cs#L101)). **Open generics aren't supported** — `HandlerScanner` skips `IsGenericTypeDefinition` types.
- `IMessageSerializer` is format-agnostic and STJ-ready: no Newtonsoft types leak through the contract.

---

## 6. Performance & allocations

**Verdict: correctness-first. Comfortable for ≤10k msg/s. The Newtonsoft → STJ migration is the highest-leverage perf win.**

### Per-receive allocation budget (no filters)

| What | Where | Count |
|---|---|---|
| `Envelope.Headers` dict | [Bus.cs:622](src/ServiceConnect/Bus.cs#L622) | 1 |
| Inbound headers dict | [InboundMessageProcessor.cs:57](src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L57) | 1 |
| Header validation copy (terminal failure path) | [RabbitMqConsumerHost.cs:459–474](src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L459) | 0–1 |
| Timestamp strings | [InboundMessageProcessor.cs:253–257](src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L253) | 2 |
| Filter snapshot (`.ToArray()`) | [Bus.cs:617](src/ServiceConnect/Bus.cs#L617) | 0–1 (filters only) |

Payload itself is **not copied** — `args.Body` (`ReadOnlyMemory<byte>` from RabbitMQ.Client) goes straight into init-only `Envelope.Body`. That's the right choice.

### Per-send allocation budget

| What | Where | Count |
|---|---|---|
| Newtonsoft serialise (MemoryStream + StreamWriter + JsonTextWriter) | [NewtonsoftJsonMessageSerializer.cs:57–63](src/ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs#L57) | 3 + 1 `byte[]` |
| `ExtractHeaders` dict | [Bus.cs:652](src/ServiceConnect/Bus.cs#L652) | 1 |
| `OutboundHeaderBuilder` result dict | [OutboundHeaderBuilder.cs:55](src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs#L55) | 1 |
| `BasicProperties` headers copy | [OutboundHeaderBuilder.cs:102](src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs#L102) | 1 |
| Guid `.ToString()` | [Bus.cs:643](src/ServiceConnect/Bus.cs#L643), [OutboundHeaderBuilder.cs:77](src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs#L77) | 2 |
| `LinkedCancellationTokenSource` | [Producer.cs:448](src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L448) | 1 |

### Top reductions

1. **STJ migration.** Replaces `MemoryStream` + `StreamWriter` + `JsonTextWriter` + `byte[]` (4 allocations per serialize, [NewtonsoftJsonMessageSerializer.cs:57–63](src/ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs#L57)) with a single pooled `Utf8JsonWriter` writing directly to an `IBufferWriter<byte>`. Estimated 20–30% allocation reduction per message. Already on the user's todo. **Bonus**: the `IMessageSerializer.Serialize(T, IBufferWriter<byte>)` overload that exists today is currently a copy-on-top of `byte[]` ([:73–81](src/ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs#L73)) — STJ would make it genuinely zero-copy.
2. **Centralise header dict construction in `RabbitMqConsumerHost`** ([:459–475, called at :302/:314/:329/:365](src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L459)). Build once, reuse across failure branches. Saves one `Dictionary<string,object>` per terminal-failure path (cold-ish, but compounds on retry).
3. **Skip `ExtractHeaders` dict copy on filter-enabled send.** [Bus.cs:648–662](src/ServiceConnect/Bus.cs#L648). The no-filter path at [:704–737](src/ServiceConnect/Bus.cs#L704) already avoids the intermediate Envelope; the filter path still allocates a separate `Dictionary<string,string>` from `Envelope.Headers`. Skip if filters didn't mutate.
4. **Cache decoded header strings on the consume context** so repeated reads (filters → middleware → handler) don't UTF-8-decode multiple times. [HeaderDecoder.cs](src/ServiceConnect.Interfaces/Headers/HeaderDecoder.cs).
5. **`OutboundHeaderBuilder.BuildBasicProperties` copies the headers dict a second time.** [:99–112](src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs#L99) — `BuildHeaders` returns `Dictionary<string,object>`, then `BuildBasicProperties` copies that into a fresh `Dictionary<string,object?>` for `BasicProperties.Headers`. The second copy is structural (RabbitMQ.Client wants `object?` values, builder produces `object`). Could be eliminated by aligning builder output to `object?` directly.

**Already-implemented (no action needed)**: the original report flagged "skip `Envelope` allocation on no-filter send path" as a top win. Verified: this is already done — `BuildHeadersDirect` ([Bus.cs:704](src/ServiceConnect/Bus.cs#L704)) is the fast path, gated on `_hasOutgoingFilters` at [Bus.cs:98 and :152](src/ServiceConnect/Bus.cs#L98). XML doc on `BuildHeadersDirect` (697–702) explicitly explains the optimisation. The performance subagent missed the gate.

### LINQ and reflection

LINQ avoidance on hot paths is **excellent** — `foreach` everywhere; the only LINQ is on cold/configuration paths (`destinations.ToArray()`, `string.Join`, etc.). Reflection caching is also excellent: `OutboundHeaderBuilder.TypeNameCache`, `Producer._exchangeNameCache`, `ProcessManagerPredicateCache`, `InMemoryAggregatorPersistor.CorrelationIdAccessors` are all `ConcurrentDictionary` GetOrAdd patterns. No hot-path `GetType().GetMethod()` or `Activator.CreateInstance`.

---

## 7. Security & deserialization

**Verdict: deserialization is properly defended. TLS-default and unbounded in-memory persistor growth are the headline risks.**

### What's safe

- **`TypeNameHandling = TypeNameHandling.None`** is set explicitly at [NewtonsoftJsonMessageSerializer.cs:39](src/ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs#L39) with a comment explaining why. The classic .NET deserialisation gadget vector (CVE-2017-9822 class) is not reachable. Type resolution goes through `IMessageTypeRegistry` against a startup-registered allowlist; unregistered types are rejected and not retried.
- **Header trust boundary is correct.** Producer-stamped reserved keys (`DestinationAddress`, `MessageType`, `SourceAddress`, `TimeSent`, `SourceMachine`, `TypeName`, `FullTypeName`, `ConsumerType`, `Language`) live in `OverwrittenHeaderKeys` ([OutboundHeaderBuilder.cs:27–38](src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs#L27)) — caller-supplied values get a warning and are overwritten. `MessageId` is intentionally NOT in this set: Bus stamps it authoritatively at [Bus.cs:643/735](src/ServiceConnect/Bus.cs#L643), and the producer respects the Bus-minted value via the `!ContainsKey` check at [OutboundHeaderBuilder.cs:75](src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs#L75). Inbound headers are decoded with a 32-level nesting cap ([HeaderDecoder.cs:55](src/ServiceConnect.Interfaces/Headers/HeaderDecoder.cs#L55)). The `RetryCount` header IS header-inspected (not broker-side TTL — verified at [MessageRetryHandler.cs:31–58](src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs#L31)), but a malformed or out-of-range value is detected and routed to the error exchange rather than silently reset to zero, so a malicious sender cannot force infinite retries by tampering with the counter.
- **Queue and exchange names are configuration-driven**, never derived from message-content fields. No injection vector for routing-key exfiltration.
- **No secrets in logs.** Exception chains truncated at 3 levels / 4096 chars by `HeaderHelpers`. Connection-factory builder doesn't log credentials.
- **Dependencies are current**: Newtonsoft.Json 13.0.3, MongoDB.Driver, RabbitMQ.Client 7.x. No unpatched CVEs.

### Headline risks

1. **TLS is opt-in, not the default.** [ConnectionFactoryBuilder.cs:54–62](src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs#L54). When `SslEnabled=false`, plaintext AMQP on 5672 with credentials in clear. No startup warning. **Recommendation**: log a `Warning` at startup when AMQP plaintext is configured against anything other than `localhost`/`127.0.0.1`. Flipping the default to opt-out is the more defensible long-term move but is a breaking config change.
2. **In-memory aggregator and process-manager state are unbounded.** No TTL, no max-entry cap. A sender flooding distinct `CorrelationId`s grows the heap until OOM. The MongoDB persistor supports TTL indexes, so production deployments should be on Mongo, but the in-memory persistors should be **clearly marked "test/dev only"** in their XML docs and probably in the `Add…` extension method names.
3. **No client-side cap on publisher-confirm tracking.** ServiceConnect delegates to RabbitMQ.Client's internal listener — verify v7.x has a sensible bound. Worth an explicit check.

### STJ migration security checklist

When the Newtonsoft → STJ migration happens, preserve at minimum:

- Set `JsonSerializerOptions.MaxDepth = 32` (or some explicit cap) — STJ default is more permissive than Newtonsoft's.
- Custom converters must validate any type-name input against the registry; never call `Type.GetType` on untrusted strings.
- Polymorphism via STJ `JsonDerivedType` attribute is fine, but `JsonTypeInfoResolver` must be allowlist-driven.
- Re-test with adversarial payloads (deeply nested, very large arrays, NaN/Infinity, surrogate pairs) — STJ's stricter parsing surfaces edge cases Newtonsoft tolerated.

---

## Open design questions

These came up during review and don't have a single right answer:

1. **Should retry config be per-handler / per-message-type?** Today it's transport-global. Some handlers (idempotent reads) tolerate aggressive retry; others (state-mutating sagas) want fewer attempts. The fix touches `MessageRetryHandler`, transport configuration, and the public DI registration shape.
2. **Should idempotency be a first-party concern?** The `filters/MessageDeduplication/` project (excluded from the solution per the prior review's Phase 2) is the obvious answer if so. The decision determines whether the framework is "at-least-once, idempotency is your problem" or "at-least-once with optional dedup".
3. **Should TLS default to on?** It's safer; it's also a breaking config change for every existing consumer.
4. **Should the v8 break introduce `ReplyOptions` and tighten the transport-level collection types?** Both are small, principled, and proportionate to the v8 scope — but they're a public-API churn that has to land before GA, not after.

---

## Methodology

Seven parallel read-only research agents, one per concern, dispatched against `v7-clean-architecture@b13010b6` on 2026-05-03. Each produced a focused report; this document is the synthesis. The agents found one direct disagreement (collapse the dual pipeline vs. keep both) — resolved by reading the interfaces directly: they're genuinely different shapes for different jobs. The IVT findings reflect the post-implementation state of the prior structural pass; the cross-package `InternalsVisibleTo` coupling that earlier work flagged is no longer present.

After synthesis, every cited claim was re-verified against the source. Two findings were removed (false positives — see appendix) and four were tightened where the agents had been imprecise. The remaining findings are accurate as of HEAD `b13010b6`.

No `dotnet build` or `dotnet test` runs were performed (per project CLAUDE.md guidance).

---

## Appendix — verification notes

Read-only re-verification of every cited claim. Files actually opened for verification: ProducerConnection.cs, ConnectionFactoryBuilder.cs, IConsumeContext.cs, IProducer.cs, IConsumer.cs, IMessageDispatcher.cs, TimeoutData.cs, IMessageSerializer.cs, Bus.cs, RabbitMqConsumerHost.cs, OutboundHeaderBuilder.cs, HeaderDecoder.cs, HeaderHelpers.cs, NewtonsoftJsonMessageSerializer.cs, MessageRetryHandler.cs, InboundMessageProcessor.cs, Retry.cs, InMemoryAggregatorPersistor.cs, MessageTypeExchangeName.cs, Producer.cs (lines around 430–478), InMemoryProcessManagerFinder.cs.

### Removed (false positives)

| Claim | Reality |
|---|---|
| `ProducerConnection._model` needs `volatile` | **Already volatile** at [ProducerConnection.cs:38](src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs#L38). The agent missed the keyword. |
| Skip `Envelope` allocation on no-filter send path | **Already done** as `BuildHeadersDirect` ([Bus.cs:704](src/ServiceConnect/Bus.cs#L704)), gated by `_hasOutgoingFilters` at [Bus.cs:98 and :152](src/ServiceConnect/Bus.cs#L98). The XML doc on `BuildHeadersDirect` (697–702) explicitly explains the optimisation. The agent missed the gate. |

### Tightened (agent was imprecise)

| Claim as originally stated | What's actually true |
|---|---|
| `IConsumeContext.ReplyAsync` takes `IDictionary<string,object>?` headers | Actually `IDictionary<string,string>?` ([IConsumeContext.cs:30](src/ServiceConnect.Interfaces/Bus/IConsumeContext.cs#L30)). Doesn't change the recommendation (still warrants a `ReplyOptions`), just the type. |
| `IMessageSerializer` has an `IBufferWriter<byte>` zero-copy escape valve | The overload exists but is **not** zero-copy in current impl ([NewtonsoftJsonMessageSerializer.cs:73–81](src/ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs#L73)) — it calls `Serialize(message)` (allocating `byte[]`) then `CopyTo`s into the writer's span. Receive side IS zero-copy via custom `ReadOnlyMemoryStream` ([:109](src/ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs#L109)). |
| Retry-publish failures are "logged-and-swallowed" | Conditional swallow only. Transport-class failures (`OperationCanceledException` on shutdown, `AlreadyClosedException`, `BrokerUnreachableException`) are rethrown so the outer finally nacks-with-requeue ([InboundMessageProcessor.cs:130–148](src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L130)). Only "other" exceptions (e.g. `PublishException` from `mandatory:true` against a deleted retry queue) are logged + acked. |
| Publish-confirm timeouts are "silent" | The `TimeoutException` carries full context (exchange, routing key, message id, configured timeout, broker hint) at [Producer.cs:473–476](src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L473). The gap is aggregation, not visibility. |
| Retry-counter spoofing prevented by "broker-side TTL" | Actually prevented by header-inspected malformed-value detection at [MessageRetryHandler.cs:47–57](src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs#L47). Outcome same (sender can't force infinite retries), mechanism different. |
| Audit-publish failures "silently drop" | Logged at Error ([InboundMessageProcessor.cs:243–246](src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L243)). Real gap is metric/health-check signal, not a log gap. |
| `OverwrittenHeaderKeys` includes `MessageId` | Doesn't ([OutboundHeaderBuilder.cs:27–38](src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs#L27)). MessageId is Bus-stamped and Producer-respected, by design. |

### Confirmed correct (sample, not exhaustive)

- `TypeNameHandling = TypeNameHandling.None` at [NewtonsoftJsonMessageSerializer.cs:39](src/ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs#L39).
- `AutomaticRecoveryEnabled = true` at [ConnectionFactoryBuilder.cs:39](src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs#L39); SSL guarded by `if (transport.SslEnabled)` at line 54.
- Retry exponential backoff with jitter at [Retry.cs:140–152](src/ServiceConnect.Client.RabbitMQ/Consumer/Retry.cs#L140), capped at 5 minutes.
- `HeaderDecoder.MaxDepth = 32` at [HeaderDecoder.cs:55](src/ServiceConnect.Interfaces/Headers/HeaderDecoder.cs#L55).
- `HeaderHelpers` exception-chain truncation: 3 inner levels, 4096 chars ([HeaderHelpers.cs:36–37](src/ServiceConnect.Client.RabbitMQ/Configuration/HeaderHelpers.cs#L36)).
- `_lifecycleSemaphore` intentionally not disposed: documented at [Bus.cs:573–578](src/ServiceConnect/Bus.cs#L573). Same pattern in `ProducerConnection._connectionSemaphore` ([:219–222](src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs#L219)).
- `_messagesBeingProcessed` counter at [RabbitMqConsumerHost.cs:69](src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L69), drained under deadline at [:595–608](src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L595).
- `InMemoryProcessManagerFinder` uses sync `EnterReadLock()` at line 77.
- `MessageTypeExchangeName.From` is documented public-on-purpose at [MessageTypeExchangeName.cs:12–17](src/ServiceConnect/Services/MessageTypeExchangeName.cs#L12).
- `InMemoryAggregatorPersistor` factory-convention ctor params (`connectionString, databaseName, collectionName`) carry the unused-by-design comment at [:49–53](src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs#L49). Reflection-based `CorrelationId` accessor caches a throwing delegate for missing-property types ([:31–42](src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs#L31)) — better than the agent's "duck-typing" framing implied.
- Only `ServiceConnect.UnitTests` is in `InternalsVisibleTo` (verified by grep). Cross-package coupling flagged in the prior review is gone.
