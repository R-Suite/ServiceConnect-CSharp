# Phase 0 — Uncertain Investigation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Before starting fix work, verify or refute the two potentially-Critical/High Uncertain items by writing the named end-to-end tests against real infrastructure. Outcomes drive Phase 1 sequencing.

**Architecture:** Each investigation is a single task that writes one integration test in `src/ServiceConnect.EndToEndTests/`. The test's behaviour (PASS vs FAIL) determines the issue's fate: a FAIL that matches the symptom promotes the issue to Phase 1 as a proper fix group; a PASS commits the test as a regression guard and marks the issue `[-] disconfirmed` in the consolidated doc. Pre-work (baselines + doc checkboxes) runs first as a single commit with no code changes.

**Tech Stack:**
- xUnit + Testcontainers (`Testcontainers.MongoDb`, `Testcontainers.RabbitMq`) — already in the project via `PersistenceFixture`
- MongoDB.Driver 2.x — used by `MongoDbTimeoutStore`
- `Microsoft.Extensions.Time.Testing.FakeTimeProvider` — already used by `MongoDbAggregatorPersistorTests`
- `sg docker -c '...'` wrapper for all Docker operations (user is not in the docker group; see user memory `feedback_docker_access.md`)

---

## File Structure

**New files:**
- `src/ServiceConnect.EndToEndTests/Persistence/MongoDbTimeoutStoreFacetTests.cs` — real-Mongo test for `GetTimeoutsBatchAsync`'s facet pattern-match.
- `src/ServiceConnect.EndToEndTests/ErrorHandling/PoisonMessageRedeliveryTests.cs` — RabbitMQ test for bounded redelivery when the retry-publish path throws.

**Modified files:**
- `consolodated-issues/2026-04-22-consolidated-issues.md` — add `[ ]` prefixes and progress summary block in Task 2; flip to `[x] (commit: <sha>)` or `[-] <reason>` in Tasks 3 and 4 as each investigation commits.

**Why these files:**
- Both tests live under `EndToEndTests` because each requires real infrastructure (Mongo + RabbitMQ via Testcontainers). `UnitTests` uses mocks and cannot reach these bugs.
- Facet test goes in `Persistence/` next to `MongoDbAggregatorPersistorTests.cs` (same fixture, same pattern).
- Poison-message test goes in `ErrorHandling/` next to `RetryAndErrorQueueTests.cs` (same bus+handler+error-queue scaffolding).

---

## Task 1 — Pre-work: baseline green + docker smoke

**Files:** none modified in this task.

- [ ] **Step 1: Confirm branch and tree state**

Run:
```bash
git status
git rev-parse --abbrev-ref HEAD
```
Expected: branch is `v7-clean-architecture`. Untracked files acceptable; unstaged modifications acceptable. No action yet.

- [ ] **Step 2: Run unit test suite on HEAD**

Run:
```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --nologo --verbosity minimal
```
Expected: PASS. Record pass/skip/fail counts in the commit message of Task 2. If any test is red, **stop**: we fix baseline before Phase 0 proceeds.

- [ ] **Step 3: Run integration test suite on HEAD**

Run:
```bash
sg docker -c 'dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --nologo --verbosity minimal'
```
Expected: PASS. Record counts for Task 2's commit message. If red: **stop** and fix baseline.

- [ ] **Step 4: Docker smoke — container pull check**

Run:
```bash
sg docker -c 'docker pull rabbitmq:3-management' && sg docker -c 'docker pull mongo:latest'
```
Expected: both images pull without authentication errors. Proves Testcontainers will be able to start containers in later tasks.

---

## Task 2 — Pre-work: add progress checkboxes to consolidated doc

**Files:**
- Modify: `consolodated-issues/2026-04-22-consolidated-issues.md`

This is a doc-only commit. No code touched.

- [ ] **Step 1: Add progress summary block at top of consolidated doc**

Open `consolodated-issues/2026-04-22-consolidated-issues.md`. Immediately after the first line (`## Critical`) is preceded by an implicit title section. Insert this block at the very top of the file, before `## Critical`:

