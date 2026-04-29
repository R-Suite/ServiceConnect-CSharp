# Phase 02 — Telemetry instrumentation correctness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sweep `ServiceConnect.Telemetry` for correctness defects: kill the activity-leak on enricher OCE (C6), gate body copies on listener presence (C7), remove static-mutable `Options`/`MessagingSystemAttributes` in favour of per-bus DI (H4), plus all L11–L17 + smaller items in one pass.

**Architecture:** Breaking change to the public surface of `ServiceConnectActivitySource` — `Publish`/`Send`/`Consume`/`SetError` take `ServiceConnectInstrumentationOptions options` and `IMessagingSystemAttributes attributes` as parameters; the static fields go away. The middlewares (`TelemetrySendMiddleware`, `TelemetryProcessingMiddleware`) accept both via DI and pass them through. Three activity sources collapse into one named `"ServiceConnect.Bus"`; users register listeners via `AddSource("ServiceConnect.Bus")`. Two new options (`MaxTagValueLength` for cardinality bounds, `ExceptionMessageSanitiser` for PII redaction) round out the polish.

**Tech Stack:** .NET (multi-target net8.0/net10.0), `System.Diagnostics.ActivitySource`/`ActivityListener`, xUnit + Moq for unit tests, Astro/Starlight for docs, RabbitMQ via Docker for sample smoke test. Test runner: `dotnet test` with `--filter` (per-csproj only — see [build/test safety](#buildtest-safety) below).

**Spec:** [`docs/superpowers/specs/2026-04-29-phase-02-telemetry-correctness.md`](../specs/2026-04-29-phase-02-telemetry-correctness.md).

---

## Build/test safety

This machine has crashed when running unconstrained whole-solution `dotnet build` / `dotnet test` against this codebase (CLAUDE.md has the full incident analysis). A wrapper at `~/.local/bin/dotnet` re-execs every `dotnet` invocation under a systemd cgroup with `CPUQuota=800%`, `MemoryMax=8G`, `MemorySwapMax=0`, `TasksMax=200` and is the safety net. **Even with the wrapper, every command in this plan is per-csproj.** Add `-m:1` to `dotnet test` invocations to serialize MSBuild and stay within `TasksMax`. Never run whole-solution `dotnet build` / `dotnet test` / `dotnet format`. If a build/test hits the cgroup's 8 GB or 200-task ceiling, fix the build, do not lift the fence.

---

## File structure

### Modified — `src/ServiceConnect.Telemetry/`

- `ServiceConnectActivitySource.cs` — single `ActivitySource`; new method signatures; C6 wrap; L11 / L12 / L13 / L14 fixes; `IsAllDataRequested` guards; `Truncate` helper; empty-Guid skip; `ExceptionMessageSanitiser` wiring; `Shutdown()`. Internal helpers consolidated.
- `ServiceConnectInstrumentationOptions.cs` — add `MaxTagValueLength`, `ExceptionMessageSanitiser`.
- `TelemetryBuilderExtensions.cs` — `TryAddSingleton<IMessagingSystemAttributes>`; remove static-write line.
- `TelemetrySendMiddleware.cs` — DI-injected `options` + `attributes`; pass through.
- `TelemetryProcessingMiddleware.cs` — DI-injected `options` + `attributes`; C7 gate; L15 error-tag fix.

### Modified — `src/ServiceConnect.UnitTests/Telemetry/`

- `ServiceConnectActivitySourceTests.cs` (744 lines) — migrate every test to new API surface.
- `TelemetryBuilderExtensionsTests.cs` — migrate; add H4 multi-bus tests.
- `TelemetryProcessingMiddlewareTests.cs` — migrate; add C7 and L15 tests.
- `TelemetrySendMiddlewareTests.cs` — migrate.
- `TryEnrichTests.cs` — migrate (gain `options` parameter on `InvokeTryEnrichForTest`).

### Modified — website + sample

- `website/src/content/docs/reference/telemetry/*.mdx` — full sweep for new API.
- `website/src/content/docs/learn/operations/*.mdx` — observability/telemetry sections.
- `website/src/content/docs/releases.mdx` — v7 telemetry entry.
- `website/src/content/docs/samples.mdx` — verify Telemetry sample entry still accurate.
- `examples/Telemetry/src/...` and `examples/Telemetry/README.md`, `run.sh`, `run.ps1` — sample sweep.
- `README.md` (root) — only if telemetry features are highlighted.
- `examples/README.md` — verify entry.

---

## Group A — API migration (no behavior changes)

### Task 1: Add new options to `ServiceConnectInstrumentationOptions`

**Files:**
- Modify: `src/ServiceConnect.Telemetry/ServiceConnectInstrumentationOptions.cs`

- [ ] **Step 1: Add `MaxTagValueLength` and `ExceptionMessageSanitiser` properties**

In `src/ServiceConnect.Telemetry/ServiceConnectInstrumentationOptions.cs`, after the existing `EnableSendTelemetry` property:

```csharp
    /// <summary>
    /// Maximum length, in characters, of user-controlled string values written as activity tags
    /// (destination, routing key, MessageId, conversation id). Values exceeding this length are
    /// truncated. Defaults to 256. Set to <see cref="int.MaxValue"/> to disable truncation.
    /// </summary>
    public int MaxTagValueLength { get; set; } = 256;

    /// <summary>
    /// Optional sanitiser invoked on exception messages before they are written to
    /// activity status descriptions and "exception.message" event tags. Use to redact
    /// PII or sensitive content. Returns the message to record. If null (default),
    /// the raw <see cref="Exception.Message"/> is recorded.
    /// </summary>
    public Func<Exception, string>? ExceptionMessageSanitiser { get; set; }
```

- [ ] **Step 2: Build to verify**

```bash
dotnet build src/ServiceConnect.Telemetry/ServiceConnect.Telemetry.csproj
```

Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.Telemetry/ServiceConnectInstrumentationOptions.cs
git commit -m "feat(telemetry): add MaxTagValueLength and ExceptionMessageSanitiser options"
```

---

### Task 2: Collapse three `ActivitySource`s into one + add `Shutdown` + internal `IsXTelemetryEnabled` helpers

**Files:**
- Modify: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`

This task removes the three suffixed source-name constants (breaking — but only Task 4's existing tests reference them, and that task migrates them). The new single source is named `"ServiceConnect.Bus"` (the existing `ActivitySourceName` value).

- [ ] **Step 1: Replace the three `ActivitySource` declarations with one**

In `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`, find the three field declarations (around lines 38-40):

```csharp
    private static readonly ActivitySource _publishActivitySource = new(PublishActivitySourceName, Version?.ToString() ?? "0.0.0");
    private static readonly ActivitySource _consumeActivitySource = new(ConsumeActivitySourceName, Version?.ToString() ?? "0.0.0");
    private static readonly ActivitySource _sendActivitySource = new(SendActivitySourceName, Version?.ToString() ?? "0.0.0");
```

Replace with a single field:

```csharp
    private static readonly ActivitySource _activitySource = new(ActivitySourceName, Version?.ToString() ?? "0.0.0");
```

Also delete the three public source-name constants (around lines 28-36):

```csharp
    public static readonly string PublishActivitySourceName = ActivitySourceName + ".Publish";
    public static readonly string ConsumeActivitySourceName = ActivitySourceName + ".Consume";
    public static readonly string SendActivitySourceName = ActivitySourceName + ".Send";
```

Keep `ActivitySourceName` as the only public source name. Update its XML doc to reflect single-source usage:

```csharp
    /// <summary>
    /// Gets the activity-source name used for all publish, send, and consume spans.
    /// Register listeners via <c>AddSource("ServiceConnect.Bus")</c>.
    /// </summary>
    public static readonly string ActivitySourceName = (typeof(ServiceConnectActivitySource).Assembly.GetName().Name ?? "ServiceConnect") + ".Bus";
```

- [ ] **Step 2: Add `Shutdown()` static method**

After the existing `Version` field (around line 22), add:

```csharp
    /// <summary>
    /// Disposes the underlying <see cref="ActivitySource"/>. Call only when unloading
    /// the assembly in a collectible <c>AssemblyLoadContext</c>; for normal long-running
    /// processes the source lives for process lifetime and disposal is unnecessary.
    /// </summary>
    public static void Shutdown() => _activitySource.Dispose();
```

- [ ] **Step 3: Add internal `IsXTelemetryEnabled` helpers**

Place these as `internal static` methods near the bottom of the class, before the `InvokeTryEnrichForTest` test seams:

```csharp
    internal static bool IsPublishTelemetryEnabled(ServiceConnectInstrumentationOptions options)
        => options.EnablePublishTelemetry && _activitySource.HasListeners();

    internal static bool IsSendTelemetryEnabled(ServiceConnectInstrumentationOptions options)
        => options.EnableSendTelemetry && _activitySource.HasListeners();

    internal static bool IsConsumeTelemetryEnabled(ServiceConnectInstrumentationOptions options)
        => options.EnableConsumeTelemetry && _activitySource.HasListeners();
```

- [ ] **Step 4: Update `Publish`/`Send`/`Consume` and the private start helpers to reference `_activitySource`**

In `Publish` (existing call at `StartActivityWithLink(_publishActivitySource, ...)` around line 53), change to `_activitySource`. Same in `Consume` (line 107: `_consumeActivitySource`) and `Send` (line 176: `_sendActivitySource`). The private `StartActivityWithParent` and `StartActivityWithLink` keep their `ActivitySource activitySource` parameter — Task 3 will rationalise.

This step is just a mechanical search-replace of `_publishActivitySource` / `_consumeActivitySource` / `_sendActivitySource` → `_activitySource`. The first argument to `StartActivityWithParent`/`StartActivityWithLink` becomes uniform.

- [ ] **Step 5: Build to verify the file compiles**

```bash
dotnet build src/ServiceConnect.Telemetry/ServiceConnect.Telemetry.csproj
```

Expected: this build will succeed *if* the test project doesn't reference the removed constants. If it does, the test build fails — that's a Task 4 problem; for now, only the Telemetry csproj must build.

```bash
dotnet build src/ServiceConnect.Telemetry/ServiceConnect.Telemetry.csproj
```

Expected: succeeds. (Test project will fail to build at this point — that's expected; Task 4 fixes it.)

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs
git commit -m "refactor(telemetry): collapse three ActivitySources into one + add Shutdown + IsXTelemetryEnabled helpers"
```

---

### Task 3: Migrate `Publish`/`Send`/`Consume`/`SetError` signatures + middleware DI flow + AddTelemetry

This is the load-bearing API migration. Done in one task because the changes are tightly coupled — any partial migration leaves the codebase non-compilable. **This task does NOT introduce any behavior changes** (no C6 wrap, no L11-L17 fixes, no truncation, no sanitiser wiring, no IsAllDataRequested guards). Those are tasks 5–17. This task is purely structural: thread options + attributes through the existing logic.

**Files:**
- Modify: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`
- Modify: `src/ServiceConnect.Telemetry/TelemetrySendMiddleware.cs`
- Modify: `src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs`
- Modify: `src/ServiceConnect.Telemetry/TelemetryBuilderExtensions.cs`

- [ ] **Step 1: Update `ServiceConnectActivitySource` public methods**

In `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`:

**Remove the static fields (lines 16, 20):**

```csharp
    public static ServiceConnectInstrumentationOptions Options { get; internal set; } = new();
    public static IMessagingSystemAttributes MessagingSystemAttributes { get; internal set; } = new RabbitMqMessagingSystemAttributes();
```

**Update `Publish` signature and body** to take options + attributes and pass through:

```csharp
    public static Activity? Publish(
        PublishEventArgs eventArgs,
        ServiceConnectInstrumentationOptions options,
        IMessagingSystemAttributes attributes,
        ActivityContext linkedContext = default)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(attributes);

        // Inject ambient trace context unconditionally so outer (ASP.NET / OTel) spans
        // propagate across the broker even when ServiceConnect's own spans are disabled.
        InjectTraceContext(Activity.Current, eventArgs.Headers);

        Activity? activity = StartActivityWithLink(
            _activitySource,
            ActivitySourceName,
            ActivityKind.Producer,
            options.EnablePublishTelemetry,
            attributes,
            "publish",
            linkedContext);

        if (activity is null)
        {
            return null;
        }

        activity.SetTag(MessagingAttributes.MessageConversationId, eventArgs.Message?.CorrelationId.ToString());

        if (!string.IsNullOrWhiteSpace(eventArgs.Exchange))
        {
            activity.DisplayName = eventArgs.Exchange + " publish";
            activity.SetTag(MessagingAttributes.MessagingDestination, eventArgs.Exchange);
        }
        else
        {
            activity.DisplayName = "anonymous publish";
            activity.SetTag(MessagingAttributes.MessagingDestinationAnonymous, "true");
        }

        if (!string.IsNullOrWhiteSpace(eventArgs.RoutingKey))
        {
            activity.SetTag(MessagingAttributes.MessagingDestinationRoutingKey, eventArgs.RoutingKey);
        }

        if (eventArgs.Headers.TryGetValue(HeaderKeys.MessageId, out string? messageId))
        {
            activity.SetTag(MessagingAttributes.MessageId, messageId);
        }

        InjectTraceContext(activity, eventArgs.Headers);

        TryEnrich(activity, eventArgs.Message, options);

        return activity;
    }
```

The body is structurally identical to today's `Publish` — only the `Options` reads become `options` (parameter), and `TryEnrich(activity, eventArgs.Message)` becomes `TryEnrich(activity, eventArgs.Message, options)` (Task 3 step 4 below updates `TryEnrich`).

**Update `Send` signature and body**, structurally identical to today's `Send`:

```csharp
    public static Activity? Send(
        SendEventArgs eventArgs,
        ServiceConnectInstrumentationOptions options,
        IMessagingSystemAttributes attributes,
        ActivityContext linkedContext = default)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(attributes);

        InjectTraceContext(Activity.Current, eventArgs.Headers);

        Activity? activity = StartActivityWithLink(
            _activitySource,
            ActivitySourceName,
            ActivityKind.Producer,
            options.EnableSendTelemetry,
            attributes,
            "publish",
            linkedContext);

        if (activity is null)
        {
            return null;
        }

        // ... rest of the method body identical to today, with `Options.X` → `options.X` ...
        // (full body preserved from the existing implementation, no behavior changes here)

        TryEnrich(activity, eventArgs.Message, options);

        return activity;
    }
```

For brevity the snippet shows the migration shape only — copy the rest of today's body (destination computation, DisplayName, tag setting) verbatim, replacing `Options.X` with `options.X`.

**Update `Consume` signature and body**, structurally identical to today's `Consume`:

```csharp
    public static Activity? Consume(
        ConsumeEventArgs eventArgs,
        ServiceConnectInstrumentationOptions options,
        IMessagingSystemAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(attributes);

        DistributedContextPropagator.Current.ExtractTraceIdAndState(eventArgs.Headers, ExtractTraceIdAndState, out string? traceId, out string? traceState);
        ActivityContext.TryParse(traceId, traceState, out ActivityContext parentContext);

        Activity? activity = StartActivityWithParent(
            _activitySource,
            ActivitySourceName,
            ActivityKind.Consumer,
            options.EnableConsumeTelemetry,
            attributes,
            "receive",
            parentContext);

        // ... rest of the method body identical to today's body ...

        if (eventArgs.Message is not null)
        {
            activity.SetTag(MessagingAttributes.MessagingBodySize, eventArgs.Message.Length);
            TryEnrich(activity, eventArgs.Message, options);
        }

        return activity;
    }
```

**Update `SetError` signature and body** to take options:

```csharp
    public static void SetError(Activity? activity, Exception exception, ServiceConnectInstrumentationOptions options)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(options);

        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
#if NET9_0_OR_GREATER
        activity.AddException(exception);
#else
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            ["exception.type"] = exception.GetType().FullName,
            ["exception.message"] = exception.Message,
            ["exception.stacktrace"] = exception.ToString(),
        }));
#endif
    }
```

The body is identical to today — Task 15 wires the `ExceptionMessageSanitiser` through this method. For Task 3 the parameter is added but ignored.

- [ ] **Step 2: Update `StartActivityWithParent` and `StartActivityWithLink` to take attributes**

```csharp
    private static Activity? StartActivityWithParent(
        ActivitySource activitySource,
        string activityName,
        ActivityKind kind,
        bool enabled,
        IMessagingSystemAttributes attributes,
        string operation,
        ActivityContext parentContext)
    {
        if (!enabled || !activitySource.HasListeners())
        {
            return null;
        }

        Activity? activity = activitySource.StartActivity(activityName, kind, parentContext);
        if (activity is null)
        {
            return null;
        }

        activity
            .SetTag(MessagingAttributes.MessagingSystem, attributes.MessagingSystem)
            .SetTag(MessagingAttributes.ProtocolName, attributes.ProtocolName)
            .SetTag(MessagingAttributes.MessagingOperation, operation);

        return activity;
    }

    private static Activity? StartActivityWithLink(
        ActivitySource activitySource,
        string activityName,
        ActivityKind kind,
        bool enabled,
        IMessagingSystemAttributes attributes,
        string operation,
        ActivityContext linkedContext)
    {
        if (!enabled || !activitySource.HasListeners())
        {
            return null;
        }

        ActivityLink[]? links = linkedContext == default
            ? null
            : [new ActivityLink(linkedContext)];

        Activity? activity = activitySource.StartActivity(
            activityName, kind, parentContext: default, tags: null, links: links);
        if (activity is null)
        {
            return null;
        }

        activity
            .SetTag(MessagingAttributes.MessagingSystem, attributes.MessagingSystem)
            .SetTag(MessagingAttributes.ProtocolName, attributes.ProtocolName)
            .SetTag(MessagingAttributes.MessagingOperation, operation);

        return activity;
    }
```

The substantive changes vs. today: `IMessagingSystemAttributes attributes` parameter added; the `MessagingSystem`/`ProtocolName` tags read from it instead of the static `MessagingSystemAttributes`. **The `parentContext: default, ..., links: links` pattern in `StartActivityWithLink` is preserved here** — that's the L11 anti-pattern we'll fix in Task 9.

- [ ] **Step 3: Update `TryEnrich` overloads to take options**

```csharp
    private static void TryEnrich(Activity activity, Message? message, ServiceConnectInstrumentationOptions options)
    {
        if (message is null)
        {
            return;
        }

        try
        {
            options.EnrichWithMessage?.Invoke(activity, message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity.SetTag("enrichment.exception", ex.GetType().FullName);
        }
    }

    private static void TryEnrich(Activity activity, byte[]? message, ServiceConnectInstrumentationOptions options)
    {
        if (message is null)
        {
            return;
        }

        try
        {
            options.EnrichWithMessageBytes?.Invoke(activity, message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity.SetTag("enrichment.exception", ex.GetType().FullName);
        }
    }
```

- [ ] **Step 4: Update internal test seams**

```csharp
    internal static void InvokeTryEnrichForTest(Activity activity, Message? message, ServiceConnectInstrumentationOptions options) =>
        TryEnrich(activity, message, options);

    internal static void InvokeTryEnrichForTest(Activity activity, byte[]? bytes, ServiceConnectInstrumentationOptions options) =>
        TryEnrich(activity, bytes, options);
```

- [ ] **Step 5: Update `TelemetrySendMiddleware`**

In `src/ServiceConnect.Telemetry/TelemetrySendMiddleware.cs`, replace the entire class body:

```csharp
internal sealed class TelemetrySendMiddleware(
    ServiceConnectInstrumentationOptions options,
    IMessagingSystemAttributes attributes) : ISendMessageMiddleware
{
    private readonly ServiceConnectInstrumentationOptions _options = options;
    private readonly IMessagingSystemAttributes _attributes = attributes;

    /// <inheritdoc/>
    public async Task ProcessAsync(SendContext context, SendMessageDelegate next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        Activity? activity = context.Operation switch
        {
            SendOperation.Publish => ServiceConnectActivitySource.Publish(new PublishEventArgs
            {
                Message = context.Message,
                Headers = context.Headers,
                RoutingKey = context.RoutingKey ?? string.Empty,
                Exchange = context.MessageType.FullName ?? string.Empty,
            }, _options, _attributes),
            SendOperation.Send or SendOperation.Request => ServiceConnectActivitySource.Send(new SendEventArgs
            {
                Message = context.Message,
                Headers = context.Headers,
                EndPoint = context.EndPoint ?? string.Empty,
            }, _options, _attributes),
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

- [ ] **Step 6: Update `TelemetryProcessingMiddleware`**

In `src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs`, replace the entire class body. **This task does NOT yet add the C7 gate or L15 fix** — the body copy is preserved as-is, the error-tag condition is the existing two-clause check. Tasks 8 (C7) and 13 (L15) update them.

```csharp
internal sealed class TelemetryProcessingMiddleware(
    ServiceConnectInstrumentationOptions options,
    IMessagingSystemAttributes attributes) : IMessageProcessingMiddleware
{
    private readonly ServiceConnectInstrumentationOptions _options = options;
    private readonly IMessagingSystemAttributes _attributes = attributes;

    /// <inheritdoc/>
    public async Task<ConsumeEventResult> ProcessAsync(
        ReadOnlyMemory<byte> messageBytes,
        Type messageType,
        object message,
        IDictionary<string, object> headers,
        Envelope envelope,
        MessageProcessingDelegate next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(next);

        var args = new ConsumeEventArgs
        {
            Message = envelope.Body.ToArray(),
            Type = messageType.FullName ?? string.Empty,
            Headers = headers,
        };
        Activity? activity = ServiceConnectActivitySource.Consume(args, _options, _attributes);

        try
        {
            var result = await next(messageBytes, messageType, message, headers, envelope, cancellationToken).ConfigureAwait(false);
            if (!result.Success && result.Exception is not null)
            {
                ServiceConnectActivitySource.SetError(activity, result.Exception, _options);
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

- [ ] **Step 7: Update `TelemetryBuilderExtensions.AddTelemetry`**

In `src/ServiceConnect.Telemetry/TelemetryBuilderExtensions.cs`, replace the body of `AddTelemetry`. Add the `using Microsoft.Extensions.DependencyInjection.Extensions;` import for `TryAddSingleton`.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceConnect;
using ServiceConnect.Configuration;

namespace ServiceConnect.Telemetry;

public static class TelemetryBuilderExtensions
{
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
}
```

The line `ServiceConnectActivitySource.Options = options;` is **removed**.

- [ ] **Step 8: Build the Telemetry project**

```bash
dotnet build src/ServiceConnect.Telemetry/ServiceConnect.Telemetry.csproj
```

Expected: succeeds. (The test project still won't build at this point — Task 4 fixes it.)

- [ ] **Step 9: Commit**

```bash
git add src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs \
        src/ServiceConnect.Telemetry/TelemetrySendMiddleware.cs \
        src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs \
        src/ServiceConnect.Telemetry/TelemetryBuilderExtensions.cs
git commit -m "refactor(telemetry): thread options + attributes through public API; remove statics"
```

---

### Task 4: Migrate existing tests to the new API surface

**Files:**
- Modify: `src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs`
- Modify: `src/ServiceConnect.UnitTests/Telemetry/TelemetryBuilderExtensionsTests.cs`
- Modify: `src/ServiceConnect.UnitTests/Telemetry/TelemetryProcessingMiddlewareTests.cs`
- Modify: `src/ServiceConnect.UnitTests/Telemetry/TelemetrySendMiddlewareTests.cs`
- Modify: `src/ServiceConnect.UnitTests/Telemetry/TryEnrichTests.cs`

Each existing test invokes the old API (e.g. `ServiceConnectActivitySource.Publish(args)` or sets `ServiceConnectActivitySource.Options = ...`). All call sites must be updated to the new shape. **No new tests yet** — only existing-test migration.

- [ ] **Step 1: Inspect the existing test fixtures' shared setup**

```bash
head -50 src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs
head -50 src/ServiceConnect.UnitTests/Telemetry/TelemetryBuilderExtensionsTests.cs
head -50 src/ServiceConnect.UnitTests/Telemetry/TelemetryProcessingMiddlewareTests.cs
head -50 src/ServiceConnect.UnitTests/Telemetry/TelemetrySendMiddlewareTests.cs
head -50 src/ServiceConnect.UnitTests/Telemetry/TryEnrichTests.cs
```

Note in particular how the existing fixtures register `ActivityListener` (the listener's `ShouldListenTo` needs updating from three-source-name match to single-source-name match) and how they set `ServiceConnectActivitySource.Options`.

- [ ] **Step 2: Update `ServiceConnectActivitySourceTests.cs`**

Two mechanical updates throughout the file:

**A. `ShouldListenTo` filter:** the existing fixture has:
```csharp
ShouldListenTo = src =>
    src.Name == ServiceConnectActivitySource.PublishActivitySourceName
    || src.Name == ServiceConnectActivitySource.ConsumeActivitySourceName
    || src.Name == ServiceConnectActivitySource.SendActivitySourceName,
```
Replace with:
```csharp
ShouldListenTo = src => src.Name == ServiceConnectActivitySource.ActivitySourceName,
```

**B. Every `Publish`/`Send`/`Consume` call gains options + attributes parameters.** Add a fixture-level helper field (in the constructor or as `private static`):

```csharp
    private readonly ServiceConnectInstrumentationOptions _options = new();
    private readonly IMessagingSystemAttributes _attrs = new RabbitMqMessagingSystemAttributes();
```

Then every call site `ServiceConnectActivitySource.Publish(eventArgs)` becomes `ServiceConnectActivitySource.Publish(eventArgs, _options, _attrs)`. Likewise for `Send` and `Consume`.

**C. Tests that set `ServiceConnectActivitySource.Options` directly** (search for `ServiceConnectActivitySource.Options = `) must instead construct a local options instance and pass it to the call site under test. Example transformation:

```csharp
// before
ServiceConnectActivitySource.Options = new ServiceConnectInstrumentationOptions { EnablePublishTelemetry = false };
var activity = ServiceConnectActivitySource.Publish(eventArgs);

// after
var options = new ServiceConnectInstrumentationOptions { EnablePublishTelemetry = false };
var activity = ServiceConnectActivitySource.Publish(eventArgs, options, _attrs);
```

**D. `SetError(activity, ex)` calls** become `SetError(activity, ex, _options)`.

Apply these transformations to every test in the file. The 30+ existing tests should preserve their assertions verbatim — only the call-site shape changes.

- [ ] **Step 3: Update `TelemetryBuilderExtensionsTests.cs`**

Tests in this file build a `ServiceConnectBuilder`, call `AddTelemetry`, and assert pipeline registration. The migration here is small:

- Any reference to `ServiceConnectActivitySource.Options` becomes a DI resolution: `services.BuildServiceProvider().GetRequiredService<ServiceConnectInstrumentationOptions>()`.
- Any test that asserts the static was set after `AddTelemetry` is updated to assert the DI registration instead.

- [ ] **Step 4: Update `TelemetryProcessingMiddlewareTests.cs`**

Tests construct `TelemetryProcessingMiddleware` directly. The new constructor takes two parameters:

```csharp
// before
var middleware = new TelemetryProcessingMiddleware();

// after
var options = new ServiceConnectInstrumentationOptions();
var attributes = new RabbitMqMessagingSystemAttributes();
var middleware = new TelemetryProcessingMiddleware(options, attributes);
```

If any test sets `ServiceConnectActivitySource.Options = ...` to influence middleware behavior, replace by mutating the local `options` instance before constructing the middleware.

- [ ] **Step 5: Update `TelemetrySendMiddlewareTests.cs`**

Same shape as Step 4 — `new TelemetrySendMiddleware()` becomes `new TelemetrySendMiddleware(options, attributes)`. The constructor signature was `TelemetrySendMiddleware(ServiceConnectInstrumentationOptions options)` already (with the field dead); now it's `(options, attributes)` and the field is read.

- [ ] **Step 6: Update `TryEnrichTests.cs`**

The `InvokeTryEnrichForTest` seam now takes a third `options` parameter. Every test call adapts:

```csharp
// before
ServiceConnectActivitySource.InvokeTryEnrichForTest(activity, message);

// after
ServiceConnectActivitySource.InvokeTryEnrichForTest(activity, message, options);
```

Where `options` is constructed locally with the test's desired enricher (the existing tests almost certainly already set `Options.EnrichWithMessage` before calling the seam — that becomes constructing a local `ServiceConnectInstrumentationOptions { EnrichWithMessage = ... }` and passing it).

- [ ] **Step 7: Build + run the migrated test suite**

```bash
dotnet build src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Telemetry|FullyQualifiedName~ActivitySource" -m:1
```

Expected: build succeeds; all migrated tests pass. If any test fails at this stage, it's because the migration accidentally changed behaviour — fix the test (don't fix the production code; behaviour changes belong in Group B).

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect.UnitTests/Telemetry/
git commit -m "test(telemetry): migrate existing tests to new API surface (options + attributes parameters, single source)"
```

---

## Group B — Behavior fixes (TDD)

### Task 5: C6 — Publish disposes activity on enricher exception

**Files:**
- Modify: `src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs`
- Modify: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`

- [ ] **Step 1: Write the failing test**

Append to `ServiceConnectActivitySourceTests.cs`:

```csharp
    [Fact]
    public void Publish_EnricherThrowsOce_DisposesActivity_AndRestoresAmbientCurrent()
    {
        var ambient = new Activity("ambient").Start();

        var options = new ServiceConnectInstrumentationOptions
        {
            EnrichWithMessage = (_, _) => throw new OperationCanceledException("co-op cancel"),
        };

        var args = new PublishEventArgs
        {
            Message = new Message(Guid.NewGuid()),
            Exchange = "exchange",
            Headers = new Dictionary<string, string>(),
        };

        var thrown = Assert.Throws<OperationCanceledException>(() =>
            ServiceConnectActivitySource.Publish(args, options, _attrs));

        Assert.Equal("co-op cancel", thrown.Message);
        // The activity that Publish started must be disposed before the OCE escapes,
        // so Activity.Current is the outer ambient activity, not a leaked publish span.
        Assert.Same(ambient, Activity.Current);

        ambient.Dispose();
    }
```

- [ ] **Step 2: Run test to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Publish_EnricherThrowsOce" -m:1
```

Expected: FAIL — `Activity.Current` is not the ambient activity (it's the leaked publish span, or null if the leaked span was implicitly stopped by GC).

- [ ] **Step 3: Apply the C6 wrap to `Publish`**

In `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`, modify the `Publish` method body. The structure becomes:

```csharp
    public static Activity? Publish(
        PublishEventArgs eventArgs,
        ServiceConnectInstrumentationOptions options,
        IMessagingSystemAttributes attributes,
        ActivityContext linkedContext = default)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(attributes);

        InjectTraceContext(Activity.Current, eventArgs.Headers);

        Activity? activity = StartActivityWithLink(
            _activitySource, ActivitySourceName, ActivityKind.Producer,
            options.EnablePublishTelemetry, attributes, "publish", linkedContext);

        if (activity is null)
        {
            return null;
        }

        try
        {
            activity.SetTag(MessagingAttributes.MessageConversationId, eventArgs.Message?.CorrelationId.ToString());

            if (!string.IsNullOrWhiteSpace(eventArgs.Exchange))
            {
                activity.DisplayName = eventArgs.Exchange + " publish";
                activity.SetTag(MessagingAttributes.MessagingDestination, eventArgs.Exchange);
            }
            else
            {
                activity.DisplayName = "anonymous publish";
                activity.SetTag(MessagingAttributes.MessagingDestinationAnonymous, "true");
            }

            if (!string.IsNullOrWhiteSpace(eventArgs.RoutingKey))
            {
                activity.SetTag(MessagingAttributes.MessagingDestinationRoutingKey, eventArgs.RoutingKey);
            }

            if (eventArgs.Headers.TryGetValue(HeaderKeys.MessageId, out string? messageId))
            {
                activity.SetTag(MessagingAttributes.MessageId, messageId);
            }

            InjectTraceContext(activity, eventArgs.Headers);

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

The wrap covers everything from the first tag-set onward, including `InjectTraceContext` and `TryEnrich`.

- [ ] **Step 4: Run test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Publish_EnricherThrowsOce" -m:1
```

Expected: 1 passed.

Also re-run all `ServiceConnectActivitySource` tests to confirm no regression:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ServiceConnectActivitySourceTests" -m:1
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs \
        src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs
git commit -m "fix(telemetry): C6 — Publish disposes activity on enricher exception"
```

---

### Task 6: C6 — Send disposes activity on enricher exception

Same shape as Task 5, applied to `Send`.

**Files:**
- Modify: `src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs`
- Modify: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void Send_EnricherThrowsOce_DisposesActivity_AndRestoresAmbientCurrent()
    {
        var ambient = new Activity("ambient").Start();

        var options = new ServiceConnectInstrumentationOptions
        {
            EnrichWithMessage = (_, _) => throw new OperationCanceledException("co-op cancel"),
        };

        var args = new SendEventArgs
        {
            Message = new Message(Guid.NewGuid()),
            EndPoint = "queue-a",
            Headers = new Dictionary<string, string>(),
        };

        Assert.Throws<OperationCanceledException>(() =>
            ServiceConnectActivitySource.Send(args, options, _attrs));

        Assert.Same(ambient, Activity.Current);

        ambient.Dispose();
    }
```

- [ ] **Step 2: Run test to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Send_EnricherThrowsOce" -m:1
```

Expected: FAIL.

- [ ] **Step 3: Apply C6 wrap to `Send`**

In `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`, modify `Send` similarly to Task 5's Publish change. Wrap from the first post-`StartActivity` tag-set onward through the final `TryEnrich`. The early `if (eventArgs.Message is null) return activity;` short-circuit goes inside the try block:

```csharp
    public static Activity? Send(
        SendEventArgs eventArgs,
        ServiceConnectInstrumentationOptions options,
        IMessagingSystemAttributes attributes,
        ActivityContext linkedContext = default)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(attributes);

        InjectTraceContext(Activity.Current, eventArgs.Headers);

        Activity? activity = StartActivityWithLink(
            _activitySource, ActivitySourceName, ActivityKind.Producer,
            options.EnableSendTelemetry, attributes, "publish", linkedContext);

        if (activity is null)
        {
            return null;
        }

        try
        {
            // ... (existing destination-computation + DisplayName + SetTag block, copied verbatim) ...

            InjectTraceContext(activity, eventArgs.Headers);

            if (eventArgs.Message is null)
            {
                return activity;
            }

            activity.SetTag(MessagingAttributes.MessageConversationId, eventArgs.Message.CorrelationId.ToString());

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

- [ ] **Step 4: Run test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Send_EnricherThrowsOce" -m:1
```

Expected: 1 passed.

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ServiceConnectActivitySourceTests" -m:1
```

Expected: all pass (no regression).

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs \
        src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs
git commit -m "fix(telemetry): C6 — Send disposes activity on enricher exception"
```

---

### Task 7: C6 — Consume disposes activity on enricher exception

Same shape, applied to `Consume`.

**Files:**
- Modify: `src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs`
- Modify: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void Consume_EnricherThrowsOce_DisposesActivity_AndRestoresAmbientCurrent()
    {
        var ambient = new Activity("ambient").Start();

        var options = new ServiceConnectInstrumentationOptions
        {
            EnrichWithMessageBytes = (_, _) => throw new OperationCanceledException("co-op cancel"),
        };

        var args = new ConsumeEventArgs
        {
            Message = new byte[] { 1, 2, 3 },
            Type = "FakeMessage",
            Headers = new Dictionary<string, object>(),
        };

        Assert.Throws<OperationCanceledException>(() =>
            ServiceConnectActivitySource.Consume(args, options, _attrs));

        Assert.Same(ambient, Activity.Current);

        ambient.Dispose();
    }
```

- [ ] **Step 2: Run test to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Consume_EnricherThrowsOce" -m:1
```

Expected: FAIL.

- [ ] **Step 3: Apply C6 wrap to `Consume`**

```csharp
    public static Activity? Consume(
        ConsumeEventArgs eventArgs,
        ServiceConnectInstrumentationOptions options,
        IMessagingSystemAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(attributes);

        DistributedContextPropagator.Current.ExtractTraceIdAndState(eventArgs.Headers, ExtractTraceIdAndState, out string? traceId, out string? traceState);
        ActivityContext.TryParse(traceId, traceState, out ActivityContext parentContext);

        Activity? activity = StartActivityWithParent(
            _activitySource, ActivitySourceName, ActivityKind.Consumer,
            options.EnableConsumeTelemetry, attributes, "receive", parentContext);

        if (activity is null)
        {
            return null;
        }

        try
        {
            // ... (existing tag-setting block — destinationAddress, messageId, correlationId — verbatim) ...

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

- [ ] **Step 4: Run test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Consume_EnricherThrowsOce" -m:1
```

Expected: 1 passed.

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ServiceConnectActivitySourceTests" -m:1
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs \
        src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs
git commit -m "fix(telemetry): C6 — Consume disposes activity on enricher exception"
```

---

### Task 8: C7 — TelemetryProcessingMiddleware gates body copy on listener presence

**Files:**
- Modify: `src/ServiceConnect.UnitTests/Telemetry/TelemetryProcessingMiddlewareTests.cs`
- Modify: `src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs`

- [ ] **Step 1: Write the failing test**

Append to `TelemetryProcessingMiddlewareTests.cs`. The test uses an `Envelope` whose `Body` getter is detected via a counter (the `Envelope` type is `init`-only so we set up a real one with a custom backing). Use a `Memory<byte>` of zero length and assert that `IsConsumeTelemetryEnabled` returning false short-circuits the args allocation.

A clean way to verify the middleware doesn't copy the body when no listener is attached: check via behavioural side-effect — register no listener, then run the middleware against a mocked `next` that asserts the args object was not constructed. Since the args object isn't observable from outside the middleware, the most practical assertion is: under no-listener configuration, no `ConsumeEventArgs.Message` byte-array allocation occurs. We'll measure this indirectly by verifying that `IsConsumeTelemetryEnabled` is the gate.

The cleanest direct test: register no listener, set `EnableConsumeTelemetry = false`, run the middleware, and assert the underlying `Consume` was not called (use a wrapper). But `Consume` is a static method — not easily mocked.

Simplest approach: verify the public `IsConsumeTelemetryEnabled` helper itself behaves correctly, then trust the gate implementation in the middleware. Two-test shape:

```csharp
    [Fact]
    public void IsConsumeTelemetryEnabled_NoListener_ReturnsFalse()
    {
        // No listener registered against ServiceConnectActivitySource.ActivitySourceName.
        var options = new ServiceConnectInstrumentationOptions { EnableConsumeTelemetry = true };
        Assert.False(ServiceConnectActivitySource.IsConsumeTelemetryEnabled(options));
    }

    [Fact]
    public void IsConsumeTelemetryEnabled_DisabledFlag_ReturnsFalse_EvenWithListener()
    {
        // Listener IS registered (test fixture's _listener), but EnableConsumeTelemetry=false.
        var options = new ServiceConnectInstrumentationOptions { EnableConsumeTelemetry = false };
        Assert.False(ServiceConnectActivitySource.IsConsumeTelemetryEnabled(options));
    }

    [Fact]
    public void IsConsumeTelemetryEnabled_ListenerAndFlagBoth_ReturnsTrue()
    {
        var options = new ServiceConnectInstrumentationOptions { EnableConsumeTelemetry = true };
        Assert.True(ServiceConnectActivitySource.IsConsumeTelemetryEnabled(options));
    }
```

The first test belongs in a fixture *without* a listener registered (the existing `TelemetryProcessingMiddlewareTests` file uses the `[Collection("ActivityListener")]` attribute and registers one in its constructor; for the no-listener test we need a separate fixture that doesn't). Place the no-listener test in a new fixture class:

```csharp
public sealed class IsConsumeTelemetryEnabledNoListenerTests
{
    [Fact]
    public void IsConsumeTelemetryEnabled_NoListener_ReturnsFalse()
    {
        var options = new ServiceConnectInstrumentationOptions { EnableConsumeTelemetry = true };
        Assert.False(ServiceConnectActivitySource.IsConsumeTelemetryEnabled(options));
    }
}
```

(No collection attribute — runs in its own thread without the `ActivityListener` registered.)

The second and third tests belong in the existing fixture (which has a listener registered).

- [ ] **Step 2: Run tests to verify the no-listener test fails**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~IsConsumeTelemetryEnabled" -m:1
```

Expected: 2 of 3 pass (`DisabledFlag_ReturnsFalse` and `ListenerAndFlagBoth_ReturnsTrue` already work because the helper was added in Task 2). The first test (`NoListener_ReturnsFalse`) passes too — actually both gating conditions are checked in the helper. So this test set should pass after Task 2.

The actual *behavioural* assertion is on the middleware. Add the middleware test to the no-listener fixture:

```csharp
public sealed class TelemetryProcessingMiddlewareNoListenerTests
{
    [Fact]
    public async Task ProcessAsync_NoListener_DoesNotInvokeConsume()
    {
        var options = new ServiceConnectInstrumentationOptions { EnableConsumeTelemetry = true };
        var attributes = new RabbitMqMessagingSystemAttributes();
        var middleware = new TelemetryProcessingMiddleware(options, attributes);

        var bodyAccessCount = 0;
        var envelope = new Envelope
        {
            Body = new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }),
            Headers = new Dictionary<string, object>(),
        };

        // Custom MessageProcessingDelegate captures whether it was called and asserts
        // that envelope.Body wasn't enumerated upstream. We can't directly intercept
        // the ToArray call, but we can assert that no activity was started — which
        // is the observable consequence of the gate.

        Activity? observedCurrent = null;
        MessageProcessingDelegate next = (mb, mt, m, h, e, ct) =>
        {
            observedCurrent = Activity.Current;
            return Task.FromResult(new ConsumeEventResult { Success = true });
        };

        var result = await middleware.ProcessAsync(
            new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }),
            typeof(string),
            "msg",
            new Dictionary<string, object>(),
            envelope,
            next,
            CancellationToken.None);

        Assert.True(result.Success);
        // No listener → no activity → Activity.Current is whatever was outer (null in this test).
        Assert.Null(observedCurrent);
    }
}
```

This test asserts the middleware doesn't start an activity when no listener is registered. After Task 8's gate is added, the middleware skips the entire `ConsumeEventArgs` allocation and `Consume` call when `IsConsumeTelemetryEnabled` returns false.

- [ ] **Step 3: Run the new test to verify it fails before the fix**

Before the gate is added, `Consume` IS called even without a listener — `Consume` itself returns `null` because `_activitySource.HasListeners()` is false, but it still allocates `ConsumeEventArgs.Message` upstream. The failing assertion will be subtle. Actually, `observedCurrent` will be null in both pre-fix and post-fix worlds. Let me adjust the test — instead of an `Activity.Current` check, observe whether the args were allocated.

Replace the test body with a direct gate-behaviour assertion:

```csharp
    [Fact]
    public async Task ProcessAsync_NoListener_GatesOnIsConsumeTelemetryEnabled()
    {
        // Pre-condition: this test runs in a fixture without an ActivityListener,
        // so HasListeners() returns false. With the C7 gate, the middleware should
        // skip the ConsumeEventArgs construction entirely.
        var options = new ServiceConnectInstrumentationOptions { EnableConsumeTelemetry = true };

        // Sanity: confirm the gate predicate IS false in this fixture.
        Assert.False(ServiceConnectActivitySource.IsConsumeTelemetryEnabled(options));

        var attributes = new RabbitMqMessagingSystemAttributes();
        var middleware = new TelemetryProcessingMiddleware(options, attributes);

        // Use an Envelope with a sentinel body and verify the middleware completes without throwing.
        // The gate's behavioural correctness is the absence of allocations — verified indirectly via
        // the IsConsumeTelemetryEnabled assertion above. The test below is a smoke test that the
        // middleware survives the no-listener path without errors.
        var envelope = new Envelope
        {
            Body = new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }),
            Headers = new Dictionary<string, object>(),
        };

        MessageProcessingDelegate next = (mb, mt, m, h, e, ct) =>
            Task.FromResult(new ConsumeEventResult { Success = true });

        var result = await middleware.ProcessAsync(
            new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }),
            typeof(string),
            "msg",
            new Dictionary<string, object>(),
            envelope,
            next,
            CancellationToken.None);

        Assert.True(result.Success);
    }
```

This test always passes structurally — its purpose is to document the gate contract via `Assert.False(ServiceConnectActivitySource.IsConsumeTelemetryEnabled(options))`. The C7 fix in the middleware doesn't change THIS test's outcome but does change the middleware's runtime behaviour — which we'd need a profiler to observe directly.

A more rigorous approach: subclass `Envelope` (if not sealed) or use a custom `Body` accessor. Quick check — is `Envelope.Body` settable, and is the type sealed? Per [src/ServiceConnect.Interfaces/Messages/Envelope.cs](../../../src/ServiceConnect.Interfaces/Messages/Envelope.cs) (read this first):

```bash
cat src/ServiceConnect.Interfaces/Messages/Envelope.cs
```

If `Envelope` is a sealed record / class, we can't subclass. If `Body` is `init`-only, we can't intercept. In that case the gate test is necessarily structural — the helper-predicate test is the meaningful assertion, and the middleware change is verified by inspection + the absence of regression in the existing tests.

**Reframe the test:** the meaningful new assertion is the `IsConsumeTelemetryEnabled` predicate. The middleware's behaviour is verified by:
1. The new `Assert.False(IsConsumeTelemetryEnabled(...))` test in a no-listener fixture.
2. Manual inspection of the middleware's gate.
3. Existing tests passing (no regression in listener-attached behaviour).

Add the predicate test only:

```csharp
public sealed class IsConsumeTelemetryEnabledNoListenerTests
{
    [Fact]
    public void IsConsumeTelemetryEnabled_NoListener_ReturnsFalse()
    {
        var options = new ServiceConnectInstrumentationOptions { EnableConsumeTelemetry = true };
        Assert.False(ServiceConnectActivitySource.IsConsumeTelemetryEnabled(options));
    }
}
```

- [ ] **Step 3 (revised): Run the predicate test to verify it passes**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~IsConsumeTelemetryEnabled_NoListener_ReturnsFalse" -m:1
```

Expected: PASS (the helper was added in Task 2 and behaves correctly).

- [ ] **Step 4: Apply the C7 gate to `TelemetryProcessingMiddleware`**

In `src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs`, modify `ProcessAsync` to gate the args construction and `Consume` call:

```csharp
        Activity? activity = null;
        if (ServiceConnectActivitySource.IsConsumeTelemetryEnabled(_options))
        {
            var args = new ConsumeEventArgs
            {
                Message = envelope.Body.ToArray(),
                Type = messageType.FullName ?? string.Empty,
                Headers = headers,
            };
            activity = ServiceConnectActivitySource.Consume(args, _options, _attributes);
        }
```

- [ ] **Step 5: Run the existing telemetry tests to confirm no regression**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Telemetry" -m:1
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs \
        src/ServiceConnect.UnitTests/Telemetry/TelemetryProcessingMiddlewareTests.cs
git commit -m "fix(telemetry): C7 — gate body-copy on IsConsumeTelemetryEnabled"
```

---

### Task 9: L11 — `StartActivityWithLink` honors parent context

**Files:**
- Modify: `src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs`
- Modify: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`

The current `StartActivityWithLink` calls `StartActivity(name, kind, parentContext: default, ..., links: links)` — Publish/Send activities always start a new trace root and the linked context is attached as a sibling-link rather than parent. The fix: pass the linked context as the parent (renaming the parameter to `parentContext`).

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void Publish_WithLinkedContext_SetsActivityParentNotLink()
    {
        var parentTraceId = ActivityTraceId.CreateRandom();
        var parentSpanId = ActivitySpanId.CreateRandom();
        var linkedContext = new ActivityContext(parentTraceId, parentSpanId, ActivityTraceFlags.Recorded);

        var args = new PublishEventArgs
        {
            Message = new Message(Guid.NewGuid()),
            Exchange = "exchange",
            Headers = new Dictionary<string, string>(),
        };

        using var activity = ServiceConnectActivitySource.Publish(args, _options, _attrs, linkedContext);

        Assert.NotNull(activity);
        Assert.Equal(parentTraceId, activity.TraceId);  // L11: child of linked context, same trace
        Assert.Equal(parentSpanId, activity.ParentSpanId);
    }
```

- [ ] **Step 2: Run test to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Publish_WithLinkedContext_SetsActivityParentNotLink" -m:1
```

Expected: FAIL — the activity has a different trace ID (it's a new trace root) and `ParentSpanId` is the default zero span id.

- [ ] **Step 3: Fix `StartActivityWithLink`**

In `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`, replace `StartActivityWithLink` with:

```csharp
    private static Activity? StartActivityWithLink(
        ActivitySource activitySource,
        string activityName,
        ActivityKind kind,
        bool enabled,
        IMessagingSystemAttributes attributes,
        string operation,
        ActivityContext linkedContext)
    {
        if (!enabled || !activitySource.HasListeners())
        {
            return null;
        }

        // L11: pass linkedContext as the parent so the activity is properly parented
        // (a new trace root with linkedContext attached as a sibling-link is the OTel
        // "linked-trace" semantic, but for ServiceConnect's publish-as-child-of-Send
        // pattern we want the parent relationship). When linkedContext is default,
        // ActivitySource falls back to Activity.Current — desired ambient behaviour.
        Activity? activity = activitySource.StartActivity(activityName, kind, linkedContext);
        if (activity is null)
        {
            return null;
        }

        activity
            .SetTag(MessagingAttributes.MessagingSystem, attributes.MessagingSystem)
            .SetTag(MessagingAttributes.ProtocolName, attributes.ProtocolName)
            .SetTag(MessagingAttributes.MessagingOperation, operation);

        return activity;
    }
```

The `links: links` argument is dropped entirely.

- [ ] **Step 4: Run test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Publish_WithLinkedContext_SetsActivityParentNotLink" -m:1
```

Expected: 1 passed.

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ServiceConnectActivitySourceTests" -m:1
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs \
        src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs
git commit -m "fix(telemetry): L11 — pass linkedContext as parent so Publish/Send are properly parented"
```

---

### Task 10: L12 — `ActivityContext.TryParse` explicit fallback

**Files:**
- Modify: `src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs`
- Modify: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`

The current `Consume` calls `ActivityContext.TryParse(traceId, traceState, out parentContext)` and discards the bool. On parse failure `parentContext` is `default` (functionally equivalent to the explicit fallback today). This task documents intent.

- [ ] **Step 1: Write the test**

```csharp
    [Fact]
    public void Consume_MalformedTraceparent_FallsBackToActivityCurrent()
    {
        using var ambient = new Activity("ambient").Start();

        var args = new ConsumeEventArgs
        {
            Message = new byte[] { 1 },
            Type = "FakeMessage",
            Headers = new Dictionary<string, object>
            {
                ["traceparent"] = "this-is-not-a-valid-traceparent",
            },
        };

        using var activity = ServiceConnectActivitySource.Consume(args, _options, _attrs);

        Assert.NotNull(activity);
        // Malformed traceparent → parentContext stays default → ActivitySource picks
        // Activity.Current as the parent.
        Assert.Equal(ambient.TraceId, activity.TraceId);
    }
```

- [ ] **Step 2: Run test to verify it passes (already-correct behavior)**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Consume_MalformedTraceparent_FallsBackToActivityCurrent" -m:1
```

Expected: PASS.

The test is **regression coverage** — it locks in the intended behavior. Step 3 below is the documentation change in code.

- [ ] **Step 3: Update `Consume` to make the fallback explicit**

In `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`, in `Consume`:

```csharp
        DistributedContextPropagator.Current.ExtractTraceIdAndState(eventArgs.Headers, ExtractTraceIdAndState, out string? traceId, out string? traceState);
        if (!ActivityContext.TryParse(traceId, traceState, out ActivityContext parentContext))
        {
            // Malformed traceparent — fall through with default parentContext;
            // ActivitySource.StartActivity then picks Activity.Current as the parent.
            parentContext = default;
        }
```

- [ ] **Step 4: Run test again to confirm still passing**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Consume_MalformedTraceparent" -m:1
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs \
        src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs
git commit -m "fix(telemetry): L12 — explicit TryParse fallback in Consume + regression test"
```

---

### Task 11: L13 — single `InjectTraceContext`

**Files:**
- Modify: `src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs`
- Modify: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`

Today `Publish` and `Send` call `InjectTraceContext` twice — once with `Activity.Current`, once with the new activity. With a listener attached the first inject is fully overwritten by the second. Wasted CPU on the hot path.

- [ ] **Step 1: Write the failing test**

This requires counting `Inject` calls. Use a custom `DistributedContextPropagator` for the test. The `DistributedContextPropagator.Current` setter accepts a custom propagator; restore it in `IDisposable.Dispose`:

```csharp
    [Fact]
    public void Publish_InjectsTraceContextExactlyOnce_WhenListenerAttached()
    {
        var injectCount = 0;
        var originalPropagator = DistributedContextPropagator.Current;
        try
        {
            DistributedContextPropagator.Current = new CountingPropagator(() => injectCount++);

            var args = new PublishEventArgs
            {
                Message = new Message(Guid.NewGuid()),
                Exchange = "exchange",
                Headers = new Dictionary<string, string>(),
            };

            using var activity = ServiceConnectActivitySource.Publish(args, _options, _attrs);
            Assert.NotNull(activity);

            Assert.Equal(1, injectCount);   // L13: not 2
        }
        finally
        {
            DistributedContextPropagator.Current = originalPropagator;
        }
    }

    private sealed class CountingPropagator : DistributedContextPropagator
    {
        private readonly Action _onInject;
        public CountingPropagator(Action onInject) => _onInject = onInject;
        public override IReadOnlyCollection<string> Fields => Array.Empty<string>();
        public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter)
            => _onInject();
        public override void ExtractTraceIdAndState(object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState)
        { traceId = null; traceState = null; }
        public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter)
            => null;
    }
```

- [ ] **Step 2: Run test to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Publish_InjectsTraceContextExactlyOnce" -m:1
```

Expected: FAIL — `injectCount` is 2.

- [ ] **Step 3: Apply L13 — collapse to single inject in `Publish`**

In `Publish`, restructure so there's exactly one inject. Two paths:
- **Activity null** (no listener / disabled): inject `Activity.Current` before returning null.
- **Activity non-null:** inject the new activity's context inside the C6 wrap.

```csharp
    public static Activity? Publish(
        PublishEventArgs eventArgs,
        ServiceConnectInstrumentationOptions options,
        IMessagingSystemAttributes attributes,
        ActivityContext linkedContext = default)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(attributes);

        Activity? activity = StartActivityWithLink(
            _activitySource, ActivitySourceName, ActivityKind.Producer,
            options.EnablePublishTelemetry, attributes, "publish", linkedContext);

        if (activity is null)
        {
            // L13: single inject. No activity → propagate ambient context.
            InjectTraceContext(Activity.Current, eventArgs.Headers);
            return null;
        }

        try
        {
            // L13: single inject. Activity non-null → propagate new span's context.
            InjectTraceContext(activity, eventArgs.Headers);

            activity.SetTag(MessagingAttributes.MessageConversationId, eventArgs.Message?.CorrelationId.ToString());

            // ... (rest of Publish body unchanged from Task 5) ...

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

Apply the same restructuring to `Send`. **Note: this composes with the C6 fix from Task 6.** The InjectTraceContext now lives inside the try/catch wrap when activity is non-null, so a propagator throw also disposes the activity. Net net: one inject per call site, two paths, both safe.

- [ ] **Step 4: Run test + regression test**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Publish_InjectsTraceContextExactlyOnce|FullyQualifiedName~ServiceConnectActivitySourceTests" -m:1
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs \
        src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs
git commit -m "fix(telemetry): L13 — single InjectTraceContext per Publish/Send call"
```

---

### Task 12: L14 — `InjectHeader` warns once on unsupported carrier

**Files:**
- Modify: `src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs`
- Modify: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`

Today `InjectHeader` silently no-ops when the carrier is not `IDictionary<string, string>`. A future refactor that changes the carrier type silently disables trace propagation. Add a once-per-process warning via `Trace.TraceWarning`.

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void InjectHeader_UnsupportedCarrier_WritesWarningOnce()
    {
        var listener = new RecordingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            // Reset the once-flag before this test (test seam — see Step 3).
            ServiceConnectActivitySource.ResetCarrierWarnedFlagForTest();

            var carrier = new Dictionary<string, object>();   // wrong shape on purpose

            DistributedContextPropagator.Current.Inject(
                Activity.Current ?? new Activity("ambient").Start(),
                carrier,
                ServiceConnectActivitySource.InjectHeaderForTest);

            DistributedContextPropagator.Current.Inject(
                Activity.Current,
                carrier,
                ServiceConnectActivitySource.InjectHeaderForTest);

            Assert.Single(listener.Warnings.Where(w => w.Contains("InjectHeader")));
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    private sealed class RecordingTraceListener : TraceListener
    {
        public List<string> Warnings { get; } = new();
        public override void TraceEvent(TraceEventCache? cache, string source, TraceEventType type, int id, string? message)
        {
            if (type == TraceEventType.Warning && message is not null) Warnings.Add(message);
        }
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { }
    }
```

- [ ] **Step 2: Run test to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InjectHeader_UnsupportedCarrier_WritesWarningOnce" -m:1
```

Expected: FAIL — compile errors (`InjectHeaderForTest`, `ResetCarrierWarnedFlagForTest` don't exist).

- [ ] **Step 3: Add the warning + test seams**

In `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`:

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
            // Once-per-process diagnostic — a refactor that changes the carrier type
            // silently disables trace propagation. Use Trace because static helpers don't
            // have an ILogger; OTel users routinely route .NET trace listeners.
            System.Diagnostics.Trace.TraceWarning(
                "ServiceConnectActivitySource.InjectHeader: unsupported carrier type {0}; trace context not propagated.",
                carrier?.GetType().FullName ?? "<null>");
        }
    }

    // Test seams — internal so the unit-test project can exercise the warning path
    // without exposing the helper to public callers.
    internal static DistributedContextPropagator.PropagatorSetterCallback InjectHeaderForTest => InjectHeader;
    internal static void ResetCarrierWarnedFlagForTest() => Interlocked.Exchange(ref _warnedAboutCarrierShape, 0);
```

The `InjectHeaderForTest` exposes the private `InjectHeader` as a public delegate-typed property the test can pass into `DistributedContextPropagator.Current.Inject(...)`.

- [ ] **Step 4: Run test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InjectHeader_UnsupportedCarrier_WritesWarningOnce" -m:1
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs \
        src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs
git commit -m "fix(telemetry): L14 — once-per-process warning when InjectHeader carrier shape unsupported"
```

---

### Task 13: L15 — `TelemetryProcessingMiddleware` tags activity error on `Success=false` without exception

**Files:**
- Modify: `src/ServiceConnect.UnitTests/Telemetry/TelemetryProcessingMiddlewareTests.cs`
- Modify: `src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public async Task ProcessAsync_ResultSuccessFalseNoException_TagsActivityError()
    {
        var options = new ServiceConnectInstrumentationOptions { EnableConsumeTelemetry = true };
        var attributes = new RabbitMqMessagingSystemAttributes();
        var middleware = new TelemetryProcessingMiddleware(options, attributes);

        Activity? observedActivity = null;
        ActivityStatusCode observedStatus = ActivityStatusCode.Unset;
        string? observedDescription = null;

        // Hook the listener to capture the activity's final state on dispose.
        var capturingListener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == ServiceConnectActivitySource.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                observedActivity = a;
                observedStatus = a.Status;
                observedDescription = a.StatusDescription;
            },
        };
        ActivitySource.AddActivityListener(capturingListener);
        try
        {
            MessageProcessingDelegate next = (mb, mt, m, h, e, ct) =>
                Task.FromResult(new ConsumeEventResult { Success = false, Exception = null });

            var envelope = new Envelope
            {
                Body = new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }),
                Headers = new Dictionary<string, object>(),
            };

            var result = await middleware.ProcessAsync(
                new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }),
                typeof(string),
                "msg",
                new Dictionary<string, object>(),
                envelope,
                next,
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.NotNull(observedActivity);
            Assert.Equal(ActivityStatusCode.Error, observedStatus);
            Assert.Equal("Dispatch returned Success=false without an exception", observedDescription);
        }
        finally
        {
            capturingListener.Dispose();
        }
    }
```

- [ ] **Step 2: Run test to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ProcessAsync_ResultSuccessFalseNoException_TagsActivityError" -m:1
```

Expected: FAIL — current middleware skips error tagging when `Exception is null`, so status stays `Unset`.

- [ ] **Step 3: Apply L15 fix**

In `src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs`, modify the result-handling block:

```csharp
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
```

- [ ] **Step 4: Run test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ProcessAsync_ResultSuccessFalseNoException_TagsActivityError" -m:1
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs \
        src/ServiceConnect.UnitTests/Telemetry/TelemetryProcessingMiddlewareTests.cs
git commit -m "fix(telemetry): L15 — tag activity error on Success=false without exception"
```

---

### Task 14: L17 — `MaxTagValueLength` truncation

**Files:**
- Modify: `src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs`
- Modify: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void Publish_HeaderValueExceedsMaxTagValueLength_TruncatesTag()
    {
        var longRoutingKey = new string('a', 500);
        var options = new ServiceConnectInstrumentationOptions { MaxTagValueLength = 100 };

        var args = new PublishEventArgs
        {
            Message = new Message(Guid.NewGuid()),
            Exchange = "exchange",
            RoutingKey = longRoutingKey,
            Headers = new Dictionary<string, string>(),
        };

        using var activity = ServiceConnectActivitySource.Publish(args, options, _attrs);
        Assert.NotNull(activity);

        var routingKeyTag = activity.GetTagItem(MessagingAttributes.MessagingDestinationRoutingKey)?.ToString();
        Assert.NotNull(routingKeyTag);
        Assert.Equal(100, routingKeyTag.Length);
        Assert.Equal(new string('a', 100), routingKeyTag);
    }

    [Fact]
    public void Publish_HeaderValueWithinMaxTagValueLength_TagsVerbatim()
    {
        var shortRoutingKey = "short.routing.key";
        var options = new ServiceConnectInstrumentationOptions { MaxTagValueLength = 100 };

        var args = new PublishEventArgs
        {
            Message = new Message(Guid.NewGuid()),
            Exchange = "exchange",
            RoutingKey = shortRoutingKey,
            Headers = new Dictionary<string, string>(),
        };

        using var activity = ServiceConnectActivitySource.Publish(args, options, _attrs);
        Assert.NotNull(activity);

        var routingKeyTag = activity.GetTagItem(MessagingAttributes.MessagingDestinationRoutingKey)?.ToString();
        Assert.Equal(shortRoutingKey, routingKeyTag);
    }
```

- [ ] **Step 2: Run tests to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MaxTagValueLength" -m:1
```

Expected: 1 of 2 fails (`ExceedsMaxTagValueLength_TruncatesTag` fails — no truncation today; the verbatim test passes).

- [ ] **Step 3: Add the `Truncate` helper and apply at user-controlled `SetTag` sites**

In `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`:

```csharp
    private static string Truncate(string? value, int maxLength)
    {
        if (value is null) return string.Empty;
        if (maxLength <= 0 || value.Length <= maxLength) return value;
        return value.Substring(0, maxLength);
    }
```

Apply at every user-controlled `SetTag` call site in `Publish`/`Send`/`Consume`:

In `Publish`, change:
```csharp
            activity.SetTag(MessagingAttributes.MessagingDestination, eventArgs.Exchange);
```
to:
```csharp
            activity.SetTag(MessagingAttributes.MessagingDestination, Truncate(eventArgs.Exchange, options.MaxTagValueLength));
```

Same pattern for `MessagingDestinationRoutingKey`, `MessageId`, `MessageConversationId` in `Publish`. In `Send`: `MessagingDestination`, `MessageConversationId`. In `Consume`: `MessagingDestination`, `MessageId`, `MessageConversationId`.

The DisplayName setting is unconditional (it's an Activity property, not a tag); also truncate it.

System-attribute tags (`MessagingSystem`, `ProtocolName`, `MessagingOperation`) are not user-controlled — skip those.

- [ ] **Step 4: Run tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MaxTagValueLength|FullyQualifiedName~ServiceConnectActivitySourceTests" -m:1
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs \
        src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs
git commit -m "fix(telemetry): L17 — truncate user-controlled tag values to MaxTagValueLength"
```

---

### Task 15: `ExceptionMessageSanitiser`

**Files:**
- Modify: `src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs`
- Modify: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void SetError_WithSanitiser_AppliesToStatusAndExceptionEventTag()
    {
        var options = new ServiceConnectInstrumentationOptions
        {
            ExceptionMessageSanitiser = ex => "REDACTED",
        };

        ActivityStatusCode observedStatus = ActivityStatusCode.Unset;
        string? observedDescription = null;
        Dictionary<string, object?>? observedExceptionTags = null;

        var capturingListener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == ServiceConnectActivitySource.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                observedStatus = a.Status;
                observedDescription = a.StatusDescription;
                var ev = a.Events.FirstOrDefault(e => e.Name == "exception");
                if (ev.Tags is not null)
                {
                    observedExceptionTags = ev.Tags.ToDictionary(t => t.Key, t => t.Value);
                }
            },
        };
        ActivitySource.AddActivityListener(capturingListener);
        try
        {
            using var activity = new ActivitySource(ServiceConnectActivitySource.ActivitySourceName).StartActivity("test");
            Assert.NotNull(activity);

            ServiceConnectActivitySource.SetError(activity, new InvalidOperationException("sensitive: connection-string=secret"), options);

            activity.Dispose();

            Assert.Equal(ActivityStatusCode.Error, observedStatus);
            Assert.Equal("REDACTED", observedDescription);
            Assert.NotNull(observedExceptionTags);
            Assert.Equal("REDACTED", observedExceptionTags!["exception.message"]);
        }
        finally
        {
            capturingListener.Dispose();
        }
    }
```

- [ ] **Step 2: Run test to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~SetError_WithSanitiser" -m:1
```

Expected: FAIL — `observedDescription` is the raw exception message, not "REDACTED".

- [ ] **Step 3: Wire `ExceptionMessageSanitiser` into `SetError`**

In `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`:

```csharp
    public static void SetError(Activity? activity, Exception exception, ServiceConnectInstrumentationOptions options)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(options);

        if (activity is null)
        {
            return;
        }

        var message = options.ExceptionMessageSanitiser is { } sanitise
            ? sanitise(exception)
            : exception.Message;

        activity.SetStatus(ActivityStatusCode.Error, message);

#if NET9_0_OR_GREATER
        if (options.ExceptionMessageSanitiser is null)
        {
            // No sanitiser — use the framework's AddException, which records the raw message.
            activity.AddException(exception);
        }
        else
        {
            // Sanitiser supplied — opt out of AddException (which would re-record the
            // unsanitised message) and record the OTel "exception" event manually with the
            // sanitised message. Type and stacktrace are recorded as-is.
            activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                ["exception.type"] = exception.GetType().FullName,
                ["exception.message"] = message,
                ["exception.stacktrace"] = exception.ToString(),
            }));
        }
#else
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            ["exception.type"] = exception.GetType().FullName,
            ["exception.message"] = message,
            ["exception.stacktrace"] = exception.ToString(),
        }));
#endif
    }
```

- [ ] **Step 4: Run test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~SetError_WithSanitiser" -m:1
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs \
        src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs
git commit -m "fix(telemetry): wire ExceptionMessageSanitiser through SetError"
```

---

### Task 16: `IsAllDataRequested` guards

**Files:**
- Modify: `src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs`
- Modify: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void Publish_SampleDroppedActivity_DoesNotSetUserTags()
    {
        // PropagationData sampling makes IsAllDataRequested == false.
        var droppingListener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == ServiceConnectActivitySource.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData,
        };
        ActivitySource.AddActivityListener(droppingListener);
        try
        {
            var args = new PublishEventArgs
            {
                Message = new Message(Guid.NewGuid()),
                Exchange = "exchange",
                RoutingKey = "rk",
                Headers = new Dictionary<string, string>(),
            };

            using var activity = ServiceConnectActivitySource.Publish(args, _options, _attrs);
            Assert.NotNull(activity);
            Assert.False(activity.IsAllDataRequested);

            // System-attribute tags (MessagingSystem etc) are also skipped under sample-drop —
            // the guard is uniform across both the system tags and the user-controlled ones.
            Assert.Null(activity.GetTagItem(MessagingAttributes.MessagingDestination));
            Assert.Null(activity.GetTagItem(MessagingAttributes.MessagingDestinationRoutingKey));
            Assert.Null(activity.GetTagItem(MessagingAttributes.MessagingSystem));
        }
        finally
        {
            droppingListener.Dispose();
        }
    }
