# Group B — Metrics Rollout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add operator-grade metrics (six instruments under the OTel `messaging.*` namespace) and operability log enrichments (four connection-lifecycle Info logs + two ack/nack MessageId enrichments) to ServiceConnect.

**Architecture:** Module-static `ServiceConnectMeter` in `ServiceConnect` core hosts a `Meter` named `"ServiceConnect.Bus"` and nine instruments. Always-on emission — instruments are zero-cost when no listener subscribes (BCL pattern). Emit sites in producer + consumer call static recorder methods unconditionally. `ServiceConnect.Telemetry` gains a thin `AddServiceConnectInstrumentation()` helper for OpenTelemetry users; everyone else calls `MeterProvider.AddMeter("ServiceConnect.Bus")` directly. Logging additions extend the existing source-generated `RabbitMqClientLog` partial class.

**Tech Stack:** .NET 8/10, C# 14, `System.Diagnostics.Metrics` (BCL — `Meter`, `Counter<T>`, `Histogram<T>`, `UpDownCounter<T>`, `MeterListener`), `Microsoft.Extensions.Logging.Abstractions` (already in scope, source-gen logger via `[LoggerMessage]`), `Microsoft.Extensions.Diagnostics.Testing` (FakeLogger — already added in Group C), xUnit + Moq.

---

## File structure

| File | Item | Action |
|---|---|---|
| `src/ServiceConnect/Diagnostics/ServiceConnectMeter.cs` | foundation | Create — static class with `Meter`, nine instruments, recorder methods |
| `src/ServiceConnect/Diagnostics/MetricNames.cs` | foundation | Create — `public const string` constants |
| `src/ServiceConnect/Diagnostics/ExceptionTypeMapper.cs` | foundation | Create — `error.type` allow-list mapper |
| `src/ServiceConnect.Telemetry/TelemetryMeterExtensions.cs` | foundation | Create — `AddServiceConnectInstrumentation` extension |
| `src/ServiceConnect.UnitTests/Diagnostics/ServiceConnectMeterTests.cs` | foundation | Create — Meter name + instrument inventory + ExceptionTypeMapper tests |
| `src/ServiceConnect.UnitTests/Telemetry/TelemetryMeterExtensionsTests.cs` | foundation | Create — extension method test |
| `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs` | OTel-standard | Modify — wrap `PublishAsync`/`SendAsync`/`SendBytesAsync` with publish-duration histogram + published-messages counter |
| `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` | OTel-standard | Modify — wrap `ProcessAsync` call with process-duration histogram + consumed-messages counter; surface `_messagesBeingProcessed` as UpDownCounter (Task 3) |
| `src/ServiceConnect.UnitTests/RabbitMQ/ProducerPublishMetricsTests.cs` | OTel-standard | Create |
| `src/ServiceConnect.UnitTests/RabbitMQ/ConsumerProcessMetricsTests.cs` | OTel-standard | Create |
| `src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs` | SC extensions | Modify — emit `RetryAttempts` counter at increment site |
| `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs` | SC extensions | Modify — emit `RetryDrops` at retry-publish-failure catch; emit `AuditDrops` at audit-failure catch |
| `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs` | SC extensions | Modify — emit `PublishConfirmTimeouts` at TimeoutException catch in `PublishWithTimeoutAsync` |
| `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` | SC extensions | Modify — increment/decrement `InFlightMessages` UpDownCounter alongside `_messagesBeingProcessed` |
| `src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerMetricsTests.cs` | SC extensions | Create |
| `src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorMetricsTests.cs` | SC extensions | Create |
| `src/ServiceConnect.UnitTests/RabbitMQ/ProducerConfirmTimeoutMetricsTests.cs` | SC extensions | Create |
| `src/ServiceConnect.Client.RabbitMQ/RabbitMqClientLog.cs` | logging | Modify — add EventIds 2-7 (4 lifecycle + 2 ack/nack failure) |
| `src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs` | logging | Modify — emit `ConnectionOpened`; subscribe to `RecoverySucceeded` + `ConnectionShutdown`; unsubscribe at dispose |
| `src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs` | logging | Modify — emit `ProducerConnectionOpened`; subscribe to recovery/shutdown; unsubscribe at dispose |
| `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` | logging | Modify — replace ack/nack failure `LogWarning` calls with source-gen `AckFailed`/`NackFailed` carrying `MessageId` |
| `src/ServiceConnect.UnitTests/RabbitMQ/ConnectionLifecycleLogsTests.cs` | logging | Create |
| `src/ServiceConnect.UnitTests/RabbitMQ/AckNackFailureLogsTests.cs` | logging | Create |
| `website/src/content/docs/learn/operations/observability.mdx` | docs | Modify — add `## Metrics` section with catalogue + connection-lifecycle log table |
| `website/src/content/docs/releases.mdx` | docs | Modify — add v8 highlight subsection |
| `architecture-fix-plan.md` | close | Modify — mark Group B done |

---

## Important implementation note: `messaging.outcome` value set

The spec named four values `{success, error, retry, drop}` for `messaging.client.consumed.messages.{outcome}`. After scouting `RabbitMqConsumerHost.OnMessageReceivedAsync` and `InboundMessageProcessor`, the host-level emit site can only distinguish three outcomes:

- `success` — `processed=true` after `ProcessAsync` returns; ack dispatched.
- `error` — exception caught in the host-level `try/catch` at the `ProcessAsync` call.
- `retry` — `processed=false`; nack-with-requeue dispatched; broker will redeliver.

The `drop` case (retry-publish-failure path inside `InboundMessageProcessor.cs:150-158`) is invisible at the host level — `InboundMessageProcessor` swallows the exception, sets `processed=true` so the original message gets acked to break the redelivery loop, and the host emits `outcome=success` for that delivery.

**Resolution:** the host-level `messaging.client.consumed.messages` counter carries `outcome={success, error, retry}` (three values). The `drop` case is captured separately on `messaging.serviceconnect.retry.drops` which emits from the actual decision site. The plan's emit-site map (Task 3 Step 4) and the docs (Task 5) reflect this.

---

## Task 1: Foundation — `ServiceConnectMeter` + `MetricNames` + helpers

Lays the foundation: the static `Meter`, the nine instruments, the constants module, the exception-type mapper, the OTel registration helper. No call sites yet — those come in Tasks 2-4.

**Files:**
- Create: `src/ServiceConnect/Diagnostics/ServiceConnectMeter.cs`
- Create: `src/ServiceConnect/Diagnostics/MetricNames.cs`
- Create: `src/ServiceConnect/Diagnostics/ExceptionTypeMapper.cs`
- Create: `src/ServiceConnect.Telemetry/TelemetryMeterExtensions.cs`
- Create: `src/ServiceConnect.UnitTests/Diagnostics/ServiceConnectMeterTests.cs`
- Create: `src/ServiceConnect.UnitTests/Telemetry/TelemetryMeterExtensionsTests.cs`

- [ ] **Step 1: Verify the OpenTelemetry.Api dependency**

The `AddServiceConnectInstrumentation` helper takes a `MeterProviderBuilder` argument from `OpenTelemetry.Api`. Check:

```bash
grep -E "PackageReference|ProjectReference" src/ServiceConnect.Telemetry/ServiceConnect.Telemetry.csproj
grep -rn "MeterProviderBuilder" src/ServiceConnect.Telemetry/ --include="*.cs"
```

If `OpenTelemetry.Api` is already a transitive or direct dependency, no action. If not, add it:

```xml
<PackageReference Include="OpenTelemetry.Api" Version="1.10.0" />
```

(Check the latest stable 1.x version on nuget.org at implementation time. Match the version any neighbouring project already uses.)

If you can't find or add the package, fall back: skip Step 5 of this task (the helper) and document in the docs task that users call `MeterProvider.AddMeter("ServiceConnect.Bus")` directly. Update the plan's Task 5 docs accordingly. Report the deviation.

- [ ] **Step 2: Create `MetricNames.cs`**

Create `src/ServiceConnect/Diagnostics/MetricNames.cs`:

```csharp
namespace ServiceConnect.Diagnostics;

/// <summary>
/// Names of metrics emitted by ServiceConnect. Exposed as <c>public const string</c>
/// so consumers (Grafana templates, custom <see cref="System.Diagnostics.Metrics.MeterListener"/>,
/// alert rules) can reference them without re-typing strings.
/// </summary>
/// <remarks>
/// OTel-standard names (<c>messaging.publish.duration</c>, <c>messaging.process.duration</c>,
/// <c>messaging.client.published.messages</c>, <c>messaging.client.consumed.messages</c>) follow
/// the <see href="https://opentelemetry.io/docs/specs/semconv/messaging/messaging-metrics/">OpenTelemetry
/// messaging-metrics semantic conventions</see>. ServiceConnect-specific extensions live under the
/// <c>messaging.serviceconnect.*</c> sub-namespace.
/// </remarks>
public static class MetricNames
{
    /// <summary>Histogram (seconds) — duration of a publish operation, broker ack to ack.</summary>
    public const string PublishDuration = "messaging.publish.duration";

    /// <summary>Histogram (seconds) — duration of consumer-side message processing (handler dispatch).</summary>
    public const string ProcessDuration = "messaging.process.duration";

    /// <summary>Counter — number of messages successfully published.</summary>
    public const string PublishedMessages = "messaging.client.published.messages";

    /// <summary>Counter — number of messages consumed, tagged by <c>messaging.outcome</c>.</summary>
    public const string ConsumedMessages = "messaging.client.consumed.messages";

    /// <summary>Counter — number of consumer-side retry attempts (header-counter increments).</summary>
    public const string RetryAttempts = "messaging.serviceconnect.retry.attempts";

    /// <summary>Counter — number of messages dropped because retry publishing failed.</summary>
    public const string RetryDrops = "messaging.serviceconnect.retry.drops";

    /// <summary>Counter — number of publishes that exceeded the configured publish timeout waiting for broker ack.</summary>
    public const string PublishConfirmTimeouts = "messaging.serviceconnect.publish.confirm_timeouts";

    /// <summary>Counter — number of audit messages that failed to publish.</summary>
    public const string AuditDrops = "messaging.serviceconnect.audit.drops";

    /// <summary>UpDownCounter — current count of in-flight (dispatched but not acked) consumer messages.</summary>
    public const string InFlightMessages = "messaging.serviceconnect.process.messages.inflight";
}
```

