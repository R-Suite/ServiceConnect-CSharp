# Phase 04 — RabbitMQ producer + topology Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop silent message loss on the RabbitMQ producer + topology side and remove the publish-lock latency hazard. Nine fixes (C11 + C12 + H1 + H21 + H22 + M11 + M12 + two smaller items) plus a doc-update sweep across `learn/operations/`, `reference/messages/`, `reference/configuration/`, `reference/bus/`, `releases/`.

**Architecture:** Two design decisions drive the largest changes — H21 chose Option B (Bus stops stamping the dead `MessageType = FullName`; producer remains the single stamper, semantics formalised as the operation flag `"Publish"|"Send"|"ByteStream"`), and H22 chose Option A (publish-timeout marks the connection for reset via `ProducerConnection.MarkResetRequired()` instead of awaiting `ReconnectAsync` under the `_publishLock`; the next publish drives the actual reconnect off-lock inside `EnsureConnectedAsync`). The remaining seven fixes are localised: per-iteration MessageId / TimeSent stamp in multi-endpoint Send (C11), DLX hard-coded `autoDelete:false` (C12), framework-wins retry-arg merge with Debug log on override (H1), null guards on Producer entry points (M11), narrowed catch + value-aware log on priority conversion (M12), reserved-header overwrite warning (smaller), and clear conversion-error context in `ConnectionFactoryBuilder` (smaller).

**Tech Stack:** .NET (multi-target net8.0/net10.0), xUnit + Moq for unit tests, Testcontainers for the two RabbitMQ E2E tests, `RabbitMQ.Client` for the producer/topology plumbing, Astro/Starlight for the website docs sweep. Test runner: `dotnet test` with `--filter` and `-m:1` (per-csproj only — see [build/test safety](#buildtest-safety) below).

**Spec:** [`docs/superpowers/specs/2026-04-29-phase-04-rabbitmq-producer-topology.md`](../specs/2026-04-29-phase-04-rabbitmq-producer-topology.md).

---

## Build/test safety

This machine has crashed when running unconstrained whole-solution `dotnet build` / `dotnet test` against this codebase (CLAUDE.md has the full incident analysis). The wrapper at `~/.local/bin/dotnet` re-execs every `dotnet` invocation under a systemd cgroup with `CPUQuota=800%`, `MemoryMax=8G`, `MemorySwapMax=0`, `TasksMax=200` and is the safety net. **Even with the wrapper, every command in this plan is per-csproj.** Add `-m:1` to `dotnet test` invocations to serialize MSBuild and stay within `TasksMax`. Never run whole-solution `dotnet build` / `dotnet test` / `dotnet format`.

For the two E2E tasks: the user is in the `docker` group; call `docker` directly. No `sg docker -c` wrapper.

---

## File structure

### Modified — production code

- `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs` — M11 (null guards on four entry points), C11 (per-iteration MessageId/TimeSent in `SendAsync(Type, byte[], ...)`), H22 (replace in-catch `ReconnectAsync` with `MarkResetRequired`).
- `src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs` — M12 (priority conversion log + narrow catch), smaller (reserved-header overwrite warning), C11 (promote `FormatTimestamp` from `private` to `internal static`).
- `src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs` — H22 (`_resetRequired` flag + `MarkResetRequired()` + flag-consume in `EnsureConnectedAsync`).
- `src/ServiceConnect.Client.RabbitMQ/Topology/RabbitMqTopologyProvisioner.cs` — C12 (DLX hard-coded `autoDelete:false`) + H1 (framework-wins retry-arg merge with Debug log).
- `src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs` — smaller (`ConvertSettingToInt32` helper at both conversion sites).
- `src/ServiceConnect/Bus.cs` — H21 (remove `MessageType` from `ReservedHeaders`, drop both `MessageType = FullName` stamp lines, update the `// Outgoing filters and middleware rely on…` comment).

### Modified / new — tests

- `src/ServiceConnect.UnitTests/RabbitMQ/ProducerNullArgumentTests.cs` — new (M11).
- `src/ServiceConnect.UnitTests/RabbitMQ/OutboundHeaderBuilderPriorityTests.cs` — new (M12).
- `src/ServiceConnect.UnitTests/RabbitMQ/OutboundHeaderBuilderReservedHeaderWarningTests.cs` — new (smaller).
- `src/ServiceConnect.UnitTests/RabbitMQ/ConnectionFactoryBuilderConversionErrorTests.cs` — new (smaller).
- `src/ServiceConnect.UnitTests/RabbitMQ/ProducerMultiEndpointSendTests.cs` — new (C11).
- `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqTopologyProvisionerRetryDlxAutoDeleteTests.cs` — new (C12 unit).
- `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqTopologyProvisionerRetryArgumentsTests.cs` — new (H1).
- `src/ServiceConnect.EndToEndTests/RabbitMq/RetryTopologyAutoDeleteE2ETests.cs` — new (C12 E2E).
- `src/ServiceConnect.UnitTests/Bus/BusEnvelopeMessageTypeTests.cs` — new (H21).
- `src/ServiceConnect.UnitTests/Bus/BusReservedHeadersTests.cs` — modify if exists, else new (H21 — invert `MessageType` rejection).
- `src/ServiceConnect.UnitTests/RabbitMQ/OutboundHeaderBuilderOperationNameTests.cs` — new (H21 — explicit operation-name assertion).
- `src/ServiceConnect.UnitTests/RabbitMQ/ProducerPublishTimeoutResetTests.cs` — new (H22 unit).
- `src/ServiceConnect.EndToEndTests/RabbitMq/ProducerPublishTimeoutE2ETests.cs` — extend (H22 E2E).

### Modified — website + sidebar

- `website/src/content/docs/reference/messages/` (header-keys page — exact filename to be located in Task 11) — H21 doc.
- `website/src/content/docs/learn/operations/` (retry-topology / DLX content — exact files to be located in Task 11) — C12 + H1 doc.
- `website/src/content/docs/reference/configuration/` (`RetryQueueArguments`) — H1 doc.
- `website/src/content/docs/reference/bus/` (`IBus.Send` multi-endpoint section) — C11 doc.
- `website/src/content/docs/releases/` (v7 release notes) — five behaviour-change bullets.

---

## Task 1: M11 — null-guard `Producer` public publish/send entry points

**Files:**
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/ProducerNullArgumentTests.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs` (4 entry points)

This task lands first because it's mechanical, isolated, and gives an early green build to anchor the rest of the phase.

- [ ] **Step 1: Locate existing Producer test fixtures for arrange-pattern reference**

```bash
grep -ln "new Producer(" src/ServiceConnect.UnitTests/RabbitMQ/ | head -5
```

Pick the simplest one (`ProducerExchangeNameCacheTests.cs` is a good candidate) and re-use its constructor-arrange shape so the new test fixture matches existing style.

- [ ] **Step 2: Write the failing tests**

Create `src/ServiceConnect.UnitTests/RabbitMQ/ProducerNullArgumentTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class ProducerNullArgumentTests
{
    private static Producer CreateProducer()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("test-queue");

        var busConfig = new Mock<IBusConfiguration>();

        return new Producer(transport.Object, queueConfig.Object, busConfig.Object, NullLogger<Producer>.Instance);
    }

    [Fact]
    public async Task PublishAsync_NullType_Throws()
    {
        var producer = CreateProducer();
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            producer.PublishAsync(null!, [1, 2, 3]));
        Assert.Equal("type", ex.ParamName);
    }

    [Fact]
    public async Task PublishAsync_NullMessage_Throws()
    {
        var producer = CreateProducer();
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            producer.PublishAsync(typeof(string), null!));
        Assert.Equal("message", ex.ParamName);
    }

    [Fact]
    public async Task SendAsyncByType_NullType_Throws()
    {
        var producer = CreateProducer();
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            producer.SendAsync(null!, [1, 2, 3]));
        Assert.Equal("type", ex.ParamName);
    }

    [Fact]
    public async Task SendAsyncByType_NullMessage_Throws()
    {
        var producer = CreateProducer();
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            producer.SendAsync(typeof(string), null!));
        Assert.Equal("message", ex.ParamName);
    }

    [Fact]
    public async Task SendAsyncToEndpoint_NullType_Throws()
    {
        var producer = CreateProducer();
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            producer.SendAsync("ep", null!, [1, 2, 3]));
        Assert.Equal("type", ex.ParamName);
    }

    [Fact]
    public async Task SendAsyncToEndpoint_NullMessage_Throws()
    {
        var producer = CreateProducer();
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            producer.SendAsync("ep", typeof(string), null!));
        Assert.Equal("message", ex.ParamName);
    }

    [Fact]
    public async Task SendBytesAsync_NullType_Throws()
    {
        var producer = CreateProducer();
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            producer.SendBytesAsync("ep", null!, [1, 2, 3]));
        Assert.Equal("type", ex.ParamName);
    }

    [Fact]
    public async Task SendBytesAsync_NullPacket_Throws()
    {
        var producer = CreateProducer();
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            producer.SendBytesAsync("ep", typeof(string), null!));
        Assert.Equal("packet", ex.ParamName);
    }
}
```

- [ ] **Step 3: Run the tests to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ProducerNullArgumentTests" -m:1
```

Expected: 8 failed. Today the methods reach `message.Length` / `packet.Length` / type usage and throw `NullReferenceException`, not `ArgumentNullException`.

- [ ] **Step 4: Apply the M11 fix at all four entry points**

In `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs`:

`PublishAsync(Type type, byte[] message, ...)`: insert at the top of the body, before `cancellationToken.ThrowIfCancellationRequested();`:

```csharp
ArgumentNullException.ThrowIfNull(type);
ArgumentNullException.ThrowIfNull(message);
```

Apply the same pattern at the head of:
- `SendAsync(Type type, byte[] message, ...)`: `ArgumentNullException.ThrowIfNull(type); ArgumentNullException.ThrowIfNull(message);`
- `SendAsync(string endPoint, Type type, byte[] message, ...)`: `ArgumentNullException.ThrowIfNull(type); ArgumentNullException.ThrowIfNull(message);` (the existing `string.IsNullOrWhiteSpace(endPoint)` guard stays).
- `SendBytesAsync(string endPoint, Type type, byte[] packet, ...)`: `ArgumentNullException.ThrowIfNull(type); ArgumentNullException.ThrowIfNull(packet);` (the existing `string.IsNullOrWhiteSpace(endPoint)` guard stays).

Order: `type` first, then `message` / `packet` — matches parameter order so the `ParamName` check in tests is unambiguous.

- [ ] **Step 5: Run the tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ProducerNullArgumentTests" -m:1
```

Expected: 8 passed.

Sanity-check the broader RabbitMQ filter:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Producer" -m:1
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/ProducerNullArgumentTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs
git commit -m "fix(producer): null-guard public publish/send entry points"
```

---

## Task 2: M12 — Priority conversion log context, narrow catch

