# ServiceConnect Docs Audit — 2026-05-18

Audit of every page under `website/src/content/docs/` (61 pages) and the root `README.md`. Read-only — produced from a sweep of the v7.0.0 codebase against the live docs. Each finding cites `file:line` so reviewers can jump straight to source.

## Summary

- **Pages reviewed:** 62 (61 docs + `README.md`)
- **Findings by severity:** critical 17 / major 34 / minor 26 / nit 2 (= 79 Pass-1 findings)
- **Gap analysis A:** 4 undocumented user-facing items (1 config option, 3 types)
- **Gap analysis B:** 5 example/docs gaps; 9/14 example projects have clean docs coverage

**Top 3 risks — fix these first**

1. **`reference/process-managers/aggregator.mdx` is heavily wrong.** `Timeout()` and `BatchSize()` are shown as `virtual` with defaults — they are `abstract`; `Timeout.InfiniteTimeSpan` and non-positive `BatchSize` are rejected at startup with `InvalidOperationException`. `IAggregatorSnapshot.ResolvedMessages` is shown as `IReadOnlyList<object>` — it is `IReadOnlyList<IHasCorrelationId>`. A reader copying this page produces non-compiling code and is told to use values that crash at startup. (4 critical findings on this one page.)
2. **`reference/telemetry/index.mdx` lists C# constants that don't exist.** `MessagingAttributes.ErrorType`, `MessagingAttributes.MessagingClientId`, and `MessagingAttributes.MessagingMessageBodySize` are all referenced in the constants table; none exist (actual name is `MessagingBodySize`; the other two have no public constant). Snippets copied from this page won't compile. The instrument count is also wrong (claims 10/6+4, actual 11/4+7).
3. **Pervasive "v8" framing across v7 code.** The codebase shipped at `Version=7.0.0` after a "revert v8 framing — these changes ship in v7" commit, but several pages still describe current v7 behaviour as a future "v8" change: `the-bus.mdx` ("v1 supported transport"), `ibusconfiguration.mdx` (`EnableRoutingSlipProcessing` "In v8…"), `error-handling.mdx` ("v8 signature change"), `imessagehandler.mdx` / `istreamhandler.mdx` (migration asides), and `releases.mdx` has an entire "v8 highlights" section plus a "Newtonsoft remains a dependency" claim that is now false. Reader confidence in version accuracy is undermined globally.

---

## Per-page findings

## learn/core-concepts + index

### website/src/content/docs/learn/getting-started.mdx

- **[minor]** Contracts project pulled in heavier package than necessary
  - **Claim in doc:** `dotnet add GettingStarted.Contracts package ServiceConnect` (line 82)
  - **Reality in code:** `Message` lives in `ServiceConnect.Interfaces` (a separate, lighter NuGet — `PackageId=ServiceConnect.Interfaces`); pulling `ServiceConnect` transitively brings MS.Extensions.DI/Hosting which a pure-contracts project doesn't need (src/ServiceConnect.Interfaces/Messages/Message.cs, src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj)
  - **Suggested fix:** Use `ServiceConnect.Interfaces` for the contracts project.

### website/src/content/docs/learn/core-concepts/the-bus.mdx

- **[major]** "v1 supported transport" — wrong version number
  - **Claim in doc:** "UseRabbitMQ is the supported transport in v1." (line 54)
  - **Reality in code:** `Version=7.0.0` (src/Directory.Build.props:26); all other docs say v7
  - **Suggested fix:** `v1` → `v7`.
- **[minor]** `SendRequestAsync` type parameter mismatch in IBus snippet
  - **Claim in doc:** `Task<TReply> SendRequestAsync<T, TReply>(T message, …)` (line 14)
  - **Reality in code:** `Task<TReply> SendRequestAsync<TRequest, TReply>(TRequest message, …)` (src/ServiceConnect.Interfaces/Bus/IBus.cs:86)
  - **Suggested fix:** Rename `T` → `TRequest`.
- **[minor]** `CreateStream<T>` parameter casing wrong
  - **Claim in doc:** `IMessageBusWriteStream CreateStream<T>(string endPoint)` (line 17)
  - **Reality in code:** `string endpoint` (src/ServiceConnect.Interfaces/Bus/IBus.cs:169)
  - **Suggested fix:** `endPoint` → `endpoint`.
- **[minor]** Request/reply correlation mechanism mis-described
  - **Claim in doc:** "correlates the reply using the correlation id and a `ResponseMessageId` header" (line 115)
  - **Reality in code:** Correlation uses only the `ResponseMessageId` header (= outbound `RequestMessageId`); application-level `CorrelationId` is not a lookup key (src/ServiceConnect/Services/RequestReplyManager.cs:517)
  - **Suggested fix:** Drop the "correlation id" half; describe `ResponseMessageId` ↔ `RequestMessageId` only.

### website/src/content/docs/learn/core-concepts/messages.mdx

- **[critical]** Default serialiser name is wrong — `NewtonsoftJsonMessageSerializer` does not exist
  - **Claim in doc:** "The default `IMessageSerializer` is `NewtonsoftJsonMessageSerializer`." (line 65)
  - **Reality in code:** `TryAddSingleton<IMessageSerializer, SystemTextJsonMessageSerializer>()` (src/ServiceConnect/DependencyInjection/ServiceCollectionExtensions.cs:102, src/ServiceConnect/Services/SystemTextJsonMessageSerializer.cs:16); no `Newtonsoft.Json` dependency in `ServiceConnect.csproj`
  - **Suggested fix:** Replace with "`SystemTextJsonMessageSerializer` (backed by `System.Text.Json`)."

