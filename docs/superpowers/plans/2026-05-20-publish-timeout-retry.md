# Publish-Timeout Retry Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Align the framework's RabbitMQ producer retry behaviour with its documented at-least-once contract by retrying `TimeoutException`, capped by a new wall-clock budget (`MaxPublishWaitTime`, default 120 s).

**Architecture:** Four discrete changes to `src/ServiceConnect.Client.RabbitMQ/`: (1) add the `MaxPublishWaitTime` configuration surface (setting key + option + transport-extension wiring); (2) read the new setting in the `Producer` constructor and check it at the top of every retry iteration; (3) remove `TimeoutException` from `IsRetriablePublishException` + drop the dedicated `catch (TimeoutException)` arm so the timeout flows into the existing retriable arm; (4) update existing publish-timeout tests (which assert immediate propagation) and add six new ones covering the retry path, the wall-clock cap, and the `MessageId`-preservation invariant.

**Tech Stack:** C# 14, .NET 10, xUnit + Moq for the existing rabbit unit tests, RabbitMQ.Client `IChannel.BasicPublishAsync`.

---

## File map

**Modified (production):**

- `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQSettingKeys.cs` — add `MaxPublishWaitTime` constant.
- `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMqOptions.cs` — add `MaxPublishWaitTime` property + validation.
- `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQExtensions.cs` — wire the option into `transport.SetClientSetting`.
- `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs` — read setting in ctor; remove `TimeoutException` from `IsRetriablePublishException`; remove dedicated catch arm; add wall-clock check in retry loop; update xmldoc on `PublishWithTimeoutAsync`.
- `src/ServiceConnect.Interfaces/Bus/IBus.cs` — add xmldoc paragraph noting publish-confirm timeouts are now retried.

**Modified (tests):**

- `src/ServiceConnect.UnitTests/RabbitMQ/ProducerPublishTimeoutTests.cs` — update the four `_ThrowsTimeoutException_WhenBasicPublishHangs` tests to account for the new retry behaviour; they now use a sequenced channel that fails first then succeeds, or use a tight `MaxPublishWaitTime` to bound the total wall-clock.

**New (tests):**

- `src/ServiceConnect.UnitTests/RabbitMQ/ProducerPublishTimeoutRetryTests.cs` — six new tests covering: timeout retried + succeeds on second attempt, `MessageId` preserved across retries, `MaxPublishWaitTime` cap, `Timeout.InfiniteTimeSpan` disables the cap, `PublishException` still propagates without retry, `MaxPublishWaitTime` validation rejects zero / negative.

---

## Task 1: Add `MaxPublishWaitTime` setting key

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQSettingKeys.cs`

- [ ] **Step 1: Add the constant**

Append to the `RabbitMQSettingKeys` class (after the existing `MaxHeaderValueBytes` entry):

```csharp
    /// <summary>
    /// Wall-clock cap on the publisher's retry loop in <c>Producer.ExecuteRetryingPublishAsync</c>.
    /// Accepts a <see cref="System.TimeSpan"/>; defaults to 120 seconds when unset.
    /// Set to <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to disable the cap
    /// and rely solely on <see cref="RetryCount"/> × <see cref="RetrySeconds"/>.
    /// Distinct from <see cref="PublishTimeout"/>, which bounds a single confirm-ack
    /// wait inside one attempt; this cap bounds the total retry budget across attempts.
    /// </summary>
    public const string MaxPublishWaitTime = nameof(MaxPublishWaitTime);
```

- [ ] **Step 2: Build the RabbitMQ client project**

Run: `dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1 -nologo`
Expected: 0 errors, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQSettingKeys.cs
git commit -m "feat(rabbitmq): add MaxPublishWaitTime setting key"
```

---

## Task 2: Add `MaxPublishWaitTime` to `RabbitMqOptions`

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMqOptions.cs`

- [ ] **Step 1: Add the property**

After the `PublishTimeout` property (line 67), add:

```csharp
    /// <summary>
    /// Wall-clock cap on the publisher's retry loop. Default: 120s. Set to
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to disable the cap.
    /// Distinct from <see cref="PublishTimeout"/>: that bounds a single confirm-ack
    /// wait, this bounds the total wall-clock across retry attempts.
    /// </summary>
    public TimeSpan? MaxPublishWaitTime { get; set; }