**Files:**
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/OutboundHeaderBuilderPriorityTests.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs`

- [ ] **Step 1: Locate any existing logger-stub helper**

```bash
grep -rln "ILogger<.*>\s\|LoggerStub\|XunitLogger\|class.*: ILogger" src/ServiceConnect.UnitTests/ | head -5
```

If a logger stub exists, reuse it; otherwise the test below uses Moq's `ILogger<T>` capture pattern.

- [ ] **Step 2: Write the failing tests**

Create `src/ServiceConnect.UnitTests/RabbitMQ/OutboundHeaderBuilderPriorityTests.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class OutboundHeaderBuilderPriorityTests
{
    private static (OutboundHeaderBuilder builder, List<(LogLevel Level, string Message, Exception? Exception)> logs) CreateBuilder()
    {
        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("q");

        var captured = new List<(LogLevel, string, Exception?)>();
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.Log(
            It.IsAny<LogLevel>(),
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()))
            .Callback(new InvocationAction(invocation =>
            {
                var level = (LogLevel)invocation.Arguments[0];
                var state = invocation.Arguments[2];
                var ex = (Exception?)invocation.Arguments[3];
                var formatter = invocation.Arguments[4];
                var message = (string)formatter.GetType()
                    .GetMethod("Invoke")!
                    .Invoke(formatter, [state, ex])!;
                captured.Add((level, message, ex));
            }));

        var builder = new OutboundHeaderBuilder(
            busConfig.Object,
            queueConfig.Object,
            new FakeTimeProvider(),
            logger.Object);
        return (builder, captured);
    }

    [Fact]
    public void Priority_ValidByte_StampsAndDoesNotLog()
    {
        var (builder, logs) = CreateBuilder();
        var headers = builder.BuildHeaders(typeof(string), null, "q", "Publish");
        headers[HeaderKeys.Priority] = (byte)5;

        var props = builder.BuildBasicProperties(headers);

        Assert.True(props.IsPriorityPresent());
        Assert.Equal((byte)5, props.Priority);
        Assert.Empty(logs);
    }

    [Fact]
    public void Priority_OutOfRangeInt_LogsValueAndType_ContinuesWithoutPriority()
    {
        var (builder, logs) = CreateBuilder();
        var headers = builder.BuildHeaders(typeof(string), null, "q", "Publish");
        headers[HeaderKeys.Priority] = 300;

        var props = builder.BuildBasicProperties(headers);

        Assert.False(props.IsPriorityPresent());
        var error = Assert.Single(logs.Where(l => l.Level == LogLevel.Error));
        Assert.Contains("300", error.Message);
        Assert.Contains("Int32", error.Message);
    }

    [Fact]
    public void Priority_NonNumericString_LogsValueAndType_ContinuesWithoutPriority()
    {
        var (builder, logs) = CreateBuilder();
        var headers = builder.BuildHeaders(typeof(string), null, "q", "Publish");
        headers[HeaderKeys.Priority] = "abc";

        var props = builder.BuildBasicProperties(headers);

        Assert.False(props.IsPriorityPresent());
        var error = Assert.Single(logs.Where(l => l.Level == LogLevel.Error));
        Assert.Contains("abc", error.Message);
        Assert.Contains("System.String", error.Message);
    }
}
```

If `OutboundHeaderBuilder` is `internal sealed`, the `InternalsVisibleTo("ServiceConnect.UnitTests")` attribute should already be wired (verify with `grep -n InternalsVisibleTo src/ServiceConnect.Client.RabbitMQ/Properties/AssemblyInfo.cs src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj`); if not, add it as a side-effect of this task.

- [ ] **Step 3: Run the tests to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~OutboundHeaderBuilderPriorityTests" -m:1
```

Expected: 2 failures. Today's log message is just `"Error setting message priority"` and contains neither the value nor the runtime type.

- [ ] **Step 4: Apply the M12 fix**

In `src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs`, locate the existing priority block:

```csharp
if (messageHeaders.TryGetValue(HeaderKeys.Priority, out var priority))
{
    try
    {
        basicProperties.Priority = Convert.ToByte(priority, System.Globalization.CultureInfo.InvariantCulture);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error setting message priority");
    }
}
```

Replace with:

```csharp
if (messageHeaders.TryGetValue(HeaderKeys.Priority, out var priority))
{
    try
    {
        basicProperties.Priority = Convert.ToByte(priority, System.Globalization.CultureInfo.InvariantCulture);
    }
    // RabbitMQ priorities are advisory — failing the publish over a misconfigured priority is
    // the wrong default. Soft-drop with enough context that the operator can see which value
    // was bad and why. Catch only the conversion exceptions; anything else propagates.
    catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
    {
        _logger.LogError(
            ex,
            "Could not set message priority from value '{Value}' (type '{ValueType}'); priority must be convertible to byte (0..255). Continuing without priority.",
            priority,
            priority?.GetType().FullName ?? "<null>");
    }
}
```

- [ ] **Step 5: Run the tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~OutboundHeaderBuilderPriorityTests" -m:1
```

Expected: 3 passed.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/OutboundHeaderBuilderPriorityTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs
git commit -m "fix(producer): log full context on priority conversion failure"
```

---

## Task 3: Smaller — caller-supplied reserved-header overwrite warning

**Files:**
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/OutboundHeaderBuilderReservedHeaderWarningTests.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/ServiceConnect.UnitTests/RabbitMQ/OutboundHeaderBuilderReservedHeaderWarningTests.cs`. Reuse the logger-capture helper from Task 2's test file via copy (xUnit fixtures are file-local; the duplication is intentional — keeps the test file self-contained):

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class OutboundHeaderBuilderReservedHeaderWarningTests
{
    private static (OutboundHeaderBuilder builder, List<(LogLevel Level, string Message)> logs) CreateBuilder()
    {
        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("source-q");

        var captured = new List<(LogLevel, string)>();
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.Log(
            It.IsAny<LogLevel>(),
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()))
            .Callback(new InvocationAction(invocation =>
            {
                var level = (LogLevel)invocation.Arguments[0];
                var state = invocation.Arguments[2];
                var ex = (Exception?)invocation.Arguments[3];
                var formatter = invocation.Arguments[4];
                var message = (string)formatter.GetType().GetMethod("Invoke")!.Invoke(formatter, [state, ex])!;
                captured.Add((level, message));
            }));

        return (
            new OutboundHeaderBuilder(busConfig.Object, queueConfig.Object, new FakeTimeProvider(), logger.Object),
            captured);
    }

    [Fact]
    public void BuildHeaders_CallerSuppliesReservedHeader_FrameworkValueWins_AndWarns()
    {
        var (builder, logs) = CreateBuilder();
        var caller = new Dictionary<string, string>
        {
            [HeaderKeys.DestinationAddress] = "user-supplied-dest",
            [HeaderKeys.TypeName] = "user.spoof.type",
            ["X-Custom"] = "ok",
        };

        var result = builder.BuildHeaders(typeof(string), caller, "framework-q", "Publish");

        // Framework wins for reserved keys.
        Assert.Equal("framework-q", result[HeaderKeys.DestinationAddress]);
        Assert.Equal(typeof(string).FullName, result[HeaderKeys.TypeName]);
        // Non-reserved header flows through.
        Assert.Equal("ok", result["X-Custom"]);

        // One warning per overwritten reserved key, each containing the key name.
        var warnings = logs.Where(l => l.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Message.Contains(HeaderKeys.DestinationAddress));
        Assert.Contains(warnings, w => w.Message.Contains(HeaderKeys.TypeName));
    }

    [Fact]
    public void BuildHeaders_CallerSuppliesMessageId_PreservedNoWarning()
    {
        // MessageId is deliberately NOT in the overwrite set — caller-supplied (Bus's
        // authoritative stamp) is preserved by the !ContainsKey check.
        var (builder, logs) = CreateBuilder();
        var bus = new Dictionary<string, string>
        {
            [HeaderKeys.MessageId] = "bus-stamped-id",
        };

        var result = builder.BuildHeaders(typeof(string), bus, "q", "Publish");

        Assert.Equal("bus-stamped-id", result[HeaderKeys.MessageId]);
        Assert.Empty(logs.Where(l => l.Level == LogLevel.Warning));
    }
}
```

- [ ] **Step 2: Run the tests to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~OutboundHeaderBuilderReservedHeaderWarningTests" -m:1
```

Expected: 1 failure (`BuildHeaders_CallerSuppliesReservedHeader_FrameworkValueWins_AndWarns`). The framework-wins assertion already passes (existing code stamps reserved keys *after* the caller copy), but the warning-emitted assertion fails because today the overwrite is silent. The MessageId test should already pass.

- [ ] **Step 3: Apply the smaller fix**

In `src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs`, add a static `OverwrittenHeaderKeys` set near the top of the class (just below `private const int StampedHeaderCount = 11;` and `TypeNameCache`):

```csharp
// Producer-stamped keys: callers cannot override these (the framework owns them).
// MessageId is deliberately NOT in this set — caller-supplied MessageId (e.g. Bus's
// authoritative stamp) is preserved by the !ContainsKey check in BuildHeaders below.
private static readonly HashSet<string> OverwrittenHeaderKeys = new(StringComparer.Ordinal)
{
    HeaderKeys.DestinationAddress,
    HeaderKeys.MessageType,
    HeaderKeys.SourceAddress,
    HeaderKeys.TimeSent,
    HeaderKeys.SourceMachine,
    HeaderKeys.TypeName,
    HeaderKeys.FullTypeName,
    HeaderKeys.ConsumerType,
    HeaderKeys.Language,
};
```

Replace the existing caller-copy loop in `BuildHeaders`:

```csharp
if (headers is not null)
{
    foreach (var kvp in headers)
    {
        result[kvp.Key] = kvp.Value;
    }
}
```

with:

```csharp
if (headers is not null)
{
    foreach (var kvp in headers)
    {
        if (OverwrittenHeaderKeys.Contains(kvp.Key))
        {
            _logger.LogWarning(
                "Caller-supplied reserved header '{Key}' will be overwritten by the framework",
                kvp.Key);
            continue;
        }
        result[kvp.Key] = kvp.Value;
    }
}
```

- [ ] **Step 4: Run the tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~OutboundHeaderBuilder" -m:1
```

Expected: all OutboundHeaderBuilder-related tests pass (Task 2's tests + Task 3's tests).

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/OutboundHeaderBuilderReservedHeaderWarningTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs
git commit -m "fix(producer): warn on caller-supplied reserved header overwrite"
```

---

## Task 4: Smaller — `ConnectionFactoryBuilder` clear conversion errors

**Files:**
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/ConnectionFactoryBuilderConversionErrorTests.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs`

`ConnectionFactoryBuilder` is `internal static`; the unit-test assembly already has `InternalsVisibleTo` (verify if unsure).

- [ ] **Step 1: Write the failing tests**

Create `src/ServiceConnect.UnitTests/RabbitMQ/ConnectionFactoryBuilderConversionErrorTests.cs`:

```csharp
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class ConnectionFactoryBuilderConversionErrorTests
{
    private static ITransportConfiguration TransportWithSettings(IDictionary<string, object> settings)
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>(settings));
        return transport.Object;
    }

    [Fact]
    public void Build_PortIsUnconvertibleString_ThrowsInvalidOperationWithKeyAndValueAndType()
    {
        var transport = TransportWithSettings(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.Port] = "not-a-port",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => ConnectionFactoryBuilder.Build(transport));

        Assert.Contains(RabbitMQSettingKeys.Port, ex.Message);
        Assert.Contains("not-a-port", ex.Message);
        Assert.Contains("System.String", ex.Message);
        Assert.NotNull(ex.InnerException);
        Assert.IsType<FormatException>(ex.InnerException);
    }

    [Fact]
    public void Build_HeartbeatIsUnconvertibleString_ThrowsInvalidOperationWithKeyAndValueAndType()
    {
        var transport = TransportWithSettings(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.HeartbeatTime] = "abc",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => ConnectionFactoryBuilder.Build(transport));

        Assert.Contains(RabbitMQSettingKeys.HeartbeatTime, ex.Message);
        Assert.Contains("abc", ex.Message);
        Assert.Contains("System.String", ex.Message);
        Assert.NotNull(ex.InnerException);
        Assert.IsType<FormatException>(ex.InnerException);
    }

    [Fact]
    public void Build_PortIsOverflowingLong_ThrowsInvalidOperationWithKey()
    {
        var transport = TransportWithSettings(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.Port] = long.MaxValue,
        });

        var ex = Assert.Throws<InvalidOperationException>(() => ConnectionFactoryBuilder.Build(transport));

        Assert.Contains(RabbitMQSettingKeys.Port, ex.Message);
        Assert.NotNull(ex.InnerException);
        Assert.IsType<OverflowException>(ex.InnerException);
    }

    [Fact]
    public void Build_PortIsValidInt_DoesNotThrow()
    {
        var transport = TransportWithSettings(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.Port] = 5672,
        });

        var factory = ConnectionFactoryBuilder.Build(transport);
        Assert.Equal(5672, factory.Port);
    }
}
```

- [ ] **Step 2: Run the tests to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ConnectionFactoryBuilderConversionErrorTests" -m:1
```

Expected: 3 failures (the three error-path tests). Today `Convert.ToInt32` throws `FormatException` / `OverflowException` directly; the tests expect `InvalidOperationException` with key context.

- [ ] **Step 3: Apply the smaller fix**

In `src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs`, add a private helper method (place it just below the `Build` method):

```csharp
private static int ConvertSettingToInt32(string key, object? value)
{
    try
    {
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }
    catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
    {
        throw new InvalidOperationException(
            $"Setting '{key}' must be convertible to Int32; got value '{value}' of type '{value?.GetType().FullName ?? "<null>"}'.",
            ex);
    }
}
```

Replace the port-conversion line (currently `Convert.ToInt32(portVal, CultureInfo.InvariantCulture)`):

```csharp
var port = explicitPortConfigured
    ? ConvertSettingToInt32(RabbitMQSettingKeys.Port, portVal)
    : AmqpTcpEndpoint.UseDefaultPort;
```

Replace the heartbeat-conversion line in `ResolveHeartbeat` (currently `Convert.ToInt32(timeRaw, CultureInfo.InvariantCulture)`):

```csharp
return TimeSpan.FromSeconds(ConvertSettingToInt32(RabbitMQSettingKeys.HeartbeatTime, timeRaw));
```

- [ ] **Step 4: Run the tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ConnectionFactoryBuilderConversionErrorTests" -m:1
```

Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/ConnectionFactoryBuilderConversionErrorTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs
git commit -m "fix(connection): wrap setting conversions with key context"
```

---

## Task 5: C11 — re-mint MessageId/TimeSent per delivery in multi-endpoint Send

**Files:**
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/ProducerMultiEndpointSendTests.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs` (multi-endpoint `SendAsync` foreach loop)
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs` (promote `FormatTimestamp` to `internal static`)

- [ ] **Step 1: Locate existing Producer publish-test arrange shape**

```bash
grep -nE "_producerConnection|ReplaceChannel|new Producer\(" src/ServiceConnect.UnitTests/RabbitMQ/ProducerExchangeNameCacheTests.cs src/ServiceConnect.UnitTests/RabbitMQ/ProducerPublishTimeoutTests.cs 2>/dev/null | head -20
```

Reuse the simplest "build a Producer with a Moq IChannel that captures `BasicPublishAsync` arguments" pattern. If those tests use a private test seam to inject a channel, mirror it.

- [ ] **Step 2: Promote `OutboundHeaderBuilder.FormatTimestamp`**

In `src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs`, change the signature of `FormatTimestamp`:

```csharp
// before
private static string FormatTimestamp(DateTime dt)

// after
internal static string FormatTimestamp(DateTime dt)
```

(The body is unchanged.) Verify `InternalsVisibleTo("ServiceConnect.UnitTests")` exists; if not, add it:

```bash
grep -n "InternalsVisibleTo" src/ServiceConnect.Client.RabbitMQ/ -r
```

If absent, add to `src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj` via `<ItemGroup><InternalsVisibleTo Include="ServiceConnect.UnitTests" /></ItemGroup>`.

- [ ] **Step 3: Write the failing test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/ProducerMultiEndpointSendTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class ProducerMultiEndpointSendTests
{
    [Fact]
    public async Task SendAsync_FanOutToThreeEndpoints_EachDeliveryHasDistinctMessageIdAndTimeSent_SharedCorrelationId()
    {
        var fakeClock = new FakeTimeProvider(new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero));

        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("source-q");
        IReadOnlyList<string> endpoints = new[] { "ep-a", "ep-b", "ep-c" };
        queueConfig.Setup(q => q.TryGetQueueMapping(typeof(FakeMsg), out endpoints!)).Returns(true);

        var busConfig = new Mock<IBusConfiguration>();

        // Capture per-call BasicProperties. Channel mock advances the clock by 1ms per publish so
        // TimeSent observably differs across iterations.
        var captured = new List<BasicProperties>();
        var channel = new Mock<IChannel>();
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns((string ex, string rk, bool m, BasicProperties bp, ReadOnlyMemory<byte> body, CancellationToken ct) =>
            {
                // Snapshot a copy of the properties — Producer reuses the dict between iterations
                // pre-fix, so capturing the live reference would give us three identical views.
                captured.Add(new BasicProperties
                {
                    MessageId = bp.MessageId,
                    Headers = bp.Headers is null ? null : new Dictionary<string, object?>(bp.Headers, StringComparer.Ordinal),
                    Persistent = bp.Persistent,
                });
                fakeClock.Advance(TimeSpan.FromMilliseconds(1));
                return ValueTask.CompletedTask;
            });

        var producer = new Producer(transport.Object, queueConfig.Object, busConfig.Object, NullLogger<Producer>.Instance, fakeClock);
        // Inject the channel via test seam (route through ProducerConnection's CreateConnectionForTests
        // so EnsureConnectedAsync builds a fake connection that yields our captured channel).
        var fakeConnection = new Mock<IConnection>();
        fakeConnection.SetupGet(c => c.IsOpen).Returns(true);
        fakeConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel.Object);
        producer.CreateConnectionForTests = (_, _, _, _) => Task.FromResult(fakeConnection.Object);

        // Bus-stamped CorrelationId — must remain constant across the fan-out.
        var correlationId = Guid.NewGuid().ToString();
        var headers = new Dictionary<string, string>
        {
            [HeaderKeys.CorrelationId] = correlationId,
        };

        await producer.SendAsync(typeof(FakeMsg), [1, 2, 3], headers);

        Assert.Equal(3, captured.Count);

        // C11 invariant: distinct MessageId per delivery.
        Assert.Equal(3, captured.Select(c => c.MessageId).Distinct(StringComparer.Ordinal).Count());

        // TimeSent re-stamped per iteration — captured snapshots see distinct values.
        var timeSentValues = captured
            .Select(c => c.Headers![HeaderKeys.TimeSent]!.ToString()!)
            .ToList();
        Assert.Equal(3, timeSentValues.Distinct(StringComparer.Ordinal).Count());

        // CorrelationId stays constant — proves we did not accidentally re-mint that.
        var correlationIds = captured
            .Select(c => c.Headers![HeaderKeys.CorrelationId]!.ToString()!)
            .ToList();
        Assert.All(correlationIds, id => Assert.Equal(correlationId, id));
    }

    private sealed class FakeMsg { }
}
```

- [ ] **Step 4: Run the test to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~SendAsync_FanOutToThreeEndpoints_EachDeliveryHasDistinctMessageIdAndTimeSent_SharedCorrelationId" -m:1
```

Expected: FAIL — pre-fix, MessageId is built once via `BuildHeaders` outside the loop and reused for all three deliveries. The "distinct MessageIds" assertion fires.

- [ ] **Step 5: Apply the C11 fix**

In `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs`, locate the multi-endpoint `SendAsync(Type type, byte[] message, ...)` `foreach` loop. The current shape is:

```csharp
var baseHeaders = _headerBuilder.BuildHeaders(type, headers, string.Empty, "Send");
foreach (string endPoint in endPoints)
{
    baseHeaders[HeaderKeys.DestinationAddress] = endPoint;
    var basicProperties = _headerBuilder.BuildBasicProperties(baseHeaders);
    await ExecuteWithConnectionRetryAsync(
        () => PublishWithTimeoutAsync(
            _producerConnection.Channel,
            string.Empty,
            endPoint,
            false,
            basicProperties,
            (ReadOnlyMemory<byte>)message,
            cancellationToken).AsTask(),
        cancellationToken).ConfigureAwait(false);
}
```

Replace with:

```csharp
var baseHeaders = _headerBuilder.BuildHeaders(type, headers, string.Empty, "Send");
foreach (string endPoint in endPoints)
{
    // Each delivery is an independent on-wire message: distinct MessageId + TimeSent per
    // endpoint. CorrelationId (Bus-stamped, copied in via BuildHeaders) is the cross-fan-out
    // correlator and is deliberately NOT re-minted.
    baseHeaders[HeaderKeys.DestinationAddress] = endPoint;
    baseHeaders[HeaderKeys.MessageId] = Guid.NewGuid().ToString();
    baseHeaders[HeaderKeys.TimeSent] = OutboundHeaderBuilder.FormatTimestamp(_timeProvider.GetUtcNow().UtcDateTime);
    var basicProperties = _headerBuilder.BuildBasicProperties(baseHeaders);
    await ExecuteWithConnectionRetryAsync(
        () => PublishWithTimeoutAsync(
            _producerConnection.Channel,
            string.Empty,
            endPoint,
            false,
            basicProperties,
            (ReadOnlyMemory<byte>)message,
            cancellationToken).AsTask(),
        cancellationToken).ConfigureAwait(false);
}
```

The `Producer` class needs a `_timeProvider` field. Check whether it already has one (the constructor already accepts `TimeProvider? timeProvider = null` and passes it to `OutboundHeaderBuilder`). If `_timeProvider` is not stored on `Producer` itself, add the field and assign it in the constructor:

```csharp
private readonly TimeProvider _timeProvider;
// ... in ctor:
_timeProvider = timeProvider ?? TimeProvider.System;
```

(Verify constructor — adjust to the already-present mechanism if `Producer` already retains the time provider via `OutboundHeaderBuilder`.)

- [ ] **Step 6: Run the test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ProducerMultiEndpointSendTests" -m:1
```