### website/src/content/docs/learn/core-concepts/handlers.mdx

- **[minor]** Singleton-handler check fires at `AddServiceConnect` call time, not "at host startup"
  - **Claim in doc:** `// This will throw at host startup:` (line 135)
  - **Reality in code:** The check runs inside `RegisterHandlerType` during the DI configuration phase, before the host is built (src/ServiceConnect/DependencyInjection/ServiceCollectionExtensions.Handlers.cs:143–151)
  - **Suggested fix:** `// This will throw during AddServiceConnect:`.
- **[minor]** "Handlers are always transient" over-states the enforcement
  - **Claim in doc:** "Handlers are always transient" / "enforces the rule at registration time" (lines 130, 132)
  - **Reality in code:** Only `Singleton` is rejected; `Scoped` pre-registrations pass through (src/ServiceConnect/DependencyInjection/ServiceCollectionExtensions.Handlers.cs:145)
  - **Suggested fix:** "Registers handlers as transient and rejects pre-registered singletons at `AddServiceConnect` time."

### website/src/content/docs/learn/core-concepts/endpoints.mdx

- **[critical]** Wrong exchange type — fanout, not topic
  - **Claim in doc:** "ServiceConnect publishes through a RabbitMQ topic exchange named after the message type." (line 112)
  - **Reality in code:** `ExchangeType.Fanout` (src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs:333, src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs:178, empty routing key src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs:287)
  - **Suggested fix:** "topic exchange" → "fanout exchange".
- **[major]** Exchange name is sanitized/hashed, not the plain type name
  - **Claim in doc:** "named after the message type" (line 112)
  - **Reality in code:** `MessageTypeExchangeName.From(type)` produces `{sanitizedFullName}_{8-hex-sha256-prefix}` (src/ServiceConnect/Services/MessageTypeExchangeName.cs:37)
  - **Suggested fix:** Describe as "a per-message-type fanout exchange" without implying the name equals the type's `.Name`.

(`index.mdx` — no findings.)

## learn/messaging-patterns

### website/src/content/docs/learn/messaging-patterns/aggregator.mdx

- **[critical]** Both `BatchSize()` and `Timeout()` are required positive — neither can be disabled
  - **Claim in doc:** "Return `0` from `BatchSize()` to disable size-based flushing; leave the default `Timeout.InfiniteTimeSpan` from `Timeout()` to disable time-based flushing." (line 58)
  - **Reality in code:** `AggregatorRegistry.BuildDescriptor` throws `InvalidOperationException` if `batchSize <= 0 || timeout <= TimeSpan.Zero`; `Aggregator<T>.Timeout()` is `abstract`, no default (src/ServiceConnect/Services/Processors/AggregatorRegistry.cs:117–122, src/ServiceConnect.Interfaces/Aggregation/Aggregator.cs:9–14)
  - **Suggested fix:** State both must be positive; flush is "whichever condition fires first".
- **[major]** Missing `IAggregatorPersistor` is silent, not fatal
  - **Claim in doc:** "If you omit the persistence configuration … registration fails." (line 90)
  - **Reality in code:** `AggregatorProcessor.ProcessAsync` logs a warning and returns `ProcessResult.NotHandled` (src/ServiceConnect/Services/Processors/AggregatorProcessor.cs:76–80)
  - **Suggested fix:** Note the silent drop + warning, not a startup failure.

### website/src/content/docs/learn/messaging-patterns/streaming.mdx

- **[critical]** `WriteAsync` signature is wrong in the sender example
  - **Claim in doc:** `await stream.WriteAsync(payload, offset, count);` "The API takes `buffer, offset, count`…" (lines 51, 58)
  - **Reality in code:** `WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)` (src/ServiceConnect.Interfaces/Streaming/IMessageBusWriteStream.cs:34); the example uses `payload.AsMemory(0, chunkSize)` (examples/Streaming/.../Uploader/Program.cs:36–38)
  - **Suggested fix:** Use `WriteAsync(payload.AsMemory(offset, count))`.
- **[major]** `document` instance in sender example is misrepresented
  - **Claim in doc:** "The generic parameter `<DocumentUploaded>` tells the bus what message type to serialize and include in the stream's metadata. The receiver's handler gets **this instance** along with the assembled bytes." (line 61)
  - **Reality in code:** `CreateStream<T>(endpoint)` takes only an endpoint string; the assembled stream bytes ARE deserialized into the `message` parameter on the receiver (src/ServiceConnect/Bus.cs:485–494, src/ServiceConnect/Services/Processors/StreamProcessor.cs:316–319). The example's local `document` is unused unless its serialized form is the payload being written.
  - **Suggested fix:** Drop the "separate message instance rides alongside the bytes" claim; clarify that bytes-on-the-wire deserialize as `TMessage` on the receiver.

### website/src/content/docs/learn/messaging-patterns/process-manager.mdx

- **[major]** `RequestTimeoutAsync` example passes the wrong correlation id
  - **Claim in doc:** `await context.Bus.RequestTimeoutAsync(message.CorrelationId, …)` (line 183)
  - **Reality in code:** Must be `data.CorrelationId`; xmldoc and the Bus.cs error message both call out the common mistake (src/ServiceConnect.Interfaces/Bus/IBus.cs:257–266, src/ServiceConnect/Bus.cs:627–628)
  - **Suggested fix:** Change to `data.CorrelationId` and add a one-line note on why.

