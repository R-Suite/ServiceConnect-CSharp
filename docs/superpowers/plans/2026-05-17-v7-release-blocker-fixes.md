# v7 release-blocker fixes — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve the one Critical and five Important findings surfaced by the pre-release multi-agent review of `v7-clean-architecture`, with TDD, per-task code review, and a final solution-wide verification gate (unit tests, e2e tests, example apps, website docs).

**Architecture:** Each fix task follows the same pattern — (1) re-verify the finding against current code to rule out a false positive, (2) write a failing test that pins the bug, (3) implement the minimum fix, (4) confirm the test passes and the surrounding test suite is unaffected, (5) update the relevant website docs if the fix changes a documented surface, (6) commit, (7) dispatch a code-reviewer subagent against that single commit. Task 6 is the final gate: a branch-wide code review plus the full verification matrix (unit tests, e2e tests, example apps, docs consistency).

**Tech Stack:** .NET 10 / C# 14, xUnit, Moq, RabbitMQ.Client v7+, OpenTelemetry, MongoDB driver 3.x, Testcontainers for e2e.

---

## Operating constraints (read this before any task)

These are non-negotiable for this repository and override any default behaviour.

- **NEVER invoke `dotnet build` / `dotnet test` / `dotnet format` / `dotnet run` from the main session.** Always dispatch a Bash subagent (`general-purpose`, `sonnet` model is fine for these mechanical runs) so the cgroup-fenced wrapper at `~/.local/bin/dotnet` handles cross-process isolation. See `CLAUDE.md` for the technical reason — main-session invocations crashed the user's desktop on 2026-05-17.
- **Per-csproj invocations only.** `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~SomeClass"` for the focused-test pattern; whole-solution `dotnet test` is forbidden.
- **Comment style:** no ticket IDs (`R1`, `T2`), no phase labels, no "fix for X" or "previously X". Describe the implementation in present tense. Acronyms (`OCE`, `TCS`, `STJ`) and AMQP error codes are fine. See `CLAUDE.md` "Comment style — implementation only".
- **LangVersion=14 is required** for `net10.0` because Moq's expression trees fail under LangVersion=13. Don't touch `Directory.Build.props`'s `LangVersion`.
- **Re-verification before fix is mandatory.** Each task's Step 1 reads the cited file and confirms the bug is still present. If the bug has been independently fixed, mark the task `[~]` with a one-line note and skip to the next.
- **Code review after each fix is mandatory.** Dispatch a single-commit code review (file:line:diff focus) before moving to the next task. Apply Critical / Important reviewer feedback before committing the next task.

---

## Task ordering

The order matches blast radius. Task 1 is the only Critical; do it first so a green test gate establishes safety. Tasks 2–5 are independent Important fixes that can theoretically run in parallel, but the plan keeps them sequential for clarity and code-review economy.

1. Consumer.DisposeAsync timeout — lifecycle semaphore + setup-channel teardown (**Critical**)
2. Telemetry middleware — OperationCanceledException must not be Error status (**Important**)
3. Telemetry registration — `TracerProviderBuilder.AddServiceConnectInstrumentation()` + correct xmldoc source name (**Important**)
4. RabbitMqHeaderValidator — broker-exception guard on terminal failure publishes (**Important**)
5. MongoDbAggregatorPersistor — `(Name, LockedBy)` compound index (**Important**)
6. Final verification — full code review, unit tests, e2e tests, example apps, docs sync

---

## File map (created or modified across the plan)

**Production code (modified):**

- `src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs` — task 1
- `src/ServiceConnect.Telemetry/TelemetrySendMiddleware.cs` — task 2
- `src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs` — task 2
- `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs` — task 3 (xmldoc only)
- `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqHeaderValidator.cs` — task 4
- `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs` — task 5

**Production code (created):**

- `src/ServiceConnect.Telemetry/TelemetryTracerExtensions.cs` — task 3

**Tests (created or modified):**

- `src/ServiceConnect.UnitTests/RabbitMqConsumerDisposeLifecycleTests.cs` — task 1 (new)
- `src/ServiceConnect.UnitTests/Telemetry/TelemetrySendMiddlewareCancellationTests.cs` — task 2 (new)
- `src/ServiceConnect.UnitTests/Telemetry/TelemetryProcessingMiddlewareCancellationTests.cs` — task 2 (new)
- `src/ServiceConnect.UnitTests/Telemetry/TelemetryTracerExtensionsTests.cs` — task 3 (new)
- `src/ServiceConnect.UnitTests/RabbitMqHeaderValidatorBrokerExceptionTests.cs` — task 4 (new)
- `src/ServiceConnect.UnitTests/MongoDbAggregatorPersistorIndexInitTests.cs` — task 5 (modified)

**Website docs (modified):**

- `website/src/content/docs/reference/telemetry/index.mdx` — task 3 (new extension + correct source-name reference)
- `website/src/content/docs/learn/operations/observability.mdx` — task 3 (replace `AddSource("ServiceConnect.Bus")` snippet with the new extension)
- `website/src/content/docs/reference/extension-points/transport/iconsumer.mdx` — task 1 (clarify Dispose/Start cycle when previous Dispose timed out)
- `website/src/content/docs/reference/process-managers/aggregator.mdx` — task 5 if index list is documented

**Example apps (modified — only if surface changes affect snippets):**

- `examples/Telemetry/src/ServiceConnect.Examples.Telemetry.Publisher/Program.cs` — task 3 (the commented `AddSource(ServiceConnectActivitySource.ActivitySourceName)` line gets a new comment showing the extension)

---

## Task 1 — Consumer.DisposeAsync timeout: lifecycle semaphore + _model teardown

**Severity:** Critical. After a wedged setup phase, `DisposeAsync` resets `_started=0` but never releases the lifecycle semaphore, so the next `StartConsumingAsync` CAS succeeds and then blocks forever on `WaitAsync`. The same timeout path also writes `_model=null` without the `ReferenceEquals` guard that the start path uses, so the wedged start can have its setup channel torn down mid-use.

**Files:**

- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs:348-410`
- Create: `src/ServiceConnect.UnitTests/RabbitMqConsumerDisposeLifecycleTests.cs`

- [ ] **Step 1: Re-verify the finding**

Read [src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs](src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs#L345-L411) lines 345–411 and confirm:

- `_lifecycleSemaphore.WaitAsync(lifecycleTimeout)` is bounded by a timeout (currently `_busConfiguration.DisposeTimeout` or 30s fallback).
- `lifecycleAcquired = false` branch logs a warning then continues to teardown.
- `Interlocked.Exchange(ref _started, 0)` runs unconditionally at the end.
- `_lifecycleSemaphore.Release()` is gated by `if (lifecycleAcquired)`.
- The `_model` close+null at lines 382–388 has no `ReferenceEquals(_model, setupChannel)` guard (compare against the start-path's finally at line 200).

If any of these no longer hold, mark the task `[~]` with a note explaining what changed and continue with the test from Step 2 to lock the invariant.

- [ ] **Step 2: Write the failing tests**

Create `src/ServiceConnect.UnitTests/RabbitMqConsumerDisposeLifecycleTests.cs`. The two cases pin the two halves of the bug. Both manipulate the consumer's lifecycle semaphore directly via reflection — the only practical way to simulate a wedged in-flight `StartConsumingAsync` without a real broker.

```csharp
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Client.RabbitMQ.Configuration;
using ServiceConnect.Client.RabbitMQ.Connection;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