Expected: 1 passed. Re-run broader Producer filter to catch regressions:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Producer" -m:1
```

Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/ProducerMultiEndpointSendTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs \
        src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs \
        src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj
git commit -m "fix(producer): re-mint MessageId/TimeSent per delivery in multi-endpoint Send"
```

(Drop the `.csproj` from the add list if no `InternalsVisibleTo` change was required.)

---

## Task 6: C12 + H1 — retry DLX always durable; framework retry args win

**Files:**
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqTopologyProvisionerRetryDlxAutoDeleteTests.cs`
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqTopologyProvisionerRetryArgumentsTests.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Topology/RabbitMqTopologyProvisioner.cs` (`ConfigureRetryTopologyAsync` — DLX `autoDelete:false` + framework-wins arg merge)

This task bundles C12 and H1 because both edits live in the same method (`ConfigureRetryTopologyAsync`) and share the same test fixture shape.

- [ ] **Step 1: Write the C12 unit test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqTopologyProvisionerRetryDlxAutoDeleteTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class RabbitMqTopologyProvisionerRetryDlxAutoDeleteTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfigureRetryTopologyAsync_DlxIsAlwaysAutoDeleteFalse_RegardlessOfCallerAutoDelete(bool callerAutoDelete)
    {
        bool? capturedDlxAutoDelete = null;
        var channel = new Mock<IChannel>();
        channel
            .Setup(c => c.ExchangeDeclareAsync(
                It.Is<string>(s => s.EndsWith(".Retries.DeadLetter", StringComparison.Ordinal)),
                ExchangeType.Direct,
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>>(),
                false, false, It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, bool, IDictionary<string, object?>, bool, bool, CancellationToken>(
                (_, _, _, autoDelete, _, _, _, _) => capturedDlxAutoDelete = autoDelete)
            .Returns(Task.CompletedTask);
        // Other ExchangeDeclareAsync / QueueDeclareAsync / QueueBindAsync calls accept anything.
        channel
            .Setup(c => c.QueueDeclareAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>>(), false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueueDeclareOk("q", 0, 0));
        channel
            .Setup(c => c.QueueBindAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var provisioner = new RabbitMqTopologyProvisioner(NullLogger.Instance);
        await provisioner.ConfigureRetryTopologyAsync(
            channel.Object,
            queueName: "main-q",
            durable: true,
            autoDelete: callerAutoDelete,
            retryDelayMs: 1000,
            retryQueueArguments: new Dictionary<string, object?>(),
            isInitialSetup: true);

        Assert.False(capturedDlxAutoDelete);
    }
}
```

- [ ] **Step 2: Write the H1 unit test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqTopologyProvisionerRetryArgumentsTests.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class RabbitMqTopologyProvisionerRetryArgumentsTests
{
    [Fact]
    public async Task ConfigureRetryTopologyAsync_CallerSuppliesFrameworkArgs_FrameworkValuesWin_AndDebugLogged()
    {
        IDictionary<string, object?>? capturedArgs = null;
        var channel = new Mock<IChannel>();
        channel
            .Setup(c => c.ExchangeDeclareAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>>(), false, false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel
            .Setup(c => c.QueueBindAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel
            .Setup(c => c.QueueDeclareAsync(
                It.Is<string>(s => s.EndsWith(".Retries", StringComparison.Ordinal)),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>>(),
                false, false, It.IsAny<CancellationToken>()))
            .Callback<string, bool, bool, bool, IDictionary<string, object?>, bool, bool, CancellationToken>(
                (_, _, _, _, args, _, _, _) => capturedArgs = new Dictionary<string, object?>(args!, StringComparer.Ordinal))
            .ReturnsAsync(new QueueDeclareOk("q", 0, 0));

        var captured = new List<(LogLevel Level, string Message)>();
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.Log(
            It.IsAny<LogLevel>(),
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()))
            .Callback(new InvocationAction(invocation =>
            {
                var level = (LogLevel)invocation.Arguments[0];
                var state = invocation.Arguments[2];
                var formatter = invocation.Arguments[4];
                var message = (string)formatter.GetType().GetMethod("Invoke")!.Invoke(formatter, [state, null])!;
                captured.Add((level, message));
            }));

        var caller = new Dictionary<string, object?>
        {
            [RabbitMqQueueNaming.XDeadLetterExchangeArgument] = "user-supplied-dlx",
            [RabbitMqQueueNaming.XMessageTtlArgument] = 999,
            ["x-max-length"] = 1000,
        };

        var provisioner = new RabbitMqTopologyProvisioner(logger.Object);
        await provisioner.ConfigureRetryTopologyAsync(
            channel.Object,
            queueName: "main-q",
            durable: true,
            autoDelete: false,
            retryDelayMs: 5000,
            retryQueueArguments: caller,
            isInitialSetup: true);

        Assert.NotNull(capturedArgs);
        Assert.Equal("main-q.Retries.DeadLetter", capturedArgs![RabbitMqQueueNaming.XDeadLetterExchangeArgument]);
        Assert.Equal(5000, capturedArgs[RabbitMqQueueNaming.XMessageTtlArgument]);
        Assert.Equal(1000, capturedArgs["x-max-length"]); // non-conflicting key flows through

        var debugLogs = captured.Where(l => l.Level == LogLevel.Debug).ToList();
        Assert.Equal(2, debugLogs.Count);
        Assert.Contains(debugLogs, l => l.Message.Contains(RabbitMqQueueNaming.XDeadLetterExchangeArgument));
        Assert.Contains(debugLogs, l => l.Message.Contains(RabbitMqQueueNaming.XMessageTtlArgument));
    }
}
```

- [ ] **Step 3: Run both tests to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMqTopologyProvisionerRetryDlxAutoDeleteTests or FullyQualifiedName~RabbitMqTopologyProvisionerRetryArgumentsTests" -m:1
```

Expected:
- C12 test fails — pre-fix, DLX `autoDelete` is the caller's value (`true` when `callerAutoDelete:true`).
- H1 test throws `ArgumentException` from the collection-initializer's `Add(KEY, VAL)` because `caller` already contains both keys.

- [ ] **Step 4: Apply the C12 + H1 fix**

In `src/ServiceConnect.Client.RabbitMQ/Topology/RabbitMqTopologyProvisioner.cs`, locate `ConfigureRetryTopologyAsync`. Two edits:

**Edit 1 (C12).** Change the DLX declaration to hard-code `autoDelete: false`:

```csharp
// before
await channel.ExchangeDeclareAsync(retryDeadLetterExchangeName, ExchangeType.Direct, durable, autoDelete, null, cancellationToken: cancellationToken).ConfigureAwait(false);

// after
// Retry DLX is always autoDelete:false: it must outlive any individual queue lifecycle so
// retried messages always have somewhere to land. The caller-supplied `autoDelete` parameter
// continues to govern the main queue (declared elsewhere) but the retry DLX is invariant.
await channel.ExchangeDeclareAsync(retryDeadLetterExchangeName, ExchangeType.Direct, durable, autoDelete: false, null, cancellationToken: cancellationToken).ConfigureAwait(false);
```

**Edit 2 (H1).** Replace the collection-initializer argument-merge with explicit indexer assignment + override-log helper:

```csharp
// before
Dictionary<string, object?> arguments = new(retryQueueArguments, StringComparer.Ordinal)
{
    {RabbitMqQueueNaming.XDeadLetterExchangeArgument, retryDeadLetterExchangeName},
    {RabbitMqQueueNaming.XMessageTtlArgument, retryDelayMs}
};

// after
Dictionary<string, object?> arguments = new(retryQueueArguments, StringComparer.Ordinal);

// Framework values for these two keys are non-negotiable: they wire the retry queue to the
// retry DLX with the configured TTL. Caller-supplied values are overridden silently except
// for a Debug log so config drift surfaces without polluting Information.
LogIfOverriding(RabbitMqQueueNaming.XDeadLetterExchangeArgument, arguments, retryDeadLetterExchangeName);
LogIfOverriding(RabbitMqQueueNaming.XMessageTtlArgument, arguments, retryDelayMs);

arguments[RabbitMqQueueNaming.XDeadLetterExchangeArgument] = retryDeadLetterExchangeName;
arguments[RabbitMqQueueNaming.XMessageTtlArgument] = retryDelayMs;
```

Add the helper as a private method on `RabbitMqTopologyProvisioner`:

```csharp
private void LogIfOverriding<T>(string key, IReadOnlyDictionary<string, object?> existing, T frameworkValue)
{
    if (existing.TryGetValue(key, out var existingValue) && !Equals(existingValue, frameworkValue))
    {
        _logger.LogDebug(
            "Overriding caller-supplied retry-queue argument {Key} (was '{ExistingValue}') with framework value '{FrameworkValue}'",
            key, existingValue, frameworkValue);
    }
}
```

(`_logger` is already a field on the class.)

- [ ] **Step 5: Run both tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMqTopologyProvisioner" -m:1
```

Expected: all pass (Task 6 tests + any pre-existing topology tests).

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqTopologyProvisionerRetryDlxAutoDeleteTests.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqTopologyProvisionerRetryArgumentsTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Topology/RabbitMqTopologyProvisioner.cs
git commit -m "fix(topology): retry DLX always durable; framework retry args win"
```

---

## Task 7: C12 E2E — DLX outlives auto-deleted main queue

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/RabbitMq/RetryTopologyAutoDeleteE2ETests.cs`

This task lands as its own commit because the unit fix already shipped in Task 6; the E2E test confirms the *user-visible* contract (no message loss). Testcontainers spin-up is ~5-10 s.

- [ ] **Step 1: Locate existing Testcontainers / E2E patterns**

```bash
grep -ln "RabbitMqContainer\|TestcontainersBuilder\|new RabbitMqBuilder" src/ServiceConnect.EndToEndTests/ -r | head -5
```

Reuse the existing fixture shape for spinning up RabbitMQ (likely `RabbitMqChannelStressE2ETests.cs` or similar).

- [ ] **Step 2: Write the failing-without-fix E2E test**

Create `src/ServiceConnect.EndToEndTests/RabbitMq/RetryTopologyAutoDeleteE2ETests.cs`. The exact RabbitMQ-container fixture wiring depends on the existing pattern; the structure of the test is:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using ServiceConnect.Client.RabbitMQ;

namespace ServiceConnect.EndToEndTests.RabbitMq;

[Collection(nameof(RabbitMqContainerCollection))]   // reuse existing container fixture if one exists
public sealed class RetryTopologyAutoDeleteE2ETests
{
    private readonly RabbitMqContainerFixture _fixture;

    public RetryTopologyAutoDeleteE2ETests(RabbitMqContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RetryDlx_OutlivesAutoDeletedMainQueue_MessageRedeliversAfterMainQueueRecreate()
    {
        // 1. Spin up — fixture already has a running broker.
        var connectionFactory = new ConnectionFactory { Uri = new Uri(_fixture.AmqpUri) };
        await using var connection = await connectionFactory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        var queueName = $"test-q-{Guid.NewGuid():N}";
        var retryQueueName = queueName + ".Retries";
        var retryDlxName = queueName + ".Retries.DeadLetter";

        var provisioner = new RabbitMqTopologyProvisioner(NullLogger.Instance);

        // 2. Provision retry topology with autoDelete:true on the main queue.
        await channel.QueueDeclareAsync(queueName, durable: false, exclusive: false, autoDelete: true, arguments: null);
        await provisioner.ConfigureRetryTopologyAsync(
            channel,
            queueName,
            durable: false,
            autoDelete: true,
            retryDelayMs: 1000,
            retryQueueArguments: new Dictionary<string, object?>(),
            isInitialSetup: true);

        // 3. Publish into the retry queue (simulates a nacked message routed via DLX).
        var props = new BasicProperties { Persistent = false };
        await channel.BasicPublishAsync(
            exchange: retryDlxName,
            routingKey: retryQueueName,
            mandatory: false,
            basicProperties: props,
            body: new byte[] { 1, 2, 3 });

        // 4. Tear down the consumer side: close the channel that owns the main queue. Reopen
        //    a fresh channel; main queue should be gone (autoDelete fired when the consumer-channel dropped).
        await channel.CloseAsync();
        await using var channel2 = await connection.CreateChannelAsync();
        var notFound = await Assert.ThrowsAsync<OperationInterruptedException>(() =>
            channel2.QueueDeclarePassiveAsync(queueName));
        Assert.Equal(404, notFound.ShutdownReason!.ReplyCode);

        // 5. Wait for the retry queue's TTL to expire (1s + jitter).
        await Task.Delay(1500);

        // 6. Pre-fix: DLX would also be gone (autoDelete:true), and the dead-lettering attempt
        //    when the retry message expires would silently drop the message.
        //    Post-fix: DLX is autoDelete:false, so it is still alive. Re-declare the main queue
        //    with the same name and re-bind it to the DLX; the dead-lettered message should
        //    be redelivered.
        await channel2.QueueDeclareAsync(queueName, durable: false, exclusive: false, autoDelete: true, arguments: null);
        await channel2.QueueBindAsync(queueName, retryDlxName, retryQueueName, null);

        // 7. Consume from the main queue — the message should arrive within a few seconds.
        var receivedTcs = new TaskCompletionSource<byte[]>();
        var consumer = new RabbitMQ.Client.Events.AsyncEventingBasicConsumer(channel2);
        consumer.ReceivedAsync += (_, args) =>
        {
            receivedTcs.TrySetResult(args.Body.ToArray());
            return Task.CompletedTask;
        };
        await channel2.BasicConsumeAsync(queueName, autoAck: true, consumer);

        // Expectation post-fix: message arrives. Pre-fix: timeout.
        var received = await receivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 1, 2, 3 }, received);
    }
}
```

Adapt to the existing fixture shape — if the project uses `xunit.IClassFixture<RabbitMqContainerFixture>` instead of `[Collection]`, mirror that.

- [ ] **Step 3: Run the E2E test to verify pass (with fix already in place)**

```bash
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "FullyQualifiedName~RetryTopologyAutoDeleteE2ETests" -m:1
```

Expected: 1 passed (the C12 unit fix from Task 6 makes the E2E green). If the test fails despite Task 6 being in place, debug — likely the test has the wrong queue/exchange naming or TTL-expiry race; do not roll back the fix.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/RabbitMq/RetryTopologyAutoDeleteE2ETests.cs
git commit -m "test(topology): E2E coverage for retry DLX outliving auto-deleted main queue"
```

---

## Task 8: H21 — Bus drops the dead `MessageType` stamp

**Files:**
- Create: `src/ServiceConnect.UnitTests/Bus/BusEnvelopeMessageTypeTests.cs`
- Create or modify: `src/ServiceConnect.UnitTests/Bus/BusReservedHeadersTests.cs`
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/OutboundHeaderBuilderOperationNameTests.cs`
- Modify: `src/ServiceConnect/Bus.cs`

This task is cross-package (Bus.cs in core, plus a confirmation test in the RabbitMQ package). The fix removes three sites in `Bus.cs`; tests assert the new contract on both sides.

- [ ] **Step 1: Locate any existing test that asserts `MessageType` rejection**

```bash
grep -nrE "MessageType.*reserved|ReservedHeaders.*MessageType|caller.*MessageType" src/ServiceConnect.UnitTests/ | head -10
```

If a test asserts that caller-supplied `MessageType` is *rejected* / *ignored* by the Bus, we'll invert it. If none exists, skip — Task 8 will create both contract-confirmations from scratch.

- [ ] **Step 2: Write/modify the Bus envelope assertion (no MessageType key)**

Create `src/ServiceConnect.UnitTests/Bus/BusEnvelopeMessageTypeTests.cs`. The exact arrange shape depends on existing Bus test fixtures. Locate one first:

```bash
grep -ln "new Bus(" src/ServiceConnect.UnitTests/Bus/ | head -3
```

Build the test on top of that arrange-shape:

```csharp
using ServiceConnect;
using ServiceConnect.Interfaces;
// ... project-specific arrange usings ...

namespace ServiceConnect.UnitTests.Bus;

public sealed class BusEnvelopeMessageTypeTests
{
    [Fact]
    public async Task PublishAsync_EnvelopeDoesNotContainMessageTypeKey()
    {
        // [Reuse the Bus arrange harness used by the existing Publish-related tests.]
        // Capture the envelope handed to ExecuteOutgoingFiltersAsync. Assert:
        //   Assert.False(captured.Headers.ContainsKey(HeaderKeys.MessageType));
        //
        // Pre-fix: ContainsKey is true (Bus.cs:651 stamps it).
        // Post-fix: ContainsKey is false; only the producer's OutboundHeaderBuilder stamps it.
    }

    [Fact]
    public async Task SendAsync_EnvelopeDoesNotContainMessageTypeKey()
    {
        // Same shape as above but exercising the SendAsync path (which currently stamps
        // MessageType at Bus.cs:730).
    }

    [Fact]
    public async Task PublishAsync_EnvelopePreservesCorrelationIdAndMessageId_StillStampedByBus()
    {
        // Belt-and-braces guard: removing one reservation shouldn't accidentally drop the others.
        // Assert.True(captured.Headers.ContainsKey(HeaderKeys.MessageId));
        // Assert.True(captured.Headers.ContainsKey(HeaderKeys.CorrelationId));
    }
}
```

The implementer fills in the harness using the existing Bus test setup — exact mock/fake wiring depends on what already exists. If no Bus harness lets the test capture the envelope handed to outgoing filters, expose a test seam (e.g. by mocking `IFilterPipeline`) that captures the argument.

- [ ] **Step 3: If a `BusReservedHeadersTests` exists, invert the `MessageType` assertion**

```bash
grep -ln "BusReservedHeadersTests\|ReservedHeaders" src/ServiceConnect.UnitTests/Bus/ src/ServiceConnect.UnitTests/ | head -5
```

If a test asserts caller-supplied `MessageType = "SpoofedType"` is dropped + a warning is logged, invert it: caller-supplied `MessageType` now flows through to the producer, and no warning is logged. (The producer overwrites it — that's the producer's responsibility, not the Bus's. The Bus stops being a defensive stamper for this key.)

If no such test exists, write a small one:

```csharp
[Fact]
public async Task PublishAsync_CallerSuppliesMessageType_FlowsThroughEnvelope_NoWarning()
{
    // Capture envelope as in BusEnvelopeMessageTypeTests.
    var caller = new Dictionary<string, string>
    {
        [HeaderKeys.MessageType] = "caller.spoof",
    };
    // ... bus.PublishAsync(msg, options with caller headers) ...

    // Post-fix: the caller value flows through to the producer. The producer will
    // overwrite it with "Publish" before the wire — but that is the producer's
    // contract, not the Bus's.
    Assert.Equal("caller.spoof", captured.Headers[HeaderKeys.MessageType]);
    // No warning log emitted by the Bus for MessageType.
    Assert.DoesNotContain(busLogs.OfLevel(LogLevel.Warning), m => m.Contains(HeaderKeys.MessageType));
}
```

- [ ] **Step 4: Write the OutboundHeaderBuilder operation-name confirmation test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/OutboundHeaderBuilderOperationNameTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class OutboundHeaderBuilderOperationNameTests
{
    private static OutboundHeaderBuilder CreateBuilder()
    {
        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);
        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("q");
        return new OutboundHeaderBuilder(busConfig.Object, queueConfig.Object, new FakeTimeProvider(), NullLogger.Instance);
    }

    [Theory]
    [InlineData("Publish")]
    [InlineData("Send")]
    [InlineData("ByteStream")]
    public void BuildHeaders_StampsOperationNameInMessageType(string operation)
    {
        var headers = CreateBuilder().BuildHeaders(typeof(string), null, "queue", operation);

        // Post-H21 contract: MessageType is the operation name on the wire.
        // (Bus's FullName stamp is gone; this builder is now the sole stamper.)
        Assert.Equal(operation, headers[HeaderKeys.MessageType]);
        Assert.Equal(typeof(string).FullName, headers[HeaderKeys.TypeName]);
        Assert.Equal(typeof(string).AssemblyQualifiedName, headers[HeaderKeys.FullTypeName]);
    }

    [Fact]
    public void BuildHeaders_CallerSuppliesMessageType_OverwrittenByOperationName()
    {
        var caller = new Dictionary<string, string>
        {
            [HeaderKeys.MessageType] = "caller.spoof",
        };
        var headers = CreateBuilder().BuildHeaders(typeof(string), caller, "queue", "Publish");

        Assert.Equal("Publish", headers[HeaderKeys.MessageType]);
    }
}
```

(This test depends on the smaller-fix from Task 3 — the warning log will fire on the second test. We don't assert against it here; Task 3 covers that contract.)

- [ ] **Step 5: Run all three test files to verify failure**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~BusEnvelopeMessageTypeTests or FullyQualifiedName~OutboundHeaderBuilderOperationNameTests" -m:1
```

Expected:
- `BusEnvelopeMessageTypeTests.PublishAsync_EnvelopeDoesNotContainMessageTypeKey` and `.SendAsync_EnvelopeDoesNotContainMessageTypeKey` fail — the Bus still stamps `MessageType`.
- `BusEnvelopeMessageTypeTests.PublishAsync_EnvelopePreservesCorrelationIdAndMessageId_StillStampedByBus` passes (those stamps stay).
- `OutboundHeaderBuilderOperationNameTests` mostly passes already — it asserts the producer's behaviour which is unchanged. Confirms no regression.

If a `BusReservedHeadersTests` test was inverted, it should also fail pre-fix.

- [ ] **Step 6: Apply the H21 fix in `Bus.cs`**

In `src/ServiceConnect/Bus.cs`, three site changes:

**Edit 1.** Locate the `ReservedHeaders` set (currently around line 615). Remove `HeaderKeys.MessageType`:

```csharp
// before
private static readonly HashSet<string> ReservedHeaders = new(StringComparer.Ordinal)
{
    HeaderKeys.MessageType,
    HeaderKeys.CorrelationId,
    HeaderKeys.MessageId,
};

// after
private static readonly HashSet<string> ReservedHeaders = new(StringComparer.Ordinal)
{
    HeaderKeys.CorrelationId,
    HeaderKeys.MessageId,
};
```

**Edit 2.** In `CreateEnvelope`, locate and delete the `MessageType` stamp line (currently `envelope.Headers[HeaderKeys.MessageType] = messageType.FullName ?? messageType.Name;`). Update the preceding comment from:

```csharp
// Bus-authoritative: stamp system headers last so callers cannot spoof via options.Headers.
// Outgoing filters and middleware rely on MessageId / CorrelationId / MessageType being present.
```

to:

```csharp
// Bus-authoritative: stamp system headers last so callers cannot spoof via options.Headers.
// Outgoing filters and middleware rely on MessageId / CorrelationId being present. MessageType
// is stamped authoritatively by the producer (OutboundHeaderBuilder) as the operation name
// "Publish"|"Send"|"ByteStream"; type info is carried by TypeName / FullTypeName.
```

The two remaining stamp lines (`MessageId` and `CorrelationId`) stay.

**Edit 3.** Locate the equivalent `MessageType` stamp in `SendWithMiddleware` (the `headers[HeaderKeys.MessageType] = messageType.FullName ?? messageType.Name;` line — currently around line 730 — search for the second occurrence). Delete it. The surrounding stamps for `MessageId` / `CorrelationId` (if present) stay.

Verify both deletions:

```bash
grep -n "HeaderKeys.MessageType" src/ServiceConnect/Bus.cs
```

Expected: zero hits post-fix.

- [ ] **Step 7: Run the tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Bus or FullyQualifiedName~OutboundHeaderBuilder" -m:1
```

Expected: all pass.

Re-run a broader gate to catch any consumer-side test that depended on Bus stamping `MessageType`:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Stream or FullyQualifiedName~Audit" -m:1
```

Expected: all pass (StreamProcessor / MessageAuditPublisher already operate on operation-name semantics; nothing should regress).

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect/Bus.cs \
        src/ServiceConnect.UnitTests/Bus/BusEnvelopeMessageTypeTests.cs \
        src/ServiceConnect.UnitTests/Bus/BusReservedHeadersTests.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/OutboundHeaderBuilderOperationNameTests.cs
git commit -m "refactor(bus): drop dead MessageType stamp; keep operation-name on the wire"
```

(Drop `BusReservedHeadersTests.cs` from the add list if it didn't exist and Task 8 didn't create it.)

---

## Task 9: H22 — defer publish-timeout reset off the publish lock (unit)

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs` (`_resetRequired` flag + `MarkResetRequired()` + flag-consume in `EnsureConnectedAsync`)
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs` (`PublishWithTimeoutAsync` catch path)
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/ProducerPublishTimeoutResetTests.cs`

This is the riskiest change in the phase; lands in its own commit. R7 (`EnsureExchangeDeclaredAsync` race) is now more frequently exercised — Task 10's E2E test catches surface-level regression; the proper fix stays in Phase 6.

- [ ] **Step 1: Add `_resetRequired` flag + helpers to `ProducerConnection`**

In `src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs`:

Add the field below the existing private fields (near `_connected`):

```csharp
// Set by Producer.PublishWithTimeoutAsync when a publish times out (broker confirm did not
// arrive within the publish budget). The next EnsureConnectedAsync drives the reconnect off
// the publish lock so concurrent publishers are not blocked behind a worst-case retry budget.
private int _resetRequired;
```

Add two methods (place just after `IsHealthy()`):

```csharp
/// <summary>
/// Marks the connection for reset on the next call to <see cref="EnsureConnectedAsync"/>.
/// Synchronous and idempotent. Used by Producer.PublishWithTimeoutAsync to avoid awaiting
/// ReconnectAsync while holding the publish lock.
/// </summary>
internal void MarkResetRequired() => Interlocked.Exchange(ref _resetRequired, 1);

/// <summary>Test seam: snapshot of the reset flag for unit-test assertions.</summary>
internal bool ResetRequiredForTests => Volatile.Read(ref _resetRequired) == 1;
```

Modify `EnsureConnectedAsync` to consume the flag *before* returning a healthy channel. Replace:

```csharp
public async Task EnsureConnectedAsync(CancellationToken cancellationToken)
{
    if (IsHealthy())
    {
        return;
    }

    await _connectionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        if (IsHealthy())
        {
            return;
        }

        await Retry.DoAsync(() => CreateConnectionAsync(cancellationToken), async ex =>
        {
            _logger.LogError(ex, "Error creating connection");
            await TearDownChannelAndConnectionAsync().ConfigureAwait(false);
        }, TimeSpan.FromSeconds(_retryTimeInSeconds), _retryCount, cancellationToken).ConfigureAwait(false);

        _connected = true;
    }
    finally
    {
        _connectionSemaphore.Release();
    }
}
```

with:

```csharp
public async Task EnsureConnectedAsync(CancellationToken cancellationToken)
{
    // Atomically consume the reset-required flag set by a prior publish timeout. The
    // ReconnectAsync below holds _connectionSemaphore (NOT the producer's _publishLock),
    // so concurrent publishers waiting on the publish lock are not blocked here. Only one
    // caller succeeds at the Exchange — the rest see flag == 0 and proceed normally.
    if (Interlocked.Exchange(ref _resetRequired, 0) == 1)
    {
        await ReconnectAsync(
            new InvalidOperationException("Channel reset required after publish timeout"),
            cancellationToken).ConfigureAwait(false);
        // ReconnectAsync calls EnsureConnectedAsync internally on the no-test-hook path, so
        // we are already healthy on return. Fall through for explicit safety in case the
        // test-hook path replaces ReconnectAsync.
    }

    if (IsHealthy())
    {
        return;
    }

    await _connectionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        if (IsHealthy())
        {
            return;
        }

        await Retry.DoAsync(() => CreateConnectionAsync(cancellationToken), async ex =>
        {
            _logger.LogError(ex, "Error creating connection");
            await TearDownChannelAndConnectionAsync().ConfigureAwait(false);
        }, TimeSpan.FromSeconds(_retryTimeInSeconds), _retryCount, cancellationToken).ConfigureAwait(false);

        _connected = true;
    }
    finally
    {
        _connectionSemaphore.Release();
    }
}
```

- [ ] **Step 2: Replace in-catch reconnect in `Producer.PublishWithTimeoutAsync`**

In `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs`, locate `PublishWithTimeoutAsync`. The current catch block is:

```csharp
catch (OperationCanceledException oce) when (linked.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
{
    // Reset the channel: the broker may eventually ack this timed-out publish, which
    // would contaminate the confirm slot of a later in-flight publish. A fresh
    // connection + channel clears the confirm-tracker's state.
    try { await _producerConnection.ReconnectAsync(oce, cancellationToken).ConfigureAwait(false); }
    catch (Exception resetEx) { _logger.LogError(resetEx, "Failed to reset connection after publish timeout; channel state may be indeterminate."); }

    throw new TimeoutException(
        $"BasicPublishAsync exceeded the configured publish timeout of {_publishTimeout.TotalSeconds:0.###}s " +
        $"(exchange='{exchange}', routingKey='{routingKey}', messageId='{basicProperties.MessageId ?? "<none>"}'). " +
        "The broker may be stalled or the connection may be half-open.");
}
```

Replace with:

```csharp
catch (OperationCanceledException) when (linked.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
{
    // Mark the channel for reset on the next publish. The reset runs inside EnsureConnectedAsync,
    // which is called BEFORE _publishLock.WaitAsync, so concurrent publishers are not blocked
    // behind it. We do NOT reconnect here: doing so would hold _publishLock for up to
    // retryCount * retrySeconds (default 60 * 10s = 10 minutes) blocking every other publisher.
    // The broker may still eventually ack this timed-out publish; a fresh connection + channel
    // on the next publish clears the confirm-tracker's state before any subsequent publish runs.
    _producerConnection.MarkResetRequired();

    throw new TimeoutException(
        $"BasicPublishAsync exceeded the configured publish timeout of {_publishTimeout.TotalSeconds:0.###}s " +
        $"(exchange='{exchange}', routingKey='{routingKey}', messageId='{basicProperties.MessageId ?? "<none>"}'). " +
        "The broker may be stalled or the connection may be half-open.");
}
```

Note: the `oce` variable is no longer used (it was only used to pass to `ReconnectAsync`); drop the binding.

- [ ] **Step 3: Write the failing tests**

Create `src/ServiceConnect.UnitTests/RabbitMQ/ProducerPublishTimeoutResetTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class ProducerPublishTimeoutResetTests
{
    private static (Producer producer, Mock<IChannel> channel, int reconnectCalls) BuildProducerWithSlowChannel(
        TimeSpan publishTimeout, TimeSpan publishDelay)
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.PublishTimeout] = publishTimeout,
            [RabbitMQSettingKeys.RetryCount] = (ushort)1,
            [RabbitMQSettingKeys.RetrySeconds] = (ushort)1,
        });

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("q");

        var busConfig = new Mock<IBusConfiguration>();

        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, bool _, BasicProperties _, ReadOnlyMemory<byte> _, CancellationToken ct) =>
            {
                await Task.Delay(publishDelay, ct);
            });

        var producer = new Producer(transport.Object, queueConfig.Object, busConfig.Object, NullLogger<Producer>.Instance);

        // Wire a fake connection so EnsureConnectedAsync yields our captured channel.
        var fakeConnection = new Mock<IConnection>();
        fakeConnection.SetupGet(c => c.IsOpen).Returns(true);
        fakeConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel.Object);
        producer.CreateConnectionForTests = (_, _, _, _) => Task.FromResult(fakeConnection.Object);

        // Reconnect probe — count invocations.
        int reconnectCount = 0;
        producer.ReconnectForTests = _ =>
        {
            Interlocked.Increment(ref reconnectCount);
            return Task.CompletedTask;
        };

        return (producer, channel, reconnectCount);  // Note: reconnectCount captured by value here; readers should grab the field directly via reflection or a wrapper.
    }

    [Fact]
    public async Task PublishTimeout_Throws_DoesNotReconnect_FlagsResetRequired()
    {
        // Arrange: 50ms publish timeout, 500ms simulated publish delay.
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.PublishTimeout] = TimeSpan.FromMilliseconds(50),
            [RabbitMQSettingKeys.RetryCount] = (ushort)1,
            [RabbitMQSettingKeys.RetrySeconds] = (ushort)1,
        });

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("q");

        var busConfig = new Mock<IBusConfiguration>();

        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, bool _, BasicProperties _, ReadOnlyMemory<byte> _, CancellationToken ct) =>
            {
                await Task.Delay(500, ct);
            });
        // ExchangeDeclareAsync is no-op
        channel
            .Setup(c => c.ExchangeDeclareAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>>(), false, false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var producer = new Producer(transport.Object, queueConfig.Object, busConfig.Object, NullLogger<Producer>.Instance);

        var fakeConnection = new Mock<IConnection>();
        fakeConnection.SetupGet(c => c.IsOpen).Returns(true);
        fakeConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel.Object);
        producer.CreateConnectionForTests = (_, _, _, _) => Task.FromResult(fakeConnection.Object);

        int reconnectCalls = 0;
        producer.ReconnectForTests = _ => { Interlocked.Increment(ref reconnectCalls); return Task.CompletedTask; };

        // Act: publish should time out.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.PublishAsync(typeof(string), [1, 2, 3]));

        // Assert: reconnect probe was NOT called from inside PublishWithTimeoutAsync's catch path.
        Assert.Equal(0, reconnectCalls);

        // Assert: reset-required flag is set on _producerConnection. Use the internal accessor.
        Assert.True(GetProducerConnection(producer).ResetRequiredForTests);
    }

    [Fact]
    public async Task NextPublishAfterTimeout_DrivesReset_BeforeAcquiringPublishLock()
    {
        // Same arrange as Test 1, but the second publish uses a fast channel and we assert
        // that reconnect is invoked exactly once (driven by the second publish's
        // EnsureConnectedAsync) before the second publish actually runs.

        // Channel that delays the FIRST publish but completes the SECOND immediately.
        int publishAttempt = 0;
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, bool _, BasicProperties _, ReadOnlyMemory<byte> _, CancellationToken ct) =>
            {
                if (Interlocked.Increment(ref publishAttempt) == 1)
                {
                    await Task.Delay(500, ct); // first publish times out
                }
                // Second publish returns immediately.
            });
        channel
            .Setup(c => c.ExchangeDeclareAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>>(), false, false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.PublishTimeout] = TimeSpan.FromMilliseconds(50),
            [RabbitMQSettingKeys.RetryCount] = (ushort)1,
            [RabbitMQSettingKeys.RetrySeconds] = (ushort)1,
        });
        var queueConfig = new Mock<IQueueConfiguration>(); queueConfig.SetupGet(q => q.QueueName).Returns("q");
        var busConfig = new Mock<IBusConfiguration>();
        var producer = new Producer(transport.Object, queueConfig.Object, busConfig.Object, NullLogger<Producer>.Instance);

        var fakeConnection = new Mock<IConnection>();
        fakeConnection.SetupGet(c => c.IsOpen).Returns(true);
        fakeConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel.Object);
        producer.CreateConnectionForTests = (_, _, _, _) => Task.FromResult(fakeConnection.Object);

        int reconnectCalls = 0;
        producer.ReconnectForTests = _ => { Interlocked.Increment(ref reconnectCalls); return Task.CompletedTask; };

        // Act: first publish times out → flag set, reconnect not called.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.PublishAsync(typeof(string), [1, 2, 3]));
        Assert.Equal(0, reconnectCalls);
        Assert.True(GetProducerConnection(producer).ResetRequiredForTests);

        // Act: second publish drives the reset before its own publish runs.
        await producer.PublishAsync(typeof(string), [4, 5, 6]);

        // Assert: exactly one reconnect was driven by the second publish.
        Assert.Equal(1, reconnectCalls);
        // Assert: flag was consumed.
        Assert.False(GetProducerConnection(producer).ResetRequiredForTests);
    }

    [Fact]
    public async Task MarkResetRequired_IsIdempotent_ConcurrentTimeouts_OneReset()
    {
        // Arrange: two concurrent publishes both time out.
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, bool _, BasicProperties _, ReadOnlyMemory<byte> _, CancellationToken ct) =>
            {
                await Task.Delay(500, ct);
            });
        channel
            .Setup(c => c.ExchangeDeclareAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>>(), false, false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.PublishTimeout] = TimeSpan.FromMilliseconds(50),
            [RabbitMQSettingKeys.RetryCount] = (ushort)1,
            [RabbitMQSettingKeys.RetrySeconds] = (ushort)1,
        });
        var queueConfig = new Mock<IQueueConfiguration>(); queueConfig.SetupGet(q => q.QueueName).Returns("q");
        var busConfig = new Mock<IBusConfiguration>();
        var producer = new Producer(transport.Object, queueConfig.Object, busConfig.Object, NullLogger<Producer>.Instance);

        var fakeConnection = new Mock<IConnection>();
        fakeConnection.SetupGet(c => c.IsOpen).Returns(true);
        fakeConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel.Object);
        producer.CreateConnectionForTests = (_, _, _, _) => Task.FromResult(fakeConnection.Object);

        int reconnectCalls = 0;
        producer.ReconnectForTests = _ => { Interlocked.Increment(ref reconnectCalls); return Task.CompletedTask; };

        // Two concurrent timeouts.
        var t1 = Assert.ThrowsAsync<TimeoutException>(() => producer.PublishAsync(typeof(string), [1]));
        var t2 = Assert.ThrowsAsync<TimeoutException>(() => producer.PublishAsync(typeof(string), [2]));
        await Task.WhenAll(t1, t2);

        Assert.Equal(0, reconnectCalls);
        Assert.True(GetProducerConnection(producer).ResetRequiredForTests);

        // A subsequent fast publish should drive exactly ONE reset (the flag is consumed atomically).
        // Replace the slow-publish setup with an immediate-completion one to avoid another timeout.
        channel.Reset();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        channel
            .Setup(c => c.ExchangeDeclareAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>>(), false, false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await producer.PublishAsync(typeof(string), [3]);

        Assert.Equal(1, reconnectCalls);
        Assert.False(GetProducerConnection(producer).ResetRequiredForTests);
    }

    // Reflection helper — Producer doesn't expose ProducerConnection directly. Tests are inside
    // the same assembly (InternalsVisibleTo); use a typed property if Producer adds one, else reflect.
    private static ProducerConnection GetProducerConnection(Producer producer)
    {
        var field = typeof(Producer).GetField("_producerConnection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return (ProducerConnection)field!.GetValue(producer)!;
    }
}
```

If reflection on `_producerConnection` is too brittle, add a small `internal ProducerConnection ProducerConnectionForTests => _producerConnection;` accessor to `Producer.cs` and use it instead. The plan favours reflection only because it adds zero production-surface; the implementer may pick the cleaner accessor approach.

- [ ] **Step 4: Run the tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ProducerPublishTimeoutResetTests" -m:1
```