### website/src/content/docs/learn/messaging-patterns/content-based-routing.mdx

- **[critical]** "Bad pattern" handler snippet won't compile against `IMessageHandler<T>`
  - **Claim in doc:** `public Task HandleAsync(OrderPlaced message, CancellationToken cancellationToken = default)` (line 100)
  - **Reality in code:** `Task HandleAsync(TMessage message, IConsumeContext context, CancellationToken cancellationToken = default)` (src/ServiceConnect.Interfaces/Handlers/IMessageHandler.cs:23)
  - **Suggested fix:** Add the missing `IConsumeContext context` parameter.

### website/src/content/docs/learn/messaging-patterns/scatter-gather.mdx

- **[minor]** Return type described as `List<TReply>` but actual is `IList<TReply>`
  - **Claim in doc:** "You receive a `List<TReply>`" (line 42)
  - **Reality in code:** `Task<IList<TReply>>` (src/ServiceConnect.Interfaces/Bus/IBus.cs:110)
  - **Suggested fix:** `List<TReply>` → `IList<TReply>`.

(`pub-sub`, `point-to-point`, `request-reply`, `competing-consumers`, `polymorphic-messages`, `routing-slip`, `filters` — no findings. Note `scatter-gather` and `streaming` have additional Pass-3 gaps in Gap analysis B.)

## learn/operations

### website/src/content/docs/learn/operations/configuration.mdx

- **[critical]** `UseRabbitMQ` overload shown with a URI string that doesn't exist
  - **Claim in doc:** `builder.UseRabbitMQ("amqps://user:pass@rabbit.prod.example.com/vhost", opts => { ... })` (line 91)
  - **Reality in code:** Only two overloads: `UseRabbitMQ(Action<ITransportConfiguration>?)` and `UseRabbitMQ(Action<RabbitMqOptions>?)` (src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQExtensions.cs:19,47)
  - **Suggested fix:** Drop the URI arg; show host configured via the `Action<ITransportConfiguration>` overload.
- **[minor]** "As of v8, TLS is enabled by default" — wrong version label
  - **Claim in doc:** "**As of v8, TLS is enabled by default.**" (line 50)
  - **Reality in code:** Version=7.0.0; `SslEnabled = true` is the default already (src/ServiceConnect/Configuration/TransportConfiguration.cs:32)
  - **Suggested fix:** Drop the version qualifier.

### website/src/content/docs/learn/operations/hosting.mdx

- **[major]** Missing-producer guard at startup is undocumented
  - **Claim in doc:** Lifecycle rules / startup-failures section omits the producer-missing guard (lines 93–100)
  - **Reality in code:** `BusHostedService.StartAsync` throws `InvalidOperationException("No IProducer is registered…")` unless `AllowMissingProducer = true` (src/ServiceConnect/Services/BusHostedService.cs:43–48)
  - **Suggested fix:** Add a bullet calling out the guard and the `AllowMissingProducer` escape hatch.

### website/src/content/docs/learn/operations/error-handling.mdx

- **[minor]** "v8 signature change" aside refers to a non-existent future version
  - **Claim in doc:** "ExceptionHandler changed from `Action<Exception>?` to `Func<Exception, CancellationToken, ValueTask>?` in v8." (lines 132–134)
  - **Reality in code:** Already `Func<Exception, CancellationToken, ValueTask>?` in v7 (src/ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs:62)
  - **Suggested fix:** Remove the aside or retitle to describe v7 in present tense.

### website/src/content/docs/learn/operations/observability.mdx

- **[critical]** Health-checks snippet sets `Host` on `RabbitMqOptions` (no such property)
  - **Claim in doc:** `builder.UseRabbitMQ(opts => opts.Host = "rabbit");` (line 245)
  - **Reality in code:** `RabbitMqOptions` has no `Host` property; host lives on `ITransportConfiguration` (src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMqOptions.cs)
  - **Suggested fix:** `builder.UseRabbitMQ(t => t.Host = "rabbit")`.
- **[minor]** Metrics catalogue omits `messaging.serviceconnect.aggregator.snapshot_remove_failed_after_dispatch`
  - **Claim in doc:** Metrics table (lines 176–188)
  - **Reality in code:** Live counter (src/ServiceConnect/Diagnostics/MetricNames.cs:57, ServiceConnectMeter.cs:71–74)
  - **Suggested fix:** Add a row for the snapshot-remove counter.

(`cancellation`, `idempotency` — no findings.)

## reference/bus + configuration

### website/src/content/docs/reference/bus/ibus.mdx

- **[major]** `ArgumentOutOfRangeException` missing from request-method exception tables
  - **Claim in doc:** Exception tables for `SendRequestAsync`, `SendRequestMultiAsync`, `PublishRequestAsync` (lines 121–125, 151–155, 181–184)
  - **Reality in code:** All three xmldoc `<exception>` lists include `ArgumentOutOfRangeException` for negative/zero `options.Timeout` (src/ServiceConnect.Interfaces/Bus/IBus.cs:80–81, 104, 140)
  - **Suggested fix:** Add the entry to each table.