- [ ] **Step 3: Create `ExceptionTypeMapper.cs`**

Create `src/ServiceConnect/Diagnostics/ExceptionTypeMapper.cs`:

```csharp
namespace ServiceConnect.Diagnostics;

/// <summary>
/// Maps an exception to a stable, low-cardinality string suitable for the OpenTelemetry
/// <c>error.type</c> tag on metric records. Uses an allow-list for common .NET exception
/// types and falls back to <see cref="System.Type.Name"/> (the type's short name).
/// </summary>
/// <remarks>
/// The exception <i>message</i> is never used — message text is unbounded cardinality and
/// would explode metric series counts.
/// </remarks>
internal static class ExceptionTypeMapper
{
    public static string Map(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            OperationCanceledException => "cancelled",
            TimeoutException => "timeout",
            _ => exception.GetType().Name,
        };
    }
}
```

- [ ] **Step 4: Create `ServiceConnectMeter.cs`**

Create `src/ServiceConnect/Diagnostics/ServiceConnectMeter.cs`:

```csharp
using System.Diagnostics.Metrics;
using System.Reflection;

namespace ServiceConnect.Diagnostics;

/// <summary>
/// Hosts the <see cref="System.Diagnostics.Metrics.Meter"/> and instruments emitted by
/// ServiceConnect. Always-on: instruments are zero-cost when no listener has subscribed,
/// matching the pattern used by .NET BCL libraries (<c>HttpClient</c>, <c>EFCore</c>).
/// </summary>
/// <remarks>
/// Subscribers wire the meter via <c>MeterProvider.AddMeter("ServiceConnect.Bus")</c> or, on
/// OpenTelemetry, via <c>builder.AddServiceConnectInstrumentation()</c> from
/// <c>ServiceConnect.Telemetry</c>.
/// </remarks>
public static class ServiceConnectMeter
{
    /// <summary>The meter name used by every ServiceConnect instrument.</summary>
    public const string MeterName = "ServiceConnect.Bus";

    private static readonly string Version =
        typeof(ServiceConnectMeter).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private static readonly Meter _meter = new(MeterName, Version);

    private static readonly Histogram<double> _publishDuration = _meter.CreateHistogram<double>(
        name: MetricNames.PublishDuration,
        unit: "s",
        description: "Duration of a publish operation, from start to broker ack.");

    private static readonly Histogram<double> _processDuration = _meter.CreateHistogram<double>(
        name: MetricNames.ProcessDuration,
        unit: "s",
        description: "Duration of consumer-side message processing (handler dispatch).");

    private static readonly Counter<long> _publishedMessages = _meter.CreateCounter<long>(
        name: MetricNames.PublishedMessages,
        unit: "{message}",
        description: "Number of messages successfully published.");

    private static readonly Counter<long> _consumedMessages = _meter.CreateCounter<long>(
        name: MetricNames.ConsumedMessages,
        unit: "{message}",
        description: "Number of messages consumed, tagged by outcome.");

    private static readonly Counter<long> _retryAttempts = _meter.CreateCounter<long>(
        name: MetricNames.RetryAttempts,
        unit: "{attempt}",
        description: "Consumer-side retry attempts (header-counter increments).");

    private static readonly Counter<long> _retryDrops = _meter.CreateCounter<long>(
        name: MetricNames.RetryDrops,
        unit: "{drop}",
        description: "Messages dropped because retry publishing failed.");

    private static readonly Counter<long> _publishConfirmTimeouts = _meter.CreateCounter<long>(
        name: MetricNames.PublishConfirmTimeouts,
        unit: "{timeout}",
        description: "Publishes that exceeded the configured publish timeout waiting for broker ack.");

    private static readonly Counter<long> _auditDrops = _meter.CreateCounter<long>(
        name: MetricNames.AuditDrops,
        unit: "{drop}",
        description: "Audit messages that failed to publish.");

    private static readonly UpDownCounter<long> _inFlightMessages = _meter.CreateUpDownCounter<long>(
        name: MetricNames.InFlightMessages,
        unit: "{message}",
        description: "Current count of in-flight (dispatched but not acked) consumer messages.");

    /// <summary>Records a publish duration in seconds with the given tags.</summary>
    public static void RecordPublishDuration(double seconds, in TagList tags)
        => _publishDuration.Record(seconds, tags);

    /// <summary>Records a consumer-side process duration in seconds with the given tags.</summary>
    public static void RecordProcessDuration(double seconds, in TagList tags)
        => _processDuration.Record(seconds, tags);

    /// <summary>Increments the published-messages counter by 1 with the given tags.</summary>
    public static void AddPublishedMessage(in TagList tags) => _publishedMessages.Add(1, tags);

    /// <summary>Increments the consumed-messages counter by 1 with the given tags.</summary>
    public static void AddConsumedMessage(in TagList tags) => _consumedMessages.Add(1, tags);

    /// <summary>Increments the retry-attempts counter by 1 with the given tags.</summary>
    public static void AddRetryAttempt(in TagList tags) => _retryAttempts.Add(1, tags);

    /// <summary>Increments the retry-drops counter by 1 with the given tags.</summary>
    public static void AddRetryDrop(in TagList tags) => _retryDrops.Add(1, tags);

    /// <summary>Increments the publish-confirm-timeouts counter by 1 with the given tags.</summary>
    public static void AddPublishConfirmTimeout(in TagList tags) => _publishConfirmTimeouts.Add(1, tags);

    /// <summary>Increments the audit-drops counter by 1 with the given tags.</summary>
    public static void AddAuditDrop(in TagList tags) => _auditDrops.Add(1, tags);

    /// <summary>Adjusts the in-flight UpDownCounter by <paramref name="delta"/> with the given tags.</summary>
    public static void AddInFlight(long delta, in TagList tags) => _inFlightMessages.Add(delta, tags);
}
```

`TagList` is the BCL struct from `System.Diagnostics`. Pass-by-`in` keeps it stack-allocated.

- [ ] **Step 5: Create `TelemetryMeterExtensions.cs`**

Create `src/ServiceConnect.Telemetry/TelemetryMeterExtensions.cs`:

```csharp
using OpenTelemetry.Metrics;
using ServiceConnect.Diagnostics;

namespace ServiceConnect.Telemetry;

/// <summary>
/// OpenTelemetry registration helpers for ServiceConnect's <see cref="ServiceConnectMeter"/>.
/// </summary>
public static class TelemetryMeterExtensions
{
    /// <summary>
    /// Subscribes the OpenTelemetry MeterProvider to ServiceConnect's <c>"ServiceConnect.Bus"</c> meter.
    /// Equivalent to <c>builder.AddMeter(ServiceConnectMeter.MeterName)</c>.
    /// </summary>
    public static MeterProviderBuilder AddServiceConnectInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddMeter(ServiceConnectMeter.MeterName);
    }
}
```

If Step 1 found `OpenTelemetry.Api` is not present, omit this step and the `TelemetryMeterExtensionsTests.cs` test in Step 7. Move on.

- [ ] **Step 6: Write the foundation test**

Create `src/ServiceConnect.UnitTests/Diagnostics/ServiceConnectMeterTests.cs`:

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;
using ServiceConnect.Diagnostics;
using Xunit;

namespace ServiceConnect.UnitTests.Diagnostics;

public class ServiceConnectMeterTests
{
    [Fact]
    public void MeterName_Is_ServiceConnectBus()
    {
        Assert.Equal("ServiceConnect.Bus", ServiceConnectMeter.MeterName);
    }

    [Fact]
    public void RecordPublishDuration_EmitsOnPublishDurationInstrument()
    {
        var captured = new List<(string Name, double Value)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ServiceConnectMeter.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            captured.Add((instrument.Name, value)));
        listener.Start();

        ServiceConnectMeter.RecordPublishDuration(0.123, new TagList());

        var record = Assert.Single(captured);
        Assert.Equal(MetricNames.PublishDuration, record.Name);
        Assert.Equal(0.123, record.Value);
    }

    [Fact]
    public void AddPublishedMessage_IncrementsPublishedMessagesCounter()
    {
        var captured = new List<(string Name, long Value)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ServiceConnectMeter.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            captured.Add((instrument.Name, value)));
        listener.Start();

        ServiceConnectMeter.AddPublishedMessage(new TagList());

        var record = Assert.Single(captured);
        Assert.Equal(MetricNames.PublishedMessages, record.Name);
        Assert.Equal(1, record.Value);
    }

    [Fact]
    public void AddInFlight_AdjustsUpDownCounterByDelta()
    {
        long total = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ServiceConnectMeter.MeterName
                    && instrument.Name == MetricNames.InFlightMessages)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => total += value);
        listener.Start();

        ServiceConnectMeter.AddInFlight(1, new TagList());
        ServiceConnectMeter.AddInFlight(1, new TagList());
        ServiceConnectMeter.AddInFlight(-1, new TagList());

        Assert.Equal(1, total);
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException), "cancelled")]
    [InlineData(typeof(TimeoutException), "timeout")]
    [InlineData(typeof(InvalidOperationException), "InvalidOperationException")]
    [InlineData(typeof(ArgumentException), "ArgumentException")]
    public void ExceptionTypeMapper_MapsKnownTypesAndFallsBackToShortName(Type exceptionType, string expected)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "test")!;

        Assert.Equal(expected, ExceptionTypeMapper.Map(exception));
    }
}
```

The `ExceptionTypeMapper.Map` method is `internal`, but `[InternalsVisibleTo("ServiceConnect.UnitTests")]` is already declared on the `ServiceConnect` core csproj — verify by `grep InternalsVisibleTo src/ServiceConnect/ServiceConnect.csproj`. If not declared, add it before running the test.

- [ ] **Step 7: Write the OTel-helper test**

Create `src/ServiceConnect.UnitTests/Telemetry/TelemetryMeterExtensionsTests.cs`:

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using ServiceConnect.Diagnostics;
using ServiceConnect.Telemetry;
using Xunit;

namespace ServiceConnect.UnitTests.Telemetry;

public class TelemetryMeterExtensionsTests
{
    [Fact]
    public void AddServiceConnectInstrumentation_SubscribesToServiceConnectBusMeter()
    {
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddServiceConnectInstrumentation()
            .Build();

        // Build succeeds and the meter provider is non-null — verifies the extension wires
        // through to AddMeter(ServiceConnectMeter.MeterName) without throwing.
        Assert.NotNull(meterProvider);
    }

    [Fact]
    public void AddServiceConnectInstrumentation_ThrowsOnNullBuilder()
    {
        MeterProviderBuilder builder = null!;

        Assert.Throws<ArgumentNullException>(() => builder.AddServiceConnectInstrumentation());
    }
}
```