```

- [ ] **Step 2: Add the validation rule**

After the existing `PublishTimeout` validator (line 123-126), add:

```csharp
        if (MaxPublishWaitTime is { } maxPublishWaitTime
            && maxPublishWaitTime <= TimeSpan.Zero
            && maxPublishWaitTime != Timeout.InfiniteTimeSpan)
        {
            errors.Add($"MaxPublishWaitTime must be positive or Timeout.InfiniteTimeSpan (was {maxPublishWaitTime}).");
        }
```

The `Timeout.InfiniteTimeSpan` exemption is important — that's `-1 ms`, semantically distinct from "negative wall-clock" and used to disable the cap.

- [ ] **Step 3: Build**

Run: `dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1 -nologo`
Expected: 0 errors, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMqOptions.cs
git commit -m "feat(rabbitmq): expose MaxPublishWaitTime on RabbitMqOptions"
```

---

## Task 3: Wire the option through `RabbitMQExtensions`

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQExtensions.cs`

- [ ] **Step 1: Find the existing `PublishTimeout` wire**

Run: `grep -n "PublishTimeout" src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQExtensions.cs`
Expected: line 98 of the file applies `options.PublishTimeout` to `transport.SetClientSetting(RabbitMQSettingKeys.PublishTimeout, ...)`.

- [ ] **Step 2: Add the parallel `MaxPublishWaitTime` wire**

Immediately after the `PublishTimeout` line (around line 98), append:

```csharp
        if (options.MaxPublishWaitTime.HasValue) { transport.SetClientSetting(RabbitMQSettingKeys.MaxPublishWaitTime, options.MaxPublishWaitTime.Value); }
```

Matches the format and style of the surrounding lines (`if (options.X.HasValue) { transport.SetClientSetting(...); }`).

- [ ] **Step 3: Build**

Run: `dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1 -nologo`
Expected: 0 errors, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQExtensions.cs
git commit -m "feat(rabbitmq): wire MaxPublishWaitTime through RabbitMQExtensions"
```

---

## Task 4: Read and apply `MaxPublishWaitTime` in `Producer`

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs`

- [ ] **Step 1: Add the backing field**

After the existing `_retryTimeInSeconds` field (around line 30), add:

```csharp
    private readonly TimeSpan _maxPublishWaitTime;
```

- [ ] **Step 2: Read the setting in the constructor**

After the existing `_retryTimeInSeconds = GetSetting(...)` line (around line 83), add:

```csharp
        _maxPublishWaitTime = GetSetting(settings, RabbitMQSettingKeys.MaxPublishWaitTime, TimeSpan.FromSeconds(120), v => (TimeSpan)v);
        // Reject the same invalid range as PublishTimeout — a value of zero or other negative
        // would let the wall-clock cap fire immediately on every publish, burning the entire
        // retry budget against an unreachable condition.
        if (_maxPublishWaitTime <= TimeSpan.Zero && _maxPublishWaitTime != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transportConfiguration),
                $"Setting '{RabbitMQSettingKeys.MaxPublishWaitTime}' must be positive or Timeout.InfiniteTimeSpan; got {_maxPublishWaitTime}.");
        }
