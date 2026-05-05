- Code reviews
- Documentation review
- Remove co-authorored by claude in commits
- Rewrite history to remove passwords, references to Ruffer
- Tracing need to be refactored to follow modern .net practices?
- ServiceCollectionExtensions files should be ogranised into a subfolder

## Follow-ups surfaced during Group B (metrics rollout)

- **`Producer.SendAsync(Type)` fan-out aborts on first failure** (pre-existing, surfaced
  during Task 2 review). The `Bus.SendToManyAsync` XML doc (`IBus.cs:21-25`) promises
  "failures on one endpoint do not abort the others", but the lower-level
  `Producer.SendAsync(Type)` (which the per-message-type queue-mapping fan-out goes
  through) lets the first endpoint's exception escape the foreach. Either the contract
  is `Bus.SendToManyAsync`-only (fine — close the issue) or the lower-level fan-out
  should also continue on failure (then add try-per-endpoint + aggregate). Decide before
  v8 GA. See commit `6bb8deb7` review for the framing.

- **`MessageRetryHandler` retry-attempt metric uses `retryQueueName`, not the consumer's
  queue** (Task 3 deviation). The retry-attempt counter currently tags
  `messaging.destination.name = <retryQueueName>` (where the message is re-published),
  not the consumer's queue (where the retry semantically originates). For an operator
  asking "how often is queue X retrying?", this means filtering on the retry queue, not
  X. Consider threading `_queueConfiguration.QueueName` into `MessageRetryHandler` or
  using a different tag name (`messaging.serviceconnect.retry.target`?) to carry the
  retry-queue destination. See commit `72df5810`.

- **`RabbitMqConsumerHost.cs` complexity is climbing** (Task 3 implementer note).
  `EventAsync` and `ProcessAsync` both tripped MA0051 (200-line method limit) once the
  metric emit blocks were inlined; resolved by extracting `BuildInFlightTags`,
  `TryRejectOversizedHeaderAsync`, and `PublishAuditWithDropMetricAsync`. The file is
  now ~700 lines doing admission, header validation, dispatch, ack/nack, in-flight
  bookkeeping, and shutdown coordination. Worth a focused split pass — possibly into
  `RabbitMqConsumerAdmission`, `RabbitMqConsumerDispatch`, etc. Not urgent; flag for the
  next architecture review.

## Deferred from Group E (resilience features)

- **Per-handler / per-message-type retry config** (Group E item 3). Today's
  `MessageRetryHandler` is constructed with a single global `_maxRetries`. A real
  use case for per-type tuning (e.g. "messages from third-party API X should retry
  20 times because the API flakes; messages from our own DB should retry 3 times
  because flakes are real bugs") would need: a per-type override surface on the
  builder (probably `RetryConfiguration { MaxRetries: int, PerMessageType: Dict... }`),
  threading it through `MessageRetryHandler.HandleFailureAsync` to look up the
  effective max-retries for the current message type, and per-type tests. Real API
  design surface. Defer until a concrete user case surfaces — without one the
  override scheme would be designed in a vacuum.

- **Idempotency story** (Group E item 4). Strategic call: ship a first-party
  `MessageDeduplication` filter again (re-introducing what v7 removed), or stay
  neutral with the "build your own filter pair" stance Group C already documented
  in `idempotency.mdx`. The current stance (neutral) is a defensible product
  position — every dedup design has tradeoffs (per-process store doesn't survive
  restart; cross-process store needs a backing service the framework doesn't want
  to take a dependency on). Revisit only if a user with a concrete deployment
  shape asks for built-in support; the user-built pattern in `idempotency.mdx`
  covers the documented case today.

## Deferred from Group G (smaller extensibility)

- **Open-generic handler support in `HandlerScanner`** (Group G item 1). The roadmap
  framed this as "smaller extensibility, additive, low risk", but the reality is
  feature work spanning three subsystems: the scanner (today skips
  `IsGenericTypeDefinition` types at `HandlerScanner.cs:53`), the registration
  extensions (today registers concrete-type-to-concrete-message pairs; would need
  open-generic `services.AddTransient(typeof(IMessageHandler<>), typeof(GenericHandler<>))`
  style), and `MessageTypeRegistry` (today pre-registers every concrete message type at
  scan time; with open generics the closed message types are unknown until first arrival,
  forcing lazy registration). Worth its own first-class brainstorm with a concrete user
  use case driving the design — the roadmap didn't surface a customer asking for this,
  and the scope doesn't fit the "drop in alongside" framing.

- **Promote `ICacheProvider` / `IKeyValueStore` to public** (Group G item 2). Both
  interfaces are *already* `public` (`ICacheProvider.cs:6`, `IKeyValueStore.cs:6`). The
  roadmap's actual intent must be "move them from `ServiceConnect.Persistence.InMemory`
  to a more general assembly so a non-in-memory cache backend can implement them without
  depending on the in-memory package." But no non-in-memory cache backend exists or is
  on anyone's roadmap, and the move is a breaking namespace change for current consumers
  (test code that does `using ServiceConnect.Persistence.InMemory;` would break). Pure
  pre-emptive YAGNI. Defer until a real backend implementation surfaces.

## Deferred from Group F (perf reductions)

- **Centralise `CopyInboundHeaders`** (Group F item 1). The roadmap framed this
  as an allocation win ("terminal-failure branches reuse one dict instead of
  allocating per branch") but the host's failure branches are mutually
  exclusive — each one returns, so only one ever fires per delivery. Hoisting
  to a shared site doesn't reduce per-delivery allocations. The genuine
  duplication is between `RabbitMqConsumerHost.CopyInboundHeaders` and
  `InboundMessageProcessor.ProcessAsync`'s identical copy at line 57-69 — a
  DRY refactor worth doing, but not for perf. Pick up alongside the next
  RabbitMqConsumerHost.cs split pass (already noted above).

- **Skip `ExtractHeaders` dict copy when filters didn't mutate** (Group F item
  2). `Bus.cs` already has a no-filter fast-path (`BuildHeadersDirect`). The
  next-level win — detecting "filters present but didn't mutate" — needs
  either an instrumented mutation-tracking wrapper around `Envelope.Headers`
  or breaking the `IDictionary<string,object>` public contract. Detection
  cost likely outweighs the saved alloc on typical workloads. Revisit only
  if profiling surfaces ExtractHeaders as a hot allocator on filter-heavy
  deployments.