If Step 5 was skipped because `OpenTelemetry.Api` isn't available, skip this step too.

The unit-tests project may need `OpenTelemetry` package reference for the `Sdk.CreateMeterProviderBuilder` test. Check existing Telemetry tests to see if it's already there: `grep PackageReference src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj | grep OpenTelemetry`. If not, add `<PackageReference Include="OpenTelemetry" Version="<match-the-Telemetry-csproj-version>" />` to the UnitTests csproj.

- [ ] **Step 8: Run the tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ServiceConnectMeterTests|FullyQualifiedName~TelemetryMeterExtensionsTests" -m:1`

Expected:
- `ServiceConnectMeterTests`: 7 passed (3 instrument tests + 4 mapper Theory cases).
- `TelemetryMeterExtensionsTests`: 2 passed (or skipped if Step 5/7 were skipped).

- [ ] **Step 9: Run the full unit-test sweep to confirm no regression**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-restore -m:1 2>&1 | tail -3`
Expected: all tests pass (the known `ProducerPublishTimeoutTimingTests` flake may hit; re-run once if it does).

- [ ] **Step 10: Commit**

```bash
git add src/ServiceConnect/Diagnostics/ServiceConnectMeter.cs \
        src/ServiceConnect/Diagnostics/MetricNames.cs \
        src/ServiceConnect/Diagnostics/ExceptionTypeMapper.cs \
        src/ServiceConnect.Telemetry/TelemetryMeterExtensions.cs \
        src/ServiceConnect.UnitTests/Diagnostics/ServiceConnectMeterTests.cs \
        src/ServiceConnect.UnitTests/Telemetry/TelemetryMeterExtensionsTests.cs

# Stage csproj changes only if Step 1 / Step 7 added package references:
git add src/ServiceConnect.Telemetry/ServiceConnect.Telemetry.csproj 2>/dev/null
git add src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj 2>/dev/null

git commit -m "$(cat <<'EOF'
feat(diagnostics): introduce ServiceConnectMeter

Foundation for Group B metrics rollout. Adds:

- ServiceConnect.Diagnostics.ServiceConnectMeter — module-static class
  hosting a Meter named "ServiceConnect.Bus" and nine instruments:
  publish/process duration histograms, published/consumed message
  counters, four ServiceConnect-specific counters (retry attempts,
  retry drops, publish-confirm timeouts, audit drops), and an
  in-flight UpDownCounter.

- ServiceConnect.Diagnostics.MetricNames — public const string
  constants for every metric name. OTel-standard names where OTel
  defines them; messaging.serviceconnect.* sub-namespace for
  extensions.

- ServiceConnect.Diagnostics.ExceptionTypeMapper — internal helper
  mapping exceptions to stable low-cardinality strings for the OTel
  error.type tag (allow-list with type-short-name fallback; never
  exception.Message).

- ServiceConnect.Telemetry.TelemetryMeterExtensions — thin
  AddServiceConnectInstrumentation() helper for OTel users.

No call sites yet — emit sites land in the next three commits.
Always-on architecture: instruments are zero-cost when no listener
subscribes, matching the BCL pattern (HttpClient, EFCore, Sockets).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: OTel-standard metrics — publish + consume duration + counts

Wires the four OTel-standard instruments into the producer publish path and consumer host process path.

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs` — wrap `PublishAsync`/`SendAsync`/`SendBytesAsync` with `Stopwatch` + emit
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` — wrap the `ProcessAsync` call with `Stopwatch` + emit consumed-messages with outcome
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/ProducerPublishMetricsTests.cs`
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/ConsumerProcessMetricsTests.cs`

- [ ] **Step 1: Write the failing publish-metrics tests**

Create `src/ServiceConnect.UnitTests/RabbitMQ/ProducerPublishMetricsTests.cs`. The tests use a `MeterListener` to capture instrument records and exercise `Producer.PublishAsync` with a mocked `IChannel` whose `BasicPublishAsync` returns a completed task.