- **[minor]** Type parameter named `T` instead of `TRequest`
  - **Claim in doc:** `SendRequestAsync<T, TReply>`, `SendRequestMultiAsync<T, TReply>` (lines 100, 103, 130, 133)
  - **Reality in code:** Both are `<TRequest, TReply>` (IBus.cs:86, 110)
  - **Suggested fix:** Rename throughout.
- **[minor]** Exception tables miss `ArgumentNullException` / `ObjectDisposedException` (and `ArgumentException` on `PublishRequestAsync`)
  - **Claim in doc:** Tables for the three request methods
  - **Reality in code:** IBus.cs:79, 82–83, 103, 105–106, 138–140
  - **Suggested fix:** Add the missing entries.
- **[minor]** `IsCancelledByBroker` and `IsStopped` are entirely undocumented
  - **Claim in doc:** Only `IsConsuming` has a section
  - **Reality in code:** `bool IsCancelledByBroker` (IBus.cs:235), `bool IsStopped` (IBus.cs:247) — both have substantial xmldoc
  - **Suggested fix:** Add reference sections for both.
- **[minor]** "Replaces the v7 `SendOptions.EndPoints` plural pattern" while the codebase IS v7
  - **Claim in doc:** Line 81
  - **Reality in code:** Version=7.0.0
  - **Suggested fix:** Drop the version label; describe present-tense behaviour.

### website/src/content/docs/reference/bus/ibusconfiguration.mdx

- **[critical]** `DisposeTimeout` declared as read-only — actually `{ get; set; }`
  - **Claim in doc:** `TimeSpan DisposeTimeout { get; }` (line 179)
  - **Reality in code:** `{ get; set; }` (src/ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs:142; src/ServiceConnect/Configuration/BusConfiguration.cs:62)
  - **Suggested fix:** Change to `{ get; set; }` and drop the "read-only on the interface" sentence.
- **[critical]** `DisposeTimeout = TimeSpan.Zero` advised but rejected at startup
  - **Claim in doc:** "Pass `TimeSpan.Zero` to skip the drain entirely" (line 184)
  - **Reality in code:** `ServiceConnectBuilder.ValidateBus` rejects `<= TimeSpan.Zero` (except `Timeout.InfiniteTimeSpan`) with `InvalidOperationException` (src/ServiceConnect/ServiceConnectBuilder.cs:273–276)
  - **Suggested fix:** Allow only strictly-positive values or `Timeout.InfiniteTimeSpan`.
- **[major]** `StrictReplyValidation` semantics described incorrectly
  - **Claim in doc:** "Validates that the reply payload's CLR type exactly matches the request site's expected reply type" (lines 157–159)
  - **Reality in code:** Controls whether cross-bus request-reply envelopes are trusted via header heuristics vs only locally-tracked exchanges; nothing about CLR-type matching (src/ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs:110–132)
  - **Suggested fix:** Rewrite from the actual xmldoc semantics.
- **[minor]** `EnableRoutingSlipProcessing` references "v8"
  - **Claim in doc:** "In v8, destinations are validated by format only…" (line 136)
  - **Suggested fix:** Drop the version prefix.
- **[minor]** `AllowMissingProducer` not documented
  - **Reality in code:** `bool AllowMissingProducer { get; set; }` (IBusConfiguration.cs:153); defaults `false` (BusConfiguration.cs:64)
  - **Suggested fix:** Add a section.
- **[minor]** `ExceptionHandler` exceptions described as "swallowed"
  - **Claim in doc:** Line 82
  - **Reality in code:** Caught and logged at `Error` level (src/ServiceConnect/Services/MessageDispatcher.cs:324; xmldoc IBusConfiguration.cs:59)
  - **Suggested fix:** "caught and logged at `Error` level by the dispatcher."

### website/src/content/docs/reference/bus/add-serviceconnect.mdx

- **[major]** `ConfigurePipeline` listed as a public builder method
  - **Claim in doc:** "Chain calls to `UseRabbitMQ`, `ConfigureQueues`, `ConfigurePersistence`, and `ConfigurePipeline`" (line 28)
  - **Reality in code:** `ConfigurePipeline` is `internal`; only the typed `AddXxxFilter<T>` / `AddXxxMiddleware<T>` methods are public (src/ServiceConnect/ServiceConnectBuilder.cs:192)
  - **Suggested fix:** Remove `ConfigurePipeline` from the parameter description; the table on line 42 is already correct.

### website/src/content/docs/reference/configuration/iqueueconfiguration.mdx

- **[major]** `AddQueueMapping` overload uses wrong list type
  - **Claim in doc:** `void AddQueueMapping(Type messageType, IList<string> queues)` (line 113)
  - **Reality in code:** `IReadOnlyList<string> queues` (src/ServiceConnect.Interfaces/Configuration/IQueueConfiguration.cs:76)
  - **Suggested fix:** Update the type.

### website/src/content/docs/reference/configuration/itransportconfiguration.mdx

- **[minor]** `SslProtocol` advice contradicts the xmldoc
  - **Claim in doc:** "avoid the default `SslProtocols.None` handoff" (line 180)
  - **Reality in code:** xmldoc treats `None` as the recommended default (runtime-negotiated TLS 1.3) (src/ServiceConnect/Configuration/TransportConfiguration.cs:142–143)
  - **Suggested fix:** Recommend the default; document overriding only when constraining to a specific version.

### website/src/content/docs/reference/configuration/ipipelineconfiguration.mdx

