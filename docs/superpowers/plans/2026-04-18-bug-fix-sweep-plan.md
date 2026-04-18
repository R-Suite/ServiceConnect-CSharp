# Bug Fix Sweep Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply the seven correctness fixes identified in `docs/superpowers/specs/2026-04-18-bug-fix-sweep-design.md`, each with TDD coverage and a single focused commit, on branch `improvements-and-fixes`.

**Architecture:** Each fix is independent and lives in a single source file plus its existing test file. Six fixes have unit-test coverage; two are documentation/comment-only (R-016 and R-030).

**Tech Stack:** .NET 10, xUnit 2.9.2, Moq 4.20.72, `Microsoft.Extensions.Time.Testing` (`FakeTimeProvider`), MongoDB.Driver, GitNexus MCP for impact checks.

---

## Pre-flight (do once before any task)

- [ ] **Confirm working directory and branch**

Run:
```bash
pwd
# Expect: /home/tim/source/ServiceConnect-CSharp
git rev-parse --abbrev-ref HEAD
# Expect: improvements-and-fixes
git status --short
# Expect (pre-existing): D docs/code-review-report.md, D docs/performance-review-report.md, ?? docs/code-review-findings.md
```

- [ ] **Confirm GitNexus index is fresh**

Use the `gitnexus_context` MCP tool against `gitnexus://repo/ServiceConnect-CSharp/context`. If it warns "stale", run:
```bash
npx gitnexus analyze --embeddings
```
(`--embeddings` preserves any existing embeddings — confirm via `.gitnexus/meta.json` `stats.embeddings` field first.)

- [ ] **Confirm baseline tests are green**

Run:
```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj
```
Expected: all tests pass. If any fail before any change, STOP and surface the failure.

---

## File Structure

| Fix | Production file modified | Test file modified/created |
|-----|--------------------------|----------------------------|
| R-001 | `src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs` | `src/ServiceConnect.UnitTests/InMemoryTimeoutStoreTests.cs` (modify) |
| R-005 | `src/ServiceConnect/Services/ProcessManagerTimeoutService.cs` | `src/ServiceConnect.UnitTests/Services/ProcessManagerTimeoutServiceTests.cs` (modify) |
| R-002 | `src/ServiceConnect/Services/Processors/StreamProcessor.cs` | `src/ServiceConnect.UnitTests/Processors/StreamProcessorTests.cs` (modify) |
| R-022 | `src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs` | `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreTests.cs` (modify) |
| R-026 | `src/ServiceConnect.Interfaces/HeaderDecoder.cs` | `src/ServiceConnect.UnitTests/HeaderDecoderTests.cs` (create) |
| R-016 | `src/ServiceConnect/Bus.cs` | (none — comment only) |
| R-030 | `src/ServiceConnect.Interfaces/Options/RequestOptions.cs` | (none — doc only) |

---