Pattern: build a small `MeterListener` test helper (`MetricCollector`) that subscribes to `ServiceConnect.Bus` and exposes `GetRecords(string instrumentName)` returning the list of (value, tags) tuples captured during a test scope.

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ.Producer;
using ServiceConnect.Configuration;
using ServiceConnect.Diagnostics;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public class ProducerPublishMetricsTests
{
    [Fact]
    public async Task PublishAsync_OnSuccess_RecordsPublishDurationAndIncrementsPublishedMessages()
    {
        using var collector = new MetricCollector();

        // Producer setup is delegated to the existing test scaffolding pattern used in
        // ProducerEnsureConnectedTests / ProducerPublishTimeoutTimingTests. Construct a
        // Producer with a mocked IChannel whose BasicPublishAsync returns Task.CompletedTask
        // and a mocked IConnection whose CreateChannelAsync returns the mock channel.
        var producer = await ProducerTestFactory.CreatePublishableAsync();

        await producer.PublishAsync(typeof(SampleMessage), new ReadOnlyMemory<byte>([1, 2, 3]));

        var durations = collector.GetRecords<double>(MetricNames.PublishDuration);
        Assert.Single(durations);
        Assert.True(durations[0].Value >= 0);
        Assert.Equal("rabbitmq", durations[0].GetTag("messaging.system"));
        Assert.Equal("publish", durations[0].GetTag("messaging.operation"));
        Assert.False(string.IsNullOrEmpty(durations[0].GetTag("messaging.destination.name")));

        var counts = collector.GetRecords<long>(MetricNames.PublishedMessages);
        var count = Assert.Single(counts);
        Assert.Equal(1, count.Value);
    }

    [Fact]
    public async Task PublishAsync_OnFailure_DoesNotIncrementPublishedMessages()
    {
        using var collector = new MetricCollector();
        var producer = await ProducerTestFactory.CreatePublishableThatThrowsAsync(
            new InvalidOperationException("simulated"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => producer.PublishAsync(typeof(SampleMessage), new ReadOnlyMemory<byte>([1])));

        // Duration is recorded for both success and failure (it's wall time of the attempt).
        Assert.Single(collector.GetRecords<double>(MetricNames.PublishDuration));
        // Counter only increments on success.
        Assert.Empty(collector.GetRecords<long>(MetricNames.PublishedMessages));
    }

    public sealed class SampleMessage : ServiceConnect.Interfaces.Message
    {
        public SampleMessage() : base(Guid.NewGuid()) { }
    }
}
```

Add a `MetricCollector` test helper if not already present. Suggested location: `src/ServiceConnect.UnitTests/Diagnostics/MetricCollector.cs`:

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;
using ServiceConnect.Diagnostics;

namespace ServiceConnect.UnitTests.Diagnostics;

/// <summary>
/// MeterListener-based helper for unit tests. Subscribes to ServiceConnect.Bus instruments
/// during the test's lifetime and exposes captured records.
/// </summary>
internal sealed class MetricCollector : IDisposable
{
    public sealed record Record<T>(string InstrumentName, T Value, IReadOnlyDictionary<string, object?> Tags)
    {
        public string? GetTag(string key) => Tags.TryGetValue(key, out var v) ? v?.ToString() : null;
    }

    private readonly MeterListener _listener;
    private readonly List<Record<long>> _longRecords = new();
    private readonly List<Record<double>> _doubleRecords = new();

    public MetricCollector()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ServiceConnectMeter.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (_longRecords)
            {
                _longRecords.Add(new(instrument.Name, value, ToDictionary(tags)));
            }
        });
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            lock (_doubleRecords)
            {
                _doubleRecords.Add(new(instrument.Name, value, ToDictionary(tags)));
            }
        });
        _listener.Start();
    }

    public IReadOnlyList<Record<long>> GetRecords<T>(string instrumentName) where T : struct
    {
        if (typeof(T) == typeof(long))
        {
            lock (_longRecords)
            {
                return _longRecords.Where(r => r.InstrumentName == instrumentName).ToList();
            }
        }
        // For double records, this overload won't be used — callers use the typed form below.
        return new List<Record<long>>();
    }

    public IReadOnlyList<Record<double>> GetRecords<T>(string instrumentName, T _ = default) where T : struct, IFloatingPointIeee754<T>
    {
        lock (_doubleRecords)
        {
            return _doubleRecords.Where(r => r.InstrumentName == instrumentName).ToList();
        }
    }

    private static Dictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, object?>(tags.Length, StringComparer.Ordinal);
        foreach (var kv in tags)
        {
            dict[kv.Key] = kv.Value;
        }
        return dict;
    }

    public void Dispose() => _listener.Dispose();
}
```

The two `GetRecords<T>` overloads use type discrimination on the type parameter; if that pattern proves awkward in C#, split into `GetLongRecords(name)` and `GetDoubleRecords(name)` — equivalent functionality. Pick whichever compiles cleanly.

`ProducerTestFactory.CreatePublishableAsync` is a small builder: search `src/ServiceConnect.UnitTests/RabbitMQ/` for any existing `ProducerEnsure*Tests` or `Producer*Tests` that constructs a Producer with mocked dependencies, and either reuse or extract a small factory. If no factory exists, add one in `src/ServiceConnect.UnitTests/RabbitMQ/ProducerTestFactory.cs` with two methods:

```csharp
public static async Task<Producer> CreatePublishableAsync()
{
    // Mock IConnection.CreateChannelAsync → IChannel
    // Mock IChannel.BasicPublishAsync → ValueTask.CompletedTask
    // Mock IChannel.ExchangeDeclareAsync → Task.CompletedTask
    // Mock IChannel.IsOpen → true
    // Construct ProducerConnection with the mocked connection.
    // Construct Producer with the producer connection + minimal queue config.
}

public static async Task<Producer> CreatePublishableThatThrowsAsync(Exception ex) { ... }
```

Refer to `ProducerEnsureConnectedTests.cs` for the mocking pattern.

- [ ] **Step 2: Run the failing tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ProducerPublishMetricsTests" -m:1`
Expected: build error (no metric emit yet) or test fail (metrics empty).

- [ ] **Step 3: Wire publish duration + counter into `Producer.PublishAsync`**

Open `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs`. Add `using` directives at the top:

```csharp
using System.Diagnostics;
using ServiceConnect.Diagnostics;
```

Around line 162-192 (the `PublishAsync` body), wrap the `await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);` through the closing `finally { _publishLock.Release(); }` with metric instrumentation.

Concretely, find the existing block:

```csharp
public async Task PublishAsync(Type type, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(type);
    cancellationToken.ThrowIfCancellationRequested();
    if (body.Length > MaximumMessageSize) { ... throw ... }

    await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
    await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        ObjectDisposedException.ThrowIf(_disposedInt != 0, this);
        ...
        string exchangeName = GetExchangeName(type);

        await ExecuteWithConnectionRetryAsync(async () =>
        {
            await _producerConnection.EnsureExchangeDeclaredAsync(exchangeName, ExchangeType.Fanout, cancellationToken).ConfigureAwait(false);
            await PublishWithTimeoutAsync(...).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
    finally { _publishLock.Release(); }
}
```

Replace with (the diff is: capture `Stopwatch.GetTimestamp()` early; emit duration in `finally`; emit counter on success):

```csharp
public async Task PublishAsync(Type type, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(type);
    cancellationToken.ThrowIfCancellationRequested();
    if (body.Length > MaximumMessageSize) { ... throw ... }   // unchanged

    var startTimestamp = Stopwatch.GetTimestamp();
    string exchangeName = string.Empty;
    bool succeeded = false;
    Exception? failure = null;

    try
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposedInt != 0, this);

            var messageHeaders = _headerBuilder.BuildHeaders(type, headers, _queueConfiguration.QueueName, "Publish");
            var basicProperties = _headerBuilder.BuildBasicProperties(messageHeaders);

            exchangeName = GetExchangeName(type);

            await ExecuteWithConnectionRetryAsync(async () =>
            {
                await _producerConnection.EnsureExchangeDeclaredAsync(exchangeName, ExchangeType.Fanout, cancellationToken).ConfigureAwait(false);

                await PublishWithTimeoutAsync(
                    _producerConnection.Channel,
                    exchangeName,
                    string.Empty,
                    false,
                    basicProperties,
                    body,
                    cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            succeeded = true;
        }
        finally { _publishLock.Release(); }
    }
    catch (Exception ex)
    {
        failure = ex;
        throw;
    }
    finally
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
        var tags = new TagList
        {
            { "messaging.system", "rabbitmq" },
            { "messaging.operation", "publish" },
            { "messaging.destination.name", string.IsNullOrEmpty(exchangeName) ? "<unresolved>" : exchangeName },
        };
        if (failure != null)
        {
            tags.Add("error.type", ExceptionTypeMapper.Map(failure));
        }
        ServiceConnectMeter.RecordPublishDuration(elapsed, tags);

        if (succeeded)
        {
            // Counter does not need error.type — only emitted on success.
            var successTags = new TagList
            {
                { "messaging.system", "rabbitmq" },
                { "messaging.operation", "publish" },
                { "messaging.destination.name", exchangeName },
            };
            ServiceConnectMeter.AddPublishedMessage(successTags);
        }
    }
}
```

Apply the same shape to `SendAsync` (line ~202) and `SendBytesAsync` (line ~268). For `SendAsync`, the destination is the per-endpoint queue name (the loop variable), so the duration and counter emit inside the loop, once per endpoint. For `SendBytesAsync` (bytes-pre-serialised), the destination is again per-endpoint. Verify by reading the existing method bodies.

- [ ] **Step 4: Run the publish tests — expect pass**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ProducerPublishMetricsTests" -m:1`
Expected: 2 passed.

- [ ] **Step 5: Write the failing consume-metrics tests**

Create `src/ServiceConnect.UnitTests/RabbitMQ/ConsumerProcessMetricsTests.cs`:

```csharp
using ServiceConnect.Diagnostics;
using ServiceConnect.UnitTests.Diagnostics;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public class ConsumerProcessMetricsTests
{
    [Theory]
    [InlineData(ProcessOutcome.Success, "success")]
    [InlineData(ProcessOutcome.Error, "error")]
    [InlineData(ProcessOutcome.Retry, "retry")]
    public async Task OnMessageReceived_RecordsProcessDurationAndConsumedMessageWithExpectedOutcome(
        ProcessOutcome outcome, string expectedOutcomeTag)
    {
        using var collector = new MetricCollector();
        var fixture = await ConsumerHostTestFixture.CreateAsync(processBehaviour: outcome);

        await fixture.SimulateMessageReceivedAsync();

        var durations = collector.GetRecords<double>(MetricNames.ProcessDuration);
        Assert.Single(durations);
        Assert.Equal("rabbitmq", durations[0].GetTag("messaging.system"));
        Assert.Equal("process", durations[0].GetTag("messaging.operation"));
        Assert.Equal(fixture.QueueName, durations[0].GetTag("messaging.destination.name"));

        var counts = collector.GetRecords<long>(MetricNames.ConsumedMessages);
        var count = Assert.Single(counts);
        Assert.Equal(1, count.Value);
        Assert.Equal(expectedOutcomeTag, count.GetTag("messaging.outcome"));
        Assert.Equal(fixture.QueueName, count.GetTag("messaging.destination.name"));
    }
}

public enum ProcessOutcome { Success, Error, Retry }
```

The `ConsumerHostTestFixture` builder needs to construct a `RabbitMqConsumerHost` with mocked dependencies that returns the configured `processed`-vs-throws behaviour. This may require new test scaffolding — find the existing `ConsumerDispose*Tests` or `InboundMessageProcessor*Tests` for patterns to crib. If the fixture work proves substantial (more than a small builder), STOP and report DONE_WITH_CONCERNS — the consumer-host-direct-test path may need a separate refactor task.

- [ ] **Step 6: Wire process duration + counter into `RabbitMqConsumerHost.OnMessageReceivedAsync`**

Open `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs`. Around line 375-457 (the `OnMessageReceivedAsync` body), wrap the `processed = await messageProcessor.ProcessAsync(...)` call (line 383) with metric instrumentation.

The outcome detection algorithm:
- `processed = true` AND no exception → `outcome=success`
- exception thrown by `ProcessAsync` (caught at line 386) → `outcome=error`, `error.type=<mapped>`
- `processed = false` AND no exception → `outcome=retry`

Insert before the `try` block that wraps the `ProcessAsync` call (currently around line 382 — verify the exact line by reading the file before editing, since prior commits in this Group D branch may have shifted line numbers):

```csharp
var startTimestamp = Stopwatch.GetTimestamp();
Exception? processFailure = null;
try
{
    processed = await messageProcessor.ProcessAsync(publishChannel!, args, cancellationToken).ConfigureAwait(false);
}
catch (Exception ex)
{
    processFailure = ex;
    _logger.LogError(ex, "Error processing message");
}
finally
{
    var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
    var processTags = new TagList
    {
        { "messaging.system", "rabbitmq" },
        { "messaging.operation", "process" },
        { "messaging.destination.name", _queueConfiguration.QueueName },
    };
    if (processFailure != null)
    {
        processTags.Add("error.type", ExceptionTypeMapper.Map(processFailure));
    }
    ServiceConnectMeter.RecordProcessDuration(elapsed, processTags);

    string outcome;
    if (processFailure != null) outcome = "error";
    else if (processed) outcome = "success";
    else outcome = "retry";

    var consumedTags = new TagList
    {
        { "messaging.system", "rabbitmq" },
        { "messaging.operation", "process" },
        { "messaging.destination.name", _queueConfiguration.QueueName },
        { "messaging.outcome", outcome },
    };
    if (processFailure != null)
    {
        consumedTags.Add("error.type", ExceptionTypeMapper.Map(processFailure));
    }
    ServiceConnectMeter.AddConsumedMessage(consumedTags);
}
```

This replaces the existing `try { processed = ... } catch { _logger.LogError(...); }` block. The outer `try/catch/finally` for ack/nack at line 389-456 remains unchanged. The new metric `finally` is the inner one wrapping just `ProcessAsync`.

Add `using System.Diagnostics;` and `using ServiceConnect.Diagnostics;` at the top if not already present.

- [ ] **Step 7: Run the consume tests — expect pass**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ConsumerProcessMetricsTests" -m:1`
Expected: 3 passed (Theory rows for success/error/retry).

- [ ] **Step 8: Run full unit-test sweep**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-restore -m:1 2>&1 | tail -3`
Expected: all pass (modulo the `ProducerPublishTimeoutTimingTests` flake).

- [ ] **Step 9: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/ProducerPublishMetricsTests.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/ConsumerProcessMetricsTests.cs \
        src/ServiceConnect.UnitTests/Diagnostics/MetricCollector.cs

# Stage any new test-scaffolding files added during the task:
git add src/ServiceConnect.UnitTests/RabbitMQ/ProducerTestFactory.cs 2>/dev/null
git add src/ServiceConnect.UnitTests/RabbitMQ/ConsumerHostTestFixture.cs 2>/dev/null

git commit -m "$(cat <<'EOF'
feat(transport): emit publish/consume duration + count metrics

Wires the four OTel-standard messaging metrics into the producer
publish path and consumer host process path:

- messaging.publish.duration (histogram, seconds) — wraps
  Producer.PublishAsync / SendAsync / SendBytesAsync.
- messaging.client.published.messages (counter) — increments on
  publish success.
- messaging.process.duration (histogram, seconds) — wraps
  RabbitMqConsumerHost's ProcessAsync call.
- messaging.client.consumed.messages (counter) — emitted at the
  end of consumer-side processing with messaging.outcome tag
  (success | error | retry).

Tags follow OTel semantic conventions: messaging.system=rabbitmq,
messaging.operation=publish/process, messaging.destination.name
carries the exchange (publish) or queue (consume) name. error.type
is added on failure paths via the allow-list mapper.

Note: messaging.outcome is a 3-value tag at this emit site
(success/error/retry). The "drop" outcome (retry-publish-failure
swallow at InboundMessageProcessor.cs:150-158) emits separately on
messaging.serviceconnect.retry.drops in the next commit.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: ServiceConnect-specific operability metrics

Wires the five ServiceConnect-extension instruments at their respective emit sites.

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs` — emit `RetryAttempts` at the increment site
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs` — emit `RetryDrops` (line 150-158) and `AuditDrops` (line 243-246)
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs` — emit `PublishConfirmTimeouts` at TimeoutException catch (line 469)
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` — emit `InFlightMessages` UpDownCounter at `_messagesBeingProcessed` increment (line 284) and decrement (line 453)
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerMetricsTests.cs`
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorMetricsTests.cs`
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/ProducerConfirmTimeoutMetricsTests.cs`

- [ ] **Step 1: Wire `RetryAttempts` into `MessageRetryHandler`**

Open `src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs`. Find the retry-counter increment at line 63-64:

```csharp
retryCount++;
HeaderHelpers.SetHeader(headers, HeaderKeys.RetryCount, retryCount);
```

Add `using System.Diagnostics;` and `using ServiceConnect.Diagnostics;` at the top. Add metric emission immediately after the `SetHeader` call:

```csharp
retryCount++;
HeaderHelpers.SetHeader(headers, HeaderKeys.RetryCount, retryCount);

ServiceConnectMeter.AddRetryAttempt(new TagList
{
    { "messaging.system", "rabbitmq" },
    { "messaging.destination.name", _queueConfiguration.QueueName },
});
```

Verify `_queueConfiguration` exists on the class — if not, locate the queue-name source the rest of the file uses.

- [ ] **Step 2: Test `RetryAttempts`**

Create `src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerMetricsTests.cs`:

```csharp
using ServiceConnect.Diagnostics;
using ServiceConnect.UnitTests.Diagnostics;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public class MessageRetryHandlerMetricsTests
{
    [Fact]
    public async Task HandleAsync_OnRetry_IncrementsRetryAttempts()
    {
        using var collector = new MetricCollector();
        var fixture = await MessageRetryHandlerTestFixture.CreateAsync();

        await fixture.SimulateRetryAsync();

        var attempts = collector.GetRecords<long>(MetricNames.RetryAttempts);
        var record = Assert.Single(attempts);
        Assert.Equal(1, record.Value);
        Assert.Equal("rabbitmq", record.GetTag("messaging.system"));
        Assert.Equal(fixture.QueueName, record.GetTag("messaging.destination.name"));
    }
}
```

Build the fixture by referencing the existing `MessageRetryHandlerCopyPropsTests.cs` (mentioned in a comment in MessageRetryHandler) for the mocking pattern.

- [ ] **Step 3: Wire `RetryDrops` into `InboundMessageProcessor`**

Open `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs`. Find the retry-publish-failure catch at line 150-158:

```csharp
catch (Exception retryEx)
{
    _logger.LogError(retryEx,
        "Retry publish failed for MessageId {MessageId} (DeliveryTag {DeliveryTag}) on queue {Queue}; dropping to prevent unbounded redelivery loop.",
        args.BasicProperties.MessageId, args.DeliveryTag, _queueConfiguration.QueueName);
    // Intentionally swallow ...
}
```

Add metric emission inside the catch (after the existing log call, before the close-brace):

```csharp
ServiceConnectMeter.AddRetryDrop(new TagList
{
    { "messaging.system", "rabbitmq" },
    { "messaging.destination.name", _queueConfiguration.QueueName },
    { "error.type", ExceptionTypeMapper.Map(retryEx) },
});
```

Add `using System.Diagnostics;` and `using ServiceConnect.Diagnostics;` at the top if not present.

- [ ] **Step 4: Wire `AuditDrops` into `InboundMessageProcessor`**

In the same file, find the audit-failure catch at line 243-246:

```csharp
catch (Exception ex)
{
    _logger.LogError(ex, "Failed to publish audit message for delivery {DeliveryTag}; continuing to ack the original message", args.DeliveryTag);
}
```

Add metric emission:

```csharp
catch (Exception ex)
{
    _logger.LogError(ex, "Failed to publish audit message for delivery {DeliveryTag}; continuing to ack the original message", args.DeliveryTag);
    ServiceConnectMeter.AddAuditDrop(new TagList
    {
        { "messaging.system", "rabbitmq" },
        { "error.type", ExceptionTypeMapper.Map(ex) },
    });
}
```

The audit drop has no `messaging.destination.name` tag because the audit queue is global per the spec.

- [ ] **Step 5: Test `RetryDrops` + `AuditDrops`**

Create `src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorMetricsTests.cs`:

```csharp
using ServiceConnect.Diagnostics;
using ServiceConnect.UnitTests.Diagnostics;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public class InboundMessageProcessorMetricsTests
{
    [Fact]
    public async Task ProcessAsync_OnRetryPublishFailure_IncrementsRetryDrops()
    {
        using var collector = new MetricCollector();
        var fixture = await InboundMessageProcessorTestFixture.CreateAsync(
            simulateRetryPublishFailure: true);

        await fixture.SimulateMessageProcessingAsync();

        var drops = collector.GetRecords<long>(MetricNames.RetryDrops);
        var record = Assert.Single(drops);
        Assert.Equal(1, record.Value);
        Assert.Equal("rabbitmq", record.GetTag("messaging.system"));
        Assert.Equal(fixture.QueueName, record.GetTag("messaging.destination.name"));
        Assert.NotNull(record.GetTag("error.type"));
    }

    [Fact]
    public async Task ProcessAsync_OnAuditPublishFailure_IncrementsAuditDrops()
    {
        using var collector = new MetricCollector();
        var fixture = await InboundMessageProcessorTestFixture.CreateAsync(
            simulateAuditPublishFailure: true);

        await fixture.SimulateMessageProcessingAsync();

        var drops = collector.GetRecords<long>(MetricNames.AuditDrops);
        var record = Assert.Single(drops);
        Assert.Equal(1, record.Value);
        Assert.Equal("rabbitmq", record.GetTag("messaging.system"));
        Assert.NotNull(record.GetTag("error.type"));
    }
}
```

Build the `InboundMessageProcessorTestFixture` by referencing the existing `InboundMessageProcessor*Tests.cs` files for mock setups.

- [ ] **Step 6: Wire `PublishConfirmTimeouts` into `Producer.PublishWithTimeoutAsync`**

Open `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs`. Find the TimeoutException-throw at line 469-472 (inside the `catch (OperationCanceledException) when (linked.IsCancellationRequested && !cancellationToken.IsCancellationRequested)` branch).

Just before the `throw new TimeoutException(...)` line, add:

```csharp
ServiceConnectMeter.AddPublishConfirmTimeout(new TagList
{
    { "messaging.system", "rabbitmq" },
    { "messaging.destination.name", string.IsNullOrEmpty(exchange) ? "<empty>" : exchange },
});
```

(`exchange` is the local parameter on `PublishWithTimeoutAsync`. The `string.IsNullOrEmpty` guard handles the SendAsync case where the exchange is empty and the routing key carries the destination — for that case, document `<empty>` as the conventional value. Or, if the SendAsync emit site uses `routingKey`, prefer that; verify by reading the call sites in `SendAsync` / `SendBytesAsync`.)

- [ ] **Step 7: Test `PublishConfirmTimeouts`**

Create `src/ServiceConnect.UnitTests/RabbitMQ/ProducerConfirmTimeoutMetricsTests.cs`. The test uses the existing `ProducerPublishTimeoutTimingTests` mocking pattern (which simulates a hung `BasicPublishAsync`) but with a much shorter timeout to keep the test fast:

```csharp
using ServiceConnect.Diagnostics;
using ServiceConnect.UnitTests.Diagnostics;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public class ProducerConfirmTimeoutMetricsTests
{
    [Fact]
    public async Task PublishAsync_WhenBrokerAckTimesOut_IncrementsPublishConfirmTimeouts()
    {
        using var collector = new MetricCollector();
        var producer = await ProducerTestFactory.CreatePublishableWithHangingChannelAsync(
            publishTimeout: TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<TimeoutException>(
            () => producer.PublishAsync(typeof(SampleMessage), new ReadOnlyMemory<byte>([1])));

        var timeouts = collector.GetRecords<long>(MetricNames.PublishConfirmTimeouts);
        var record = Assert.Single(timeouts);
        Assert.Equal(1, record.Value);
        Assert.Equal("rabbitmq", record.GetTag("messaging.system"));
    }

    public sealed class SampleMessage : ServiceConnect.Interfaces.Message
    {
        public SampleMessage() : base(Guid.NewGuid()) { }
    }
}
```

- [ ] **Step 8: Wire `InFlightMessages` UpDownCounter**

Open `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs`. Find:

- Line 284: `Interlocked.Increment(ref _messagesBeingProcessed);` — add a `+1` UpDownCounter emit.
- Line 453: `Interlocked.Decrement(ref _messagesBeingProcessed);` — add a `-1` UpDownCounter emit.

Both inside the existing field-update logic. Concretely:

```csharp
// Around line 284:
Interlocked.Increment(ref _messagesBeingProcessed);
ServiceConnectMeter.AddInFlight(1, new TagList
{
    { "messaging.system", "rabbitmq" },
    { "messaging.destination.name", _queueConfiguration.QueueName },
});

// Around line 453:
Interlocked.Decrement(ref _messagesBeingProcessed);
ServiceConnectMeter.AddInFlight(-1, new TagList
{
    { "messaging.system", "rabbitmq" },
    { "messaging.destination.name", _queueConfiguration.QueueName },
});
```

The increment/decrement pair is balanced — every callback that increments also decrements in the `finally` (the existing code at line 451-454 confirms this).

- [ ] **Step 9: Add an in-flight test to `ConsumerProcessMetricsTests.cs`**

Append a third test to the existing `ConsumerProcessMetricsTests.cs` from Task 2:

```csharp
[Fact]
public async Task OnMessageReceived_TogglesInFlightUpDownCounter()
{
    using var collector = new MetricCollector();
    var fixture = await ConsumerHostTestFixture.CreateAsync(processBehaviour: ProcessOutcome.Success);

    await fixture.SimulateMessageReceivedAsync();

    var deltas = collector.GetRecords<long>(MetricNames.InFlightMessages);
    Assert.Equal(2, deltas.Count);
    Assert.Equal(1, deltas[0].Value);   // increment at dispatch
    Assert.Equal(-1, deltas[1].Value);  // decrement at completion
    // Net delta is zero — invariant.
    Assert.Equal(0, deltas.Sum(r => r.Value));
}
```

- [ ] **Step 10: Run all Task 3 tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageRetryHandlerMetricsTests|FullyQualifiedName~InboundMessageProcessorMetricsTests|FullyQualifiedName~ProducerConfirmTimeoutMetricsTests|FullyQualifiedName~ConsumerProcessMetricsTests" -m:1`

Expected: all pass.

- [ ] **Step 11: Run full unit-test sweep**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-restore -m:1 2>&1 | tail -3`
Expected: all pass.

- [ ] **Step 12: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs \
        src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerMetricsTests.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorMetricsTests.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/ProducerConfirmTimeoutMetricsTests.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/ConsumerProcessMetricsTests.cs

git commit -m "$(cat <<'EOF'
feat(transport): emit ServiceConnect-specific operability metrics

Wires five ServiceConnect-extension instruments:

- messaging.serviceconnect.retry.attempts: increments at the
  MessageRetryHandler.cs retry-counter increment site.
- messaging.serviceconnect.retry.drops: increments at the
  InboundMessageProcessor retry-publish-failure catch
  (the path that breaks unbounded redelivery loops).
- messaging.serviceconnect.publish.confirm_timeouts: increments at
  the Producer.PublishWithTimeoutAsync TimeoutException remap
  (broker ack didn't arrive within the configured timeout).
- messaging.serviceconnect.audit.drops: increments at the
  audit-publish-failure catch (audit queue is global, so no
  destination tag).
- messaging.serviceconnect.process.messages.inflight: UpDownCounter
  surfacing _messagesBeingProcessed; +1 at dispatch entry, -1 in
  the finally at the end of OnMessageReceivedAsync.

Each metric carries the appropriate tag subset per the spec
(messaging.system + messaging.destination.name where applicable;
error.type on failure paths via the allow-list mapper).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Connection-lifecycle Info logs + ack/nack `MessageId` enrichment

Six new entries on the existing `RabbitMqClientLog` source-gen partial class. EventIds 2-7.

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/RabbitMqClientLog.cs` — add 6 new `[LoggerMessage]` methods
- Modify: `src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs` — emit `ConnectionOpened`; subscribe to recovery/shutdown events
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs` — emit `ProducerConnectionOpened`; subscribe to recovery/shutdown events
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` — replace ack/nack failure `LogWarning` calls with `AckFailed`/`NackFailed`
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/ConnectionLifecycleLogsTests.cs`
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/AckNackFailureLogsTests.cs`

- [ ] **Step 1: Extend `RabbitMqClientLog` with 6 new methods**

Open `src/ServiceConnect.Client.RabbitMQ/RabbitMqClientLog.cs`. Add to the existing `internal static partial class RabbitMqClientLog`:

```csharp
public const int ConnectionOpenedEventId = 2;
public const int ProducerConnectionOpenedEventId = 3;
public const int ConnectionRecoveredEventId = 4;
public const int ConnectionLostEventId = 5;
public const int AckFailedEventId = 6;
public const int NackFailedEventId = 7;

[LoggerMessage(
    EventId = ConnectionOpenedEventId,
    EventName = "ConnectionOpened",
    Level = LogLevel.Information,
    Message = "ServiceConnect connection opened to {Host}:{Port} (vhost='{VirtualHost}', name='{ConnectionName}').")]
public static partial void ConnectionOpened(ILogger logger, string host, int port, string virtualHost, string connectionName);

[LoggerMessage(
    EventId = ProducerConnectionOpenedEventId,
    EventName = "ProducerConnectionOpened",
    Level = LogLevel.Information,
    Message = "ServiceConnect producer connection opened to {Host}:{Port} (vhost='{VirtualHost}', name='{ConnectionName}').")]
public static partial void ProducerConnectionOpened(ILogger logger, string host, int port, string virtualHost, string connectionName);

[LoggerMessage(
    EventId = ConnectionRecoveredEventId,
    EventName = "ConnectionRecovered",
    Level = LogLevel.Information,
    Message = "ServiceConnect connection recovered to {Host}:{Port} (name='{ConnectionName}').")]
public static partial void ConnectionRecovered(ILogger logger, string host, int port, string connectionName);

[LoggerMessage(
    EventId = ConnectionLostEventId,
    EventName = "ConnectionLost",
    Level = LogLevel.Information,
    Message = "ServiceConnect connection lost to {Host}:{Port} (name='{ConnectionName}', initiator={Initiator}, reason={Reason}).")]
public static partial void ConnectionLost(ILogger logger, string host, int port, string connectionName, string initiator, string reason);

[LoggerMessage(
    EventId = AckFailedEventId,
    EventName = "AckFailed",
    Level = LogLevel.Warning,
    Message = "Failed to ack message {MessageId} (DeliveryTag {DeliveryTag}) on queue {Queue}.")]
public static partial void AckFailed(ILogger logger, Exception exception, string messageId, ulong deliveryTag, string queue);

[LoggerMessage(
    EventId = NackFailedEventId,
    EventName = "NackFailed",
    Level = LogLevel.Warning,
    Message = "Failed to nack message {MessageId} (DeliveryTag {DeliveryTag}) on queue {Queue}.")]
public static partial void NackFailed(ILogger logger, Exception exception, string messageId, ulong deliveryTag, string queue);
```

- [ ] **Step 2: Wire connection-lifecycle hooks into `Connection.cs`**

Open `src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs`. The class is `public sealed class Connection(...) : IAsyncDisposable, IServiceConnectConnection` — primary-constructor style.

Modify `CreateConnectionCoreAsync` (line 48) to emit the `ConnectionOpened` log AND attach event handlers immediately after `CreateConnectionAsync` returns. Concretely:

Replace:

```csharp
private async Task CreateConnectionCoreAsync(CancellationToken cancellationToken)
{
    logger.LogDebug("Creating connection to queue {QueueName}", queueName);
    var connectionFactory = BuildConnectionFactory();
    var connector = CreateConnectionForTests ?? ((f, h, n, ct) => f.CreateConnectionAsync(h, n, ct));
    _connection = await connector(connectionFactory, _hosts, queueName, cancellationToken).ConfigureAwait(false);
}
```

With:

```csharp
private async Task CreateConnectionCoreAsync(CancellationToken cancellationToken)
{
    logger.LogDebug("Creating connection to queue {QueueName}", queueName);
    var connectionFactory = BuildConnectionFactory();
    var connector = CreateConnectionForTests ?? ((f, h, n, ct) => f.CreateConnectionAsync(h, n, ct));
    _connection = await connector(connectionFactory, _hosts, queueName, cancellationToken).ConfigureAwait(false);

    AttachLifecycleHandlers(_connection);
    RabbitMqClientLog.ConnectionOpened(
        logger,
        _connection.Endpoint.HostName,
        _connection.Endpoint.Port,
        _connection.Endpoint.VirtualHost ?? "/",
        _connection.ClientProvidedName ?? string.Empty);
}

private void AttachLifecycleHandlers(IConnection connection)
{
    connection.RecoverySucceededAsync += OnRecoverySucceededAsync;
    connection.ConnectionShutdownAsync += OnConnectionShutdownAsync;
}

private void DetachLifecycleHandlers(IConnection connection)
{
    connection.RecoverySucceededAsync -= OnRecoverySucceededAsync;
    connection.ConnectionShutdownAsync -= OnConnectionShutdownAsync;
}

private Task OnRecoverySucceededAsync(object? sender, AsyncEventArgs e)
{
    if (sender is IConnection connection)
    {
        RabbitMqClientLog.ConnectionRecovered(
            logger,
            connection.Endpoint.HostName,
            connection.Endpoint.Port,
            connection.ClientProvidedName ?? string.Empty);
    }
    return Task.CompletedTask;
}

private Task OnConnectionShutdownAsync(object? sender, ShutdownEventArgs e)
{
    if (sender is IConnection connection)
    {
        RabbitMqClientLog.ConnectionLost(
            logger,
            connection.Endpoint.HostName,
            connection.Endpoint.Port,
            connection.ClientProvidedName ?? string.Empty,
            e.Initiator.ToString(),
            e.ReplyText ?? "<no reason>");
    }
    return Task.CompletedTask;
}
```

The async event names (`RecoverySucceededAsync`, `ConnectionShutdownAsync`) and signatures match RabbitMQ.Client v7. If event names differ at implementation time (verify by reading the IConnection interface in the v7 source), adjust accordingly.

In the existing `DisposeAsync` method (around line 104), call `DetachLifecycleHandlers(conn)` BEFORE the `conn.CloseAsync()` call:

```csharp
if (conn != null)
{
    DetachLifecycleHandlers(conn);
    try
    {
        if (conn.IsOpen) { await conn.CloseAsync().ConfigureAwait(false); }
        conn.Dispose();
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Error closing connection during async dispose");
    }
}
```

- [ ] **Step 3: Wire connection-lifecycle hooks into `ProducerConnection.cs`**

Open `src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs`. Apply the same pattern as Step 2:
- After successful `CreateConnectionAsync` (around line 243), call `AttachLifecycleHandlers(connection)` and `RabbitMqClientLog.ProducerConnectionOpened(...)`.
- Detach in the disposal path.

The exact insertion point and detach site need to be located by reading the current file (the disposal logic spans `DisposeConnectionAsync` and the `DisposeConnectionInstanceAsync` helper). Mirror the Connection.cs Steps-2 structure.

- [ ] **Step 4: Replace ack/nack failure logs in `RabbitMqConsumerHost.cs`**

Open `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs`. Find the four `_logger.LogWarning(...)`-style calls in the ack/nack catch blocks at lines 420-447.

The four call sites:
- Line 431 `_logger.LogWarning(ex, "Channel already closed while acking/nacking message {DeliveryTag}", args.DeliveryTag);`
- Line 442 `_logger.LogWarning(ex, "Channel disposed while acking/nacking message {DeliveryTag}", args.DeliveryTag);`
- Line 447 `_logger.LogWarning(ex, "Error acking/nacking the message");`

The Debug-level shutdown branches (lines 427, 438) stay as-is — they're not failures, they're expected shutdown noise.

For each remaining `LogWarning`, replace with the source-gen call. Determine ack vs nack by looking at the code path:

The `BasicAckAsync` call is at line 412 (inside `if (processed)`), `BasicNackAsync` at line 416 (inside `else`). Both are inside the same outer `try`, so the catch can't distinguish — but we know `processed`'s value at the catch point (the catch is in the `finally` so `processed` is determinate).

Simplest path: check `processed` in each catch block:

```csharp
catch (global::RabbitMQ.Client.Exceptions.AlreadyClosedException ex)
{
    if (_shutdownStarted)
    {
        _logger.LogDebug(ex, "Channel already closed while acking/nacking message {DeliveryTag} during shutdown", args.DeliveryTag);
    }
    else
    {
        var messageId = args.BasicProperties.MessageId ?? args.DeliveryTag.ToString();
        if (processed)
        {
            RabbitMqClientLog.AckFailed(_logger, ex, messageId, args.DeliveryTag, _queueConfiguration.QueueName);
        }
        else
        {
            RabbitMqClientLog.NackFailed(_logger, ex, messageId, args.DeliveryTag, _queueConfiguration.QueueName);
        }
    }
}
```

Apply the same pattern to the `ObjectDisposedException` catch (line 434) and the catch-all at line 445.

- [ ] **Step 5: Write the lifecycle-logs test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/ConnectionLifecycleLogsTests.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Client.RabbitMQ.Connection;
using ServiceConnect.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public class ConnectionLifecycleLogsTests
{
    [Fact]
    public async Task CreateConnectionCoreAsync_OnSuccess_LogsConnectionOpened()
    {
        var fakeLogger = new FakeLogger();
        var transport = new TransportConfiguration { Host = "localhost", SslEnabled = false };

        var mockConnection = new Mock<IConnection>();
        mockConnection.SetupGet(c => c.Endpoint).Returns(new AmqpTcpEndpoint("rabbit.example", 5672));
        mockConnection.SetupGet(c => c.ClientProvidedName).Returns("test-connection");

        var connection = new Connection(transport, "queue-name", fakeLogger);
        connection.CreateConnectionForTests = (_, _, _, _) => Task.FromResult(mockConnection.Object);

        // Trigger the connection through any public method that calls ConnectAsync.
        // CreateChannelAsync is the public entry point; it requires the mocked connection
        // to also handle CreateChannelAsync, so set that up too:
        mockConnection.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IChannel>());

        _ = await connection.CreateChannelAsync(default);

        var records = fakeLogger.Collector.GetSnapshot();
        var opened = Assert.Single(records, r => r.Id.Id == RabbitMqClientLog.ConnectionOpenedEventId);
        Assert.Equal(LogLevel.Information, opened.Level);
        Assert.Contains("rabbit.example", opened.Message);
    }
}
```

Add tests for `ConnectionRecovered` and `ConnectionLost` by raising the events on the mock connection (Moq supports `Raise`).

- [ ] **Step 6: Write the ack/nack-failure test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/AckNackFailureLogsTests.cs` — set up a consumer host scenario that triggers the catch-all log path with a known `MessageId`, then assert `AckFailed`/`NackFailed` event IDs are emitted with the message-id substring in the formatted message.