- **[minor]** Tells readers to read `IBusConfiguration.Pipeline` — no such property exists
  - **Claim in doc:** "reading `IBusConfiguration.Pipeline` (or other sub-config interfaces) from DI" (line 10)
  - **Reality in code:** Sub-configs are not exposed on `IBusConfiguration`; they're resolved directly via DI (`IServiceProvider.GetRequiredService<IPipelineConfiguration>()`)
  - **Suggested fix:** "resolving `IPipelineConfiguration` directly from DI".

(`reference/index.mdx`, `ipersistenceconfiguration.mdx` — no findings.)

## reference/handlers + messages + filters

### website/src/content/docs/reference/handlers/imessagehandler.mdx

- **[major]** Migration aside titled "v8 migration" — current version is 7.0.0
  - **Claim in doc:** `<Aside type="note" title="v8 migration — Context property removed">` (lines 37–38)
  - **Reality in code:** `Directory.Build.props:26 — <Version>7.0.0</Version>`; commit `8bde4d08` reverted v8 framing
  - **Suggested fix:** Retitle "v7 migration" and relabel snippets `// before (v6)` / `// after (v7)`.

### website/src/content/docs/reference/handlers/istreamhandler.mdx

- **[critical]** Remarks paragraph claims handler must dispose the stream — `IMessageBusReadStream` is not `IDisposable`
  - **Claim in doc:** "Disposing the stream (or reading it to completion) is the handler's responsibility; the pipeline does not automatically release it for you." (line 35)
  - **Reality in code:** `IMessageBusReadStream` has no `Dispose`; the same page at line 91 / 98 correctly states the framework owns the lifetime (src/ServiceConnect.Interfaces/Streaming/IMessageBusReadStream.cs)
  - **Suggested fix:** Replace with "Not `IDisposable` — handlers must not dispose it."
- **[major]** "v8 migration" aside (same issue as `imessagehandler.mdx`) (lines 37–38)

### website/src/content/docs/reference/handlers/iconsumecontext.mdx

- **[major]** Self-referencing bus isolation bullet
  - **Claim in doc:** "A handler running on bus A cannot accidentally read the current message context set by bus A's consumer." (line 134)
  - **Suggested fix:** "set by bus **B**'s consumer."

### website/src/content/docs/reference/handlers/event-args.mdx

- **[major]** `ConsumeEventArgs.BodySize` not documented
  - **Reality in code:** `public int BodySize { get; init; }` always populated with on-wire byte count (src/ServiceConnect.Interfaces/Bus/ConsumeEventArgs.cs:22)
  - **Suggested fix:** Add an entry.
- **[major]** `ConsumeEventResult.TerminalFailure` not documented
  - **Reality in code:** `public bool TerminalFailure { get; init; }` — instructs transport to bypass retry queue and route directly to error exchange (src/ServiceConnect.Interfaces/Bus/ConsumeEventResult.cs:42)
  - **Suggested fix:** Add an entry.
- **[minor]** `Headers` described as lazily allocated; field initializer is eager (line 47 vs src/ServiceConnect.Interfaces/Bus/ConsumeEventArgs.cs:32)
- **[minor]** `Message` default shown as `Array.Empty<byte>()`; code uses `[]` (line 23 vs ConsumeEventArgs.cs:14)

### website/src/content/docs/reference/filters/isendmessagemiddleware.mdx

- **[major]** `SendContext.Message` described as optional — actually `required`
  - **Claim in doc:** "`Message` — the strongly-typed outgoing `Message` instance, when available." (line 28)
  - **Reality in code:** `public required Message Message { get; init; }` (src/ServiceConnect.Interfaces/Pipelines/SendContext.cs:11)
  - **Suggested fix:** Drop "when available".

(`messages/message.mdx`, `messages/envelope.mdx`, `messages/options.mdx`, `filters/ifilter.mdx`, `filters/imessageprocessingmiddleware.mdx` — no findings.)

## reference/process-managers + healthchecks + telemetry

### website/src/content/docs/reference/process-managers/iprocesshandler.mdx

- **[major]** `ConfigureMapper` invocation count is wrong
  - **Claim in doc:** "invokes `ConfigureMapper` twice per delivery" (line 88)
  - **Reality in code:** Called once; the mapper is reused for both load and persist (src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs:106–118; xmldoc IProcessHandler.cs:47)
  - **Suggested fix:** "once per delivery; the same mapper instance is reused for both calls."

### website/src/content/docs/reference/process-managers/iprocessmanagerpropertymapper.mdx

- **[major]** `ConfigureMapping` signature missing the `where TMessage : Message` constraint (line 31–34 vs src/ServiceConnect.Interfaces/ProcessManagers/IProcessManagerPropertyMapper.cs:44–46)

### website/src/content/docs/reference/process-managers/aggregator.mdx

- **[critical]** `IAggregatorSnapshot.ResolvedMessages` wrong element type — `object` shown, `IHasCorrelationId` actual (line 73; src/ServiceConnect.Interfaces/Aggregation/IAggregatorSnapshot.cs:11)
- **[critical]** `AggregatorSnapshot` record constructor parameter wrong type (line 84–87 vs src/ServiceConnect.Interfaces/Aggregation/AggregatorSnapshot.cs:9–11)
- **[critical]** `Timeout()` / `BatchSize()` declared `virtual` — actually `abstract` (lines 29, 41 vs src/ServiceConnect.Interfaces/Aggregation/Aggregator.cs:26, 32)
- **[critical]** "Leave the default" guidance — there are no defaults; values are validated at startup
  - **Reality in code:** Both abstract; registry rejects `Timeout.InfiniteTimeSpan` or non-positive `BatchSize` with startup `InvalidOperationException` (src/ServiceConnect.Interfaces/Aggregation/Aggregator.cs:7–14)