```

- [ ] **Step 3: Add the wall-clock check at the top of `ExecuteRetryingPublishAsync`**

Locate `ExecuteRetryingPublishAsync` (around line 134). Replace its body's opening:

```csharp
    private async Task ExecuteRetryingPublishAsync(
        Func<CancellationToken, Task> lockedAction,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (int attempt = 0; attempt <= _retryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
```

With:

```csharp
    private async Task ExecuteRetryingPublishAsync(
        Func<CancellationToken, Task> lockedAction,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        var publishStartedAt = Stopwatch.GetTimestamp();
        for (int attempt = 0; attempt <= _retryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_maxPublishWaitTime != Timeout.InfiniteTimeSpan
                && Stopwatch.GetElapsedTime(publishStartedAt) >= _maxPublishWaitTime)
            {
                throw new TimeoutException(
                    $"Publish wall-clock budget {_maxPublishWaitTime.TotalSeconds:0.###}s exhausted " +
                    $"after {attempt} attempt(s); last error: {lastException?.Message ?? "<none>"}.",
                    lastException);
            }
```

The `Timeout.InfiniteTimeSpan` shortcut avoids calling `GetElapsedTime` when the cap is disabled.

- [ ] **Step 4: Build**

Run: `dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1 -nologo`
Expected: 0 errors, 0 warnings.

- [ ] **Step 5: Run the existing producer unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~RabbitMQ.Producer" -nologo`
Expected: Existing tests still pass (no behavioural change yet — `TimeoutException` still propagates because it remains in `IsRetriablePublishException`'s exclusion list).

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs
git commit -m "feat(rabbitmq): read MaxPublishWaitTime and cap retry loop wall-clock"
```

---

## Task 5: Treat `TimeoutException` as retriable

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs`

This is the load-bearing change. The Task-4 wall-clock cap exists to bound the retry budget that this change is about to enable.

- [ ] **Step 1: Remove `TimeoutException` from `IsRetriablePublishException`**

Locate `IsRetriablePublishException` (around line 270-288). Replace its body with:

```csharp
    // Broker-side nacks (PublishException) are usually poison messages — rejected by a
    // policy (e.g. max-length, unroutable, access denied). Retrying them burns the entire
    // retry budget against a condition that will not heal, and worse, triggers a reconnect
    // loop that tears down the connection for a publish-layer error. Only transport-level
    // failures should flow into the reconnect-retry path.
    //
    // OperationCanceledException is caller-driven cancellation — retrying it would violate
    // the caller's intent.
    //
    // TimeoutException (from PublishWithTimeoutAsync's _publishTimeout firing) IS retriable:
    // the publish-confirm ack timer fired before the broker acked, and a fresh channel
    // built by the next attempt's EnsureConnectedAsync prologue can resubmit. The framework's
    // at-least-once delivery contract (IBus.cs:9-27) permits duplicate delivery; consumers
    // must be idempotent or use a BeforeConsuming + OnConsumedSuccessfully dedup filter
    // pair. The same BasicProperties (including MessageId) is reused across attempts, so
    // dedup by MessageId is valid.
    private static bool IsRetriablePublishException(Exception ex)
    {
        if (ex is global::RabbitMQ.Client.Exceptions.PublishException)
        {
            return false;
        }

        if (ex is OperationCanceledException)
        {
            return false;
        }

        return true;
    }
```

- [ ] **Step 2: Remove the dedicated `catch (TimeoutException)` arm**

Locate `ExecuteRetryingPublishAsync`. Delete the entire block:

```csharp
            catch (TimeoutException)
            {
                // PublishWithTimeoutAsync already flagged MarkResetRequired before throwing —
                // do not double-flag and do not retry. The next publish's EnsureConnectedAsync
                // consumes the flag and rebuilds the channel.
                throw;
            }
```

`TimeoutException` now falls through to the existing `catch (Exception ex) when (IsRetriablePublishException(ex))` arm at line ~219. The double `MarkResetRequired()` call (once in `PublishWithTimeoutAsync` line 776, once in the retriable arm at line 222) is safe — that method sets a boolean flag and is idempotent.

- [ ] **Step 3: Build**

Run: `dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1 -nologo`
Expected: 0 errors, 0 warnings.

- [ ] **Step 4: Run the existing producer unit tests — many should now fail**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~RabbitMQ.Producer" -nologo`
Expected: The four `ProducerPublishTimeoutTests` tests that assert "`_ThrowsTimeoutException_WhenBasicPublishHangs`" should now fail (they previously asserted single-attempt propagation; now the timeout retries). The next task updates them.

Do **NOT** commit yet — Task 6 updates the tests so the suite goes green again before commit.

---

## Task 6: Update existing publish-timeout tests for the new retry contract

**Files:**
- Modify: `src/ServiceConnect.UnitTests/RabbitMQ/ProducerPublishTimeoutTests.cs`

The four "hangs" tests rely on the timeout being terminal. Update them to assert the new contract: the timeout retries against the still-hanging channel until either the `RetryCount` exhausts or the wall-clock cap fires.

- [ ] **Step 1: Update `CreateProducer` to accept a `MaxPublishWaitTime` parameter**

Replace the existing helper at line 17-47 with:

```csharp
    private static Producer CreateProducer(
        TimeSpan? publishTimeout = null,
        TimeSpan? maxPublishWaitTime = null,
        ushort retryCount = 1)
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");

        var settings = new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.RetryCount] = retryCount,
            [RabbitMQSettingKeys.RetrySeconds] = (ushort)0,
        };
        if (publishTimeout.HasValue)
        {
            settings[RabbitMQSettingKeys.PublishTimeout] = publishTimeout.Value;
        }
        if (maxPublishWaitTime.HasValue)
        {
            settings[RabbitMQSettingKeys.MaxPublishWaitTime] = maxPublishWaitTime.Value;
        }

        transport.SetupGet(t => t.ClientSettings).Returns(settings);

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");
        queue.Setup(q => q.TryGetQueueMapping(It.IsAny<Type>(), out It.Ref<IReadOnlyList<string>?>.IsAny))
            .Returns((Type _, out IReadOnlyList<string>? endpoints) =>
            {
                endpoints = ["q"];
                return true;
            });

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        return new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance);
    }
```

- [ ] **Step 2: Update `PublishAsync_ThrowsTimeoutException_WhenBasicPublishHangs`**

Replace its body with the wall-clock-budget version:

```csharp
    [Fact]
    public async Task PublishAsync_ThrowsTimeoutException_WhenBasicPublishHangsForLongerThanBudget()
    {
        // After the retry contract change, TimeoutException is retriable. The wall-clock cap
        // is now the terminal signal. Configure a tight cap so retries against the still-hanging
        // channel exhaust the budget quickly.
        var producer = CreateProducer(
            publishTimeout: TimeSpan.FromMilliseconds(100),
            maxPublishWaitTime: TimeSpan.FromMilliseconds(300),
            retryCount: 10);
        var channel = MakeHangingChannel();
        var declaredExchanges = GetField<ConcurrentDictionary<string, long>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = 0L;

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }));
        Assert.Contains("wall-clock budget", ex.Message, StringComparison.Ordinal);
    }
```

- [ ] **Step 3: Update `SendAsync_ByType_ThrowsAggregateException_ContainingTimeoutException_WhenBasicPublishHangs`**

```csharp
    [Fact]
    public async Task SendAsync_ByType_ThrowsAggregateException_ContainingTimeoutException_WhenBasicPublishHangsForLongerThanBudget()
    {
        var producer = CreateProducer(
            publishTimeout: TimeSpan.FromMilliseconds(100),
            maxPublishWaitTime: TimeSpan.FromMilliseconds(300),
            retryCount: 10);
        var channel = MakeHangingChannel();

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        var ex = await Assert.ThrowsAsync<AggregateException>(() =>
            producer.SendAsync(typeof(object), new byte[] { 1, 2, 3 }));

        Assert.Single(ex.InnerExceptions);
        var timeout = Assert.IsType<TimeoutException>(ex.InnerExceptions[0]);
        Assert.Contains("wall-clock budget", timeout.Message, StringComparison.Ordinal);
    }
```

- [ ] **Step 4: Update `SendAsync_ByEndpoint_ThrowsTimeoutException_WhenBasicPublishHangs`**

```csharp
    [Fact]
    public async Task SendAsync_ByEndpoint_ThrowsTimeoutException_WhenBasicPublishHangsForLongerThanBudget()
    {
        var producer = CreateProducer(
            publishTimeout: TimeSpan.FromMilliseconds(100),
            maxPublishWaitTime: TimeSpan.FromMilliseconds(300),
            retryCount: 10);
        var channel = MakeHangingChannel();

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.SendAsync("destination-queue", typeof(object), new byte[] { 1, 2, 3 }));
        Assert.Contains("wall-clock budget", ex.Message, StringComparison.Ordinal);
    }
```

- [ ] **Step 5: Update `SendBytesAsync_ThrowsTimeoutException_WhenBasicPublishHangs`**

```csharp
    [Fact]
    public async Task SendBytesAsync_ThrowsTimeoutException_WhenBasicPublishHangsForLongerThanBudget()
    {
        var producer = CreateProducer(
            publishTimeout: TimeSpan.FromMilliseconds(100),
            maxPublishWaitTime: TimeSpan.FromMilliseconds(300),
            retryCount: 10);
        var channel = MakeHangingChannel();

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.SendBytesAsync("destination-queue", typeof(object), new byte[] { 1, 2, 3 }));
        Assert.Contains("wall-clock budget", ex.Message, StringComparison.Ordinal);
    }
```

- [ ] **Step 6: Verify the `PublishAsync_MarksResetRequired_WhenBasicPublishTimesOut_WithoutReconnectingUnderLock` test (around line 207)**

Read the test (it's at the end of the file). If it asserts `TimeoutException` is thrown after exactly one attempt, the existing assertion still holds when the test uses `retryCount: 0` (no retry attempts). Verify the test setup uses `retryCount: 0` or update it to use the new helper signature with `retryCount: 0` so that `MarkResetRequired` is still observed once on the single attempt's timeout.

If the test passes `RetryCount=1` (the previous default in the helper), update it to pass `retryCount: 0`:

```csharp
        var producer = CreateProducer(
            publishTimeout: TimeSpan.FromMilliseconds(100),
            retryCount: 0);
```

This keeps the test's intent (assert reset-required happens on timeout) without bringing the retry path into scope.

- [ ] **Step 7: Run the producer test filter**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~RabbitMQ.Producer" -nologo`
Expected: All producer tests pass.

- [ ] **Step 8: Run the full unit test suite**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 -nologo`
Expected: All tests pass (we may have to fix any other test that asserts "TimeoutException not retried" — search for that pattern: `grep -rn "TimeoutException" src/ServiceConnect.UnitTests --include="*.cs" | head -20`).

- [ ] **Step 9: Commit Tasks 5 + 6 together**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/ProducerPublishTimeoutTests.cs
git commit -m "feat(rabbitmq): treat TimeoutException as retriable under at-least-once contract"
```

---

## Task 7: New unit tests for the retry semantics

**Files:**
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/ProducerPublishTimeoutRetryTests.cs`

- [ ] **Step 1: Inspect the existing `ProducerInternals` helper**

Run: `grep -rn "class ProducerInternals" src/ServiceConnect.UnitTests --include="*.cs"` to find the reflection helper. It exposes `SetField` and `GetField` for white-box assertions. The new test file uses the same pattern.

- [ ] **Step 2: Create the test file**

Create `src/ServiceConnect.UnitTests/RabbitMQ/ProducerPublishTimeoutRetryTests.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Pins the producer's retry contract under the at-least-once delivery model: a publish-confirm
/// TimeoutException retries on a fresh channel, the MessageId is preserved across attempts, and
/// MaxPublishWaitTime caps the total wall-clock budget so a permanently-dead broker cannot hold
/// a publisher indefinitely.
/// </summary>
public class ProducerPublishTimeoutRetryTests
{
    private static Producer CreateProducer(
        TimeSpan? publishTimeout = null,
        TimeSpan? maxPublishWaitTime = null,
        ushort retryCount = 2)
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");

        var settings = new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.RetryCount] = retryCount,
            [RabbitMQSettingKeys.RetrySeconds] = (ushort)0,
        };
        if (publishTimeout.HasValue) { settings[RabbitMQSettingKeys.PublishTimeout] = publishTimeout.Value; }
        if (maxPublishWaitTime.HasValue) { settings[RabbitMQSettingKeys.MaxPublishWaitTime] = maxPublishWaitTime.Value; }

        transport.SetupGet(t => t.ClientSettings).Returns(settings);

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");
        queue.Setup(q => q.TryGetQueueMapping(It.IsAny<Type>(), out It.Ref<IReadOnlyList<string>?>.IsAny))
            .Returns((Type _, out IReadOnlyList<string>? endpoints) =>
            {
                endpoints = ["q"];
                return true;
            });

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        var producer = new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance)
        {
            RetryDelayForTests = (_, _) => Task.CompletedTask,
        };
        return producer;
    }

    private static void SetField<T>(Producer producer, string fieldName, T value) =>
        ProducerInternals.SetField(producer, fieldName, value);

    private static T GetField<T>(Producer producer, string fieldName) =>
        ProducerInternals.GetField<T>(producer, fieldName);

    [Fact]
    public async Task PublishAsync_RetriesTimeoutException_SucceedsOnSecondAttempt()
    {
        var producer = CreateProducer(
            publishTimeout: TimeSpan.FromMilliseconds(100),
            maxPublishWaitTime: TimeSpan.FromSeconds(5));
        var declaredExchanges = GetField<ConcurrentDictionary<string, long>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = 0L;

        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);

        var callCount = 0;
        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, string _, bool _, BasicProperties _, ReadOnlyMemory<byte> _, CancellationToken ct) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // First call: simulate the broker's confirm-ack never arriving so the
                    // publish-timeout CTS fires inside PublishWithTimeoutAsync.
                    return new ValueTask(Task.Delay(Timeout.Infinite, ct));
                }
                return ValueTask.CompletedTask;
            });

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        await producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 });

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task PublishAsync_RetriesTimeoutException_PreservesMessageIdAcrossAttempts()
    {
        var producer = CreateProducer(
            publishTimeout: TimeSpan.FromMilliseconds(100),
            maxPublishWaitTime: TimeSpan.FromSeconds(5));
        var declaredExchanges = GetField<ConcurrentDictionary<string, long>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = 0L;

        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);

        var capturedMessageIds = new List<string?>();
        var callCount = 0;
        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, string _, bool _, BasicProperties props, ReadOnlyMemory<byte> _, CancellationToken ct) =>
            {
                capturedMessageIds.Add(props.MessageId);
                callCount++;
                if (callCount == 1)
                {
                    return new ValueTask(Task.Delay(Timeout.Infinite, ct));
                }
                return ValueTask.CompletedTask;
            });

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        await producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 });

        Assert.Equal(2, capturedMessageIds.Count);
        Assert.NotNull(capturedMessageIds[0]);
        Assert.Equal(capturedMessageIds[0], capturedMessageIds[1]);
    }

    [Fact]
    public async Task PublishAsync_MaxPublishWaitTime_CapsRetryLoopWallClock()
    {
        // Configure a tight wall-clock cap so retries against a permanently-hanging channel
        // exhaust the budget quickly. Assert the wrapping TimeoutException mentions "wall-clock
        // budget" and that the test completed well below RetryCount × PublishTimeout.
        var producer = CreateProducer(
            publishTimeout: TimeSpan.FromMilliseconds(100),
            maxPublishWaitTime: TimeSpan.FromMilliseconds(200),
            retryCount: 60);
        var declaredExchanges = GetField<ConcurrentDictionary<string, long>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = 0L;

        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, string _, bool _, BasicProperties _, ReadOnlyMemory<byte> _, CancellationToken ct) =>
                new ValueTask(Task.Delay(Timeout.Infinite, ct)));

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }));
        sw.Stop();

        Assert.Contains("wall-clock budget", ex.Message, StringComparison.Ordinal);
        // RetryCount=60, PublishTimeout=100ms — without the cap the test would burn 6+ seconds.
        // The cap is 200ms; allow generous overhead (one more attempt's worth) before failing.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1),
            $"Test took {sw.Elapsed.TotalSeconds:F2}s but the cap was 200ms.");
    }

    [Fact]
    public async Task PublishAsync_MaxPublishWaitTime_InfiniteTimeSpan_DisablesCap()
    {
        // With Timeout.InfiniteTimeSpan, the cap is disabled and RetryCount × PublishTimeout
        // bounds the wall-clock instead. Configure a small RetryCount + short PublishTimeout
        // so the test still completes fast, then assert the final exception is the inner
        // retriable TimeoutException (not the cap's wrap message).
        var producer = CreateProducer(
            publishTimeout: TimeSpan.FromMilliseconds(50),
            maxPublishWaitTime: Timeout.InfiniteTimeSpan,
            retryCount: 2);
        var declaredExchanges = GetField<ConcurrentDictionary<string, long>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = 0L;

        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, string _, bool _, BasicProperties _, ReadOnlyMemory<byte> _, CancellationToken ct) =>
                new ValueTask(Task.Delay(Timeout.Infinite, ct)));

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }));
        Assert.DoesNotContain("wall-clock budget", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishAsync_PublishException_StillPropagatesWithoutRetry()
    {
        // Regression: broker nacks (poison messages) must NOT trigger the retry path.
        // The PublishException is poison, not transport failure.
        var producer = CreateProducer(
            publishTimeout: TimeSpan.FromMilliseconds(100),
            maxPublishWaitTime: TimeSpan.FromSeconds(5),
            retryCount: 10);
        var declaredExchanges = GetField<ConcurrentDictionary<string, long>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = 0L;

        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);

        var callCount = 0;
        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, string _, bool _, BasicProperties _, ReadOnlyMemory<byte> _, CancellationToken _) =>
            {
                callCount++;
                throw new PublishException(deliveryTag: 1, isReturn: true, reason: "broker nack");
            });

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        await Assert.ThrowsAsync<PublishException>(() =>
            producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }));

        Assert.Equal(1, callCount);
    }

    [Fact]
    public void RabbitMqOptions_Validate_RejectsZeroMaxPublishWaitTime()
    {
        var options = new ServiceConnect.Client.RabbitMQ.RabbitMqOptions
        {
            MaxPublishWaitTime = TimeSpan.Zero,
        };

        var errors = options.Validate();

        Assert.Contains(errors, e => e.Contains("MaxPublishWaitTime", StringComparison.Ordinal));
    }
}
```

- [ ] **Step 3: Verify the `PublishException` constructor signature**

Run: `grep -n "public PublishException" /root/.nuget/packages/rabbitmq.client/*/lib/*/RabbitMQ.Client.xml 2>/dev/null | head -5` (or search the binary via dotnet decompile).

If the signature differs from `(ulong deliveryTag, bool isReturn, string reason)`, adapt the test. Most likely the signature uses positional parameters — the exact form may be `PublishException(deliveryTag: 1, isReturn: true)` or `PublishException(deliveryTag: 1, reason: "...")`. Check the Producer.cs existing reference at line 172 (`catch (global::RabbitMQ.Client.Exceptions.PublishException pex)`) — pex.Message is read — and construct the test exception so `.Message` carries the test's reason string. If the simplest constructor is `new PublishException(1, true)` and `.Message` defaults to something, use that and drop the message-content assertion.

- [ ] **Step 4: Run the new tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 --filter "FullyQualifiedName~ProducerPublishTimeoutRetryTests" -nologo`
Expected: 6 tests pass.

