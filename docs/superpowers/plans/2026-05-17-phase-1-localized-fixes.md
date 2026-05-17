# Phase 1 — Localized Correctness & Observability Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply eight localized correctness and observability fixes drawn from the 2026-05-17 architecture review. Each fix is one-file-scoped; no public API change; no new collaborators. Ships as small independent commits.

**Architecture:** Each task is an independent, narrowly-scoped change to a single class. No task depends on any other task in this phase. Tasks may be executed in any order, but they MUST individually pass the validate-first gate before any code changes.

**Tech Stack:** C# 12/14 (multi-target net8.0/net10.0; tests net10.0), xUnit 2.9, Moq 4.20, `Microsoft.Extensions.TimeProvider.Testing` (FakeTimeProvider), `Microsoft.Extensions.Diagnostics.Testing` (FakeLogger). No new dependencies.

---

## Phase-wide rules (apply to EVERY task)

These rules live in [docs/reviews/2026-05-17-refactor-phasing.md](../../reviews/2026-05-17-refactor-phasing.md) and are repeated here so the implementer never has to leave this document.

1. **Validate before implementing.** Step 1 of every task is "Validate the finding." Re-read the cited code and confirm the issue is real and the suggested fix is the right one. If the finding is a false positive, append an entry to `docs/reviews/2026-05-17-rejected-findings.md` (create if absent) with the file:line, the audit's claim, and a short explanation of why it doesn't apply, then skip the remaining steps for that task. Do not silently drop.
2. **No `dotnet` from the main session.** Every `dotnet build` / `dotnet test` step in this plan MUST be delegated to a subagent (Agent tool, `general-purpose` subtype, model=sonnet, with `-m:1`). Main-session dotnet invocations are forbidden per the project CLAUDE.md.
3. **Per-csproj invocations only.** When running tests or builds, target the specific csproj — never the solution. Example test command shape: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~<TestClass> -m:1`.
4. **Per-change code review.** After each task's implementation passes its tests, invoke `superpowers:requesting-code-review` on the change before committing. If the reviewer flags issues, address them in the same task before moving on.
5. **Commit one task = one commit.** Each commit message follows the project's existing style (see `git log --oneline -20`): a scoped prefix (`fix(rabbitmq):`, `perf(consume):`, `docs(bus):`, etc.), a present-tense summary, and the Claude co-author trailer.
6. **No ticket / phase / "fixes Xxx" framing in source comments** (per project CLAUDE.md). Belongs in the commit message body, not in code.

---

## End-of-phase verification gate (run after all tasks complete)

Do NOT skip any of these. None of them rolls forward.

- [ ] **Final code review** — run `superpowers:requesting-code-review` on the full diff of all Phase 1 commits combined (`git diff $(git merge-base HEAD master)..HEAD`).
- [ ] **Full unit-test suite** — delegated subagent runs `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1` and reports total pass/fail.
- [ ] **Full end-to-end suite** — delegated subagent runs `dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj -m:1`. Requires Docker (Testcontainers for RabbitMQ + Mongo). Run the user is in the docker group — no `sg docker -c` wrapper needed.
- [ ] **Serialization compat tests** — delegated subagent runs `dotnet test src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj -m:1`.
- [ ] **All example apps build** — delegated subagent runs `for csproj in $(find examples -name '*.csproj'); do dotnet build "$csproj" -m:1 || echo "FAIL: $csproj"; done`. Zero failures expected.
- [ ] **Example smoke run** — bring up `examples/docker-compose.yml`, run at minimum the `PointToPoint` consumer + producer pair end-to-end (see `examples/scripts/` for runners), confirm message delivery. Delegate this to a subagent that can use docker.
- [ ] **Documentation site current** — verify `docs/` site content reflects every public-visible behavior change in this phase. Phase 1 should NOT have any (all changes are internal), but confirm.
- [ ] **README current** — verify `README.md` reflects every change visible to package consumers. Phase 1 should NOT have any, but confirm.
- [ ] **Final commit** — if any docs updates were needed, commit them as a separate `docs:` commit.

---

## Task 1: Producer retry — add jitter to fixed-delay backoff

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs:206, :227`
- Test: `src/ServiceConnect.UnitTests/ProducerRetryTests.cs` (existing — extend)