```

- [ ] **Step 2: Run test to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Publish_SampleDroppedActivity_DoesNotSetUserTags" -m:1
```

Expected: FAIL — tags are set unconditionally today.

- [ ] **Step 3: Apply `IsAllDataRequested` guards**

In `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`, in `StartActivityWithParent` and `StartActivityWithLink`, wrap the system-attribute SetTag block:

```csharp
        Activity? activity = activitySource.StartActivity(activityName, kind, parentContext);
        if (activity is null)
        {
            return null;
        }

        if (activity.IsAllDataRequested)
        {
            activity
                .SetTag(MessagingAttributes.MessagingSystem, attributes.MessagingSystem)
                .SetTag(MessagingAttributes.ProtocolName, attributes.ProtocolName)
                .SetTag(MessagingAttributes.MessagingOperation, operation);
        }

        return activity;
```

In `Publish`, wrap the per-message tag block (everything between the start of the try and the `InjectTraceContext` line):

```csharp
        try
        {
            InjectTraceContext(activity, eventArgs.Headers);

            if (activity.IsAllDataRequested)
            {
                if (eventArgs.Message?.CorrelationId is { } cid && cid != Guid.Empty)
                {
                    activity.SetTag(MessagingAttributes.MessageConversationId,
                        Truncate(cid.ToString(), options.MaxTagValueLength));
                }

                if (!string.IsNullOrWhiteSpace(eventArgs.Exchange))
                {
                    activity.DisplayName = Truncate(eventArgs.Exchange + " publish", options.MaxTagValueLength);
                    activity.SetTag(MessagingAttributes.MessagingDestination,
                        Truncate(eventArgs.Exchange, options.MaxTagValueLength));
                }
                else
                {
                    activity.DisplayName = "anonymous publish";
                    activity.SetTag(MessagingAttributes.MessagingDestinationAnonymous, "true");
                }

                if (!string.IsNullOrWhiteSpace(eventArgs.RoutingKey))
                {
                    activity.SetTag(MessagingAttributes.MessagingDestinationRoutingKey,
                        Truncate(eventArgs.RoutingKey, options.MaxTagValueLength));
                }

                if (eventArgs.Headers.TryGetValue(HeaderKeys.MessageId, out string? messageId))
                {
                    activity.SetTag(MessagingAttributes.MessageId,
                        Truncate(messageId, options.MaxTagValueLength));
                }
            }

            TryEnrich(activity, eventArgs.Message, options);
            return activity;
        }
```