- [ ] **Step 5: Run the full unit test suite**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1 -nologo`
Expected: All tests pass, including the four updated ones from Task 6.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/ProducerPublishTimeoutRetryTests.cs
git commit -m "test(rabbitmq): cover retry-on-TimeoutException, MessageId preservation, and wall-clock cap"
```

---

## Task 8: Update xmldoc on `PublishWithTimeoutAsync` + `IBus`

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs` (xmldoc on `PublishWithTimeoutAsync` only)
- Modify: `src/ServiceConnect.Interfaces/Bus/IBus.cs` (xmldoc on `SendAsync` + `PublishAsync`)

- [ ] **Step 1: Replace the xmldoc on `PublishWithTimeoutAsync`**

Locate `PublishWithTimeoutAsync` (around line 744). Replace its `<summary>` + `<remarks>` block with:

```csharp
    /// <summary>
    /// Publishes via <see cref="IChannel.BasicPublishAsync"/> under a linked
    /// <see cref="CancellationTokenSource"/> that fires after <c>_publishTimeout</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If the caller's <paramref name="cancellationToken"/> fires, an
    /// <see cref="OperationCanceledException"/> propagates unchanged.
    /// </para>
    /// <para>
    /// If the broker ack does not arrive within <c>_publishTimeout</c>, the linked CTS fires
    /// and the method throws <see cref="TimeoutException"/>. It also calls
    /// <see cref="ProducerConnection.MarkResetRequired"/> so the next attempt's
    /// <see cref="EnsureConnectedAsync"/> prologue rebuilds the channel before re-publishing.
    /// The exception flows into <see cref="ExecuteRetryingPublishAsync"/>'s retriable arm:
    /// a fresh channel is built and the same <c>BasicProperties</c> (including
    /// <c>MessageId</c>) is re-published. The framework's at-least-once contract
    /// (<see cref="ServiceConnect.Interfaces.IBus"/>) permits the broker to deliver both
    /// the original and the retry — consumers must be idempotent or use a
    /// <c>BeforeConsuming</c> + <c>OnConsumedSuccessfully</c> dedup filter pair to
    /// short-circuit duplicates.
    /// </para>
    /// </remarks>