Expected: 3 passed.

Run the broader Producer + ProducerConnection filters to catch regressions in existing publish-timeout / reconnect tests:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Producer" -m:1
```

Expected: all pass — including the pre-existing `ProducerPublishTimeoutTests` (which asserted `TimeoutException` is thrown — still true post-fix; it just no longer asserts that reconnect happened in-line).

If a pre-existing test asserted "publish-timeout triggers reconnect under the lock" (i.e. enforced the bug), update it to assert the new contract: timeout sets the reset flag, next publish drives the reconnect. The implementer should look for and surface any such locked-in-bug test rather than silently break it.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/ProducerPublishTimeoutResetTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs \
        src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs
git commit -m "fix(producer): defer connection reset off the publish lock"
```

---

## Task 10: H22 E2E — concurrent publishers under timeout (extend existing E2E)

**Files:**
- Modify: `src/ServiceConnect.EndToEndTests/RabbitMq/ProducerPublishTimeoutE2ETests.cs`

This task confirms the user-visible contract (no publisher waits beyond ~`2 × _publishTimeout`). It also acts as the R7 surface-check called out in the spec's "Out of scope" section.

- [ ] **Step 1: Locate the existing E2E fixture and inspect its arrange shape**

```bash
test -f src/ServiceConnect.EndToEndTests/RabbitMq/ProducerPublishTimeoutE2ETests.cs && grep -n "class ProducerPublishTimeoutE2ETests" src/ServiceConnect.EndToEndTests/RabbitMq/ProducerPublishTimeoutE2ETests.cs
```