## Task 1: R-001 — Implement `InMemoryTimeoutStore.ReleaseDispatchedTimeoutAsync`

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs:101-105`
- Modify: `src/ServiceConnect.UnitTests/InMemoryTimeoutStoreTests.cs`

- [ ] **Step 1: GitNexus impact check**

Use the `gitnexus_impact` MCP tool with `target: "ReleaseDispatchedTimeoutAsync"`, `direction: "upstream"`. Report direct callers and risk level. Halt if HIGH/CRITICAL surfaces unexpectedly. (Expected callers: `ProcessManagerTimeoutService.PollOnceAsync` line 98.)

- [ ] **Step 2: Write the failing tests**

Add these two tests at the end of `src/ServiceConnect.UnitTests/InMemoryTimeoutStoreTests.cs` (inside the `InMemoryTimeoutStoreTests` class, before the closing brace):

```csharp
    [Fact]
    public async Task ReleaseDispatchedTimeout_ClearsLockFields_OnExistingEntry()
    {
        var now = new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(timeProvider: time);

        var id = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = id,
            Time = now.AddMinutes(-1),
            Locked = true,
            LockedBy = Guid.NewGuid(),
            LockExpiresAt = now.AddMinutes(5),
        });

        await store.ReleaseDispatchedTimeoutAsync(id);

        var batch = await store.GetTimeoutsBatchAsync();
        var fetched = Assert.Single(batch.DueTimeouts);
        Assert.Equal(id, fetched.Id);
        Assert.False(fetched.Locked);
        Assert.Equal(Guid.Empty, fetched.LockedBy);
        Assert.Null(fetched.LockExpiresAt);
    }

    [Fact]
    public async Task ReleaseDispatchedTimeout_UnknownId_DoesNotThrow()
    {
        var store = new InMemoryTimeoutStore();

        var exception = await Record.ExceptionAsync(() =>
            store.ReleaseDispatchedTimeoutAsync(Guid.NewGuid()));

        Assert.Null(exception);
    }

    [Fact]
    public async Task ReleaseDispatchedTimeout_PreCancelledToken_Throws()
    {
        var store = new InMemoryTimeoutStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.ReleaseDispatchedTimeoutAsync(Guid.NewGuid(), cts.Token));
    }
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~InMemoryTimeoutStoreTests.ReleaseDispatchedTimeout"
```

Expected: `ReleaseDispatchedTimeout_ClearsLockFields_OnExistingEntry` FAILS (Locked is still true). The other two PASS already (the no-op happens to satisfy them). That is fine — the failing test is what gates correctness.

- [ ] **Step 4: Apply the fix**

Replace lines 101-105 of `src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs` with:

```csharp
    public Task ReleaseDispatchedTimeoutAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _state.SyncRoot.EnterWriteLock();
        try
        {
            if (_state.TimeoutsById.TryGetValue(id, out var entry))
            {
                entry.Data.Locked = false;
                entry.Data.LockedBy = Guid.Empty;
                entry.Data.LockExpiresAt = null;
            }
        }
        finally
        {
            _state.SyncRoot.ExitWriteLock();
        }

        return Task.CompletedTask;
    }
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~InMemoryTimeoutStoreTests"
```

Expected: all `InMemoryTimeoutStoreTests` PASS.

- [ ] **Step 6: GitNexus scope check**

Use the `gitnexus_detect_changes` MCP tool with `scope: "all"`. Verify only the two files listed above show changes. Halt if any other file shows up.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs \
         src/ServiceConnect.UnitTests/InMemoryTimeoutStoreTests.cs
git commit -m "$(cat <<'EOF'
fix(R-001): implement InMemoryTimeoutStore.ReleaseDispatchedTimeoutAsync

Previously a no-op that silently broke the ITimeoutStore contract.
Now clears Locked/LockedBy/LockExpiresAt under the write lock to mirror
MongoDbTimeoutStore. GetTimeoutsBatchAsync still does not read these
fields (existing semantic gap), so observable polling behaviour is
unchanged for callers that only insert+remove.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: R-005 — Filter `OperationCanceledException` from `ProcessManagerTimeoutService` catches

**Files:**
- Modify: `src/ServiceConnect/Services/ProcessManagerTimeoutService.cs:92, 100`
- Modify: `src/ServiceConnect.UnitTests/Services/ProcessManagerTimeoutServiceTests.cs`

- [ ] **Step 1: GitNexus impact check**

Use `gitnexus_impact` with `target: "PollOnceAsync"`, `direction: "upstream"`. Report callers (expected: `PollLoop` and unit tests).

- [ ] **Step 2: Write the failing tests**

Append to `src/ServiceConnect.UnitTests/Services/ProcessManagerTimeoutServiceTests.cs` (inside the class):

```csharp
    [Fact]
    public async Task PollOnce_RemoveDispatchedThrowsOCE_Propagates()
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
                    Headers = new Dictionary<string, object>(),
                }
            },
            NextQueryTime = DateTimeOffset.UtcNow.AddSeconds(30),
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.IsAny<SendOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockFinder.Setup(f => f.RemoveDispatchedTimeoutAsync(timeoutId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = CreateSut(_mockFinder.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.PollOnceAsync());

        _mockFinder.Verify(f => f.ReleaseDispatchedTimeoutAsync(timeoutId, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollOnce_ReleaseDispatchedThrowsOCE_Propagates()
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
                    Headers = new Dictionary<string, object>(),
                }
            },
            NextQueryTime = DateTimeOffset.UtcNow.AddSeconds(30),
        };

        _mockFinder.Setup(f => f.GetTimeoutsBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(batch);
        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.IsAny<SendOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        _mockFinder.Setup(f => f.ReleaseDispatchedTimeoutAsync(timeoutId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = CreateSut(_mockFinder.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.PollOnceAsync());
    }
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~ProcessManagerTimeoutServiceTests.PollOnce_RemoveDispatchedThrowsOCE_Propagates|FullyQualifiedName~ProcessManagerTimeoutServiceTests.PollOnce_ReleaseDispatchedThrowsOCE_Propagates"
```

Expected: both tests FAIL because the catches currently swallow OCE.

- [ ] **Step 4: Apply the fix**

In `src/ServiceConnect/Services/ProcessManagerTimeoutService.cs`, change line 92 from:

```csharp
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error dispatching timeout {TimeoutId}", timeout.Id);
```

to:

```csharp
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Error dispatching timeout {TimeoutId}", timeout.Id);
```

And change line 100 from:

```csharp
                    catch (Exception releaseEx)
                    {
                        logger.LogError(releaseEx, "Error releasing timeout {TimeoutId} after dispatch failure", timeout.Id);
                    }
```

to:

```csharp
                    catch (Exception releaseEx) when (releaseEx is not OperationCanceledException)
                    {
                        logger.LogError(releaseEx, "Error releasing timeout {TimeoutId} after dispatch failure", timeout.Id);
                    }
```

The outer `catch (Exception ex)` at line 107 is left as-is (`PollLoop`'s `catch (OperationCanceledException) { break; }` at line 123 already handles it).

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~ProcessManagerTimeoutServiceTests"
```

Expected: all `ProcessManagerTimeoutServiceTests` PASS.

- [ ] **Step 6: GitNexus scope check**

`gitnexus_detect_changes({scope: "all"})`. Verify only the two files listed above show changes.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect/Services/ProcessManagerTimeoutService.cs \
         src/ServiceConnect.UnitTests/Services/ProcessManagerTimeoutServiceTests.cs
git commit -m "$(cat <<'EOF'
fix(R-005): propagate OperationCanceledException from PollOnceAsync

The dispatch and release catches in PollOnceAsync swallowed OCE,
masking host-shutdown cancellations and turning them into logged
errors that allowed the loop to keep processing. Add an exception
filter so OCE bypasses the catch and bubbles up to PollLoop's
existing OCE handler. Outer catch at line 107 is unchanged.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: R-002 — Switch `StreamProcessor` to `IAsyncDisposable`

**Files:**
- Modify: `src/ServiceConnect/Services/Processors/StreamProcessor.cs:8, 185-188`
- Modify: `src/ServiceConnect.UnitTests/Processors/StreamProcessorTests.cs`

- [ ] **Step 1: GitNexus impact check**

Use `gitnexus_impact` with `target: "StreamProcessor"`, `direction: "upstream"`. Report direct callers and DI registration sites (expected: `ServiceCollectionExtensions` registers it; the host's DI container disposes it).

If any caller explicitly casts to `IDisposable` or calls `.Dispose()` directly, surface that — those will need updating too.

- [ ] **Step 2: Write the failing test**

Append to `src/ServiceConnect.UnitTests/Processors/StreamProcessorTests.cs` (inside the class):

```csharp
    [Fact]
    public void StreamProcessor_Implements_IAsyncDisposable()
    {
        // R-002: Disposal must wait for in-flight EvictStaleStreams callbacks.
        // ITimer.DisposeAsync awaits the callback; ITimer.Dispose does not.
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(StreamProcessor)),
            "StreamProcessor must implement IAsyncDisposable so disposal waits for the cleanup-timer callback.");
    }

    [Fact]
    public async Task DisposeAsync_CompletesWithoutThrowing()
    {
        var processor = BuildProcessor();
        var ex = await Record.ExceptionAsync(async () =>
        {
            await ((IAsyncDisposable)processor).DisposeAsync();
        });
        Assert.Null(ex);
    }
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~StreamProcessorTests.StreamProcessor_Implements_IAsyncDisposable|FullyQualifiedName~StreamProcessorTests.DisposeAsync_CompletesWithoutThrowing"
```

Expected: `StreamProcessor_Implements_IAsyncDisposable` FAILS; `DisposeAsync_CompletesWithoutThrowing` may FAIL to compile (cast to `IAsyncDisposable` invalid).

- [ ] **Step 4: Apply the fix**

In `src/ServiceConnect/Services/Processors/StreamProcessor.cs`:

Change line 8 from:
```csharp
internal sealed class StreamProcessor : IMessageProcessor, IDisposable
```
to:
```csharp
internal sealed class StreamProcessor : IMessageProcessor, IAsyncDisposable
```

Replace lines 185-188:
```csharp
    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }
```
with:
```csharp
    public ValueTask DisposeAsync()
    {
        return _cleanupTimer.DisposeAsync();
    }
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~StreamProcessorTests"
```

Expected: all `StreamProcessorTests` PASS.

- [ ] **Step 6: Run the full unit-test suite**

Because `StreamProcessor`'s public contract changed (from `IDisposable` to `IAsyncDisposable`), run the full suite to catch any consumer that broke:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj
```

Expected: green. If anything fails, the consumer needs updating to `await using` or to call `DisposeAsync()`.

- [ ] **Step 7: GitNexus scope check**

`gitnexus_detect_changes({scope: "all"})`. Verify only the two files above show changes. Halt if any other file shows up — that would mean a consumer needed updating and the previous step missed it.

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect/Services/Processors/StreamProcessor.cs \
         src/ServiceConnect.UnitTests/Processors/StreamProcessorTests.cs
git commit -m "$(cat <<'EOF'
fix(R-002): make StreamProcessor IAsyncDisposable

ITimer.Dispose() does not wait for in-flight callbacks, so
EvictStaleStreams could run after Dispose returned. The race is
benign today (the callback only touches thread-safe state) but
exposed any future cleanup-in-Dispose change. Switch to
IAsyncDisposable + ITimer.DisposeAsync(), which awaits the in-flight
callback before completing.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: R-022 — Tolerate concurrent index creation in `MongoDbTimeoutStore`

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs:175-204`
- Modify: `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreTests.cs`

- [ ] **Step 1: GitNexus impact check**

Use `gitnexus_impact` with `target: "EnsureTimeoutIndexAsync"`, `direction: "upstream"`. Report callers (expected: only `InsertTimeoutAsync`).

- [ ] **Step 2: Write the failing tests**

Append to `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreTests.cs`. Add the necessary `using` directives at the top:

```csharp
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Configuration;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using Moq;
using ServiceConnect.Interfaces.Exceptions;
```

(Some of these may already be present — keep one copy.)

Then add inside the `MongoDbTimeoutStoreTests` class:

```csharp
    private static MongoCommandException MakeMongoCommandException(int code)
    {
        var connectionId = new ConnectionId(
            new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017)));
        var result = new MongoDB.Bson.BsonDocument
        {
            ["ok"] = 0,
            ["code"] = code,
            ["errmsg"] = $"index conflict (code {code})",
        };
        var command = new MongoDB.Bson.BsonDocument { ["createIndexes"] = "Timeouts" };
        return new MongoCommandException(connectionId, $"command failed with code {code}", command, result);
    }

    private static (MongoDbTimeoutStore Store, Mock<IMongoCollection<TimeoutData>> Collection)
        BuildStoreWithIndexException(int? throwCode)
    {
        var indexes = new Mock<IMongoIndexManager<TimeoutData>>();
        var indexSetup = indexes.Setup(m => m.CreateManyAsync(
            It.IsAny<IEnumerable<CreateIndexModel<TimeoutData>>>(),
            It.IsAny<CancellationToken>()));
        if (throwCode.HasValue)
            indexSetup.ThrowsAsync(MakeMongoCommandException(throwCode.Value));
        else
            indexSetup.ReturnsAsync(new List<string> { "ok" });

        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.SetupGet(c => c.Indexes).Returns(indexes.Object);
        collection.Setup(c => c.InsertOneAsync(
                It.IsAny<TimeoutData>(),
                It.IsAny<InsertOneOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<TimeoutData>("Timeouts", null))
            .Returns(collection.Object);

        var client = new Mock<IMongoClient>();
        client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);

        var store = new MongoDbTimeoutStore(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test" },
            NullLogger<MongoDbTimeoutStore>.Instance);

        return (store, collection);
    }

    [Fact]
    public async Task InsertTimeout_IndexConflictCode85_IsTolerated_AndInsertProceeds()
    {
        var (store, collection) = BuildStoreWithIndexException(85);

        var ex = await Record.ExceptionAsync(() =>
            store.InsertTimeoutAsync(new TimeoutData { Id = Guid.NewGuid(), Time = DateTimeOffset.UtcNow }));

        Assert.Null(ex);
        collection.Verify(c => c.InsertOneAsync(
                It.IsAny<TimeoutData>(),
                It.IsAny<InsertOneOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InsertTimeout_IndexConflictCode86_IsTolerated_AndInsertProceeds()
    {
        var (store, collection) = BuildStoreWithIndexException(86);

        var ex = await Record.ExceptionAsync(() =>
            store.InsertTimeoutAsync(new TimeoutData { Id = Guid.NewGuid(), Time = DateTimeOffset.UtcNow }));

        Assert.Null(ex);
        collection.Verify(c => c.InsertOneAsync(
                It.IsAny<TimeoutData>(),
                It.IsAny<InsertOneOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InsertTimeout_OtherMongoCommandException_PropagatesAsPersistenceException()
    {
        var (store, _) = BuildStoreWithIndexException(13); // Unauthorized — must NOT be swallowed.

        await Assert.ThrowsAsync<PersistenceException>(() =>
            store.InsertTimeoutAsync(new TimeoutData { Id = Guid.NewGuid(), Time = DateTimeOffset.UtcNow }));
    }
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~MongoDbTimeoutStoreTests.InsertTimeout_IndexConflict"
```

Expected: both `Code85` and `Code86` tests FAIL (currently propagate as `PersistenceException`). The `OtherMongoCommandException` test PASSES already.

- [ ] **Step 4: Apply the fix**

In `src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs`, replace lines 175-204 (the `EnsureTimeoutIndexAsync` method) with:

```csharp
    private async Task EnsureTimeoutIndexAsync(IMongoCollection<TimeoutData> collection)
    {
        if (_timeoutIndexEnsured) return;

        try
        {
            var idIndexModel = new CreateIndexModel<TimeoutData>(
                Builders<TimeoutData>.IndexKeys.Ascending(x => x.Id));

            var lockedTimeIndexModel = new CreateIndexModel<TimeoutData>(
                Builders<TimeoutData>.IndexKeys
                    .Ascending(x => x.Locked)
                    .Ascending(x => x.Time));

            var lockedByIndexModel = new CreateIndexModel<TimeoutData>(
                Builders<TimeoutData>.IndexKeys
                    .Ascending(x => x.LockedBy)
                    .Ascending(x => x.Locked));

            var lockExpiresAtIndexModel = new CreateIndexModel<TimeoutData>(
                Builders<TimeoutData>.IndexKeys.Ascending(x => x.LockExpiresAt));

            await collection.Indexes.CreateManyAsync(
                [idIndexModel, lockedTimeIndexModel, lockedByIndexModel, lockExpiresAtIndexModel]
            ).ConfigureAwait(false);
            _timeoutIndexEnsured = true;
        }
        catch (MongoCommandException ex) when (ex.Code is 85 or 86)
        {
            // 85 IndexOptionsConflict / 86 IndexKeySpecsConflict — another process
            // created the same index concurrently. Treat as success to avoid spurious
            // first-insert failures in multi-process deployments.
            _timeoutIndexEnsured = true;
        }
    }
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~MongoDbTimeoutStoreTests"
```

Expected: all `MongoDbTimeoutStoreTests` PASS.

- [ ] **Step 6: GitNexus scope check**

`gitnexus_detect_changes({scope: "all"})`. Verify only the two files above.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs \
         src/ServiceConnect.UnitTests/MongoDbTimeoutStoreTests.cs
git commit -m "$(cat <<'EOF'
fix(R-022): tolerate concurrent index creation in MongoDbTimeoutStore

EnsureTimeoutIndexAsync had a dead try/catch that rethrew everything,
so multi-process deployments where two processes hit InsertTimeoutAsync
simultaneously could fail the loser with MongoCommandException code 85
(IndexOptionsConflict) or 86 (IndexKeySpecsConflict). Filter on those
codes and treat as success — the index exists either way.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: R-026 — Replace `Debug.Assert` in `HeaderDecoder.Decode` with a runtime guard

**Files:**
- Modify: `src/ServiceConnect.Interfaces/HeaderDecoder.cs`
- Create: `src/ServiceConnect.UnitTests/HeaderDecoderTests.cs`

- [ ] **Step 1: GitNexus impact check**

Use `gitnexus_impact` with `target: "Decode"` (or `"HeaderDecoder.Decode"` if disambiguation needed), `direction: "upstream"`. Enumerate every call site. The fix is a behaviour change in RELEASE — if any caller relies on the silent `value?.ToString()` fallback for non-string/non-byte[] types, halt and surface that.

Expected callers (from earlier reading): `StreamProcessor` (parses Guid / long), `MessageDispatcher`, others that all pass header values that arrive as `byte[]` or `string`.

- [ ] **Step 2: Write the failing tests**

Create `src/ServiceConnect.UnitTests/HeaderDecoderTests.cs`:

```csharp
using System.Text;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests;

public class HeaderDecoderTests
{
    [Fact]
    public void Decode_NullValue_ReturnsNull()
    {
        Assert.Null(HeaderDecoder.Decode(null));
    }

    [Fact]
    public void Decode_StringValue_ReturnsSameString()
    {
        Assert.Equal("hello", HeaderDecoder.Decode("hello"));
    }

    [Fact]
    public void Decode_ByteArrayValue_ReturnsUtf8DecodedString()
    {
        var bytes = Encoding.UTF8.GetBytes("payload");

        Assert.Equal("payload", HeaderDecoder.Decode(bytes));
    }

    [Fact]
    public void Decode_UnexpectedIntType_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => HeaderDecoder.Decode(42));
        Assert.Contains("System.Int32", ex.Message);
    }

    [Fact]
    public void Decode_UnexpectedGuidType_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => HeaderDecoder.Decode(Guid.NewGuid()));
        Assert.Contains("System.Guid", ex.Message);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~HeaderDecoderTests"
```

Expected: `Decode_UnexpectedIntType_Throws` and `Decode_UnexpectedGuidType_Throws` FAIL (current code returns `"42"` / the GUID's string form). The other three PASS.

- [ ] **Step 4: Apply the fix**

Replace the entire body of `src/ServiceConnect.Interfaces/HeaderDecoder.cs` with:

```csharp
using System.Runtime.CompilerServices;
using System.Text;

namespace ServiceConnect.Interfaces;

public static class HeaderDecoder
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string? Decode(object? value)
    {
        if (value is null) return null;
        if (value is byte[] bytes) return Encoding.UTF8.GetString(bytes);
        if (value is string s) return s;
        throw new ArgumentException(
            $"Unexpected header value type: {value.GetType().FullName}",
            nameof(value));
    }
}
```

(The `using System.Diagnostics;` directive is removed — no longer needed.)

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~HeaderDecoderTests"
```

Expected: all PASS.

- [ ] **Step 6: Run the full unit-test suite**

This is a behaviour change in RELEASE for any caller currently passing a non-string/non-byte[] header. Run the whole suite:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj
```

Expected: all green. If anything fails, an internal caller was relying on the silent fallback — investigate before committing.

- [ ] **Step 7: GitNexus scope check**

`gitnexus_detect_changes({scope: "all"})`. Verify only the two files above show changes.

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect.Interfaces/HeaderDecoder.cs \
         src/ServiceConnect.UnitTests/HeaderDecoderTests.cs
git commit -m "$(cat <<'EOF'
fix(R-026): throw on unexpected header value types in HeaderDecoder

Debug.Assert is a no-op in RELEASE, so unexpected types (e.g. boxed
ints) silently fell through to value.ToString(), producing strings
like "System.Int32" that downstream Guid/long parsers fail on with
unhelpful errors. Replace with a runtime guard that throws
ArgumentException at the boundary, naming the offending type.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: R-016 — Annotate `Bus.BuildRoutingSlip` loop start

**Files:**
- Modify: `src/ServiceConnect/Bus.cs:390-405`

No tests — comment-only change.

- [ ] **Step 1: GitNexus impact check**

Use `gitnexus_impact` with `target: "BuildRoutingSlip"`, `direction: "upstream"`. Should be low risk (comment-only change).

- [ ] **Step 2: Add the comment**

In `src/ServiceConnect/Bus.cs`, change the body of `BuildRoutingSlip` (lines 390-405). The current code is:

```csharp
    private static string BuildRoutingSlip(IList<string> destinations)
    {
        if (destinations.Count <= 1)
            return string.Empty;

        var builder = new System.Text.StringBuilder();
        for (var index = 1; index < destinations.Count; index++)
        {
            if (index > 1)
                builder.Append(',');

            builder.Append(destinations[index]);
        }

        return builder.ToString();
    }
```

Change it to:

```csharp
    private static string BuildRoutingSlip(IList<string> destinations)
    {
        if (destinations.Count <= 1)
            return string.Empty;

        // destinations[0] is the immediate send target; the routing slip describes
        // the *subsequent* hops, so the loop deliberately starts at index 1 (R-016).
        var builder = new System.Text.StringBuilder();
        for (var index = 1; index < destinations.Count; index++)
        {
            if (index > 1)
                builder.Append(',');

            builder.Append(destinations[index]);
        }

        return builder.ToString();
    }
```

- [ ] **Step 3: Verify compile**

```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj
```

Expected: build succeeds.

- [ ] **Step 4: GitNexus scope check**

`gitnexus_detect_changes({scope: "all"})`. Verify only `src/ServiceConnect/Bus.cs` shows a change.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Bus.cs
git commit -m "$(cat <<'EOF'
docs(R-016): explain BuildRoutingSlip's index-1 loop start

The off-by-one was intentional but undocumented. Add a comment
clarifying that destinations[0] is the immediate send target and
the routing slip lists subsequent hops only.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: R-030 — Document `RequestOptions.ExpectedReplyCount = 0` semantics

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Options/RequestOptions.cs`

No tests — XML doc only.

- [ ] **Step 1: GitNexus impact check**

`gitnexus_impact({target: "ExpectedReplyCount", direction: "upstream"})`. Comment-only change, expect low risk.

- [ ] **Step 2: Apply the doc change**

Replace the entire content of `src/ServiceConnect.Interfaces/Options/RequestOptions.cs` with:

```csharp
namespace ServiceConnect.Interfaces.Options;

public sealed class RequestOptions
{
    public const int DefaultTimeoutMs = 10_000;
    public static RequestOptions Default { get; } = new();

    public Dictionary<string, string>? Headers { get; set; }
    public string? EndPoint { get; set; }
    public IList<string>? EndPoints { get; set; }
    public int Timeout { get; set; } = DefaultTimeoutMs;

    /// <summary>
    /// Number of replies the multi-request should wait for before completing
    /// (only used by <c>SendRequestMultiAsync</c>).
    /// </summary>
    /// <remarks>
    /// Behaviour:
    /// <list type="bullet">
    /// <item><description>
    /// <b>Positive value</b> — the call completes as soon as that many replies
    /// have arrived, or when <see cref="Timeout"/> elapses (whichever happens first).
    /// </description></item>
    /// <item><description>
    /// <b>Zero or negative</b> — the call always waits the full <see cref="Timeout"/>
    /// and returns every reply received during the window.
    /// </description></item>
    /// <item><description>
    /// <b><c>null</c> (default)</b> — falls back to <see cref="EndPoints"/>.Count if
    /// <see cref="EndPoints"/> is set; otherwise behaves as the negative case
    /// (timeout-only).
    /// </description></item>
    /// </list>
    /// </remarks>
    public int? ExpectedReplyCount { get; set; }
}
```

- [ ] **Step 3: Verify compile**

```bash
dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj
```

Expected: build succeeds (no warnings about XML).

- [ ] **Step 4: GitNexus scope check**

`gitnexus_detect_changes({scope: "all"})`. Verify only `RequestOptions.cs` shows a change.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Interfaces/Options/RequestOptions.cs
git commit -m "$(cat <<'EOF'
docs(R-030): document RequestOptions.ExpectedReplyCount semantics

Clarify the three modes: positive (early completion when count
reached), zero-or-negative (always wait the full Timeout, return
every reply), and null (default to EndPoints.Count, otherwise
timeout-only).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Wrap-up (do once after all tasks)

- [ ] **Step 1: Run the full unit-test suite a final time**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj
```

Expected: all green.

- [ ] **Step 2: Run the E2E suite**

The E2E suite uses Testcontainers; the user is not in the docker group, so wrap with `sg docker -c '...'`:

```bash
sg docker -c 'dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj'
```

Expected: all green. If a test fails, investigate whether it relates to one of the seven fixes or is a pre-existing flake — do not silently revert.

- [ ] **Step 3: Refresh the GitNexus index**

The post-commit hook should have already triggered `npx gitnexus analyze` after each commit. Confirm freshness via the `gitnexus_context` MCP resource. If stale, run:

```bash
npx gitnexus analyze --embeddings
```

- [ ] **Step 4: Review the commit history**

```bash
git log --oneline -10
```

Expected: seven new `fix(R-NNN): …` or `docs(R-NNN): …` commits on top of the spec commit.

- [ ] **Step 5: Report status**

Summarise to the user:
- Fixes applied (list R-IDs)
- Unit test count delta (added X tests; all green)
- E2E result
- Any deviations from the plan and why
