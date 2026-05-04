# Architecture review — fix plan

Roadmap for working through findings from [architecture-review-deep.md](architecture-review-deep.md). The deep review produced 9 ranked top actions and ~20 secondary findings across 7 concerns; that's too much for one design doc, so the work is decomposed into seven independent groups below. Each group will be brainstormed → spec'd → planned → implemented as its own cycle.

**Status legend:** `pending` (not started) · `brainstorming` (in active design) · `spec'd` (design doc written, awaiting plan) · `planned` (implementation plan written) · `in-progress` (code in flight) · `done` (shipped).

---

## Group A — v8 public-API tightening · *Phase A.2 done; A.3 pending*

Breaking interface changes. **Ship blocker for v8 GA** because once v8 ships these become harder to change.

| Item | Where | Notes |
|---|---|---|
| Tighten transport-level collection types to `IReadOnly*` | [IProducer.cs:11–28](src/ServiceConnect.Interfaces/Bus/IProducer.cs#L11), [IConsumer.cs:23](src/ServiceConnect.Interfaces/Bus/IConsumer.cs#L23), [IMessageDispatcher.cs:11](src/ServiceConnect.Interfaces/Bus/IMessageDispatcher.cs#L11) | High-level `IBus` already does this; propagate down |
| Add `ReplyOptions` struct | [IConsumeContext.cs:30](src/ServiceConnect.Interfaces/Bus/IConsumeContext.cs#L30) | Mirror `SendOptions` / `PublishOptions` shape |
| `TimeoutData.Destination` → `string?` | [TimeoutData.cs:16](src/ServiceConnect.Interfaces/Timeouts/TimeoutData.cs#L16) | Align with `SendContext.EndPoint` / `TransportException.Endpoint` |
| `IMessageSerializer` true zero-copy on the send side | [IMessageSerializer.cs:14–22](src/ServiceConnect.Interfaces/Messages/IMessageSerializer.cs#L14), [NewtonsoftJsonMessageSerializer.cs:73–81](src/ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs#L73) | Today's `IBufferWriter<byte>` overload allocates `byte[]` then copies; STJ migration unlocks real zero-copy |
| `IAggregatorPersistor` factory convention | [InMemoryAggregatorPersistor.cs:49–53](src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs#L49) | Either formalise the unused-ctor-param contract, or refactor so it isn't needed |
| `IHasCorrelationId` interface to replace duck-typed reflection | [InMemoryAggregatorPersistor.cs:21–47](src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs#L21) | Cleaner than reflecting for `Guid CorrelationId` |
| `SendOptions` union for `EndPoint`/`EndPoints` (maybe) | [SendOptions.cs:18–23](src/ServiceConnect.Interfaces/Options/SendOptions.cs#L18), [RequestOptions.cs:26–34](src/ServiceConnect.Interfaces/Options/RequestOptions.cs#L26) | Currently runtime-validated at [Bus.cs:142–147](src/ServiceConnect/Bus.cs#L142). Type-level encoding is nicer; risk is over-engineering |

**Cross-cutting question for this group:** deprecation strategy — clean break in v8 with no transitional overloads, or ship `[Obsolete]` shims alongside new shapes? Affects scope of every item.

---

## Group B — Metrics rollout · *pending*

Add a `Meter` and operator-grade counters/histograms. Additive, low risk, the highest-leverage observability win.

| Item | Source |
|---|---|
| Add `Meter` to `ServiceConnect.Telemetry`; emit `messaging.{publish,consume,handler}.duration` histograms | Section 4 of deep review |
| Retry-attempt counter | Section 4 |
| In-flight handler gauge — surface `RabbitMqConsumerHost._messagesBeingProcessed` as a public bus property + diagnostic health check | [RabbitMqConsumerHost.cs:69](src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L69) |
| Retry-publish-drop counter | [InboundMessageProcessor.cs:150–158](src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L150) |
| Publish-confirm-timeout counter | [Producer.cs:439–478](src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L439) — exception already carries context, just needs aggregation |
| Audit-drop counter | [InboundMessageProcessor.cs:243–246](src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L243) |
| Connection lifecycle logs at Info | RabbitMQ connection open/close/reconnect |
| Ack/nack failure logs enriched with `MessageId` | [RabbitMqConsumerHost.cs:395–408](src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L395) |

**Open decision:** strict OTel semantic-conventions naming, or ServiceConnect-namespaced metrics?

---

## Group C — Docs & contracts · *pending*

Make implicit guarantees explicit. Mostly XML doc + website work, naturally pairs with the API churn in Group A.

- At-least-once contract + persist-vs-ack gap stated in `IBus` XML docs and v8 release notes
- In-memory persistors marked test/dev-only in XML docs (and possibly extension-method names)
- Dual-pipeline decision tree on website (filter vs. middleware: when to use which)
- `IAggregatorPersistor` factory convention documented (resolves once Group A picks formalise-or-refactor)
- v8 breaking-change summary on the website
- Supported-runtimes section calling out TLS expectations (depends on Group D outcome)

---

## Group D — Security defaults · *pending*

Decide v8 policy on TLS and connection-secret hygiene.

- Startup `Warning` log when AMQP plaintext is configured against a non-localhost host
- Decide: should v8 flip `SslEnabled` default to `true`? (Breaking config change for every existing deployment.)
- Verify RabbitMQ.Client v7.x publisher-confirm tracking has a sensible bound

**Open decision:** the TLS-default flip is a hard call. Safer-by-default vs. "every existing deployment must explicitly opt-out" is the trade-off.

---

## Group E — Resilience features · *pending*

Genuine new behaviour, not docs/metrics. The idempotency call is the strategic one.

- Connection-recovery backoff / circuit breaker (currently no backoff between RabbitMQ.Client auto-recovery attempts)
- `ExceptionHandler` callback shouldn't silently swallow user-handler exceptions ([MessageDispatcher.cs:186–196](src/ServiceConnect/Services/MessageDispatcher.cs#L186))
- Per-handler / per-message-type retry config (today's `MessageRetryHandler` is constructed with one global `maxRetries`)
- **Idempotency story** — decide whether `filters/MessageDeduplication/` (excluded from solution) becomes a first-party plugin, or framework remains "at-least-once, idempotency is your problem". Strategic.

---

## Group F — Perf reductions · *pending*

Allocation-shaving and small inefficiencies. Internal-only changes; safe in any minor release.

- **STJ migration** (own large effort, already on user's todo list) — biggest win, ~20–30% allocation reduction per message
- Centralise `CopyInboundHeaders` in `RabbitMqConsumerHost` so terminal-failure branches reuse one dict instead of allocating per branch ([:459–475, called at :302/:314/:329/:365](src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L459))
- Skip `ExtractHeaders` dict copy on filter-enabled send if filters didn't mutate ([Bus.cs:648–662](src/ServiceConnect/Bus.cs#L648))
- Cache decoded header strings on consume context (today filter→middleware→handler can each UTF-8-decode the same header)
- Eliminate `OutboundHeaderBuilder` double-copy by aligning builder output to `Dictionary<string, object?>` ([:99–112](src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs#L99))

---

## Group G — Smaller extensibility · *pending*

Pure feature adds. Patch/minor safe.

- Open generic handler support in `HandlerScanner` (currently skips `IsGenericTypeDefinition` types)
- Promote `ICacheProvider`/`IKeyValueStore` to public if a non-in-memory cache backend is ever wanted

---

## Recommended order

1. **A first**, in parallel with **C** — public-surface breaks must land in v8 GA, and the docs naturally follow the same churn.
2. **D next** — TLS-default decision is config policy; resolve before v8 GA.
3. **B + F** — additive, low-risk, slot into any minor release.
4. **E last** — idempotency in particular is a strategic call worth treating as its own brainstorm.
5. **G** — opportunistic, drop in when adjacent.

## Cross-cutting decisions (still open)

- **Deprecation strategy for v8 API breaks** (Group A) — clean break or `[Obsolete]` shims? Affects every Group A item.
- **TLS default flip** (Group D) — breaking config change but safer-by-default.
- **Idempotency** (Group E) — first-party plugin or framework stays neutral?
- **Metrics naming** (Group B) — strict OTel conventions or namespaced?

These four cross-cuts will surface again as we brainstorm individual groups.
