# ServiceConnect-CSharp Code Review Findings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Address all verified critical and high severity code review findings from the comprehensive code review.

**Architecture:** The plan addresses findings across 6 dimensions: Async/Threading correctness, Architecture SOLID violations, CLEAN code principles, .NET best practices, Technical Debt, and Security. Each fix will be implemented with TDD approach and verified with existing/new tests.

**Tech Stack:** .NET 8, C#, xUnit, Moq, FluentAssertions

---

## Summary of Verified Findings

| ID | Severity | Category | Description |
|----|----------|----------|-------------|
| C-01 | Critical | Async/Threading | Fire-and-forget in Producer.Dispose() - resource leaks |
| C-02 | Critical | Async/Threading | Fire-and-forget in Connection.Dispose() - resource leaks |
| C-03 | Critical | Async/Threading | Null handler invocation in Client.Event() - NRE risk |
| C-04 | Critical | Async/Threading | Sync-over-async in Client.CloseChannel() - deadlock risk |
| C-05 | High | Async/Threading | Missing ConfigureAwait in Client.cs |
| C-06 | High | .NET Best Practices | Random thread-safety in Retry.cs |
| S-01 | High | Architecture | Layering violation - RabbitMQ depends on Core |
| S-02 | High | Architecture | IBus fat interface (11 methods) violates ISP |
| B-01 | Medium | .NET Best Practices | Missing CancellationToken on async methods |
| B-02 | Medium | CLEAN | Mutable dictionary exposure in TransportConfiguration |
| T-01 | High | Security | SSL certificate revocation disabled |
| T-02 | Medium | Tech Debt | Inconsistent exception types in persistence |

---

## Task 1: Fix Fire-and-Forget in Producer.Dispose()

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer.cs:190-199`

- [ ] **Step 1: Write failing test for Dispose async behavior**

```csharp
// In ProducerTests.cs
[Fact]
public async Task DisposeAsync_ShouldCompleteBeforeReturning()
{
    // Arrange
    var producer = new Producer(_connection, _config, _logger);
    await producer.InitializeAsync();

    // Act
    var disposeTask = producer.DisposeAsync(); // Need to add this method
    await disposeTask;

    // Assert - verify disposal completed
    Assert.True(producer.IsDisposed);
}
```

- [ ] **Step 2: Run test to verify it fails**
Expected: FAIL - method doesn't exist yet

- [ ] **Step 3: Implement DisposeAsyncCore and track disposal**

```csharp
private Task? _disposeTask;

public void Dispose()
{
    if (_disposed) return;
    _disposed = true;
    _disposeTask = Task.Run(async () =>
    {
        try { await DisposeAsyncCore().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "Error during dispose"); }
    });
}

public async Task DisposeAsync()
{
    if (_disposed) return;
    _disposed = true;
    await DisposeAsyncCore().ConfigureAwait(false);
}
```

- [ ] **Step 4: Run test to verify it passes**
Expected: PASS

- [ ] **Step 5: Commit**
```bash
git add src/ServiceConnect.Client.RabbitMQ/Producer.cs
git commit -m "fix: add async disposal pattern to Producer to prevent resource leaks"
```

---

## Task 2: Fix Fire-and-Forget in Connection.Dispose()

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Connection.cs:109-128`

- [ ] **Step 1: Write failing test for Connection dispose**
```csharp
[Fact]
public async Task DisposeAsync_ShouldCloseConnection()
{
    var connection = new Connection(_options, _logger);
    await connection.ConnectAsync();
    
    await connection.DisposeAsync(); // Need to add
    
    Assert.False(connection.IsConnected);
}
```

- [ ] **Step 2: Run test to verify it fails**
Expected: FAIL

- [ ] **Step 3: Implement DisposeAsync in Connection**
```csharp
private Task? _disposeTask;

public void Dispose()
{
    if (_connection == null) return;
    var conn = _connection;
    _connection = null;
    _disposeTask = Task.Run(async () =>
    {
        try
        {
            if (conn.IsOpen)
                await conn.CloseAsync().ConfigureAwait(false);
            conn.Dispose();
        }
        catch (Exception ex) { logger.LogError(ex, "Error closing connection during dispose"); }
    });
}

public async ValueTask DisposeAsync()
{
    if (_connection == null) return;
    var conn = _connection;
    _connection = null;
    if (conn.IsOpen)
        await conn.CloseAsync().ConfigureAwait(false);
    conn.Dispose();
}
```

- [ ] **Step 4: Run test to verify it passes**
Expected: PASS

- [ ] **Step 5: Commit**
```bash
git add src/ServiceConnect.Client.RabbitMQ/Connection.cs
git commit -m "fix: add async disposal pattern to Connection"
```

---