- **[major]** Aside "Override at least one" — actually both required (lines 48–50)

### website/src/content/docs/reference/healthchecks/index.mdx

- **[major]** `AddServiceConnectBus` default overload described with wrong mechanism
  - **Claim in doc:** "Uses `ActivatorUtilities.CreateInstance<BusConsumingHealthCheck>`" (line 58)
  - **Reality in code:** Uses `sp => sp.GetRequiredService<IBus>()` wrapped in `PerProviderCache<BusConsumingHealthCheck>` (src/ServiceConnect.HealthChecks/HealthChecksBuilderExtensions.cs:29–30, 98–103)
- **[major]** `ConsumerConnectionHealthCheck` documented as 2-state; actually 4-state with grace window and 3-arg constructor not documented (lines 192–193 vs src/ServiceConnect.HealthChecks/ConsumerConnectionHealthCheck.cs:35–124)
- **[major]** `ProducerConnectionHealthCheck` lazy-state message text wrong
  - **Claim in doc:** "Producer has not yet attempted a connection." (line 210)
  - **Reality in code:** "Producer has not yet attempted connection (lazy)." (src/ServiceConnect.HealthChecks/ProducerConnectionHealthCheck.cs:56)

### website/src/content/docs/reference/telemetry/index.mdx

- **[critical]** `ActivitySourceName` comment hedges with "e.g." but value is deterministic
  - **Claim in doc:** `// computed from assembly name, e.g. "ServiceConnect.Telemetry.Bus"` (line 163)
  - **Reality in code:** Assembly is `ServiceConnect.Telemetry`; value is exactly `"ServiceConnect.Telemetry.Bus"` (src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs:21, ServiceConnect.Telemetry.csproj:7)
  - **Suggested fix:** "e.g." → "currently".
- **[critical]** `MessagingAttributes` table references non-existent constants `ErrorType` and `MessagingClientId` (lines 258, 261 vs src/ServiceConnect.Telemetry/MessagingAttributes.cs:12–84)
- **[critical]** `MessagingMessageBodySize` constant name wrong — actual name `MessagingBodySize` (line 257 vs MessagingAttributes.cs:68)
- **[major]** Instrument count wrong: "10 instruments — six OTel-standard plus four extensions" (line 267); actual is **11** (4 OTel-standard + 7 ServiceConnect-specific) (src/ServiceConnect/Diagnostics/ServiceConnectMeter.cs:26–79, MetricNames.cs)
- **[major]** `IMessagingSystemAttributes` interface snippet omits `ServerAddress`/`ServerPort` default-interface-method members (lines 223–228 vs src/ServiceConnect.Telemetry/IMessagingSystemAttributes.cs:26–36)

(`iprocessmanagerdata.mdx` — no findings.)

## reference/extension-points

### website/src/content/docs/reference/extension-points/index.mdx

- **[major]** Missing card for the Bus extension point (`IRequestReplyManager` page exists as a peer) (lines 14–35)

### website/src/content/docs/reference/extension-points/bus/irequestreplymanager.mdx

- **[minor]** `IReplyStatusRequestReplyManager` presented as implementable; it's `internal` (src/ServiceConnect/Services/IReplyStatusRequestReplyManager.cs:3) — custom replacements that don't also implement it cause startup `InvalidOperationException` (lines 163–175)
- **[nit]** Missing `using ServiceConnect.Interfaces.Exceptions;` in the snippet

### website/src/content/docs/reference/extension-points/persistence/iaggregatorpersistor.mdx

- **[major]** `ReleaseSnapshotAsync` is absent from the reference list
  - **Reality in code:** DIM returning `Task.CompletedTask`; MongoDB persistors override it (src/ServiceConnect.Interfaces/Aggregation/IAggregatorPersistor.cs:90)
  - **Suggested fix:** Add a subsection.
- **[nit]** Skeletal implementation example doesn't include the method

### website/src/content/docs/reference/extension-points/persistence/iprocessmanagerfinder.mdx

- **[major]** `IVersioned.Version` shown as `int`; actual `long`
  - **Claim in doc:** `int Version { get; }` (line 123) and the skeleton example at line 251
  - **Reality in code:** `long Version { get; }` (src/ServiceConnect.Interfaces/Persistence/IVersioned.cs:18; src/ServiceConnect.Persistence.InMemory/ProcessManager/MemoryData.cs:18)
- **[minor]** Same wrong type carried into the `PostgresPersistenceData<T>` skeleton example

### website/src/content/docs/reference/extension-points/persistence/itimeoutstore.mdx

- **[major]** `ReapStaleLeasesAsync` mentioned in prose but not as a member; missing from reference code block
  - **Reality in code:** `Task<long> ReapStaleLeasesAsync(CancellationToken)` with DIM (src/ServiceConnect.Interfaces/Persistence/ITimeoutStore.cs:101)
- **[major]** `TimeoutsBatch.NextQueryTime` referenced repeatedly — no such property
  - **Reality in code:** Only `IReadOnlyList<TimeoutData> DueTimeouts` (src/ServiceConnect.Interfaces/Timeouts/TimeoutsBatch.cs:12)
