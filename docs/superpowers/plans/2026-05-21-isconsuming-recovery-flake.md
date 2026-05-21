# `IBus.IsConsuming` Recovery-Flake Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop `IBus.IsConsuming` from returning `false` after a multi-kill chaos cycle by ignoring late-firing `OnConsumerUnregisteredAsync` events whose `ConsumerTags` no longer match the live `_consumerTag`.

**Architecture:** Single guard added to `RabbitMqConsumerHost.OnConsumerUnregisteredAsync`. The handler reads the live `_consumerTag` (already maintained by `OnConsumerTagChangedAfterRecoveryAsync`) and compares against `ConsumerEventArgs.ConsumerTags`. Stale events (whose tag has been superseded by topology-recovery's `BasicConsumeAsync`) log at Debug and do not set the cancellation flag. The handler logic is moved into an `internal` method so unit tests can invoke it directly with synthetic `ConsumerEventArgs`.

**Tech Stack:** C# 14, .NET 10, RabbitMQ.Client 7.0.0 (`ConsumerEventArgs.ConsumerTags` is `string[]`), xUnit + Moq for the existing RabbitMQ unit tests.

---

## File map

**Modified (production):**
- `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` — split the body of `OnConsumerUnregisteredAsync` into an `internal HandleConsumerUnregistered(ConsumerEventArgs args)` method that the event handler delegates to; add the live-tag gate inside that method.

**New (tests):**
- `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostStaleUnregisteredTests.cs` — 4 unit tests pinning the gate's behaviour.

**Verification only:**
- `./verify-all.sh` — re-run the chaos soak 3 times consecutively.

---

## Task 1: Add live-tag gate + 4 unit tests (TDD)

### Step 1.1: Refactor `OnConsumerUnregisteredAsync` to delegate to an internal method

**File:** `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` (around lines 436-446).

Replace the current method:

```csharp
    private Task OnConsumerUnregisteredAsync(object? sender, ConsumerEventArgs args)
    {
        // Fires on broker-initiated basic.cancel (e.g. queue deleted while consuming).
        // Set the flag *before* logging so a downstream health probe racing with the log call
        // observes Unhealthy on the same tick the operator first sees the warning.
        _channelHost.NotifyBrokerCancelled();
        _logger.LogWarning(
            "AMQP consumer '{ConsumerTag}' unregistered by broker (broker-initiated shutdown) on queue '{Queue}'; reporting unhealthy via BusConsumingHealthCheck",
            _consumerTag, _queueName);
        return Task.CompletedTask;
    }
```

with:

```csharp
    private Task OnConsumerUnregisteredAsync(object? sender, ConsumerEventArgs args)
    {
        HandleConsumerUnregistered(args);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Test seam: synchronous body of <see cref="OnConsumerUnregisteredAsync"/>.
    /// Exposed as <c>internal</c> so unit tests can drive the handler with synthetic
    /// <see cref="ConsumerEventArgs"/> without needing to invoke the consumer's
    /// async event delegate through reflection.
    /// </summary>
    /// <remarks>
    /// Stale events from prior chaos cycles can land AFTER topology recovery has
    /// re-issued <c>BasicConsumeAsync</c> with a new tag. RabbitMQ.Client's async
    /// event dispatch does not strictly order <c>UnregisteredAsync</c> against
    /// <c>RecoverySucceededAsync</c>, so a late unregistered notification for a
    /// tag we no longer own can clobber the post-recovery healthy state. The
    /// live-tag gate compares <c>args.ConsumerTags</c> against
    /// <see cref="_consumerTag"/>; an event for a tag we don't own is observational
    /// only and does not flip the channel host's cancelled flag.
    /// </remarks>
    internal void HandleConsumerUnregistered(ConsumerEventArgs args)
    {
        var liveTag = Volatile.Read(ref _consumerTag);
        if (!string.IsNullOrEmpty(liveTag)
            && args.ConsumerTags is { Length: > 0 } tags
            && !tags.Contains(liveTag, StringComparer.Ordinal))
        {
            _logger.LogDebug(
                "Ignoring stale AMQP consumer unregistered event on queue '{Queue}' for tags [{StaleTags}]; live tag is '{LiveTag}'",
                _queueName,
                string.Join(", ", tags),
                liveTag);
            return;
        }

        // Set the flag *before* logging so a downstream health probe racing with the log call
        // observes Unhealthy on the same tick the operator first sees the warning.
        _channelHost.NotifyBrokerCancelled();
        _logger.LogWarning(
            "AMQP consumer '{ConsumerTag}' unregistered by broker (broker-initiated shutdown) on queue '{Queue}'; reporting unhealthy via BusConsumingHealthCheck",
            _consumerTag, _queueName);
    }
```

`Contains` with `StringComparer.Ordinal` works on `string[]` because LINQ's `Enumerable.Contains` accepts an `IEqualityComparer<T>`. Add `using System.Linq;` if it isn't already at the top of the file (it likely is — `RabbitMqConsumerHost.cs` is a large file with many LINQ uses, but verify).

### Step 1.2: Build to confirm the refactor compiles

Run: `dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1 -nologo`
Expected: 0 errors, 0 warnings.

### Step 1.3: Run the existing producer + consumer test suites to confirm no regression

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~RabbitMqConsumerHost" -nologo`
Expected: all existing consumer-host tests pass (the refactor preserves behaviour for the cases they exercise — tag has not been reassigned, so the live-tag check is a no-op).

### Step 1.4: Write the 4 unit tests

Create `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostStaleUnregisteredTests.cs`:

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

/// <summary>
/// Pins the stale-tag gate inside <see cref="RabbitMqConsumerHost.HandleConsumerUnregistered"/>.
/// Under multi-kill chaos, RabbitMQ.Client's async event dispatch can deliver a prior
/// cycle's UnregisteredAsync AFTER the current cycle's RecoverySucceededAsync has cleared
/// the broker-cancelled flag. The gate ignores events whose tag has been superseded by
/// topology recovery's re-issued BasicConsumeAsync.
/// </summary>
public sealed class RabbitMqConsumerHostStaleUnregisteredTests
{
    [Fact]
    public async Task HandleConsumerUnregistered_with_live_tag_marks_cancelled()
    {
        var host = await BuildHostWithLiveTagAsync("live");

        host.HandleConsumerUnregistered(new ConsumerEventArgs(["live"], CancellationToken.None));

        Assert.True(host.IsCancelledByBroker);
    }

    [Fact]
    public async Task HandleConsumerUnregistered_with_stale_tag_does_not_mark_cancelled()
    {
        var host = await BuildHostWithLiveTagAsync("live");

        host.HandleConsumerUnregistered(new ConsumerEventArgs(["stale-from-prior-kill"], CancellationToken.None));

        Assert.False(host.IsCancelledByBroker);
    }

    [Fact]
    public async Task HandleConsumerUnregistered_with_multiple_tags_including_live_marks_cancelled()
    {
        var host = await BuildHostWithLiveTagAsync("live");

        host.HandleConsumerUnregistered(new ConsumerEventArgs(["stale", "live", "another-stale"], CancellationToken.None));

        Assert.True(host.IsCancelledByBroker);
    }

    [Fact]
    public async Task HandleConsumerUnregistered_with_empty_tags_marks_cancelled_fallback()
    {
        var host = await BuildHostWithLiveTagAsync("live");

        host.HandleConsumerUnregistered(new ConsumerEventArgs([], CancellationToken.None));

        Assert.True(host.IsCancelledByBroker);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static async Task<RabbitMqConsumerHost> BuildHostWithLiveTagAsync(string liveTag)
    {
        var underlyingConn = new Mock<IConnection>(MockBehavior.Loose);

        var consumerChannel = new Mock<IChannel>(MockBehavior.Loose);
        consumerChannel.SetupGet(c => c.IsOpen).Returns(true);
        consumerChannel.Setup(c => c.BasicQosAsync(
                It.IsAny<uint>(), It.IsAny<ushort>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        consumerChannel.Setup(c => c.BasicConsumeAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<IAsyncBasicConsumer>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(liveTag);
        consumerChannel.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        consumerChannel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        consumerChannel.SetupAdd(c => c.ChannelShutdownAsync += It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());
        consumerChannel.SetupRemove(c => c.ChannelShutdownAsync -= It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());

        var publishChannel = new Mock<IChannel>(MockBehavior.Loose);
        publishChannel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var serviceConn = new Mock<IServiceConnectConnection>();
        serviceConn.SetupGet(c => c.UnderlyingConnection).Returns(underlyingConn.Object);
        serviceConn.Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(consumerChannel.Object);
        serviceConn.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(publishChannel.Object);

        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.MaxRetries).Returns(3);
        transport.SetupGet(t => t.PrefetchCount).Returns((ushort)10);
        transport.SetupProperty(t => t.GracefulShutdownTimeoutMilliseconds, 5000);
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");
        queue.SetupGet(q => q.ErrorQueueName).Returns("err");
        queue.SetupGet(q => q.AuditQueueName).Returns("audit");
        queue.SetupGet(q => q.DisableErrors).Returns(false);
        queue.SetupGet(q => q.AuditingEnabled).Returns(false);

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);
        bus.SetupGet(b => b.DeadLetterUnhandledMessages).Returns(false);

        var retry = new MessageRetryHandler(3, "err", "q", NullLogger.Instance);
        var audit = new MessageAuditPublisher(queue.Object);

        var host = new RabbitMqConsumerHost(
            serviceConn.Object, transport.Object, queue.Object, bus.Object,
            retry, new RabbitMqAdmissionGate("q"), audit, NullLogger.Instance);

        await host.StartConsumingAsync(
            (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }),
            queueName: "q");

        return host;
    }
}
```

### Step 1.5: Run the new tests

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~RabbitMqConsumerHostStaleUnregisteredTests" -nologo`
Expected: 4/4 pass.

### Step 1.6: Run the full unit test suite

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 -nologo`
Expected: all tests pass (existing + 4 new = ~1702 total or similar; the existing suite was 1696 + 4 from the prior aggregator work = 1700, plus 4 new = ~1704).

### Step 1.7: Commit

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostStaleUnregisteredTests.cs
git commit -m "fix(rabbitmq): ignore stale UnregisteredAsync events whose tag has been superseded by recovery"
```

---

## Task 2: Variance-check verification via `./verify-all.sh` chaos runs

**Files:** none modified.

The race condition has natural jitter (it depends on whether RabbitMQ.Client's async event queue happens to deliver a stale `UnregisteredAsync` after the corresponding `RecoverySucceededAsync` on a given run). A single clean run is necessary but not sufficient evidence. Three consecutive clean runs are the bar.

### Step 2.1: Build the harness (single-CPU to avoid cgroup OOM)

Run: `dotnet build examples/StressHarness/src/ServiceConnect.Examples.StressHarness/ServiceConnect.Examples.StressHarness.csproj -m:1 -nologo`
Expected: 0 errors, 0 warnings.

### Step 2.2: Run chaos soak — first pass

Run (in a background task if shell timeout limits apply):
```bash
SKIP_UNIT=1 SKIP_E2E=1 SKIP_HARNESS=1 ./verify-all.sh
```

Expected: trailing line reads `Chaos harness OK: **Flows:** N / N passed`. No `RecoveryAssertion` failure in `out/report.md`.

If this run reports a `RecoveryAssertion` failure, inspect `out/report.md`'s `## Assertion failures` section and report back — the fix isn't sufficient, possibly because the race occurs in a path other than `UnregisteredAsync`.

### Step 2.3: Run chaos soak — second pass

Run:
```bash
SKIP_UNIT=1 SKIP_E2E=1 SKIP_HARNESS=1 ./verify-all.sh
```

Expected: same as 2.2.

### Step 2.4: Run chaos soak — third pass

Run:
```bash
SKIP_UNIT=1 SKIP_E2E=1 SKIP_HARNESS=1 ./verify-all.sh
```

Expected: same as 2.2.

### Step 2.5: Capture verdict (no commit)

Summarise the three runs in conversation: flow-pass counts, presence/absence of `RecoveryAssertion` failure, and the message-ledger numbers from each run. If all three are clean, the fix is verified. If any reports `RecoveryAssertion`, the underlying race is broader than the spec's hypothesis and needs additional investigation.

---

## Self-review

**1. Spec coverage:**

- "Gate `OnConsumerUnregisteredAsync` on live consumer tag" — Task 1 step 1.1 ✓
- "Stale events log at Debug and do not set flag" — Task 1 step 1.1 ✓
- "Live-tag check uses `Volatile.Read(ref _consumerTag)`" — Task 1 step 1.1 ✓
- "Empty `ConsumerTags` falls through to set flag (defensive default)" — Task 1 step 1.1 (the `args.ConsumerTags is { Length: > 0 } tags` pattern skips the gate when tags array is empty) + test in step 1.4 ✓
- "Multi-tag case: live tag in set → still cancelled" — Test 3 in step 1.4 ✓
- "Internal test seam (`HandleConsumerUnregistered`)" — Task 1 step 1.1 ✓
- "4 unit tests" — Task 1 step 1.4 ✓
- "Three consecutive clean chaos runs as the verification bar" — Task 2 ✓

**2. Placeholder scan:** No "TBD" / "TODO" entries. Step 1.1 contains a "verify `using System.Linq;` is present" instruction — the file is large enough that I can't pre-confirm; the build in step 1.2 will surface any missing using.

**3. Type consistency:**
- `HandleConsumerUnregistered(ConsumerEventArgs args)` — same signature in test invocations (Task 1.4) and production (Task 1.1). ✓
- `ConsumerEventArgs` constructor `(string[] consumerTags, CancellationToken cancellationToken)` — matches RabbitMQ.Client 7.0.0's public ctor per the package's XML docs at `/home/tim/.nuget/packages/rabbitmq.client/7.0.0/lib/net8.0/RabbitMQ.Client.xml:1327`. ✓
- `ConsumerEventArgs.ConsumerTags` is a public field returning `string[]` per the same xml docs. ✓
- `host.IsCancelledByBroker` (the property name on `RabbitMqConsumerHost`) — verify in the existing file; it's `internal bool IsCancelledByBroker => _channelHost.IsCancelledByBroker;` at line 301 of `RabbitMqConsumerHost.cs`. ✓
- `RabbitMqConsumerHost.StartConsumingAsync(handler, queueName)` — matches the existing test pattern at `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostConnectionEventCaptureTests.cs:142`. ✓
- `IServiceConnectConnection` is the framework interface for the connection wrapper — used in the existing test as `serviceConn.SetupGet(c => c.UnderlyingConnection)`. ✓
