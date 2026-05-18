# ServiceConnect Pre-Release Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the six pre-release fixes identified in the 2026-05-18 architecture review — three doc/style fixes and three small behaviour additions — each as an independent commit so the release can ship with: (a) a fail-fast missing-producer error at host start, (b) a public `AggregatorLeaseDuration` option, (c) `RabbitMqOptions` validation, (d) a snapshot-remove-failure metric, (e) handler-cooperation warnings on the public shutdown surface, and (f) the lingering `Phase A.2` comment removed.

**Architecture:** Six independent tasks. Each task is one commit. Tasks 1–3 are pure docs/string changes with no behaviour impact (low risk). Tasks 4–6 add behaviour or DI surface; each is gated by a unit test that exercises the new code path. No task depends on another; they can ship in any order, but the order below is "warmest cache first" — Task 1 is a 30-second edit, Task 6 is the largest surface change.

**Tech Stack:** .NET 8 / .NET 10 multi-target, xUnit, Moq, `Microsoft.Extensions.DependencyInjection`, `System.Diagnostics.Metrics`. Build command per project is `dotnet build src/<Project>/<Project>.csproj -m:1`; test command is `dotnet test src/<TestProject>/<TestProject>.csproj -m:1 --filter "FullyQualifiedName~<Class>"`. Per the repo's CLAUDE.md, `dotnet` invocations run inside a cgroup-bounded systemd scope automatically via the `~/.local/bin/dotnet` wrapper — call `dotnet` normally, do not bypass the wrapper. Per the user's auto-memory, agents (subagents) must run all `dotnet build` / `dotnet test` commands; do not run them from the main session.

---

## File Structure

| File | Responsibility | Touched by task |
|---|---|---|
| `src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj` | Test-project csproj — strip lingering meta-reference | 1 |
| `src/ServiceConnect.Interfaces/Bus/IBus.cs` | Public bus surface — add handler-cooperation warning to `StopConsumingAsync` xmldoc | 2 |
| `src/ServiceConnect.Interfaces/Bus/IConsumer.cs` | Public consumer surface — add handler-cooperation warning to `StopConsumingAsync` xmldoc | 2 |
| `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMqOptions.cs` | Add `Validate()` method returning failure messages | 3 |
| `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQExtensions.cs` | Call `options.Validate()` inside `UseRabbitMQ(opts => …)` before applying to client settings | 3 |
| `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqOptionsValidateTests.cs` | New xUnit test class for `RabbitMqOptions.Validate()` | 3 |
| `src/ServiceConnect.Persistence.MongoDb/Configuration/MongoDbPersistenceOptions.cs` | Add public `AggregatorLeaseDuration` property | 4 |
| `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs` | Read lease default from `options.AggregatorLeaseDuration` instead of hard-coded `DefaultLeaseDuration` | 4 |
| `src/ServiceConnect.UnitTests/Persistence/MongoDb/MongoDbPersistenceOptionsAggregatorLeaseTests.cs` | New xUnit test asserting the persistor honours `AggregatorLeaseDuration` | 4 |
| `src/ServiceConnect/Diagnostics/MetricNames.cs` | Add new metric name constant | 5 |
| `src/ServiceConnect/Diagnostics/ServiceConnectMeter.cs` | Add new `Counter<long>` + `Add…` accessor | 5 |
| `src/ServiceConnect/Services/Processors/AggregatorProcessor.cs` | Increment the new counter in the snapshot-remove-after-success failure path | 5 |
| `src/ServiceConnect.UnitTests/Aggregator/AggregatorProcessorSnapshotRemoveCounterTests.cs` | New xUnit test asserting the counter fires on persistor failure | 5 |
| `src/ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs` | Add `AllowMissingProducer` property with `{ get; set; }` and full xmldoc | 6 |
| `src/ServiceConnect/Configuration/BusConfiguration.cs` | Add backing field + freeze-guarded setter for `AllowMissingProducer` | 6 |
| `src/ServiceConnect/Services/BusHostedService.cs` | Accept optional `IProducer? producer` param, throw if null and `!AllowMissingProducer` | 6 |
| `src/ServiceConnect.UnitTests/Bus/BusHostedServiceMissingProducerTests.cs` | New xUnit test class for the validation paths | 6 |

---

## Pre-flight (every task)

Before starting a task, the agent should:

1. Confirm `dotnet` resolves to the wrapper: `which dotnet` must return `/home/tim/.local/bin/dotnet`. If not, stop and ask the user — do NOT proceed.
2. Confirm the working tree is clean: `git status` should show `nothing to commit, working tree clean` (or only show the files this task is about to touch). If unrelated dirty files exist, stop and ask.
3. Confirm the current branch is suitable. The default branch is `v7-clean-architecture`; ask before committing if on `master` or a release branch.

---

## Task 1: Strip `Phase A.2` meta-reference from test-project csproj

**Files:**
- Modify: `src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj:35`

**Why:** The repo's CLAUDE.md prohibits ticket/phase/work-stream references in source. The csproj comment is the last surviving violation — found via `grep -rn "Phase\s*[A-Z]\.[0-9]" src --include='*.cs' --include='*.csproj'` which returns exactly one match. Strip it; the rest of the comment carries the technical content.

- [ ] **Step 1: Read the current comment**

Read: `src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj` lines 30–40.
Expected: line 35 reads `         Production packages have no Newtonsoft dependency after Phase A.2. -->`

- [ ] **Step 2: Edit the comment**

Apply this Edit:

```
old_string:
    <!-- Newtonsoft is referenced only here, scoped to wire-compat verification.
         Production packages have no Newtonsoft dependency after Phase A.2. -->

new_string:
    <!-- Newtonsoft is referenced only here, scoped to wire-compat verification.
         Production packages have no Newtonsoft dependency. -->
```

- [ ] **Step 3: Verify no other phase references remain**

Run: `grep -rn "Phase\s*[A-Z]\.[0-9]\|Phase [0-9]" src --include='*.cs' --include='*.csproj' 2>/dev/null | grep -v '/bin/\|/obj/'`
Expected: zero lines printed (no remaining matches).

- [ ] **Step 4: Build the test project**

Delegate to a subagent (not the main session). The subagent should run:
`dotnet build src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj -m:1`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj
git commit -m "$(cat <<'EOF'
chore(serialization-compat-tests): strip stale Phase A.2 reference from csproj comment