- **[minor]** "Skeletal third-party" implementation is essentially the shipping `InMemoryTimeoutStore` — labelling implies hypothetical

### website/src/content/docs/reference/extension-points/transport/iconsumer.mdx

- **[major]** `ConsumeEventResult` block missing `TerminalFailure` property (lines 109–114 vs src/ServiceConnect.Interfaces/Bus/ConsumeEventResult.cs:42)
- **[minor]** Skeletal/Kafka examples use `IList<string> messageTypes`; interface is `IReadOnlyList<string>` (lines 182, 262 vs src/ServiceConnect.Interfaces/Bus/IConsumer.cs:23)

### website/src/content/docs/reference/extension-points/transport/iproducer.mdx

- **[major]** `PublishAsync` routing-key overload not documented
  - **Reality in code:** DIM `PublishAsync(Type, ReadOnlyMemory<byte>, string? routingKey, IReadOnlyDictionary<string,string>?, CancellationToken)` (src/ServiceConnect.Interfaces/Bus/IProducer.cs:34)
- **[major]** `SendAsync` routing-slip-hop overload not documented
  - **Reality in code:** DIM with `int? routingSlipHopsCompleted` parameter that stamps the header (src/ServiceConnect.Interfaces/Bus/IProducer.cs:82)
- **[minor]** Skeletal example declares `MaximumMessageSize` twice (lines 230 and 274) — won't compile

(`registry/ihandlerregistry.mdx`, `registry/imessagedispatcher.mdx`, `registry/imessageprocessor.mdx`, `serialization/imessageserializer.mdx`, `serialization/imessagetyperegistry.mdx` — no findings.)

## releases + samples + README

### website/src/content/docs/releases.mdx

- **[major]** "v8 highlights" framed as a separate forthcoming release; all listed changes are in v7.0.0
  - **Claim in doc:** "## v8 highlights / Version 8 is a public-API tightening pass…" (line 28)
  - **Reality in code:** `Directory.Build.props:26 — <Version>7.0.0</Version>`; commit `8bde4d08`: "revert v8 framing — these changes ship in v7"
  - **Suggested fix:** Merge "v8 highlights" content into the v7 section.
- **[major]** "Newtonsoft.Json remains a dependency of … InMemory … and Client.RabbitMQ" — both are now STJ-only
  - **Reality in code:** Neither csproj references Newtonsoft.Json; `DeepClone.cs:1` and `MessageRetryHandler.cs:122` use `System.Text.Json`
  - **Suggested fix:** Replace with "removed from all production packages; `SerializationCompatTests` retains the reference as the wire-compat holdout."
- **[minor]** v7 section claims `IQueueConfiguration.AuditRoutingKey` was added — property absent from `IQueueConfiguration.cs`

### website/src/content/docs/samples.mdx

No findings — all 14 example directories exist; `run.sh`/`run.ps1` present in each; `docker-compose.yml` command accurate; `DependencyWaiter` confirmed; internal links resolve.

### README.md

No findings — package IDs, code snippets, API signatures (`IBus.StartConsumingAsync`, `PublishAsync`, `IMessageHandler<T>.HandleAsync`, `Message(Guid)`, `UseRabbitMQ`, `ConfigureQueues`), TFM claims (`net8.0;net10.0`), and example directory references all check out.

---

## Gap analysis A — undocumented public surface

### Summary

- ServiceConnect.Interfaces public types: 65 / 68 documented
- ServiceConnect public types: 4 / 10 (excluding internal-only surface)
- ServiceConnect.HealthChecks public types: 5 / 5
- ServiceConnect.Telemetry public types: 8 / 8
- Configuration options documented: 56 / 57 (the gap is `AllowMissingProducer`)

### Undocumented user-facing types

| Type / Member | Project | Notes | Suggested docs home |
|---|---|---|---|
| `ServiceConnectException` (abstract base) | ServiceConnect.Interfaces | Base of all framework exceptions; users writing `catch (ServiceConnectException)` have nothing to link to | New `reference/bus/exceptions.mdx`, or section in `learn/operations/error-handling.mdx` |
| `TransportException` | ServiceConnect.Interfaces | Thrown by IConsumer/IProducer on broker-level failures; zero doc hits | Same exceptions page/section |
| `HandlerInterfaceKind` (enum) | ServiceConnect.Interfaces | Discriminates `HandlerReference` source (MessageHandler / ProcessHandler / StreamHandler / Aggregator); needed for custom `IHandlerRegistry` implementers | `reference/extension-points/registry/ihandlerregistry.mdx` — companion section |
| `IBusConfiguration.AllowMissingProducer` | ServiceConnect.Interfaces | Allows consume-only / in-process test buses to start without an IProducer; zero doc hits across the site | `reference/bus/ibusconfiguration.mdx` |

### Public types that look infra and probably DON'T need docs

`ServiceConnectMeter`, `ExceptionTypeMapper`, `MetricNames` (constants), `TimeoutHeaderPersistence`, `RoutingSlipDestinationValidator`, `MessageTypeExchangeName`, `HandlerScanner`, `RabbitMqMessagingSystemAttributes`, `ProcessResult` (enum), `ISendMessagePipeline`, `IFilterPipeline`, `AggregatorSnapshot` (concrete record), `ProcessManagerToMessageMap`, `HeaderDecoder` (helper). All are either internal-feeling helpers or already covered by the surrounding interface's page.