```

- [ ] **Step 2: Add a retry note to `IBus.SendAsync` and `IBus.PublishAsync` xmldoc**

In `src/ServiceConnect.Interfaces/Bus/IBus.cs`, locate the `PublishAsync` summary (around line 33-37) and `SendAsync` summary (around line 39-54). Add a new `<remarks>` paragraph to each (or extend the existing `<remarks>` if present) noting:

```csharp
    /// <para>
    /// Publish-confirm timeouts (the broker's ack does not arrive within the configured
    /// publish timeout) are retried by the framework's RabbitMQ producer under the
    /// at-least-once contract. The same <c>MessageId</c> is reused across attempts, so a
    /// consumer-side deduplication filter pair (<c>BeforeConsuming</c> +
    /// <c>OnConsumedSuccessfully</c>) can short-circuit duplicates by message id.
    /// </para>
```

Insert at the end of each method's `<remarks>` block (or create a `<remarks>` block if the method only has a `<summary>`).

- [ ] **Step 3: Build the framework**

Run: `dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1 -nologo`
Run: `dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj -m:1 -nologo`
Expected: 0 errors, 0 warnings each.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs \
        src/ServiceConnect.Interfaces/Bus/IBus.cs
git commit -m "docs(rabbitmq): document TimeoutException retry contract on PublishWithTimeoutAsync + IBus"
```