The comment's technical content (Newtonsoft scoped to wire-compat tests, no production
dependency) is preserved; only the work-stream label is removed per the repo's
in-source meta-reference style rule.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Add handler-cooperation warning to IBus / IConsumer shutdown xmldoc

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Bus/IBus.cs:198-206`
- Modify: `src/ServiceConnect.Interfaces/Bus/IConsumer.cs:25-38`

**Why:** Today's xmldoc describes the *terminal* semantics of `StopConsumingAsync` but does not warn that handlers performing synchronous I/O (or ignoring their `CancellationToken`) will block graceful shutdown for the full `DisposeTimeout` window. This is the single most likely production-support ticket and the cheapest finding to address — doc-only.

- [ ] **Step 1: Read the IBus.StopConsumingAsync xmldoc**

Read: `src/ServiceConnect.Interfaces/Bus/IBus.cs` lines 195–210 to confirm the current `<summary>` exactly matches the `old_string` below.

- [ ] **Step 2: Edit IBus.StopConsumingAsync xmldoc**

Apply this Edit to `src/ServiceConnect.Interfaces/Bus/IBus.cs`:

```
old_string:
    /// <summary>
    /// Stops consuming messages and disposes the underlying consumer.
    /// <para>
    /// This operation is <b>terminal</b>: once stopped, the bus cannot be restarted.
    /// <see cref="StartConsumingAsync"/> will throw <see cref="InvalidOperationException"/>.
    /// To resume consumption, dispose this bus and create a new instance.
    /// </para>
    /// </summary>
    Task StopConsumingAsync(CancellationToken cancellationToken = default);

new_string:
    /// <summary>
    /// Stops consuming messages and disposes the underlying consumer.
    /// <para>
    /// This operation is <b>terminal</b>: once stopped, the bus cannot be restarted.
    /// <see cref="StartConsumingAsync"/> will throw <see cref="InvalidOperationException"/>.
    /// To resume consumption, dispose this bus and create a new instance.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Handler cooperation is required for prompt shutdown.</b> The framework signals
    /// graceful stop by cancelling the per-message <see cref="CancellationToken"/> threaded
    /// through handler dispatch and by closing the transport admission gate. A handler that
    /// performs synchronous I/O or ignores its <c>CancellationToken</c> will block the
    /// shutdown grace window for the full duration of that work, up to the configured
    /// <see cref="IBusConfiguration.DisposeTimeout"/>. Use cancellation-aware async APIs
    /// (<c>HttpClient.GetAsync</c>, database drivers that accept a <c>CancellationToken</c>,
    /// etc.) inside handlers to ensure prompt shutdown.
    /// </remarks>
    Task StopConsumingAsync(CancellationToken cancellationToken = default);
```

- [ ] **Step 3: Read the IConsumer.StopConsumingAsync xmldoc**

Read: `src/ServiceConnect.Interfaces/Bus/IConsumer.cs` lines 20–40 to confirm the current `<summary>` matches the `old_string` below.

- [ ] **Step 4: Edit IConsumer.StopConsumingAsync xmldoc**

Apply this Edit to `src/ServiceConnect.Interfaces/Bus/IConsumer.cs`:

```
old_string:
    /// <summary>
    /// Issues a graceful stop: instructs the broker to stop delivering messages to
    /// this consumer and drains any in-flight handler invocations. Does NOT tear
    /// down the underlying channel/connection — that happens on
    /// <see cref="IAsyncDisposable.DisposeAsync"/>. Idempotent.
    /// </summary>
    /// <remarks>
    /// The default-interface-method is a no-op so existing third-party
    /// <see cref="IConsumer"/> implementations remain source-compatible. Custom
    /// transports that want graceful shutdown semantics should override this — without
    /// an override, <c>Bus.StopConsumingAsync</c> only flips the consuming flag and
    /// the broker keeps delivering until DI disposal.
    /// </remarks>
    Task StopConsumingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

new_string:
    /// <summary>
    /// Issues a graceful stop: instructs the broker to stop delivering messages to
    /// this consumer and drains any in-flight handler invocations. Does NOT tear
    /// down the underlying channel/connection — that happens on
    /// <see cref="IAsyncDisposable.DisposeAsync"/>. Idempotent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default-interface-method is a no-op so existing third-party
    /// <see cref="IConsumer"/> implementations remain source-compatible. Custom
    /// transports that want graceful shutdown semantics should override this — without
    /// an override, <c>Bus.StopConsumingAsync</c> only flips the consuming flag and
    /// the broker keeps delivering until DI disposal.
    /// </para>
    /// <para>
    /// <b>Handler cooperation.</b> Drain semantics depend on every in-flight handler
    /// observing the <see cref="CancellationToken"/> threaded through dispatch. A
    /// handler that performs synchronous I/O or ignores its <c>CancellationToken</c>
    /// will block the drain for the full duration of that work. Implementations
    /// should bound their own drain wait by the host's graceful-shutdown grace
    /// window rather than waiting indefinitely.
    /// </para>
    /// </remarks>
    Task StopConsumingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