## Task 3: Fix Null Handler Invocation in Client.Event()

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Client.cs:122`

- [ ] **Step 1: Write failing test for null handler protection**
```csharp
[Fact]
public async Task Event_ShouldNotThrowWhenHandlerIsNull()
{
    var client = new Client(...);
    // Setup without setting consumer handler

    var args = CreateFakeBasicDeliverEventArgs();
    
    // Should not throw NRE
    await client.Event(null!, args); // passing null to test protection
    
    // Verify message was not processed (logged error)
}
```

- [ ] **Step 2: Run test to verify it fails**
Expected: FAIL - currently throws NRE

- [ ] **Step 3: Add null check before handler invocation**
```csharp
// Around line 120-122 in Client.cs
if (_consumerEventHandler == null)
{
    _logger.LogError("Consumer event handler is not set. Message discarded.");
    return ProcessResult.NotHandled;
}
result = await _consumerEventHandler(args.Body.ToArray(), typeName, headers);
```

- [ ] **Step 4: Run test to verify it passes**
Expected: PASS

- [ ] **Step 5: Commit**
```bash
git add src/ServiceConnect.Client.RabbitMQ/Client.cs
git commit -m "fix: add null check for consumer event handler to prevent NRE"
```

---

## Task 4: Fix Sync-over-Async in Client.CloseChannel()

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Client.cs:314-329`

- [ ] **Step 1: Write failing test for CloseChannelAsync**
```csharp
[Fact]
public async Task CloseChannelAsync_ShouldNotBlock()
{
    var client = new Client(...);
    await client.StartConsumingAsync(...);
    
    // Act - should complete without blocking
    await client.CloseChannelAsync();
    
    Assert.Null(client.Channel);
}
```

- [ ] **Step 2: Run test to verify it fails**
Expected: FAIL - CloseChannelAsync doesn't exist

- [ ] **Step 3: Implement CloseChannelAsync and remove blocking call**
```csharp
private async Task CloseChannelAsync()
{
    if (_model == null) return;
    try
    {
        if (_model.IsOpen)
            await _model.CloseAsync().ConfigureAwait(false);
        _model.Dispose();
    }
    catch (ObjectDisposedException) { }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Error closing channel during dispose");
    }
    _model = null;
}
```

- [ ] **Step 4: Run test to verify it passes**
Expected: PASS

- [ ] **Step 5: Commit**
```bash
git add src/ServiceConnect.Client.RabbitMQ/Client.cs
git commit -m "fix: add async CloseChannelAsync to eliminate sync-over-async"
```

---

## Task 5: Add ConfigureAwait(false) in Client.cs

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Client.cs`

- [ ] **Step 1: Add ConfigureAwait to all await calls in Client.cs**

Locations needing fixes:
- Line 71: `await ProcessMessage(args);`
- Line 83: `await _model!.BasicAckAsync(...);`
- Line 85: `await _model!.BasicNackAsync(...);`
- Line 122: `await _consumerEventHandler!(...);`
- Line 211: `await _connection.CreateChannelAsync(...);`
- Line 215: `await _model.BasicQosAsync(...);`
- Line 221: `await _model.BasicConsumeAsync(...);`

```csharp
// Line 71 - change from
await ProcessMessage(args);
// to
await ProcessMessage(args).ConfigureAwait(false);
```

- [ ] **Step 2: Run tests to verify nothing breaks**
Expected: All tests pass

- [ ] **Step 3: Commit**
```bash
git add src/ServiceConnect.Client.RabbitMQ/Client.cs
git commit -m "chore: add ConfigureAwait(false) to all async calls in Client"
```

---

## Task 6: Fix Random Thread-Safety in Retry.cs

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Retry.cs:5,71`

- [ ] **Step 1: Write failing test for concurrent retry calculations**
```csharp
[Fact]
public async Task CalculateDelay_ShouldBeThreadSafe()
{
    var tasks = Enumerable.Range(0, 100)
        .Select(_ => Task.Run(() => Retry.CalculateDelay(TimeSpan.FromSeconds(1), 3)))
        .ToList();
    
    var results = await Task.WhenAll(tasks);
    
    // All should complete without exception
    Assert.All(results, r => Assert.True(r >= TimeSpan.Zero));
}
```

- [ ] **Step 2: Run test to verify it fails or shows thread-safety issue**
Expected: May pass but could show duplicate random values

- [ ] **Step 3: Fix by using Random.Shared**
```csharp
// Line 5 - change from
private static readonly Random Jitter = new();
// to
// In CalculateDelay (line 71), change from
var jitterMs = Jitter.Next(0, (int)Math.Min(baseInterval.TotalMilliseconds, 1000));
// to
var jitterMs = Random.Shared.Next(0, (int)Math.Min(baseInterval.TotalMilliseconds, 1000));
```

- [ ] **Step 4: Run test to verify it passes**
Expected: PASS