If the file exists, append a new test method to it. If not (the spec said "extend, don't add" — but verify), create the file using the same Testcontainer-fixture pattern as Task 7's E2E test.

- [ ] **Step 2: Append the test**

Add the following test method (adapt fixture name / containers per the existing class):

```csharp
[Fact]
public async Task ConcurrentPublishersUnderTimeout_NoneBlockedBeyondTwoPublishTimeouts()
{
    // Arrange: configure a tight publish timeout and a broker that artificially slows the FIRST
    // publish only. Ten concurrent publishers; assert no individual publisher's wall time exceeds
    // 2 × _publishTimeout (one timeout + one normal publish post-reset).

    var publishTimeout = TimeSpan.FromMilliseconds(500);
    var transport = TransportPointingAt(_fixture.AmqpUri, publishTimeout);
    // queueConfig + busConfig as per other E2E tests in this fixture

    await using var producer = new Producer(transport, queueConfig, busConfig, _logger);

    // Use toxiproxy or a custom interceptor channel if available to slow the first publish.
    // Simplest: have one publisher publish to a queue with a no-ack consumer that drops the
    // first ack (simulating a stalled broker for that one publish). The remaining nine
    // publishers run concurrently — none should wait > 2 × publishTimeout.

    var tasks = Enumerable.Range(0, 10).Select(async i =>
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // First publisher (i==0) is the one that sees the slow broker; expect TimeoutException.
            // Remaining publishers run normal publishes.
            if (i == 0)
            {
                await Assert.ThrowsAsync<TimeoutException>(() =>
                    producer.PublishAsync(typeof(StalledMessage), [(byte)i]));
            }
            else
            {
                await producer.PublishAsync(typeof(NormalMessage), [(byte)i]);
            }
        }
        finally
        {
            sw.Stop();
            return (Index: i, Elapsed: sw.Elapsed);
        }
    });

    var results = await Task.WhenAll(tasks);

    // Assert: every publisher completed within 2 × publishTimeout of wall-clock. Pre-fix, the
    // non-stalled publishers would block behind the stalled publisher's reconnect-under-lock
    // (worst case 60 * 10s).
    foreach (var (index, elapsed) in results)
    {
        Assert.True(
            elapsed <= TimeSpan.FromMilliseconds(publishTimeout.TotalMilliseconds * 2 + 500), // +500ms slack for E2E noise
            $"Publisher {index} took {elapsed.TotalMilliseconds:F0}ms; expected ≤ {(publishTimeout.TotalMilliseconds * 2 + 500):F0}ms");
    }
}
```