---

## Task 9: Run the 5-minute chaos soak and verify

**Files:** (no source changes; verification step)

- [ ] **Step 1: Single-CPU build of the stress harness (avoids the cgroup-fenced parallel-build OOM)**

Run: `dotnet build examples/StressHarness/src/ServiceConnect.Examples.StressHarness/ServiceConnect.Examples.StressHarness.csproj -m:1 -nologo`
Expected: 0 errors, 0 warnings.

- [ ] **Step 2: Bring up the broker**

Run (in foreground):
```bash
cd examples/StressHarness && docker compose -p stress-harness up -d
```
Then wait for RabbitMQ to be ready:
```bash
until nc -z localhost 5672 2>/dev/null; do sleep 2; done && echo "RabbitMQ ready"
```

- [ ] **Step 3: Run the 5-minute chaos soak**

Run from the repo root:
```bash
dotnet run --no-build --project examples/StressHarness/src/ServiceConnect.Examples.StressHarness/ServiceConnect.Examples.StressHarness.csproj -- --mode soak --duration 00:05:00 --rate 100 --persistence inmemory --chaos docker --chaos-interval 00:00:50 --chaos-downtime 00:00:20 --chaos-compose-file examples/StressHarness/docker-compose.yml
```

Expected: ~5 minutes wall-clock; summary lists "Flows: N passed, K failed of N+K" with K ≈ 0 (down from 19 in the pre-change run).