Use the existing consumer-host test scaffolding (from Task 2). If the fixture doesn't expose hooks to inject a failing channel post-process, extend it minimally.

- [ ] **Step 7: Run all Task 4 tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ConnectionLifecycleLogsTests|FullyQualifiedName~AckNackFailureLogsTests" -m:1`
Expected: all pass.

- [ ] **Step 8: Run full unit-test sweep**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-restore -m:1 2>&1 | tail -3`
Expected: all pass.

- [ ] **Step 9: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/RabbitMqClientLog.cs \
        src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs \
        src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/ConnectionLifecycleLogsTests.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/AckNackFailureLogsTests.cs

git commit -m "$(cat <<'EOF'
feat(transport): emit connection-lifecycle Info logs and enrich ack/nack failure logs

Six new entries on RabbitMqClientLog (EventIds 2-7):

- ConnectionOpened (2): emitted from Connection.cs after a fresh
  consumer connection establishes.
- ProducerConnectionOpened (3): same for producers.
- ConnectionRecovered (4): emitted from RabbitMQ.Client's
  RecoverySucceededAsync event handler.
- ConnectionLost (5): emitted from ConnectionShutdownAsync; carries
  ShutdownEventArgs.{Initiator,ReplyText}.
- AckFailed (6) / NackFailed (7): replace the prior LogWarning at
  RabbitMqConsumerHost.cs's ack/nack catch blocks; carry MessageId
  (BasicProperties.MessageId, falling back to DeliveryTag) so log
  readers can correlate to a specific message.

Lifecycle handlers attach in CreateConnectionCoreAsync (Connection)
and the equivalent producer-connection initialiser, and detach in
the corresponding DisposeAsync paths to avoid leaking the handler
until GC.

Connection-lost stays at Information level (broker-initiated
shutdowns happen for normal reasons — rolling restarts, cluster
maintenance — and don't warrant a Warning page).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Documentation

**Files:**
- Modify: `website/src/content/docs/learn/operations/observability.mdx` — add a `## Metrics` section after the existing `## Tracing (OpenTelemetry)` section
- Modify: `website/src/content/docs/releases.mdx` — append a v8 highlight subsection

