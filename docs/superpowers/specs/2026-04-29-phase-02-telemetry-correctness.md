# Phase 02 — Telemetry instrumentation correctness (spec)

**Date:** 2026-04-29
**Branch:** `v7-clean-architecture` (single branch — Phase 2 lands on top of Phase 1, which already merged here)
**Phase doc:** [`consolidated-issues/phases/phase-02-telemetry-correctness.md`](../../../consolidated-issues/phases/phase-02-telemetry-correctness.md)
**Status:** approved by user, ready for implementation plan

## Background

Phase 2 of the consolidated bug backlog covers the entirety of `ServiceConnect.Telemetry`'s correctness defects. Verification on this branch (2026-04-29, against the post-Phase-1 HEAD) confirmed every finding is still real:

| ID | Defect | File / line |
|---|---|---|
| C6 | Activity leaks if `TryEnrich` rethrows `OperationCanceledException` | [ServiceConnectActivitySource.cs:90, :152, :231](../../../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L90) |
| C7 | Full message-body copy on every consume, before listener check | [TelemetryProcessingMiddleware.cs:28](../../../src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs#L28) |
| H4 | `Options` and `MessagingSystemAttributes` are static-mutable cross-instance state | [ServiceConnectActivitySource.cs:16-20](../../../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L16-L20), [TelemetryBuilderExtensions.cs:43](../../../src/ServiceConnect.Telemetry/TelemetryBuilderExtensions.cs#L43) |
| L11 | `StartActivityWithLink` ignores parent context — Publish/Send always start a new trace root | [ServiceConnectActivitySource.cs:387-388](../../../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L387-L388) |
| L12 | `ActivityContext.TryParse` discards bool result | [ServiceConnectActivitySource.cs:104](../../../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L104) |
| L13 | `InjectTraceContext` runs twice on every publish/send | [ServiceConnectActivitySource.cs:50, :88, :168, :222](../../../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L50) |
| L14 | `InjectHeader` silent no-op for non-`IDictionary<string,string>` carrier | [ServiceConnectActivitySource.cs:335-341](../../../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L335-L341) |
| L15 | `TelemetryProcessingMiddleware` skips error tagging when `result.Success==false` but `result.Exception==null` | [TelemetryProcessingMiddleware.cs:37-40](../../../src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs#L37-L40) |
| L16 | `TelemetrySendMiddleware._options` field is dead code | [TelemetrySendMiddleware.cs:12](../../../src/ServiceConnect.Telemetry/TelemetrySendMiddleware.cs#L12) |
| L17 | High-cardinality user-controlled tags without bounds | [ServiceConnectActivitySource.cs:80, :132, :142, :213](../../../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L80) |
| (smaller) | Three separate `ActivitySource` instances vs OTel one-per-library convention | [ServiceConnectActivitySource.cs:38-40](../../../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L38-L40) |
| (smaller) | Static `ActivitySource` instances never disposed | same |
| (smaller) | Tag setters unguarded by `IsAllDataRequested` | [ServiceConnectActivitySource.cs:362-365, :394-397](../../../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L362-L365) and per-method tag sites |
| (smaller) | Raw exception message in span status (PII risk) | [ServiceConnectActivitySource.cs:254](../../../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L254) |
| (smaller) | `eventArgs.Message?.CorrelationId.ToString()` writes empty Guid | [ServiceConnectActivitySource.cs:65, :229](../../../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L65) |

The `OutgoingEventArgs._headers` ordinal-comparer concern (smaller item) is in `ServiceConnect.Interfaces`, not Telemetry — out of Phase 2 scope (Phase 12).

## Decisions (locked during brainstorming)

1. **Q1 — H4 ownership: B (per-bus, statics removed).** No process-wide static state. The race the H4 finding describes is fully resolved (no shared mutable state to race on).
2. **Q2 — API shape: A (separate parameters).** `Publish`/`Send`/`Consume` each gain `ServiceConnectInstrumentationOptions options` and `IMessagingSystemAttributes attributes` parameters rather than bundling into a context object.
3. **Q3 — Scope: C (everything Phase 2 lists).** All 14 items in this phase.

## Scope

### In scope

- All 5 critical/high findings (C6, C7, H4) listed above.
- All 6 low findings (L11–L16; L17 listed separately because it requires a new option).
- L17 — `MaxTagValueLength` option with truncation at every user-controlled `SetTag` site.
- Smaller items (ActivitySource consolidation, disposal, `IsAllDataRequested` guards, exception-message sanitiser, empty-Guid skip).
- Documentation sweep across telemetry reference, observability learn-page, sample, release notes.

### Out of scope (deferred to other phases)

- `OutgoingEventArgs._headers` ordinal-comparer issue (Phase 12 — Interfaces project).
- Public `IsConsumeEnabled` / `HasConsumeListeners` helper as part of the user-facing API (the helpers exist as `internal` for the C7 fix but are not advertised).

## Architecture overview

After Phase 2:

```
ServiceConnectBuilder
   │
   └─ AddTelemetry(opts => ...)  ──┐
                                    ├─ services.AddSingleton(options)
                                    ├─ services.TryAddSingleton<IMessagingSystemAttributes, RabbitMqMessagingSystemAttributes>()
                                    ├─ services.AddSingleton<TelemetrySendMiddleware>()
                                    └─ services.AddSingleton<TelemetryProcessingMiddleware>()

TelemetrySendMiddleware(options, attributes)
   ├─ Publish(eventArgs, options, attributes, parentContext)   → ServiceConnectActivitySource
   └─ Send(eventArgs, options, attributes, parentContext)      → ServiceConnectActivitySource

TelemetryProcessingMiddleware(options, attributes)
   ├─ IsConsumeTelemetryEnabled(options)?                      → ServiceConnectActivitySource (gate)
   └─ Consume(eventArgs, options, attributes)                  → ServiceConnectActivitySource

ServiceConnectActivitySource (static class, single ActivitySource)
   ├─ One ActivitySource named "ServiceConnect.Bus"
   ├─ No static Options or MessagingSystemAttributes
   ├─ Publish/Send/Consume take options + attributes as parameters
   ├─ SetError takes options (for ExceptionMessageSanitiser)
   └─ TryGetExistingContext, internal IsConsumeTelemetryEnabled
```

The activity-source class stays static — it's a procedural facade over `ActivitySource.StartActivity`. Removing the static state from it doesn't require making it instance-based; it just needs the configuration to flow through method parameters.

## H4 — public API change (breaking)

### `ServiceConnectActivitySource.cs`

**Removed:**

```csharp
public static ServiceConnectInstrumentationOptions Options { get; internal set; } = new();
public static IMessagingSystemAttributes MessagingSystemAttributes { get; internal set; } = new RabbitMqMessagingSystemAttributes();
```

**Public method signature changes:**

```csharp
// before
public static Activity? Publish(PublishEventArgs eventArgs, ActivityContext linkedContext = default);
public static Activity? Send(SendEventArgs eventArgs, ActivityContext linkedContext = default);
public static Activity? Consume(ConsumeEventArgs eventArgs);
public static void SetError(Activity? activity, Exception exception);

// after
public static Activity? Publish(PublishEventArgs eventArgs, ServiceConnectInstrumentationOptions options, IMessagingSystemAttributes attributes, ActivityContext parentContext = default);
public static Activity? Send(SendEventArgs eventArgs, ServiceConnectInstrumentationOptions options, IMessagingSystemAttributes attributes, ActivityContext parentContext = default);
public static Activity? Consume(ConsumeEventArgs eventArgs, ServiceConnectInstrumentationOptions options, IMessagingSystemAttributes attributes);
public static void SetError(Activity? activity, Exception exception, ServiceConnectInstrumentationOptions options);
```

`TryGetExistingContext(headers, out context)` is unchanged — it doesn't read either piece of removed state.

The `linkedContext` parameter is renamed to `parentContext` to reflect the L11 fix (it now sets the activity's parent rather than attaching as a sibling-link).

**New internal helpers** (used by middlewares, not part of the public API):

```csharp
internal static bool IsPublishTelemetryEnabled(ServiceConnectInstrumentationOptions options);
internal static bool IsSendTelemetryEnabled(ServiceConnectInstrumentationOptions options);
internal static bool IsConsumeTelemetryEnabled(ServiceConnectInstrumentationOptions options);
```

Each returns `options.EnableXTelemetry && _activitySource.HasListeners()`.

**Internal test seams** updated:

```csharp
internal static void InvokeTryEnrichForTest(Activity activity, Message? message, ServiceConnectInstrumentationOptions options);
internal static void InvokeTryEnrichForTest(Activity activity, byte[]? bytes, ServiceConnectInstrumentationOptions options);
```

The third parameter lets tests pass a controlled options instance with a controlled enricher delegate.

### Single `ActivitySource` (smaller-item resolution)

Three sources collapse to one:

```csharp
// before
private static readonly ActivitySource _publishActivitySource = new(PublishActivitySourceName, Version?.ToString() ?? "0.0.0");
private static readonly ActivitySource _consumeActivitySource = new(ConsumeActivitySourceName, Version?.ToString() ?? "0.0.0");
private static readonly ActivitySource _sendActivitySource = new(SendActivitySourceName, Version?.ToString() ?? "0.0.0");

// after
private static readonly ActivitySource _activitySource = new(ActivitySourceName, Version?.ToString() ?? "0.0.0");
```

`ActivitySourceName` stays as `"ServiceConnect.Bus"`. The three `PublishActivitySourceName` / `ConsumeActivitySourceName` / `SendActivitySourceName` public constants are **removed** (breaking — but they only existed so users could `AddSource(...)` against three separate names; with one source, the OTel registration becomes `AddSource("ServiceConnect.Bus")`).

The activities still differ by `ActivityKind` (`Producer` for Publish/Send, `Consumer` for Consume) and by the `messaging.operation` tag. Per-direction enablement remains via `EnablePublishTelemetry` / `EnableConsumeTelemetry` / `EnableSendTelemetry` flags on `ServiceConnectInstrumentationOptions`, which gate inside the new `IsXTelemetryEnabled` helpers.

### `Shutdown()` — collectible-ALC support

```csharp
/// <summary>
/// Disposes the underlying <see cref="ActivitySource"/>. Call only when unloading
/// the assembly in a collectible <c>AssemblyLoadContext</c>; for normal long-running
/// processes the source lives for process lifetime and disposal is unnecessary.
/// </summary>
public static void Shutdown() => _activitySource.Dispose();
```

`static class` can't implement `IDisposable`, so `Shutdown()` is the documented opt-in.

### `TelemetryBuilderExtensions.AddTelemetry`

```csharp
public static ServiceConnectBuilder AddTelemetry(
    this ServiceConnectBuilder builder,
    Action<ServiceConnectInstrumentationOptions>? configure = null)
{
    ArgumentNullException.ThrowIfNull(builder);

    var options = new ServiceConnectInstrumentationOptions();
    configure?.Invoke(options);

    builder.AddRegistration(services =>
    {
        services.AddSingleton(options);
        services.TryAddSingleton<IMessagingSystemAttributes, RabbitMqMessagingSystemAttributes>();
        services.AddSingleton<TelemetrySendMiddleware>();
        services.AddSingleton<TelemetryProcessingMiddleware>();
    });

    builder.ConfigurePipeline(p =>
    {
        p.SendMessageMiddleware.Insert(0, typeof(TelemetrySendMiddleware));
        p.MessageProcessingMiddleware.Insert(0, typeof(TelemetryProcessingMiddleware));
    });

    return builder;
}
```

The `ServiceConnectActivitySource.Options = options` line at the end of the current implementation is **removed**. `TryAddSingleton<IMessagingSystemAttributes, ...>` is the user override hook: a caller who registered a Kafka/Azure-Service-Bus `IMessagingSystemAttributes` *before* `AddTelemetry` keeps theirs; otherwise the RabbitMQ default applies.

### `TelemetrySendMiddleware`

```csharp
internal sealed class TelemetrySendMiddleware(
    ServiceConnectInstrumentationOptions options,
    IMessagingSystemAttributes attributes) : ISendMessageMiddleware
{
    private readonly ServiceConnectInstrumentationOptions _options = options;
    private readonly IMessagingSystemAttributes _attributes = attributes;

    public async Task ProcessAsync(SendContext context, SendMessageDelegate next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        Activity? activity = context.Operation switch
        {
            SendOperation.Publish => ServiceConnectActivitySource.Publish(
                new PublishEventArgs { /* ... */ }, _options, _attributes),
            SendOperation.Send or SendOperation.Request => ServiceConnectActivitySource.Send(
                new SendEventArgs { /* ... */ }, _options, _attributes),
            _ => null,
        };

        try
        {
            await next(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ServiceConnectActivitySource.SetError(activity, ex, _options);
            throw;
        }
        finally
        {
            activity?.Dispose();
        }
    }
}
```

The `_options` field that's currently dead code (L16) is now load-bearing. `_attributes` is added.

### `TelemetryProcessingMiddleware`

```csharp
internal sealed class TelemetryProcessingMiddleware(
    ServiceConnectInstrumentationOptions options,
    IMessagingSystemAttributes attributes) : IMessageProcessingMiddleware
{
    private readonly ServiceConnectInstrumentationOptions _options = options;
    private readonly IMessagingSystemAttributes _attributes = attributes;

    public async Task<ConsumeEventResult> ProcessAsync(...)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(next);

        Activity? activity = null;
        if (ServiceConnectActivitySource.IsConsumeTelemetryEnabled(_options))
        {
            var args = new ConsumeEventArgs
            {
                Message = envelope.Body.ToArray(),  // C7: only allocated when needed
                Type = messageType.FullName ?? string.Empty,
                Headers = headers,
            };
            activity = ServiceConnectActivitySource.Consume(args, _options, _attributes);
        }

        try
        {
            var result = await next(messageBytes, messageType, message, headers, envelope, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                if (result.Exception is not null)
                {
                    ServiceConnectActivitySource.SetError(activity, result.Exception, _options);
                }
                else
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "Dispatch returned Success=false without an exception");
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            ServiceConnectActivitySource.SetError(activity, ex, _options);
            throw;
        }
        finally
        {
            activity?.Dispose();
        }
    }
}
```

Three changes from the current code: H4 fields, C7 gate, L15 fix (synthetic error description when `Success=false` without exception).

## C6 — activity-leak fix

Three call sites (`Publish`, `Send`, `Consume`) get a `try { ... } catch { activity.Dispose(); throw; }` wrap around the post-`StartActivity` work. Pattern at every call site:

```csharp
public static Activity? Publish(
    PublishEventArgs eventArgs,
    ServiceConnectInstrumentationOptions options,
    IMessagingSystemAttributes attributes,
    ActivityContext parentContext = default)
{
    Activity? activity = StartActivityWithParent(_activitySource, ActivityKind.Producer, options.EnablePublishTelemetry, attributes, "publish", parentContext);

    if (activity is null)
    {
        // No activity started — propagate ambient W3C context so downstream consumers
        // can still link to whatever outer (ASP.NET / OTel) span is active.
        InjectTraceContext(Activity.Current, eventArgs.Headers);
        return null;
    }

    try
    {
        // L13: single inject. With activity non-null, the new span's context
        // supersedes the ambient one — downstream consumers link to our span.
        InjectTraceContext(activity, eventArgs.Headers);

        if (activity.IsAllDataRequested)   // smaller-item: tag guard
        {
            // ... display name, destination, routing key, MessageId, MessageConversationId tags ...
        }

        TryEnrich(activity, eventArgs.Message, options);
        return activity;
    }
    catch
    {
        activity.Dispose();
        throw;
    }
}
```

Same shape applied in `Send`. **`Consume` is symmetric but does not inject** — it *extracts* the W3C context from inbound headers (the consumer side of the trace boundary). The Consume wrap covers tag-setting + `TryEnrich`:

```csharp
public static Activity? Consume(
    ConsumeEventArgs eventArgs,
    ServiceConnectInstrumentationOptions options,
    IMessagingSystemAttributes attributes)
{
    DistributedContextPropagator.Current.ExtractTraceIdAndState(eventArgs.Headers, ExtractTraceIdAndState, out string? traceId, out string? traceState);
    if (!ActivityContext.TryParse(traceId, traceState, out ActivityContext parentContext))
    {
        parentContext = default;   // L12: explicit fallback
    }

    Activity? activity = StartActivityWithParent(_activitySource, ActivityKind.Consumer, options.EnableConsumeTelemetry, attributes, "receive", parentContext);
    if (activity is null)
    {
        return null;
    }

    try
    {
        if (activity.IsAllDataRequested)
        {
            // ... display name, destination, MessageId, MessageConversationId tags ...
        }

        if (eventArgs.Message is not null)
        {
            activity.SetTag(MessagingAttributes.MessagingBodySize, eventArgs.Message.Length);
            TryEnrich(activity, eventArgs.Message, options);
        }
        return activity;
    }
    catch
    {
        activity.Dispose();
        throw;
    }
}
```

The L13 fix applies to `Publish` and `Send` only (those are the inject sites). `Consume`'s only L1*-related change is L12 (the explicit `TryParse` bool capture).

## C7 — body-copy gate

In `TelemetryProcessingMiddleware.ProcessAsync`, the `envelope.Body.ToArray()` call is gated on `ServiceConnectActivitySource.IsConsumeTelemetryEnabled(_options)` — an internal helper that returns `_options.EnableConsumeTelemetry && _activitySource.HasListeners()`. When no listener is attached and/or consume telemetry is disabled, the body is not copied.

The gate covers the full `ConsumeEventArgs` allocation, not just the body — the args object exists only to be passed into `Consume(...)`, so allocating it when no activity will be created is wasteful regardless.

## Low items detail

### L11 — `StartActivityWithLink` ignores parent context

The `links: links` mechanism is replaced with `parentContext: parentContext`. `StartActivityWithLink` is renamed to `StartActivityWithParent` (already exists for Consume) — the two helpers consolidate. The parameter `linkedContext` is renamed to `parentContext` everywhere.

When `parentContext == default(ActivityContext)` (the common case from the middleware), `StartActivity` automatically falls back to `Activity.Current`, which is the right ambient-trace behaviour. The `links` argument is dropped entirely.

### L12 — TryParse bool result captured

```csharp
if (!ActivityContext.TryParse(traceId, traceState, out ActivityContext parentContext))
{
    parentContext = default;
}
```

Functionally identical (`parentContext` is `default` on parse failure either way) but the explicit branch documents intent.

### L13 — Single `InjectTraceContext`

The two-inject pattern collapses to one. Two code paths through `Publish`/`Send`:

- **Activity is null** (no listener / disabled): inject ambient context (`Activity.Current`) before returning `null`. Downstream consumers can link to whatever outer span is active.
- **Activity is non-null:** inject the new span's context inside the C6 wrap. Composes with C6 — if the inject itself throws, the wrap disposes the activity.

Net result: exactly one inject per call (vs. the current two), and the C6 wrap protects the activity-path inject.

### L14 — `InjectHeader` once-per-process warning

```csharp
private static int _warnedAboutCarrierShape;

private static void InjectHeader(object? carrier, string fieldName, string fieldValue)
{
    if (carrier is IDictionary<string, string> headers)
    {
        headers[fieldName] = fieldValue;
        return;
    }

    if (Interlocked.CompareExchange(ref _warnedAboutCarrierShape, 1, 0) == 0)
    {
        System.Diagnostics.Trace.TraceWarning(
            "ServiceConnectActivitySource.InjectHeader: unsupported carrier type {0}; trace context not propagated.",
            carrier?.GetType().FullName ?? "<null>");
    }
}
```

### L15 — Error-tagging on `Success=false` without exception

Already shown in the `TelemetryProcessingMiddleware` block above. When `!result.Success && result.Exception is null`, the activity gets `ActivityStatusCode.Error` with description `"Dispatch returned Success=false without an exception"` — no `SetError` call (no exception to record) but the status reflects the failure.

### L16 — `_options` becomes load-bearing

Resolved by H4. The field is now read by the `Publish`/`Send` call sites in the middleware.

### L17 — `MaxTagValueLength` truncation

New option:

```csharp
public sealed class ServiceConnectInstrumentationOptions
{
    // ... existing properties ...

    /// <summary>
    /// Maximum length, in characters, of user-controlled string values written as activity tags
    /// (destination, routing key, MessageId, conversation id). Values exceeding this length are
    /// truncated. Defaults to 256. Set to <see cref="int.MaxValue"/> to disable truncation.
    /// </summary>
    public int MaxTagValueLength { get; set; } = 256;
}
```

Centralised helper in `ServiceConnectActivitySource`:

```csharp
private static string Truncate(string? value, int maxLength)
{
    if (value is null) return string.Empty;
    return value.Length <= maxLength ? value : value.Substring(0, maxLength);
}
```

Applied at every `SetTag` site for user-controlled values: destination, routing key, MessageId, conversation id (CorrelationId). System-attribute tags (`MessagingSystem`, `ProtocolName`, `MessagingOperation`) are not user-controlled — no truncation needed.

## Smaller items detail

### Single `ActivitySource`

Three sources → one named `"ServiceConnect.Bus"`. The three public constants `PublishActivitySourceName`, `ConsumeActivitySourceName`, `SendActivitySourceName` are removed. OTel registration simplifies to `AddSource("ServiceConnect.Bus")`.

### `Shutdown()` static method

Already shown. `static class` can't be `IDisposable`; `Shutdown()` is the documented opt-in for collectible-ALC scenarios.

### `IsAllDataRequested` guards

Tag-setting blocks in `Publish`/`Send`/`Consume` and the system-attribute tags in `StartActivityWithParent` are wrapped in `if (activity.IsAllDataRequested) { ... }`. The display-name set is unconditional (it's an Activity property, not a tag, and OTel exporters always read it).

### `ExceptionMessageSanitiser` option

```csharp
public sealed class ServiceConnectInstrumentationOptions
{
    // ... existing properties ...

    /// <summary>
    /// Optional sanitiser invoked on exception messages before they are written to
    /// activity status descriptions and "exception.message" event tags. Use to redact
    /// PII or sensitive content. Returns the message to record. If null (default),
    /// the raw <see cref="Exception.Message"/> is recorded.
    /// </summary>
    public Func<Exception, string>? ExceptionMessageSanitiser { get; set; }
}
```

`SetError(Activity?, Exception, ServiceConnectInstrumentationOptions)` reads `options.ExceptionMessageSanitiser` and applies it to both:
- The `ActivityStatusCode.Error` description.
- The `exception.message` field of the `OTel exception` event.

When the sanitiser is set, `SetError` opts out of `Activity.AddException(exception)` (which would re-record the unsanitised message) and uses the manual `ActivityEvent` path on .NET 9+ as well.

### Empty-Guid CorrelationId skip

```csharp
if (eventArgs.Message?.CorrelationId is { } cid && cid != Guid.Empty)
{
    activity.SetTag(MessagingAttributes.MessageConversationId, Truncate(cid.ToString(), options.MaxTagValueLength));
}
```

Applied at lines 65 (`Publish`) and 229 (`Send`). The `Truncate` call composes with L17.

## Test strategy

### New tests (in `src/ServiceConnect.UnitTests/Telemetry/`)

**C6 (3 tests):**
- `Publish_EnricherThrowsOce_DisposesActivity` — register an enricher that throws OCE, call `Publish`, assert `Activity.Current` is null after the OCE escapes. Pre-call setup: start an outer ambient activity so the test can detect parent-restore breakage.
- `Send_EnricherThrowsOce_DisposesActivity` — same shape against `Send`.
- `Consume_EnricherThrowsOce_DisposesActivity` — same shape against `Consume`.
- Variants with `InvalidOperationException` (non-OCE) for completeness — same dispose-on-throw guarantee.

**C7 (1 test):**
- `ProcessingMiddleware_NoListener_DoesNotCopyBody` — register the middleware against a no-listener `ActivitySource` configuration; pass an `Envelope` whose `Body` getter throws on access; assert the middleware does NOT trip the throw.

**H4 (3 tests):**
- `AddTelemetry_TwoBuses_ProduceDistinctOptionsInstances` — `Assert.NotSame` after resolving from each `IServiceProvider`.
- `AddTelemetry_PerBusEnricher_DoesNotLeakAcrossInstances` — two bus instances with different enrichers; each enricher invoked exactly once for its own bus's traffic.
- `AddTelemetry_UserRegisteredAttributesWin` — register a custom `IMessagingSystemAttributes` before `AddTelemetry`; assert the user's instance appears on emitted activity tags.

**L11 (2 tests):**
- `Publish_NonDefaultParentContext_SetsActivityParent` — pass a non-default `parentContext`; assert `activity.Parent` (or `activity.ParentSpanId`) matches the supplied context.
- `Publish_DefaultParentContext_FallsBackToCurrent` — start an outer ambient `Activity`, call `Publish` with `default` `parentContext`, assert the published activity is parented on the ambient one.

**L12 (1 test):**
- `Consume_MalformedTraceparent_FallsBackToActivityCurrent` — headers with malformed `traceparent`; assert the consume activity is parented on `Activity.Current`, not on a fabricated invalid context.

**L13 (1 test):**
- `Publish_InjectTraceContext_FiresOnce` — custom `DistributedContextPropagator` that counts `Inject` calls; assert exactly one inject per `Publish`.

**L14 (2 tests):**
- `InjectHeader_NonStringDictionaryCarrier_LogsOnceAndNoOps` — pass a non-`IDictionary<string,string>` carrier via the internal test seam; assert `Trace.TraceWarning` fires once across multiple invocations.
- `InjectHeader_StringDictionaryCarrier_NoWarning` — correct shape; assert no warning fires.

**L15 (1 test):**
- `ProcessingMiddleware_ResultSuccessFalseNoException_TagsActivityError` — `next` returns `result.Success=false, result.Exception=null`; assert the activity has `ActivityStatusCode.Error` with the synthetic description string.

**L17 (2 tests):**
- `Publish_HeaderValueExceedsMax_TruncatesTag` — header value longer than `MaxTagValueLength`; assert resulting tag value's length equals `MaxTagValueLength`.
- `Publish_HeaderValueWithinMax_TagVerbatim` — header value at or under the limit; assert tag set verbatim.

**Smaller-item tests:**
- `SetError_WithSanitiser_AppliesToStatusAndEventTag` — register a sanitiser returning `"REDACTED"`; trigger an exception; assert `ActivityStatusCode.Error` description is `"REDACTED"` and the `exception.message` event tag is `"REDACTED"`.
- `Publish_SampleDroppedActivity_DoesNotSetUserTags` — register a `PropagationData` listener (`IsAllDataRequested == false`); trigger publish; assert per-message tags are NOT set.
- `Publish_EmptyCorrelationId_DoesNotSetTag` — message with `CorrelationId = Guid.Empty`; assert `messaging.message.conversation_id` tag is NOT set.
- `ActivitySource_SingleSourceName_IsServiceConnectBus` — verify `Publish`/`Send`/`Consume` all emit on the same `"ServiceConnect.Bus"` source.

**Total new tests: ~17.**

### Existing test migration

Existing tests under `src/ServiceConnect.UnitTests/Telemetry/` that write to `ServiceConnectActivitySource.Options` directly need migration to the new parameter shape. The implementing agent reads each existing test, identifies the migration shape, and updates inline.

### Build/test safety

Per CLAUDE.md, this machine has crashed when running unconstrained whole-solution `dotnet build`/`test`. The cgroup wrapper at `~/.local/bin/dotnet` is the safety net but per-csproj invocations are still preferred. Plan must use:

```bash
dotnet build src/ServiceConnect.Telemetry/ServiceConnect.Telemetry.csproj
dotnet build src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj
dotnet test  src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Telemetry|FullyQualifiedName~ActivitySource" -m:1
```

`-m:1` is required (cgroup `TasksMax=200` is tight under MSBuild parallel-csc). No whole-solution invocations.

## Documentation updates

- `website/src/content/docs/reference/telemetry/` — main telemetry reference. Update for:
  - New parameter shape on `Publish`/`Send`/`Consume`/`SetError` (breaking).
  - Removal of static `Options` and `MessagingSystemAttributes`.
  - DI registration story (`AddTelemetry` registers options + `IMessagingSystemAttributes` via `TryAddSingleton`).
  - New options: `ExceptionMessageSanitiser`, `MaxTagValueLength`.
  - Three sources → one: OTel listener config simplifies to `AddSource("ServiceConnect.Bus")`.
  - `Shutdown()` documentation for collectible-ALC scenarios.
- `website/src/content/docs/learn/operations/` — observability/telemetry/trace sections. Update OTel listener-registration examples.
- `examples/Telemetry/README.md` and `examples/Telemetry/src/...` — full sweep. Sample registers OTel against the old three-source names; rewrite to single-source registration. Any sample code reading `ServiceConnectActivitySource.Options` directly migrates to DI-injected options.
- `website/src/content/docs/releases.mdx` — v7 entry: breaking activity-source API change, static-state removal, new options, single-source consolidation.
- `README.md` (repo root) — only if telemetry features are highlighted.
- `examples/README.md` — confirm Telemetry sample description still matches.

## Verification gate before merge

1. All new tests pass; all migrated existing telemetry tests pass.
2. Per-csproj build of `ServiceConnect.Telemetry`, `ServiceConnect`, `ServiceConnect.UnitTests`, and `examples/Telemetry/src/...` succeeds.
3. Repo-wide grep for `ServiceConnectActivitySource.Options` and `ServiceConnectActivitySource.MessagingSystemAttributes` is clean.
4. Astro site builds (`npm --prefix website run build`).
5. `examples/Telemetry/run.sh` smoke-tests against real RabbitMQ — verify spans emit on the new single source and that the OTel sample exporter receives them.
6. Optional code-review pass via `superpowers:requesting-code-review`.

## Rollout

**Single PR.** All Phase 2 changes are in `ServiceConnect.Telemetry` plus tests + docs + sample. The breaking activity-source surface change must land atomically — a partial rollout would leave the codebase non-compilable. Suggested commit shape:

1. Remove statics + collapse to single `ActivitySource` + add internal `IsXTelemetryEnabled` helpers.
2. Update `Publish`/`Send`/`Consume`/`SetError` signatures + private helpers.
3. Update `TelemetrySendMiddleware` and `TelemetryProcessingMiddleware`.
4. Update `TelemetryBuilderExtensions.AddTelemetry`.
5. Apply L11–L17 + smaller items.
6. Tests (one or more commits).
7. Docs + sample updates.
8. Final repo-wide grep + Astro build.

Each step buildable on its own; reviewers can step through sequentially.

## Out of scope

- `OutgoingEventArgs._headers` ordinal-comparer issue (Phase 12 — Interfaces project).
- Public `IsConsumeEnabled` / `HasConsumeListeners` helper as part of the user-facing API. The internal helpers exist for the C7 fix but aren't advertised; users still register `ActivityListener` against the source directly.
- Renaming `ServiceConnectInstrumentationOptions` or other public types — out of scope.
- Adding new instrumentation surfaces (metrics, logs) — Phase 2 is correctness for the existing activity-only instrumentation.

## Open questions

None at spec time. All design questions were settled during brainstorming.
