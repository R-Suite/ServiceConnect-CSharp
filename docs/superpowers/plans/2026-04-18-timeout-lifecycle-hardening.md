# Timeout Lifecycle Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve timeout metadata across dispatch and eliminate the duplicate-dispatch window caused by cancellation during post-send timeout cleanup.

**Architecture:** Keep the existing timeout-store interfaces stable unless the header-preservation requirement proves impossible to implement without an API extension. Tighten `ProcessManagerTimeoutService` first, because the observed duplicate-dispatch bug is caused by send/removal ordering and cancellation token usage in that service.

**Tech Stack:** C#, .NET 8/10, xUnit, Moq, Microsoft.Extensions.Hosting

---

## File Map

- Modify: `src/ServiceConnect/Services/ProcessManagerTimeoutService.cs`
  Preserve headers on send and prevent successful sends from being undone by cleanup cancellation.
- Modify: `src/ServiceConnect/Bus.cs` only if header preservation requires capturing current headers at timeout-scheduling time.
- Modify: `src/ServiceConnect.Interfaces/IBus.cs` only if header preservation cannot be satisfied internally.
- Test: `src/ServiceConnect.UnitTests/Services/ProcessManagerTimeoutServiceTests.cs`
- Test: `src/ServiceConnect.UnitTests/BusTests.cs` only if `RequestTimeoutAsync` behavior changes.
- Test: `src/ServiceConnect.EndToEndTests/ProcessManagerTimeoutTests.cs` if an end-to-end header preservation path is added.

### Task 1: Preserve Timeout Headers During Dispatch

**Files:**
- Modify: `src/ServiceConnect/Services/ProcessManagerTimeoutService.cs:82-88`
- Test: `src/ServiceConnect.UnitTests/Services/ProcessManagerTimeoutServiceTests.cs`

- [ ] **Step 1: Add a failing timeout-service test for preserved headers**

In `ProcessManagerTimeoutServiceTests`, add a test that verifies headers from `TimeoutData.Headers` are sent on the timeout message.

```csharp
[Fact]
public async Task PollOnce_IncludesStoredHeaders_WhenDispatchingTimeout()
{
    _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

    var timeoutId = Guid.NewGuid();
    var batch = new TimeoutsBatch
    {
        DueTimeouts = new List<TimeoutData>
        {
            new TimeoutData
            {
                Id = timeoutId,
                ProcessManagerId = Guid.NewGuid(),
                Destination = "test-queue",
                Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                Headers = new Dictionary<string, object> { [HeaderKeys.RetryCount] = "3" }
            }
        },
        NextQueryTime = DateTimeOffset.UtcNow.AddSeconds(30)
    };

    _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(batch);
    _mockBus.Setup(bus => bus.SendAsync(
            It.IsAny<TimeoutMessage>(),
            It.IsAny<SendOptions>(),
            It.IsAny<CancellationToken>()))
        .Returns(Task.CompletedTask);

    var sut = CreateSut(_mockFinder.Object);
    await sut.PollOnceAsync();

    _mockBus.Verify(bus => bus.SendAsync(
        It.IsAny<TimeoutMessage>(),
        It.Is<SendOptions>(options => options.Headers != null && options.Headers[HeaderKeys.RetryCount] == "3"),
        It.IsAny<CancellationToken>()), Times.Once);
}
```

- [ ] **Step 2: Run the focused header-preservation test to verify it fails**

Run: `rtk dotnet test "src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj" --filter "FullyQualifiedName~ProcessManagerTimeoutServiceTests.PollOnce_IncludesStoredHeaders_WhenDispatchingTimeout"`
Expected: FAIL because `ProcessManagerTimeoutService` currently sends a `SendOptions` with only `EndPoint`.

- [ ] **Step 3: Preserve headers when sending timeout messages**

Convert `TimeoutData.Headers` into `Dictionary<string, string>` for `SendOptions.Headers`, preserving string values and using `ToString()` for simple scalar cases.

```csharp
var outgoingHeaders = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var header in timeout.Headers)
{
    outgoingHeaders[header.Key] = header.Value switch
    {
        null => string.Empty,
        string s => s,
        byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
        _ => header.Value.ToString() ?? string.Empty
    };
}

await _bus.Value.SendAsync(timeoutMessage, new SendOptions
{
    EndPoint = timeout.Destination,
    Headers = outgoingHeaders
}).ConfigureAwait(false);
```

