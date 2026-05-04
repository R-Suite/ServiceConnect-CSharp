# Group D — Security Defaults Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make ServiceConnect's transport security secure-by-default for v8 — flip the `SslEnabled` default to `true`, warn loudly when an operator opts out of TLS for a non-loopback broker, and bound RabbitMQ.Client v7's outstanding-publisher-confirm tracker.

**Architecture:** Five atomic commits on the existing `v7-clean-architecture` branch, in this order: (1) investigation-led publisher-confirm tracker bound, (2) `SslEnabled` default flip with all examples + E2E tests + unit-test default-assertion updated in one diff, (3) source-generated `Warning`-level log when plaintext is configured against a non-loopback host (emit point: `ConnectionFactoryBuilder.Build`, threaded an `ILogger` from the existing call sites that already have one), (4) website docs covering the v8 TLS posture, (5) roadmap close-out.

**Tech Stack:** .NET 8/10, C# 14, RabbitMQ.Client 7.2.1, `Microsoft.Extensions.Logging.Abstractions` 9.0.0 (already present in the RabbitMQ client project), `System.Threading.RateLimiting` (built into the runtime, no new package), `Microsoft.Extensions.Diagnostics.Testing` 9.0.0 (`FakeLogger` — already added in Group C), Astro / Starlight (website), xUnit + Moq.

---

## File structure

| File | Item | Action |
|---|---|---|
| `src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs` | 3 | Modify — wire `OutstandingPublisherConfirmationsRateLimiter` in `CreateChannelOptions` |
| `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQSettingKeys.cs` | 3 | Modify — add `MaxOutstandingPublishConfirms` constant |
| `src/ServiceConnect.UnitTests/.../OutstandingPublisherConfirmationsTrackerTests.cs` | 3 | Create — verify limiter wired |
| `src/ServiceConnect/Configuration/TransportConfiguration.cs` | 2 | Modify — `SslEnabled = true` default + XML doc rewrite |
| `src/ServiceConnect.Interfaces/Configuration/ITransportConfiguration.cs` | 2 | Modify — interface XML doc rewrite |
| `src/ServiceConnect.UnitTests/TransportConfigurationTests.cs` | 2 | Modify — invert default-value test |
| `examples/**/Program.cs` (13 example projects) | 2 | Modify — add `transport.SslEnabled = false` to each `UseRabbitMQ` block |
| `src/ServiceConnect.EndToEndTests/**/*.cs` (every test with `ConfigureTransport`) | 2 | Modify — add `t.SslEnabled = false` to each block |
| `src/ServiceConnect.Client.RabbitMQ/RabbitMqClientLog.cs` | 1 | Create — source-gen logger with `PlaintextOnNonLoopbackHost` event (id 1, level Warning) |
| `src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs` | 1 | Modify — add `ILogger? logger = null` parameter; emit warning when `SslEnabled == false` and any host entry is non-loopback |
| `src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs:57` | 1 | Modify — pass `logger` to `ConnectionFactoryBuilder.Build` |
| `src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs:227` | 1 | Modify — pass `_logger` to `ConnectionFactoryBuilder.Build` |
| `src/ServiceConnect.UnitTests/.../ConnectionFactoryBuilderWarningTests.cs` | 1 | Create — five-case warning test |
| `website/src/content/docs/learn/operations/configuration.mdx:44-55` | 6 | Modify — TLS section rewrite |
| `website/src/content/docs/learn/getting-started.mdx` | 6 | Modify — spot-fix any plaintext snippet |
| `website/src/content/docs/releases.mdx` v8 highlights | 6 | Modify — new "Breaking — TLS is on by default" subsection |
| `architecture-fix-plan.md` | close | Modify — mark Group D done |

---

## Task 1: Publisher-confirm tracker bound (Item 3)

Investigation-first. Read RabbitMQ.Client v7.2.1 source for the `BasicPublishAsync` path when `publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true` and `OutstandingPublisherConfirmationsRateLimiter == null`. Either the tracker is unbounded (expected: ~80% confidence) and we add a `ConcurrencyLimiter`, or there's an implicit bound and the commit is documentation-only.

