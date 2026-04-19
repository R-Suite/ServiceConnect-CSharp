# Transport And Request/Reply Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Harden reply handling so reply traffic honors consume filters, forged request headers cannot bypass reply-destination validation, unknown replies stop being silently accepted, and the RabbitMQ client builds on both target frameworks.

**Architecture:** Keep the public API stable where possible by changing internal trust checks and dispatch ordering. Treat reply handling as a transport concern that still passes through before-consume filters, and use the request manager as the source of truth for whether a request/reply context is locally owned.

**Tech Stack:** C#, .NET 8/10, xUnit, Moq, Microsoft.Extensions.DependencyInjection, RabbitMQ client

---

## File Map

- Modify: `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs`
  Replace the `Lock` field with a synchronization primitive available on both target frameworks.
- Modify: `src/ServiceConnect/Services/RequestReplyManager.cs`
  Add internal trust-query support and return reply-processing status instead of silently swallowing unknown replies.
- Modify: `src/ServiceConnect.Interfaces/IRequestReplyManager.cs`
  Update the internal contract so the dispatcher can distinguish handled replies from invalid reply traffic.
- Modify: `src/ServiceConnect/Services/Processors/ReplyProcessor.cs`
  Route reply handling through the new status-returning contract.
- Modify: `src/ServiceConnect/Services/MessageDispatcher.cs`
  Move reply processing so before-consume filters always run first.
- Modify: `src/ServiceConnect/Services/ConsumeContext.cs`
  Use a verifiable local-request trust check instead of trusting raw `RequestMessageId` header presence.
- Modify: `src/ServiceConnect/Services/ConsumeContextPool.cs`
  Mirror the `ConsumeContext` trust-check change for pooled contexts.
- Modify: `src/ServiceConnect/ServiceCollectionExtensions.cs`
  Ensure any new constructor dependencies still resolve cleanly.
- Test: `src/ServiceConnect.UnitTests/ConsumeContextTests.cs`
- Test: `src/ServiceConnect.UnitTests/MessageDispatcherTests.cs`
- Test: `src/ServiceConnect.UnitTests/Processors/ReplyProcessorTests.cs`
- Test: `src/ServiceConnect.UnitTests/RequestReplyManagerTests.cs`

### Task 1: Restore `net8.0` RabbitMQ Build Compatibility

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs:34,126-133`

- [ ] **Step 1: Add a failing cross-target build check to your working notes**

Use the existing compile failure as the red state for this task.

Run: `rtk dotnet build "src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj" -f net8.0`
Expected: FAIL with `CS0246` complaining that `Lock` cannot be found in `RabbitMqConsumerHost.cs`.

- [ ] **Step 2: Replace `Lock` with an `object` monitor field**

Update the field and keep the existing `lock (...)` blocks intact.

```csharp
private readonly object _callbackAdmissionGate = new();
```

- [ ] **Step 3: Verify the RabbitMQ project now builds for `net8.0`**

Run: `rtk dotnet build "src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj" -f net8.0`
Expected: PASS with `Build succeeded`.

- [ ] **Step 4: Verify the RabbitMQ project still builds for `net10.0`**

Run: `rtk dotnet build "src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj" -f net10.0`
Expected: PASS with `Build succeeded`.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs
git commit -m "fix: restore rabbitmq consumer cross-target locking"
```

### Task 2: Make Reply Processing Report Invalid Reply Traffic

**Files:**
- Modify: `src/ServiceConnect.Interfaces/IRequestReplyManager.cs:32`
- Modify: `src/ServiceConnect/Services/RequestReplyManager.cs:216-276`
- Modify: `src/ServiceConnect/Services/Processors/ReplyProcessor.cs:13-29`
- Test: `src/ServiceConnect.UnitTests/Processors/ReplyProcessorTests.cs`
- Test: `src/ServiceConnect.UnitTests/RequestReplyManagerTests.cs`

- [ ] **Step 1: Add a failing unit test for unknown replies**