The "stall the first publish" mechanism depends on what's available in the fixture. Two options the implementer may pick from:
1. **Toxiproxy.** If the test fixture already has a Testcontainers-managed toxiproxy, add a `latency` toxic for the first publish window only.
2. **Custom slow handler.** Set up a consumer on the target queue that delays its ack handling such that the publish-confirm doesn't return within the timeout. (Requires `PublisherAcknowledgements=true`, which `RabbitMQSettingKeys.PublisherAcknowledgements` controls.)

If neither is straightforward, fall back to a single-publisher version that asserts the timeout returns within `~publishTimeout + slack` (rather than within `~retryCount * retrySeconds`), which is the load-bearing assertion. The 10-concurrent variant is the strongest E2E catch but is dependent on existing fixture capabilities.

- [ ] **Step 3: Run the E2E test to verify pass**

```bash
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "FullyQualifiedName~ConcurrentPublishersUnderTimeout_NoneBlockedBeyondTwoPublishTimeouts" -m:1
```

Expected: 1 passed (Task 9's fix has already landed). If the test fails, capture the elapsed-time histogram in the failure message — it tells you immediately whether the bug is back or whether the test's slack budget was too tight.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/RabbitMq/ProducerPublishTimeoutE2ETests.cs
git commit -m "test(producer): E2E coverage for concurrent publishers under publish timeout"
```

---

## Task 11: Documentation sweep (website + release notes)

**Files (to be located in Step 1 — exact filenames depend on existing structure):**
- `website/src/content/docs/reference/messages/` — header-keys page
- `website/src/content/docs/learn/operations/` — retry-topology / DLX / dead-letter content
- `website/src/content/docs/reference/configuration/` — `RetryQueueArguments` / `AutoDelete` / `Priority`
- `website/src/content/docs/reference/bus/` — `IBus.Send` multi-endpoint section
- `website/src/content/docs/releases/` — v7 release notes

- [ ] **Step 1: Locate the relevant doc files**

```bash
find website/src/content/docs/reference/messages -name "*.mdx" | head
find website/src/content/docs/learn/operations -name "*.mdx" | head
find website/src/content/docs/reference/configuration -name "*.mdx" | head
find website/src/content/docs/reference/bus -name "*.mdx" | head
find website/src/content/docs/releases -name "*.mdx" | head
grep -rln "MessageType\|HeaderKeys\|retry topology\|DLX\|dead.letter" website/src/content/docs/ | head -20
```

Inventory which pages mention `MessageType`, retry topology / DLX, `RetryQueueArguments`, multi-endpoint Send, and v7 release notes.

- [ ] **Step 2: Update the `MessageType` header-keys reference**

In whichever page documents `HeaderKeys.MessageType` (likely under `reference/messages/`), reposition the semantics from "type FullName" to "operation name". Use this canonical wording (adapt to the page's existing voice):

```mdx
### `MessageType`

The operation flag carried with every outbound message: `"Publish"`, `"Send"`, or `"ByteStream"`.

For type information, use [`TypeName`](#typename) (the message's `FullName`) or [`FullTypeName`](#fulltypename) (its `AssemblyQualifiedName`). `MessageType` is **not** the type — it tells consumers which kind of operation the producer performed, not which type the body deserialises to.
```

If existing prose claims `MessageType` is the FullName, replace it. If the page contains an example showing `MessageType: "MyApp.Domain.MyMessage"`, change it to `MessageType: "Publish"` (or `"Send"` / `"ByteStream"` per context).

- [ ] **Step 3: Update retry topology / DLX content**

In whichever page covers retry topology / DLX behaviour (likely under `learn/operations/`), add a paragraph documenting the durability invariant:

```mdx
The retry dead-letter exchange (DLX) is always declared with `autoDelete: false`, even when the
main consumer queue is auto-deleted. This guarantees that messages dwelling in the retry queue
have somewhere to land when their TTL expires — without this invariant, retried messages are
silently dropped after a consumer disconnects.
```

In the same page (or in `reference/configuration/RetryQueueArguments`), document the framework-wins arg merge:

```mdx
ServiceConnect manages two retry-queue AMQP arguments authoritatively: `x-dead-letter-exchange`
and `x-message-ttl`. If your `RetryQueueArguments` configuration includes either key, the
caller-supplied value is overridden with the framework value at provisioning time and a Debug
log records the override. Other `x-*` AMQP arguments (e.g. `x-max-length`, `x-queue-mode`) flow
through unchanged.
```

- [ ] **Step 4: Update multi-endpoint Send doc**

In whichever page covers `IBus.Send` (likely under `reference/bus/`), add a callout in the multi-endpoint section:

```mdx
:::note
When `Send` resolves to multiple endpoints (via `AddQueueMapping` mappings), each delivery is
an independent on-wire message with a distinct `MessageId`. Use `CorrelationId` to correlate
deliveries that originated from the same logical bus operation.
:::
```

- [ ] **Step 5: Add v7 release notes**

In `website/src/content/docs/releases/` (find the v7 / latest release-notes page), append to a "behaviour changes" section:

```mdx
### Phase 4 — RabbitMQ producer + topology (2026-04-29)

- **Per-delivery `MessageId` for multi-endpoint Send.** Every endpoint resolved by `AddQueueMapping` now receives a delivery with a distinct `MessageId` and `TimeSent`. Correlate cross-endpoint deliveries via `CorrelationId`, which remains constant across the fan-out.
- **`MessageType` header semantics formalised as the operation name.** No observable wire change — the on-wire value has always been `"Publish"`, `"Send"`, or `"ByteStream"`. Downstream auditors that read `MessageType` expecting a type FullName based on out-of-date documentation should switch to `FullTypeName` (the message's `AssemblyQualifiedName`).
- **Retry DLX always durable.** The retry dead-letter exchange is now always declared with `autoDelete: false`, regardless of the consumer queue's auto-delete setting. Pre-existing topologies are unaffected on next provisioning.
- **Caller-supplied retry args overridden with framework values.** `x-dead-letter-exchange` and `x-message-ttl` in `RetryQueueArguments` are now overridden silently with framework values at retry-queue provisioning time. A Debug log records each override. Pre-fix, supplying either key threw `ArgumentException` from the AMQP arg merge.
- **Publish timeout no longer holds the publish lock during reconnect.** A timed-out publish now marks the connection for reset; the next publish drives the actual reconnect off-lock. Concurrent publishers are no longer blocked behind the worst-case retry budget (~10 minutes by default).
```

- [ ] **Step 6: Build the website**

```bash
npm --prefix website install
npm --prefix website run build
```

Expected: succeeds. No new broken-link warnings.

- [ ] **Step 7: Examples / READMEs verification (no edits unless required)**

```bash
grep -rn "MessageId\|MessageType\|multi-endpoint" examples/ README.md 2>/dev/null | head -20
```

Look for assertions that multi-endpoint Send produces a single `MessageId`, or for `MessageType` documented as a type FullName. If any are found, update them in this same task. If nothing is found, no edits are needed — sample tests will continue to pass.

- [ ] **Step 8: Commit**

```bash
git add website/src/content/docs/
# add any examples/ or top-level README updates only if Step 7 turned anything up
git commit -m "docs(website): Phase 4 doc sweep — MessageType, retry topology, multi-endpoint Send"
```

---

## Task 12: Final verification gate

This task is mechanical. It runs all the verification commands the spec calls out before final code review. No code changes — purely a verification commit (or no commit if everything is already green).

- [ ] **Step 1: Per-csproj build cleans**

```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1
```

Expected: both succeed with no warnings introduced by this phase.

- [ ] **Step 2: Unit-test pass — RabbitMQ + Bus filters**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMQ or FullyQualifiedName~Bus" -m:1
```

Expected: all pass. Total count includes Phase 4's new tests (~20+ added).

- [ ] **Step 3: E2E test pass — RetryTopology + ProducerPublishTimeout**

```bash
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "FullyQualifiedName~RetryTopologyAutoDelete or FullyQualifiedName~ProducerPublishTimeout" -m:1
```

Expected: all pass.

- [ ] **Step 4: Astro build clean**

```bash
npm --prefix website run build
```

Expected: succeeds, no new warnings.

- [ ] **Step 5: Grep verifications**

```bash
# H21: Bus no longer stamps MessageType
grep -rn "envelope.Headers\[HeaderKeys.MessageType\] *=" src/ServiceConnect/
# Expected: zero hits.

# C12: DLX hard-coded autoDelete:false
grep -n "ExchangeDeclareAsync.*Direct.*autoDelete" src/ServiceConnect.Client.RabbitMQ/Topology/RabbitMqTopologyProvisioner.cs
# Expected: at least one hit, all DLX-related calls show autoDelete: false.

# H22: timeout path uses MarkResetRequired, not ReconnectAsync
grep -nE "MarkResetRequired|ReconnectAsync" src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs
# Expected: MarkResetRequired present in PublishWithTimeoutAsync's catch; no ReconnectAsync call there.
```

If any grep returns unexpected hits, fix the underlying code in a small follow-up commit and re-run.

- [ ] **Step 6: Final code review**

Dispatch `superpowers:code-reviewer` with model `opus` over all Phase 4 commits (from `4e5afec2` — the spec commit — through to the doc-sweep commit). The reviewer's brief:

```
Review Phase 4 commits (4e5afec2..HEAD) against
docs/superpowers/specs/2026-04-29-phase-04-rabbitmq-producer-topology.md.

Focus on:
- C11: distinct MessageId per delivery on multi-endpoint Send.
- C12 + H1: retry DLX always durable; framework retry args win.
- H21: Bus no longer stamps MessageType; consumer-side semantics intact.
- H22: timeout path uses MarkResetRequired, not ReconnectAsync; flag consumed
  off-lock in EnsureConnectedAsync; concurrent timeouts produce one reset.
- M11/M12/smaller: null guards present; priority log includes value+type;
  reserved-header overwrite warned; ConnectionFactoryBuilder errors clear.

Verify the spec's R7 note (concurrent EnsureConnectedAsync after H22) is captured
by the H22 E2E test. Flag any test that locks in pre-fix behaviour, any unhandled
edge case the spec did not anticipate, and any production code that uses bare
`catch (Exception)` without an OCE filter where the surrounding context awaits
on a cancellable token.
```

Capture review findings; address any "Critical" or "Important" issues in a cleanup task; "Minor" issues may roll forward.

- [ ] **Step 7: Commit (only if cleanup was needed)**

If Steps 5 or 6 surfaced fixes, commit them with a clear `cleanup:` prefix. If nothing needed, skip.

```bash
git add <only-cleanup-files>
git commit -m "cleanup(phase-04): address final-review findings"
```

---

## Phase 4 done

All 9 findings closed. Move to writing-up the closing summary; the user's standard pattern is "phase complete; continue to phase N+1?".