```markdown
**Progress:** Critical 0/1 · High 0/8 · Medium 0/22 · Low 0/14 · Uncertain 0/3 (updated 2026-04-22)

**Legend:** `[ ]` pending · `[x] (commit: <sha>)` done · `[-] <reason>` deferred/disconfirmed · `[~] <reason>` inconclusive

---

```

- [ ] **Step 2: Prefix every issue bullet with `[ ]`**

Every issue is introduced by a line matching `- **<title>**`. Prefix each such bullet with `[ ]`:

Before:
```markdown
- **`ConsumeContextPool` self-referential `EnsureActive` guard permits cross-message data leak** — ...
```

After:
```markdown
- [ ] **`ConsumeContextPool` self-referential `EnsureActive` guard permits cross-message data leak** — ...
```

Apply this to all 48 issue bullets across Critical / High / Medium / Low / Uncertain.

Verify count:
```bash
grep -c '^- \[ \] \*\*' consolodated-issues/2026-04-22-consolidated-issues.md
```
Expected: `48`.

- [ ] **Step 3: Commit**

```bash
git add consolodated-issues/2026-04-22-consolidated-issues.md
git commit -m "$(cat <<'EOF'
chore(tracking): add progress checkboxes to 2026-04-22 consolidated issues doc

Pre-work for the round-2 remediation plan
(docs/superpowers/specs/2026-04-22-consolidated-issues-remediation-design.md).
Adds [ ] prefix to every issue and a progress summary block at the top of the
doc. Baseline test suites green on HEAD: unit <N>, integration <N>.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

Replace `<N>` with the counts recorded during Task 1 before running the commit command.

Expected: one commit, no code files changed. `git show --stat HEAD` shows only the consolidated-issues doc modified.

---

## Task 3 — Investigation A: Mongo `AggregateFacetResult<T>` pattern-match

**The hypothesis:** `MongoDbTimeoutStore.GetTimeoutsBatchAsync` at lines 167-182 contains:

```csharp
var dueFacet = facetResult.Facets.FirstOrDefault(f => f.Name == "Due");
if (dueFacet is AggregateFacetResult<TimeoutData> typedDue)
{
    foreach (var doc in typedDue.Output)
        retval.DueTimeouts.Add(doc);
}
```

If the MongoDB driver returns a non-generic `AggregateFacetResult` (or a BsonDocument-wrapped carrier) instead of the typed `AggregateFacetResult<TimeoutData>`, the `is` pattern match fails silently, the `foreach` body never runs, and `DueTimeouts` is always empty. Production symptom: no timeouts ever dispatch on Mongo.

**The test:** insert a `TimeoutData` row with `Time <= utcNow` into a fresh Mongo database, call `GetTimeoutsBatchAsync`, assert that `result.DueTimeouts` contains the inserted row.

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/Persistence/MongoDbTimeoutStoreFacetTests.cs`
- Modify: `consolodated-issues/2026-04-22-consolidated-issues.md` (flip one `[ ]` to `[x]` or `[-]`)

- [ ] **Step 1: Write the test**