- [ ] **Step 4: Read the ledger section in `out/report.md`**

Run from the repo root: `awk '/## Message ledger/,0' out/report.md`
Expected output:
- `Failed-and-lost: 0` (or near-zero — the wall-clock cap could fire if a kill aligns with the budget, but should be 0 for the standard 50s-interval / 20s-downtime cycle).
- `Per-message redeliveries:` count modestly higher than the baseline (some publishes whose original eventually arrived AND a retry delivered → duplicate).
- `Acked-but-lost: 0` (or near-zero, dominated by `(unknown)` pattern noise from framework-internal `TimeoutMessage` sends — those are not real losses).

- [ ] **Step 5: Tear down the broker**

Run: `docker compose -p stress-harness down --remove-orphans`

- [ ] **Step 6: Capture verdict (no commit)**

Summarise the diagnostic outcome in conversation: did `Failed-and-lost` drop to 0? Did `Per-message redeliveries` rise? If the ledger reports a non-zero `Failed-and-lost`, inspect the per-pattern breakdown — the residue is either (a) the wall-clock cap firing (in which case raise it via `RabbitMqOptions.MaxPublishWaitTime`) or (b) a different exception class slipping past `IsRetriablePublishException` (in which case widen the retriable set or wrap that class in a retriable shim).

