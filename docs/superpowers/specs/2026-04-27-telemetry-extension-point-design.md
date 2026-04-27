# Telemetry Extension Point, Sample, and Website Updates

Date: 2026-04-27
Branch context: `v7-clean-architecture`

## Summary

Three coordinated changes:

1. **Framework** — change `ISendMessageMiddleware` to take a `SendContext` object so the strongly-typed `Message` (and routing key, operation kind) survives the send pipeline. Add `builder.AddTelemetry(opts => ...)` to `ServiceConnectBuilder` so `ServiceConnect.Telemetry` can be wired with one line.
2. **Sample** — add `examples/Telemetry/` (publisher → two subscribers) demonstrating end-to-end W3C trace propagation across the broker. Verifiable in `run.sh` without external dependencies.
3. **Website** — add a Telemetry samples entry, replace the hand-wavy "wire the helpers from a filter" paragraph in `observability.mdx` with the new one-line API, and update the `event-args.mdx` example to point at the package's built-in middleware.

## Motivation

`ServiceConnect.Telemetry` ships `ServiceConnectActivitySource.Publish/Send/Consume(eventArgs)` helpers that produce W3C-compliant spans with the OTel messaging semantic-convention tags. The package was clearly designed assuming the caller has rich context — the helpers read `PublishEventArgs.Message?.CorrelationId`, expose an `EnrichWithMessage(activity, message)` callback, etc.

But none of the bus's existing extension points actually provide that context to a middleware author:

| Extension point | What it gives you | Can populate a full `PublishEventArgs.Message`? |
|---|---|---|
| `IFilter` | `Envelope` (Headers + Body bytes) | No |
| `IMessageProcessingMiddleware` | bytes, type, **deserialized message**, headers, envelope | n/a — consume side, already correct |
| `ISendMessageMiddleware` | type, bytes, headers, single `endPoint` | **No** — strongly-typed message is gone by this point |

