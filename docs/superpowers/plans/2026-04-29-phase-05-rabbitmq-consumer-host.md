# Phase 05 — RabbitMQ consumer + host Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tighten consumer-side correctness across 14 findings: the `_messagesBeingProcessed` counter race (H3), broker `basic.cancel` blindness (H24), unroutable retry/error publishes silently dropping (M3), plus a cluster of `InboundMessageProcessor` / `RabbitMqConsumerHost` fit-and-finish issues.

**Architecture:** All fixes live in `ServiceConnect.Client.RabbitMQ.Consumer/*` plus one cross-package change for H24: `IConsumer.IsCancelledByBroker` (new property) propagates a broker-cancel signal up through `Consumer` (aggregator) and through `IBus.IsConsuming` so the existing `BusConsumingHealthCheck` reports `Unhealthy` automatically. Counter discipline (H3) moves from lock-protected `++` to atomic `Interlocked.Increment` everywhere. Retry/error publishes (M3) flip from `mandatory:false` to `mandatory:true`, surfacing unroutable failures via the existing `PublishException` catches.

**Tech Stack:** .NET multi-target net8.0/net10.0, xUnit + Moq for unit tests, Testcontainers RabbitMQ for E2E, `RabbitMQ.Client` for consumer plumbing, Astro/Starlight for website docs.

**Spec:** [`docs/superpowers/specs/2026-04-29-phase-05-rabbitmq-consumer-host.md`](../specs/2026-04-29-phase-05-rabbitmq-consumer-host.md).

---

## Build/test safety

This machine has crashed when running unconstrained whole-solution `dotnet build` / `dotnet test` against this codebase (CLAUDE.md has the full incident analysis). The wrapper at `~/.local/bin/dotnet` re-execs every `dotnet` invocation under a systemd cgroup with `CPUQuota=800%`, `MemoryMax=8G`, `MemorySwapMax=0`, `TasksMax=200` and is the safety net. **Even with the wrapper, every command in this plan is per-csproj.** Add `-m:1` to `dotnet test` invocations to serialize MSBuild and stay within `TasksMax`. Never run whole-solution `dotnet build` / `dotnet test` / `dotnet format`.

For E2E: the user is in the `docker` group; call `docker` directly (no `sg docker -c`).

---

## File structure

### Modified — production code

- `src/ServiceConnect.Client.RabbitMQ/Consumer/Retry.cs` — M14 (formatting at line 104).
- `src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs` — M1 (off-by-one validation), M3 (mandatory:true on both publishes), smaller (explicit-field BasicProperties copy).
- `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs` — M4 (synthetic exception when handler is null), M6 (null-aware FullTypeName fallback in not-handled path), comment-update note for M3 catches, smaller header-comparer verification.
- `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` — H3 (atomic counter), H24 (`_consumerCancelledByBroker` flag + `IsCancelledByBroker` getter), M7 (Debug log + IsOpen pre-check), M8 (defensive `_messageProcessor` null-check), M9 (split `StartConsumingAsync` into `PrepareAsync`/`BeginConsumingAsync`), M15 (string-header byte-count), smaller consumer-tag refresh.
- `src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs` — M9 (call `PrepareAsync` → bind loop → `BeginConsumingAsync`); H24 aggregator (`IsCancelledByBroker` returns `true` if any host reports it).
- `src/ServiceConnect.Interfaces/Bus/IConsumer.cs` — H24 (add `IsCancelledByBroker` property).
- `src/ServiceConnect/Bus.cs` — H24 (`IsConsuming` returns `false` if `_consumer?.IsCancelledByBroker == true`).

### New / modified — tests

- `src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerRetryCountValidationTests.cs` — M1 (new or extend existing).
- `src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerCopyPropsTests.cs` — smaller (new).
- `src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorNotHandledFallbackTests.cs` — M6 (new).
- `src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorNullHandlerTests.cs` — M4 (new).
- `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostHeaderSizeTests.cs` — M15 (new or extend).
- `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostAckNackTests.cs` — M7 (new or extend).
- `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostMessageProcessorNullTests.cs` — M8 (new).
- `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostInflightCounterTests.cs` — H3 (new).
- `src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerMandatoryTests.cs` — M3 (new).
- `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostBindOrderingTests.cs` — M9 (new).
- `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostRecoveryTests.cs` — smaller consumer-tag refresh (new, may be skipped if API isn't testable).
- `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostBrokerCancelTests.cs` — H24 host-side (new).
- `src/ServiceConnect.UnitTests/HealthChecks/BusConsumingHealthCheckTests.cs` — H24 health-check side (extend existing).
- `src/ServiceConnect.EndToEndTests/RabbitMq/ConsumerBrokerCancelE2ETests.cs` — H24 E2E (new).
- `src/ServiceConnect.EndToEndTests/RabbitMq/UnroutableRetryPublishE2ETests.cs` — M3 E2E (new).

### Modified — website + sidebar

- `website/src/content/docs/learn/operations/observability.mdx` — broker-cancel signal documentation.
- `website/src/content/docs/reference/healthchecks/` — add `Consumer cancelled by broker` failure mode.
- `website/src/content/docs/learn/operations/error-handling.mdx` — clarify retry-publish behaviour when topology is missing.
- `website/src/content/docs/releases.mdx` — Phase 5 release-notes section.

---

## Task 1: M14 — Format Retry.cs:104

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/Retry.cs`

This task lands first as a trivial format fix; confirms the per-csproj build/test pipeline works before substantive changes start.

- [ ] **Step 1: Read the current Retry.cs around line 104**

```bash
sed -n '95,115p' src/ServiceConnect.Client.RabbitMQ/Consumer/Retry.cs
```

Confirm the current shape: `} (exceptions ??= []).Add(ex);` collapsed onto one line.

- [ ] **Step 2: Apply the formatting fix**

Edit `src/ServiceConnect.Client.RabbitMQ/Consumer/Retry.cs` to split:

```csharp
// before
                if (shouldRetry != null && !shouldRetry(ex))
                {
                    throw;
                } (exceptions ??= []).Add(ex);

// after
                if (shouldRetry != null && !shouldRetry(ex))
                {
                    throw;
                }

                (exceptions ??= []).Add(ex);
```

- [ ] **Step 3: Build and run existing Retry tests**

```bash
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Retry" -m:1
```

Expected: build clean, all Retry tests pass (no behaviour change).

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/Retry.cs
git commit -m "style(retry): split collapsed statements at Retry.cs:104"
```

---

## Task 2: M1 — Reject `RetryCount > maxRetries` (off-by-one)

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs`
- Create or extend: `src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerRetryCountValidationTests.cs`

The legitimate range for the `RetryCount` header is `[0, _maxRetries]`. A value of `_maxRetries + 1` is malformed and should route via the existing "Malformed or out-of-range" warning path.

- [ ] **Step 1: Locate any existing retry-count validation tests**

```bash
grep -rln "Malformed or out-of-range\|RetryCount.*Validation\|RetryCount.*Test" src/ServiceConnect.UnitTests/ | head
```

If there's an existing file covering this surface, extend it; otherwise create the new file.

- [ ] **Step 2: Write the failing test**

Create or append to `src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerRetryCountValidationTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class MessageRetryHandlerRetryCountValidationTests
{
    private const int MaxRetries = 3;

    private static (MessageRetryHandler handler, Mock<IChannel> channel, List<(string Exchange, string RoutingKey)> publishes) CreateHandler()
    {
        var publishes = new List<(string, string)>();
        var channel = new Mock<IChannel>();
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>(
                (ex, rk, _, _, _, _) => publishes.Add((ex, rk)))
            .Returns(ValueTask.CompletedTask);

        var handler = new MessageRetryHandler(MaxRetries, "test.error", NullLogger.Instance);
        return (handler, channel, publishes);
    }

    [Fact]
    public async Task RetryCount_EqualsMaxRetries_RoutesToErrorExchange_NotMalformed()
    {
        var (handler, channel, publishes) = CreateHandler();

        var args = new BasicDeliverEventArgs(
            consumerTag: "ct",
            deliveryTag: 1,
            redelivered: false,
            exchange: "",
            routingKey: "main",
            properties: new BasicProperties(),
            body: new byte[] { 1 });

        var headers = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [HeaderKeys.RetryCount] = MaxRetries,
        };

        await handler.HandleFailureAsync(channel.Object, "main.Retries", args, headers, ex: null);

        // Routes to the error exchange (max-retries-exceeded path), NOT a "malformed" warning.
        var publish = Assert.Single(publishes);
        Assert.Equal("test.error", publish.Exchange);
    }

    [Fact]
    public async Task RetryCount_GreaterThanMaxRetries_RoutedToErrorExchangeAsMalformed()
    {
        var (handler, channel, publishes) = CreateHandler();

        var args = new BasicDeliverEventArgs(
            consumerTag: "ct",
            deliveryTag: 1,
            redelivered: false,
            exchange: "",
            routingKey: "main",
            properties: new BasicProperties(),
            body: new byte[] { 1 });

        var headers = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [HeaderKeys.RetryCount] = MaxRetries + 1,  // out-of-range
        };

        await handler.HandleFailureAsync(channel.Object, "main.Retries", args, headers, ex: null);

        // Pre-fix: candidate (= MaxRetries + 1) passes the `> _maxRetries + 1` check
        // and is admitted as a legitimate retry count, so it routes via the normal
        // max-retries-exceeded path — NOT via the "malformed" warning.
        // Post-fix: rejected at the `> _maxRetries` check, routes via PublishErrorAsync
        // with logAsMaxRetries: false (the "malformed" warning path).
        // We can't distinguish the two paths from the publish destination alone (both
        // go to the error exchange). The discriminator is the log level/message;
        // assert that exactly ONE publish occurred (i.e., we didn't try to retry first).
        var publish = Assert.Single(publishes);
        Assert.Equal("test.error", publish.Exchange);
    }
}
```

Note: distinguishing "malformed" from "max-retries" requires reading log lines; the simpler test asserts the publish count (one publish, not zero, not two). For a stronger assertion, capture log levels via Moq's `ILogger` setup.

- [ ] **Step 3: Run the tests pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageRetryHandlerRetryCountValidationTests" -m:1
```

Expected: both tests pass pre-fix (the existing logic admits `MaxRetries + 1` as legitimate, leading to one publish). The behavioural difference between pre-fix and post-fix is at the LOG LEVEL/MESSAGE, not the publish-count.

If the implementer wants a stronger assertion: extend the test to inject a Moq `ILogger` and capture log levels. The "malformed" path at `MessageRetryHandler.cs:52-54` logs at Warning with text containing "Malformed or out-of-range"; the legitimate max-retries path logs at Error with text containing "Max retries exceeded".