- [ ] **Step 1: Add the metrics section to `observability.mdx`**

Open `website/src/content/docs/learn/operations/observability.mdx`. After `## Tracing (OpenTelemetry)` and its subsections (ending around the `### Propagation` section), insert a new `## Metrics` section:

```markdown
## Metrics

ServiceConnect emits operator-grade metrics via `System.Diagnostics.Metrics`. The Meter is named `ServiceConnect.Bus` and is **always-on** — instruments are zero-cost when no listener subscribes (BCL pattern, same as `HttpClient`).

### Wiring

For OpenTelemetry users:

```csharp
services.AddOpenTelemetry().WithMetrics(b => b.AddServiceConnectInstrumentation());
```

Without OpenTelemetry, attach a `MeterListener` directly:

```csharp
var listener = new MeterListener();
listener.InstrumentPublished = (instrument, l) =>
{
    if (instrument.Meter.Name == "ServiceConnect.Bus") l.EnableMeasurementEvents(instrument);
};
listener.Start();
```

### Catalogue

Tags follow the [OpenTelemetry messaging-metrics conventions](https://opentelemetry.io/docs/specs/semconv/messaging/messaging-metrics/). All metrics carry `messaging.system="rabbitmq"` and (where applicable) `messaging.destination.name`. ServiceConnect-specific metrics live under the `messaging.serviceconnect.*` sub-namespace.

| Metric | Type | Unit | What it counts |
|---|---|---|---|
| `messaging.publish.duration` | Histogram | s | Wall time of a publish, from start to broker ack |
| `messaging.process.duration` | Histogram | s | Wall time of consumer-side handler dispatch |
| `messaging.client.published.messages` | Counter | {message} | Messages successfully published |
| `messaging.client.consumed.messages` | Counter | {message} | Messages consumed; tagged `messaging.outcome=success\|error\|retry` |
| `messaging.serviceconnect.retry.attempts` | Counter | {attempt} | Header-counter retry increments |
| `messaging.serviceconnect.retry.drops` | Counter | {drop} | Messages dropped because retry publishing failed |
| `messaging.serviceconnect.publish.confirm_timeouts` | Counter | {timeout} | Publishes that exceeded the configured publish timeout |
| `messaging.serviceconnect.audit.drops` | Counter | {drop} | Audit messages that failed to publish |
| `messaging.serviceconnect.process.messages.inflight` | UpDownCounter | {message} | Currently-dispatched, not-yet-acked messages |

The `error.type` tag is added on failure paths via an allow-list mapper (`OperationCanceledException → cancelled`, `TimeoutException → timeout`, fallback to the exception type's short name). Exception messages are never used as tags — they're unbounded cardinality.

### Cardinality

Typical deployments — 5-20 queues, 5-20 exchanges, ~5 outcome / error categories — yield ~400 active series per metric upper-bound. Operators with very high queue counts (1000+) should consider this when sizing their TSDB.

The `messaging.outcome` tag is a closed three-value set (`success | error | retry`) at the consumer-host emit site. The "drop" outcome (retry-publish-failure path) is captured separately on `messaging.serviceconnect.retry.drops` rather than as a fourth `outcome` value, because the drop decision is made in a deeper layer than the host-level counter sees.

Per-message tags (`messaging.message.id`, routing keys, conversation IDs) are deliberately NOT included on metrics — they belong on traces, where one span per message matches the data model.

### Tracing vs metrics

Tracing is **opt-in** via [`AddTelemetry()`](#wiring) because span creation has a per-message allocation cost that's only worth paying when you'll actually export the spans. Metrics are **always-on** because instrument emission is free without a listener — same pattern as the .NET BCL libraries (`HttpClient`, `EFCore`, `Sockets`).

### Connection-lifecycle logs

Beyond metrics, ServiceConnect emits structured Info logs for connection state transitions. Filter on the `ServiceConnect.Client.RabbitMQ` category:

| Event ID | Event name | Level | Emitted when |
|---|---|---|---|
| 2 | `ConnectionOpened` | Information | A consumer connection establishes |
| 3 | `ProducerConnectionOpened` | Information | A producer connection establishes |
| 4 | `ConnectionRecovered` | Information | RabbitMQ.Client's auto-recovery succeeds |
| 5 | `ConnectionLost` | Information | The broker initiates `ConnectionShutdown` (e.g. broker restart, cluster failover) — NOT escalated to Warning because broker-initiated shutdowns happen for normal operational reasons |
| 6 | `AckFailed` | Warning | An ack call fails; carries `MessageId` for correlation |
| 7 | `NackFailed` | Warning | A nack call fails; carries `MessageId` for correlation |
```