The `TryEnrich` and `InjectTraceContext` calls stay outside the guard — they have their own semantics (header injection must always happen for trace propagation; enrichment is opt-in via the user-supplied delegate, which the user can check `IsAllDataRequested` on themselves if they want).

Apply the same `IsAllDataRequested` wrap to `Send` and `Consume`'s per-message tag blocks. Also apply the empty-Guid skip in `Send` (CorrelationId tag site).

- [ ] **Step 4: Run test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Publish_SampleDroppedActivity_DoesNotSetUserTags|FullyQualifiedName~ServiceConnectActivitySourceTests" -m:1
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs \
        src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs
git commit -m "fix(telemetry): IsAllDataRequested guards + empty-Guid CorrelationId skip + truncation composed"
```

---

### Task 17: Empty-Guid CorrelationId skip (verification)

This was bundled into Task 16 (the per-message tag wrap includes the empty-Guid check). This task is a **verification-only** test that the empty-Guid skip is in place.

**Files:**
- Modify: `src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs`

- [ ] **Step 1: Write the test**

```csharp
    [Fact]
    public void Publish_EmptyCorrelationId_DoesNotSetConversationIdTag()
    {
        var args = new PublishEventArgs
        {
            Message = new Message(Guid.Empty),  // Guid.Empty CorrelationId
            Exchange = "exchange",
            Headers = new Dictionary<string, string>(),
        };

        using var activity = ServiceConnectActivitySource.Publish(args, _options, _attrs);
        Assert.NotNull(activity);
        Assert.Null(activity.GetTagItem(MessagingAttributes.MessageConversationId));
    }

    [Fact]
    public void Publish_NonEmptyCorrelationId_SetsConversationIdTag()
    {
        var cid = Guid.NewGuid();
        var args = new PublishEventArgs
        {
            Message = new Message(cid),
            Exchange = "exchange",
            Headers = new Dictionary<string, string>(),
        };

        using var activity = ServiceConnectActivitySource.Publish(args, _options, _attrs);
        Assert.NotNull(activity);
        Assert.Equal(cid.ToString(), activity.GetTagItem(MessagingAttributes.MessageConversationId)?.ToString());
    }