Create `src/ServiceConnect.EndToEndTests/Persistence/MongoDbTimeoutStoreFacetTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(PersistenceCollection))]
public class MongoDbTimeoutStoreFacetTests
{
    private readonly PersistenceFixture _fixture;

    public MongoDbTimeoutStoreFacetTests(PersistenceFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task GetTimeoutsBatchAsync_ReturnsDueTimeoutsFromFacet()
    {
        // Investigation for the "Uncertain" item at consolodated-issues/2026-04-22-consolidated-issues.md:
        // MongoDbTimeoutStore.GetTimeoutsBatchAsync uses `is AggregateFacetResult<TimeoutData>` pattern match on
        // the facet result. If the pinned MongoDB driver returns a non-generic carrier, the match fails silently
        // and DueTimeouts is always empty → total timeout-dispatch outage.
        //
        // This test PASSES if the pattern match works as intended (issue disconfirmed).
        // This test FAILS (DueTimeouts is empty) if the pattern match is broken (issue confirmed).

        var now = new DateTimeOffset(2026, 4, 22, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var dbName = _fixture.GetUniqueDatabaseName("timeoutfacet");
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = dbName,
        };
        var client = MongoClientFactory.Create(options);
        var store = new MongoDbTimeoutStore(
            client,
            options,
            NullLogger<MongoDbTimeoutStore>.Instance,
            timeProvider);

        var timeoutId = Guid.NewGuid();
        var processManagerId = Guid.NewGuid();
        var timeout = new TimeoutData
        {
            Id = timeoutId,
            ProcessManagerId = processManagerId,
            Destination = "test-destination",
            Time = now.AddMinutes(-5), // already due
            Locked = false,
            LockedBy = Guid.Empty,
            Headers = new Dictionary<string, object> { ["k"] = "v" },
        };

        await store.InsertTimeoutAsync(timeout);

        var batch = await store.GetTimeoutsBatchAsync();

        Assert.NotNull(batch);
        Assert.Single(batch.DueTimeouts);
        Assert.Equal(timeoutId, batch.DueTimeouts[0].Id);
        Assert.Equal(processManagerId, batch.DueTimeouts[0].ProcessManagerId);
    }
}
```

- [ ] **Step 2: Run the test**

Run:
```bash
sg docker -c 'dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "FullyQualifiedName~MongoDbTimeoutStoreFacetTests" --nologo --verbosity normal'
```

Expected outcomes:
- **PASS** → pattern match works. Issue is **disconfirmed**. Proceed to Step 4 with outcome=disconfirmed.
- **FAIL with `Assert.Single` failure** (`DueTimeouts` empty) → pattern match is broken. Issue is **confirmed**. Proceed to Step 3 (stop-and-ask gate).
- **Other failure** (compile error, Mongo container failed to start, test infra problem) → fix the test before interpreting. This is not a confirmation either way.

- [ ] **Step 3: Stop-and-ask gate (confirmed only)**

If the test FAILED with `DueTimeouts` empty, **stop and notify the user**:

> "Phase 0 Task 3: Mongo facet pattern-match issue is CONFIRMED. `GetTimeoutsBatchAsync` returned empty `DueTimeouts` for an inserted due timeout. This is a Critical-severity bug (total timeout-dispatch outage on Mongo). Recommend promoting to Phase 1 as its own group, slotted before the InMemory/Mongo lease-safety parity group. Proceed?"

Wait for user response before committing. If the user confirms the promotion, commit the test as-is and include a line in the design doc's follow-up history. If the user redirects, follow their direction. Do **not** commit a fix in Phase 0 — fixes only happen in Phases 1-4.

- [ ] **Step 4: Update the consolidated doc**

Open `consolodated-issues/2026-04-22-consolidated-issues.md` and find the Uncertain bullet:

```markdown
- [ ] **Mongo `AggregateFacetResult<T>` pattern-match may fail under the pinned driver** — ...
```

Flip the checkbox based on the outcome:

- **Disconfirmed (test passed):**
  ```markdown
  - [-] **Mongo `AggregateFacetResult<T>` pattern-match may fail under the pinned driver** — disconfirmed by MongoDbTimeoutStoreFacetTests.GetTimeoutsBatchAsync_ReturnsDueTimeoutsFromFacet — ...
  ```

- **Confirmed (test failed, user approved promotion):**
  ```markdown
  - [x] (commit: <sha-placeholder>) **Mongo `AggregateFacetResult<T>` pattern-match may fail under the pinned driver** — ...
  ```
  After the commit in Step 5, replace `<sha-placeholder>` with the actual commit SHA (you can do this with a follow-up commit or an amend — but per design-doc rules we never amend published commits; use a follow-up doc-only touch-up if needed).

Also update the progress summary block's Uncertain counter: `Uncertain 0/3` → `Uncertain 1/3`.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/Persistence/MongoDbTimeoutStoreFacetTests.cs \
        consolodated-issues/2026-04-22-consolidated-issues.md