- [ ] **Step 4: Run the focused timeout header tests to verify they pass**

Run: `rtk dotnet test "src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj" --filter "FullyQualifiedName~ProcessManagerTimeoutServiceTests.PollOnce_IncludesStoredHeaders_WhenDispatchingTimeout|FullyQualifiedName~ProcessManagerTimeoutServiceTests.PollOnce_DueTimeouts_RemovesDispatched"`
Expected: PASS with both timeout dispatch tests green.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/ProcessManagerTimeoutService.cs src/ServiceConnect.UnitTests/Services/ProcessManagerTimeoutServiceTests.cs
git commit -m "fix: preserve stored headers on timeout dispatch"
```

### Task 2: Prevent Duplicate Dispatch When Cleanup Is Canceled After Send

**Files:**
- Modify: `src/ServiceConnect/Services/ProcessManagerTimeoutService.cs:75-105`
- Test: `src/ServiceConnect.UnitTests/Services/ProcessManagerTimeoutServiceTests.cs`

- [ ] **Step 1: Add a failing test for cancellation after a successful send**

In `ProcessManagerTimeoutServiceTests`, add a test that cancels the poll token only after `SendAsync` succeeds and verifies remove still uses a non-canceled token.

```csharp
[Fact]
public async Task PollOnce_WhenSendSucceeds_UsesNonCanceledTokenForRemove()
{
    _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

    var timeoutId = Guid.NewGuid();
    var batch = new TimeoutsBatch
    {
        DueTimeouts = new List<TimeoutData>
        {
            new TimeoutData
            {
                Id = timeoutId,
                ProcessManagerId = Guid.NewGuid(),
                Destination = "test-queue",
                Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                Headers = new Dictionary<string, object>()
            }
        },
        NextQueryTime = DateTimeOffset.UtcNow.AddSeconds(30)
    };

    using var cts = new CancellationTokenSource();
    _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(batch);
    _mockBus.Setup(bus => bus.SendAsync(It.IsAny<TimeoutMessage>(), It.IsAny<SendOptions>(), It.IsAny<CancellationToken>()))
        .Callback(() => cts.Cancel())
        .Returns(Task.CompletedTask);

    CancellationToken removeToken = default;
    _mockFinder.Setup(f => f.RemoveDispatchedTimeoutAsync(timeoutId, It.IsAny<CancellationToken>()))
        .Callback<Guid, CancellationToken>((_, token) => removeToken = token)
        .Returns(Task.CompletedTask);

    var sut = CreateSut(_mockFinder.Object);
    await sut.PollOnceAsync(cts.Token);

    Assert.False(removeToken.IsCancellationRequested);
}
```

- [ ] **Step 2: Run the focused cancellation-race test to verify it fails**

Run: `rtk dotnet test "src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj" --filter "FullyQualifiedName~ProcessManagerTimeoutServiceTests.PollOnce_WhenSendSucceeds_UsesNonCanceledTokenForRemove"`
Expected: FAIL because remove currently uses the poll cancellation token directly.

- [ ] **Step 3: Split send cancellation from post-send cleanup cancellation**

Keep the poll token for batch retrieval and dispatch send, but once send succeeds, call remove with `CancellationToken.None` so a completed delivery is finalized even if shutdown begins.

```csharp
await _bus.Value.SendAsync(timeoutMessage, sendOptions, cancellationToken).ConfigureAwait(false);

if (_leaseAwareFinder != null && timeout.LockedBy != Guid.Empty)
    await _leaseAwareFinder.RemoveDispatchedTimeoutAsync(timeout.Id, timeout.LockedBy, CancellationToken.None).ConfigureAwait(false);
else
    await _finder.RemoveDispatchedTimeoutAsync(timeout.Id, CancellationToken.None).ConfigureAwait(false);