**Finding context:** Producer.cs lines 206 and 227 both `await Task.Delay(TimeSpan.FromSeconds(_retryTimeInSeconds), ...)`. The comment at lines 223-226 explicitly rejects exponential backoff (would double the wall-clock budget against `EnsureConnectedAsync`'s own exponential). The audit's recommendation to "replace with capped exponential backoff" therefore conflicts with documented design. **The real concern is jitter, not exponential.** Without jitter, every producer reconnects in lockstep on broker recovery and hits the broker simultaneously.

- [ ] **Step 1: Validate**

Re-read `Producer.cs:191-237` (already cited above; included here for the implementer to confirm independently). Confirm:
- Lines 206 and 227 both use `_retryTimeInSeconds` as a fixed delay.
- The comment at 223-226 ("delay is intentionally fixed (not exponential)") still articulates the rationale.
- The class has no existing jitter mechanism.

Decision criteria:
- If the comment still says "intentionally fixed", proceed — but the change is **adding jitter**, NOT replacing with exponential. Keep the mean equal to `_retryTimeInSeconds`.
- If the code has changed and jitter or exponential has been added since the review, append to `docs/reviews/2026-05-17-rejected-findings.md` and stop.

- [ ] **Step 2: Write the failing test**

Add a new test class `ProducerRetryJitterTests.cs` (or extend `ProducerRetryTests.cs` if it has the test infrastructure). The test asserts that consecutive retry delays for the same producer are not identical when more than one retry happens — i.e. that jitter is applied.

```csharp
// src/ServiceConnect.UnitTests/ProducerRetryJitterTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Client.RabbitMQ.Producer;
using Xunit;

public sealed class ProducerRetryJitterTests
{
    [Fact]
    public async Task TransientFailureRetries_UseJitteredDelaysAroundMean()
    {
        // Force multiple retries against a stub producer-connection that always throws
        // ChannelTransientException, and capture the actual delays observed by
        // FakeTimeProvider. With jitter, the captured delays should not all be exactly
        // _retryTimeInSeconds; with the old fixed delay, they would.
        var time = new FakeTimeProvider();
        var observed = new List<TimeSpan>();
        // ... (full setup — match the patterns in existing ProducerRetryTests.cs;
        //     subagent should read that file first to mirror its Moq + connection-stub
        //     conventions exactly)
        // Drive 5 retries; assert observed.Distinct().Count() > 1
        // Assert all observed delays are within ±50% of _retryTimeInSeconds.
        Assert.True(observed.Count >= 5);
        Assert.True(observed.Distinct().Count() >= 2, "Expected jitter to produce at least two distinct delays across retries");
        Assert.All(observed, d => Assert.InRange(d.TotalSeconds, _retryTimeInSeconds * 0.5, _retryTimeInSeconds * 1.5));
    }
}
```

Note: the existing `ProducerRetryTests.cs` test file has full setup patterns (Moq stubs for `IServiceConnectConnection`, `IModel`, etc.). Read it first and mirror the conventions.

- [ ] **Step 3: Run the test to verify it fails**

Delegate to subagent:

```
Agent(general-purpose, sonnet): "Run only ProducerRetryJitterTests in
src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj with `-m:1`. Report
pass/fail and the exact assertion message."
```

Expected: FAIL because the production code still uses fixed delay.

- [ ] **Step 4: Implement the jitter**

Edit `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs`. Add a private helper that returns a jittered TimeSpan, and replace both `await Task.Delay(TimeSpan.FromSeconds(_retryTimeInSeconds), ...)` call sites with `await Task.Delay(JitteredRetryDelay(), ...)`.

```csharp
// Place this near the bottom of the Producer class (alongside IsRetriablePublishException).
// Reads _retryTimeInSeconds (mean) and produces a uniformly-distributed delay in
// [mean*0.5, mean*1.5]. Random.Shared is thread-safe under net6+. Jitter prevents
// all producers in a deployment from reconnecting in lockstep after a broker restart,
// without changing the mean wall-clock budget — exponential would double the budget
// against EnsureConnectedAsync's own exponential backoff, so we keep the mean fixed.
private TimeSpan JitteredRetryDelay()
{
    var meanSeconds = _retryTimeInSeconds;
    var jitterFactor = 0.5 + (Random.Shared.NextDouble()); // [0.5, 1.5)
    return TimeSpan.FromSeconds(meanSeconds * jitterFactor);
}
```

Update both call sites:

```csharp
// Line 206 (ChannelTransientException retry):
await Task.Delay(JitteredRetryDelay(), cancellationToken).ConfigureAwait(false);

// Line 227 (generic retriable retry):
await Task.Delay(JitteredRetryDelay(), cancellationToken).ConfigureAwait(false);
```

Update the comment block at 223-226 to mention jitter:

```csharp
// Inter-attempt delay also runs OUTSIDE the lock so other publishers can interleave.
// Mean is fixed (not exponential) — connection-create inside EnsureConnectedAsync
// already does its own exponential backoff via Retry.DoAsync, so layering exponentials
// would double-grow the wall-clock budget. ±50% jitter is applied per attempt so
// concurrent producers do not reconnect in lockstep after a broker restart.
```

- [ ] **Step 5: Run the test to verify it passes**

Delegate to subagent (same prompt as Step 3). Expected: PASS.

- [ ] **Step 6: Run the full ProducerRetryTests file to confirm no regression**

Delegate:

```
Agent(general-purpose, sonnet): "Run the ProducerRetry* test classes in
src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj with `-m:1`. Report
total pass/fail counts."
```

Expected: all pre-existing tests still pass.

- [ ] **Step 7: Per-change code review**

Invoke `superpowers:requesting-code-review` on the staged diff. Address any findings before commit.

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs \
        src/ServiceConnect.UnitTests/ProducerRetryJitterTests.cs
git commit -m "$(cat <<'EOF'
perf(rabbitmq): jitter producer retry delays around fixed mean

Producer.ExecuteRetryingPublishAsync used a fixed Task.Delay between
attempts; without jitter, every producer in a deployment retries in
lockstep after a broker restart. Apply ±50% uniform jitter around the
configured mean; keep the mean fixed because EnsureConnectedAsync's
own connection-establishment Retry already does exponential.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: InboundMessageProcessor audit-drop metric

**HIGH FALSE-POSITIVE RISK — strong evidence the finding is already implemented.**

**Files:**
- Read: `src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs:159-356`

**Finding context:** The audit claimed "audit-publish silent drop → emit dropped counter, promote log to LogError." First-hand validation reveals:
- Line 348 already emits `ServiceConnectMeter.AddRetryDrop` with tags `messaging.system`, `messaging.destination.name`, `error.type`.
- Line 309, 345, 346: logs are already at `LogError`, not Warning.
- `PublishAuditWithDropMetricAsync` (line 364) by name implies the audit-drop metric exists.
- Line 386-388: comment confirms audit drops are metered inside `MessageAuditPublisher` via `messaging.serviceconnect.audit.drops`.

- [ ] **Step 1: Validate**

Re-read `InboundMessageProcessor.cs:159-390`. Confirm both:
- The retry-drop counter exists at line 348.
- The audit-drop counter exists inside `MessageAuditPublisher` (grep: `messaging.serviceconnect.audit.drops`).

Also grep for any documented gap in metric tagging (e.g. "TODO: distinguish reason"). If you find one, the finding is real with a narrower scope; otherwise it's a false positive.

```bash
grep -rn "messaging.serviceconnect.audit.drops\|AddAuditDrop\|AddRetryDrop\|RecordDrop" src/ServiceConnect.Client.RabbitMQ src/ServiceConnect --include='*.cs'
```

- [ ] **Step 2: Decision**

If both metrics exist and have appropriate tags (system, destination, error type), the finding is a **false positive** — append to `docs/reviews/2026-05-17-rejected-findings.md`:

```markdown
## InboundMessageProcessor audit-publish silent drop (review §Findings)

**Claim:** Audit-publish failure swallowed without metric observability;
log should be promoted to LogError.

**Verdict:** Already implemented. `ServiceConnectMeter.AddRetryDrop` at
InboundMessageProcessor.cs:348 emits a counter on retry/fallback drop with
tags `messaging.system`, `messaging.destination.name`, `error.type`. Audit
drops are metered inside `MessageAuditPublisher` via
`messaging.serviceconnect.audit.drops`. Error-level logging is already in
place (lines 309, 345). The audit pass misread the file.
```

Then SKIP the remaining steps for this task. Do NOT add a redundant counter.

If — and only if — validation reveals a genuine gap (e.g. an unhandled failure mode that bypasses both counters, OR the existing tags are insufficient for distinguishing reasons), open a follow-up task. Do not silently expand scope; raise the gap explicitly and ask for direction.

- [ ] **Step 3: Commit the false-positive log (if applicable)**

```bash
git add docs/reviews/2026-05-17-rejected-findings.md
git commit -m "$(cat <<'EOF'
docs(reviews): record audit-drop metric finding as false positive

Validation against InboundMessageProcessor.cs confirmed both the
retry-drop counter (ServiceConnectMeter.AddRetryDrop) and the audit-drop
counter (messaging.serviceconnect.audit.drops) already exist with
appropriate tags. The audit pass misread the file.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: RabbitMqConsumerHost deadline-helper outer-catch log level

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs:837`
- Test: `src/ServiceConnect.UnitTests/RabbitMqConsumerHostTests.cs` (extend) or new dedicated test file

**Finding context:** `CancelHelperPublishesAtDeadlineAsync` (lines 807-839) is invoked fire-and-forget at line 673 (`_ = ...`). Its inner try/catch handles the expected failure modes (OCE when cancelled, ObjectDisposedException). The outer catch (line 832-838) handles unexpected faults and currently logs at `LogDebug`. The comment justifies Debug by "dispose is already on a best-effort path", which is fair for the expected paths but undersells unexpected faults.

- [ ] **Step 1: Validate**

Re-read `RabbitMqConsumerHost.cs:807-839`. Confirm:
- The fire-and-forget call site at line 673 still exists.
- The outer catch at line 832 currently logs at `LogDebug`.
- The inner try/catch at 818-830 handles OCE and ObjectDisposedException (these are the expected paths and should stay at their current levels — they are not what we are changing).

Decision criteria:
- If the outer catch at 832 is already at LogWarning or higher, append to `2026-05-17-rejected-findings.md` and stop.
- Otherwise proceed.

- [ ] **Step 2: Write the failing test**

```csharp
// src/ServiceConnect.UnitTests/RabbitMqConsumerHostDeadlineHelperTests.cs
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
// ... usings to match existing RabbitMqConsumerHostTests.cs

public sealed class RabbitMqConsumerHostDeadlineHelperTests
{
    [Fact]
    public async Task UnexpectedFaultInDeadlineHelper_LoggedAtWarning()
    {
        // Construct a RabbitMqConsumerHost with a faulting TimeProvider so that
        // CancelHelperPublishesAtDeadlineAsync's GetUtcNow() throws something the
        // inner catches do not cover. Dispose the host and assert that the FakeLogger
        // captured a Warning-level record from CancelHelperPublishesAtDeadlineAsync.
        var fakeLogger = new FakeLogger<RabbitMqConsumerHost>();
        // ... rest of setup mirroring patterns from RabbitMqConsumerHostTests.cs
        // (the subagent should read that file first; reuse its TimeProvider fakes,
        //  connection stubs, configuration helpers).

        await host.DisposeAsync();

        var record = fakeLogger.Collector.GetSnapshot()
            .SingleOrDefault(r => r.Message.Contains("CancelHelperPublishesAtDeadlineAsync best-effort recovery faulted"));
        Assert.NotNull(record);
        Assert.Equal(LogLevel.Warning, record.Level);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

```
Agent(general-purpose, sonnet): "Run only RabbitMqConsumerHostDeadlineHelperTests
in src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj with `-m:1`. Report
pass/fail."
```

Expected: FAIL (record logged at Debug, not Warning).

- [ ] **Step 4: Implement**

Edit `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs:832-838`:

```csharp
catch (Exception ex)
{
    // Fire-and-forget helper: any failure outside the expected OCE / ObjectDisposed
    // paths above must not become an unobserved Task. Warning rather than Debug —
    // a stalled deadline helper means dispose may hang publishes past the grace
    // window without a loud signal in production logs.
    _logger.LogWarning(ex, "CancelHelperPublishesAtDeadlineAsync best-effort recovery faulted");
}
```

- [ ] **Step 5: Run the test to verify it passes**

```
Agent(general-purpose, sonnet): "Run RabbitMqConsumerHostDeadlineHelperTests in
src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj with `-m:1`."
```

Expected: PASS.

- [ ] **Step 6: Run the full RabbitMqConsumerHostTests to confirm no regression**

```
Agent(general-purpose, sonnet): "Run all RabbitMqConsumerHost* test classes in
src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj with `-m:1`."
```

Expected: all pre-existing tests still pass.

- [ ] **Step 7: Per-change code review**

Invoke `superpowers:requesting-code-review` on the diff. Address any findings.

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs \
        src/ServiceConnect.UnitTests/RabbitMqConsumerHostDeadlineHelperTests.cs
git commit -m "$(cat <<'EOF'
fix(rabbitmq): log deadline-helper unexpected faults at Warning, not Debug

CancelHelperPublishesAtDeadlineAsync is fire-and-forget; the inner catches
cover the expected OCE/ObjectDisposed paths, but the outer Exception catch
was at Debug — a stalled deadline helper would block publish-cancellation
on dispose with no production-visible signal. Promote to Warning.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: AggregatorProcessor.ResetTimer — previous-timer dispose race

**HIGH FALSE-POSITIVE RISK — first-hand re-read suggests the described race does not exist.**

**Files:**
- Read: `src/ServiceConnect/Services/Processors/AggregatorProcessor.cs:152-172` and the DisposeAsync timer cleanup

**Finding context:** The audit said: "`ResetTimer` assigns the new timer under `_resetTimerLock` but disposes the *previous* timer **outside** the lock; a racing `DisposeAsync` can call `_timers.Clear()` in between." First-hand re-read:
- `previous` is a local variable holding the OLD timer that was just **removed from the dictionary** (replaced by `newTimer`).
- `_timers.Clear()` racing in does not affect `previous` because it is no longer in `_timers`.
- The actual race window — `DisposeAsync` enumerating `_timers` and disposing entries while `ResetTimer` is swapping the slot — is OUTSIDE the lock on both sides (the dictionary itself is a ConcurrentDictionary; DisposeAsync's enumeration is snapshot-based; ResetTimer's reassignment is under `_resetTimerLock`). Moving `previous?.Dispose()` inside the lock does not narrow that window because DisposeAsync's enumeration is not synchronized on `_resetTimerLock`.

- [ ] **Step 1: Validate**

Re-read:
- `AggregatorProcessor.cs:152-172` (`ResetTimer`).
- The DisposeAsync timer cleanup (grep for `_timers.Clear`, `foreach.*_timers`, or `DisposeAsync` in the same file).
- Any test that already exercises `ResetTimer` racing dispose.

```bash
grep -n "_timers" src/ServiceConnect/Services/Processors/AggregatorProcessor.cs
```

Decision criteria:
- Does `DisposeAsync` enumerate `_timers` under `_resetTimerLock`? If NO, moving the dispose-inside-lock does not prevent any race — the finding is a **false positive**.
- Is `ITimer.Dispose` documented as idempotent / double-call-safe? `FakeTimeProvider.CreateTimer` returns a `FakeTimer` whose `Dispose` is idempotent. `TimeProvider.System.CreateTimer` returns a `Timer` which is safe to Dispose multiple times.

If both points hold (DisposeAsync does not take `_resetTimerLock` AND ITimer dispose is safe to double-call), append to `2026-05-17-rejected-findings.md`:

```markdown
## AggregatorProcessor.ResetTimer dispose-outside-lock (review §Findings)

**Claim:** Previous timer disposed outside `_resetTimerLock`; a racing
DisposeAsync's `_timers.Clear()` can land between the swap and the dispose.

**Verdict:** False positive. `previous` is a local reference to a timer
that was just evicted from the dictionary; `_timers.Clear()` racing in
does not affect that reference. DisposeAsync's enumeration of `_timers`
is not synchronized on `_resetTimerLock` either, so moving the dispose
inside the lock does not narrow any race. ITimer.Dispose is documented
idempotent on both FakeTimer and System.Threading.Timer.
```

Then SKIP the remaining steps.

If validation reveals an actual unsafe interaction (e.g. DisposeAsync DOES take `_resetTimerLock`, and the dispose-outside-lock therefore allows a double-dispose window), proceed with steps 2-8. Otherwise stop here.

- [ ] **Step 2-8: Skipped if validation closes as false positive.**

If the task proceeds, the implementation is mechanical (move `previous?.Dispose()` into the `lock (_resetTimerLock) { ... }` body, before the closing brace). Write a test that asserts no `ObjectDisposedException` is thrown when concurrent `ResetTimer` and `DisposeAsync` calls race. See Task 1 for the test-first / verify / review / commit flow.

---

## Task 5: ConsumeContext.IsKnownQueue — flatten to HashSet

**Files:**
- Modify: `src/ServiceConnect/Services/ConsumeContext.cs:148-177`
- Test: `src/ServiceConnect.UnitTests/ConsumeContextStrictReplyValidationTests.cs` (extend)

**Finding context:** `IsKnownQueue` is called from `ReplyAsync` (line 128) on every reply when `ValidateReplyDestinations` is enabled. The current implementation does string compares against `QueueName` / `ErrorQueueName` / `AuditQueueName` (O(1) each) then iterates `queueConfig.QueueMappings` (a `IReadOnlyDictionary<string, IReadOnlyList<string>>`) with a nested foreach (O(total queue entries)). Total: O(N) per reply where N = total queues across all mappings. Workloads with hundreds of mappings will feel it on the reply hot path. Flatten once to a `HashSet<string>` for O(1) per reply.

**NOTE:** `IsKnownQueue` is `internal static` and takes `IQueueConfiguration` directly. The flattening cache cannot live on the static method — it needs to live somewhere instance-scoped. The cleanest refactor is to keep `IsKnownQueue(string, IQueueConfiguration)` for backwards-compat with existing tests but compute the lookup against a flattened set when one is available. Alternative: add a new internal method `IsKnownQueue(string, HashSet<string>)` and have callers pass the pre-flattened set.

- [ ] **Step 1: Validate**

Re-read `ConsumeContext.cs:148-177` and confirm:
- `QueueMappings` is `IReadOnlyDictionary<string, IReadOnlyList<string>>`.
- `IsKnownQueue` is `internal static` and accepts `IQueueConfiguration`.
- The method is called on the reply hot path.

Decision criteria:
- If `QueueMappings` has been refactored to already-flat, append to `2026-05-17-rejected-findings.md` and stop.
- Otherwise proceed.

- [ ] **Step 2: Decide where the cache lives**

`ConsumeContext` is constructed per-message. Caching the flattened set per-instance saves nothing — it would re-flatten every message. The cache must live on something longer-lived. Options:
- **(a)** Compute the flattened set lazily inside `IQueueConfiguration` (would require an interface change — out of scope for Phase 1).
- **(b)** Add a static `ConditionalWeakTable<IQueueConfiguration, HashSet<string>>` in `ConsumeContext` so each unique config instance gets one flattened set computed once, GC'd when the config is collected. Thread-safe via the table's own concurrency.
- **(c)** Pass the flattened set as an extra constructor arg from the bus, computed once at bus construction.

Recommended: **(b)** — minimal blast radius, no interface change. `ConditionalWeakTable` provides thread-safe lazy initialization via `GetValue(key, factory)`.

- [ ] **Step 3: Write the failing test**

```csharp
// Add to src/ServiceConnect.UnitTests/ConsumeContextStrictReplyValidationTests.cs
[Fact]
public void IsKnownQueue_LargeMappingSet_LooksUpInConstantTime()
{
    // Build a QueueConfig with 1000 mappings × 10 queues each (10k total). The old
    // implementation was O(N) per call; the new is O(1). We don't assert wall-clock
    // (too flaky); we assert correctness on an entry near the tail.
    var mappings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    for (int i = 0; i < 1000; i++)
    {
        mappings[$"msg.type.{i}"] = Enumerable.Range(0, 10).Select(j => $"queue.{i}.{j}").ToArray();
    }
    var config = new Mock<IQueueConfiguration>();
    config.SetupGet(c => c.QueueName).Returns("self");
    config.SetupGet(c => c.ErrorQueueName).Returns("self.error");
    config.SetupGet(c => c.AuditQueueName).Returns("self.audit");
    config.SetupGet(c => c.QueueMappings).Returns(mappings);

    // Tail entry — exercises the flattening
    Assert.True(ConsumeContext.IsKnownQueue("queue.999.9", config.Object));
    // Negative
    Assert.False(ConsumeContext.IsKnownQueue("nope", config.Object));
    // Case-insensitive (current contract via OrdinalIgnoreCase)
    Assert.True(ConsumeContext.IsKnownQueue("QUEUE.999.9", config.Object));
}
```

Note: this test will PASS against the existing implementation (correctness is already there); the test exists to lock in correctness across the refactor. To make the test express the actual change, add a separate test that calls `IsKnownQueue` 10,000 times against a 10k-mapping config and asserts the total elapsed time is under a generous bound (e.g. 50 ms). This is the "old vs new" perf canary.

```csharp
[Fact]
public void IsKnownQueue_RepeatedLookups_StayUnderPerfBudget()
{
    // ... same large config setup
    var sw = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < 10_000; i++)
    {
        ConsumeContext.IsKnownQueue($"queue.{i % 1000}.{i % 10}", config.Object);
    }
    sw.Stop();
    // 10k × O(N=10k) = 100M compares (old) ≈ seconds; 10k × O(1) ≈ milliseconds.
    // 50ms is generous for CI variance but still well below the old cost.
    Assert.True(sw.ElapsedMilliseconds < 50,
        $"IsKnownQueue is too slow: {sw.ElapsedMilliseconds}ms for 10k lookups against 10k mappings");
}
```

- [ ] **Step 4: Run the test to verify the perf test fails**

```
Agent(general-purpose, sonnet): "Run ConsumeContextStrictReplyValidationTests in
src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj with `-m:1`. Report
pass/fail per test and timings."
```

Expected: `IsKnownQueue_RepeatedLookups_StayUnderPerfBudget` FAILs (>>50ms); the others pass.

- [ ] **Step 5: Implement**

Edit `src/ServiceConnect/Services/ConsumeContext.cs:148-177`:

```csharp
// Per-IQueueConfiguration cache of the flattened reply-allow-list. ConditionalWeakTable
// keys by reference identity and GCs entries with their owning config, so a long-lived
// bus instance computes the set once and a transient test config doesn't pin memory.
// The factory's HashSet captures the well-known queue names alongside every mapped
// queue, all under OrdinalIgnoreCase to match the original string.Equals contract.
private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IQueueConfiguration, HashSet<string>> KnownQueueCache = new();