Strengthen Test 2's assertions:

```csharp
// Replace NullLogger.Instance with a Moq<ILogger> that captures log entries.
// Build a logger like Phase 4's OutboundHeaderBuilderPriorityTests pattern (DynamicInvoke + IsEnabled).
// After the call, assert: ONE Warning containing "Malformed or out-of-range".

var warningLogs = capturedLogs.Where(l => l.Level == LogLevel.Warning).ToList();
Assert.Single(warningLogs);
Assert.Contains("Malformed or out-of-range", warningLogs[0].Message);
```

- [ ] **Step 4: Apply the M1 fix**

Edit `src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs` line 47:

```csharp
// before
if (candidate < 0 || candidate > _maxRetries + 1)

// after
if (candidate < 0 || candidate > _maxRetries)
```

- [ ] **Step 5: Run tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageRetryHandler" -m:1
```

Expected: all MessageRetryHandler tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerRetryCountValidationTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs
git commit -m "fix(retry): reject RetryCount > maxRetries (off-by-one)"
```

---

## Task 3: Smaller — Explicit-field `BasicProperties` copy

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs`
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerCopyPropsTests.cs`

Replace the copy-constructor `new BasicProperties(args.BasicProperties)` with explicit field copying. This protects against malformed source-property fields throwing inside the copy-ctor.

- [ ] **Step 1: Verify the BasicProperties shape in RabbitMQ.Client v7**

Read the type definition (use Read tool on the package source if available, or search the project's existing usage):

```bash
grep -rn "BasicProperties\b" src/ServiceConnect.Client.RabbitMQ/ --include="*.cs" | head
```

The standard AMQP BASIC properties: ContentType, ContentEncoding, DeliveryMode, Priority, CorrelationId, ReplyTo, Expiration, MessageId, Timestamp, Type, UserId, AppId, Headers. Verify all are settable on `BasicProperties` in v7.

- [ ] **Step 2: Write the failing test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerCopyPropsTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class MessageRetryHandlerCopyPropsTests
{
    [Fact]
    public async Task HandleFailureAsync_RetryPublish_PreservesAllSourceProperties()
    {
        BasicProperties? capturedProps = null;
        var channel = new Mock<IChannel>();
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>(
                (_, _, _, p, _, _) => capturedProps = p)
            .Returns(ValueTask.CompletedTask);

        var sourceProps = new BasicProperties
        {
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            DeliveryMode = DeliveryModes.Persistent,
            Priority = (byte)5,
            CorrelationId = "corr-1",
            ReplyTo = "reply.queue",
            Expiration = "60000",
            MessageId = "msg-1",
            Timestamp = new AmqpTimestamp(1234567890),
            Type = "MyMessage",
            UserId = "guest",
            AppId = "test-app",
        };

        var args = new BasicDeliverEventArgs(
            consumerTag: "ct",
            deliveryTag: 1,
            redelivered: false,
            exchange: "",
            routingKey: "main",
            properties: sourceProps,
            body: new byte[] { 1 });

        var handler = new MessageRetryHandler(maxRetries: 3, errorExchange: "error", NullLogger.Instance);
        var headers = new Dictionary<string, object>(StringComparer.Ordinal);

        await handler.HandleFailureAsync(channel.Object, "main.Retries", args, headers, ex: null);

        Assert.NotNull(capturedProps);
        Assert.Equal("application/json", capturedProps.ContentType);
        Assert.Equal("utf-8", capturedProps.ContentEncoding);
        Assert.Equal(DeliveryModes.Persistent, capturedProps.DeliveryMode);
        Assert.Equal((byte)5, capturedProps.Priority);
        Assert.Equal("corr-1", capturedProps.CorrelationId);
        Assert.Equal("reply.queue", capturedProps.ReplyTo);
        Assert.Equal("60000", capturedProps.Expiration);
        Assert.Equal("msg-1", capturedProps.MessageId);
        Assert.Equal(new AmqpTimestamp(1234567890), capturedProps.Timestamp);
        Assert.Equal("MyMessage", capturedProps.Type);
        Assert.Equal("guest", capturedProps.UserId);
        Assert.Equal("test-app", capturedProps.AppId);
    }
}
```

- [ ] **Step 3: Run the test pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageRetryHandlerCopyPropsTests" -m:1
```

Expected: PASS pre-fix (the existing copy-constructor preserves these fields). The test locks in field-by-field copy correctness so the upcoming explicit-copy refactor can't silently drop a field.

- [ ] **Step 4: Apply the smaller fix — explicit field copy**

Edit `src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs`:

Replace `MessageRetryHandler.cs:65-68`:

```csharp
// before
var props = new BasicProperties(args.BasicProperties)
{
    Headers = HeaderHelpers.ToNullableHeaders(headers)
};

// after
// Explicit copy avoids the copy-constructor's "any malformed source field throws" risk.
// The set of fields here mirrors the AMQP BASIC properties RabbitMQ.Client exposes;
// adding a field to BasicProperties without updating this copy is a silent regression —
// MessageRetryHandlerCopyPropsTests guards against that.
var props = new BasicProperties
{
    ContentType = args.BasicProperties.ContentType,
    ContentEncoding = args.BasicProperties.ContentEncoding,
    DeliveryMode = args.BasicProperties.DeliveryMode,
    Priority = args.BasicProperties.Priority,
    CorrelationId = args.BasicProperties.CorrelationId,
    ReplyTo = args.BasicProperties.ReplyTo,
    Expiration = args.BasicProperties.Expiration,
    MessageId = args.BasicProperties.MessageId,
    Timestamp = args.BasicProperties.Timestamp,
    Type = args.BasicProperties.Type,
    UserId = args.BasicProperties.UserId,
    AppId = args.BasicProperties.AppId,
    Headers = HeaderHelpers.ToNullableHeaders(headers),
};
```

Apply the same pattern at `MessageRetryHandler.cs:120-123`:

```csharp
// before
var errorProps = new BasicProperties(args.BasicProperties)
{
    Headers = HeaderHelpers.ToNullableHeaders(headers)
};

// after
var errorProps = new BasicProperties
{
    ContentType = args.BasicProperties.ContentType,
    ContentEncoding = args.BasicProperties.ContentEncoding,
    DeliveryMode = args.BasicProperties.DeliveryMode,
    Priority = args.BasicProperties.Priority,
    CorrelationId = args.BasicProperties.CorrelationId,
    ReplyTo = args.BasicProperties.ReplyTo,
    Expiration = args.BasicProperties.Expiration,
    MessageId = args.BasicProperties.MessageId,
    Timestamp = args.BasicProperties.Timestamp,
    Type = args.BasicProperties.Type,
    UserId = args.BasicProperties.UserId,
    AppId = args.BasicProperties.AppId,
    Headers = HeaderHelpers.ToNullableHeaders(headers),
};
```

- [ ] **Step 5: Run the tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageRetryHandler" -m:1
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerCopyPropsTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs
git commit -m "fix(retry): explicit-field BasicProperties copy"
```

---

## Task 4: M6 — Null-aware `FullTypeName` fallback in not-handled path

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs`
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorNotHandledFallbackTests.cs`

The not-handled-with-DLQ path at lines 170-173 only triggers the `TypeName` fallback when `FullTypeName` key is ABSENT, not when it's present-but-null. The pattern at lines 87-90 in the same file gets this right; mirror it.

- [ ] **Step 1: Locate existing InboundMessageProcessor test patterns**

```bash
ls src/ServiceConnect.UnitTests/RabbitMQ/ | grep -i Inbound
grep -ln "InboundMessageProcessor" src/ServiceConnect.UnitTests/RabbitMQ/ -r 2>/dev/null
```

Reuse the simplest arrange shape; if there's an existing `InboundMessageProcessorAuditCancellationTests.cs` or similar, mirror it.

- [ ] **Step 2: Write the failing test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorNotHandledFallbackTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class InboundMessageProcessorNotHandledFallbackTests
{
    [Fact]
    public async Task ProcessAsync_NotHandled_FullTypeNameNullFallsBackToTypeName()
    {
        // Arrange: build the processor with deadLetterUnhandledMessages=true, errorsDisabled=false.
        // Capture the exception passed to HandleTerminalFailureAsync; it should mention "Foo.Bar"
        // (sourced from TypeName via the fallback), NOT "<unknown>".

        Exception? capturedException = null;
        var retryHandler = new Mock<MessageRetryHandler>(MockBehavior.Loose, 3, "error.exchange", NullLogger.Instance);
        // MessageRetryHandler is sealed in the production tree; if Moq cannot mock it,
        // build a real instance and inject a Moq<IChannel> whose BasicPublishAsync captures
        // the exception via the BasicProperties.Headers["Exception"] JSON. See the
        // alternative test shape below if MessageRetryHandler is sealed.

        // Alternative arrange shape if MessageRetryHandler is sealed:
        var channel = new Mock<IChannel>();
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>(
                (_, _, _, props, _, _) =>
                {
                    if (props.Headers != null && props.Headers.TryGetValue(HeaderKeys.Exception, out var exJson) && exJson is string s)
                    {
                        // Capture the JSON; assertion below parses it.
                        capturedException = new InvalidOperationException(s);
                    }
                })
            .Returns(ValueTask.CompletedTask);

        var realRetryHandler = new MessageRetryHandler(maxRetries: 3, errorExchange: "error.exchange", NullLogger.Instance);
        var auditPublisher = new MessageAuditPublisher(Mock.Of<IQueueConfiguration>());
        var queueConfig = Mock.Of<IQueueConfiguration>(q => q.QueueName == "main-q");

        var processor = new InboundMessageProcessor(
            consumerEventHandler: (body, type, headers, ct) =>
                Task.FromResult(new ConsumeEventResult { Success = true, NotHandled = true }),
            retryHandler: realRetryHandler,
            auditPublisher: auditPublisher,
            queueConfiguration: queueConfig,
            timeProvider: TimeProvider.System,
            logger: NullLogger.Instance,
            retryQueueName: "main-q.Retries",
            errorsDisabled: false,
            deadLetterUnhandledMessages: true,
            includeMachineNameInHeaders: false,
            shutdownTimedOut: () => false,
            shutdownPublishToken: () => CancellationToken.None);

        var props = new BasicProperties
        {
            // FullTypeName is present but NULL.
            Headers = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [HeaderKeys.FullTypeName] = null,
                [HeaderKeys.TypeName] = "Foo.Bar",
            }!,
        };

        var args = new BasicDeliverEventArgs(
            consumerTag: "ct",
            deliveryTag: 1,
            redelivered: false,
            exchange: "",
            routingKey: "main-q",
            properties: props,
            body: new byte[] { 1 });

        await processor.ProcessAsync(channel.Object, args, CancellationToken.None);

        Assert.NotNull(capturedException);
        // Pre-fix: the exception payload (JSON) contains "<unknown>" because the fallback was skipped.
        // Post-fix: the JSON contains "Foo.Bar".
        Assert.Contains("Foo.Bar", capturedException.Message);
    }
}
```

If `MessageRetryHandler` or `MessageAuditPublisher` constructors don't match the actual shapes, adapt. Read the actual class definitions first.

- [ ] **Step 3: Run the test pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InboundMessageProcessorNotHandledFallbackTests" -m:1
```

Expected: FAIL pre-fix — the captured Exception JSON contains `"<unknown>"` instead of `"Foo.Bar"` because the fallback at line 170 only triggered on absent key.

- [ ] **Step 4: Apply the M6 fix**

Edit `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs` lines 170-173:

```csharp
// before
if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var typeNameRaw))
{
    headers.TryGetValue(HeaderKeys.TypeName, out typeNameRaw);
}