In `RequestReplyManagerTests`, add a test that expects unknown replies to report "not handled" instead of silently succeeding.

```csharp
[Fact]
public void ProcessReply_ReturnsFalse_WhenMessageIdIsUnknown()
{
    var manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

    var handled = manager.ProcessReply(Guid.NewGuid().ToString(), new byte[] { 1, 2, 3 }, typeof(FakeMessage1));

    Assert.False(handled);
    _mockSerializer.Verify(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<Type>()), Times.Never);
}
```

- [ ] **Step 2: Add a failing `ReplyProcessor` test for unknown replies**

In `ReplyProcessorTests`, make the processor propagate a not-handled result when the request manager rejects the reply.

```csharp
[Fact]
public async Task ProcessAsync_WhenReplyManagerRejectsReply_ReturnsNotHandled()
{
    _mockReplyManager
        .Setup(r => r.ProcessReply("reply-123", It.IsAny<ReadOnlyMemory<byte>>(), typeof(TestReplyMsg)))
        .Returns(false);

    var processor = new ReplyProcessor(_mockReplyManager.Object);
    var headers = new Dictionary<string, object>
    {
        [HeaderKeys.ResponseMessageId] = "reply-123",
        [HeaderKeys.FullTypeName] = typeof(TestReplyMsg).AssemblyQualifiedName!
    };
    var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

    var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(TestReplyMsg), null, headers, envelope);

    Assert.Equal(ProcessResult.NotHandled, result);
}
```

- [ ] **Step 3: Run the focused tests to verify they fail**

Run: `rtk dotnet test "src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj" --filter "FullyQualifiedName~RequestReplyManagerTests.ProcessReply_ReturnsFalse_WhenMessageIdIsUnknown|FullyQualifiedName~ReplyProcessorTests.ProcessAsync_WhenReplyManagerRejectsReply_ReturnsNotHandled"`
Expected: FAIL because `ProcessReply` currently returns `void` and `ReplyProcessor` always returns `Handled`.

- [ ] **Step 4: Change the request/reply contract to return `bool`**

Update `IRequestReplyManager` and `RequestReplyManager` so `ProcessReply` returns `true` only when a pending request accepted the reply.

```csharp
public interface IRequestReplyManager
{
    bool ProcessReply(string messageId, ReadOnlyMemory<byte> messageBytes, Type type);
}
```

```csharp
public bool ProcessReply(string messageId, ReadOnlyMemory<byte> messageBytes, Type type)
{
    if (!Guid.TryParse(messageId, out var requestId) || !_pendingRequests.TryGetValue(requestId, out var state))
        return false;

    lock (state.SyncRoot)
    {
        try
        {
            if (!state.TryAcceptReply(out var completesRequest))
                return false;

            object reply = _serializer.Deserialize(messageBytes, state.ReplyType);
            // existing completion logic
            return true;
        }
        finally
        {
            state.EndReply();
        }
    }
}
```

- [ ] **Step 5: Make `ReplyProcessor` honor the returned status**

Update `ReplyProcessor.ProcessAsync` so it returns `Handled` only when `ProcessReply(...)` returns `true`.

```csharp
if (replyManager.ProcessReply(responseMessageId, messageBytes, messageType))
    return HandledTask;

return NotHandledTask;
```

- [ ] **Step 6: Run the focused tests to verify they pass**