- [ ] **Step 5: Commit**
```bash
git add src/ServiceConnect.Client.RabbitMQ/Retry.cs
git commit -m "fix: use Random.Shared for thread-safe jitter generation"
```

---

## Task 7: Add CancellationToken to Async Methods

**Files:**
- Modify: `src/ServiceConnect.Interfaces/IBus.cs`
- Modify: `src/ServiceConnect/Bus.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Client.cs`

- [ ] **Step 1: Add CancellationToken to IBus interface methods**
```csharp
// IBus.cs - add CancellationToken to all async methods
Task PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default);
Task SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default);
Task SendRequestAsync<T, TReply>(T message, RequestOptions? options = null, CancellationToken cancellationToken = default);
// etc.
```

- [ ] **Step 2: Update Bus.cs implementation**
- [ ] **Step 3: Update Client.cs and Consumer.cs**
- [ ] **Step 4: Update all processors to propagate CancellationToken**
- [ ] **Step 5: Run tests**
- [ ] **Step 6: Commit**
```bash
git add src/ServiceConnect.Interfaces/IBus.cs src/ServiceConnect/Bus.cs
git commit -m "feat: add CancellationToken support to IBus and implementations"
```

---

## Task 8: Fix Mutable Dictionary Exposure in TransportConfiguration

**Files:**
- Modify: `src/ServiceConnect/Configuration/TransportConfiguration.cs:37`

- [ ] **Step 1: Write test for immutable ClientSettings**
```csharp
[Fact]
public void ClientSettings_ShouldBeReadOnly()
{
    var config = new TransportConfiguration();
    var settings = config.ClientSettings;
    
    // Should not be able to modify
    Assert.Throws<NotSupportedException>(() => settings.Add("key", "value"));
}
```

- [ ] **Step 2: Run test to verify it fails**
Expected: FAIL - currently allows modification

- [ ] **Step 3: Make ClientSettings read-only**
```csharp
private Dictionary<string, object> _clientSettings = new();
public IReadOnlyDictionary<string, object> ClientSettings => _clientSettings;

// Add method to modify
public void SetClientSetting(string key, object value) => _clientSettings[key] = value;
```

- [ ] **Step 4: Run test to verify it passes**
Expected: PASS

- [ ] **Step 5: Commit**
```bash
git add src/ServiceConnect/Configuration/TransportConfiguration.cs
git commit -m "fix: expose ClientSettings as IReadOnlyDictionary to prevent external mutation"
```

---

## Task 9: Fix SSL Certificate Revocation Disabled

**Files:**
- Modify: `filters/ServiceConnect.Filters.MessageDeduplication/.../MessageDeduplicationPersistorMongoDbSsl.cs:122`

- [ ] **Step 1: Write test for SSL settings validation**
```csharp
[Fact]
public void SslSettings_ShouldEnableCertificateRevocationCheck()
{
    var settings = CreateSslSettings();
    Assert.True(settings.CheckCertificateRevocation);
}
```

- [ ] **Step 2: Run test to verify it fails**
Expected: FAIL

- [ ] **Step 3: Change CheckCertificateRevocation to true**
```csharp
// Line 122 - change from
CheckCertificateRevocation = false
// to
CheckCertificateRevocation = true
```

- [ ] **Step 4: Run test to verify it passes**
Expected: PASS

- [ ] **Step 5: Commit**
```bash
git add filters/ServiceConnect.Filters.MessageDeduplication/.../MessageDeduplicationPersistorMongoDbSsl.cs
git commit -m "security: enable certificate revocation checking in MongoDB SSL settings"
```

---

## Task 10: Consolidate Exception Types in Persistence

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs`

- [ ] **Step 1: Add tests for exception types**
```csharp
[Fact]
public void FindData_WhenMappingMissing_ShouldThrowPersistenceException()
{
    var finder = new InMemoryProcessManagerFinder(...);
    
    Assert.Throws<PersistenceException>(() => finder.FindData<Message>(...));
}
```

- [ ] **Step 2: Run test to verify it fails**
Expected: FAIL - currently throws InvalidOperationException

- [ ] **Step 3: Update to throw PersistenceException**
```csharp
// In InMemoryProcessManagerFinder.cs
// Change from throwing InvalidOperationException to PersistenceException
throw new PersistenceException($"...");
```

- [ ] **Step 4: Run test to verify it passes**
Expected: PASS

- [ ] **Step 5: Commit**
```bash
git add src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs
git commit -m "fix: standardize exception types in InMemory persistence to use PersistenceException"
```

---

## Verification Phase

After completing all tasks:

- [ ] Run `dotnet build` - must compile with zero errors
- [ ] Run `dotnet test` - all tests must pass
- [ ] Run `dotnet test --coverage` - verify test coverage improved

---

## Plan Execution Options

**1. Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks

**2. Inline Execution** - Execute tasks in this session using executing-plans

**Which approach would you prefer?**