git commit -m "$(cat <<'EOF'
test(timeout): add real-Mongo assertion for GetTimeoutsBatchAsync facet result

Phase 0 investigation for the "Uncertain" item flagged at
consolodated-issues/2026-04-22-consolidated-issues.md: GetTimeoutsBatchAsync
uses `is AggregateFacetResult<TimeoutData>` pattern match on the facet
output. A broken match would silently yield empty DueTimeouts and halt all
timeout dispatch on Mongo. This test inserts a due TimeoutData row, calls
GetTimeoutsBatchAsync, and asserts the row appears in DueTimeouts.

Outcome: <disconfirmed | confirmed>

Refs: Uncertain — Mongo AggregateFacetResult<T> pattern-match

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

Replace `<disconfirmed | confirmed>` with the actual outcome. Include a one-line rationale if confirmed (e.g., `confirmed — DueTimeouts empty for inserted due row under Mongo 7.x driver`).

---

## Task 4 — Investigation B: Poison-message redelivery under retry-publish failure

**The hypothesis:** `RabbitMqConsumerHost.ProcessMessageAsync` (src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs lines 166-227, 229, 252, 353-360) calls `_retryHandler.HandleFailureAsync` / `HandleTerminalFailureAsync` without an inner try/catch around either. If those calls throw (publisher confirm rejection, broker outage, closed channel), the exception propagates to the outer `catch (Exception ex)` at line 231-234, leaves `processed = false`, and the `finally` block nacks the message with `requeue: true`. The broker redelivers, the handler throws again, the retry-publish throws again → loop.

The question is whether the loop self-throttles via broker RTT or grows unbounded within a fixed observation window.

**The test:** configure a consumer with an always-throwing handler. Set `MaxRetries = 0` so every failure is a terminal-failure. Sabotage the error path by pre-declaring the error queue with `durable: true` + `x-max-length=0` + `x-overflow=reject-publish`. When the bus starts it declares the same queue name with matching `durable: true` and no extra args — `RabbitmqClient.QueueDeclareAsync` is tolerant of *equivalent* args, so the bus's declare either succeeds (args ignored because pre-declared) or fails and the topology provisioner swallows the `OperationInterruptedException` unless `isInitialSetup=true`. Enable publisher confirms via the `PublisherAcknowledgements` **client setting** (not a top-level property — set via `t.SetClientSetting("PublisherAcknowledgements", true)`) so that the broker's `reject-publish` nack surfaces as a thrown exception inside `MessageRetryHandler.PublishErrorAsync`. Count handler invocations over a 10-second observation window. Assert redelivery count is bounded at or below an empirically-reasonable ceiling (e.g., < 100).