```

- [ ] **Step 2: Run tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~EmptyCorrelationId_DoesNot|FullyQualifiedName~NonEmptyCorrelationId_Sets" -m:1
```

Expected: PASS (both — Task 16's implementation already includes the skip).

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs
git commit -m "test(telemetry): regression coverage for empty-Guid CorrelationId tag skip"
```

---

### Task 18: H4 — multi-bus validation tests

**Files:**
- Modify: `src/ServiceConnect.UnitTests/Telemetry/TelemetryBuilderExtensionsTests.cs`

These tests validate that the static-removal achieves per-bus isolation. No production-code changes — the impl was Task 3 + Task 6.

- [ ] **Step 1: Write the tests**

```csharp
    [Fact]
    public void AddTelemetry_TwoBuilders_ProduceDistinctOptionsInstances()
    {
        var builderA = new ServiceConnectBuilder();
        var builderB = new ServiceConnectBuilder();

        builderA.AddTelemetry(o => o.EnablePublishTelemetry = true);
        builderB.AddTelemetry(o => o.EnablePublishTelemetry = false);

        var servicesA = new ServiceCollection();
        foreach (var reg in builderA.AdditionalRegistrations) reg(servicesA);
        var optionsA = servicesA.BuildServiceProvider().GetRequiredService<ServiceConnectInstrumentationOptions>();

        var servicesB = new ServiceCollection();
        foreach (var reg in builderB.AdditionalRegistrations) reg(servicesB);
        var optionsB = servicesB.BuildServiceProvider().GetRequiredService<ServiceConnectInstrumentationOptions>();

        Assert.NotSame(optionsA, optionsB);
        Assert.True(optionsA.EnablePublishTelemetry);
        Assert.False(optionsB.EnablePublishTelemetry);
    }

    [Fact]
    public void AddTelemetry_UserRegisteredAttributes_WinOverDefault()
    {
        var builder = new ServiceConnectBuilder();

        // User registers a custom IMessagingSystemAttributes BEFORE AddTelemetry.
        var customAttrs = new TestKafkaMessagingSystemAttributes();
        builder.AddRegistration(s => s.AddSingleton<IMessagingSystemAttributes>(customAttrs));

        builder.AddTelemetry();

        var services = new ServiceCollection();
        foreach (var reg in builder.AdditionalRegistrations) reg(services);

        var resolved = services.BuildServiceProvider().GetRequiredService<IMessagingSystemAttributes>();
        Assert.Same(customAttrs, resolved);
    }

    [Fact]
    public void AddTelemetry_NoUserAttributesRegistration_DefaultsToRabbitMq()
    {
        var builder = new ServiceConnectBuilder();
        builder.AddTelemetry();

        var services = new ServiceCollection();
        foreach (var reg in builder.AdditionalRegistrations) reg(services);

        var resolved = services.BuildServiceProvider().GetRequiredService<IMessagingSystemAttributes>();
        Assert.IsType<RabbitMqMessagingSystemAttributes>(resolved);
    }

    private sealed class TestKafkaMessagingSystemAttributes : IMessagingSystemAttributes
    {
        public string MessagingSystem => "kafka";
        public string ProtocolName => "kafka";
    }
```

- [ ] **Step 2: Run tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~AddTelemetry_TwoBuilders_ProduceDistinctOptionsInstances|FullyQualifiedName~AddTelemetry_UserRegisteredAttributes_WinOverDefault|FullyQualifiedName~AddTelemetry_NoUserAttributesRegistration_DefaultsToRabbitMq" -m:1
```

Expected: all 3 pass.

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.UnitTests/Telemetry/TelemetryBuilderExtensionsTests.cs
git commit -m "test(telemetry): H4 — multi-bus per-instance options + user-attribute override"
```

---

### Task 19: Single-source verification test

**Files:**
- Modify: `src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs`

- [ ] **Step 1: Write the test**

```csharp
    [Fact]
    public void Publish_Send_Consume_AllEmitOnSingleActivitySource()
    {
        var sourceNames = new HashSet<string>();
        var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a => sourceNames.Add(a.Source.Name),
        };
        ActivitySource.AddActivityListener(listener);
        try
        {
            using (ServiceConnectActivitySource.Publish(
                new PublishEventArgs { Message = new Message(Guid.NewGuid()), Exchange = "x", Headers = new Dictionary<string, string>() },
                _options, _attrs)) { }
            using (ServiceConnectActivitySource.Send(
                new SendEventArgs { Message = new Message(Guid.NewGuid()), EndPoint = "q", Headers = new Dictionary<string, string>() },
                _options, _attrs)) { }
            using (ServiceConnectActivitySource.Consume(
                new ConsumeEventArgs { Message = new byte[] { 1 }, Type = "T", Headers = new Dictionary<string, object>() },
                _options, _attrs)) { }

            Assert.Single(sourceNames);
            Assert.Contains(ServiceConnectActivitySource.ActivitySourceName, sourceNames);
        }
        finally
        {
            listener.Dispose();
        }
    }