Run: `rtk dotnet test "src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj" --filter "FullyQualifiedName~RequestReplyManagerTests.ProcessReply_ReturnsFalse_WhenMessageIdIsUnknown|FullyQualifiedName~ReplyProcessorTests.ProcessAsync_WhenReplyManagerRejectsReply_ReturnsNotHandled"`
Expected: PASS with both tests green.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Interfaces/IRequestReplyManager.cs src/ServiceConnect/Services/RequestReplyManager.cs src/ServiceConnect/Services/Processors/ReplyProcessor.cs src/ServiceConnect.UnitTests/Processors/ReplyProcessorTests.cs src/ServiceConnect.UnitTests/RequestReplyManagerTests.cs
git commit -m "fix: reject unknown request replies"
```

### Task 3: Run Before-Consume Filters Before Reply Handling

**Files:**
- Modify: `src/ServiceConnect/Services/MessageDispatcher.cs:58-86`
- Test: `src/ServiceConnect.UnitTests/MessageDispatcherTests.cs`

- [ ] **Step 1: Add a failing dispatcher test proving replies go through before-consume filters**

In `MessageDispatcherTests`, add a test that blocks a reply in `ExecuteBeforeConsumingFiltersAsync` and verifies the reply manager is not called.

```csharp
[Fact]
public async Task Dispatch_ResponseMessage_BlockedByBeforeConsumingFilter_DoesNotReachReplyManager()
{
    var replyId = Guid.NewGuid().ToString();
    var headers = MakeHeaders(responseMessageId: replyId);
    _mockFilterPipeline
        .Setup(f => f.ExecuteBeforeConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(true);

    var dispatcher = CreateDispatcher(new ServiceCollection().BuildServiceProvider());

    var result = await dispatcher.Dispatch(new byte[] { 1, 2, 3 }, "FakeMessage1", headers);

    Assert.True(result.Success);
    _mockReplyManager.Verify(r => r.ProcessReply(It.IsAny<string>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<Type>()), Times.Never);
}
```

- [ ] **Step 2: Add a failing dispatcher test for rejected replies becoming failures**

```csharp
[Fact]
public async Task Dispatch_ResponseMessage_WithUnknownReplyId_ReturnsFailure()
{
    var replyId = Guid.NewGuid().ToString();
    var headers = MakeHeaders(responseMessageId: replyId);
    _mockReplyManager
        .Setup(r => r.ProcessReply(replyId, It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
        .Returns(false);

    var dispatcher = CreateDispatcher(new ServiceCollection().BuildServiceProvider());

    var result = await dispatcher.Dispatch(new byte[] { 1, 2, 3 }, "FakeMessage1", headers);

    Assert.False(result.Success);
}
```

- [ ] **Step 3: Run the focused dispatcher tests to verify they fail**

Run: `rtk dotnet test "src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj" --filter "FullyQualifiedName~MessageDispatcherTests.Dispatch_ResponseMessage_BlockedByBeforeConsumingFilter_DoesNotReachReplyManager|FullyQualifiedName~MessageDispatcherTests.Dispatch_ResponseMessage_WithUnknownReplyId_ReturnsFailure"`
Expected: FAIL because reply processing currently runs before filters and unknown replies are still treated as success.

- [ ] **Step 4: Reorder reply processing inside `MessageDispatcher.Dispatch`**

Keep stream processors in the pre-deserialization loop, but move reply processing to after type resolution and `ExecuteBeforeConsumingFiltersAsync`.

```csharp
foreach (var proc in _processors)
{
    if (!proc.RunBeforeDeserialization || proc is ReplyProcessor)
        continue;

    var preResult = await proc.ProcessAsync(messageBytes, typeof(Message), null, headers, envelope, cancellationToken);
    if (preResult == ProcessResult.Handled)
        return new ConsumeEventResult { Success = true };
}

// resolve type, deserialize, run ExecuteBeforeConsumingFiltersAsync

var replyProcessor = _processors.OfType<ReplyProcessor>().FirstOrDefault();
if (replyProcessor != null)
{
    var replyResult = await replyProcessor.ProcessAsync(messageBytes, type, message, headers, envelope, cancellationToken);
    if (replyResult == ProcessResult.Handled)
        return new ConsumeEventResult { Success = true };

    if (headers.ContainsKey(HeaderKeys.ResponseMessageId))
        return new ConsumeEventResult { Success = false, Exception = new InvalidOperationException("Reply message did not match a pending request.") };
}
```

- [ ] **Step 5: Run the focused dispatcher tests to verify they pass**

Run: `rtk dotnet test "src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj" --filter "FullyQualifiedName~MessageDispatcherTests.Dispatch_ResponseMessage_BlockedByBeforeConsumingFilter_DoesNotReachReplyManager|FullyQualifiedName~MessageDispatcherTests.Dispatch_ResponseMessage_WithUnknownReplyId_ReturnsFailure|FullyQualifiedName~MessageDispatcherTests.Dispatch_ResponseMessage_RoutesToReplyManager"`
Expected: PASS with all three dispatcher tests green.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect/Services/MessageDispatcher.cs src/ServiceConnect.UnitTests/MessageDispatcherTests.cs
git commit -m "fix: run consume filters before reply handling"
```

### Task 4: Verify Request Ownership Before Allowing Context Replies

**Files:**
- Modify: `src/ServiceConnect/Services/RequestReplyManager.cs:10-12,278-372`
- Modify: `src/ServiceConnect/Services/ConsumeContext.cs:60-81`
- Modify: `src/ServiceConnect/Services/ConsumeContextPool.cs:94-116`
- Modify: `src/ServiceConnect.Interfaces/IRequestReplyManager.cs`
- Modify: `src/ServiceConnect/ServiceCollectionExtensions.cs` (only if constructor injection changes)
- Test: `src/ServiceConnect.UnitTests/ConsumeContextTests.cs`

- [ ] **Step 1: Add a failing `ConsumeContext` test for spoofed request headers**

In `ConsumeContextTests`, add a test proving that an arbitrary `RequestMessageId` does not bypass destination validation.

```csharp
[Fact]
public async Task ReplyAsync_ThrowsWhenRequestMessageIdIsPresentButNotTrusted()
{
    var headers = new Dictionary<string, object>
    {
        { HeaderKeys.SourceAddress, "unknown-evil-queue" },
        { HeaderKeys.RequestMessageId, Guid.NewGuid().ToString() }
    };

    var context = new ConsumeContext(_mockBus.Object, headers, _queueConfig, _busConfig, requestReplyManager: Mock.Of<IRequestReplyManager>());
    var reply = new ConsumeContextTestReply(Guid.NewGuid()) { Value = "hello" };

    await Assert.ThrowsAsync<InvalidOperationException>(() => context.ReplyAsync(reply));
}
```

- [ ] **Step 2: Add a passing-path test for trusted local requests**

```csharp
[Fact]
public async Task ReplyAsync_AllowsUnknownQueue_WhenRequestMessageIdIsTrusted()
{
    var requestId = Guid.NewGuid().ToString();
    var replyManager = new Mock<IRequestReplyManager>();
    replyManager.Setup(r => r.IsKnownRequest(requestId)).Returns(true);

    var headers = new Dictionary<string, object>
    {
        { HeaderKeys.SourceAddress, "unknown-but-local" },
        { HeaderKeys.RequestMessageId, requestId }
    };

    var context = new ConsumeContext(_mockBus.Object, headers, _queueConfig, _busConfig, requestReplyManager: replyManager.Object);
    var reply = new ConsumeContextTestReply(Guid.NewGuid()) { Value = "hello" };

    await context.ReplyAsync(reply);

    _mockBus.Verify(b => b.SendAsync(reply, It.Is<SendOptions?>(o => o.HasValue && o.Value.EndPoint == "unknown-but-local")), Times.Once);
}
```

- [ ] **Step 3: Run the focused `ConsumeContext` tests to verify they fail**

Run: `rtk dotnet test "src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj" --filter "FullyQualifiedName~ConsumeContextTests.ReplyAsync_ThrowsWhenRequestMessageIdIsPresentButNotTrusted|FullyQualifiedName~ConsumeContextTests.ReplyAsync_AllowsUnknownQueue_WhenRequestMessageIdIsTrusted"`
Expected: FAIL because `ConsumeContext` currently trusts any non-empty `RequestMessageId` and has no request manager dependency.

- [ ] **Step 4: Add an internal trust query to `IRequestReplyManager`**

Extend the interface with a small internal-style query method and implement it against `_pendingRequests`.

```csharp
public interface IRequestReplyManager
{
    bool IsKnownRequest(string messageId);
}
```

```csharp
public bool IsKnownRequest(string messageId)
{
    return Guid.TryParse(messageId, out var requestId)
        && _pendingRequests.ContainsKey(requestId);
}
```

- [ ] **Step 5: Thread the request manager into `ConsumeContext` and `ConsumeContextPool`**

Update the constructors and `Rent(...)`/`Initialize(...)` methods so both context implementations can query request ownership.

```csharp
public sealed class ConsumeContext(
    IBus bus,
    IDictionary<string, object> headers,
    IQueueConfiguration queueConfig,
    IBusConfiguration busConfig,
    IRequestReplyManager? requestReplyManager = null,
    CancellationToken cancellationToken = default) : IConsumeContext
```

```csharp
var isTrustedRequestReply = !string.IsNullOrEmpty(requestMessageId)
    && requestReplyManager?.IsKnownRequest(requestMessageId) == true;

if (busConfig.ValidateReplyDestinations && !isTrustedRequestReply && !IsKnownQueue(sourceAddress, queueConfig))
{
    throw new InvalidOperationException(...);
}
```

Make the same change in `ConsumeContextPool.PooledConsumeContext.ReplyAsync`.

- [ ] **Step 6: Update registrations if required**

If `HandlerProcessor` and `ProcessManagerProcessor` need an extra dependency to populate the context pool with the request manager, update `ServiceCollectionExtensions` and the processor constructors to pass `IRequestReplyManager` through cleanly.

```csharp
new HandlerProcessor(handlerRegistry, serviceProvider, new Lazy<IBus>(...), busConfig, queueConfig, sp.GetRequiredService<IRequestReplyManager>(), contextPool)
```

- [ ] **Step 7: Run the focused `ConsumeContext` tests to verify they pass**

Run: `rtk dotnet test "src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj" --filter "FullyQualifiedName~ConsumeContextTests.ReplyAsync_ThrowsWhenRequestMessageIdIsPresentButNotTrusted|FullyQualifiedName~ConsumeContextTests.ReplyAsync_AllowsUnknownQueue_WhenRequestMessageIdIsTrusted|FullyQualifiedName~ConsumeContextTests.ReplyAsync_ThrowsWhenSourceAddressNotKnown"`
Expected: PASS with all three tests green.

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect.Interfaces/IRequestReplyManager.cs src/ServiceConnect/Services/RequestReplyManager.cs src/ServiceConnect/Services/ConsumeContext.cs src/ServiceConnect/Services/ConsumeContextPool.cs src/ServiceConnect/ServiceCollectionExtensions.cs src/ServiceConnect.UnitTests/ConsumeContextTests.cs
git commit -m "fix: require trusted local requests for context replies"
```

### Task 5: Run The Full Transport Hardening Verification Suite

**Files:**
- No code changes expected

- [ ] **Step 1: Run the transport-focused unit tests**

Run: `rtk dotnet test "src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj" --filter "FullyQualifiedName~ConsumeContextTests|FullyQualifiedName~MessageDispatcherTests|FullyQualifiedName~ReplyProcessorTests|FullyQualifiedName~RequestReplyManagerTests"`
Expected: PASS with all targeted unit tests green.

- [ ] **Step 2: Run the request/reply end-to-end tests**

Run: `rtk dotnet test "src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj" --filter "FullyQualifiedName~RequestReplyTests|FullyQualifiedName~ConsumeContextReplyTests"`
Expected: PASS for all request/reply end-to-end coverage available in the current environment.

- [ ] **Step 3: Run the RabbitMQ project builds for both frameworks again**

Run: `rtk dotnet build "src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj" -f net8.0 && rtk dotnet build "src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj" -f net10.0`
Expected: PASS twice with no compile errors.

- [ ] **Step 4: Commit verification-only follow-up if needed**

If no files changed, do not create an empty commit.