**Three possible outcomes** (not two):
- **Confirmed** — redelivery count ≥ ceiling → unbounded loop, promote to Phase 1.
- **Disconfirmed** — redelivery count < ceiling and > 1 (the sabotage definitely triggered the failure path) → broker RTT self-throttles acceptably. Keep as regression guard.
- **Inconclusive** — redelivery count is exactly 1 (error publish somehow succeeded) or 0 (handler never invoked) → sabotage didn't trigger the failure path we wanted to study. The test does not prove anything either way. Document the observation in the commit message, leave the issue as `[~] inconclusive` in the consolidated doc, and flag to the user.

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/ErrorHandling/PoisonMessageRedeliveryTests.cs`
- Modify: `consolodated-issues/2026-04-22-consolidated-issues.md`

- [ ] **Step 1: Write the test**

Create `src/ServiceConnect.EndToEndTests/ErrorHandling/PoisonMessageRedeliveryTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class PoisonMessageRedeliveryTests
{
    private readonly MessagingFixture _fixture;

    public PoisonMessageRedeliveryTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task RetryPublishFailure_DoesNotCauseUnboundedRedelivery()
    {
        // Investigation for the "Uncertain" item at consolodated-issues/2026-04-22-consolidated-issues.md:
        // if HandleFailureAsync / HandleTerminalFailureAsync throws (e.g., broker nacks the publish
        // under publisher confirms), the outer catch leaves processed=false and the finally block
        // nacks the original with requeue:true → redelivery loop. This test arranges a handler that
        // always throws with MaxRetries=0 and an error queue that rejects all publishes, forcing the
        // terminal-failure publish to fail. It then counts handler invocations over a fixed window.
        //
        // PASS (redelivery bounded below ceiling) = disconfirmed; broker RTT self-throttles enough.
        // FAIL (redelivery count >= ceiling) = confirmed; needs a try/catch around the retry/terminal
        // publishes to bound the loop explicitly.

        var queueName = _fixture.GetUniqueQueueName("poison");
        var errorQueueName = _fixture.GetUniqueQueueName("poison-eq");
        int attemptCount = 0;

        // Pre-declare the error queue with reject-publish so the broker nacks publishes to it
        // once it hits the max-length of 0. This forces HandleTerminalFailureAsync to throw
        // under publisher confirms, simulating the broker-outage scenario from the issue.
        var factory = new ConnectionFactory
        {
            HostName = _fixture.RabbitMqHostname,
            Port = _fixture.RabbitMqPort,
            UserName = _fixture.RabbitMqUsername,
            Password = _fixture.RabbitMqPassword,
        };
        await using (var conn = await factory.CreateConnectionAsync())
        await using (var channel = await conn.CreateChannelAsync())
        {
            var errorQueueArgs = new Dictionary<string, object?>
            {
                ["x-max-length"] = 0,
                ["x-overflow"] = "reject-publish",
            };
            // Must match the bus's own declare (durable: true, exclusive: false, autoDelete: false)
            // so the bus's subsequent declare via RabbitMqTopologyProvisioner.ConfigureDeclareUtilityQueueAsync
            // sees equivalent args and succeeds. If durable flags mismatch, RabbitMQ returns
            // inequivalent_arg, and the topology provisioner's catch suppresses it (isInitialSetup=false),
            // but the queue would still be the pre-declared one — which is fine for us.
            await channel.QueueDeclareAsync(errorQueueName, durable: true, exclusive: false, autoDelete: false, arguments: errorQueueArgs);
        }

        var handlerReferences = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(CallbackHandler<TestMessage>),
                MessageType = typeof(TestMessage),
            },
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerReferences);
        services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new CallbackHandler<TestMessage>(_ =>
            {
                Interlocked.Increment(ref attemptCount);
                throw new InvalidOperationException("Simulated handler failure");
            }));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.MaxRetries = 0; // every failure → terminal-failure → publishes to error exchange
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("PublisherAcknowledgements", true); // so broker reject-publish surfaces as thrown exception
            });
            builder.ConfigureQueues(q =>
            {
                q.QueueName = queueName;
                q.ErrorQueueName = errorQueueName;
            });
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();

        try
        {
            var correlationId = Guid.NewGuid();
            var sent = new TestMessage(correlationId) { Content = "poison" };
            await bus.PublishAsync(sent);

            // Observe for 10 seconds. Redelivery loop symptom: attemptCount grows rapidly.
            await Task.Delay(TimeSpan.FromSeconds(10));

            const int ceiling = 100;
            Assert.True(attemptCount < ceiling,
                $"Handler invoked {attemptCount} times within 10 seconds — expected < {ceiling}. " +
                $"Issue CONFIRMED: retry-publish failure causes unbounded redelivery.");
        }
        finally
        {
            await bus.DisposeAsync();
            if (provider is IAsyncDisposable asyncProvider) await asyncProvider.DisposeAsync();
        }
    }
}
```

Notes on the test:
- Uses `CallbackHandler<T>` from `src/ServiceConnect.EndToEndTests/Bus/PublishSubscribeTests.cs` (declared `public class CallbackHandler<T> : IMessageHandler<T>` at line 10).
- Uses `TestMessage` from `src/ServiceConnect.EndToEndTests/Messages/TestMessage.cs`.
- Pre-declares the error queue with `durable: true` + `x-max-length=0` + `x-overflow=reject-publish` **before** the bus starts — this is the sabotage. `durable: true` matches the bus's own declare (`RabbitMqTopologyProvisioner:113`).
- `MaxRetries = 0` routes every failure directly to the error exchange (the sabotaged queue is bound to this exchange by the bus's own topology setup).
- Publisher confirms are enabled via the `PublisherAcknowledgements` **client setting** (`Producer.cs:73` reads it via `GetSetting`), set with `t.SetClientSetting("PublisherAcknowledgements", true)` — not a top-level property. This makes the broker's nack surface as a thrown exception in `MessageRetryHandler.PublishErrorAsync`'s `BasicPublishAsync` call.
- 10-second observation window; ceiling of 100 invocations. If the broker's RTT self-throttles, expect counts well below the ceiling but > 1. If exactly 1 or 0, the sabotage didn't trigger — inconclusive outcome, see Step 3.

- [ ] **Step 2: Verify the test compiles and the error-queue sabotage takes effect**

Run:
```bash
sg docker -c 'dotnet build src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --nologo --verbosity minimal'
```
Expected: BUILD SUCCESS.

If compile fails on any `builder.UseRabbitMQ(t => ...)` property name, grep for the correct names in the current codebase:
```bash
grep -n 'MaxRetries\|ErrorQueueName\|RabbitMQSettingKeys\.' src/ServiceConnect.Client.RabbitMQ/*.cs src/ServiceConnect/Bus/*.cs
```
Adjust property/setting names to match what's exposed in the current codebase (settings shape has shifted across versions).

- [ ] **Step 3: Run the test**

Run:
```bash
sg docker -c 'dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "FullyQualifiedName~PoisonMessageRedeliveryTests" --nologo --verbosity normal'
```

Expected outcomes:
- **PASS with `1 < attemptCount < 100`** → sabotage triggered (invocation count > 1 proves redelivery happened) AND loop self-throttled. Issue is **disconfirmed**. Proceed to Step 5 with outcome=disconfirmed.
- **FAIL with `attemptCount >= 100`** → loop is unbounded. Issue is **confirmed**. Proceed to Step 4 (stop-and-ask).
- **PASS with `attemptCount <= 1`** → sabotage didn't trigger the failure path (error publish succeeded, or handler was never invoked). Outcome is **inconclusive**. Proceed to Step 4b (inconclusive notification).

- [ ] **Step 4: Stop-and-ask gate (confirmed only)**

If the test FAILED with `attemptCount >= 100`, **stop and notify the user**:

> "Phase 0 Task 4: Poison-message redelivery issue is CONFIRMED. `attemptCount = <N>` over 10 seconds when the error-publish path throws. This is a High-severity bug (production hot-loop under partial broker outage). Recommend promoting to Phase 1 as its own group. The fix is a try/catch around `HandleFailureAsync`/`HandleTerminalFailureAsync` that falls back to logging + letting the broker handle the nack once, breaking the loop. Proceed?"

Wait for user response. On approval, commit the test as-is and log the promotion for Phase 1 sequencing. No fix in Phase 0.

- [ ] **Step 4b: Inconclusive outcome (sabotage didn't trigger)**

If `attemptCount <= 1`, the sabotage failed to produce an error-publish exception. Before abandoning, make one diagnostic pass:

1. **Confirm publisher confirms are active.** Run with `--verbosity normal` and search the test output for a log line mentioning publisher acks. If absent, the client setting name may have drifted; inspect `src/ServiceConnect.Client.RabbitMQ/RabbitMQSettingKeys.cs` for the current key.
2. **Confirm the error queue's args are in effect.** In the test, after `QueueDeclareAsync`, issue a `QueueDeclarePassiveAsync` to read the queue back and assert `x-max-length=0` is present.
3. **Confirm the handler was invoked at all.** If `attemptCount == 0`, the message never reached the handler; check queue bindings / exchange naming in the log output.

If none of (1), (2), (3) point to a simple fix, commit the test as an inconclusive investigation and notify the user:

> "Phase 0 Task 4: Poison-message redelivery is INCONCLUSIVE. The sabotage (pre-declared error queue with reject-publish) did not produce a BasicPublishAsync exception within a 10s window; `attemptCount = <0 or 1>`. The hypothesis remains unproven but not ruled out. Options: (a) mark the issue as `[~] inconclusive` and defer, (b) invest in a stronger reproducer such as killing the RabbitMQ container mid-test. Recommend (a) since the bug, if real, needs a production observation to characterise rather than a synthetic test."

Default behaviour: follow (a). Wait for user redirect if they prefer (b).

- [ ] **Step 5: Update the consolidated doc**

Find the Uncertain bullet:
```markdown
- [ ] **Poison-message infinite redelivery when retry/error publish throws** — ...
```

Flip based on outcome:
- **Disconfirmed:** `- [-] **Poison-message infinite redelivery ...** — disconfirmed by PoisonMessageRedeliveryTests.RetryPublishFailure_DoesNotCauseUnboundedRedelivery; <N> invocations over 10s, under ceiling of 100 — ...`
- **Confirmed (and user approved promotion):** `- [x] (commit: <sha-placeholder>) **Poison-message infinite redelivery ...** — ...`
- **Inconclusive:** `- [~] **Poison-message infinite redelivery ...** — inconclusive; sabotage did not reliably trigger BasicPublishAsync exception (attemptCount=<0 or 1>) — ...`

Update progress summary block: only increment `Uncertain` counter if the outcome is a definitive disconfirm or confirm. Inconclusive stays counted as pending.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/ErrorHandling/PoisonMessageRedeliveryTests.cs \
        consolodated-issues/2026-04-22-consolidated-issues.md
git commit -m "$(cat <<'EOF'
test(rabbitmq): add bounded-redelivery test for failing retry/error publish

Phase 0 investigation for the "Uncertain" item flagged at
consolodated-issues/2026-04-22-consolidated-issues.md: if the retry or
terminal-failure publish throws inside ProcessMessageAsync, the outer
catch leaves processed=false and the finally block nacks with
requeue:true → redelivery loop. This test sabotages the error queue with
x-max-length=0 + reject-publish, forces MaxRetries=0, and asserts handler
invocations stay bounded over a 10-second observation window.

Outcome: <disconfirmed | confirmed>

Refs: Uncertain — Poison-message infinite redelivery

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

Replace `<disconfirmed | confirmed>` with the actual outcome and include a one-line observation (e.g., `disconfirmed — 14 invocations over 10 seconds`).

---

## Task 5 — End-of-phase summary

**Files:** none modified in this task — just a status post back to the user.

- [ ] **Step 1: Post a one-paragraph end-of-phase summary**

After Task 4 commits, post to the user:

> "Phase 0 complete. Two Uncertain items investigated:
> - Mongo `AggregateFacetResult<T>` pattern-match: <outcome> (commit: <sha>)
> - Poison-message redelivery: <outcome> (commit: <sha>)
>
> Progress: Critical 0/1 · High 0/8 · Medium 0/22 · Low 0/14 · Uncertain 2/3
> Remaining Uncertain item (AggregatorProcessor timer) deferred to Phase 4.
>
> Proceeding to Phase 1 (writing Phase 1 implementation plan first)."

If either investigation was confirmed and promoted to Phase 1, state that clearly in the summary so the reader knows Phase 1's scope has grown.

- [ ] **Step 2: Begin Phase 1 planning**

Invoke the writing-plans skill again to produce the Phase 1 implementation plan. The design doc at `docs/superpowers/specs/2026-04-22-consolidated-issues-remediation-design.md` is the spec. Start from Task 1 of Phase 1 in that plan.

---

## Rollback

Each of the three commits in Phase 0 (Task 2, Task 3, Task 4) is a single-purpose commit. Any one can be reverted with `git revert <sha>` without touching the others.

## Out of scope for Phase 0

- Any fix work. Phase 0 is observation-only.
- The third Uncertain item (`AggregatorProcessor` timer-fired flush) — deferred to Phase 4 per the design doc.
- Mutation of existing tests. Phase 0 only adds.