```

Keep release-on-failure using the original cancellation token so failed sends still honor shutdown.

- [ ] **Step 4: Run the focused timeout race tests to verify they pass**

Run: `rtk dotnet test "src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj" --filter "FullyQualifiedName~ProcessManagerTimeoutServiceTests.PollOnce_WhenSendSucceeds_UsesNonCanceledTokenForRemove|FullyQualifiedName~ProcessManagerTimeoutServiceTests.PollOnce_RemoveDispatchedThrowsOCE_Propagates|FullyQualifiedName~ProcessManagerTimeoutServiceTests.PollOnce_SendFails_ReleasesTimeoutForRetry"`
Expected: PASS with all three timeout lifecycle tests green.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/ProcessManagerTimeoutService.cs src/ServiceConnect.UnitTests/Services/ProcessManagerTimeoutServiceTests.cs
git commit -m "fix: finalize successful timeout dispatches during shutdown"
```

### Task 3: Decide Whether Timeout Scheduling Must Capture Headers

**Files:**
- Inspect: `src/ServiceConnect/Bus.cs:294-311`
- Inspect: `src/ServiceConnect.Interfaces/IBus.cs:69-70`
- Optional Modify: `src/ServiceConnect/Bus.cs`
- Optional Modify: `src/ServiceConnect.Interfaces/IBus.cs`
- Optional Test: `src/ServiceConnect.UnitTests/BusTests.cs`
- Optional Test: `src/ServiceConnect.EndToEndTests/ProcessManagerTimeoutTests.cs`

- [ ] **Step 1: Confirm whether the existing scheduling path ever populates `TimeoutData.Headers`**

Read `Bus.RequestTimeoutAsync(...)` and verify that it currently creates `TimeoutData` without setting `Headers`.

Expected finding: header preservation on dispatch only helps persisted data that was already populated elsewhere.

- [ ] **Step 2: If header preservation is required end-to-end, add a failing bus test first**

Only do this if the desired behavior is to preserve the current consume context into newly scheduled timeouts.

```csharp
[Fact]
public async Task RequestTimeoutAsync_PopulatesTimeoutHeaders_WhenCurrentMessageContextIsAvailable()
{
    // Define this test only after choosing an internal mechanism for capturing current consume headers.
}
```

- [ ] **Step 3: Choose one minimal implementation path and document it in code comments**

Pick exactly one:

Option A, recommended if possible: add an internal consume-context accessor so `Bus.RequestTimeoutAsync(...)` can capture current headers without changing the public `IBus` signature.

Option B, only if A is not practical: add an overload that accepts timeout headers explicitly and keep the existing method delegating with empty headers.

- [ ] **Step 4: Implement only the chosen minimal path**

Keep the existing public API stable unless Option B is strictly necessary.

- [ ] **Step 5: Run the related unit and end-to-end tests if this task changed code**

Run: `rtk dotnet test "src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj" --filter "FullyQualifiedName~BusTests"`
Expected: PASS.

If you added end-to-end timeout propagation coverage, also run:
`rtk dotnet test "src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj" --filter "FullyQualifiedName~ProcessManagerTimeoutTests"`

- [ ] **Step 6: Commit if this task changed code**

```bash
git add src/ServiceConnect/Bus.cs src/ServiceConnect.Interfaces/IBus.cs src/ServiceConnect.UnitTests/BusTests.cs src/ServiceConnect.EndToEndTests/ProcessManagerTimeoutTests.cs
git commit -m "fix: preserve headers when scheduling process-manager timeouts"
```

Skip this commit if no code changed in this task.

### Task 4: Run The Timeout Verification Suite

**Files:**
- No code changes expected

- [ ] **Step 1: Run the timeout-focused unit tests**

Run: `rtk dotnet test "src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj" --filter "FullyQualifiedName~ProcessManagerTimeoutServiceTests|FullyQualifiedName~InMemoryTimeoutStoreTests|FullyQualifiedName~MongoDbTimeoutStoreTests"`
Expected: PASS with all timeout-focused unit tests green.

- [ ] **Step 2: Run timeout end-to-end coverage if the environment supports it**

Run: `rtk dotnet test "src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj" --filter "FullyQualifiedName~ProcessManagerTimeoutTests"`
Expected: PASS in an environment with RabbitMQ/Docker support.

- [ ] **Step 3: Commit verification-only follow-up if needed**

If no files changed, do not create an empty commit.