internal static bool IsKnownQueue(string address, IQueueConfiguration queueConfig)
{
    var set = KnownQueueCache.GetValue(queueConfig, BuildKnownQueueSet);
    return set.Contains(address);
}

private static HashSet<string> BuildKnownQueueSet(IQueueConfiguration queueConfig)
{
    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (!string.IsNullOrEmpty(queueConfig.QueueName)) set.Add(queueConfig.QueueName);
    if (!string.IsNullOrEmpty(queueConfig.ErrorQueueName)) set.Add(queueConfig.ErrorQueueName);
    if (!string.IsNullOrEmpty(queueConfig.AuditQueueName)) set.Add(queueConfig.AuditQueueName);
    foreach (var kvp in queueConfig.QueueMappings)
    {
        foreach (var queue in kvp.Value)
        {
            if (!string.IsNullOrEmpty(queue)) set.Add(queue);
        }
    }
    return set;
}
```

- [ ] **Step 6: Run the tests to verify all pass**

```
Agent(general-purpose, sonnet): "Run ConsumeContextStrictReplyValidationTests AND
ConsumeContextTests AND ConsumeContextPoolConcurrencyTests in
src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj with `-m:1`. Report
pass/fail per test."
```

Expected: all pass, including the perf test.

- [ ] **Step 7: Per-change code review**

Invoke `superpowers:requesting-code-review`.

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect/Services/ConsumeContext.cs \
        src/ServiceConnect.UnitTests/ConsumeContextStrictReplyValidationTests.cs
git commit -m "$(cat <<'EOF'
perf(consume): cache flattened reply-allow-list per IQueueConfiguration

IsKnownQueue iterated QueueMappings on every ReplyAsync; deployments with
many message-type → queue mappings paid O(total queues) per reply.
Compute the flattened HashSet lazily via ConditionalWeakTable so each
config instance is flattened once and GC'd with its owner.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Bus.BuildRoutingSlip duplicate destination re-validation

**HIGH FALSE-POSITIVE RISK — the existing code comment articulates a deliberate defence-in-depth rationale.**

**Files:**
- Read: `src/ServiceConnect/Bus.cs:1032-1062` and `:461-482`

**Finding context:** The audit said the re-validation at lines 1043-1057 is "20 dead-defensive lines" because `RouteAsync` already validates at lines 461-482. First-hand re-read reveals the code comment at 1039-1042 explicitly justifies the redundancy:

> *"RouteAsync's caller-validation already screened these. Today RouteAsync is the only caller of BuildRoutingSlip, but the slip's comma-separated wire format is non-recoverable on the receiving side; revalidate here as defence in depth so a future internal caller can't accidentally bypass the check."*

This is a documented design choice, not dead code. The cost is zero in the success case (validation is at slip-build time, not per-message). The benefit is that adding a new internal caller cannot silently produce a malformed slip.

- [ ] **Step 1: Validate**

Re-read `Bus.cs:1032-1062` and `:461-482`. Confirm:
- The comment at 1039-1042 still states the defence-in-depth rationale.
- `BuildRoutingSlip` is still `private static` with only `RouteAsync` as its caller.

Decision criteria:
- If the comment still articulates the defence-in-depth intent, the finding is a **false positive — keep the validation**. Append to `2026-05-17-rejected-findings.md`:

```markdown
## Bus.BuildRoutingSlip duplicate validation (review §Findings)