The send-side gap is the painful one. The strongly-typed `Message` is alive in `Bus.PublishAsync<T>`/`SendAsync<T>` ([Bus.cs:89-117](src/ServiceConnect/Bus.cs#L89-L117)) but is serialized to bytes immediately, before the pipeline runs. Middleware sees `byte[]` and headers only. As a result, current observability docs say "wire the package by calling the Publish/Send/Consume helpers from a filter or custom outgoing hook" without ever showing a working setup — because no working setup is possible without re-deserializing the bytes inside the middleware.

The consume-side `IMessageProcessingMiddleware` already carries a strongly-typed `message` parameter, so it does not need any contract change.

The user-facing goal is a one-line wire-up:

```csharp
services.AddServiceConnect(builder =>
{
    builder.UseRabbitMQ(/* ... */);
    builder.AddTelemetry(opts => { /* optional enrichment */ });
});
```

To deliver that one-liner, the framework needs a send-pipeline shape that lets the telemetry middleware read the message it was given.

## Goals

- The strongly-typed `Message` instance is visible to send-side middleware authors.
- A single, supported wiring path: `builder.AddTelemetry(opts => ...)`.
- W3C trace context propagates across the broker for publish, send, and request/reply, without the user opting in to anything beyond `AddTelemetry()`.
- The `ServiceConnect.Telemetry` package contains all telemetry-specific code; the core bus has no compile-time dependency on it.
- A runnable sample at `examples/Telemetry/` proves cross-broker trace correlation in CI without requiring Jaeger or an OTel collector.
- Existing `IFilter` and `IMessageProcessingMiddleware` extension points are unchanged.

## Non-goals

- A new `IPublishObserver`/`ISendObserver`/`IConsumeObserver` parallel pipeline. (Considered and rejected; one pipeline is enough once the contract is fixed.)
- Bus-raised events on `IBus`. (Considered and rejected; events compose poorly with DI and around-style activity lifecycles.)
- Auto-wiring telemetry inside the bus internals so users don't need `AddTelemetry()`. (Couples the bus to the telemetry package; rejected.)
- Adding a Jaeger / OTLP service to `examples/docker-compose.yml`. (Out of scope; the sample uses an in-process `ActivityListener` for verification, and the Console Exporter is shown as commented-out alternative.)
- Changes to the consume-side middleware contract (`IMessageProcessingMiddleware`). It is already correct.
- Changes to `IFilter` (different layer, used for header-stamping/short-circuit filters; out of scope).
- A health-check implementation (existing `observability.mdx` "No health checks" section is unchanged).

## Architecture overview

Two repository-level changes plus website updates.

**Framework**

- New `SendContext` record. `ISendMessageMiddleware.ProcessAsync` takes `(SendContext, SendMessageDelegate, CancellationToken)`. `Bus.PublishAsync<T>`/`SendAsync<T>`/`SendRequestAsync<T,…>`/`SendRequestMultiAsync<T,…>`/`PublishRequestAsync<T,…>` build a `SendContext` once after serialization and thread it through `_sendPipeline.ExecutePublish/SendMessagePipelineAsync`.
- `ServiceConnect.Telemetry` gains a `TelemetryBuilderExtensions.AddTelemetry(this ServiceConnectBuilder, Action<ServiceConnectInstrumentationOptions>?)` extension that registers a built-in `TelemetrySendMiddleware` and `TelemetryProcessingMiddleware` as singletons and inserts them at position 0 of both pipelines.
- `ServiceConnectActivitySource.Options` and `ServiceConnectActivitySource.MessagingSystemAttributes` setters are already `internal`; this work introduces `AddTelemetry()` as the supported configuration path that populates them.

**Sample**

- New `examples/Telemetry/` mirroring `examples/PublishSubscribe/`. Each program calls `builder.AddTelemetry()` and registers an `ActivityListener` that prints `TRACE:<endpoint>:<op>:<traceId>:<spanId>:<parentSpanId>` lines. `run.sh` asserts the trace IDs match across processes and the parent linkage holds.

**Website**

- `samples.mdx` gains a Telemetry catalog entry.
- `observability.mdx` Tracing section is rewritten around the `AddTelemetry()` wiring.
- `event-args.mdx` usage example is updated to show the package-supplied middleware as the primary path, with the hand-rolled middleware retained under "If you need a custom middleware".

## Framework changes

### `SendContext` and `SendOperation`

```csharp
namespace ServiceConnect.Interfaces;

public sealed class SendContext
{
    public required Message Message { get; init; }
    public required Type MessageType { get; init; }
    public required byte[] MessageBytes { get; init; }
    public required IDictionary<string, string> Headers { get; init; }
    public string? EndPoint { get; init; }
    public string? RoutingKey { get; init; }
    public SendOperation Operation { get; init; }
}

public enum SendOperation { Publish, Send, Request }
```

Notes:

- `Message` is non-nullable. Every existing send-pipeline call site has a real `Message` in hand at the point of construction. If a future raw-bytes producer API is added, it will not flow through this pipeline.
- `Headers` stays `IDictionary<string, string>` and mutable; trace-context injection happens inside the telemetry middleware on the way down, so the producer sees the `traceparent` header.
- `RoutingKey` is populated for `Operation.Publish` when `PublishOptions.RoutingKey` was set; null otherwise. `EndPoint` is populated for `Operation.Send`/`Request`; null for `Publish`. Multi-endpoint sends are still expanded by `Bus.SendAsync` ([Bus.cs:155-160](src/ServiceConnect/Bus.cs#L155-L160)) into N sequential pipeline calls, each with one `EndPoint`.
- `Operation` is a tri-state, not an `IsPublish` bool, because `Bus.SendRequestAsync`/`SendRequestMultiAsync` are a third call site that the telemetry middleware will eventually want to distinguish (a future request/reply `ActivitySource` becomes possible without breaking the contract again).

### `ISendMessageMiddleware` (new contract)

```csharp
namespace ServiceConnect.Interfaces;

public delegate Task SendMessageDelegate(SendContext context, CancellationToken cancellationToken);

public interface ISendMessageMiddleware
{
    Task ProcessAsync(SendContext context, SendMessageDelegate next, CancellationToken cancellationToken);
}
```

Existing test-only fixtures (`BlockingSendMiddleware`, `RecordingSendMiddleware`, `ShortCircuitSendMiddleware`, `HeaderAddingSendMiddleware`, `TestSendMiddleware`) port to the new shape with one-line changes.

### Bus-side wiring

`SendMessagePipeline.BuildPublishChain`/`BuildSendChain` ([SendMessagePipeline.cs:56-89](src/ServiceConnect/Services/SendMessagePipeline.cs#L56-L89)) change to thread `SendContext` instead of the loose tuple of params. The terminal `producer.PublishAsync`/`producer.SendAsync` continues to take the existing `(Type, byte[], headers, ct)` shape — the producer interface (`IProducer`) is unchanged; only the middleware wrapping changes.

`ISendMessagePipeline.ExecutePublish/SendMessagePipelineAsync` signatures change to take `SendContext` directly:

```csharp
Task ExecutePublishMessagePipelineAsync(SendContext context, CancellationToken cancellationToken = default);
Task ExecuteSendMessagePipelineAsync(SendContext context, CancellationToken cancellationToken = default);
```

`Bus.cs` builds the context once per public method:

```csharp
public async Task PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken ct = default) where T : Message
{
    /* unchanged: ThrowIfDisposed, cancellation, serialize, run outgoing filters, build headers */

    var ctx = new SendContext
    {
        Message = message,
        MessageType = typeof(T),
        MessageBytes = messageBytes,
        Headers = headers,
        EndPoint = null,
        RoutingKey = options?.RoutingKey,
        Operation = SendOperation.Publish,
    };
    await _sendPipeline.ExecutePublishMessagePipelineAsync(ctx, ct).ConfigureAwait(false);
}
```

`SendAsync` builds with `Operation = Send` and `EndPoint = options?.EndPoint`. `SendRequestAsync`/`SendRequestMultiAsync`/`PublishRequestAsync` build with `Operation = Request`. Multi-endpoint expansion in `SendAsync` continues to issue N pipeline calls; each gets its own `SendContext` with a single `EndPoint`.

### `builder.AddTelemetry()` API

```csharp
namespace ServiceConnect.Telemetry;

public static class TelemetryBuilderExtensions
{
    public static ServiceConnectBuilder AddTelemetry(
        this ServiceConnectBuilder builder,
        Action<ServiceConnectInstrumentationOptions>? configure = null)
    {
        var options = new ServiceConnectInstrumentationOptions();
        configure?.Invoke(options);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<TelemetrySendMiddleware>();
        builder.Services.AddSingleton<TelemetryProcessingMiddleware>();

        builder.ConfigurePipeline(p =>
        {
            p.SendMessageMiddleware.Insert(0, typeof(TelemetrySendMiddleware));
            p.MessageProcessingMiddleware.Insert(0, typeof(TelemetryProcessingMiddleware));
        });

        ServiceConnectActivitySource.Options = options;

        return builder;
    }
}
```

`Insert(0, …)` rather than `AddSendMessageMiddleware<T>()`/`AddMessageProcessingMiddleware<T>()` (the existing append-only methods at [ServiceConnectBuilder.cs:179,190](src/ServiceConnect/ServiceConnectBuilder.cs#L179)) so telemetry is the **outermost** middleware regardless of when `AddTelemetry()` is called relative to other `AddSendMessageMiddleware<T>` calls in the builder block. Outermost matters: it ensures the activity is alive for the full duration of any other middleware (correct timing) and that `traceparent` injection happens before user middleware can mutate headers. The concrete `PipelineConfiguration` exposes the middleware lists as mutable `IList<Type>` ([PipelineConfiguration.cs:35](src/ServiceConnect/Configuration/PipelineConfiguration.cs#L35)), reachable through the existing public `ConfigurePipeline` ([ServiceConnectBuilder.cs:124](src/ServiceConnect/ServiceConnectBuilder.cs#L124)) — no new builder method, no `InternalsVisibleTo` shim.

`MessagingSystemAttributes` is set the same way; the default remains `RabbitMqMessagingSystemAttributes`.

### Telemetry middleware classes

```csharp
internal sealed class TelemetrySendMiddleware(ServiceConnectInstrumentationOptions options) : ISendMessageMiddleware
{
    public async Task ProcessAsync(SendContext context, SendMessageDelegate next, CancellationToken ct)
    {
        var args = context.Operation == SendOperation.Publish
            ? (OutgoingEventArgs)new PublishEventArgs
                {
                    Message = context.Message,
                    Headers = context.Headers,
                    RoutingKey = context.RoutingKey ?? string.Empty,
                }
            : new SendEventArgs
                {
                    Message = context.Message,
                    Headers = context.Headers,
                    EndPoint = context.EndPoint ?? string.Empty,
                };

        using Activity? activity = context.Operation == SendOperation.Publish
            ? ServiceConnectActivitySource.Publish((PublishEventArgs)args)
            : ServiceConnectActivitySource.Send((SendEventArgs)args);

        try
        {
            await next(context, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ServiceConnectActivitySource.SetError(activity, ex);
            throw;
        }
    }
}
```

`TelemetryProcessingMiddleware` is symmetric against `IMessageProcessingMiddleware` and `ConsumeEventArgs`:

```csharp
internal sealed class TelemetryProcessingMiddleware : IMessageProcessingMiddleware
{
    public async Task<ConsumeEventResult> ProcessAsync(
        ReadOnlyMemory<byte> messageBytes, Type messageType, object message,
        IDictionary<string, object> headers, Envelope envelope,
        MessageProcessingDelegate next, CancellationToken ct)
    {
        var args = new ConsumeEventArgs
        {
            Message = envelope.Body.ToArray(),
            Type = messageType.FullName ?? string.Empty,
            Headers = headers,
        };

        using Activity? activity = ServiceConnectActivitySource.Consume(args);

        try
        {
            var result = await next(messageBytes, messageType, message, headers, envelope, ct).ConfigureAwait(false);
            if (!result.Success && result.Exception is not null)
            {
                ServiceConnectActivitySource.SetError(activity, result.Exception);
            }
            return result;
        }
        catch (Exception ex)
        {
            ServiceConnectActivitySource.SetError(activity, ex);
            throw;
        }
    }
}
```

### Static options remain internal

```csharp
public static ServiceConnectInstrumentationOptions Options { get; internal set; } = new();
public static IMessagingSystemAttributes MessagingSystemAttributes { get; internal set; } = new RabbitMqMessagingSystemAttributes();
```

The setters are already internal in the current codebase ([ServiceConnectActivitySource.cs:16,20](src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L16)); no visibility change is required. The new `AddTelemetry()` extension lives in the same assembly as `ServiceConnectActivitySource` (`ServiceConnect.Telemetry`), so it writes the setters directly with no `InternalsVisibleTo` shim. The existing `InternalsVisibleTo` for `ServiceConnect.UnitTests` is unaffected.

## Sample (`examples/Telemetry/`)

### Layout

```
examples/Telemetry/
├── README.md
├── Telemetry.sln
├── run.sh
├── run.ps1
├── output.log                                  ← gitignored at runtime
└── src/
    ├── ServiceConnect.Examples.Telemetry.Contracts/
    │   ├── OrderPlaced.cs
    │   └── ServiceConnect.Examples.Telemetry.Contracts.csproj
    ├── ServiceConnect.Examples.Telemetry.Publisher/
    │   ├── Program.cs
    │   ├── TelemetryConsoleListener.cs
    │   └── ServiceConnect.Examples.Telemetry.Publisher.csproj
    ├── ServiceConnect.Examples.Telemetry.BillingSubscriber/
    │   ├── Program.cs
    │   ├── OrderPlacedHandler.cs
    │   ├── TelemetryConsoleListener.cs
    │   └── ServiceConnect.Examples.Telemetry.BillingSubscriber.csproj
    └── ServiceConnect.Examples.Telemetry.AnalyticsSubscriber/
        └── (mirror of BillingSubscriber)
```

### Per-process Program.cs shape

Subscriber:

```csharp
TelemetryConsoleListener.Register("billing-subscriber");

var settings = ExampleSettingsLoader.Load();
await DependencyWaiter.WaitForRabbitMqAsync(/* ... */);

var handlerReferences = new List<HandlerReference>
{
    new() { HandlerType = typeof(OrderPlacedHandler), MessageType = typeof(OrderPlaced) }
};

var services = new ServiceCollection();
services.AddSingleton<IList<HandlerReference>>(handlerReferences);
services.AddTransient<IMessageHandler<OrderPlaced>, OrderPlacedHandler>();
services.AddExampleBus(settings, "billing-subscriber",
    configureBuilder: builder => builder.AddTelemetry());

// To export to a real OTel pipeline, replace the listener registration above with:
// services.AddOpenTelemetry().WithTracing(t => t
//     .AddSource(ServiceConnectActivitySource.PublishActivitySourceName)
//     .AddSource(ServiceConnectActivitySource.SendActivitySourceName)
//     .AddSource(ServiceConnectActivitySource.ConsumeActivitySourceName)
//     .AddConsoleExporter());

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
ConsoleStatus.Ready("billing-subscriber");
await Task.Delay(Timeout.InfiniteTimeSpan);
```

Publisher is the corresponding shape ending in `bus.PublishAsync(new OrderPlaced(...))`.

### TelemetryConsoleListener

```csharp
public static class TelemetryConsoleListener
{
    public static void Register(string endpoint)
    {
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = src => src.Name.StartsWith("ServiceConnect.Bus.", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => Console.WriteLine(
                $"TRACE:{endpoint}:{a.OperationName}:{a.TraceId}:{a.SpanId}:{a.ParentSpanId}"),
        });
    }
}
```

Duplicated per project — same approach as `TraceHeaderFilter` in the Filters sample. Acceptable duplication; teaching artifact.

### run.sh assertion

After the existing READY/SUCCESS waits, parse `output.log` and assert:

1. Exactly one `TRACE:telemetry-publisher:publish:` line exists.
2. Both `TRACE:billing-subscriber:receive:` and `TRACE:analytics-subscriber:receive:` lines exist.
3. The publisher's TraceId equals both subscribers' TraceIds.
4. The publisher's SpanId equals each subscriber's ParentSpanId.

Implementation is `awk`/`grep`; ~20 lines. On any assertion failure, exit 1 with a clear message identifying which property failed.

`run.ps1` mirrors the same logic in PowerShell.

### README.md

Follows the established sample shape: Overview / Participants / Mermaid sequence diagram / Prerequisites / Run This Example / Run Manually / Expected Output / What To Notice. The "What To Notice" section explicitly calls out the single shared TraceId across three processes as the property the package guarantees, and points at `observability.mdx` Tracing for the conceptual story.

## Website updates

### samples.mdx — new entry

Inserted alongside Filters / MessageDeduplication:

```markdown
### Telemetry

End-to-end OpenTelemetry tracing across publish → consume, demonstrating
W3C trace-context propagation through the broker. Three processes share
one TraceId; each subscriber's span is a direct child of the publisher's.

- Tracing reference: [Observability — Tracing](/ServiceConnect-CSharp/learn/operations/observability/#tracing-opentelemetry)
- Source: [`examples/Telemetry`](https://github.com/R-Suite/ServiceConnect-CSharp/tree/master/examples/Telemetry)
```

### observability.mdx — rewrite the Tracing section

Replace [observability.mdx:74-106](website/src/content/docs/learn/operations/observability.mdx#L74-L106) with:

- **One-paragraph intro** of the three sources (`ServiceConnect.Bus.Publish/Send/Consume`) and the OTel messaging semantic-convention tags they emit. Kept from the current page.
- **Wiring** subsection:

  ```csharp
  services.AddServiceConnect(builder =>
  {
      builder.UseRabbitMQ(/* ... */);
      builder.AddTelemetry(opts => { /* optional enrichment */ });
  });

  services.AddOpenTelemetry()
      .WithTracing(tracing => tracing
          .AddSource(ServiceConnectActivitySource.PublishActivitySourceName)
          .AddSource(ServiceConnectActivitySource.SendActivitySourceName)
          .AddSource(ServiceConnectActivitySource.ConsumeActivitySourceName)
          .AddOtlpExporter());
  ```

- **Enrichment** subsection showing `EnrichWithMessage` with the existing security-warning callout preserved verbatim.
- **Disabling specific sources** subsection (`opts.EnablePublishTelemetry = false` etc.).
- **Propagation** paragraph: traceparent injected on outgoing, extracted on incoming, link to the runnable `examples/Telemetry/` sample.

The "No health checks" and "Putting it together" sections at the bottom of `observability.mdx` are unchanged.

### event-args.mdx — update the usage example

Replace the current hand-rolled `OpenTelemetryConsumeMiddleware` block at [event-args.mdx:146-212](website/src/content/docs/reference/handlers/event-args.mdx#L146-L212) with:

- A "Recommended: use the `ServiceConnect.Telemetry` package" subsection showing the one-line `builder.AddTelemetry()` and pointing at `observability.mdx`.
- An "If you need a custom middleware" subsection that retains the original hand-rolled `IMessageProcessingMiddleware` example and adds a parallel `ISendMessageMiddleware` example using the new `SendContext` shape.

## Testing

### Unit tests

`ServiceConnect.UnitTests`:

- Port `BlockingSendMiddleware`, `RecordingSendMiddleware`, `ShortCircuitSendMiddleware`, `HeaderAddingSendMiddleware`, `TestSendMiddleware` to the new `(SendContext, next, ct)` shape.
- `SendMessagePipelineTests` — assert `SendContext.Message` is the same instance the caller passed; assert each `SendOperation` discriminator is correctly populated for the three call-site flavours.

`ServiceConnect.Telemetry.Tests` (new test project, or `ServiceConnect.UnitTests/Telemetry/`):

- `TelemetrySendMiddlewareTests` — for each `SendOperation`, build a `SendContext`, invoke the middleware, assert: correct `ActivitySource` was used, span carries correct tags (destination, message-id, conversation-id, routing-key when present), `EnrichWithMessage` is invoked with the caller's `Message`, exception path calls `SetError` and rethrows.
- `TelemetryProcessingMiddlewareTests` — symmetric for the consume side; covers both `Success = false` (with exception) and thrown exception paths.
- `TelemetryBuilderExtensionsTests` — `AddTelemetry()` registers options, both middleware as singletons, inserts both in the pipeline configuration, and updates `ServiceConnectActivitySource.Options`.

### E2E tests

`ServiceConnect.EndToEndTests`:

- `TelemetryE2ETests` — start a bus with `AddTelemetry()`, register an in-process `ActivityListener`, publish one message, await consumption, assert: one publish span and one consume span exist, share TraceId, `consume.ParentSpanId == publish.SpanId`, both spans contain expected tags. Mirror for `SendAsync`. Framework-level analog of the sample's `run.sh` assertion.
- Port `HeaderAddingSendMiddleware` in `MiddlewarePipelineE2ETests` to `SendContext`.

### Sample-level smoke

The `examples/Telemetry/run.sh` trace-correlation assertion is itself a regression test. Existing sample CI infra runs all samples; this is automatic.

## Migration / breaking changes

| What | Before | After | Mitigation |
|---|---|---|---|
| `ISendMessageMiddleware.ProcessAsync` signature | `(Type, byte[], IDictionary<string,string>, string?, SendMessageDelegate, CancellationToken)` | `(SendContext, SendMessageDelegate, CancellationToken)` | Tests-only consumer in this repo; release-notes entry shows the trivial port. |
| `SendMessageDelegate` signature | `(Type, byte[], IDictionary<string,string>, string?, CancellationToken) → Task` | `(SendContext, CancellationToken) → Task` | Same — internal pipeline + tests only. |
| `ISendMessagePipeline.ExecutePublish/SendMessagePipelineAsync` signatures | loose-tuple params | `(SendContext, ct)` | Internal-ish; no external consumers in the codebase. |

The `ServiceConnectActivitySource.Options` and `MessagingSystemAttributes` setters were **already internal** before this work, so they require no migration story — the change is conceptual: there is now a supported configuration path (`builder.AddTelemetry(opts => ...)`) rather than relying on `InternalsVisibleTo` for unit tests.

Release-notes entry under the next v7 release: a "Breaking changes" subsection listing the three `ISendMessageMiddleware`/`SendMessageDelegate`/`ISendMessagePipeline` signature changes with a one-line port for each.

## Out-of-scope follow-ups

- A request/reply-specific `ActivitySource` (currently `Operation = Request` is bridged to the Send activity source). The `SendOperation` enum gives us room to split this later without another contract change.
- Auto-disposal of `ActivityListener` resources in the sample on Ctrl-C. Not relevant to a sample whose job is to publish-and-exit / consume-until-killed.
- Migrating any future production middleware (logging, retry) to the richer `SendContext` shape. Today none exist; this spec only forces the contract change.