**Files:**
- Read: NuGet package `RabbitMQ.Client` 7.2.1 (or [github.com/rabbitmq/rabbitmq-dotnet-client](https://github.com/rabbitmq/rabbitmq-dotnet-client/tree/v7.2.1)).
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs:248`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQSettingKeys.cs`
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/OutstandingPublisherConfirmationsTrackerTests.cs` (or alongside existing producer tests)

- [ ] **Step 1: Read RabbitMQ.Client v7.2.1 source for the confirm-tracker behaviour**

Find the v7.2.1 source. Easiest paths in order: NuGet cache (`~/.nuget/packages/rabbitmq.client/7.2.1/`), GitHub tag `v7.2.1`, or decompile via `ildasm` / `ilspycmd` if needed.

Trace the path:
- `RabbitMQ.Client.IConnection.CreateChannelAsync(CreateChannelOptions, CancellationToken)` → channel impl with `publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true`.
- The channel implementation type (likely `Channel` or `AutorecoveringChannel`) wraps publishes with a confirm tracker. Find the field that holds the in-flight confirms (usually `Dictionary<ulong, …>` or similar keyed by delivery tag).
- Confirm whether anything bounds that field's growth when `OutstandingPublisherConfirmationsRateLimiter` is null. Typical signals of "unbounded": no `Count` check before adding; no semaphore wait on add; no eviction on stall.

Record findings inline as a paragraph (~5 lines) in the commit message of the next step.

- [ ] **Step 2: If unbounded — add the `MaxOutstandingPublishConfirms` constant**

Open `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQSettingKeys.cs`. Add a new constant alongside the existing keys:

```csharp
/// <summary>
/// Maximum outstanding publisher-confirms per producer channel before publishes back-pressure.
/// Without this cap, a stalled broker can let the RabbitMQ.Client tracker grow unboundedly.
/// Tunable via <c>SetClientSetting("MaxOutstandingPublishConfirms", N)</c>; defaults to 256.
/// </summary>
public const string MaxOutstandingPublishConfirms = nameof(MaxOutstandingPublishConfirms);
```

If the investigation found the tracker is already bounded, skip Steps 2-5 and go straight to Step 6 (documentation-only commit).

- [ ] **Step 3: Wire the `ConcurrencyLimiter` into the channel options**

Open `src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs`. Find the existing `_publisherAcks` branch around line 246-251:

```csharp
if (_publisherAcks)
{
    var channelOptions = new CreateChannelOptions(
        publisherConfirmationsEnabled: true,
        publisherConfirmationTrackingEnabled: true);
    model = await connection.CreateChannelAsync(channelOptions, cancellationToken).ConfigureAwait(false);
}
```

Replace with:

```csharp
if (_publisherAcks)
{
    var permitLimit = ResolveMaxOutstandingPublishConfirms(_transportConfiguration);
    var rateLimiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
    {
        PermitLimit = permitLimit,
        QueueLimit = int.MaxValue,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    });
    var channelOptions = new CreateChannelOptions(
        publisherConfirmationsEnabled: true,
        publisherConfirmationTrackingEnabled: true,
        outstandingPublisherConfirmationsRateLimiter: rateLimiter);
    model = await connection.CreateChannelAsync(channelOptions, cancellationToken).ConfigureAwait(false);
}
```

Add the helper method at the bottom of the class:

```csharp
private const int DefaultMaxOutstandingPublishConfirms = 256;

private static int ResolveMaxOutstandingPublishConfirms(ITransportConfiguration transport)
{
    if (transport.ClientSettings.TryGetValue(RabbitMQSettingKeys.MaxOutstandingPublishConfirms, out var raw)
        && raw is int permits && permits > 0)
    {
        return permits;
    }
    return DefaultMaxOutstandingPublishConfirms;
}
```

Add `using System.Threading.RateLimiting;` at the top of the file alongside the existing `using` statements. The runtime already ships this namespace; no package reference needed.

- [ ] **Step 4: Write the test**

Producer-related tests live in `src/ServiceConnect.UnitTests/RabbitMQ/` (existing files: `ProducerEnsureConnectedTests.cs`, `ProducerConnectionDisposeTests.cs`, `ConnectionFactoryBuilderConversionErrorTests.cs`). Place the new test there.

Create `src/ServiceConnect.UnitTests/RabbitMQ/OutstandingPublisherConfirmationsTrackerTests.cs`:

```csharp
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Client.RabbitMQ.Producer;
using ServiceConnect.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public class OutstandingPublisherConfirmationsTrackerTests
{
    [Fact]
    public void ConcurrencyLimiter_DefaultsTo256Permits_WhenSettingUnset()
    {
        var transport = new TransportConfiguration();
        transport.SetClientSetting(RabbitMQSettingKeys.PublisherAcknowledgements, true);

        var limiter = ProducerConnectionTestAccess.BuildPermitLimiter(transport);

        Assert.NotNull(limiter);
        var concurrency = Assert.IsType<ConcurrencyLimiter>(limiter);
        Assert.Equal(256, concurrency.GetStatistics()!.CurrentAvailablePermits);
    }

    [Fact]
    public void ConcurrencyLimiter_RespectsExplicitMaxOutstandingPublishConfirms()
    {
        var transport = new TransportConfiguration();
        transport.SetClientSetting(RabbitMQSettingKeys.PublisherAcknowledgements, true);
        transport.SetClientSetting(RabbitMQSettingKeys.MaxOutstandingPublishConfirms, 16);

        var limiter = ProducerConnectionTestAccess.BuildPermitLimiter(transport);

        var concurrency = Assert.IsType<ConcurrencyLimiter>(limiter);
        Assert.Equal(16, concurrency.GetStatistics()!.CurrentAvailablePermits);
    }
}
```

The test calls a small `internal` test access surface on `ProducerConnection`. Add it to `ProducerConnection.cs`:

```csharp
internal static class ProducerConnectionTestAccess
{
    public static RateLimiter BuildPermitLimiter(ITransportConfiguration transport)
    {
        var permitLimit = ResolveMaxOutstandingPublishConfirms(transport);
        return new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = permitLimit,
            QueueLimit = int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    }
}
```

Make `ResolveMaxOutstandingPublishConfirms` `internal` (instead of `private`) so the helper can call it. The `[InternalsVisibleTo("ServiceConnect.UnitTests")]` already declared in `ServiceConnect.Client.RabbitMQ.csproj` (verified earlier in Group C work) gives the test access.

- [ ] **Step 5: Run the test and confirm pass**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~OutstandingPublisherConfirmationsTrackerTests" -m:1`
Expected: 2 passed.

- [ ] **Step 6: Commit**

```bash
# If unbounded path:
git add src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs \
        src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQSettingKeys.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/OutstandingPublisherConfirmationsTrackerTests.cs

git commit -m "$(cat <<'EOF'
fix(transport)!: bound the publisher-confirm tracker

RabbitMQ.Client v7.2.1's outstanding-confirms tracker is unbounded
when OutstandingPublisherConfirmationsRateLimiter is null. A stalled
broker can let the tracker grow until memory or PublishTimeout (30s)
trips the channel reset. ProducerConnection now configures a
ConcurrencyLimiter (System.Threading.RateLimiting, runtime built-in)
at default permit limit 256, with QueueLimit=int.MaxValue so the
publisher back-pressures rather than errors when the cap is reached.

Operators tune via
SetClientSetting("MaxOutstandingPublishConfirms", N).

Investigation: traced BasicPublishAsync in
RabbitMQ.Client/Impl/Channel.cs; the publish-confirm tracker writes
to a Dictionary<ulong, …> keyed by delivery tag, gated only on the
provided rate limiter. With no limiter, growth is bounded only by
the per-publish PublishTimeout (30s default), which translates to
"unbounded times concurrent publish rate" in the worst case.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"

# If bounded-already path (documentation-only):
# Add a comment above the existing CreateChannelOptions construction
# documenting the bound, then commit with a "docs(transport)" message
# explaining what the investigation found.
```

If the investigation outcome was "already bounded," replace the entire commit body with a short paragraph explaining the implicit bound (e.g. "v7.2.1's `Channel.cs` line N caps the dictionary at M entries via the dispatcher's queue limit"). Skip Steps 2-5 entirely.

---

## Task 2: `SslEnabled` defaults to `true` (Item 2)

The flip plus the entire blast radius (examples + E2E tests + unit-test default-assertion) lands as one atomic commit. Reviewer sees the breaking-change footprint in one diff.

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Configuration/ITransportConfiguration.cs:48-51`
- Modify: `src/ServiceConnect/Configuration/TransportConfiguration.cs:40-41`
- Modify: `src/ServiceConnect.UnitTests/TransportConfigurationTests.cs` (the `DefaultSslEnabledIsFalse` test)
- Modify: every `examples/**/Program.cs` that has a `UseRabbitMQ(transport => …)` block
- Modify: every `src/ServiceConnect.EndToEndTests/**/*.cs` that has a `ConfigureTransport(t => …)` block

- [ ] **Step 1: Update the interface XML doc**

Open `src/ServiceConnect.Interfaces/Configuration/ITransportConfiguration.cs`. Find the existing `<summary>` on `SslEnabled` (around lines 48-51):

```csharp
/// <summary>
/// Gets or sets a value indicating whether TLS is enabled.
/// </summary>
bool SslEnabled { get; set; }
```

Replace with:

```csharp
/// <summary>
/// Gets or sets a value indicating whether TLS is enabled.
/// </summary>
/// <remarks>
/// <b>Defaults to <see langword="true"/> as of v8.</b> The framework connects to the broker over
/// TLS on port 5671 by default. To connect to a plaintext broker (e.g. a local RabbitMQ in
/// Docker without TLS configured), set this to <see langword="false"/>; the framework logs a
/// <see cref="Microsoft.Extensions.Logging.LogLevel.Warning"/> when this is disabled against
/// a non-loopback host.
/// </remarks>
bool SslEnabled { get; set; }
```

Add `using Microsoft.Extensions.Logging;` at the top if it isn't already there. (The interface project already references `Microsoft.Extensions.Logging.Abstractions` for the source-gen logger work in Task 3 — wait, this is the Interfaces project not the Client.RabbitMQ project. Check whether the Interfaces project already has the package; if not, the `<see cref="Microsoft.Extensions.Logging.LogLevel.Warning"/>` reference will fail. Verify with `grep "Microsoft.Extensions.Logging" src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj`. If the package is not present, change the `<see cref>` to plain text `<c>Warning</c>` to avoid adding a runtime dependency to the interfaces package just for an XML-doc reference.)

- [ ] **Step 2: Update the implementation default + XML doc**

Open `src/ServiceConnect/Configuration/TransportConfiguration.cs`. Find lines 40-41:

```csharp
    /// <remarks>Defaults to false. Consider logging a warning when disabled on non-localhost hosts.</remarks>
    public bool SslEnabled { get; set; }
```

Replace with:

```csharp
    /// <inheritdoc />
    public bool SslEnabled { get; set; } = true;
```

The `<inheritdoc />` picks up the interface remarks added in Step 1.

- [ ] **Step 3: Update the unit-test assertion**

Open `src/ServiceConnect.UnitTests/TransportConfigurationTests.cs`. Find around line 60:

```csharp
[Fact]
public void DefaultSslEnabledIsFalse()
{
    var config = new TransportConfiguration();
    Assert.False(config.SslEnabled);
}
```

Replace the test name and the assertion:

```csharp
[Fact]
public void DefaultSslEnabledIsTrue()
{
    var config = new TransportConfiguration();
    Assert.True(config.SslEnabled);
}
```

- [ ] **Step 4: Run the unit test to confirm the inverted assertion**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~TransportConfigurationTests.DefaultSslEnabledIsTrue" -m:1`
Expected: 1 passed.

- [ ] **Step 5: Run the full unit-test sweep — expect failures**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-restore -m:1 2>&1 | tail -10`
Expected: a small number of failures from any other tests that quietly assumed `SslEnabled = false`. For each failure, read the test, decide whether to update the assertion (the test was probing default behaviour) or set `SslEnabled = false` explicitly in the test setup (the test cares about plaintext path semantics). Fix in this commit.

- [ ] **Step 6: Find every example `UseRabbitMQ` block**

Run:
```bash
find examples -name "*.cs" -not -path "*/bin/*" -not -path "*/obj/*" -exec grep -l "UseRabbitMQ" {} \;
```

Expected output: 13 example projects' Program.cs files (each example has a Sender/Producer/Consumer/etc. — the count may exceed 13 since examples have multiple programs). Record the actual list.

- [ ] **Step 7: Patch every example block**

For each file from Step 6, find the `UseRabbitMQ(transport =>` block and add `transport.SslEnabled = false;` with a comment. The canonical insertion pattern:

```csharp
builder.UseRabbitMQ(transport =>
{
    transport.Host = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost";
    transport.Username = "guest";
    transport.Password = "guest";
    transport.SslEnabled = false; // local-dev plaintext; production must use TLS
});
```

Add the `transport.SslEnabled = false;` line at the END of the block (after the existing `Host`/`Username`/`Password`/etc. lines), with the trailing comment exactly as shown. Do not reorder existing lines. Do not add or remove blank lines.

- [ ] **Step 8: Find every E2E test `ConfigureTransport` block**

Run:
```bash
grep -rln "ConfigureTransport" src/ServiceConnect.EndToEndTests/ --include="*.cs"
```

Expected: 60+ files. Record the count for the commit message.

- [ ] **Step 9: Patch every E2E test block**

For each file from Step 8, find each `ConfigureTransport(t =>` block. Add `t.SslEnabled = false;` inside the block — typically as the first or last line, depending on how dense the block is.

Two canonical patterns in the E2E tests:

```csharp
// Pattern A (multi-line block):
builder.ConfigureTransport(t =>
{
    t.Host = _fixture.RabbitMqHostname;
    t.Username = _fixture.RabbitMqUsername;
    t.Password = _fixture.RabbitMqPassword;
    t.SetClientSetting("Port", _fixture.RabbitMqPort);
    t.SslEnabled = false; // Testcontainers RabbitMQ runs plaintext
});

// Pattern B (single-line lambda):
builder.ConfigureTransport(t => t.MaxRetries = 0);
```

For Pattern B, expand to a multi-line block:

```csharp
builder.ConfigureTransport(t =>
{
    t.MaxRetries = 0;
    t.SslEnabled = false; // Testcontainers RabbitMQ runs plaintext
});
```

A bulk find-and-replace is acceptable but verify each substitution by hand — the two patterns require different edits. Do NOT attempt a regex replacement that handles both.

- [ ] **Step 10: Run the full E2E test BUILD (not the runtime suite)**

Run: `dotnet build src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj -m:1`
Expected: 0 errors, 0 warnings. Build-only confirms the syntax of all 60+ edits is correct without spinning up Testcontainers.

- [ ] **Step 11: Run the full unit-test sweep again**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-restore -m:1 2>&1 | tail -3`
Expected: all tests pass. The MongoDB BsonSerializer init-order flake may hit on first run; re-run if it does.

- [ ] **Step 12: Run every example build**

```bash
for slnfile in examples/*/[A-Za-z]*.sln; do
    echo "=== $slnfile ==="
    dotnet build "$slnfile" -m:1 2>&1 | tail -1
done
```

Expected: every example "Build succeeded" with 0 errors.

- [ ] **Step 13: Commit**

```bash
# Stage every modified file. Examples and E2E tests get glob-staged.
git add src/ServiceConnect.Interfaces/Configuration/ITransportConfiguration.cs \
        src/ServiceConnect/Configuration/TransportConfiguration.cs \
        src/ServiceConnect.UnitTests/TransportConfigurationTests.cs \
        examples/ \
        src/ServiceConnect.EndToEndTests/

# Verify nothing else snuck in
git status -s

git commit -m "$(cat <<'EOF'
feat(transport)!: SslEnabled defaults to true

TransportConfiguration.SslEnabled defaults to true in v8 (was false
in v7). The framework now connects on AMQPS port 5671 by default;
production deployments with TLS-configured brokers no longer need
any transport setup beyond Host and credentials.

Migration: existing deployments connecting to a plaintext broker
must opt out explicitly with transport.SslEnabled = false. This
commit applies the opt-out to every example (13 projects, all of
which target the local docker-compose RabbitMQ on port 5672) and
to every E2E test fixture (60+ files using Testcontainers RabbitMQ).
A startup Warning log catches the case where an operator opts out
on a non-loopback host (next commit).

The unit-test asserting the v7 default-of-false inverts to assert
the v8 default-of-true. The XML doc moves to the interface; the
implementation uses <inheritdoc />.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Plaintext-on-non-loopback warning (Item 1)

Source-generated logger emits a `Warning` from `ConnectionFactoryBuilder.Build` when `SslEnabled == false` and any host entry is non-loopback. Both call sites (`Connection.cs:57`, `ProducerConnection.cs:227`) thread an `ILogger` they already have.

**Files:**
- Create: `src/ServiceConnect.Client.RabbitMQ/RabbitMqClientLog.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs:57`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs:227`
- Create: `src/ServiceConnect.UnitTests/Client.RabbitMQ/ConnectionFactoryBuilderWarningTests.cs` (or matching the existing layout — search `src/ServiceConnect.UnitTests/` for existing `ConnectionFactoryBuilder*Tests.cs` and use that directory)

- [ ] **Step 1: Write the failing test**

`ConnectionFactoryBuilder*Tests.cs` files live under `src/ServiceConnect.UnitTests/RabbitMQ/` (e.g. `ConnectionFactoryBuilderConversionErrorTests.cs`). Place the new test alongside them.

Create `src/ServiceConnect.UnitTests/RabbitMQ/ConnectionFactoryBuilderWarningTests.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public class ConnectionFactoryBuilderWarningTests
{
    [Theory]
    [InlineData("localhost", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("10.0.0.5", true)]
    [InlineData("rabbit.example.com", true)]
    public void Build_WithSslDisabled_WarnsOnlyForNonLoopbackHosts(string host, bool warningExpected)
    {
        var transport = new TransportConfiguration { Host = host, SslEnabled = false };
        var fakeLogger = new FakeLogger<ConnectionFactoryBuilderTag>();

        ConnectionFactoryBuilder.Build(transport, fakeLogger);

        var records = fakeLogger.Collector.GetSnapshot();
        if (warningExpected)
        {
            Assert.Single(records);
            Assert.Equal(LogLevel.Warning, records[0].Level);
            Assert.Equal(RabbitMqClientLog.PlaintextOnNonLoopbackHostEventId, records[0].Id.Id);
            Assert.Contains(host, records[0].Message);
        }
        else
        {
            Assert.Empty(records);
        }
    }

    [Fact]
    public void Build_WithSslEnabled_DoesNotWarn_RegardlessOfHost()
    {
        var transport = new TransportConfiguration { Host = "rabbit.example.com", SslEnabled = true };
        var fakeLogger = new FakeLogger<ConnectionFactoryBuilderTag>();

        ConnectionFactoryBuilder.Build(transport, fakeLogger);

        Assert.Empty(fakeLogger.Collector.GetSnapshot());
    }

    [Fact]
    public void Build_WithMixedClusterHostList_WarnsOnFirstNonLoopbackEntry()
    {
        var transport = new TransportConfiguration { Host = "localhost,rabbit-2", SslEnabled = false };
        var fakeLogger = new FakeLogger<ConnectionFactoryBuilderTag>();

        ConnectionFactoryBuilder.Build(transport, fakeLogger);

        var records = fakeLogger.Collector.GetSnapshot();
        Assert.Single(records);
        Assert.Equal(LogLevel.Warning, records[0].Level);
        Assert.Contains("rabbit-2", records[0].Message);
    }

    /// <summary>Placeholder type so FakeLogger has a category — the actual ILogger
    /// passed to ConnectionFactoryBuilder.Build is generic ILogger.</summary>
    public sealed class ConnectionFactoryBuilderTag { }
}
```

- [ ] **Step 2: Run the test — expect compile failure**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ConnectionFactoryBuilderWarningTests" -m:1`
Expected: build error — `RabbitMqClientLog` doesn't exist; `ConnectionFactoryBuilder.Build` doesn't take an `ILogger`.

- [ ] **Step 3: Create the source-gen logger**

Create `src/ServiceConnect.Client.RabbitMQ/RabbitMqClientLog.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Source-generated logger entries emitted by the RabbitMQ client package.
/// </summary>
internal static partial class RabbitMqClientLog
{
    /// <summary>
    /// Stable event id for the plaintext-on-non-loopback warning emitted at connection setup.
    /// </summary>
    public const int PlaintextOnNonLoopbackHostEventId = 1;

    [LoggerMessage(
        EventId = PlaintextOnNonLoopbackHostEventId,
        EventName = "PlaintextOnNonLoopbackHost",
        Level = LogLevel.Warning,
        Message = "ServiceConnect transport is configured for plaintext (SslEnabled=false) against a non-loopback host '{Host}'. Production deployments should use TLS; set SslEnabled=true (the v8 default) and configure certificates. To suppress this warning in environments where plaintext is intentional, raise the ServiceConnect.Client.RabbitMQ category to Error.")]
    public static partial void PlaintextOnNonLoopbackHost(ILogger logger, string host);
}
```

The class is `internal`; the unit test reaches it via the existing `[InternalsVisibleTo("ServiceConnect.UnitTests")]` declared in `ServiceConnect.Client.RabbitMQ.csproj`.

- [ ] **Step 4: Add the `ILogger` parameter to `ConnectionFactoryBuilder.Build`**

Open `src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs`. Modify the `Build` signature and add the warning emission logic:

```csharp
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

internal static class ConnectionFactoryBuilder
{
    private static readonly TimeSpan DefaultHeartbeat = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Builds a <see cref="ConnectionFactory"/> from the transport configuration. When
    /// <paramref name="logger"/> is supplied and <see cref="ITransportConfiguration.SslEnabled"/>
    /// is <see langword="false"/> against a non-loopback host, emits a warning to surface the
    /// likely-misconfiguration in production.
    /// </summary>
    /// <param name="transport">Transport settings including SSL, credentials, and hosts.</param>
    /// <param name="logger">Optional logger for the plaintext-non-loopback warning. When
    /// <see langword="null"/>, no warning is emitted.</param>
    public static ConnectionFactory Build(ITransportConfiguration transport, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transport);

        WarnIfPlaintextOnNonLoopbackHost(transport, logger ?? NullLogger.Instance);

        // ... rest unchanged ...
    }

    private static void WarnIfPlaintextOnNonLoopbackHost(ITransportConfiguration transport, ILogger logger)
    {
        if (transport.SslEnabled || string.IsNullOrEmpty(transport.Host))
        {
            return;
        }

        foreach (var entry in transport.Host.Split(','))
        {
            var trimmed = entry.Trim();
            if (trimmed.Length == 0 || IsLoopback(trimmed))
            {
                continue;
            }
            RabbitMqClientLog.PlaintextOnNonLoopbackHost(logger, trimmed);
            return; // one warning per Build call regardless of how many non-loopback entries
        }
    }

    private static bool IsLoopback(string host)
    {
        if (IPAddress.TryParse(host, out var addr))
        {
            return IPAddress.IsLoopback(addr);
        }
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    // ... existing private helpers (ConvertSettingToInt32, ResolveHeartbeat) unchanged ...
}
```

Keep the rest of the existing `Build` body intact — the `var explicitPortConfigured = ...` line through the `return factory;` line. Insert the new `WarnIfPlaintextOnNonLoopbackHost` call as the first statement after `ArgumentNullException.ThrowIfNull(transport);`.

- [ ] **Step 5: Update `Connection.cs:57` to pass the logger**

Open `src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs`. The `Connection` constructor at line 13 already takes an `ILogger logger` parameter. Find the call:

```csharp
ConnectionFactoryBuilder.Build(transportSettings);
```

Replace with:

```csharp
ConnectionFactoryBuilder.Build(transportSettings, logger);
```

- [ ] **Step 6: Update `ProducerConnection.cs:227` to pass the logger**

Open `src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs`. The class already has `private readonly ILogger _logger;`. Find:

```csharp
_connectionFactory = ConnectionFactoryBuilder.Build(_transportConfiguration);
```

Replace with:

```csharp
_connectionFactory = ConnectionFactoryBuilder.Build(_transportConfiguration, _logger);
```

- [ ] **Step 7: Run the warning test**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ConnectionFactoryBuilderWarningTests" -m:1`
Expected: 7 passed (5 Theory cases + 2 Facts).

- [ ] **Step 8: Run the full unit-test sweep**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-build --no-restore -m:1 2>&1 | tail -3`
Expected: all tests pass.

- [ ] **Step 9: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/RabbitMqClientLog.cs \
        src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs \
        src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs \
        src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/ConnectionFactoryBuilderWarningTests.cs

git commit -m "$(cat <<'EOF'
feat(transport): warn on plaintext over non-loopback host

ServiceConnect now emits a Warning-level log when a producer or
consumer is configured for plaintext (SslEnabled=false) against a
non-loopback host. The warning fires once per producer/consumer
connection setup (so 2 warnings per typical bus). Catches the
production-misconfig case after the v8 SslEnabled-default flip.

Implementation: source-generated logger
ServiceConnect.Client.RabbitMQ.RabbitMqClientLog with stable
EventId 1 ("PlaintextOnNonLoopbackHost"). Emitted from
ConnectionFactoryBuilder.Build, which now optionally takes an
ILogger threaded from each call site (Connection and
ProducerConnection both already have one). The warning is
filterable via standard Microsoft.Extensions.Logging category
configuration; raise ServiceConnect.Client.RabbitMQ to Error to
silence in environments where plaintext is intentional.

Loopback detection: parses the Host string (after splitting on ','
for cluster lists) and checks IPAddress.IsLoopback for parsable
IPs, or matches the literal "localhost" (case-insensitive) for
hostnames. No DNS resolution.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Document v8 TLS posture (Item 6)

Three website touchpoints, one atomic commit.

**Files:**
- Modify: `website/src/content/docs/learn/operations/configuration.mdx:44-55` — TLS section rewrite
- Modify: `website/src/content/docs/learn/getting-started.mdx` — spot-fix
- Modify: `website/src/content/docs/releases.mdx` v8 highlights — new subsection

- [ ] **Step 1: Rewrite the TLS section in `configuration.mdx`**

Open `website/src/content/docs/learn/operations/configuration.mdx`. Find the existing `### TLS` subsection (around lines 44-55). Replace from `### TLS` through the existing closing of the subsection (the line starting `Client certs can also be supplied in memory…`) with:

```markdown
### TLS

**As of v8, TLS is enabled by default.** `SslEnabled` defaults to `true`, and ServiceConnect connects on the default AMQPS port (5671). For production deployments connecting to a broker with TLS configured, no transport setup is required beyond `Host` and credentials.

For brokers behind TLS with custom server names or client certificates:

```csharp
transport.SslEnabled = true;            // (default)
transport.ServerName = "rabbit.prod.example.com";
transport.CertPath = "/etc/ssl/client.pfx";
transport.CertPassphrase = Environment.GetEnvironmentVariable("CLIENT_CERT_PASSPHRASE");
```

Client certs can also be supplied in memory via `Certs`, or selected dynamically via `CertificateSelectionCallback`. For broker chains that don't validate cleanly, `AcceptablePolicyErrors` lets you widen acceptance — use sparingly, and never `SslPolicyErrors.RemoteCertificateNameMismatch` in production.

#### Connecting to a plaintext broker (local dev)

If your broker runs without TLS — typically the official `rabbitmq:3-management` Docker image on port 5672 — set `SslEnabled = false`:

```csharp
transport.SslEnabled = false;          // local-dev plaintext; production must use TLS
```

ServiceConnect logs a `Warning`-level message under the `ServiceConnect.Client.RabbitMQ` category when plaintext is configured against a non-loopback host. To silence the warning in environments where plaintext is intentional (e.g. an isolated VPC), raise the category's minimum level to `Error` via standard `Microsoft.Extensions.Logging` filter configuration.
```

- [ ] **Step 2: Spot-check `getting-started.mdx` for plaintext snippets**

Open `website/src/content/docs/learn/getting-started.mdx`. Search for `UseRabbitMQ` or `ConfigureTransport`. For each snippet:

- If the snippet targets `localhost` (or has the framing of "first run, local dev"), add `transport.SslEnabled = false; // local-dev plaintext` inside the block.
- If the snippet is framed as production-style, add no opt-out (the v8 default of `true` is correct).

If there are no `UseRabbitMQ` / `ConfigureTransport` snippets in the file, no change needed — record this in the commit message.

- [ ] **Step 3: Add the v8 highlight subsection to `releases.mdx`**

Open `website/src/content/docs/releases.mdx`. Find the v8 highlights section (the `## v8 highlights` block from prior Group A / C work). Append a new subsection at the end of the v8 highlights, before the v7 / older sections begin:

```markdown
### Breaking — TLS is on by default

`TransportConfiguration.SslEnabled` defaults to `true` in v8 (was `false` in v7). The framework now connects on AMQPS port 5671 by default; production deployments with TLS-configured brokers no longer need any transport setup beyond `Host` and credentials.

**Migration.** If your broker runs without TLS — most localhost dev setups do — explicitly opt out:

```csharp
builder.ConfigureTransport(t =>
{
    t.Host = "localhost";
    t.SslEnabled = false;        // local-dev plaintext; production must use TLS
});
```

ServiceConnect emits a `Warning`-level log under the `ServiceConnect.Client.RabbitMQ` category when plaintext is configured against a non-loopback host. The warning is filterable via standard `Microsoft.Extensions.Logging` category configuration; it is NOT an error and the framework still connects.

**Why.** ServiceConnect-CSharp v7 defaulted to plaintext for ergonomic localhost dev. v8 inverts the default to align with secure-by-default norms — production deployments connecting over plaintext to a non-loopback broker are almost always a misconfiguration, and the warning catches the rest.
```

- [ ] **Step 4: Verify Astro build**

Run:
```bash
cd website && npx astro build && cd ..
```
Expected: build succeeds (66 pages, no errors).

- [ ] **Step 5: Commit**

```bash
git add website/src/content/docs/learn/operations/configuration.mdx \
        website/src/content/docs/learn/getting-started.mdx \
        website/src/content/docs/releases.mdx

git commit -m "$(cat <<'EOF'
docs(website): document v8 TLS posture

Three doc touchpoints for the v8 TLS-on-by-default change:

- configuration.mdx#tls: rewrites the TLS section to lead with
  "v8 enables TLS by default", drops the old "set SslEnabled=true"
  framing, and adds a new "Connecting to a plaintext broker
  (local dev)" subsection naming the warning category.

- getting-started.mdx: spot-fix any plaintext snippet so the
  page works end-to-end on the v8 default.

- releases.mdx v8 highlights: new "Breaking — TLS is on by
  default" subsection covering the change, the migration path
  (one explicit SslEnabled=false), the warning behaviour, and
  the rationale.

No new pages; the supported-runtimes deferral from Group C is
handled here through configuration.mdx + releases.mdx, not a
dedicated supported-runtimes.mdx.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Mark Group D done in the roadmap

After Tasks 1-4 land, update the roadmap.

**Files:**
- Modify: `architecture-fix-plan.md` — change Group D status

- [ ] **Step 1: Update the roadmap status line**

Open `architecture-fix-plan.md`. Find:

```
## Group D — Security defaults · *pending*
```

Change to:

```
## Group D — Security defaults · *done*
```

- [ ] **Step 2: Commit**

```bash
git add architecture-fix-plan.md

git commit -m "$(cat <<'EOF'
docs(architecture): mark Group D done in the fix plan

Group D lands in four feature commits:

- fix(transport)!: bound the publisher-confirm tracker
- feat(transport)!: SslEnabled defaults to true
- feat(transport): warn on plaintext over non-loopback host
- docs(website): document v8 TLS posture

The TLS-default flip was the strategic call; the operator-facing
warning catches the production-misconfig case after the flip; the
publisher-confirm bound closes a memory-safety gap in
RabbitMQ.Client v7.

Group D is complete. Group B (metrics rollout) and Group F (perf
reductions) are the recommended next groups per the roadmap's order
— both additive, low risk, slot into any minor release. Group E
(resilience features, idempotency strategy) remains last because of
its strategic call on the framework's idempotency story.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Final verification

After all five tasks land, run the full sweep to confirm nothing else regressed.

- [ ] **Step 1: Full solution build**

Run: `dotnet build src/ServiceConnect.slnx -m:1`
Expected: 0 errors, 0 warnings.

- [ ] **Step 2: Full unit-test sweep**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-build --no-restore -m:1`
Expected: all tests pass. The test count is end-of-Group-C baseline (1177) plus new tests from Tasks 1 and 3 (~9 new tests if Task 1 takes the unbounded code path; ~7 if it takes the documentation-only path).

- [ ] **Step 3: SerializationCompatTests sweep (sanity)**

Run: `dotnet test src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj --no-build --no-restore`
Expected: 48/48 pass. Group D doesn't touch the serializer.

- [ ] **Step 4: EndToEndTests build**

Run: `dotnet build src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj -m:1`
Expected: 0 errors, 0 warnings. (Build-only — running the suite needs Docker; that's beyond CI scope here.)

- [ ] **Step 5: Verify every example builds**

```bash
for slnfile in examples/*/[A-Za-z]*.sln; do
    echo "=== $slnfile ==="
    dotnet build "$slnfile" -m:1 2>&1 | tail -1
done
```

Expected: every example "Build succeeded" with 0 errors.

- [ ] **Step 6: Astro site build**

Run: `cd website && npx astro build && cd ..`
Expected: 66 pages built, no errors.

- [ ] **Step 7: Confirm commit chain**

Run: `git log <commit-before-task-1>..HEAD --oneline`
Expected: exactly five commits in this order:

```
<sha> docs(architecture): mark Group D done in the fix plan
<sha> docs(website): document v8 TLS posture
<sha> feat(transport): warn on plaintext over non-loopback host
<sha> feat(transport)!: SslEnabled defaults to true
<sha> fix(transport)!: bound the publisher-confirm tracker
```

---

## Risks and rollback

- **Task 1 outcome unknown until investigation.** If RabbitMQ.Client v7.2.1 already bounds the tracker, the commit is documentation-only — no `MaxOutstandingPublishConfirms` constant, no rate limiter, no test. The commit still lands first; the message body shifts to "verified bounded by [mechanism]".
- **Task 2 blast radius is large.** ~75 files touched in one commit (interface + impl + 1 unit test + ~13 examples + ~60 E2E test files). Each E2E edit is mechanical (one line added inside an existing `ConfigureTransport` block), but the volume risks tedium-induced typos. Mitigation: Step 10's full E2E build run catches syntax errors; Step 11's unit-test sweep catches assertion regressions; Step 12's example build run catches example breakage.
- **`<see cref="LogLevel.Warning"/>` in the interface XML doc** depends on the Interfaces project referencing `Microsoft.Extensions.Logging.Abstractions`. If it doesn't (Step 1 of Task 2 verifies), the `<see cref>` falls back to plain `<c>Warning</c>` — no functional difference in IDE rendering.
- **`ConnectionFactoryBuilder.Build` parameter addition** is a signature change. The method is `internal`, so it has no out-of-package consumers. The two in-package call sites (`Connection.cs:57`, `ProducerConnection.cs:227`) are updated atomically in Task 3. No external risk.
- **Loopback detection is hostname-string + `IPAddress.IsLoopback`** — no DNS. A non-standard hostname like `myhost.local` resolving to `127.0.0.1` will warn. Documented; opt out via log-category filtering. The TOCTOU and slow-DNS reasons against resolving were called out in the spec.
- **Rate-limiter at 256 permits** could be wrong for a specific deployment shape (very high throughput, tolerant of latency). Operators tune via `SetClientSetting("MaxOutstandingPublishConfirms", N)`. If the default is too low in practice we adjust in a patch release; the change is one constant.
- **Rollback** — each of the five commits is independently revertible. `git revert` of any single commit cleanly removes that item. Task 2 in particular is the most painful to revert (75-file diff) but `git revert <sha>` handles it mechanically. No data migrations, no schema changes.

---

## Decisions banked from the brainstorm

For audit trail (cross-referencing the design doc):

- **Strategic call:** option **(c) aggressive** — flip `SslEnabled` to `true`. User accepted breaking-change cost for v8.
- **Warning condition:** fires whenever `SslEnabled == false` AND host is non-loopback, regardless of how `SslEnabled` got to `false`.
- **Loopback detection:** hostname-string + `IPAddress.IsLoopback`. No DNS resolution.
- **Cluster-list policy:** if any entry in a comma-separated `Host` is non-loopback, the warning fires.
- **Rate-limiter default:** 256 outstanding publishes, configurable via `SetClientSetting("MaxOutstandingPublishConfirms", N)`.
- **Item 3 commit-as-investigation:** Task 1 lands first; outcome (code or docs) doesn't entangle with the security-flip commits.
- **Five-commit rollout** on `v7-clean-architecture`, mirroring Groups A and C structure.
- **No `[Obsolete]` shims, no `RequireTransportSecurity` strict-mode flag, no environment-aware suppression of the warning, no DNS resolution, no automatic certificate generation, no metrics counter, no per-message-type concurrency split, no new `supported-runtimes.mdx` page.**