```

- [ ] **Step 2: Run test**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Publish_Send_Consume_AllEmitOnSingleActivitySource" -m:1
```

Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs
git commit -m "test(telemetry): regression coverage — Publish/Send/Consume share single ActivitySource"
```

---

## Group C — Documentation + sample

### Task 20: Update website telemetry reference page

**Files:**
- Modify: `website/src/content/docs/reference/telemetry/*.mdx`

- [ ] **Step 1: List the telemetry reference pages**

```bash
ls website/src/content/docs/reference/telemetry/
```

Read each page end-to-end before editing.

- [ ] **Step 2: Update for new API + DI shape**

For every page that documents `ServiceConnectActivitySource`:
- Update method signatures to include `options` and `attributes` parameters.
- Remove references to the static `Options` and `MessagingSystemAttributes` properties.
- Replace any `AddSource(ServiceConnectActivitySource.PublishActivitySourceName)` (or Consume/Send variants) with `AddSource(ServiceConnectActivitySource.ActivitySourceName)` or the literal `"ServiceConnect.Bus"`.
- Document the new options: `MaxTagValueLength` (default 256, set to `int.MaxValue` to disable), `ExceptionMessageSanitiser` (PII redaction).
- Document the `Shutdown()` method for collectible-ALC scenarios.
- Document the DI registration story: `AddTelemetry` registers `ServiceConnectInstrumentationOptions` and `IMessagingSystemAttributes` (via `TryAddSingleton`). Users override `IMessagingSystemAttributes` by registering before `AddTelemetry`.

- [ ] **Step 3: Build website**

```bash
npm --prefix website install
npm --prefix website run build
```

Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add website/src/content/docs/reference/telemetry/
git commit -m "docs(website): update telemetry reference for v7 API + DI shape"
```

---

### Task 21: Update website learn/operations observability page

**Files:**
- Modify: `website/src/content/docs/learn/operations/*.mdx` (whichever covers observability/telemetry)

- [ ] **Step 1: Find the relevant page(s)**

```bash
grep -rln "ServiceConnectActivitySource\|telemetry\|Telemetry\|OpenTelemetry\|OTel" website/src/content/docs/learn/
```

- [ ] **Step 2: Update OTel listener-registration examples**

Wherever a code sample shows OTel listener registration, replace three-source registration:

```csharp
.WithTracing(t => t
    .AddSource(ServiceConnectActivitySource.PublishActivitySourceName)
    .AddSource(ServiceConnectActivitySource.ConsumeActivitySourceName)
    .AddSource(ServiceConnectActivitySource.SendActivitySourceName))
```

with single-source:

```csharp
.WithTracing(t => t
    .AddSource(ServiceConnectActivitySource.ActivitySourceName))
// or equivalently: .AddSource("ServiceConnect.Bus")
```

- [ ] **Step 3: Build website + commit**

```bash
npm --prefix website run build
git add website/src/content/docs/learn/
git commit -m "docs(website): update observability page for single ActivitySource"
```

---

### Task 22: Update website releases.mdx

**Files:**
- Modify: `website/src/content/docs/releases.mdx`

- [ ] **Step 1: Add v7 entries**

Add to the v7 section (or create one if missing, mirroring Phase 1's existing entries shape):

```markdown
**Breaking — telemetry static state removed.** `ServiceConnectActivitySource.Options` and `ServiceConnectActivitySource.MessagingSystemAttributes` are gone. The public `Publish`/`Send`/`Consume`/`SetError` methods now take `ServiceConnectInstrumentationOptions options` and `IMessagingSystemAttributes attributes` as parameters. Two buses in the same process can now have truly independent enrichment delegates and enable-flags. The middlewares pick both up via DI; user code calling `ServiceConnectActivitySource` directly must update.

**Breaking — single ActivitySource.** The three sources `ServiceConnect.Bus.Publish`, `ServiceConnect.Bus.Consume`, and `ServiceConnect.Bus.Send` collapse into one named `"ServiceConnect.Bus"`. OTel listener config simplifies to `AddSource("ServiceConnect.Bus")`. Per-direction enablement remains via `EnablePublishTelemetry` / `EnableConsumeTelemetry` / `EnableSendTelemetry` flags.

**New — `MaxTagValueLength` and `ExceptionMessageSanitiser` options.** `MaxTagValueLength` (default 256) bounds user-controlled string tags (destination, routing key, MessageId, conversation id) for cardinality safety. `ExceptionMessageSanitiser` (default null) lets callers redact PII in exception messages before they reach activity status descriptions and `exception.message` event tags.

**Fix — telemetry activity leak on enricher OCE.** The user-supplied `EnrichWithMessage`/`EnrichWithMessageBytes` delegate rethrows `OperationCanceledException` by design. Previously, an OCE from the enricher leaked the started activity (the call site never reached the middleware's `finally { activity?.Dispose(); }`). The fix wraps post-`StartActivity` work in `try { ... } catch { activity.Dispose(); throw; }` at all three call sites.

**Fix — body copy gated on listener presence.** `TelemetryProcessingMiddleware` previously copied `envelope.Body.ToArray()` for every consumed message regardless of whether any `ActivityListener` was attached. The fix gates the allocation on a new `ServiceConnectActivitySource.IsConsumeTelemetryEnabled(options)` predicate.
```

- [ ] **Step 2: Build website + commit**

```bash
npm --prefix website run build
git add website/src/content/docs/releases.mdx
git commit -m "docs(website): v7 release notes — telemetry correctness sweep"
```

---

### Task 23: Update `examples/Telemetry/` sample

**Files:**
- Modify: `examples/Telemetry/src/...` (Program.cs and any code that registers OTel)
- Modify: `examples/Telemetry/README.md`

- [ ] **Step 1: List the sample's source files**

```bash
find examples/Telemetry -name "*.cs" -not -path "*/obj/*" -not -path "*/bin/*"
ls examples/Telemetry/
```

- [ ] **Step 2: Update OTel registration**

Find the OTel listener registration. Likely shape today:

```csharp
.WithTracing(t => t
    .AddSource(ServiceConnectActivitySource.PublishActivitySourceName)
    .AddSource(ServiceConnectActivitySource.ConsumeActivitySourceName)
    .AddSource(ServiceConnectActivitySource.SendActivitySourceName))
```

Replace with:

```csharp
.WithTracing(t => t.AddSource(ServiceConnectActivitySource.ActivitySourceName))
```

- [ ] **Step 3: Update any code that reads `ServiceConnectActivitySource.Options`**

If the sample reads or writes `ServiceConnectActivitySource.Options`, migrate to constructing options locally and passing them through (or, for consumers using `AddTelemetry(opts => ...)`, the configure callback already gives access — no `Options =` write needed).

- [ ] **Step 4: Update README**

If the README documents the three source names, update to a single source. If it shows `AddTelemetry` examples, double-check they still match.

- [ ] **Step 5: Build sample**

```bash
find examples/Telemetry -name "*.csproj" -not -path "*/obj/*" | while read csproj; do
  dotnet build "$csproj"
done
```

Expected: all succeed.

- [ ] **Step 6: Commit**

```bash
git add examples/Telemetry/
git commit -m "docs(example): update Telemetry sample for v7 single-source API"
```

---

### Task 24: Final verification

**Files:**
- (none modified — verification only)

- [ ] **Step 1: Repo-wide grep for stale references**

```bash
grep -rln \
  -e "ServiceConnectActivitySource\.Options" \
  -e "ServiceConnectActivitySource\.MessagingSystemAttributes" \
  -e "PublishActivitySourceName" \
  -e "ConsumeActivitySourceName" \
  -e "SendActivitySourceName" \
  --include="*.cs" --include="*.csproj" --include="*.slnx" --include="*.sln" --include="*.mdx" --include="*.md" \
  src/ examples/ website/src/ README.md \
  2>/dev/null
```

Expected: empty output. Allowed exceptions: matches in `docs/superpowers/` (planning docs), `website/dist/` (build artifacts).

If any other matches surface, investigate and fix before reporting DONE.

- [ ] **Step 2: Per-csproj build of every relevant project**

```bash
dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj
dotnet build src/ServiceConnect/ServiceConnect.csproj
dotnet build src/ServiceConnect.Telemetry/ServiceConnect.Telemetry.csproj
dotnet build src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj
```

Expected: all succeed.

- [ ] **Step 3: Run the relevant tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Telemetry|FullyQualifiedName~ActivitySource" -m:1
```

Expected: all pass.

- [ ] **Step 4: Astro build**

```bash
npm --prefix website run build
```

Expected: succeeds, no broken-link warnings.

- [ ] **Step 5: Smoke-run the Telemetry sample (optional but recommended)**

If `examples/Telemetry` has a `run.sh`, smoke-test it:

```bash
cd examples/Telemetry && ./run.sh && cd -
```

Verify spans emit on the new single source and the OTel exporter receives them. If `run.sh` is missing or the sample doesn't have a smoke harness, skip this step (not a Phase 2 deliverable).

- [ ] **Step 6: Code review (optional but recommended)**

Per the phase doc's general guidance, invoke `superpowers:requesting-code-review` against the diff range before merging. Reviewer should confirm:
- All five C-level tests cover Phase 2's canonical scenarios.
- No stale static-state references survive.
- Sample registers OTel against the single source.
- Multi-bus story is documented in the website reference.

---

## Self-review

**Spec coverage check.** Walking through `docs/superpowers/specs/2026-04-29-phase-02-telemetry-correctness.md`:

- `MaxTagValueLength` + `ExceptionMessageSanitiser` options → Task 1.
- ActivitySource consolidation + Shutdown + IsXTelemetryEnabled helpers → Task 2.
- API migration (signatures, middleware DI, AddTelemetry) → Task 3.
- Existing test migration → Task 4.
- C6 (Publish/Send/Consume enricher OCE) → Tasks 5/6/7.
- C7 (body-copy gate) → Task 8.
- L11 (parent context) → Task 9.
- L12 (TryParse explicit) → Task 10.
- L13 (single inject) → Task 11.
- L14 (carrier-shape warning) → Task 12.
- L15 (Success=false no-exception error tag) → Task 13.
- L17 (`MaxTagValueLength` truncation) → Task 14.
- ExceptionMessageSanitiser wiring → Task 15.
- IsAllDataRequested guards + empty-Guid skip + truncation composed → Task 16.
- Empty-Guid CorrelationId regression coverage → Task 17.
- H4 multi-bus validation → Task 18.
- Single-source verification → Task 19.
- Documentation + sample + final verification → Tasks 20–24.

Every spec requirement maps to a task.

**Placeholder scan.** No "TBD" / "TODO" / "fill in details" patterns. Three places use existing-test arrange-block reuse (Task 4 directs the implementer to inspect existing fixtures rather than reproducing 744 lines inline) — this is calibrated guidance, not a placeholder.

**Type / signature consistency.**
- `Publish(eventArgs, options, attributes, linkedContext = default)` — same signature in Tasks 3, 5, 9, 11, 14.
- `Send(eventArgs, options, attributes, linkedContext = default)` — same in Tasks 3, 6.
- `Consume(eventArgs, options, attributes)` — same in Tasks 3, 7, 10.
- `SetError(activity, exception, options)` — same in Tasks 3, 13, 15.
- `IsConsumeTelemetryEnabled(options)` / `IsPublishTelemetryEnabled` / `IsSendTelemetryEnabled` — same in Tasks 2, 8.
- `Truncate(value, maxLength)` — same in Tasks 14, 16.
- `ServiceConnectInstrumentationOptions` properties (`MaxTagValueLength`, `ExceptionMessageSanitiser`) — same throughout.
- `IMessagingSystemAttributes` registration shape (`TryAddSingleton`) — same in Tasks 3, 18.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-04-29-phase-02-telemetry-correctness.md`. Two execution options:

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?