```

- [ ] **Step 5: Build the Interfaces project**

Delegate to a subagent:
`dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj -m:1`
Expected: `Build succeeded.` with 0 errors. (XML doc warnings would surface as build errors because `TreatWarningsAsErrors=true` in `Directory.Build.props`.)

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.Interfaces/Bus/IBus.cs src/ServiceConnect.Interfaces/Bus/IConsumer.cs
git commit -m "$(cat <<'EOF'
docs(interfaces): document handler-cooperation requirement on StopConsumingAsync

Adds an explicit <remarks> paragraph to both IBus.StopConsumingAsync and
IConsumer.StopConsumingAsync explaining that handlers which perform synchronous
I/O or ignore their CancellationToken will block the graceful-shutdown grace
window. This was the most common cause of unexplained DisposeTimeout-bounded
shutdowns; surfacing the contract in xmldoc makes the failure mode self-
diagnosable.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Add `RabbitMqOptions.Validate()` and invoke at `UseRabbitMQ` setup

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMqOptions.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQExtensions.cs:47-68`
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqOptionsValidateTests.cs`

**Why:** Bad values for `Port`, `RetryCount`, `PublishTimeout`, etc. currently surface at first connection or first publish — never at DI registration time. The core `BusConfiguration` already validates inside `AddServiceConnect`; the RabbitMQ transport options should match that fail-fast contract. The `ushort?` properties (PrefetchCount, RetrySeconds, HeartbeatTime) are non-negative by type, so they need no range check; the `int?` and `long?` and `TimeSpan?` properties need explicit checks.

- [ ] **Step 1: Write the failing test (file does not exist yet)**

Create `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqOptionsValidateTests.cs` with this content:

```csharp
using ServiceConnect.Client.RabbitMQ.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class RabbitMqOptionsValidateTests
{
    [Fact]
    public void Validate_AllDefaults_ReturnsEmpty()
    {
        var options = new RabbitMqOptions();
        var errors = options.Validate();
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_PortBelowOne_ReturnsError()
    {
        var options = new RabbitMqOptions { Port = 0 };
        var errors = options.Validate();
        Assert.Contains(errors, e => e.Contains("Port", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_PortAbove65535_ReturnsError()
    {
        var options = new RabbitMqOptions { Port = 70000 };
        var errors = options.Validate();
        Assert.Contains(errors, e => e.Contains("Port", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NegativeRetryCount_ReturnsError()
    {
        var options = new RabbitMqOptions { RetryCount = -1 };
        var errors = options.Validate();
        Assert.Contains(errors, e => e.Contains("RetryCount", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ZeroPublishTimeout_ReturnsError()
    {
        var options = new RabbitMqOptions { PublishTimeout = System.TimeSpan.Zero };
        var errors = options.Validate();
        Assert.Contains(errors, e => e.Contains("PublishTimeout", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NegativePublishTimeout_ReturnsError()
    {
        var options = new RabbitMqOptions { PublishTimeout = System.TimeSpan.FromSeconds(-1) };
        var errors = options.Validate();
        Assert.Contains(errors, e => e.Contains("PublishTimeout", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ZeroMaxOutstandingPublishConfirms_ReturnsError()
    {
        var options = new RabbitMqOptions { MaxOutstandingPublishConfirms = 0 };
        var errors = options.Validate();
        Assert.Contains(errors, e => e.Contains("MaxOutstandingPublishConfirms", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NegativeMessageSize_ReturnsError()
    {
        var options = new RabbitMqOptions { MessageSize = -1 };
        var errors = options.Validate();
        Assert.Contains(errors, e => e.Contains("MessageSize", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ZeroNetworkRecoveryInterval_ReturnsError()
    {
        var options = new RabbitMqOptions { NetworkRecoveryInterval = System.TimeSpan.Zero };
        var errors = options.Validate();
        Assert.Contains(errors, e => e.Contains("NetworkRecoveryInterval", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AggregatesMultipleErrors()
    {
        var options = new RabbitMqOptions
        {
            Port = 0,
            RetryCount = -5,
            PublishTimeout = System.TimeSpan.Zero,
        };
        var errors = options.Validate();
        Assert.Equal(3, errors.Count);
    }
}
```

- [ ] **Step 2: Run the failing test (verify it fails to compile)**

Delegate to a subagent:
`dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~RabbitMqOptionsValidateTests"`
Expected: build fails with `'RabbitMqOptions' does not contain a definition for 'Validate'`. This confirms the test is exercising the new surface.

- [ ] **Step 3: Add `Validate()` to RabbitMqOptions**

Apply this Edit to `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMqOptions.cs`:

```
old_string:
    /// <summary>Interval between auto-recovery attempts after a connection drop.</summary>
    public TimeSpan? NetworkRecoveryInterval { get; set; }
}

new_string:
    /// <summary>Interval between auto-recovery attempts after a connection drop.</summary>
    public TimeSpan? NetworkRecoveryInterval { get; set; }

    /// <summary>
    /// Validates the option values that have explicit range constraints. Properties
    /// typed as <see cref="ushort"/>? are non-negative by type and need no runtime check;
    /// this method covers the <see cref="int"/>?, <see cref="long"/>?, and
    /// <see cref="TimeSpan"/>? properties whose acceptable range cannot be expressed in
    /// the type system.
    /// </summary>
    /// <returns>
    /// A list of human-readable error messages — one per invalid property. Returns an
    /// empty list when all set values are within range. Properties left at <see langword="null"/>
    /// (i.e. not configured) are skipped; defaults are not asserted here.
    /// </returns>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (Port is { } port && (port < 1 || port > 65535))
        {
            errors.Add($"Port must be between 1 and 65535 (was {port}).");
        }

        if (RetryCount is { } retryCount && retryCount < 0)
        {
            errors.Add($"RetryCount must be non-negative (was {retryCount}).");
        }

        if (MessageSize is { } messageSize && messageSize <= 0)
        {
            errors.Add($"MessageSize must be positive (was {messageSize}).");
        }

        if (PublishTimeout is { } publishTimeout && publishTimeout <= TimeSpan.Zero)
        {
            errors.Add($"PublishTimeout must be positive (was {publishTimeout}).");
        }

        if (MaxOutstandingPublishConfirms is { } maxOutstanding && maxOutstanding <= 0)
        {
            errors.Add($"MaxOutstandingPublishConfirms must be positive (was {maxOutstanding}).");
        }

        if (NetworkRecoveryInterval is { } recoveryInterval && recoveryInterval <= TimeSpan.Zero)
        {
            errors.Add($"NetworkRecoveryInterval must be positive (was {recoveryInterval}).");
        }

        return errors;
    }
}
```

- [ ] **Step 4: Run the tests — Validate() unit tests should pass**

Delegate to a subagent:
`dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~RabbitMqOptionsValidateTests"`
Expected: 10 tests pass.

- [ ] **Step 5: Wire validation into `UseRabbitMQ(opts =>)`**

Apply this Edit to `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQExtensions.cs`:

```
old_string:
        if (configure is not null)
        {
            var options = new RabbitMqOptions();
            configure(options);

            builder.ConfigureTransport(transport => ApplyToClientSettings(transport, options));
        }

new_string:
        if (configure is not null)
        {
            var options = new RabbitMqOptions();
            configure(options);

            var validationErrors = options.Validate();
            if (validationErrors.Count > 0)
            {
                throw new ArgumentException(
                    "RabbitMqOptions contains invalid values: " + string.Join("; ", validationErrors),
                    nameof(configure));
            }

            builder.ConfigureTransport(transport => ApplyToClientSettings(transport, options));
        }
```

- [ ] **Step 6: Build the RabbitMQ project**

Delegate to a subagent:
`dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 7: Run the full unit-test suite for `RabbitMQ`**

Delegate to a subagent:
`dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~RabbitMQ"`
Expected: every existing RabbitMQ test still passes; the 10 new `Validate` tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMqOptions.cs \
        src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQExtensions.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqOptionsValidateTests.cs
git commit -m "$(cat <<'EOF'
feat(rabbitmq): validate RabbitMqOptions at UseRabbitMQ setup time

Adds RabbitMqOptions.Validate() returning a list of human-readable error
messages for the int/long/TimeSpan properties whose acceptable range cannot
be expressed in the type system (Port, RetryCount, MessageSize, PublishTimeout,
MaxOutstandingPublishConfirms, NetworkRecoveryInterval). The ushort properties
are non-negative by type and need no runtime check.

UseRabbitMQ(opts => ...) now invokes Validate() before applying the options
to ClientSettings and throws ArgumentException with the aggregated error
list if any values are out of range. This matches the fail-fast contract
already implemented by BusConfiguration.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Expose `AggregatorLeaseDuration` on `MongoDbPersistenceOptions`

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/Configuration/MongoDbPersistenceOptions.cs`
- Modify: `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs`
- Create: `src/ServiceConnect.UnitTests/Persistence/MongoDb/MongoDbPersistenceOptionsAggregatorLeaseTests.cs`

**Why:** `MongoDbPersistenceOptions` already exposes a public `TimeoutLockLeaseDuration` (TimeSpan, 5 min default). The aggregator persistor has the symmetric concept but it's only tunable via an internal-ctor `leaseDuration?` parameter intended for E2E tests; production consumers cannot adjust it without forking. Add an `AggregatorLeaseDuration` property to options and route the public ctor through it. Preserve the internal test ctor so existing E2E tests that pass an explicit override continue to work — the explicit override takes precedence over `options.AggregatorLeaseDuration`.

- [ ] **Step 1: Write the failing test (file does not exist yet)**

Create `src/ServiceConnect.UnitTests/Persistence/MongoDb/MongoDbPersistenceOptionsAggregatorLeaseTests.cs` with this content:

```csharp
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence.MongoDb;

public sealed class MongoDbPersistenceOptionsAggregatorLeaseTests
{
    [Fact]
    public void AggregatorLeaseDuration_DefaultIsFiveMinutes()
    {
        var options = new MongoDbPersistenceOptions();
        Assert.Equal(System.TimeSpan.FromMinutes(5), options.AggregatorLeaseDuration);
    }

    [Fact]
    public void AggregatorLeaseDuration_IsSettable()
    {
        var options = new MongoDbPersistenceOptions
        {
            AggregatorLeaseDuration = System.TimeSpan.FromSeconds(30),
        };
        Assert.Equal(System.TimeSpan.FromSeconds(30), options.AggregatorLeaseDuration);
    }
}
```

- [ ] **Step 2: Run the failing test (expect compile error)**

Delegate to a subagent:
`dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~MongoDbPersistenceOptionsAggregatorLeaseTests"`
Expected: build fails with `'MongoDbPersistenceOptions' does not contain a definition for 'AggregatorLeaseDuration'`.

- [ ] **Step 3: Add the `AggregatorLeaseDuration` property**

Apply this Edit to `src/ServiceConnect.Persistence.MongoDb/Configuration/MongoDbPersistenceOptions.cs`:

```
old_string:
    /// <summary>
    /// Gets or sets the lease duration applied when claiming a timeout for
    /// dispatch. Shorter leases recover faster from crashed handlers; longer
    /// leases are safer for handlers with variable dispatch latency. Must
    /// be positive.
    /// </summary>
    public TimeSpan TimeoutLockLeaseDuration { get; set; } = TimeSpan.FromMinutes(5);
}

new_string:
    /// <summary>
    /// Gets or sets the lease duration applied when claiming a timeout for
    /// dispatch. Shorter leases recover faster from crashed handlers; longer
    /// leases are safer for handlers with variable dispatch latency. Must
    /// be positive.
    /// </summary>
    public TimeSpan TimeoutLockLeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the lease duration applied when an aggregator snapshot is claimed
    /// for dispatch. A worker that crashes mid-flush holds the rows for at most this
    /// long before another worker may reclaim them. Shorter leases recover faster from
    /// crashed handlers; longer leases are safer for handlers with variable dispatch
    /// latency. Must be positive.
    /// </summary>
    public TimeSpan AggregatorLeaseDuration { get; set; } = TimeSpan.FromMinutes(5);
}
```

- [ ] **Step 4: Run the new tests — they should now pass**

Delegate to a subagent:
`dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~MongoDbPersistenceOptionsAggregatorLeaseTests"`
Expected: 2 tests pass.

- [ ] **Step 5: Route the persistor's default lease through the option**

Read: `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs` around lines 117–140 to confirm the current `_leaseDuration = leaseDuration ?? DefaultLeaseDuration;` assignment.

Apply this Edit to `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs`:

```
old_string:
        _mongoClient = mongoClient;
        _logger = logger;
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _leaseDuration = leaseDuration ?? DefaultLeaseDuration;

new_string:
        _mongoClient = mongoClient;
        _logger = logger;
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        _timeProvider = timeProvider ?? TimeProvider.System;
        // Explicit leaseDuration (test path) wins; otherwise fall back to options.AggregatorLeaseDuration
        // which itself defaults to DefaultLeaseDuration. The DefaultLeaseDuration constant remains as
        // the documented type-level default for callers reading the public API.
        var resolvedLease = leaseDuration ?? options.AggregatorLeaseDuration;
        if (resolvedLease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"MongoDbPersistenceOptions.AggregatorLeaseDuration must be strictly positive (was {resolvedLease}).");
        }
        _leaseDuration = resolvedLease;
```

Note: the existing pre-check `if (leaseDuration is { } lease && lease <= TimeSpan.Zero)` at line 128 still rejects an explicit non-positive override before we reach the assignment above. Both validations are intentional — they cover the two different param sources.

- [ ] **Step 6: Build the MongoDb persistence project**

Delegate to a subagent:
`dotnet build src/ServiceConnect.Persistence.MongoDb/ServiceConnect.Persistence.MongoDb.csproj -m:1`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 7: Run the full unit-test suite for MongoDb persistence**

Delegate to a subagent:
`dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~Persistence.MongoDb"`
Expected: all existing tests still pass; the 2 new tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect.Persistence.MongoDb/Configuration/MongoDbPersistenceOptions.cs \
        src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs \
        src/ServiceConnect.UnitTests/Persistence/MongoDb/MongoDbPersistenceOptionsAggregatorLeaseTests.cs
git commit -m "$(cat <<'EOF'
feat(persistence-mongodb): expose AggregatorLeaseDuration on MongoDbPersistenceOptions

Mirrors the existing TimeoutLockLeaseDuration knob so production consumers can
tune the aggregator's snapshot lease window without forking. Default remains
5 minutes; the internal test ctor's explicit override still takes precedence
when supplied. Validates the resolved value is strictly positive.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Counter for snapshot-remove-after-success failures

**Files:**
- Modify: `src/ServiceConnect/Diagnostics/MetricNames.cs`
- Modify: `src/ServiceConnect/Diagnostics/ServiceConnectMeter.cs`
- Modify: `src/ServiceConnect/Services/Processors/AggregatorProcessor.cs:412-438`
- Create: `src/ServiceConnect.UnitTests/Aggregator/AggregatorProcessorSnapshotRemoveCounterTests.cs`

**Why:** When an aggregator handler succeeds but `RemoveSnapshotAsync` fails (a Mongo blip mid-flight), the framework intentionally swallows the persistor failure to avoid re-running the handler via broker NACK — the rows stay leased until the TTL expires and a peer may then re-claim and re-dispatch. This is a correct at-least-once trade-off, but today it is observable only via log scraping. A counter on `ServiceConnectMeter` turns it into an alert-able signal.

- [ ] **Step 1: Write the failing test (file does not exist yet)**

Create `src/ServiceConnect.UnitTests/Aggregator/AggregatorProcessorSnapshotRemoveCounterTests.cs` with this content:

```csharp
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Diagnostics;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Aggregator;

public sealed class AggregatorProcessorSnapshotRemoveCounterTests
{
    [Fact]
    public async Task SnapshotRemoveFailureAfterDispatch_IncrementsCounter()
    {
        using var collector = new MetricCollector<long>(
            null,
            ServiceConnectMeter.MeterName,
            MetricNames.SnapshotRemoveFailedAfterDispatch);

        var persistor = new Mock<IAggregatorPersistor>(MockBehavior.Strict);
        var registry = new AggregatorRegistry();
        // Register a no-op aggregator descriptor for TestAggregateMessage so ProcessAsync
        // proceeds past the registry check.
        registry.Register(typeof(TestAggregateMessage),
            new AggregatorDescriptor(
                aggregatorName: "test-agg",
                aggregatorBaseType: typeof(object),
                buildTypedList: items => items,
                invokeExecuteAsync: (_, _, _) => Task.CompletedTask,
                batchSize: 1,
                timeout: System.TimeSpan.Zero));

        var snapshot = new Mock<IAggregatorSnapshot>();
        snapshot.SetupGet(s => s.ResolvedMessages)
                .Returns(new System.Collections.Generic.List<IHasCorrelationId> { new TestAggregateMessage() });
        snapshot.SetupGet(s => s.UnresolvedCount).Returns(0);

        persistor.Setup(p => p.InsertDataAsync(It.IsAny<IHasCorrelationId>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()))
                 .Returns(Task.CompletedTask);
        persistor.Setup(p => p.CountResolvedAsync(It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()))
                 .ReturnsAsync(1L);
        persistor.Setup(p => p.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()))
                 .ReturnsAsync(snapshot.Object);
        persistor.Setup(p => p.RemoveSnapshotAsync(It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<System.Threading.CancellationToken>()))
                 .ThrowsAsync(new System.InvalidOperationException("simulated mongo blip"));
        persistor.Setup(p => p.ReleaseSnapshotAsync(It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<System.Threading.CancellationToken>()))
                 .Returns(Task.CompletedTask);

        var services = new ServiceCollection().BuildServiceProvider();
        var scopeAccessor = new ConsumeScopeAccessor();
        using var rootScope = scopeAccessor.Push(services);

        var processor = new AggregatorProcessor(
            registry: registry,
            scopeAccessor: scopeAccessor,
            scopeFactory: services.GetRequiredService<IServiceScopeFactory>(),
            logger: NullLogger<AggregatorProcessor>.Instance,
            persistor: persistor.Object);

        await processor.ProcessAsync(
            messageBytes: System.ReadOnlyMemory<byte>.Empty,
            messageType: typeof(TestAggregateMessage),
            message: new TestAggregateMessage(),
            headers: new System.Collections.Generic.Dictionary<string, object>(System.StringComparer.Ordinal),
            envelope: new Envelope { Body = System.ReadOnlyMemory<byte>.Empty, Headers = new System.Collections.Generic.Dictionary<string, object>(System.StringComparer.Ordinal) },
            cancellationToken: default);

        // The snapshot-remove failure path swallows the exception by design and increments
        // the counter — verify the counter was hit exactly once.
        var measurements = collector.GetMeasurementSnapshot();
        Assert.Single(measurements);
        Assert.Equal(1L, measurements[0].Value);
    }

    private sealed class TestAggregateMessage : Message, IHasCorrelationId
    {
        public TestAggregateMessage() : base(System.Guid.NewGuid()) { }
    }
}
```

**Implementation note for the agent executing this:** the test references `AggregatorRegistry.Register` and `AggregatorDescriptor` constructor shapes that may not match the production signatures exactly — confirm by reading `src/ServiceConnect/Services/Processors/AggregatorRegistry.cs` and `AggregatorDescriptor`. Adjust the test's setup-helper portion (registry + descriptor construction + snapshot mock) to match the real shapes before running. The assertion (`measurements.Count == 1 && measurements[0].Value == 1`) is the load-bearing part of the test and should not change.

- [ ] **Step 2: Run the failing test (expect compile error referencing missing const)**

Delegate to a subagent:
`dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~AggregatorProcessorSnapshotRemoveCounterTests"`
Expected: build fails with `'MetricNames' does not contain a definition for 'SnapshotRemoveFailedAfterDispatch'`. This confirms the test is exercising the new metric.

- [ ] **Step 3: Add the metric name constant**

Read: `src/ServiceConnect/Diagnostics/MetricNames.cs` to locate the last `public const string` entry.

Apply this Edit to `src/ServiceConnect/Diagnostics/MetricNames.cs` — the `old_string` should be the closing brace `}` of the `MetricNames` static class plus the preceding constant (you'll need to read the file to confirm which is last; if `OutgoingFiltersBlocked` is the last entry, use the snippet below; otherwise insert before the closing brace):

```
old_string:
    public const string OutgoingFiltersBlocked = "messaging.serviceconnect.outgoing_filters.blocked";
}

new_string:
    public const string OutgoingFiltersBlocked = "messaging.serviceconnect.outgoing_filters.blocked";

    /// <summary>
    /// Counter incremented when an aggregator handler succeeds but the subsequent
    /// <c>RemoveSnapshotAsync</c> call fails. The framework intentionally swallows the
    /// remove failure to avoid re-running the handler via broker NACK; the rows remain
    /// leased until the lease expires and a peer may then re-claim and re-dispatch
    /// (the at-least-once trade-off). A spike on this counter translates directly into
    /// duplicate handler invocations after the lease expires.
    /// </summary>
    public const string SnapshotRemoveFailedAfterDispatch = "messaging.serviceconnect.aggregator.snapshot_remove_failed_after_dispatch";
}
```

- [ ] **Step 4: Add the counter and accessor to ServiceConnectMeter**

Apply this Edit to `src/ServiceConnect/Diagnostics/ServiceConnectMeter.cs`:

```
old_string:
    private static readonly Counter<long> _outgoingFiltersBlocked = _meter.CreateCounter<long>(
        name: MetricNames.OutgoingFiltersBlocked,
        unit: "{message}",
        description: "Outgoing operations aborted because an outgoing filter returned FilterAction.Stop.");

new_string:
    private static readonly Counter<long> _outgoingFiltersBlocked = _meter.CreateCounter<long>(
        name: MetricNames.OutgoingFiltersBlocked,
        unit: "{message}",
        description: "Outgoing operations aborted because an outgoing filter returned FilterAction.Stop.");

    private static readonly Counter<long> _snapshotRemoveFailedAfterDispatch = _meter.CreateCounter<long>(
        name: MetricNames.SnapshotRemoveFailedAfterDispatch,
        unit: "{failure}",
        description: "Aggregator snapshot-remove failures after a successful handler dispatch — invisible at-least-once window.");
```

Then add the public `Add…` method by editing the existing block of accessors. Apply this Edit:

```
old_string:
    /// <summary>Increments the outgoing-filters-blocked counter by 1 with the given tags.</summary>
    public static void AddOutgoingFiltersBlocked(in TagList tags) => _outgoingFiltersBlocked.Add(1, tags);

new_string:
    /// <summary>Increments the outgoing-filters-blocked counter by 1 with the given tags.</summary>
    public static void AddOutgoingFiltersBlocked(in TagList tags) => _outgoingFiltersBlocked.Add(1, tags);

    /// <summary>Increments the snapshot-remove-after-dispatch failure counter by 1 with the given tags.</summary>
    public static void AddSnapshotRemoveFailedAfterDispatch(in TagList tags) => _snapshotRemoveFailedAfterDispatch.Add(1, tags);
```

- [ ] **Step 5: Wire the increment into AggregatorProcessor**

Read: `src/ServiceConnect/Services/Processors/AggregatorProcessor.cs` lines 425–445 to confirm the `catch (Exception removeEx)` block matches the `old_string` below.

Apply this Edit to `src/ServiceConnect/Services/Processors/AggregatorProcessor.cs`:

```
old_string:
            catch (Exception removeEx)
            {
                logger.LogWarning(removeEx,
                    "Aggregator {AggregatorName} RemoveSnapshotAsync failed after successful handler dispatch; rows remain under lease and will be reclaimed when the lease expires. Handler side effects are NOT replayed by NACKing the broker.",
                    descriptor.AggregatorName);
            }

new_string:
            catch (Exception removeEx)
            {
                logger.LogWarning(removeEx,
                    "Aggregator {AggregatorName} RemoveSnapshotAsync failed after successful handler dispatch; rows remain under lease and will be reclaimed when the lease expires. Handler side effects are NOT replayed by NACKing the broker.",
                    descriptor.AggregatorName);
                var tags = new System.Diagnostics.TagList
                {
                    { "messaging.system", "serviceconnect" },
                    { "aggregator.name", descriptor.AggregatorName },
                };
                ServiceConnectMeter.AddSnapshotRemoveFailedAfterDispatch(tags);
            }
```

- [ ] **Step 6: Build the core project**

Delegate to a subagent:
`dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 7: Run the snapshot-remove counter test**

Delegate to a subagent:
`dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~AggregatorProcessorSnapshotRemoveCounterTests"`
Expected: 1 test passes. If the test's setup helpers (registry/descriptor shape) don't match production, fix the test's setup-only code (the assertion is correct) and re-run.

- [ ] **Step 8: Run the full aggregator unit-test suite to confirm no regressions**

Delegate to a subagent:
`dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~Aggregator"`
Expected: every existing aggregator test still passes; the new counter test passes.

- [ ] **Step 9: Commit**

```bash
git add src/ServiceConnect/Diagnostics/MetricNames.cs \
        src/ServiceConnect/Diagnostics/ServiceConnectMeter.cs \
        src/ServiceConnect/Services/Processors/AggregatorProcessor.cs \
        src/ServiceConnect.UnitTests/Aggregator/AggregatorProcessorSnapshotRemoveCounterTests.cs
git commit -m "$(cat <<'EOF'
feat(diagnostics): emit counter when aggregator snapshot remove fails after success

When an aggregator handler succeeds but RemoveSnapshotAsync fails, the framework
intentionally swallows the persistor failure so the handler is not re-run via
broker NACK. The rows remain under the current lease and may be re-claimed by
a peer after the lease expires (at-least-once trade-off).

This was observable only via log scraping. Adds a counter
`messaging.serviceconnect.aggregator.snapshot_remove_failed_after_dispatch` so
operators can alert on the invisible duplicate-dispatch window without parsing
logs. Tagged with the aggregator name for per-aggregator drill-down.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Fail at host start when no `IProducer` is registered

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs`
- Modify: `src/ServiceConnect/Configuration/BusConfiguration.cs`
- Modify: `src/ServiceConnect/Services/BusHostedService.cs`
- Create: `src/ServiceConnect.UnitTests/Bus/BusHostedServiceMissingProducerTests.cs`

**Why:** `IConsumer` missing is already caught at host start by `Bus.StartConsumingAsync`, which throws a clear "No consumer registered…" error. `IProducer` missing currently surfaces only at first `Publish`/`Send`/`CreateStream` — minutes or hours into production. Move the producer check to host start so the failure mode is "host start" instead of "first message". Make it opt-out (default-on enforcement) via a new `AllowMissingProducer` flag on `BusConfiguration` for the rare test bus that legitimately runs without a producer.

**Naming note:** the property is `AllowMissingProducer` not `AllowNoTransport`, because (a) the consumer case is already handled by `StartConsumingAsync` and (b) a more specific name reads better at the call site.

- [ ] **Step 1: Write the failing test (file does not exist yet)**

Create `src/ServiceConnect.UnitTests/Bus/BusHostedServiceMissingProducerTests.cs` with this content:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Bus;

public sealed class BusHostedServiceMissingProducerTests
{
    [Fact]
    public async Task StartAsync_NoProducer_DefaultAllowMissingProducer_Throws()
    {
        var bus = new Mock<IBus>(MockBehavior.Strict);
        bus.Setup(b => b.StartConsumingAsync(It.IsAny<System.Threading.CancellationToken>()))
           .Returns(System.Threading.Tasks.Task.CompletedTask);

        var config = new BusConfiguration { AutoStartConsuming = false };
        var transport = new TransportConfiguration();

        var hostedService = new BusHostedService(
            bus.Object,
            config,
            transport,
            NullLogger<BusHostedService>.Instance,
            scanWarnings: null,
            producer: null);

        var ex = await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => hostedService.StartAsync(System.Threading.CancellationToken.None));

        Assert.Contains("IProducer", ex.Message, System.StringComparison.Ordinal);
        Assert.Contains("AllowMissingProducer", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_NoProducer_AllowMissingProducerTrue_Succeeds()
    {
        var bus = new Mock<IBus>(MockBehavior.Loose);
        bus.Setup(b => b.StartConsumingAsync(It.IsAny<System.Threading.CancellationToken>()))
           .Returns(System.Threading.Tasks.Task.CompletedTask);

        var config = new BusConfiguration { AutoStartConsuming = false, AllowMissingProducer = true };
        var transport = new TransportConfiguration();

        var hostedService = new BusHostedService(
            bus.Object,
            config,
            transport,
            NullLogger<BusHostedService>.Instance,
            scanWarnings: null,
            producer: null);

        // Must not throw.
        await hostedService.StartAsync(System.Threading.CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ProducerPresent_DefaultFlags_Succeeds()
    {
        var bus = new Mock<IBus>(MockBehavior.Loose);
        bus.Setup(b => b.StartConsumingAsync(It.IsAny<System.Threading.CancellationToken>()))
           .Returns(System.Threading.Tasks.Task.CompletedTask);

        var producer = new Mock<IProducer>().Object;
        var config = new BusConfiguration { AutoStartConsuming = false };
        var transport = new TransportConfiguration();

        var hostedService = new BusHostedService(
            bus.Object,
            config,
            transport,
            NullLogger<BusHostedService>.Instance,
            scanWarnings: null,
            producer: producer);

        await hostedService.StartAsync(System.Threading.CancellationToken.None);
    }
}
```

**Implementation note:** the test sets `AutoStartConsuming = false` so the test does not attempt to start consumption via the strict-mode `IBus` mock — the producer check should run *before* the `AutoStartConsuming` branch, which is what we implement in Step 5.

- [ ] **Step 2: Run the failing test (expect compile error)**

Delegate to a subagent:
`dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~BusHostedServiceMissingProducerTests"`
Expected: build fails with `BusHostedService` constructor signature mismatch and `BusConfiguration` does not contain `AllowMissingProducer`. This confirms the test exercises the new surface.

- [ ] **Step 3: Add `AllowMissingProducer` to `IBusConfiguration`**

Read: `src/ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs` to locate the last property (around `DisposeTimeout` at line 142).

Apply this Edit to `src/ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs`. The `old_string` should include the closing `}` of the interface. Confirm by reading the file first:

```
old_string:
    TimeSpan DisposeTimeout { get; set; }
}

new_string:
    TimeSpan DisposeTimeout { get; set; }

    /// <summary>
    /// Gets or sets whether the host is allowed to start without an <see cref="IProducer"/>
    /// registered. Defaults to <see langword="false"/>: <c>BusHostedService.StartAsync</c>
    /// throws <see cref="InvalidOperationException"/> at host start if no <see cref="IProducer"/>
    /// has been registered, surfacing the missing transport at host build time rather than
    /// at the first publish/send/<c>CreateStream</c> call. Set to <see langword="true"/> only
    /// in tests or specialised in-memory scenarios that legitimately operate without a
    /// producer (consume-only buses).
    /// </summary>
    bool AllowMissingProducer { get; set; }
}
```

- [ ] **Step 4: Add `AllowMissingProducer` to `BusConfiguration`**

Apply this Edit to `src/ServiceConnect/Configuration/BusConfiguration.cs`. There are two anchor points — the backing field block and the property block.

First Edit (backing field, near line 31):

```
old_string:
    private TimeSpan _disposeTimeout = TimeSpan.FromSeconds(30);

new_string:
    private TimeSpan _disposeTimeout = TimeSpan.FromSeconds(30);
    private bool _allowMissingProducer;
```

Second Edit (property, near line 58):

```
old_string:
    /// <inheritdoc />
    public TimeSpan DisposeTimeout { get => _disposeTimeout; set { ThrowIfFrozen(); _disposeTimeout = value; } }

new_string:
    /// <inheritdoc />
    public TimeSpan DisposeTimeout { get => _disposeTimeout; set { ThrowIfFrozen(); _disposeTimeout = value; } }
    /// <inheritdoc />
    public bool AllowMissingProducer { get => _allowMissingProducer; set { ThrowIfFrozen(); _allowMissingProducer = value; } }
```

- [ ] **Step 5: Add the producer check to `BusHostedService.StartAsync`**

Apply this Edit to `src/ServiceConnect/Services/BusHostedService.cs`. First update the primary constructor signature:

```
old_string:
internal sealed class BusHostedService(
    IBus bus,
    IBusConfiguration config,
    ITransportConfiguration transport,
    ILogger<BusHostedService> logger,
    IReadOnlyList<HandlerScanWarning>? scanWarnings = null) : IHostedService

new_string:
internal sealed class BusHostedService(
    IBus bus,
    IBusConfiguration config,
    ITransportConfiguration transport,
    ILogger<BusHostedService> logger,
    IReadOnlyList<HandlerScanWarning>? scanWarnings = null,
    IProducer? producer = null) : IHostedService
```

Then add the validation early in `StartAsync` (before the scan-warning replay so the failure surfaces immediately):

```
old_string:
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Replay handler-scan warnings captured before the logger was available.
        // A broken handler assembly that survives the scan with no warning shows up
        // only when a message arrives with no registered handler — surfacing the
        // partial-scan warning here turns silent under-discovery into a startup log.
        if (scanWarnings is { Count: > 0 })

new_string:
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Fail fast at host start if no producer was registered. The IConsumer-missing
        // case is already caught further down by bus.StartConsumingAsync; the producer-
        // missing case used to surface only at first Publish/Send/CreateStream, which
        // delayed the operator signal from host build to first message dispatch.
        if (producer is null && !config.AllowMissingProducer)
        {
            throw new InvalidOperationException(
                "No IProducer is registered. Call UseRabbitMQ() (or another transport extension) before " +
                "the host is built, or set BusConfiguration.AllowMissingProducer = true if this is an " +
                "intentional consume-only or in-memory test bus.");
        }

        // Replay handler-scan warnings captured before the logger was available.
        // A broken handler assembly that survives the scan with no warning shows up
        // only when a message arrives with no registered handler — surfacing the
        // partial-scan warning here turns silent under-discovery into a startup log.
        if (scanWarnings is { Count: > 0 })
```

- [ ] **Step 6: Run the missing-producer tests — they should now pass**

Delegate to a subagent:
`dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~BusHostedServiceMissingProducerTests"`
Expected: 3 tests pass.

- [ ] **Step 7: Build the core project + interfaces**

Delegate to a subagent (sequentially since Interfaces is a dependency):
`dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj -m:1 && dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1`
Expected: both projects build with 0 errors.

- [ ] **Step 8: Run the full Bus/hosted-service unit-test suite to catch regressions**

Delegate to a subagent:
`dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~BusHostedService|FullyQualifiedName~Bus."`
Expected: all existing tests still pass. If any existing test constructs `BusHostedService` directly without producer and starts it, it will now throw — that test must be updated to set `AllowMissingProducer = true` on its `BusConfiguration` or pass a producer mock. Fix any such test in place; do not remove assertions.

- [ ] **Step 9: Check the E2E test suite for any direct `BusHostedService` constructions**

Delegate to a subagent:
`grep -rn "new BusHostedService" src/ServiceConnect.EndToEndTests src/ServiceConnect.UnitTests 2>/dev/null | grep -v '/bin/\|/obj/'`
For each occurrence, confirm either (a) it passes a real `IProducer` (e.g. via Testcontainers RabbitMQ setup) or (b) it sets `AllowMissingProducer = true`. Document any sites that need updating; fix them in this same commit.

- [ ] **Step 10: Build the EndToEndTests project (to make sure the property addition didn't break it)**

Delegate to a subagent:
`dotnet build src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj -m:1`
Expected: build succeeds.

- [ ] **Step 11: Commit**

```bash
git add src/ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs \
        src/ServiceConnect/Configuration/BusConfiguration.cs \
        src/ServiceConnect/Services/BusHostedService.cs \
        src/ServiceConnect.UnitTests/Bus/BusHostedServiceMissingProducerTests.cs
# If any other test file was updated to set AllowMissingProducer, add it here too.
git commit -m "$(cat <<'EOF'
feat(bus): fail at host start when no IProducer is registered

A user who calls AddServiceConnect but forgets UseRabbitMQ used to see no
error until the first Publish/Send/CreateStream — minutes or hours into
production. BusHostedService.StartAsync now checks for an injected
IProducer and throws an explicit InvalidOperationException pointing at the
likely fix (call UseRabbitMQ()).

Adds BusConfiguration.AllowMissingProducer (default false) as an opt-out
for consume-only test buses. The IConsumer-missing case already fails at
host start via Bus.StartConsumingAsync; this commit closes the symmetric
gap on the producer side.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Final smoke test (after all six tasks)

After every task is committed, run one full sweep to confirm nothing slipped through cross-project dependencies.

- [ ] **Step F1: Per-project build sweep**

Delegate to a subagent. Run sequentially (later projects depend on earlier):

```
dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj -m:1 \
 && dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1 \
 && dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1 \
 && dotnet build src/ServiceConnect.Persistence.MongoDb/ServiceConnect.Persistence.MongoDb.csproj -m:1 \
 && dotnet build src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj -m:1 \
 && dotnet build src/ServiceConnect.Telemetry/ServiceConnect.Telemetry.csproj -m:1 \
 && dotnet build src/ServiceConnect.HealthChecks/ServiceConnect.HealthChecks.csproj -m:1
```

Expected: every project builds with 0 errors.

- [ ] **Step F2: Unit-test sweep**

Delegate to a subagent:
`dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1`
Expected: all unit tests pass. If any pre-existing test fails because of the `BusHostedService` constructor signature change or `BusConfiguration.AllowMissingProducer` enforcement, fix it as part of Task 6 (it should have been caught at Task 6 Step 8, but the full sweep is the safety net).

- [ ] **Step F3: SerializationCompatTests build**

Delegate to a subagent:
`dotnet build src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj -m:1`
Expected: build succeeds (this confirms the Task 1 csproj edit doesn't accidentally affect compilation).

- [ ] **Step F4: Confirm the commit log**

```bash
git log --oneline -10
```

Expected: six new commits, each with a clear conventional-commits prefix (`chore`, `docs`, `feat`).