// after — mirrors the pattern at lines 87-90
if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var typeNameRaw) || typeNameRaw is null)
{
    headers.TryGetValue(HeaderKeys.TypeName, out typeNameRaw);
}
```

- [ ] **Step 5: Run the tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InboundMessageProcessor" -m:1
```

Expected: all InboundMessageProcessor tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorNotHandledFallbackTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs
git commit -m "fix(processor): null-aware FullTypeName fallback in not-handled path"
```

---

## Task 5: M4 — Synthetic exception when consumer event handler is null

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs`
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorNullHandlerTests.cs`

Today the null-handler branch sets `result = new ConsumeEventResult { Success = false }` with no Exception attached. The retry handler then JSON-serializes a `null` exception, leaving error-queue consumers with no diagnostic.

- [ ] **Step 1: Write the failing test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorNullHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class InboundMessageProcessorNullHandlerTests
{
    [Fact]
    public async Task ProcessAsync_NullConsumerEventHandler_AttachesSyntheticInvalidOperationException()
    {
        // Capture the exception JSON published to the retry queue (if retries < maxRetries)
        // or to the error exchange (if max-retries-exhausted). With maxRetries=0 the very first
        // failure routes to the error exchange immediately.

        BasicProperties? capturedProps = null;
        var channel = new Mock<IChannel>();
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>(
                (_, _, _, p, _, _) => capturedProps = p)
            .Returns(ValueTask.CompletedTask);

        var retryHandler = new MessageRetryHandler(maxRetries: 0, errorExchange: "error", NullLogger.Instance);
        var auditPublisher = new MessageAuditPublisher(Mock.Of<IQueueConfiguration>(q => q.QueueName == "main-q"));
        var queueConfig = Mock.Of<IQueueConfiguration>(q => q.QueueName == "main-q");

        var processor = new InboundMessageProcessor(
            consumerEventHandler: null!,  // the case under test
            retryHandler: retryHandler,
            auditPublisher: auditPublisher,
            queueConfiguration: queueConfig,
            timeProvider: TimeProvider.System,
            logger: NullLogger.Instance,
            retryQueueName: "main-q.Retries",
            errorsDisabled: false,
            deadLetterUnhandledMessages: false,
            includeMachineNameInHeaders: false,
            shutdownTimedOut: () => false,
            shutdownPublishToken: () => CancellationToken.None);

        var args = new BasicDeliverEventArgs(
            consumerTag: "ct",
            deliveryTag: 1,
            redelivered: false,
            exchange: "",
            routingKey: "main-q",
            properties: new BasicProperties
            {
                Headers = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [HeaderKeys.FullTypeName] = "Foo.Bar",
                }!,
            },
            body: new byte[] { 1 });

        await processor.ProcessAsync(channel.Object, args, CancellationToken.None);

        Assert.NotNull(capturedProps);
        Assert.NotNull(capturedProps.Headers);
        Assert.True(capturedProps.Headers.TryGetValue(HeaderKeys.Exception, out var exJson));
        Assert.NotNull(exJson);
        Assert.IsType<string>(exJson);
        // Post-fix: the JSON contains the synthetic InvalidOperationException type and message.
        // Pre-fix: the JSON serialises a null Exception (e.g. {"ExceptionType": null, ...}).
        Assert.Contains("InvalidOperationException", (string)exJson);
        Assert.Contains("Consumer event handler not set", (string)exJson);
    }
}
```

- [ ] **Step 2: Run the test pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InboundMessageProcessorNullHandlerTests" -m:1
```

Expected: FAIL pre-fix — the JSON has `null` for `ExceptionType` and `Message`, the assertions on "InvalidOperationException" / "Consumer event handler not set" fail.

- [ ] **Step 3: Apply the M4 fix**

Edit `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs` lines 94-98:

```csharp
// before
if (_consumerEventHandler == null)
{
    _logger.LogError("Consumer event handler not set — message will be nacked for redelivery. Queue: {Queue}", _queueConfiguration.QueueName);
    result = new ConsumeEventResult { Success = false };
}

// after
if (_consumerEventHandler == null)
{
    _logger.LogError("Consumer event handler not set — message will be nacked for redelivery. Queue: {Queue}", _queueConfiguration.QueueName);
    result = new ConsumeEventResult
    {
        Success = false,
        Exception = new InvalidOperationException("Consumer event handler not set; message could not be dispatched."),
    };
}
```

- [ ] **Step 4: Run the test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InboundMessageProcessor" -m:1
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorNullHandlerTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs
git commit -m "fix(processor): attach synthetic exception when consumer event handler is null"
```

---

## Task 6: M15 — Bound `string` header values too

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs`
- Create or extend: `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostHeaderSizeTests.cs`

The current header-size guard at lines 264-282 only bounds `byte[]` values. ServiceConnect itself stamps strings (`TypeName`, `FullTypeName`); a buggy or malicious producer could send a 100MB string and exhaust memory on every consumer.

- [ ] **Step 1: Locate existing header-size tests**

```bash
grep -rln "DefaultMaxHeaderValueBytes\|InboundHeader.*size\|Header.*ExceedsConfiguredLimit" src/ServiceConnect.UnitTests/ | head
```

If there's an existing `RabbitMqConsumerHostHeaderSizeTests.cs` (or similar), extend it; else create.

- [ ] **Step 2: Write the failing test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostHeaderSizeTests.cs` (or extend the existing file). The test must drive the host's `EventAsync` path — depending on existing test seams this may need either reflection, an internal raise-delivery seam, or a Moq-driven `IConsumer.HandleBasicDeliverAsync` invocation:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class RabbitMqConsumerHostHeaderSizeTests
{
    [Fact]
    public async Task EventAsync_LargeStringHeader_RoutesToTerminalFailure()
    {
        // [Reuse the existing RabbitMqConsumerHost test arrange harness — find a sibling
        //  test like RabbitMqConsumerHostConsumeMessageTypeTests.cs and copy its setup.
        //  The harness must allow the test to drive a synthetic delivery through EventAsync,
        //  capture the exception passed to HandleTerminalFailureAsync, and skip the actual
        //  ack/nack via a Moq IChannel.]

        // Arrange: 9000-byte string header (>8192 default limit).
        var largeString = new string('x', 9000);
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [HeaderKeys.FullTypeName] = "Foo.Bar",
            ["X-Large"] = largeString,
        };

        // Drive the delivery; capture the Exception that was passed into HandleTerminalFailureAsync.
        // Assert: exception message contains the header key "X-Large" and "9000" (or the actual byte count).
    }

    [Fact]
    public async Task EventAsync_SmallStringHeader_PassesAdmission()
    {
        // Headers["X-Small"] = "small" — 5 bytes, well under 8192.
        // Drive the delivery; assert HandleTerminalFailureAsync was NOT called for header-size reasons.
    }
}
```

The implementer fills in the harness using existing patterns. If no existing test drives `EventAsync` directly, the implementer adds an internal test seam:

```csharp
// In RabbitMqConsumerHost.cs (internal):
internal Task RaiseDeliveryForTests(BasicDeliverEventArgs args, CancellationToken ct = default)
    => EventAsync(this, args, ct);
```

Note: `EventAsync` is `private`; the seam must use a wrapper or re-shape the `EventAsync` to be `internal` if no other path lets the test exercise it. Choose the smallest surface change.

- [ ] **Step 3: Run the test pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMqConsumerHostHeaderSizeTests" -m:1
```

Expected: large-string test FAILS — pre-fix the loop only checks `byte[]`, so a 9000-byte string passes through.

- [ ] **Step 4: Apply the M15 fix**

Edit `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` lines 264-282. Replace the loop body:

```csharp
if (inboundHeaders != null)
{
    foreach (var kvp in inboundHeaders)
    {
        int byteSize;
        if (kvp.Value is byte[] bytes)
        {
            byteSize = bytes.Length;
        }
        else if (kvp.Value is string s)
        {
            // string headers stamped by ServiceConnect (TypeName, FullTypeName, etc.) need
            // bounding too — a buggy producer could send a 100MB string and exhaust memory
            // on every consumer in the system. UTF-8 byte count matches the on-wire size.
            byteSize = System.Text.Encoding.UTF8.GetByteCount(s);
        }
        else
        {
            // Non-string, non-byte-array headers (int, bool, etc.) are size-bounded by their type.
            continue;
        }

        if (byteSize > DefaultMaxHeaderValueBytes)
        {
            await _retryHandler.HandleTerminalFailureAsync(
                publishChannel!,
                args,
                CopyInboundHeaders(args),
                new InvalidOperationException(
                    $"Inbound header '{kvp.Key}' size {byteSize} bytes exceeds configured limit {DefaultMaxHeaderValueBytes} bytes."),
                GetShutdownPublishToken())
                .ConfigureAwait(false);
            processed = true;
            return;
        }
    }
}
```

- [ ] **Step 5: Run tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMqConsumerHost" -m:1
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostHeaderSizeTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs
git commit -m "fix(consumer-host): bound string header values too"
```

---

## Task 7: M7 — Demote ack/nack-on-null log to Debug; add `IsOpen` pre-check

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs`
- Create or extend: `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostAckNackTests.cs`

- [ ] **Step 1: Write the failing tests**

Create or extend `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostAckNackTests.cs`. The two tests need to capture log entries (canonical `DynamicInvoke` + `IsEnabled` pattern from Phase 4 cleanup) and observe that no `BasicAckAsync` / `BasicNackAsync` was called when the channel is null or closed:

```csharp
[Fact]
public async Task EventAsync_ChannelNullDuringFinally_LogsAtDebug()
{
    // Arrange: drive a delivery through EventAsync; AFTER admission, set _model = null
    //   (via reflection on the host instance) so the finally block sees model == null.
    // Capture log entries through a Moq<ILogger>. Assert: exactly one log entry was
    //   emitted at LogLevel.Debug containing "Channel was null".

    // [Use the harness from Task 6 / RabbitMqConsumerHostHeaderSizeTests.]
}

[Fact]
public async Task EventAsync_ChannelClosedDuringFinally_LogsAtDebug_NoAckCalled()
{
    // Arrange: Moq<IChannel> with IsOpen returning false. Drive a delivery; capture
    //   logs and verify no BasicAckAsync / BasicNackAsync was called on the channel.

    // [Use the harness from Task 6 / RabbitMqConsumerHostHeaderSizeTests.]
}
```

The test bodies require the harness from Task 6 (or a sibling existing test). Reuse rather than redefine.

- [ ] **Step 2: Run the tests pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMqConsumerHostAckNackTests" -m:1
```

Expected: both fail. Test 1 fails because today the null-channel log is at LogLevel.Warning, not Debug. Test 2 fails because there's no `IsOpen` pre-check; the test would see `BasicAckAsync` / `BasicNackAsync` being called on the closed channel (which then throws `AlreadyClosedException`).

- [ ] **Step 3: Apply the M7 fix**

Edit `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` lines 296-311 (the ack/nack section in the `EventAsync` finally):

```csharp
if (callbackAdmitted)
{
    if (model == null)
    {
        // Channel was nulled by concurrent DisposeAsync. Expected during teardown;
        // broker will redeliver unacked messages on next consumer start.
        _logger.LogDebug("Channel was null during ack/nack — message {DeliveryTag} may be redelivered", args.DeliveryTag);
    }
    else if (!model.IsOpen)
    {
        // Channel closed concurrently. Expected during teardown / connection drop.
        _logger.LogDebug("Channel was closed during ack/nack — message {DeliveryTag} may be redelivered", args.DeliveryTag);
    }
    else if (Volatile.Read(ref _shutdownTimedOut) != 0)
    {
        _logger.LogDebug("Shutdown grace window expired before finishing message {DeliveryTag}; leaving unacked for broker redelivery", args.DeliveryTag);
    }
    else if (processed)
    {
        await model.BasicAckAsync(args.DeliveryTag, false).ConfigureAwait(false);
    }
    else
    {
        await model.BasicNackAsync(args.DeliveryTag, false, true).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Run the tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMqConsumerHost" -m:1
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostAckNackTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs
git commit -m "fix(consumer-host): demote ack/nack-on-null log to Debug; add IsOpen pre-check"
```

---

## Task 8: M8 — Defensive null-check on `_messageProcessor`

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs`
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostMessageProcessorNullTests.cs`

The `_messageProcessor!.ProcessAsync(...)` at line 284 silent-fails to NRE if `_messageProcessor` is null (e.g., after a future refactor changes init order). Add a defensive check.

- [ ] **Step 1: Write the failing test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostMessageProcessorNullTests.cs`. The test forces `_messageProcessor` to be null (via reflection if no other path exists) and drives a delivery:

```csharp
[Fact]
public async Task EventAsync_MessageProcessorNull_LogsWarning_AndNacksWithRequeue()
{
    // [Reuse host-arrange harness from Task 6/7.]
    // Set the host's _messageProcessor field to null via reflection.
    // Drive a synthetic delivery through EventAsync.
    // Capture log entries.
    //
    // Assert:
    //   1. No NRE thrown.
    //   2. Warning log emitted with "Message processor not initialised".
    //   3. The finally block called BasicNackAsync(_, _, true) — i.e. nack-with-requeue (processed=false path).
}
```

- [ ] **Step 2: Run the test pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMqConsumerHostMessageProcessorNullTests" -m:1
```

Expected: FAIL — pre-fix throws NRE on `_messageProcessor!`, the test sees an NRE leak through the host's own `catch (Exception ex)` block (which only logs), and the assertion on "Message processor not initialised" fails.

- [ ] **Step 3: Apply the M8 fix**

Edit `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` line 284:

```csharp
// before
processed = await _messageProcessor!.ProcessAsync(publishChannel!, args, cancellationToken).ConfigureAwait(false);

// after
var messageProcessor = _messageProcessor;
if (messageProcessor == null)
{
    _logger.LogWarning("Message processor not initialised — message {DeliveryTag} will be nacked for redelivery", args.DeliveryTag);
    return;  // processed stays false; finally nacks-with-requeue
}
processed = await messageProcessor.ProcessAsync(publishChannel!, args, cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 4: Run the test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMqConsumerHost" -m:1
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostMessageProcessorNullTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs
git commit -m "fix(consumer-host): defensive null-check on _messageProcessor"
```

---

## Task 9: H3 — Atomic `_messagesBeingProcessed` discipline

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs`
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostInflightCounterTests.cs`

This is a concurrency fix. Lands alone for reviewer focus. The bug: `_messagesBeingProcessed++` runs INSIDE the `lock (_callbackAdmissionGate)` block, but `Interlocked.Decrement(ref _messagesBeingProcessed)` and `Volatile.Read(ref _messagesBeingProcessed)` run OUTSIDE any lock. A concurrent decrement during the `lock`-protected `++` can lose the increment.

- [ ] **Step 1: Write the failing concurrent test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostInflightCounterTests.cs`:

```csharp
[Fact]
public async Task EventAsync_ConcurrentDeliveriesAndDrains_CounterReachesZero_NeverNegative()
{
    // [Reuse host-arrange harness from prior tasks.]
    //
    // 8 concurrent producer tasks, each driving 100 deliveries through the host's EventAsync.
    // Each handler delegate yields (await Task.Yield()) before completing to maximise interleaving.
    //
    // A background sampler polls Volatile.Read(ref _messagesBeingProcessed) every 1ms.
    // If any sample is < 0, fail immediately.
    //
    // After all 8 × 100 = 800 deliveries complete, brief settle window (Task.Delay(100)),
    // then assert: counter reads exactly 0.
    //
    // Pre-fix: counter goes negative or overshoots intermittently.
    // Post-fix: deterministically zero at drain.

    // Sketch (implementer fills in harness):
    var minObserved = int.MaxValue;
    using var samplerCts = new CancellationTokenSource();
    var sampler = Task.Run(async () =>
    {
        while (!samplerCts.IsCancellationRequested)
        {
            var v = ReadInflightCount(host);  // reflection helper
            if (v < minObserved) Interlocked.Exchange(ref minObserved, v);
            await Task.Delay(1);
        }
    });

    var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
    {
        for (int i = 0; i < 100; i++)
        {
            await DriveDelivery(host, /* synthetic args */);
        }
    })).ToArray();
    await Task.WhenAll(tasks);
    samplerCts.Cancel();
    await sampler;

    Assert.True(minObserved >= 0, $"Counter went negative: min observed = {minObserved}");
    Assert.Equal(0, ReadInflightCount(host));
}

private static int ReadInflightCount(RabbitMqConsumerHost host)
{
    var field = typeof(RabbitMqConsumerHost).GetField("_messagesBeingProcessed",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    return (int)field!.GetValue(host)!;
}
```

The handler delegate that the host calls into can be the bus-supplied `ConsumerEventHandler` — the test injects one that does `await Task.Yield()` then returns `Success = true`.

- [ ] **Step 2: Run the test pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMqConsumerHostInflightCounterTests" -m:1
```

Expected: FAIL or FLAKY pre-fix. The counter lost-update bug is racy; if a single run passes, run 5x to confirm. If the lost-update doesn't reproduce on this hardware, document it: the test still locks in the contract for the post-fix code, even if pre-fix the bug is too rare to hit reliably.

- [ ] **Step 3: Apply the H3 fix**

Edit `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs`. Find the admission block at lines 203-212:

```csharp
// before
lock (_callbackAdmissionGate)
{
    if (_shutdownStarted)
    {
        return;
    }

    _messagesBeingProcessed++;
    callbackAdmitted = true;
}

// after
lock (_callbackAdmissionGate)
{
    if (_shutdownStarted)
    {
        return;
    }

    callbackAdmitted = true;
}
Interlocked.Increment(ref _messagesBeingProcessed);
```

The `lock` block now only gates the `_shutdownStarted` admission decision; the counter mutation is atomic via `Interlocked.Increment`. Decrement at line 347 (already `Interlocked.Decrement`) and read at line 461 (already `Volatile.Read`) stay unchanged.

- [ ] **Step 4: Run the test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMqConsumerHost" -m:1
```

Expected: all pass.

Run the test 5 times to check determinism:

```bash
for i in 1 2 3 4 5; do
    dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMqConsumerHostInflightCounterTests" -m:1 || break
done
```

Expected: 5/5 pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostInflightCounterTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs
git commit -m "fix(consumer-host): atomic _messagesBeingProcessed discipline"
```

---

## Task 10: M3 — `mandatory:true` on retry/error publishes

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs` (two single-character flips + comments)
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs` (comment update on existing catches)
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerMandatoryTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerMandatoryTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class MessageRetryHandlerMandatoryTests
{
    private static (MessageRetryHandler handler, Mock<IChannel> channel, List<bool> mandatoryCaptures) CreateHandler(int maxRetries)
    {
        var captures = new List<bool>();
        var channel = new Mock<IChannel>();
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>(
                (_, _, mandatory, _, _, _) => captures.Add(mandatory))
            .Returns(ValueTask.CompletedTask);

        return (new MessageRetryHandler(maxRetries, "error.exchange", NullLogger.Instance), channel, captures);
    }

    private static BasicDeliverEventArgs MakeArgs() => new(
        consumerTag: "ct",
        deliveryTag: 1,
        redelivered: false,
        exchange: "",
        routingKey: "main",
        properties: new BasicProperties(),
        body: new byte[] { 1 });

    [Fact]
    public async Task HandleFailureAsync_RetryPath_PublishesMandatoryTrue()
    {
        var (handler, channel, captures) = CreateHandler(maxRetries: 3);
        var headers = new Dictionary<string, object>(StringComparer.Ordinal);

        await handler.HandleFailureAsync(channel.Object, "main.Retries", MakeArgs(), headers, ex: null);

        var mandatory = Assert.Single(captures);
        Assert.True(mandatory);
    }

    [Fact]
    public async Task HandleFailureAsync_MaxRetriesPath_PublishesMandatoryTrue()
    {
        var (handler, channel, captures) = CreateHandler(maxRetries: 0);  // first failure → error
        var headers = new Dictionary<string, object>(StringComparer.Ordinal);

        await handler.HandleFailureAsync(channel.Object, "main.Retries", MakeArgs(), headers, ex: null);

        var mandatory = Assert.Single(captures);
        Assert.True(mandatory);
    }

    [Fact]
    public async Task HandleTerminalFailureAsync_PublishesMandatoryTrue()
    {
        var (handler, channel, captures) = CreateHandler(maxRetries: 3);
        var headers = new Dictionary<string, object>(StringComparer.Ordinal);

        await handler.HandleTerminalFailureAsync(channel.Object, MakeArgs(), headers, new InvalidOperationException("test"));

        var mandatory = Assert.Single(captures);
        Assert.True(mandatory);
    }
}
```

- [ ] **Step 2: Run the tests pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageRetryHandlerMandatoryTests" -m:1
```

Expected: 3/3 FAIL — pre-fix the captured `mandatory` is `false`.

- [ ] **Step 3: Apply the M3 fix**

Edit `src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs`:

Line ~69 (retry publish):

```csharp
// before
await channel.BasicPublishAsync(string.Empty, retryQueueName, false, props, args.Body, cancellationToken).ConfigureAwait(false);

// after
// mandatory:true so publisher confirms surface unroutable returns as PublishException;
// otherwise the broker silently drops the message and we lose the failure signal.
// The catch in InboundMessageProcessor logs Error and acks-to-break-the-loop on PublishException.
await channel.BasicPublishAsync(string.Empty, retryQueueName, true, props, args.Body, cancellationToken).ConfigureAwait(false);
```

Line ~124 (error publish):

```csharp
// before
await channel.BasicPublishAsync(_errorExchange, string.Empty, false, errorProps, args.Body, cancellationToken).ConfigureAwait(false);

// after
// mandatory:true — see comment in HandleFailureAsync. PublishException on unroutable
// surfaces through the InboundMessageProcessor catch; logged at Error and acked to
// prevent unbounded redelivery.
await channel.BasicPublishAsync(_errorExchange, string.Empty, true, errorProps, args.Body, cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 4: Update comments on the existing catches in InboundMessageProcessor.cs**

Edit `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs`. Find the `catch (Exception retryEx)` block at lines 150-159 and the `catch (Exception terminalEx)` block at lines 205-211. Update the comment to mention `PublishException`:

```csharp
catch (Exception retryEx)
{
    _logger.LogError(retryEx,
        "Retry publish failed for MessageId {MessageId} (DeliveryTag {DeliveryTag}) on queue {Queue}; dropping to prevent unbounded redelivery loop.",
        args.BasicProperties.MessageId, args.DeliveryTag, _queueConfiguration.QueueName);
    // Intentionally swallow: the message is already failed and we cannot retry-publish it.
    // Includes RabbitMQ.Client.Exceptions.PublishException (raised when retry queue is gone
    // and mandatory:true returns the message). Acking now (processed = true, returned below)
    // prevents the broker from redelivering it into the same failed path. Letting the
    // exception propagate would cause the finally block to nack with requeue:true and
    // hot-loop the broker on a poison message.
}
```

(Same wording shape applied at the terminal-failure catch.)

- [ ] **Step 5: Run tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageRetryHandler|FullyQualifiedName~InboundMessageProcessor" -m:1
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerMandatoryTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs
git commit -m "fix(retry): mandatory:true on retry/error publishes"
```

---

## Task 11: M9 — Bind queues before `BasicConsume`

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` (split `StartConsumingAsync` into `PrepareAsync` + `BeginConsumingAsync`).
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs` (orchestrate Prepare → Bind → BeginConsuming).
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostBindOrderingTests.cs`

The current orchestration calls `StartConsumingAsync` (which issues `BasicConsumeAsync`) BEFORE `ConsumeMessageTypeAsync` (which issues `QueueBindAsync` on the same channel). RabbitMQ.Client requires per-channel serialisation; running a bind concurrent with an in-flight delivery callback violates that contract.

- [ ] **Step 1: Identify all callers of `StartConsumingAsync`**

```bash
grep -rn "StartConsumingAsync" src/ examples/ 2>/dev/null | grep -v "obj/\|bin/" | head
```

Expected callers: `Consumer.StartConsumingAsync` (line 181), and possibly tests. The `IConsumer` interface declares `StartConsumingAsync(string, IList<string>, ConsumerEventHandler, CancellationToken)`. The host's `StartConsumingAsync` is a separate (internal) method.

- [ ] **Step 2: Write the failing test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostBindOrderingTests.cs`. The test tracks call order on a Moq `IChannel`:

```csharp
[Fact]
public async Task StartConsumingAsync_BindsBeforeBasicConsume()
{
    int callOrder = 0;
    int? bindOrder = null;
    int? consumeOrder = null;

    var channel = new Mock<IChannel>();
    channel
        .Setup(c => c.QueueBindAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IDictionary<string, object?>>(), false, It.IsAny<CancellationToken>()))
        .Callback(() => bindOrder ??= Interlocked.Increment(ref callOrder))
        .Returns(Task.CompletedTask);
    channel
        .Setup(c => c.BasicConsumeAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>>(),
            It.IsAny<IAsyncBasicConsumer>(), It.IsAny<CancellationToken>()))
        .Callback(() => consumeOrder ??= Interlocked.Increment(ref callOrder))
        .ReturnsAsync("consumer-tag");

    // Drive Consumer.StartConsumingAsync (the orchestrator) with one message-type binding.
    // [Reuse arrange harness; mock IServiceConnectConnection to yield the captured channel for both
    //   _model and _publishChannel.]
    //
    // After the call:
    Assert.NotNull(bindOrder);
    Assert.NotNull(consumeOrder);
    Assert.True(bindOrder < consumeOrder, $"Expected QueueBindAsync ({bindOrder}) before BasicConsumeAsync ({consumeOrder})");
}
```

- [ ] **Step 3: Run the test pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMqConsumerHostBindOrderingTests" -m:1
```

Expected: FAIL — pre-fix `BasicConsumeAsync` is called before `QueueBindAsync`.

- [ ] **Step 4: Apply the M9 fix — split `StartConsumingAsync`**

Edit `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs`. The current `StartConsumingAsync` does setup work + issues `BasicConsumeAsync` at the end. Split it:

```csharp
/// <summary>
/// Sets up the consumer channel, publish channel, message processor, and broker-event subscriptions.
/// Does NOT call BasicConsumeAsync — the caller must invoke <see cref="ConsumeMessageTypeAsync"/>
/// for any required bindings, then <see cref="BeginConsumingAsync"/> to start consuming.
/// </summary>
public async Task PrepareAsync(
    ConsumerEventHandler messageReceived, string queueName,
    bool? autoDelete = null, CancellationToken cancellationToken = default)
{
    _consumerEventHandler = messageReceived;
    _queueName = queueName;
    _retryQueueName = queueName + RabbitMqQueueNaming.RetryQueueSuffix;

    if (autoDelete.HasValue)
    {
        _autoDelete = autoDelete.Value;
    }

    _model = await _connection.CreateChannelAsync(cancellationToken).ConfigureAwait(false);
    var publishChannelOptions = new CreateChannelOptions(
        publisherConfirmationsEnabled: true,
        publisherConfirmationTrackingEnabled: true);
    _publishChannel = await _connection.CreateChannelAsync(publishChannelOptions, cancellationToken).ConfigureAwait(false);
    if (!_disablePrefetch)
    {
        await _model.BasicQosAsync(0, _prefetchCount, false).ConfigureAwait(false);
    }

    Volatile.Write(ref _shutdownTimedOut, 0);
    _shutdownPublishCts.Dispose();
    _shutdownPublishCts = new CancellationTokenSource();
    _deliveryCts.Dispose();
    _deliveryCts = new CancellationTokenSource();
    _deliveryToken = _deliveryCts.Token;
    _messageProcessor = new InboundMessageProcessor(
        _consumerEventHandler,
        _retryHandler,
        _auditPublisher,
        _queueConfiguration,
        _timeProvider,
        _logger,
        _retryQueueName,
        _errorsDisabled,
        _deadLetterUnhandledMessages,
        _includeMachineNameInHeaders,
        shutdownTimedOut: () => Volatile.Read(ref _shutdownTimedOut) != 0,
        shutdownPublishToken: () => _shutdownPublishCts.Token);
    var deliveryToken = _deliveryToken;
    _consumer = new AsyncEventingBasicConsumer(_model);
    _consumer.ReceivedAsync += async (sender, args) => await EventAsync(sender, args, deliveryToken).ConfigureAwait(false);
    _consumer.ShutdownAsync += OnConsumerShutdownAsync;
    _consumer.UnregisteredAsync += OnConsumerUnregisteredAsync;
    _model.ChannelShutdownAsync += OnChannelShutdownAsync;
    var underlying = _connection.UnderlyingConnection;
    if (underlying is not null)
    {
        underlying.ConnectionShutdownAsync += OnConnectionShutdownAsync;
        underlying.ConnectionBlockedAsync += OnConnectionBlockedAsync;
        underlying.ConnectionUnblockedAsync += OnConnectionUnblockedAsync;
    }
    // NOTE: BasicConsumeAsync is deliberately NOT called here — see BeginConsumingAsync.
}

/// <summary>
/// Issues the BasicConsumeAsync that puts this host into the actively-consuming state.
/// MUST be called AFTER all <see cref="ConsumeMessageTypeAsync"/> bindings are complete —
/// running QueueBindAsync on the consumer channel after BasicConsume violates RabbitMQ.Client's
/// per-channel serialisation contract.
/// </summary>
public async Task BeginConsumingAsync(CancellationToken cancellationToken = default)
{
    if (_model == null || _consumer == null)
    {
        throw new InvalidOperationException("PrepareAsync must be called before BeginConsumingAsync.");
    }

    _consumerTag = await _model.BasicConsumeAsync(_queueName, false, "", false, false, null, _consumer, cancellationToken).ConfigureAwait(false);
    _logger.LogDebug("Started consuming on {QueueName}, tag={ConsumerTag}", _queueName, _consumerTag);
}

/// <summary>
/// Backward-compatible shorthand: <see cref="PrepareAsync"/> followed by <see cref="BeginConsumingAsync"/>.
/// Bind any per-message-type queues via <see cref="ConsumeMessageTypeAsync"/> BETWEEN these two calls
/// to honour RabbitMQ.Client's per-channel serialisation.
/// </summary>
public async Task StartConsumingAsync(
    ConsumerEventHandler messageReceived, string queueName,
    bool? autoDelete = null, CancellationToken cancellationToken = default)
{
    await PrepareAsync(messageReceived, queueName, autoDelete, cancellationToken).ConfigureAwait(false);
    await BeginConsumingAsync(cancellationToken).ConfigureAwait(false);
}
```

The `StartConsumingAsync` shorthand stays for any test/external caller that doesn't bind types. Production callers (`Consumer.cs`) use the new shape.

- [ ] **Step 5: Update Consumer.cs to use the new orchestration**

Edit `src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs` lines 181-185:

```csharp
// before
_clients.Add(client);
await client.StartConsumingAsync(eventHandler, queueName, cancellationToken: cancellationToken).ConfigureAwait(false);
foreach (string messageType in messageTypes)
{
    await client.ConsumeMessageTypeAsync(messageType, cancellationToken).ConfigureAwait(false);
}

// after
_clients.Add(client);
await client.PrepareAsync(eventHandler, queueName, cancellationToken: cancellationToken).ConfigureAwait(false);
foreach (string messageType in messageTypes)
{
    await client.ConsumeMessageTypeAsync(messageType, cancellationToken).ConfigureAwait(false);
}
await client.BeginConsumingAsync(cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 6: Run the test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMqConsumerHost|FullyQualifiedName~Consumer" -m:1
```

Expected: all pass — including any pre-existing `Consumer*Tests` that exercised the orchestrator.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostBindOrderingTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs
git commit -m "fix(consumer): bind queues before BasicConsume"
```

---

## Task 12: Smaller — Refresh consumer tag on auto-recovery

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs`
- Create (or skip if API isn't testable): `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostRecoveryTests.cs`

RabbitMQ.Client v7's auto-recovery re-issues `BasicConsumeAsync` on reconnect. The cached `_consumerTag` becomes stale, breaking later `BasicCancelAsync` calls during `DisposeAsync`.

- [ ] **Step 1: Investigate the v7 recovery API**

```bash
grep -rn "RecoverySucceeded\|RecoveringAsync\|RecoveredAsync\|IRecoverable\|AutorecoveringConnection" src/ServiceConnect.Client.RabbitMQ/ 2>/dev/null | head
grep -rn "ConsumerTags\|Recovery" /home/tim/.nuget/packages/rabbitmq.client/ 2>/dev/null | head -20
```

If RabbitMQ.Client exposes a recovery event in a usable shape (e.g. `IConnection.RecoverySucceededAsync` or `IRecoverable.Recovery`), proceed to Step 2. If not, document the limitation in a code comment and skip the fix:

```csharp
// RabbitMQ.Client v7 auto-recovery may re-issue BasicConsumeAsync on reconnect with
// a different consumer tag, leaving _consumerTag stale. We do NOT subscribe to a
// recovery callback to refresh the tag because the v7 surface does not expose this
// event in a stable way. Manifests only on DisposeAsync after auto-recovery (a narrow
// window); operator workaround: full pod restart on broker reconnect.
```

If the API IS available, continue.

- [ ] **Step 2: Write the test (if API is available)**

Create `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostRecoveryTests.cs`:

```csharp
[Fact]
public async Task RecoverySucceeded_RefreshesConsumerTag()
{
    // Build a host with a fake recoverable IConnection.
    // Start the host; capture the initial _consumerTag (e.g. "tag-1").
    //
    // Set up the channel mock so BasicConsumeAsync returns "tag-2" on the second call.
    // Fire the recovery event on the connection.
    // Wait briefly for the async handler to run.
    //
    // Assert: reflection-read of _consumerTag now equals "tag-2".
}
```

- [ ] **Step 3: Apply the smaller fix**

Edit `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs`. After the `BasicConsumeAsync` call in `BeginConsumingAsync` (added by Task 11), subscribe to the recovery event. Sketch (verify exact API name first):

```csharp
// In BeginConsumingAsync, after _consumerTag = await _model.BasicConsumeAsync(...):
var underlying = _connection.UnderlyingConnection;
if (underlying is global::RabbitMQ.Client.IRecoverable recoverable)
{
    recoverable.RecoverySucceeded += OnRecoverySucceeded;
}

private void OnRecoverySucceeded(object? sender, EventArgs args)
{
    // After auto-recovery the broker has re-issued our consumer; refresh the cached tag.
    if (_consumer is not null && _consumer.ConsumerTags.Length > 0)
    {
        _consumerTag = _consumer.ConsumerTags[0];
        _logger.LogDebug("Consumer tag refreshed after auto-recovery: {ConsumerTag}", _consumerTag);
    }
}
```

Also add unsubscribe in `DisposeAsync` (around lines 506-512 where other connection-event handlers are unsubscribed).

If the v7 API differs (e.g. `RecoverySucceededAsync`), adapt; if not exposed at all, apply the documentation-only path from Step 1 instead.

- [ ] **Step 4: Run the test**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMqConsumerHostRecovery" -m:1
```

Expected: pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostRecoveryTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs
git commit -m "fix(consumer-host): refresh consumer tag on auto-recovery"
```

(If the API isn't available and only the documentation-comment was added, drop the test file from the commit and use the message: `docs(consumer-host): document consumer-tag staleness on auto-recovery (v7 API limitation)`)

---

## Task 13: Smaller — Verify `StringComparer.Ordinal` choice on inbound headers

**Files:**
- Read: callers of `InboundMessageProcessor` headers dict
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs` (comment OR comparer change)
- Possibly create: regression test if comparer changes

This is a verification task — read code, decide, document. The phase doc flags case-insensitive HTTP-style consumers downstream as a possible mismatch, but AMQP keys are ordinal on the wire.

- [ ] **Step 1: Trace downstream readers of the headers dict**

```bash
# Find consumers of the headers passed by InboundMessageProcessor to ConsumerEventHandler.
grep -rn "ConsumerEventHandler\|ConsumeContext\|ConsumeContextAccessor" src/ServiceConnect/ src/ServiceConnect.Telemetry/ src/ServiceConnect.Filters.MessageDeduplication/ --include="*.cs" 2>/dev/null | grep -v "obj/\|bin/" | head -30

# Find sites that look up known header keys via string lookup.
grep -rn "headers\[\".*\"\]\|headers.TryGetValue\|HeaderKeys\." src/ServiceConnect/ src/ServiceConnect.Telemetry/ --include="*.cs" 2>/dev/null | grep -v "obj/\|bin/\|.xml:" | head -30
```

Identify whether any code path:
- Looks up a header key with a case-insensitive comparison (e.g. `headers.Keys.Any(k => string.Equals(k, "...", StringComparison.OrdinalIgnoreCase))`).
- Stamps a key in one case and reads it back in another case.

- [ ] **Step 2: Document the conclusion**

If all callers use ordinal-case lookup (likely): add a code comment at `InboundMessageProcessor.cs:55-57`:

```csharp
// StringComparer.Ordinal is correct: AMQP header keys are case-sensitive on the wire,
// and ServiceConnect's HeaderKeys constants use a fixed casing. Downstream readers (filters,
// middleware, ConsumeContext) all look up headers by the canonical HeaderKeys.X constants;
// no case-insensitive lookup paths exist. Verified <YYYY-MM-DD by Phase 5 Task 13>.
```

If a case-insensitive consumer exists (unlikely but possible): change the comparer to `StringComparer.OrdinalIgnoreCase` AND add a regression test that proves the new comparer matters (e.g. lookup with mixed-case key).

- [ ] **Step 3: Build + test**

```bash
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InboundMessageProcessor" -m:1
```

Expected: all pass.

- [ ] **Step 4: Commit**

If only a comment was added:

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs
git commit -m "verify(processor): document StringComparer.Ordinal rationale"
```

If the comparer was changed:

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorHeaderComparerTests.cs
git commit -m "fix(processor): use OrdinalIgnoreCase comparer for inbound headers"
```

---

## Task 14: H24 — `IsCancelledByBroker` propagates through to health-check

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Bus/IConsumer.cs` (add `IsCancelledByBroker` property).
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` (flag + setter in `OnConsumerUnregisteredAsync` + `internal IsCancelledByBroker` getter).
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs` (aggregate flag across hosts).
- Modify: `src/ServiceConnect/Bus.cs` (`IsConsuming` short-circuits to false on broker-cancel).
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostBrokerCancelTests.cs`.
- Modify or create: `src/ServiceConnect.UnitTests/HealthChecks/BusConsumingHealthCheckTests.cs`.

Riskiest commit in the phase. Lands second-to-last so the rest of the phase has stabilised.

- [ ] **Step 1: Add `IsCancelledByBroker` to `IConsumer`**

Edit `src/ServiceConnect.Interfaces/Bus/IConsumer.cs`:

```csharp
namespace ServiceConnect.Interfaces;

/// <summary>
/// Consumes messages from the message broker.
/// </summary>
public interface IConsumer : IAsyncDisposable
{
    /// <summary>
    /// Gets whether the consumer is currently connected to the broker.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Gets whether the broker has cancelled this consumer (e.g. queue deleted, queue policy
    /// expired, mirror promoted). When true the consumer is no longer receiving deliveries
    /// from the broker; <see cref="IBus.IsConsuming"/> returns false to signal the unhealthy state.
    /// </summary>
    bool IsCancelledByBroker { get; }

    /// <summary>
    /// Starts consuming messages from the specified queue for the given message types.
    /// </summary>
    Task StartConsumingAsync(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler, CancellationToken cancellationToken = default);
}
```

This is a public-interface addition; any external implementer of `IConsumer` MUST add this property. Document in the release notes (Task 16).

- [ ] **Step 2: Add the host-side flag and getter**

Edit `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs`. Add the field with the other private fields (near `_messagesBeingProcessed`):

```csharp
// Set by OnConsumerUnregisteredAsync when the broker cancels our consumer (queue deleted,
// policy expired, mirror promoted). Bubbled up through Consumer.IsCancelledByBroker → Bus.IsConsuming
// → BusConsumingHealthCheck so operators see the bus go Unhealthy when this happens.
private int _consumerCancelledByBroker;

internal bool IsCancelledByBroker => Volatile.Read(ref _consumerCancelledByBroker) != 0;
```

Modify `OnConsumerUnregisteredAsync` (lines 386-393) to set the flag synchronously:

```csharp
private Task OnConsumerUnregisteredAsync(object? sender, ConsumerEventArgs args)
{
    Interlocked.Exchange(ref _consumerCancelledByBroker, 1);
    _logger.LogWarning(
        "AMQP consumer '{ConsumerTag}' unregistered by broker (broker-initiated shutdown) on queue '{Queue}'; reporting unhealthy via BusConsumingHealthCheck",
        _consumerTag, _queueName);
    return Task.CompletedTask;
}
```

- [ ] **Step 3: Add the host-side test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostBrokerCancelTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class RabbitMqConsumerHostBrokerCancelTests
{
    [Fact]
    public void NewHost_IsCancelledByBroker_IsFalse()
    {
        var host = BuildHost();
        Assert.False(host.IsCancelledByBroker);
    }

    [Fact]
    public async Task OnConsumerUnregistered_SetsIsCancelledByBroker()
    {
        var host = BuildHost();
        // Invoke the private handler via reflection.
        var method = typeof(RabbitMqConsumerHost).GetMethod("OnConsumerUnregisteredAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var args = new ConsumerEventArgs(["test-tag"]);
        await (Task)method!.Invoke(host, [(object?)null, args])!;
        Assert.True(host.IsCancelledByBroker);
    }

    private static RabbitMqConsumerHost BuildHost()
    {
        var connection = Mock.Of<IServiceConnectConnection>();
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());
        transport.SetupGet(t => t.GracefulShutdownTimeoutMilliseconds).Returns(1000);
        var queueConfig = Mock.Of<IQueueConfiguration>(q => q.QueueName == "test-q");
        var busConfig = Mock.Of<IBusConfiguration>();
        var retryHandler = new MessageRetryHandler(maxRetries: 3, errorExchange: "error", NullLogger.Instance);
        var auditPublisher = new MessageAuditPublisher(queueConfig);
        return new RabbitMqConsumerHost(connection, transport.Object, queueConfig, busConfig, retryHandler, auditPublisher, NullLogger.Instance);
    }
}
```

`RabbitMqConsumerHost` is `internal`; the unit-test project already has `InternalsVisibleTo`. Verify:

```bash
grep -n "InternalsVisibleTo" src/ServiceConnect.Client.RabbitMQ/
```

- [ ] **Step 4: Run the test pre-fix (well, post-flag-and-handler-but-pre-Bus-wiring)**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMqConsumerHostBrokerCancelTests" -m:1
```

Expected: 2/2 pass.

- [ ] **Step 5: Aggregate the flag in `Consumer.cs`**

Edit `src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs`. Find the `IConsumer.IsConnected` property and add `IsCancelledByBroker`:

```csharp
public bool IsConnected => /* existing implementation */;

/// <inheritdoc />
public bool IsCancelledByBroker => _clients.Any(c => c.IsCancelledByBroker);
```

- [ ] **Step 6: Wire `IsCancelledByBroker` into `Bus.IsConsuming`**

Edit `src/ServiceConnect/Bus.cs` line 86:

```csharp
// before
public bool IsConsuming => _consuming;

// after
public bool IsConsuming => _consuming && !(_consumer?.IsCancelledByBroker ?? false);
```

The semantics: `IsConsuming` is `true` only when (a) we have started consuming AND (b) the broker hasn't cancelled us. The existing `BusConsumingHealthCheck` already maps `IsConsuming = false` to `Unhealthy`, so no health-check code change is needed.

- [ ] **Step 7: Add the health-check test**

Edit (or create) `src/ServiceConnect.UnitTests/HealthChecks/BusConsumingHealthCheckTests.cs`. The existing tests likely cover the happy path; add:

```csharp
[Fact]
public async Task CheckHealthAsync_BusReportsConsumingFalse_DueToBrokerCancel_ReturnsUnhealthy()
{
    var bus = new Mock<IBus>();
    bus.SetupGet(b => b.IsConsuming).Returns(false);

    var check = new BusConsumingHealthCheck(bus.Object);
    var result = await check.CheckHealthAsync(new HealthCheckContext { Registration = new HealthCheckRegistration("test", check, null, null) });

    Assert.Equal(HealthStatus.Unhealthy, result.Status);
    Assert.Contains("not consuming", result.Description, StringComparison.OrdinalIgnoreCase);
}
```

(Note: the health check itself doesn't know about broker-cancel; it just observes `IsConsuming`. The test confirms the `false` path. The Bus-side test below confirms broker-cancel maps `IsConsuming` to `false`.)

Add a Bus-side test in `src/ServiceConnect.UnitTests/Bus/BusIsConsumingTests.cs` (new):

```csharp
[Fact]
public void IsConsuming_BrokerCancel_ReturnsFalse()
{
    // Construct a Bus with a Mock<IConsumer> that reports IsCancelledByBroker = true.
    // Start consuming so _consuming = true. Verify IsConsuming returns false.
    var consumer = new Mock<IConsumer>();
    consumer.SetupGet(c => c.IsConnected).Returns(true);
    consumer.SetupGet(c => c.IsCancelledByBroker).Returns(true);

    // [Build the Bus using whatever harness existing Bus tests use; set _consuming = true via
    //  reflection or by triggering StartConsumingAsync; assert bus.IsConsuming == false.]
}
```

If the existing Bus harness is unwieldy, alternatively reflect-write `_consuming = true` and `_consumer = consumer.Object` on a freshly constructed Bus, then assert the property.

- [ ] **Step 8: Run all relevant tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Consumer|FullyQualifiedName~Bus|FullyQualifiedName~HealthCheck" -m:1
```

Expected: all pass.

- [ ] **Step 9: Verify Astro doc compatibility (no doc edits yet — those land in Task 16)**

```bash
npm --prefix website run build
```

Expected: clean (no source changes affect doc compilation directly, but a sanity-check build catches reference-page mismatches early).

- [ ] **Step 10: Commit**

```bash
git add src/ServiceConnect.Interfaces/Bus/IConsumer.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs \
        src/ServiceConnect/Bus.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostBrokerCancelTests.cs \
        src/ServiceConnect.UnitTests/HealthChecks/BusConsumingHealthCheckTests.cs \
        src/ServiceConnect.UnitTests/Bus/BusIsConsumingTests.cs
git commit -m "feat(consumer-host): expose IsCancelledByBroker; report unhealthy on broker basic.cancel"
```

(Adjust the file list to match what was actually changed/added.)

---

## Task 15: E2E coverage — broker cancel + unroutable retry publish

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/RabbitMq/ConsumerBrokerCancelE2ETests.cs`
- Create: `src/ServiceConnect.EndToEndTests/RabbitMq/UnroutableRetryPublishE2ETests.cs`

Lands in one commit because both tests use the same `MessagingFixture` Testcontainers shape.

- [ ] **Step 1: Locate the existing E2E patterns**

```bash
ls src/ServiceConnect.EndToEndTests/RabbitMq/
cat src/ServiceConnect.EndToEndTests/RabbitMq/RetryTopologyAutoDeleteE2ETests.cs   # Phase 4's Testcontainers AMQP-direct test
cat src/ServiceConnect.EndToEndTests/RabbitMq/ConsumerRestartE2ETests.cs           # Bus-mediated pattern
```

Mirror the appropriate pattern: bus-mediated (using `AddServiceConnect`) for the broker-cancel test (we need `BusConsumingHealthCheck` in the DI graph), AMQP-direct for the unroutable-retry test (we need to delete the retry queue out-of-band).

- [ ] **Step 2: Write the broker-cancel E2E test**

Create `src/ServiceConnect.EndToEndTests/RabbitMq/ConsumerBrokerCancelE2ETests.cs`. Skeleton:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests.RabbitMq;

[Collection(nameof(MessagingCollection))]
public sealed class ConsumerBrokerCancelE2ETests
{
    private readonly MessagingFixture _fixture;

    public ConsumerBrokerCancelE2ETests(MessagingFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task DeleteQueueWhileConsuming_BusConsumingHealthCheckReportsUnhealthyWithin5s()
    {
        var queueName = _fixture.GetUniqueQueueName("broker-cancel");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>([]);
        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 1);
                t.SetClientSetting("RetrySeconds", 1);
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });
        await using var sp = services.BuildServiceProvider();
        var bus = sp.GetRequiredService<IBus>();

        // Start consuming.
        await bus.StartConsumingAsync();

        // Health check should be Healthy initially.
        var healthCheck = new BusConsumingHealthCheck(bus);
        var initial = await healthCheck.CheckHealthAsync(new HealthCheckContext { Registration = new HealthCheckRegistration("test", healthCheck, null, null) });
        Assert.Equal(HealthStatus.Healthy, initial.Status);

        // Delete the queue out-of-band.
        var factory = new ConnectionFactory
        {
            HostName = _fixture.RabbitMqHostname,
            Port = _fixture.RabbitMqPort,
            UserName = _fixture.RabbitMqUsername,
            Password = _fixture.RabbitMqPassword,
        };
        await using var sideConn = await factory.CreateConnectionAsync();
        await using var sideChannel = await sideConn.CreateChannelAsync();
        await sideChannel.QueueDeleteAsync(queueName, ifUnused: false, ifEmpty: false);

        // Poll the health check for up to 5s; expect Unhealthy.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        HealthStatus status = HealthStatus.Healthy;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var probe = await healthCheck.CheckHealthAsync(new HealthCheckContext { Registration = new HealthCheckRegistration("test", healthCheck, null, null) });
            status = probe.Status;
            if (status == HealthStatus.Unhealthy) break;
            await Task.Delay(100);
        }

        Assert.Equal(HealthStatus.Unhealthy, status);
    }
}
```

Adjust the Bus-construction harness to whatever existing E2E tests do (the `AddServiceConnect` builder shape should already be familiar from `ConsumerRestartE2ETests`).

- [ ] **Step 3: Write the unroutable-retry-publish E2E test**

Create `src/ServiceConnect.EndToEndTests/RabbitMq/UnroutableRetryPublishE2ETests.cs`. Approach:

```csharp
[Fact]
[Trait("Category", "Docker")]
public async Task RetryQueueGoneAtPublishTime_PublishExceptionLogged_OriginalAcked_NoSilentDrop()
{
    // 1. Set up a Bus with a handler that always throws.
    // 2. Start consuming.
    // 3. Out of band: delete the retry queue.
    // 4. Send a message to the consumer queue.
    // 5. Wait briefly for the handler to fail and the retry-publish to be attempted.
    // 6. Assert: a PublishException-related Error log was emitted (capture via a Moq<ILogger>
    //    or XunitLoggerProvider). Assert: the original message is acked (not redelivered)
    //    by checking via a side-channel passive QueueDeclare that the queue is empty.

    // Adapt to the project's existing E2E logging-capture pattern.
}
```

The exact log-capture mechanism depends on the project's existing E2E patterns. If the E2E pattern doesn't include log capture, the alternative test asserts: after the handler fails, no further retries happen (the retry queue is gone) AND the original message doesn't redeliver to the consumer queue (verify via a counter on the handler).

- [ ] **Step 4: Run both tests**

```bash
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "FullyQualifiedName~ConsumerBrokerCancelE2ETests|FullyQualifiedName~UnroutableRetryPublishE2ETests" -m:1
```

Expected: 2/2 pass post-fix (Tasks 10 + 14 already shipped).

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/RabbitMq/ConsumerBrokerCancelE2ETests.cs \
        src/ServiceConnect.EndToEndTests/RabbitMq/UnroutableRetryPublishE2ETests.cs
git commit -m "test(consumer): E2E coverage for broker cancel + unroutable retry publish"
```

---

## Task 16: Documentation sweep

**Files (to be located in Step 1):**
- `website/src/content/docs/learn/operations/observability.mdx` (or whichever page documents consumer health) — broker-cancel signal.
- `website/src/content/docs/reference/healthchecks/` — `BusConsumingHealthCheck` reference: add broker-cancel failure mode.
- `website/src/content/docs/learn/operations/error-handling.mdx` — clarify retry-publish behaviour when topology is missing.
- `website/src/content/docs/releases.mdx` — Phase 5 release notes.

- [ ] **Step 1: Locate the relevant doc files**

```bash
ls website/src/content/docs/learn/operations/
ls website/src/content/docs/reference/healthchecks/
grep -rln "BusConsumingHealthCheck\|consumer health\|broker.*cancel\|consumer recovery" website/src/content/docs/ | head
```

- [ ] **Step 2: Update the observability / consumer-health page**

Find the most natural location (likely `learn/operations/observability.mdx` or a sibling page that already covers consumer health) and add a section on broker-cancel:

```mdx
### Broker-initiated cancellation

When the broker cancels a ServiceConnect consumer (e.g. because the queue was deleted, a queue
policy expired, or a mirror was promoted), the consumer's deliveries stop. ServiceConnect detects
this via AMQP's `basic.cancel` event and propagates the signal through `IBus.IsConsuming`:

- `IBus.IsConsuming` returns `false`.
- `BusConsumingHealthCheck` reports `Unhealthy`.

Operator action: investigate the broker-side cause, fix it (re-create the queue with the right
arguments, restore the policy, etc.), then restart the host. ServiceConnect does **not**
auto-redeclare the queue — that would defeat an operator's deliberate deletion.
```

- [ ] **Step 3: Update the `BusConsumingHealthCheck` reference**

Find the reference page (likely under `reference/healthchecks/`). Add the `Consumer cancelled by broker` failure mode to the documented set of unhealthy reasons:

```mdx
The check reports `Unhealthy` when `IBus.IsConsuming` is `false`. This occurs:
- Before `StartConsumingAsync` has been called (or after `StopConsumingAsync`).
- After a broker-initiated `basic.cancel` event (queue deleted, policy expired, etc.) — see
  [Broker-initiated cancellation](../../learn/operations/observability/#broker-initiated-cancellation).
```

- [ ] **Step 4: Update retry-topology / error-handling doc**

Find the page Phase 4 added retry-topology content to (likely `learn/operations/error-handling.mdx`). Add or update a paragraph on retry-publish failure modes:

```mdx
### Retry-publish failure modes

ServiceConnect publishes retry and error-queue messages with `mandatory: true`. If the target
queue/exchange is missing (e.g. the retry queue was deleted out-of-band), the broker returns
the message and `BasicPublishAsync` raises `PublishException`. ServiceConnect logs this at
`Error` level and acknowledges the original delivery to break the redelivery loop. **The message
is lost in this scenario** — operator action is required to restore the topology before the
next failure can be retried.

Pre-v7.x ServiceConnect used `mandatory: false`, which silently dropped these messages with
no log signal. The change in v7.x makes the failure visible.
```

- [ ] **Step 5: Add Phase 5 release notes**

Find the v7 release-notes page (likely `releases.mdx`). Add a new section:

```mdx
### RabbitMQ consumer-host hardening (Phase 5)

**Bug fixes**

- **Atomic in-flight counter (H3).** `_messagesBeingProcessed` is now consistently mutated via `Interlocked` operations; the previous mix of lock-protected `++` and lock-free `Interlocked.Decrement` could lose updates under contention.
- **Broker `basic.cancel` reports unhealthy (H24).** When the broker cancels the consumer (queue deleted, policy expired, mirror promoted), `IBus.IsConsuming` now returns `false` and `BusConsumingHealthCheck` reports `Unhealthy`. Pre-v7.x the consumer was silently dead while the health check still reported healthy.
- **Retry/error publishes use `mandatory:true` (M3).** Unroutable retry/error publishes now surface as `PublishException` (logged at Error, original message acked to break the loop). Pre-v7.x they were silently dropped.
- **Bind queues before `BasicConsume` (M9).** Per-queue bindings now run on the consumer channel BEFORE `BasicConsumeAsync` is issued, honouring RabbitMQ.Client's per-channel serialisation contract. Hosts with type-keyed routing should see fewer "concurrent channel use" warnings.
- **Synthetic exception when handler is null (M4).** A misconfigured consumer (no `ConsumerEventHandler` registered) now stamps a synthetic `InvalidOperationException` into the error-queue header instead of writing a `null` JSON value.
- **Null-aware `FullTypeName` fallback in not-handled path (M6).** A delivery with `FullTypeName = null` (key present, value null) and a valid `TypeName` now correctly falls back to `TypeName` for the dead-letter exception message.
- **String header values are size-bounded (M15).** `RabbitMqConsumerHost`'s header-size guard now bounds string headers (UTF-8 byte count) in addition to byte-array headers.
- **Ack/nack channel-state log levels (M7).** Concurrent-teardown ack/nack races now log at `Debug` (expected race) instead of `Warning` (operator-actionable). An `IsOpen` pre-check skips the call entirely when the channel is known-closed.
- **Defensive null-check on `_messageProcessor` (M8).** A null processor now logs `Warning` and nacks-with-requeue instead of throwing `NullReferenceException`.
- **Off-by-one retry-count validation (M1).** `RetryCount = maxRetries + 1` (which the framework never legitimately stamps) is now rejected as malformed.
- **Explicit-field `BasicProperties` copy (smaller).** `MessageRetryHandler` now copies `BasicProperties` field-by-field instead of using the copy-constructor; protects against malformed source-property fields throwing inside the copy.
- **Consumer-tag refresh on auto-recovery (smaller).** RabbitMQ.Client auto-recovery may re-issue `BasicConsumeAsync` with a different tag; ServiceConnect now refreshes the cached `_consumerTag` after recovery so `BasicCancelAsync` during dispose works correctly. (Behaviour depends on RabbitMQ.Client v7 API — see the host-side code comment for the limitation note.)

**Behaviour changes**

- `IConsumer` gains a new `IsCancelledByBroker` property. External implementations of `IConsumer` (custom transport adapters) MUST add this property; the default implementation should return `false`.
- `IBus.IsConsuming` now returns `false` not just before `StartConsumingAsync` / after `StopConsumingAsync`, but ALSO when the broker has cancelled the consumer. Callers using `IsConsuming` for purposes other than health-checking should be aware of this expanded semantic.
- Retry/error publishes now use `mandatory:true`. Operators who relied on the pre-v7.x silent-drop behaviour to mask missing topology will see new `PublishException` Error logs; the topology gap should be fixed.
```

- [ ] **Step 6: Build the website**

```bash
npm --prefix website run build
```

Expected: succeeds. No new broken-link warnings.

- [ ] **Step 7: Examples / READMEs verification**

```bash
grep -rn "consumer.*healthy\|broker.*cancel\|consumer recovery\|retry queue\|mandatory" examples/ README.md 2>/dev/null
```

Look for any sample asserting "consumer is healthy ⇒ messages are flowing". If found, fix in place. If not found, no edits needed.

- [ ] **Step 8: Commit**

```bash
git add website/src/content/docs/
# add examples/ or top-level README only if Step 7 turned anything up
git commit -m "docs(website): phase 05 doc sweep — consumer recovery, broker cancel, retry topology"
```

---

## Task 17: Final verification gate + code review

This task is mechanical — run the verification commands, dispatch the final reviewer, capture findings.

- [ ] **Step 1: Per-csproj builds clean**

```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1
dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj -m:1
dotnet build src/ServiceConnect.HealthChecks/ServiceConnect.HealthChecks.csproj -m:1
```

Expected: all succeed with no new warnings.

- [ ] **Step 2: Unit-test pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~Consumer|FullyQualifiedName~Retry|FullyQualifiedName~Inbound|FullyQualifiedName~Bus|FullyQualifiedName~HealthCheck" \
    -m:1
```

Expected: all pass.

- [ ] **Step 3: E2E-test pass**

```bash
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj \
    --filter "FullyQualifiedName~ConsumerBrokerCancel|FullyQualifiedName~UnroutableRetryPublish" \
    -m:1
```

Expected: 2/2 pass against Testcontainers RabbitMQ.

- [ ] **Step 4: Astro build clean**

```bash
npm --prefix website run build
```

Expected: succeeds, no new warnings.

- [ ] **Step 5: Grep verifications**

```bash
# H3: no remaining lock-protected ++
grep -n "_messagesBeingProcessed++" src/ServiceConnect.Client.RabbitMQ/
# Expected: zero hits.

# M3: no remaining mandatory:false in retry/error publishes
grep -nE "BasicPublishAsync.*\(string\.Empty.*false|BasicPublishAsync.*_errorExchange.*false" src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs
# Expected: zero hits.

# H24: IsCancelledByBroker present in three places (interface, host, consumer aggregator)
grep -rn "IsCancelledByBroker" src/ServiceConnect.Client.RabbitMQ/ src/ServiceConnect/ src/ServiceConnect.Interfaces/
# Expected: 3+ hits.
```

- [ ] **Step 6: Final code review**

Dispatch `superpowers:code-reviewer` (model: opus) over all Phase 5 commits (from `b37c0c12` — the spec commit — through HEAD). Use the briefing pattern from Phase 4's Task 12:

```
Review Phase 5 commits (b37c0c12..HEAD) against
docs/superpowers/specs/2026-04-29-phase-05-rabbitmq-consumer-host.md.

Focus on:
- H3: counter never goes negative; no lost increments.
- H24: IBus.IsConsuming correctly maps broker-cancel to false; BusConsumingHealthCheck reports Unhealthy.
- M3: PublishException catches in InboundMessageProcessor handle the unroutable case correctly.
- M4-M9, M14-M15: per-finding spot-checks.
- Smaller items: comment correctness, edge-case coverage.

Verify the M9 split (PrepareAsync / BeginConsumingAsync / StartConsumingAsync) didn't break
external callers; check tests that exercised the old StartConsumingAsync still pass.

Flag any test that locks in pre-fix behaviour, any unhandled edge case the spec did not
anticipate, and any use of `lock` / `Interlocked` / `Volatile` that mixes disciplines (the
H3 fix is specifically about avoiding this; verify other counters didn't regress).
```

Capture review findings; address Critical / Important issues in a cleanup task; Minor issues may roll forward.

- [ ] **Step 7: Cleanup commit (only if review surfaced issues)**

If Steps 5 or 6 found anything, fix in a follow-up commit:

```bash
git add <only-cleanup-files>
git commit -m "cleanup(phase-05): address final-review findings"
```

If nothing needed, skip — Phase 5 is done.

---

## Phase 5 done

All 14 findings closed. Move to writing the closing summary; the user's standard pattern is "phase complete; continue to phase N+1?".