---

## Gap analysis B — examples not reflected in docs

### Summary

- Example projects audited: 14
- Examples with full docs coverage: 9
- Examples with partial coverage (gaps flagged): 4 (`CustomFilterAndMiddleware`, `ScatterGather`, `Streaming`, `Telemetry`)
- Examples with no docs counterpart: 1 (the `CustomFilterAndMiddleware` hosted-consumer shape is not covered anywhere)

### Per-example coverage

| Example | Primary docs page | Coverage |
|---|---|---|
| Aggregator | learn/messaging-patterns/aggregator.mdx | full |
| CompetingConsumers | learn/messaging-patterns/competing-consumers.mdx | full |
| ContentBasedRouting | learn/messaging-patterns/content-based-routing.mdx | full |
| CustomFilterAndMiddleware | learn/messaging-patterns/filters.mdx | **gaps:** hosted-consumer shape (Host.CreateDefaultBuilder + IHostApplicationLifetime); `HeaderDecoder.Decode` |
| Filters | learn/messaging-patterns/filters.mdx | full |
| PointToPoint | learn/messaging-patterns/point-to-point.mdx | full |
| PolymorphicMessages | learn/messaging-patterns/polymorphic-messages.mdx | full |
| ProcessManager | learn/messaging-patterns/process-manager.mdx | full |
| PublishSubscribe | learn/messaging-patterns/pub-sub.mdx | full |
| RequestReply | learn/messaging-patterns/request-reply.mdx | full |
| RoutingSlip | learn/messaging-patterns/routing-slip.mdx | full |
| ScatterGather | learn/messaging-patterns/scatter-gather.mdx | **gap:** example uses `PublishRequestAsync` (broadcast + callback); docs teach `SendRequestMultiAsync` (queue-mapping). Example comment says queue-mapping fan-out is removed |
| Streaming | learn/messaging-patterns/streaming.mdx | **gap:** docs show `WriteAsync(buffer, offset, count)`; example uses `WriteAsync(ReadOnlyMemory<byte>)` |
| Telemetry | learn/operations/observability.mdx; reference/telemetry/index.mdx | **gap:** example uses raw `ActivitySource.AddActivityListener`; docs only show OTel SDK path |

### Detailed gap findings

- **[major]** ScatterGather example uses `PublishRequestAsync`; docs teach `SendRequestMultiAsync` — incompatible call shapes
  - **In example:** examples/ScatterGather/.../Requester/Program.cs:34–41 (callback + `RequestOptions.ExpectedReplyCount`); comment line 30–32 says "EndPoints fan-out is no longer supported on request/reply; broadcast + manual correlation is the right pattern"
  - **In docs:** learn/messaging-patterns/scatter-gather.mdx:42–67 uses `SendRequestMultiAsync` + `AddQueueMapping(typeof(SearchRequest), new[] { "catalog-a", "catalog-b" })`
  - **Suggested fix:** Rewrite the scatter-gather page to lead with `PublishRequestAsync`; note when each method applies.
- **[minor]** Streaming docs show three-param `WriteAsync(buffer, offset, count)`; example uses `WriteAsync(ReadOnlyMemory<byte>)`
  - **In example:** examples/Streaming/.../Uploader/Program.cs:36–38 — `stream.WriteAsync(payload.AsMemory(0, chunkSize))`
  - **In docs:** learn/messaging-patterns/streaming.mdx:48–54
  - **Suggested fix:** Update the snippet to `payload.AsMemory(offset, count)`. (Same issue is filed Pass-1 critical on the streaming page.)
- **[minor]** Telemetry example uses raw `ActivitySource.AddActivityListener`; docs only show OTel SDK
  - **In example:** examples/Telemetry/.../Publisher/TelemetryConsoleListener.cs:8–17
  - **In docs:** learn/operations/observability.mdx:84–98 and reference/telemetry/index.mdx:289–310 show only `services.AddOpenTelemetry().WithTracing(b => b.AddServiceConnectInstrumentation().AddOtlpExporter())`
  - **Suggested fix:** Add a short "lightweight listener (no OTel SDK)" subsection to the telemetry reference page.
- **[minor]** CustomFilterAndMiddleware uses `Host.CreateDefaultBuilder` + `IHostApplicationLifetime.ApplicationStarted`; docs only show manual-DI console pattern
  - **In example:** examples/CustomFilterAndMiddleware/.../Consumer/Program.cs:11–52
  - **In docs:** learn/operations/hosting.mdx covers `Host.CreateApplicationBuilder` with `AutoStartConsuming = true` and the manual `ServiceCollection` path but not `IHostApplicationLifetime` readiness signalling
  - **Suggested fix:** Add a short note in hosting.mdx.
- **[minor]** `HeaderDecoder.Decode` is used by example filters; docs never name the helper
  - **In example:** examples/CustomFilterAndMiddleware/.../Consumer/Filters/DedupeIncomingFilter.cs:17–19, DedupeOnSuccessFilter.cs:23
  - **In docs:** reference/messages/envelope.mdx:26 and reference/handlers/iconsumecontext.mdx:34 say "defensively cast or decode" but never mention `HeaderDecoder.Decode`
  - **Suggested fix:** Add a `HeaderDecoder.Decode` callout in envelope.mdx, noting RabbitMQ delivers headers as `byte[]` / `string` / numerics.