public sealed class RabbitMqConsumerDisposeLifecycleTests
{
    [Fact]
    public async Task DisposeAsync_when_lifecycle_wait_times_out_does_not_reset_started_flag()
    {
        // Pin: a timeout-forced dispose must leave _started == 1 so a subsequent
        // StartConsumingAsync fails fast with InvalidOperationException rather
        // than CAS-ing to 1 and deadlocking on WaitAsync.
        var consumer = BuildConsumerWithHeldSemaphore(disposeTimeoutMs: 100);

        await consumer.DisposeAsync();

        var startedField = typeof(Consumer).GetField("_started", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(startedField);
        var startedValue = (int)startedField!.GetValue(consumer)!;
        Assert.Equal(1, startedValue);
    }

    [Fact]
    public async Task DisposeAsync_when_lifecycle_wait_times_out_does_not_null_model_owned_by_wedged_start()
    {
        // Pin: a timeout-forced dispose must not write _model = null when an
        // in-flight StartConsumingAsync still owns the setup channel; the
        // start path's finally is responsible for the channel's lifecycle.
        var consumer = BuildConsumerWithHeldSemaphore(disposeTimeoutMs: 100);

        var modelField = typeof(Consumer).GetField("_model", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(modelField);
        var sentinel = new object();
        modelField!.SetValue(consumer, sentinel);

        await consumer.DisposeAsync();

        var modelAfter = modelField.GetValue(consumer);
        Assert.Same(sentinel, modelAfter);
    }

    private static Consumer BuildConsumerWithHeldSemaphore(int disposeTimeoutMs)
    {
        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(c => c.DisposeTimeout).Returns(TimeSpan.FromMilliseconds(disposeTimeoutMs));
        busConfig.SetupGet(c => c.ConsumerCount).Returns(1);

        var transportConfig = new Mock<ITransportConfiguration>();
        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("test");
        queueConfig.SetupGet(q => q.ErrorQueueName).Returns("error");

        var connection = new Mock<IServiceConnectConnection>();
        var consumer = new Consumer(
            busConfiguration: busConfig.Object,
            transportConfiguration: transportConfig.Object,
            queueConfiguration: queueConfig.Object,
            connection: connection.Object,
            ownsConnection: false,
            logger: NullLogger<Consumer>.Instance);

        var semaphoreField = typeof(Consumer).GetField("_lifecycleSemaphore", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(semaphoreField);
        var sem = (SemaphoreSlim)semaphoreField!.GetValue(consumer)!;
        // Drain to 0 → DisposeAsync's WaitAsync will time out.
        Assert.True(sem.Wait(0));
        return consumer;
    }
}
```

Important: the `Consumer` constructor signature above is illustrative — read the actual constructor in `src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs` (around the top of the class) and match parameter names exactly. If the constructor doesn't accept an `IServiceConnectConnection` directly, use whatever the production construction path uses (likely via a factory).

- [ ] **Step 3: Run the test to verify it fails**

Dispatch a Bash subagent with this command and confirm both new tests fail (the second will fail because `_model = null` is unconditionally written; the first will fail because `_started = 0` is unconditionally written).

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 \
  --filter "FullyQualifiedName~RabbitMqConsumerDisposeLifecycleTests" --no-restore -v minimal
```

Expected: both `DisposeAsync_when_lifecycle_wait_times_out_*` tests fail. If they pass already, re-read Step 1 — the bug may have been independently fixed.

- [ ] **Step 4: Implement the fix**

Edit [src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs:380-410](src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs#L380-L410). The teardown block currently looks like this (showing the relevant region):

```csharp
        // Close and dispose the setup channel before nulling it.
        if (_model is { IsOpen: true })
        {
            try { await _model.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error closing consumer setup channel"); }
        }
        _model?.Dispose();
        _model = null;
        if (_ownsConnection && _connection != null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        // Reset the started flag so a DisposeAsync → StartConsumingAsync sequence remains valid.
        Interlocked.Exchange(ref _started, 0);

        if (lifecycleAcquired)
        {
            _lifecycleSemaphore.Release();
        }
```

Replace with:

```csharp
        // The setup channel and connection are only safe to tear down when we hold
        // the lifecycle lock. Without it, an in-flight StartConsumingAsync still owns
        // those handles inside its own finally; closing them here would surface as an
        // opaque AlreadyClosedException out of topology declares.
        if (lifecycleAcquired)
        {
            if (_model is { IsOpen: true })
            {
                try { await _model.CloseAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error closing consumer setup channel"); }
            }
            _model?.Dispose();
            _model = null;
            if (_ownsConnection && _connection != null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }

            // Reset the started flag so a DisposeAsync → StartConsumingAsync sequence remains valid.
            Interlocked.Exchange(ref _started, 0);

            _lifecycleSemaphore.Release();
        }
        // When lifecycleAcquired is false the wedged StartConsumingAsync still holds
        // the semaphore and owns _model / _connection. Leaving _started at 1 makes a
        // subsequent StartConsumingAsync fail fast with InvalidOperationException
        // ("already consuming") instead of CAS-ing to 1 and then deadlocking on
        // WaitAsync against the semaphore that no one will release. Operators must
        // resolve the wedge (typically process restart) before consumption resumes.
```

- [ ] **Step 5: Run the targeted tests to verify they pass**

Dispatch a Bash subagent:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 \
  --filter "FullyQualifiedName~RabbitMqConsumerDisposeLifecycleTests" --no-restore -v minimal
```

Expected: 2 passed.

- [ ] **Step 6: Run the surrounding consumer-lifecycle tests to confirm no regression**

Dispatch a Bash subagent:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 \
  --filter "FullyQualifiedName~Consumer|FullyQualifiedName~BusLifecycle|FullyQualifiedName~BusStartConsuming" --no-restore -v minimal
```

Expected: all green. If any pre-existing test breaks, read the failure and decide whether the test pinned the old (buggy) behaviour or genuinely regressed. Pre-existing tests that assumed `_started` always resets after `DisposeAsync` need updating to reflect the new invariant (timeout path keeps `_started` at 1).

- [ ] **Step 7: Update website docs**

Edit `website/src/content/docs/reference/extension-points/transport/iconsumer.mdx`. Search for the section on `DisposeAsync` lifecycle (likely under "Lifecycle" or "Cleanup"). Add a paragraph:

```mdx
If `DisposeAsync` times out waiting for an in-flight `StartConsumingAsync` to complete its setup, the consumer reports `IsConsuming=false` (the stopped latch is set on entry) but the started flag is intentionally left set. A subsequent `StartConsumingAsync` on the same instance throws `InvalidOperationException("Consumer is already consuming. Call DisposeAsync before starting again.")` rather than deadlocking against the lifecycle lock the wedged start still holds. Operators should resolve the wedge (typically a process restart) before consumption resumes.
```

If the file doesn't have a Lifecycle section that this fits into, skip the doc edit and note it in the commit message — the new invariant is also implicit in the public contract (`InvalidOperationException` from a second Start).

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs \
  src/ServiceConnect.UnitTests/RabbitMqConsumerDisposeLifecycleTests.cs \
  website/src/content/docs/reference/extension-points/transport/iconsumer.mdx
git commit -m "$(cat <<'EOF'
fix(consumer): preserve _started and skip channel teardown when DisposeAsync wait times out

When the lifecycle semaphore can't be acquired within the configured DisposeTimeout,
an in-flight StartConsumingAsync still owns the setup channel and the connection.
Previously DisposeAsync closed _model and reset _started regardless, so a subsequent
StartConsumingAsync CAS'd from 0 to 1 and then deadlocked on WaitAsync against the
semaphore that the wedged caller still held; concurrently the setup channel could
be torn down from under the running start path, surfacing as an opaque AlreadyClosed
exception out of topology declares.

Keep _started at 1 in the timeout path so a second Start fails fast with the existing
"already consuming" InvalidOperationException, and skip channel/connection teardown
so the wedged start's finally retains ownership.
EOF
)"
```

- [ ] **Step 9: Code review of this commit**

Dispatch a `general-purpose` subagent (sonnet model) with the following prompt template. The agent should review only this single commit's diff.

```
You are a Senior Code Reviewer reviewing a single fix commit on the
v7-clean-architecture branch of ServiceConnect — a .NET RabbitMQ-based service
bus framework.

What was implemented: a fix for a deadlock in
src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs where DisposeAsync's
lifecycle-wait-timeout path reset _started and tore down _model regardless of
whether the wedged StartConsumingAsync still owned them.

Review the single commit at HEAD against its parent. Working dir:
/home/tim/source/ServiceConnect-CSharp.

  git show HEAD --stat
  git diff HEAD~1..HEAD

Constraints:
- Do NOT run `dotnet build` or `dotnet test`. This machine has cgroup limits;
  reading code is sufficient.
- Per-file:line references only.

Check:
1. Does the fix actually prevent the deadlock — i.e., is _started left at 1
   when lifecycleAcquired=false?
2. Does the new branching correctly skip _model / _connection teardown only
   when lifecycleAcquired=false, and run the existing teardown when true?
3. Are the new unit tests pinning the actual invariants (the test asserts
   the post-condition you'd want to hold on every release of this code)?
4. Is the commit message accurate? Is the in-source comment present-tense
   and free of ticket IDs / phase labels (project convention)?
5. Any subtle race or contract violation introduced by the change? E.g. does
   the new shape break any other caller of these fields?

Output: a short report — Strengths, Issues (Critical/Important/Minor with
file:line), Verdict (Accept | Accept with fixes | Reject). Aim for <800 words.
```

If the reviewer flags Critical or Important issues, address them in a follow-up commit before proceeding to Task 2. Minor issues can be noted and deferred.

---

## Task 2 — Telemetry middleware: OperationCanceledException must not be Error status

**Severity:** Important. Both `TelemetrySendMiddleware` and `TelemetryProcessingMiddleware` catch `Exception` (including OCE) and route it through `SetError`, which sets `ActivityStatusCode.Error`. Graceful shutdown / cooperative cancellation should not emit error-status spans — every orderly host stop currently floods downstream SLO dashboards with false failures.

**Files:**

- Modify: `src/ServiceConnect.Telemetry/TelemetrySendMiddleware.cs:50-63`
- Modify: `src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs:83-109`
- Create: `src/ServiceConnect.UnitTests/Telemetry/TelemetrySendMiddlewareCancellationTests.cs`
- Create: `src/ServiceConnect.UnitTests/Telemetry/TelemetryProcessingMiddlewareCancellationTests.cs`

- [ ] **Step 1: Re-verify the finding**

Read [src/ServiceConnect.Telemetry/TelemetrySendMiddleware.cs:50-63](src/ServiceConnect.Telemetry/TelemetrySendMiddleware.cs#L50-L63) and [src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs:83-109](src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs#L83-L109). Confirm both have a generic `catch (Exception ex) { ServiceConnectActivitySource.SetError(activity, ex, _options); throw; }` with no preceding OCE catch. Read [ServiceConnectActivitySource.cs:361-388](src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L361-L388) and confirm `SetError` calls `activity.SetStatus(ActivityStatusCode.Error, ...)` unconditionally.

- [ ] **Step 2: Write the failing tests**

Create `src/ServiceConnect.UnitTests/Telemetry/TelemetrySendMiddlewareCancellationTests.cs`. Mirror the listener-fixture pattern from `TelemetrySendMiddlewareTests`:

```csharp
using System.Diagnostics;
using ServiceConnect.Interfaces;
using ServiceConnect.Telemetry;
using Xunit;

namespace ServiceConnect.UnitTests.Telemetry;

[Collection("ActivityListener")]
public sealed class TelemetrySendMiddlewareCancellationTests : IDisposable
{
    private readonly List<Activity> _activities = [];
    private readonly ActivityListener _listener;
    private readonly ServiceConnectInstrumentationOptions _options = new();
    private readonly IMessagingSystemAttributes _attrs = new RabbitMqMessagingSystemAttributes();

    public TelemetrySendMiddlewareCancellationTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == ServiceConnectActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = _activities.Add,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task ProcessAsync_when_next_throws_OperationCanceled_span_status_is_not_error()
    {
        var sut = new TelemetrySendMiddleware(_options, _attrs);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Task Next(SendContext ctx, CancellationToken ct) => throw new OperationCanceledException(ct);

        var context = new SendContext
        {
            Message = new SampleMessage(),
            MessageType = typeof(SampleMessage),
            MessageBytes = ReadOnlyMemory<byte>.Empty,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal),
            Operation = SendOperation.Publish,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.ProcessAsync(context, Next, cts.Token));

        var span = Assert.Single(_activities);
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task ProcessAsync_when_next_throws_non_cancellation_exception_span_status_is_error()
    {
        // Regression guard: the OCE-filter must not swallow the existing error-status
        // path for real exceptions. Mirrors TelemetrySendMiddlewareTests.ProcessAsync_records_exception_and_rethrows.
        var sut = new TelemetrySendMiddleware(_options, _attrs);
        Task Next(SendContext ctx, CancellationToken ct) => throw new InvalidOperationException("boom");

        var context = new SendContext
        {
            Message = new SampleMessage(),
            MessageType = typeof(SampleMessage),
            MessageBytes = ReadOnlyMemory<byte>.Empty,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal),
            Operation = SendOperation.Publish,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ProcessAsync(context, Next, CancellationToken.None));

        var span = Assert.Single(_activities);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    private sealed class SampleMessage() : Message(Guid.NewGuid());
}
```

Create `src/ServiceConnect.UnitTests/Telemetry/TelemetryProcessingMiddlewareCancellationTests.cs` with the equivalent two cases for the processing middleware. Use `TelemetryProcessingMiddlewareTests.cs` as the fixture template and replace `Next` with one that throws `OperationCanceledException` / `InvalidOperationException`.

```csharp
using System.Diagnostics;
using ServiceConnect.Interfaces;
using ServiceConnect.Telemetry;
using Xunit;

namespace ServiceConnect.UnitTests.Telemetry;

[Collection("ActivityListener")]
public sealed class TelemetryProcessingMiddlewareCancellationTests : IDisposable
{
    private readonly List<Activity> _activities = [];
    private readonly ActivityListener _listener;
    private readonly ServiceConnectInstrumentationOptions _options = new();
    private readonly IMessagingSystemAttributes _attrs = new RabbitMqMessagingSystemAttributes();

    public TelemetryProcessingMiddlewareCancellationTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == ServiceConnectActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = _activities.Add,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task ProcessAsync_when_next_throws_OperationCanceled_span_status_is_not_error()
    {
        var sut = new TelemetryProcessingMiddleware(_options, _attrs);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var envelope = new Envelope(ReadOnlyMemory<byte>.Empty, new Dictionary<string, object>());

        Task<ConsumeEventResult> Next(
            ReadOnlyMemory<byte> _, Type __, object ___,
            IDictionary<string, object> ____, Envelope _____, CancellationToken ct)
            => throw new OperationCanceledException(ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.ProcessAsync(
                ReadOnlyMemory<byte>.Empty,
                typeof(SampleMessage),
                new SampleMessage(),
                new Dictionary<string, object>(),
                envelope,
                Next,
                cts.Token));

        var span = Assert.Single(_activities);
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task ProcessAsync_when_next_throws_non_cancellation_exception_span_status_is_error()
    {
        var sut = new TelemetryProcessingMiddleware(_options, _attrs);
        var envelope = new Envelope(ReadOnlyMemory<byte>.Empty, new Dictionary<string, object>());

        Task<ConsumeEventResult> Next(
            ReadOnlyMemory<byte> _, Type __, object ___,
            IDictionary<string, object> ____, Envelope _____, CancellationToken ct)
            => throw new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ProcessAsync(
                ReadOnlyMemory<byte>.Empty,
                typeof(SampleMessage),
                new SampleMessage(),
                new Dictionary<string, object>(),
                envelope,
                Next,
                CancellationToken.None));

        var span = Assert.Single(_activities);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    private sealed class SampleMessage() : Message(Guid.NewGuid());
}
```

Note: read `TelemetryProcessingMiddlewareTests.cs` first to copy the exact `Envelope` constructor / `Next` delegate shape — those have changed shape across the refactor. Adjust if the snippet above doesn't compile against the current types.

- [ ] **Step 3: Run the tests to verify they fail**

Dispatch a Bash subagent:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 \
  --filter "FullyQualifiedName~TelemetrySendMiddlewareCancellationTests|FullyQualifiedName~TelemetryProcessingMiddlewareCancellationTests" \
  --no-restore -v minimal
```

Expected: both `*_OperationCanceled_*` tests fail (currently OCE sets status to Error). The non-OCE regression tests should pass.

- [ ] **Step 4: Implement the fix in TelemetrySendMiddleware**

Edit [src/ServiceConnect.Telemetry/TelemetrySendMiddleware.cs:50-63](src/ServiceConnect.Telemetry/TelemetrySendMiddleware.cs#L50-L63). Replace the try/catch/finally with:

```csharp
        try
        {
            await next(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cooperative cancellation is not a span error per OTel messaging
            // semconv. The activity is disposed in the finally block; do not
            // tag it with ActivityStatusCode.Error or downstream SLO dashboards
            // will record every graceful shutdown as a failed publish.
            throw;
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
```

- [ ] **Step 5: Implement the fix in TelemetryProcessingMiddleware**

Edit [src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs:83-109](src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs#L83-L109). Replace the try/catch/finally with:

```csharp
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
        catch (OperationCanceledException)
        {
            // Cooperative cancellation is not a span error per OTel messaging
            // semconv. The activity is disposed in the finally block; do not
            // tag it with ActivityStatusCode.Error or downstream SLO dashboards
            // will record every graceful shutdown as a failed consume.
            throw;
        }
        catch (Exception ex)
        {
            ServiceConnectActivitySource.SetError(activity, ex, _options);
            throw;
        }
        finally
        {
            activity?.Dispose();
            inboundFallback?.Dispose();
        }
```

- [ ] **Step 6: Run the targeted tests to verify they pass**

Dispatch a Bash subagent:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 \
  --filter "FullyQualifiedName~TelemetrySendMiddleware|FullyQualifiedName~TelemetryProcessingMiddleware" \
  --no-restore -v minimal
```

Expected: all green, including the two pre-existing `*_records_exception_and_rethrows` style tests that pin the non-OCE error path.

- [ ] **Step 7: Update website docs**

Edit `website/src/content/docs/reference/telemetry/index.mdx`. Search for any text claiming the middleware "records all exceptions as Error" or similar. If found, qualify it: "records all exceptions except `OperationCanceledException` as `ActivityStatusCode.Error`; cooperative cancellation completes the activity with status `Unset` so graceful shutdowns do not appear as failures in trace-error-rate metrics." Also update `website/src/content/docs/learn/operations/observability.mdx` if it has a similar claim.

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect.Telemetry/TelemetrySendMiddleware.cs \
  src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs \
  src/ServiceConnect.UnitTests/Telemetry/TelemetrySendMiddlewareCancellationTests.cs \
  src/ServiceConnect.UnitTests/Telemetry/TelemetryProcessingMiddlewareCancellationTests.cs \
  website/src/content/docs/reference/telemetry/index.mdx \
  website/src/content/docs/learn/operations/observability.mdx
git commit -m "$(cat <<'EOF'
fix(telemetry): cancellation does not set ActivityStatusCode.Error

Both telemetry middlewares previously caught all exceptions including
OperationCanceledException and routed them through SetError, which sets
ActivityStatusCode.Error and records an exception event. Cooperative cancellation
is not a span error per OTel messaging semantic conventions; emitting Error here
floods downstream dashboards with false failures on every graceful host stop.

Add a leading catch for OperationCanceledException that rethrows without touching
the activity status. The non-cancellation error path is preserved.
EOF
)"
```

- [ ] **Step 9: Code review of this commit**

Dispatch a Bash subagent with the same code-review prompt template from Task 1 Step 9, adjusted for this commit's focus: "fix for OperationCanceledException being incorrectly recorded as Error status in TelemetrySendMiddleware and TelemetryProcessingMiddleware." Apply Critical/Important feedback before moving on.

---

## Task 3 — Telemetry registration: `TracerProviderBuilder.AddServiceConnectInstrumentation()` + correct xmldoc

**Severity:** Important. Two related problems form one setup trap:

1. `ServiceConnectActivitySource.cs:17` xmldoc says `AddSource("ServiceConnect.Bus")` but the actual source name computed at line 19 is `assemblyName + ".Bus"` = `"ServiceConnect.Telemetry.Bus"`. Copying the doc string produces zero spans.
2. `TelemetryMeterExtensions.AddServiceConnectInstrumentation(MeterProviderBuilder)` exists but there is no `TracerProviderBuilder` equivalent. Users must hand-write `AddSource(...)` with a string they have to guess.

Adding the tracer extension solves both — the xmldoc gets to point at the extension instead of a literal name.

**Files:**

- Create: `src/ServiceConnect.Telemetry/TelemetryTracerExtensions.cs`
- Modify: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs:15-19` (xmldoc only)
- Create: `src/ServiceConnect.UnitTests/Telemetry/TelemetryTracerExtensionsTests.cs`
- Modify: `website/src/content/docs/reference/telemetry/index.mdx` (registration snippet)
- Modify: `website/src/content/docs/learn/operations/observability.mdx` (registration snippet)
- Modify: `examples/Telemetry/src/ServiceConnect.Examples.Telemetry.Publisher/Program.cs` (commented OTel snippet at line 26)

- [ ] **Step 1: Re-verify the finding**

Read [src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs:14-21](src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L14-L21) and confirm the xmldoc still references `"ServiceConnect.Bus"` while the computed name is `assemblyName + ".Bus"`. List the files under `src/ServiceConnect.Telemetry/` and confirm no `TelemetryTracerExtensions.cs` exists. Confirm `TelemetryMeterExtensions.cs` has `AddServiceConnectInstrumentation(this MeterProviderBuilder ...)`.

- [ ] **Step 2: Write the failing test**

Create `src/ServiceConnect.UnitTests/Telemetry/TelemetryTracerExtensionsTests.cs`:

```csharp
using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using ServiceConnect.Telemetry;
using Xunit;

namespace ServiceConnect.UnitTests.Telemetry;

public sealed class TelemetryTracerExtensionsTests
{
    [Fact]
    public void AddServiceConnectInstrumentation_subscribes_to_ServiceConnect_ActivitySource()
    {
        var exportedSpans = new List<Activity>();
        using var tp = Sdk.CreateTracerProviderBuilder()
            .AddServiceConnectInstrumentation()
            .AddInMemoryExporter(exportedSpans)
            .Build();

        using var src = new ActivitySource(ServiceConnectActivitySource.ActivitySourceName);
        using var span = src.StartActivity("probe");
        Assert.NotNull(span);

        // Drain the exporter.
        span!.Dispose();
        tp.ForceFlush();

        Assert.Single(exportedSpans);
        Assert.Equal(ServiceConnectActivitySource.ActivitySourceName, exportedSpans[0].Source.Name);
    }

    [Fact]
    public void AddServiceConnectInstrumentation_returns_same_builder_for_chaining()
    {
        var builder = Sdk.CreateTracerProviderBuilder();
        var returned = builder.AddServiceConnectInstrumentation();
        Assert.Same(builder, returned);
    }

    [Fact]
    public void AddServiceConnectInstrumentation_null_builder_throws()
    {
        TracerProviderBuilder? builder = null;
        Assert.Throws<ArgumentNullException>(() => builder!.AddServiceConnectInstrumentation());
    }
}
```

Verify the OpenTelemetry packages are present in `src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj`. The meter test uses `Microsoft.Extensions.Diagnostics.Testing` or `OpenTelemetry.Exporter.InMemory`; check the existing `TelemetryMeterExtensionsTests.cs` for the package reference. If `OpenTelemetry.Exporter.InMemory` is missing, add it to the unit-test csproj.

- [ ] **Step 3: Run the test to verify it fails to compile**

Dispatch a Bash subagent:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 \
  --filter "FullyQualifiedName~TelemetryTracerExtensionsTests" --no-restore -v minimal
```

Expected: compilation failure on `AddServiceConnectInstrumentation` (no such method on `TracerProviderBuilder`).

- [ ] **Step 4: Create the new extension**

Create `src/ServiceConnect.Telemetry/TelemetryTracerExtensions.cs`:

```csharp
using OpenTelemetry.Trace;

namespace ServiceConnect.Telemetry;

/// <summary>
/// Registers ServiceConnect's <see cref="System.Diagnostics.ActivitySource"/> with
/// an OpenTelemetry tracer provider so publish, send, and consume activities are
/// exported.
/// </summary>
public static class TelemetryTracerExtensions
{
    /// <summary>
    /// Subscribes the OpenTelemetry tracer provider to the activity source emitted
    /// by <see cref="ServiceConnectActivitySource"/>.
    /// </summary>
    /// <param name="builder">The tracer provider builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// Equivalent to calling
    /// <c>builder.AddSource(ServiceConnectActivitySource.ActivitySourceName)</c>;
    /// using this extension keeps the source name in one place, so a rename never
    /// silently disables a caller's telemetry.
    /// </remarks>
    public static TracerProviderBuilder AddServiceConnectInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddSource(ServiceConnectActivitySource.ActivitySourceName);
    }
}
```

- [ ] **Step 5: Fix the xmldoc in ServiceConnectActivitySource**

Edit [src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs:14-19](src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L14-L19). Replace:

```csharp
    /// <summary>
    /// Gets the activity-source name used for all publish, send, and consume spans.
    /// Register listeners via <c>AddSource("ServiceConnect.Bus")</c>.
    /// </summary>
    public static readonly string ActivitySourceName = (typeof(ServiceConnectActivitySource).Assembly.GetName().Name ?? "ServiceConnect") + ".Bus";
```

With:

```csharp
    /// <summary>
    /// Gets the activity-source name used for all publish, send, and consume spans.
    /// Prefer <see cref="TelemetryTracerExtensions.AddServiceConnectInstrumentation"/>
    /// over registering this string directly so a future rename cannot silently
    /// disable telemetry for callers that hard-coded the literal.
    /// </summary>
    public static readonly string ActivitySourceName = (typeof(ServiceConnectActivitySource).Assembly.GetName().Name ?? "ServiceConnect") + ".Bus";
```

- [ ] **Step 6: Run the targeted tests to verify they pass**

Dispatch a Bash subagent:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 \
  --filter "FullyQualifiedName~TelemetryTracerExtensionsTests|FullyQualifiedName~TelemetryMeterExtensionsTests|FullyQualifiedName~TelemetryBuilderExtensionsTests" \
  --no-restore -v minimal
```

Expected: all green.

- [ ] **Step 7: Update website docs**

Edit `website/src/content/docs/reference/telemetry/index.mdx`:

1. Find the section showing `.AddSource(ServiceConnectActivitySource.ActivitySourceName)` (around line 156) and the commented `.AddSource("ServiceConnect.Bus")` fallback (around line 159). Replace the active snippet with:

```csharp
.AddServiceConnectInstrumentation()
```

…and the comment with:

```csharp
// Equivalent: .AddSource(ServiceConnectActivitySource.ActivitySourceName)
// Do NOT hard-code the literal string — a rename of the activity source would
// silently disable your telemetry.
```

2. Search for any other AddSource references and update similarly.

3. Add a new subsection under `TelemetryBuilderExtensions` (or a new `TelemetryTracerExtensions` section) documenting the new method:

```mdx
## TelemetryTracerExtensions

### `AddServiceConnectInstrumentation`

```csharp
public static TracerProviderBuilder AddServiceConnectInstrumentation(
    this TracerProviderBuilder builder);
```

Subscribes the OpenTelemetry tracer provider to the activity source emitted by
`ServiceConnectActivitySource`. Equivalent to
`builder.AddSource(ServiceConnectActivitySource.ActivitySourceName)`, but using
the extension keeps the source name in one place — a future rename never
silently disables a caller's telemetry.

**Parameters**
- `builder` — the `TracerProviderBuilder` being configured.

**Returns.** The same `builder`, for chaining.

**Example**

```csharp
services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddServiceConnectInstrumentation()
        .AddConsoleExporter());
```
```

Edit `website/src/content/docs/learn/operations/observability.mdx`:

Find `.AddSource(ServiceConnectActivitySource.ActivitySourceName)` (around line 97) and replace with `.AddServiceConnectInstrumentation()`. Update surrounding prose to mention the extension. Find any `"ServiceConnect.Bus"` string literals in the same file and update.

- [ ] **Step 8: Update the example app**

Edit `examples/Telemetry/src/ServiceConnect.Examples.Telemetry.Publisher/Program.cs` line 26 region:

```csharp
//     .WithTracing(t => t.AddSource(ServiceConnectActivitySource.ActivitySourceName).AddConsoleExporter());
```

Replace with:

```csharp
//     .WithTracing(t => t.AddServiceConnectInstrumentation().AddConsoleExporter());
```

Search the rest of the `examples/Telemetry/` tree for similar `AddSource` references in commented snippets and update consistently.

- [ ] **Step 9: Commit**

```bash
git add src/ServiceConnect.Telemetry/TelemetryTracerExtensions.cs \
  src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs \
  src/ServiceConnect.UnitTests/Telemetry/TelemetryTracerExtensionsTests.cs \
  website/src/content/docs/reference/telemetry/index.mdx \
  website/src/content/docs/learn/operations/observability.mdx \
  examples/Telemetry/src/ServiceConnect.Examples.Telemetry.Publisher/Program.cs
git commit -m "$(cat <<'EOF'
feat(telemetry): add TracerProviderBuilder.AddServiceConnectInstrumentation()

The MeterProviderBuilder already exposes AddServiceConnectInstrumentation, but
the tracing side required hand-writing AddSource(...) with a literal string. The
ActivitySourceName xmldoc compounded the trap by suggesting "ServiceConnect.Bus"
while the actual computed name is the assembly-qualified "ServiceConnect.Telemetry.Bus".

Add the parallel tracer extension and rewrite the xmldoc to direct users to the
extension rather than a literal. Docs and the Telemetry example are updated to
demonstrate the discoverable surface.
EOF
)"
```

- [ ] **Step 10: Code review of this commit**

Dispatch a Bash subagent with the same code-review prompt template, framed for this commit's focus.

---

## Task 4 — RabbitMqHeaderValidator: broker-exception guard on terminal failure publishes

**Severity:** Important. Each rule's `await _retryHandler.HandleTerminalFailureAsync(...)` ultimately calls `channel.BasicPublishAsync`. If the publish channel is closed (`AlreadyClosedException`, `OperationInterruptedException`, `BrokerUnreachableException`), the exception escapes `ValidateAsync` and the broader catch in `RabbitMqConsumerHost.EventAsync` nacks the original delivery with requeue. The broker redelivers the same permanently-invalid message and the cycle repeats while the publish channel remains unhealthy.

The validator's contract is "rejected messages should be acked"; treat publish-channel failure as a transient that should still result in a `Reject` so the caller acks.

**Files:**

- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqHeaderValidator.cs` (six `HandleTerminalFailureAsync` call sites)
- Create: `src/ServiceConnect.UnitTests/RabbitMqHeaderValidatorBrokerExceptionTests.cs`

- [ ] **Step 1: Re-verify the finding**

Read [src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqHeaderValidator.cs:55-167](src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqHeaderValidator.cs#L55-L167) and confirm the six `await _retryHandler.HandleTerminalFailureAsync(...)` calls (rules 1, 2, 3, 4, 5-aggregate-first, 5-aggregate-second). Read [src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs:102-110](src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs#L102-L110) and confirm `HandleTerminalFailureAsync` simply delegates to `PublishErrorAsync` with no exception classification of its own (the comment-noted classification lives in `HandleTerminalFailureDirectAsync` at a different call site). Find the broader catch in `RabbitMqConsumerHost` (search for `EventAsync` and the generic `catch (Exception` around line 366) and confirm it issues a nack-with-requeue.

- [ ] **Step 2: Write the failing test**

Create `src/ServiceConnect.UnitTests/RabbitMqHeaderValidatorBrokerExceptionTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using ServiceConnect.Client.RabbitMQ.Consumer;
using Xunit;

namespace ServiceConnect.UnitTests;

public sealed class RabbitMqHeaderValidatorBrokerExceptionTests
{
    [Fact]
    public async Task ValidateAsync_when_publish_channel_is_closed_returns_Reject_instead_of_propagating()
    {
        // Pin: a closed publish channel during a terminal-failure path must not
        // escape the validator. The caller (RabbitMqConsumerHost.EventAsync) treats
        // every escaping exception as a nack-with-requeue, which redelivers the
        // permanently-invalid message indefinitely while the broker is unhealthy.
        var retryHandler = new Mock<IMessageRetryHandler>(MockBehavior.Strict);
        retryHandler
            .Setup(h => h.HandleTerminalFailureAsync(
                It.IsAny<IChannel>(),
                It.IsAny<BasicDeliverEventArgs>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<Exception>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AlreadyClosedException(new ShutdownEventArgs(
                ShutdownInitiator.Peer, 0, "channel closed by peer")));

        var validator = new RabbitMqHeaderValidator(
            retryHandler.Object,
            maxInboundMessageSize: 1024,
            maxHeaderCount: 64,
            maxHeaderValueBytes: 8192,
            shutdownPublishTokenFactory: () => CancellationToken.None,
            logger: NullLogger<RabbitMqHeaderValidator>.Instance);

        // Trigger rule 2 (missing type name) — concrete payload is irrelevant to the
        // race we are asserting on.
        var basicProps = new BasicProperties { Headers = new Dictionary<string, object>() };
        var args = new BasicDeliverEventArgs(
            consumerTag: "ct",
            deliveryTag: 1,
            redelivered: false,
            exchange: string.Empty,
            routingKey: "q",
            properties: basicProps,
            body: new ReadOnlyMemory<byte>([1]));

        var publishChannelMock = new Mock<IChannel>();
        var result = await validator.ValidateAsync(
            publishChannelMock.Object,
            args,
            new Dictionary<string, object>());

        Assert.False(result.Accepted);
        Assert.Contains("missing type", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_when_OCE_during_terminal_failure_propagates()
    {
        // Counter-pin: OCE remains a propagation case so the caller can distinguish
        // cooperative shutdown from broker-error swallowing.
        var retryHandler = new Mock<IMessageRetryHandler>(MockBehavior.Strict);
        retryHandler
            .Setup(h => h.HandleTerminalFailureAsync(
                It.IsAny<IChannel>(),
                It.IsAny<BasicDeliverEventArgs>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<Exception>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var validator = new RabbitMqHeaderValidator(
            retryHandler.Object,
            maxInboundMessageSize: 1024,
            maxHeaderCount: 64,
            maxHeaderValueBytes: 8192,
            shutdownPublishTokenFactory: () => CancellationToken.None,
            logger: NullLogger<RabbitMqHeaderValidator>.Instance);

        var basicProps = new BasicProperties { Headers = new Dictionary<string, object>() };
        var args = new BasicDeliverEventArgs(
            consumerTag: "ct",
            deliveryTag: 1,
            redelivered: false,
            exchange: string.Empty,
            routingKey: "q",
            properties: basicProps,
            body: new ReadOnlyMemory<byte>([1]));

        var publishChannelMock = new Mock<IChannel>();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            validator.ValidateAsync(publishChannelMock.Object, args, new Dictionary<string, object>()));
    }
}
```

Note: the test assumes `MessageRetryHandler` has an interface (`IMessageRetryHandler`) or is mockable through inheritance. Check the actual class shape — if `MessageRetryHandler` is a sealed concrete class without a virtualised `HandleTerminalFailureAsync`, the fix has to either introduce an interface or take a delegate. If introducing an interface, do it in a small preliminary refactor commit; if a delegate, parameterise the validator's constructor with `Func<IChannel, BasicDeliverEventArgs, Dictionary<string, object>, Exception, CancellationToken, Task>` for the terminal-failure publisher.

- [ ] **Step 3: Run the test to verify it fails (or fails to compile)**

Dispatch a Bash subagent:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 \
  --filter "FullyQualifiedName~RabbitMqHeaderValidatorBrokerExceptionTests" --no-restore -v minimal
```

Expected: failure (broker exception propagates instead of returning `Reject`).

- [ ] **Step 4: Implement the fix**

Edit `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqHeaderValidator.cs`. The cleanest approach is a private helper that classifies broker exceptions and swallows-with-log while leaving OCE to propagate:

```csharp
private async Task<HeaderValidationResult> SafePublishTerminalAsync(
    IChannel publishChannel,
    BasicDeliverEventArgs args,
    Dictionary<string, object> copiedHeaders,
    Exception failure,
    string rejectReason,
    CancellationToken cancellationToken)
{
    try
    {
        await _retryHandler.HandleTerminalFailureAsync(
            publishChannel, args, copiedHeaders, failure, cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex) when (
        ex is AlreadyClosedException or OperationInterruptedException or BrokerUnreachableException)
    {
        // Publish channel is unhealthy. Swallow so the caller still receives a Reject
        // and acks the original delivery: the message is permanently invalid (it failed
        // header validation), so requeuing for redelivery while the broker is degraded
        // would loop the same message indefinitely. The error queue publish is the
        // best-effort observability path; a closed publish channel means the
        // message is dropped but the ack still removes it from the inbound queue,
        // which is the correct shape per the IQueueConfiguration contract.
        _logger.LogWarning(ex,
            "RabbitMqHeaderValidator could not publish terminal failure to error exchange ({Reason}); dropping the inbound message after ack.",
            rejectReason);
    }
    return HeaderValidationResult.Reject(rejectReason);
}
```

Then replace each of the six `HandleTerminalFailureAsync` call sites with a call to the helper. For example, rule 1:

```csharp
        if (args.Body.Length > _maxInboundMessageSize)
        {
            return await SafePublishTerminalAsync(
                publishChannel, args, copiedHeaders,
                new InvalidOperationException(
                    $"Inbound message size {args.Body.Length} bytes exceeds configured limit {_maxInboundMessageSize} bytes."),
                "oversized body",
                _shutdownPublishTokenFactory()).ConfigureAwait(false);
        }
```

Do the same for rules 2, 3, 4, and both rule-5 sites. The exception classes (`AlreadyClosedException`, `OperationInterruptedException`, `BrokerUnreachableException`) are from `RabbitMQ.Client.Exceptions` — add a `using` if not already present.

If `_logger` doesn't exist on the validator, add a constructor parameter `ILogger<RabbitMqHeaderValidator> logger` and wire it through the caller (the `RabbitMqConsumerHost` constructor that creates the validator).

- [ ] **Step 5: Run the targeted tests to verify they pass**

Dispatch a Bash subagent:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 \
  --filter "FullyQualifiedName~RabbitMqHeaderValidator" --no-restore -v minimal
```

Expected: all green.

- [ ] **Step 6: Run the broader consumer tests for regressions**

Dispatch a Bash subagent:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 \
  --filter "FullyQualifiedName~RabbitMqConsumer|FullyQualifiedName~MessageRetry" --no-restore -v minimal
```

Expected: all green.

- [ ] **Step 7: Update website docs**

Edit `website/src/content/docs/reference/extension-points/transport/iconsumer.mdx` (or the closest doc page that describes header validation). Find any text claiming "validator throws on publish failure" or "publish-channel failure surfaces as nack-with-requeue" and update to reflect the new contract: "header-validation rejects always result in an ack of the inbound delivery; if the error-exchange publish itself fails, the failure is logged and the inbound delivery is still acked, mirroring the `DisableErrors=true` drop-on-publish-fail contract."

If no such language exists, skip; the new behaviour is observable only as an absence of looping nacks.

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqHeaderValidator.cs \
  src/ServiceConnect.UnitTests/RabbitMqHeaderValidatorBrokerExceptionTests.cs \
  website/src/content/docs/reference/extension-points/transport/iconsumer.mdx
git commit -m "$(cat <<'EOF'
fix(rabbitmq): validator does not nack-loop permanently-invalid messages when publish channel is unhealthy

RabbitMqHeaderValidator's six terminal-failure call sites previously let broker
exceptions (AlreadyClosedException, OperationInterruptedException,
BrokerUnreachableException) escape ValidateAsync. The broader catch in
RabbitMqConsumerHost.EventAsync caught them as generic Exception and issued a
nack-with-requeue, so the same permanently-invalid message kept redelivering for
as long as the publish channel was unhealthy.

Wrap the publish in a helper that classifies broker exceptions, swallows them
with a warning log, and still returns a Reject result. The caller acks the
delivery; the message is dropped (the same shape DisableErrors=true already
produces) instead of looping. OperationCanceledException remains a propagation
case so cooperative shutdown is distinguishable from broker swallowing.
EOF
)"
```

- [ ] **Step 9: Code review of this commit**

Same template as Task 1 Step 9, focused on this commit. Pay attention to: (a) is the exception classification complete — are there other broker-error types we should include? (b) is the log level (Warning) appropriate? (c) does the new helper preserve OCE rethrow correctly?

---

## Task 5 — MongoDbAggregatorPersistor: `(Name, LockedBy)` compound index

**Severity:** Important (performance). The release filter at line 584 and the read-back filter at line 536 both filter by `LockedBy` after Name. The existing indexes are: Name, (Name, InsertedAtTicks, InsertSequence), (Name, DataBson.CorrelationId), and (Name, IdempotencyKey, partial). None covers LockedBy. At high cardinality per aggregator (thousands of rows under one Name), release / read-back queries do a Name-narrowed but otherwise in-memory scan. The timeout store already has a `(LockedBy, Locked)` index; the aggregator should match.

**Files:**

- Modify: `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs:680-721`
- Modify: `src/ServiceConnect.UnitTests/MongoDbAggregatorPersistorIndexInitTests.cs`

- [ ] **Step 1: Re-verify the finding**

Read [src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs:682-721](src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs#L682-L721) and enumerate the indexes. Confirm no `(Name, LockedBy)` index exists. Read the release filter at `:584` and the read-back filter at `:536` and confirm both filter on `LockedBy` after `Name`.

- [ ] **Step 2: Modify the existing index-init test to assert the new index**

Read `src/ServiceConnect.UnitTests/MongoDbAggregatorPersistorIndexInitTests.cs` to understand the pattern. Add a new test method that asserts the four-existing-plus-new-one index set is registered. If the existing tests assert "exactly four indexes are created", relax to "at least five" or update the count. Example:

```csharp
[Fact]
public async Task EnsureIndexesAsync_registers_Name_LockedBy_compound_index()
{
    // Pin: release-filter and read-back-filter queries filter by (Name, LockedBy);
    // without a covering compound index they degrade to in-memory scans at high
    // per-Name cardinality.
    var capturedModels = new List<CreateIndexModel<AggregatorDocument>>();
    var indexesMock = new Mock<IMongoIndexManager<AggregatorDocument>>();
    indexesMock
        .Setup(i => i.CreateManyAsync(
            It.IsAny<IEnumerable<CreateIndexModel<AggregatorDocument>>>(),
            It.IsAny<CancellationToken>()))
        .Callback<IEnumerable<CreateIndexModel<AggregatorDocument>>, CancellationToken>(
            (models, _) => capturedModels.AddRange(models))
        .ReturnsAsync(Array.Empty<string>());

    // ... existing fixture setup that calls EnsureIndexesAsync ...

    Assert.Contains(capturedModels, m =>
    {
        var keys = m.Keys.Render(BsonSerializer.LookupSerializer<AggregatorDocument>(), BsonSerializer.SerializerRegistry);
        return keys.Names.SequenceEqual(["Name", "LockedBy"]);
    });
}
```

The exact fixture shape depends on the existing test class. Match its construction pattern.

- [ ] **Step 3: Run the test to verify it fails**

Dispatch a Bash subagent:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 \
  --filter "FullyQualifiedName~MongoDbAggregatorPersistorIndexInitTests.EnsureIndexesAsync_registers_Name_LockedBy_compound_index" \
  --no-restore -v minimal
```

Expected: failure ("no index with Name+LockedBy keys").

- [ ] **Step 4: Add the new index**

Edit [src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs:709-721](src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs#L709-L721). Insert before the `CreateManyAsync` call:

```csharp
                // Compound index on (Name, LockedBy) supports the release filter at
                // ReleaseSnapshotAsync and the lease-bounded read-back filter at
                // GetSnapshotAsync's tail read. The single-field Name index narrows
                // by aggregator, but LockedBy filtering after the Name scan degrades
                // to an in-memory match at high per-Name cardinality; the compound
                // covers the predicate end-to-end.
                var nameLockedByIndex = new CreateIndexModel<AggregatorDocument>(
                    Builders<AggregatorDocument>.IndexKeys
                        .Ascending(x => x.Name)
                        .Ascending(x => x.LockedBy));
```

And update the `CreateManyAsync` array:

```csharp
                await _collection.Indexes.CreateManyAsync(
                    [nameIndex, nameInsertOrderIndex, nameCorrelationIndex, nameIdempotencyIndex, nameLockedByIndex],
                    cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 5: Run the targeted tests to verify they pass**

Dispatch a Bash subagent:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 \
  --filter "FullyQualifiedName~MongoDbAggregatorPersistorIndex" --no-restore -v minimal
```

Expected: all green.

- [ ] **Step 6: Update website docs**

Edit `website/src/content/docs/reference/process-managers/aggregator.mdx` if the doc enumerates the Mongo indexes. If it does, add `(Name, LockedBy)` to the list with a short description matching the in-source comment. If the doc page doesn't enumerate indexes, skip.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs \
  src/ServiceConnect.UnitTests/MongoDbAggregatorPersistorIndexInitTests.cs \
  website/src/content/docs/reference/process-managers/aggregator.mdx
git commit -m "$(cat <<'EOF'
perf(aggregator): add (Name, LockedBy) compound index to cover lease release / read-back filters

ReleaseSnapshotAsync and the lease-bounded tail read in GetSnapshotAsync both
filter by Name+LockedBy. The existing single-field Name index narrows the scan
by aggregator, but the LockedBy match after the Name scan degraded to an
in-memory comparison once per-Name cardinality exceeded a few hundred rows.

Add the compound index alongside the existing four; index creation is already
idempotent and benign-error-tolerant, so this is a zero-downtime change for
operators upgrading from v6.
EOF
)"
```

- [ ] **Step 8: Code review of this commit**

Same template; focus on whether the index choice (Ascending Name, Ascending LockedBy) is correct for both query shapes, whether a partial filter (e.g., only documents where `LockedBy != null`) would be a stronger choice, and whether the new index conflicts with any existing one.

---

## Task 6 — Final verification gate

**Goal:** confirm the cumulative branch state is release-ready: clean code review, all unit tests green, e2e tests green, every example app starts/finishes a smoke run, website docs render.

- [ ] **Step 1: Branch-wide code review**

Dispatch a single `general-purpose` (sonnet) subagent with this prompt:

```
You are a Senior Code Reviewer reviewing the final state of the
v7-clean-architecture branch before release. Five fixes have just landed; verify
they integrate cleanly with the rest of the branch and that no regression was
introduced.

Working dir: /home/tim/source/ServiceConnect-CSharp. Base SHA: 758e50bb (master).
Head SHA: $(git rev-parse HEAD).

  git log --oneline master..HEAD | head -10
  git diff --stat master..HEAD -- src/

Do NOT run `dotnet build` or `dotnet test`. Read code only.

Scope: the five most recent commits (the v7 release-blocker fixes). For each, verify:
1. Test pins the actual invariant — not a tautology.
2. Fix is the minimum change; no scope creep into surrounding code.
3. Public surface changes have docs updates in website/src/content/docs/.
4. Comment style: no ticket IDs (R1, T2, …), no phase labels, no "fix for X"
   framing (project convention, see CLAUDE.md).
5. Cross-commit interactions: do the fixes interfere with each other?

Output: Strengths, Issues (Critical/Important/Minor with file:line),
Final verdict (Release-ready | Release-ready with follow-ups | Block release).
Aim for under 1200 words.
```

If the reviewer flags Critical or Important issues, address them in follow-up commits before continuing.

- [ ] **Step 2: Run the full unit-test suite**

Dispatch a Bash subagent. Per-csproj invocation only:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --no-restore -v minimal
```

Expected: 0 failed. If any test fails, classify:
- Pre-existing flake → investigate; if genuinely flaky, file as separate issue and re-run once.
- Regression introduced by the fixes → halt and fix.

- [ ] **Step 3: Run the serialisation-compat tests**

```bash
dotnet test src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj -m:1 --no-restore -v minimal
```

Expected: 0 failed. This suite pins wire-format compatibility; failures here are blockers for any release that claims wire compat with v6.

- [ ] **Step 4: Run the e2e tests (Testcontainers — RabbitMQ + MongoDB)**

```bash
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj -m:1 --no-restore -v minimal
```

The user is in the `docker` group, so Testcontainers will spin up RabbitMQ and MongoDB directly with no `sg docker -c` wrapper needed.

Expected: 0 failed. If a single test flakes (Testcontainers sometimes races), re-run just that test. If multiple, halt and investigate. Track flakes separately from real failures.

- [ ] **Step 5: Smoke-test the example apps**

Each example app has its own solution under `examples/`. The repo provides a docker-compose for shared deps (`examples/docker-compose.yml`) and helper scripts in `examples/scripts/`. The smoke-test pattern per example:

```bash
# From examples/ root, ensure dependencies are up:
docker compose -f examples/docker-compose.yml up -d rabbitmq mongodb

# Per example (sequentially — they share the same broker/db):
for SAMPLE in PointToPoint PublishSubscribe RequestReply ContentBasedRouting CompetingConsumers PolymorphicMessages CustomFilterAndMiddleware Filters ProcessManager Aggregator ScatterGather RoutingSlip Streaming Telemetry; do
  echo "--- smoke: $SAMPLE ---"
  (cd examples/$SAMPLE && timeout 30 dotnet run --no-restore --project src/*Publisher* 2>&1 | tee /tmp/smoke-$SAMPLE.log) || echo "FAILED: $SAMPLE"
done
```

Don't run this command from the main session; dispatch a Bash subagent (this is dotnet run × N). The pattern above is illustrative — read each example's README first; some samples have a different shape (no separate publisher/subscriber processes), and some need a specific consumer process started before the publisher.

For each example, the smoke-test asserts: it builds, it connects to the broker, it publishes/consumes/completes its scenario without an unhandled exception. The publisher should produce its expected console output before timeout.

For the Telemetry example specifically, verify it now uses `AddServiceConnectInstrumentation()` (the task 3 update) without the consumer / publisher complaining about missing source registration.

If an example fails, classify:
- Genuine regression from one of the fixes → halt and address.
- Pre-existing breakage on master → file separately; don't block release on it.

- [ ] **Step 6: Verify website docs build**

The website lives at `website/`. Astro / Starlight; check `website/package.json` for the build command (typically `npm run build` or `pnpm build`).

Dispatch a Bash subagent:

```bash
cd website && npm ci && npm run build 2>&1 | tail -40
```

Expected: build succeeds. If the build fails on a broken link from one of the task-3 / task-1 doc edits, fix and recommit.

- [ ] **Step 7: Verify docs link consistency**

After the build passes, verify that the registration snippets across docs and examples are consistent:

```bash
grep -rn 'AddSource("ServiceConnect' website/ examples/ 2>/dev/null
grep -rn 'AddServiceConnectInstrumentation' website/ examples/ 2>/dev/null
```

Expected: only commented-out fallback references to `AddSource(ServiceConnectActivitySource.ActivitySourceName)` should remain. No active code or docs should hard-code the literal `"ServiceConnect.Bus"` string.

- [ ] **Step 8: Final commit if any docs / cleanup follow-ups**

If steps 6–7 surface a stray hard-coded source name or a build error, fix and commit:

```bash
git add -- <files>
git commit -m "$(cat <<'EOF'
docs: consistent AddServiceConnectInstrumentation registration across docs and examples
EOF
)"
```

- [ ] **Step 9: Produce a release-readiness summary**

Output a short summary to the user (text response, no file) listing:
- Each fix commit's short SHA + one-line subject.
- Unit / serialization-compat / e2e test results (pass count, fail count if any deferred).
- Example-app smoke-test results.
- Website docs build status.
- Final verdict: `Release-ready` or `Release-ready with follow-ups (<list>)`.

The summary is the handoff signal to the user.

---

## Notes on subagent dispatch

For every `dotnet ...` step in this plan, prefer this dispatch shape (general-purpose, sonnet, run-in-foreground unless steps run truly in parallel):

```
Agent({
  description: "Run focused unit tests",
  subagent_type: "general-purpose",
  model: "sonnet",
  prompt: |
    Run this exact command and report pass/fail counts and the first 200 lines
    of failure output (if any):

    dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 \
      --filter "FullyQualifiedName~SomeClass" --no-restore -v minimal

    Working dir: /home/tim/source/ServiceConnect-CSharp. The cgroup-fenced
    dotnet wrapper at ~/.local/bin/dotnet is already in PATH.

    Do not interpret failures, just report what happened. Under 300 words.
})
```

This keeps the main session's context clean of multi-megabyte MSBuild and xUnit output.

For code reviewers, prefer the template in [code-reviewer.md](.claude/plugins/cache/claude-plugins-official/superpowers/5.1.0/skills/requesting-code-review/code-reviewer.md) referenced by the `superpowers:requesting-code-review` skill.

---

## Self-review checklist (executor reads this before starting)

- [ ] Every test step shows the exact command to run, not a paraphrase.
- [ ] Every code-edit step shows the actual code to write, not "implement appropriately".
- [ ] Every commit step has a complete HEREDOC commit-message body, not "describe what you did".
- [ ] All file paths in this plan are repo-relative and resolve to the v7-clean-architecture branch's current state.
- [ ] No ticket IDs, phase labels, or "fix for X" framing in any code-comment template above.
- [ ] No `dotnet build` / `dotnet test` step is meant for the main session; all are dispatched to Bash subagents.