---

## Self-review

**1. Spec coverage:**

- "Treat `TimeoutException` as retriable" — Task 5 ✓
- "Wall-clock cap on retry loop (default 120s)" — Tasks 1+2+3+4 (setting key, option, wire, ctor + check) ✓
- "Preserve `MessageId` across retries" — Task 7 step 2 (test pins the invariant; the existing `BasicProperties` construction is already outside the retry loop, no production change needed) ✓
- "xmldoc updates on `PublishWithTimeoutAsync` + `IBus`" — Task 8 ✓
- "Six new unit tests" — Task 7 ✓
- "Update existing tests asserting non-retried `TimeoutException`" — Task 6 ✓
- "5-min chaos soak verifies `Failed-and-lost ≈ 0`" — Task 9 ✓
- "`MaxPublishWaitTime` validation rejects ≤ Zero (except `Timeout.InfiniteTimeSpan`)" — Task 2 step 2 + Task 4 step 2 (ctor guard) + Task 7 test 6 (validator regression) ✓

**2. Placeholder scan:** No "TBD" or "TODO" entries. Step 3 of Task 7 says "verify the PublishException constructor signature" — that's a concrete check with a fallback (use the simplest ctor + drop the message-content assertion), not a placeholder.

**3. Type consistency:**
- `_maxPublishWaitTime` field, `RabbitMqOptions.MaxPublishWaitTime` property, `RabbitMQSettingKeys.MaxPublishWaitTime` constant — all consistent.
- `IsRetriablePublishException` keeps its signature `(Exception ex) → bool`. ✓
- `Stopwatch.GetTimestamp()` / `Stopwatch.GetElapsedTime(long)` are the BCL .NET 7+ APIs; the project targets net10. ✓
- `Timeout.InfiniteTimeSpan` is `System.Threading.Timeout.InfiniteTimeSpan` (`-1ms`); used consistently as the sentinel for "disabled". ✓
- The new test file uses the same `ProducerInternals.SetField/GetField` helpers and `Producer.RetryDelayForTests` hook as the existing tests. ✓