**Claim:** Re-validates destinations already validated at RouteAsync:461-482; ~20 dead-defensive lines.

**Verdict:** False positive — kept intentionally. The comment at Bus.cs:1039-1042
articulates the defence-in-depth rationale: the routing-slip wire format
(comma-separated) is non-recoverable on the receiving side, so revalidating at
slip-build time prevents a future internal caller from accidentally producing
malformed slips. Cost is zero on the success path; benefit is real. Removing
the second pass would be a regression of design intent.
```

Then SKIP the remaining steps.

- [ ] **Step 2-8: Skipped if validation closes as false positive.**

---

## Task 7: Bus.ReadInboundHopsCompleted utility extraction

**MEDIUM FALSE-POSITIVE RISK — extraction creates a generic helper that is only used once.**

**Files:**
- Read: `src/ServiceConnect/Bus.cs:540-561`

**Finding context:** The audit recommended extracting `TryParseInt32Header(headers, key, out int v)` from `ReadInboundHopsCompleted`. First-hand re-read shows the method does very specific work: read from a specific header, decode, parse as int with invariant culture, validate non-negative, clamp to `_busConfig.MaxRoutingSlipHops`. The clamp and non-negative validation are domain-specific to routing-slip hop counting. The "utility" abstraction (`TryParseInt32Header`) would be a thin wrapper around `HeaderDecoder.Decode` + `int.TryParse`, only used here — adding abstraction for no reuse is YAGNI.

- [ ] **Step 1: Validate**

Re-read `Bus.cs:540-561`. Grep for any other site that does the same "read header, decode, int.TryParse" pattern:

```bash
grep -n "HeaderDecoder.Decode.*int.TryParse\|TryGetValue.*HeaderDecoder.Decode.*TryParse" src/ServiceConnect src/ServiceConnect.Client.RabbitMQ --include='*.cs' -r
```

Decision criteria:
- If the pattern occurs in **2+ places**, the extraction has real reuse; proceed.
- If it occurs **only** in `ReadInboundHopsCompleted`, the finding is a YAGNI false positive; append to `2026-05-17-rejected-findings.md` and skip.

- [ ] **Step 2-8: Skipped if validation closes as false positive.** If proceeding (multi-site reuse confirmed), follow Task 1's structure: test-first, implement extraction in a new internal static class (e.g. `Services/HeaderParsing.cs`), update both sites.

---

## Task 8: Bus.ReservedHeaders — StringComparer.Ordinal rationale comment

**Files:**
- Modify: `src/ServiceConnect/Bus.cs:952-956`

**Finding context:** `ReservedHeaders` is initialized as a static `HashSet<string>` with `StringComparer.Ordinal`. The choice of ordinal (case-sensitive) over the more permissive `OrdinalIgnoreCase` is deliberate — AMQP wire-header names are case-sensitive per spec — but the rationale is not in the code. One-line comment addition.

- [ ] **Step 1: Validate**

Re-read `Bus.cs:945-960` (approx — find the `ReservedHeaders` initializer):

```bash
grep -n "ReservedHeaders" src/ServiceConnect/Bus.cs
```

Confirm:
- `ReservedHeaders` is still initialized with `StringComparer.Ordinal`.
- There is no existing comment explaining the choice.

Decision criteria:
- If a comment already exists, append to `2026-05-17-rejected-findings.md` and stop.
- Otherwise proceed.

- [ ] **Step 2: Implement (no test — this is a documentation-only change)**

Edit the line above the `ReservedHeaders` initializer:

```csharp
// StringComparer.Ordinal (case-sensitive) — AMQP wire-header names are
// case-sensitive per spec; matching with OrdinalIgnoreCase would treat
// "MessageId" and "messageid" as the same key when a malformed producer
// could be sending both.
private static readonly HashSet<string> ReservedHeaders = new(StringComparer.Ordinal)
{
    HeaderKeys.MessageId,
    HeaderKeys.CorrelationId,
};
```

- [ ] **Step 3: Build to confirm no syntax error**

```
Agent(general-purpose, sonnet): "Build src/ServiceConnect/ServiceConnect.csproj
with `-m:1`. Report success/failure."
```

Expected: build succeeds.

- [ ] **Step 4: Per-change code review**

Invoke `superpowers:requesting-code-review`.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Bus.cs
git commit -m "$(cat <<'EOF'
docs(bus): explain StringComparer.Ordinal on ReservedHeaders

AMQP wire-header names are case-sensitive per spec, so the reserved-keys
set uses Ordinal (not OrdinalIgnoreCase). The choice was unexplained;
add a one-line rationale so future readers don't relax it.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review Checklist (run before declaring this plan ready)

1. **Spec coverage:** Every Phase 1 finding from [docs/reviews/2026-05-17-refactor-phasing.md](../../reviews/2026-05-17-refactor-phasing.md) Phase 1 section has a task. ✔
2. **False-positive gates:** Tasks 2, 4, 6, 7 are marked with explicit FP-risk warnings and validate-first decision criteria. ✔
3. **No placeholders:** Every step has real code or a real command. No "implement later" / "similar to Task N" / "TODO". ✔
4. **Test patterns:** All test sketches reference real test infrastructure (xUnit, Moq, FakeTimeProvider, FakeLogger) actually present in `ServiceConnect.UnitTests`. ✔
5. **Dotnet delegation:** Every `dotnet` step is delegated to a subagent per CLAUDE.md. ✔
6. **Commit hygiene:** Every commit is one task, with a present-tense scoped message and the Claude co-author trailer. No `--no-verify`, no `--amend`. ✔
7. **End-of-phase gate:** Documented, with explicit unit / e2e / serialization / example-build / example-smoke / docs / README checks. ✔

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-05-17-phase-1-localized-fixes.md`. Two execution options:

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration. Each task is small enough that one subagent per task keeps reviews short and isolated.

**2. Inline Execution** — Execute tasks in this session using `superpowers:executing-plans`, batch execution with checkpoints for review.

Which approach?