- [ ] **Step 2: Add the v8 highlight to `releases.mdx`**

Open `website/src/content/docs/releases.mdx`. Find the v8 highlights section. Append a new subsection at the end of v8 highlights, before any v7 / older sections:

```markdown
### Feature — operator metrics + connection-lifecycle logs

ServiceConnect now emits operator-grade metrics via `System.Diagnostics.Metrics`. Six instruments under the OTel `messaging.*` namespace (publish/process duration histograms, published/consumed message counters with outcome dimensions) plus four ServiceConnect-specific extensions (`messaging.serviceconnect.retry.attempts / retry.drops / publish.confirm_timeouts / audit.drops`) and an in-flight gauge.

OpenTelemetry users wire them with `MeterProvider.AddServiceConnectInstrumentation()` from the `ServiceConnect.Telemetry` package. Without OTel, attach a `MeterListener` to the `ServiceConnect.Bus` meter.

Always-on: instruments are zero-cost when no listener subscribes — operator opt-in is wiring the listener, not changing config. See [Observability — Metrics](/ServiceConnect-CSharp/learn/operations/observability/#metrics) for the full catalogue and tag schema.

Bundled: connection-lifecycle Info logs (`ConnectionOpened` / `ProducerConnectionOpened` / `ConnectionRecovered` / `ConnectionLost`) and ack/nack failure logs now carry the `MessageId` for correlation.

Additive — no API breaks.
```

- [ ] **Step 3: Verify Astro build**

Run:
```bash
cd website && npx astro build && cd ..
```
Expected: build succeeds (66 pages, no errors).

- [ ] **Step 4: Commit**

```bash
git add website/src/content/docs/learn/operations/observability.mdx \
        website/src/content/docs/releases.mdx

git commit -m "$(cat <<'EOF'
docs(website): document ServiceConnect metrics + lifecycle logs

Adds a Metrics section to learn/operations/observability.mdx
covering: wiring (OTel + plain MeterListener), the full instrument
catalogue, tag schema, cardinality budget, the metrics-vs-tracing
opt-in framing, and the new connection-lifecycle log table.

Adds a v8 highlight subsection to releases.mdx for the
metrics + lifecycle-logs feature. Additive — no breaking changes.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Mark Group B done in the roadmap

After Tasks 1-5 land, update the roadmap.

**Files:**
- Modify: `architecture-fix-plan.md` — change Group B status

- [ ] **Step 1: Update the roadmap status line**

Open `architecture-fix-plan.md`. Find:

```
## Group B — Metrics rollout · *pending*
```

Change to:

```
## Group B — Metrics rollout · *done*
```

- [ ] **Step 2: Commit**

```bash
git add architecture-fix-plan.md

git commit -m "$(cat <<'EOF'
docs(architecture): mark Group B done in the fix plan

Group B lands in five feature commits:

- feat(diagnostics): introduce ServiceConnectMeter
- feat(transport): emit publish/consume duration + count metrics
- feat(transport): emit ServiceConnect-specific operability metrics
- feat(transport): emit connection-lifecycle Info logs and enrich
  ack/nack failure logs
- docs(website): document ServiceConnect metrics + lifecycle logs

Six metrics under the OTel messaging.* namespace + five
ServiceConnect-specific extensions + four lifecycle logs + two
ack/nack enrichments. All additive; no API breaks.

Group F (perf reductions) is the remaining additive group;
Group E (resilience features, idempotency strategy) closes the
roadmap.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Final verification

- [ ] **Step 1: Full solution build**

Run: `dotnet build src/ServiceConnect.slnx -m:1`
Expected: 0 errors, 0 warnings.

- [ ] **Step 2: Full unit-test sweep**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-build --no-restore -m:1`
Expected: all tests pass (the `ProducerPublishTimeoutTimingTests` flake may hit; re-run once if it does). Net new tests: ~17.

- [ ] **Step 3: SerializationCompatTests sweep (sanity)**

Run: `dotnet test src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj --no-build --no-restore`
Expected: 48/48 pass. Group B doesn't touch the serializer.

- [ ] **Step 4: EndToEndTests build**

Run: `dotnet build src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj -m:1`
Expected: 0 errors, 0 warnings.

- [ ] **Step 5: Verify every example builds**

```bash
for slnfile in examples/*/[A-Za-z]*.sln; do
    echo "=== $slnfile ==="
    dotnet build "$slnfile" -m:1 2>&1 | tail -1
done
```

Expected: every example "Build succeeded".

- [ ] **Step 6: Astro site build**

Run: `cd website && npx astro build && cd ..`
Expected: 66 pages built, no errors.

- [ ] **Step 7: Confirm commit chain**

Run: `git log <commit-before-task-1>..HEAD --oneline`
Expected: six commits in order:

```
docs(architecture): mark Group B done in the fix plan
docs(website): document ServiceConnect metrics + lifecycle logs
feat(transport): emit connection-lifecycle Info logs and enrich ack/nack failure logs
feat(transport): emit ServiceConnect-specific operability metrics
feat(transport): emit publish/consume duration + count metrics
feat(diagnostics): introduce ServiceConnectMeter
```

---

## Risks and rollback

- **Test scaffolding cost.** Tasks 2-4 each call for new test fixtures (`ProducerTestFactory`, `ConsumerHostTestFixture`, `MessageRetryHandlerTestFixture`, `InboundMessageProcessorTestFixture`). The plan instructs the implementer to crib from existing test files and only build the minimum necessary. If a fixture proves substantial (refactor-shaped), STOP and report DONE_WITH_CONCERNS — the foundation work may need a separate scaffolding task ahead of the metrics emit-site work.
- **`OpenTelemetry.Api` package availability.** Task 1 Step 1 verifies. If unavailable and adding it causes issues, the helper falls back to documentation-only.
- **`messaging.outcome` 3-vs-4 values.** Spec said four; reality at the host level is three. Plan and docs reflect three; the "drop" outcome lives on the dedicated counter. If a future change exposes the drop path to the host (e.g. by changing `InboundMessageProcessor` to return a richer status), revisit the value set.
- **`ConnectionShutdown` event handler lifetime.** If the unsubscribe in `DisposeAsync` is missed, the handler keeps the `Connection` alive until GC. Plan Step 2/3 of Task 4 explicitly call out the detach. Verify by spot-reading the diff at review time.
- **Cardinality blow-up.** Mitigated by the closed `messaging.outcome` set, the `error.type` allow-list, and deliberate exclusion of per-message tags. Documented; flag any code path that adds a new tag during implementation.
- **Hot-path overhead.** `MeterListener` zero-cost when no listener subscribed is a BCL guarantee. Verify with a publish-path benchmark on master vs HEAD if any reviewer questions it.
- **Rollback** — each of the six commits is independently revertible. Reverting commit 1 forces 2-4 to revert with it (they consume the foundation); 5 and 6 are pure-docs and revert standalone.

---

## Decisions banked from the brainstorm

- **OTel-extension naming (option A).** OTel-standard names where OTel defines them; `messaging.serviceconnect.*` for ServiceConnect-specific events.
- **Logging items in scope (option a).** Bundled with metrics — same operability theme.
- **Always-on metrics, not opt-in via middleware.** Modern BCL pattern.
- **Meter in `ServiceConnect` core.** Forced by the reference graph (`Telemetry → ServiceConnect`).
- **`messaging.outcome` is a closed set** (3 values at the host level: success/error/retry).
- **`error.type` uses an allow-list mapper** with type-short-name fallback.
- **Connection-lifecycle logs at Information level**, not Warning, even for "lost".
- **No `AddMetrics()` rename** of `AddTelemetry()`. Documentation explains the distinction.
