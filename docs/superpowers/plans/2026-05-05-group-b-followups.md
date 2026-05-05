# Group B Follow-ups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land three Group-B follow-ups as one PR / three commits: (1) `Producer`/`Bus` fan-out continue-on-failure with `AggregateException`; (2) `MessageRetryHandler` retry-attempt metric tag fix (consumer queue under `messaging.destination.name`, retry queue under `messaging.serviceconnect.retry.target`); (3) `RabbitMqConsumerHost` three-way collaborator split with test migration.

**Architecture:** Three logically-independent commits inside a single PR, ordered Item 2 → Item 1 → Item 3 so each commit is reviewable on its own and downstream tests assert against the corrected tag scheme. Item 1 changes a public exception contract (one paragraph in `IBus`/`IProducer` XML docs); Items 2 and 3 are internal-only.

**Tech Stack:** .NET 8/10, C# 14, RabbitMQ.Client 7.2.1, xUnit, Moq, `Microsoft.Extensions.Logging.Abstractions.NullLogger`, `System.Diagnostics.TagList`, custom `ServiceConnect.UnitTests.Diagnostics.MetricCollector`.

**Source spec:** [`docs/superpowers/specs/2026-05-05-group-b-followups-design.md`](../specs/2026-05-05-group-b-followups-design.md)

---

## File structure

| File | Item | Action |
|---|---|---|
| `src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs` | 2 | Modify — ctor adds `string consumerQueueName`; tag scheme updated |
| `src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs` | 2 | Modify — pass `_queueConfiguration.QueueName` to ctor at line 173-174 |
| `src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerMetricsTests.cs` | 2 | Modify — assert new tag scheme |
| `src/ServiceConnect.UnitTests/**/*MessageRetryHandler*.cs` (~70 sites) | 2 | Modify — mechanical 4-arg ctor migration |
| `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs` | 1 | Modify `SendAsync(Type)` (lines 225-294) — try/catch in loop + `AggregateException` |
| `src/ServiceConnect/Bus.cs` | 1 | Modify `SendToManyAsync<T>` (lines 173-223) — try/catch in loop + `AggregateException` |
| `src/ServiceConnect.Interfaces/Bus/IBus.cs` | 1 | Modify XML docs on `SendAsync<T>` and `SendToManyAsync<T>` |
| `src/ServiceConnect.Interfaces/IProducer.cs` (or wherever `SendAsync(Type, ...)` is declared) | 1 | Modify XML doc on `SendAsync(Type, ...)` |
| `src/ServiceConnect.UnitTests/RabbitMQ/ProducerSendAsyncFanoutTests.cs` | 1 | Create — partial-failure / cancellation matrix |
| `src/ServiceConnect.UnitTests/BusSendToManyAsyncFanoutTests.cs` (or similar) | 1 | Create — partial-failure / cancellation matrix at Bus layer |
| `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqAdmissionGate.cs` | 3 | Create — admission, in-flight bookkeeping, shutdown gate |
| `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqHeaderValidator.cs` | 3 | Create — header validation + reject-with-audit |
| `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqDispatchPipeline.cs` | 3 | Create — dispatch + ack/nack + consume-duration metric |
| `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` | 3 | Modify — slim to ~300 lines; orchestrate three collaborators |
| `src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs` | 3 | Modify — instantiate the three collaborators at line 173-183 |
| `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqAdmissionGateTests.cs` | 3 | Create — migrated from `RabbitMqConsumerHostInflightCounterTests` |
| `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqHeaderValidatorTests.cs` | 3 | Create — migrated from `RabbitMqConsumerHostHeaderSizeTests` |
| `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqDispatchPipelineTests.cs` | 3 | Create — migrated from `RabbitMqConsumerHostAckNackTests` |
| `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostTests.cs` | 3 | Modify — drop logic-level tests that moved out |
| `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostInflightCounterTests.cs` | 3 | Delete |
| `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostHeaderSizeTests.cs` | 3 | Delete |
| `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostAckNackTests.cs` | 3 | Delete |

---

## Phase A — Item 2: Retry-attempt metric tag fix (Commit 1)

Lands first because the change is internal-only with the smallest blast radius. The new tag scheme also benefits Item 3's dispatch tests, which assert on consumer-side metrics and would otherwise need a follow-up update.

### Task A.1: Add `consumerQueueName` ctor parameter to `MessageRetryHandler`; update tag scheme

**Files:**
- Modify: [src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs)
- Test: [src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerMetricsTests.cs](../../../src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerMetricsTests.cs)

- [ ] **Step 1: Replace existing metric assertions in `MessageRetryHandlerMetricsTests` with the new tag scheme**

The existing two facts (`HandleFailureAsync_RetryPath_IncrementsRetryAttempts` and `HandleFailureAsync_MaxRetriesExceeded_DoesNotIncrementRetryAttempts`) currently filter on `messaging.destination.name = retryQueueName`. Update them so the filter and assertion target `consumerQueueName`, and add an assertion for the new namespaced tag.

```csharp
[Fact]
public async Task HandleFailureAsync_RetryPath_IncrementsRetryAttempts()
{
    var consumerQueueName = $"q-consumer-{Guid.NewGuid():N}";
    var retryQueueName = $"{consumerQueueName}.Retries";
    using var collector = new MetricCollector("messaging.destination.name", consumerQueueName);

    var channel = new Mock<IChannel>();
    channel
        .Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
        .Returns(ValueTask.CompletedTask);

    var handler = new MessageRetryHandler(maxRetries: 3, errorExchange: "err", consumerQueueName, NullLogger.Instance);
    var headers = new Dictionary<string, object>(StringComparer.Ordinal);

    await handler.HandleFailureAsync(channel.Object, retryQueueName, MakeArgs(), headers, ex: null);

    var record = Assert.Single(collector.GetLongRecords(MetricNames.RetryAttempts));
    Assert.Equal(1, record.Value);
    Assert.Equal("rabbitmq", record.GetTag("messaging.system"));
    Assert.Equal(consumerQueueName, record.GetTag("messaging.destination.name"));
    Assert.Equal(retryQueueName, record.GetTag("messaging.serviceconnect.retry.target"));
}
```

For the `MaxRetriesExceeded` fact: change the filter and the ctor argument list the same way (`consumerQueueName` filter, 4-arg ctor); the assertion `Assert.Empty(...)` body stays.

- [ ] **Step 2: Run the tests to confirm they fail**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~MessageRetryHandlerMetricsTests" \
    --no-restore
```

Expected: compilation error (4-arg ctor doesn't exist yet) OR test failure (new tag not present).

- [ ] **Step 3: Add `consumerQueueName` to `MessageRetryHandler` ctor and emit the new tag**

Update the primary constructor at [MessageRetryHandler.cs:17](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs#L17):

```csharp
internal sealed class MessageRetryHandler(
    int maxRetries,
    string errorExchange,
    string consumerQueueName,
    ILogger logger,
    TimeProvider? timeProvider = null)
{
    private readonly int _maxRetries = maxRetries;
    private readonly string _errorExchange = errorExchange ?? throw new ArgumentNullException(nameof(errorExchange));
    private readonly string _consumerQueueName = consumerQueueName ?? throw new ArgumentNullException(nameof(consumerQueueName));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
```

Update the metric emit at [MessageRetryHandler.cs:71-75](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs#L71-L75):

```csharp
ServiceConnectMeter.AddRetryAttempt(new TagList
{
    { "messaging.system", "rabbitmq" },
    { "messaging.destination.name", _consumerQueueName },
    { "messaging.serviceconnect.retry.target", retryQueueName },
});
```

Update the comment immediately above the emit (around line 68-70) to describe the new scheme:

```csharp
// Emitted at the increment site so a counter delta corresponds 1:1 with a retry-queue
// republish, regardless of whether the subsequent BasicPublishAsync ultimately succeeds.
// messaging.destination.name carries the consumer queue (operator filter key);
// messaging.serviceconnect.retry.target carries the per-message retry-queue destination.
```

- [ ] **Step 4: Run the targeted tests — compilation and the two metric facts must compile but the rest of the test suite will be broken**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~MessageRetryHandlerMetricsTests" \
    --no-restore
```

Expected: the two facts in `MessageRetryHandlerMetricsTests` PASS. The rest of the unit-test project will not compile yet (other tests still construct `MessageRetryHandler` with 3 args). That's expected — fixed in Task A.2.

- [ ] **Step 5: Do not commit yet** — Task A.2 fixes the rest of the test project; commit at the end of A.3.

### Task A.2: Mechanical migration of all `new MessageRetryHandler(...)` test sites

**Files (all under `src/ServiceConnect.UnitTests/`):**

The following test files construct `MessageRetryHandler` and need a queue-name argument inserted as the 3rd positional argument (before the logger):

```
src/ServiceConnect.UnitTests/MessageRetryHandlerTests.cs
src/ServiceConnect.UnitTests/RabbitMQ/AckNackFailureLogsTests.cs
src/ServiceConnect.UnitTests/RabbitMQ/ConsumerProcessMetricsTests.cs
src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorAuditCancellationTests.cs
src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorMetricsTests.cs
src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorNotHandledFallbackTests.cs
src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorNullHandlerTests.cs
src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorTransportTests.cs
src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerCopyPropsTests.cs
src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerMandatoryTests.cs
src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerRetryCountValidationTests.cs
src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostAckNackTests.cs
src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostBrokerCancelTests.cs
src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostConnectionEventCaptureTests.cs
src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostConsumeMessageTypeTests.cs
src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostHeaderSizeTests.cs
src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostInflightCounterTests.cs
src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostMessageProcessorNullTests.cs
src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostRecoveryTests.cs
src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostTests.cs
```

- [ ] **Step 1: Run a fresh listing of all construction sites to confirm coverage**

```bash
grep -rn "new MessageRetryHandler" src/ServiceConnect.UnitTests/ --include="*.cs"
```

This is the authoritative list. Cross-reference against the file list above and add any newly-introduced site to the migration set.

- [ ] **Step 2: Apply the migration site-by-site**

For each construction site, insert a queue-name argument in the 3rd position. Two patterns occur:

**Pattern 1 — short positional form** (most sites):

```csharp
// before
new MessageRetryHandler(3, "err", NullLogger.Instance)

// after
new MessageRetryHandler(3, "err", "test.consumer.queue", NullLogger.Instance)
```

**Pattern 2 — named-argument form** (a handful):

```csharp
// before
new MessageRetryHandler(maxRetries: 3, errorExchange: "error", NullLogger.Instance)

// after
new MessageRetryHandler(maxRetries: 3, errorExchange: "error", consumerQueueName: "test.consumer.queue", NullLogger.Instance)
```

If a test fixture already has a consumer queue identity (e.g. `_queueConfiguration.QueueName` from a Mock setup, or a `const string QueueName = "..."`), pass *that* identity instead of `"test.consumer.queue"` — preserves the test's intent.

- [ ] **Step 3: Run the entire unit-test project**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-restore
```

Expected: compilation succeeds; all tests pass except for any `RabbitMqConsumerHost*` tests that wire end-to-end with a `MessageRetryHandler` whose new queue-name arg conflicts with a queue-name fixture elsewhere — fix per-test on a case-by-case basis.

- [ ] **Step 4: Do not commit yet** — wait for Task A.3.

### Task A.3: Wire `_queueConfiguration.QueueName` into `Consumer.cs` ctor; commit

**Files:**
- Modify: [src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs:173-174](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs#L173-L174)

- [ ] **Step 1: Update the production construction site**

```csharp
var retryHandler = new MessageRetryHandler(
    _transportConfiguration.MaxRetries,
    _queueConfiguration.ErrorQueueName,
    _queueConfiguration.QueueName,
    _logger);
```

- [ ] **Step 2: Build the production project to confirm**

```bash
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj --no-restore
```

Expected: build succeeds.

- [ ] **Step 3: Run the full test suite for the RabbitMQ assembly + unit tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-restore
```

Expected: all tests pass.

- [ ] **Step 4: Commit Item 2**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs \
        src/ServiceConnect.UnitTests/

git commit -m "$(cat <<'EOF'
feat(transport): retry-attempt metric tags consumer queue, not retry queue

The retry-attempt counter now carries messaging.destination.name =
<consumerQueue> (the operator's filter key) and messaging.serviceconnect.retry.target =
<consumerQueue>.Retries (the per-message retry destination). Operators querying
"how often is queue X retrying?" now filter on the queue they care about.

MessageRetryHandler gains a consumerQueueName ctor parameter; threaded through
from Consumer.cs via _queueConfiguration.QueueName. Tag cardinality is unchanged
(retry-queue identity is determined by consumer-queue identity).
EOF
)"
```

---

## Phase B — Item 1: Producer / Bus fan-out continue-on-failure (Commit 2)

Switches `Producer.SendAsync(Type)` and `Bus.SendToManyAsync<T>` from fail-fast to continue-on-failure with `AggregateException` collection. Cancellation propagates as `OperationCanceledException` directly, never wrapped.

### Task B.1: Failing tests for `Producer.SendAsync(Type)` partial-failure matrix

**Files:**
- Create: [src/ServiceConnect.UnitTests/RabbitMQ/ProducerSendAsyncFanoutTests.cs](../../../src/ServiceConnect.UnitTests/RabbitMQ/ProducerSendAsyncFanoutTests.cs)

- [ ] **Step 1: Locate an existing producer-test scaffold to reuse the test setup**

```bash
sed -n '1,80p' src/ServiceConnect.UnitTests/RabbitMQ/ProducerMultiEndpointSendTests.cs
```

This file already constructs a `Producer` with mocks for the channel, connection, and queue mapping. Match its setup style (helper methods for building a `Producer`, mock `IChannel.BasicPublishAsync` setups).

- [ ] **Step 2: Write the failing tests**

Cover four scenarios — match the existing `ProducerMultiEndpointSendTests` style (`Mock<IChannel>`, queue-mapping with multiple endpoints):

```csharp
[Fact]
public async Task SendAsync_AllEndpointsSucceed_NoException()
{
    // arrange: queue mapping {typeof(TestMessage)} -> ["q1", "q2", "q3"], all BasicPublishAsync setups return ValueTask.CompletedTask
    // act
    await producer.SendAsync(typeof(TestMessage), body, headers, CancellationToken.None);
    // assert: BasicPublishAsync called 3 times; no exception
}

[Fact]
public async Task SendAsync_SingleEndpointFails_AggregateExceptionWithOneInner_OtherEndpointsAttempted()
{
    // arrange: q1 publish setup throws BrokerUnreachableException; q2 + q3 succeed
    // act
    var ex = await Assert.ThrowsAsync<AggregateException>(
        () => producer.SendAsync(typeof(TestMessage), body, headers, CancellationToken.None));
    // assert
    Assert.Single(ex.InnerExceptions);
    Assert.IsType<BrokerUnreachableException>(ex.InnerExceptions[0]);
    // verify q2 and q3 BasicPublishAsync were each called exactly once
}

[Fact]
public async Task SendAsync_AllEndpointsFail_AggregateExceptionWithAllInners()
{
    // arrange: q1, q2, q3 publish setups all throw distinct exceptions
    // act
    var ex = await Assert.ThrowsAsync<AggregateException>(
        () => producer.SendAsync(typeof(TestMessage), body, headers, CancellationToken.None));
    // assert: ex.InnerExceptions.Count == 3
}

[Fact]
public async Task SendAsync_CancellationMidLoop_ThrowsOperationCanceledDirectly()
{
    // arrange: cts cancels after the first BasicPublishAsync setup completes
    using var cts = new CancellationTokenSource();
    // q1 setup: cancels cts and returns ValueTask.CompletedTask
    // q2, q3 setups: should not be reached
    // act + assert: OperationCanceledException thrown directly (not nested in AggregateException)
    await Assert.ThrowsAsync<OperationCanceledException>(
        () => producer.SendAsync(typeof(TestMessage), body, headers, cts.Token));
}
```

- [ ] **Step 3: Run the tests to confirm they fail**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~ProducerSendAsyncFanoutTests" \
    --no-restore
```

Expected: the `SingleEndpointFails` and `AllEndpointsFail` tests FAIL because the current loop fail-fast throws `BrokerUnreachableException` (not `AggregateException`) and stops iterating after q1.

### Task B.2: Implement continue-on-failure in `Producer.SendAsync(Type)`

**Files:**
- Modify: [src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs:225-294](../../../src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L225-L294)

- [ ] **Step 1: Wrap each iteration with try/catch + collect**

Replace the existing `foreach (string endPoint in endPoints)` body. The shape:

```csharp
List<Exception>? endpointFailures = null;
foreach (string endPoint in endPoints)
{
    baseHeaders[HeaderKeys.DestinationAddress] = endPoint;
    baseHeaders[HeaderKeys.MessageId] = Guid.NewGuid().ToString();
    baseHeaders[HeaderKeys.TimeSent] = OutboundHeaderBuilder.FormatTimestamp(_timeProvider.GetUtcNow().UtcDateTime);
    var basicProperties = _headerBuilder.BuildBasicProperties(baseHeaders);

    var endpointStart = Stopwatch.GetTimestamp();
    bool endpointSucceeded = false;
    Exception? endpointFailure = null;
    try
    {
        await ExecuteWithConnectionRetryAsync(
            () => PublishWithTimeoutAsync(
                _producerConnection.Channel,
                string.Empty,
                endPoint,
                false,
                basicProperties,
                body,
                cancellationToken).AsTask(),
            cancellationToken).ConfigureAwait(false);
        endpointSucceeded = true;
    }
    catch (OperationCanceledException)
    {
        // Cancellation propagates directly — never wrapped, never aggregated.
        EmitPublishMetrics(endpointStart, endPoint, endpointSucceeded: false, endpointFailure: null);
        throw;
    }
    catch (Exception ex)
    {
        endpointFailure = ex;
        (endpointFailures ??= []).Add(ex);
    }
    finally
    {
        if (endpointFailure is not OperationCanceledException)
        {
            EmitPublishMetrics(endpointStart, endPoint, endpointSucceeded, endpointFailure);
        }
    }
}

if (endpointFailures is { Count: > 0 })
{
    throw new AggregateException(
        $"One or more endpoints failed during fan-out send for message type '{type.FullName}'.",
        endpointFailures);
}
```

Notes:
- The `OperationCanceledException` catch re-throws *inside* the `try`, so the `finally` still emits a per-endpoint metric for the cancelled endpoint (no metric is left dangling). Subsequent endpoints are not attempted — that matches the cancellation contract.
- `EmitPublishMetrics` is called per iteration, same as today.
- The publish lock (`_publishLock`) is held across the entire loop, same as today.

- [ ] **Step 2: Update the XML doc on the method to describe fan-out failure semantics**

Append a paragraph to the `<summary>` for [Producer.cs:225](../../../src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L225):

```csharp
/// <remarks>
/// When the message type maps to multiple queues, every endpoint is attempted; per-endpoint
/// failures are collected and surface as an <see cref="AggregateException"/> at the end of
/// the loop. Cancellation via <paramref name="cancellationToken"/> propagates as
/// <see cref="OperationCanceledException"/> directly and aborts the remaining iterations.
/// </remarks>
```

- [ ] **Step 3: Run the new tests to confirm they pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~ProducerSendAsyncFanoutTests" \
    --no-restore
```

Expected: all four tests PASS.

- [ ] **Step 4: Run the existing producer test suite to confirm no regression**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~Producer" \
    --no-restore
```

Expected: all tests pass. If `ProducerMultiEndpointSendTests` had a test asserting the *old* fail-fast behaviour, update it to assert `AggregateException` and document the change in the test name.

### Task B.3: Failing tests + implementation for `Bus.SendToManyAsync<T>`

**Files:**
- Create: `src/ServiceConnect.UnitTests/BusSendToManyAsyncFanoutTests.cs` (or extend an existing `Bus*Tests` file if one already covers `SendToManyAsync`)
- Modify: [src/ServiceConnect/Bus.cs:203-222](../../../src/ServiceConnect/Bus.cs#L203-L222)

- [ ] **Step 1: Locate the existing Bus test scaffold**

```bash
grep -rln "class Bus.*Tests\|SendToManyAsync" src/ServiceConnect.UnitTests/ | head
```

If a `Bus*SendToManyAsync*Tests` file exists, extend it. Otherwise create a new file matching the existing Bus-test pattern.

- [ ] **Step 2: Write failing tests at the Bus layer**

Mirror the four Producer-level scenarios but at the `IBus` surface, with at least one test that includes a registered `ISendMessageMiddleware` to exercise the per-iteration header-copy invariant (the shallow copy at [Bus.cs:209](../../../src/ServiceConnect/Bus.cs#L209)):

```csharp
[Fact]
public async Task SendToManyAsync_PartialFailure_AggregatesAndAttemptsRemaining()
{
    // arrange: pipeline configured so endpoint "q1" throws, "q2" + "q3" succeed
    // act
    var ex = await Assert.ThrowsAsync<AggregateException>(
        () => bus.SendToManyAsync(message, ["q1", "q2", "q3"]));
    // assert: ex.InnerExceptions.Count == 1; pipeline was invoked 3 times (one per endpoint)
}

[Fact]
public async Task SendToManyAsync_CancellationMidLoop_ThrowsOperationCanceledDirectly()
{
    // arrange + act + assert: OperationCanceledException directly, NOT nested in AggregateException
}

[Fact]
public async Task SendToManyAsync_PartialFailure_HeaderCopyIsolatedPerIteration()
{
    // arrange: middleware that mutates ctx.Headers; first endpoint throws
    // assert: second endpoint's ctx.Headers does NOT carry first endpoint's mutations
}

[Fact]
public async Task SendToManyAsync_AllSucceed_NoException()
{
    // baseline
}
```

- [ ] **Step 3: Run tests, confirm failure**

Expected: at least the partial-failure tests fail (current behaviour fails-fast on first endpoint).

- [ ] **Step 4: Update `Bus.SendToManyAsync<T>` to continue-on-failure**

Replace the `foreach` body:

```csharp
List<Exception>? endpointFailures = null;
foreach (var endpoint in endPoints)
{
    var perEndpointHeaders = new Dictionary<string, string>(headers, StringComparer.Ordinal);

    var context = new SendContext
    {
        Message = message,
        MessageType = typeof(T),
        MessageBytes = messageBytes,
        Headers = perEndpointHeaders,
        EndPoint = endpoint,
        RoutingKey = null,
        Operation = SendOperation.Send,
    };
    try
    {
        await _sendPipeline.ExecuteSendMessagePipelineAsync(context, cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex)
    {
        (endpointFailures ??= []).Add(ex);
    }
}

if (endpointFailures is { Count: > 0 })
{
    throw new AggregateException(
        $"One or more endpoints failed during SendToManyAsync of message type '{typeof(T).FullName}'.",
        endpointFailures);
}
```

- [ ] **Step 5: Run tests, confirm all pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~SendToManyAsync" \
    --no-restore
```

### Task B.4: Update XML docs on `IBus` and `IProducer`; commit

**Files:**
- Modify: [src/ServiceConnect.Interfaces/Bus/IBus.cs:36-47](../../../src/ServiceConnect.Interfaces/Bus/IBus.cs#L36-L47)
- Modify: [src/ServiceConnect.Interfaces/IProducer.cs](../../../src/ServiceConnect.Interfaces/IProducer.cs) (the `SendAsync(Type, ...)` declaration)

- [ ] **Step 1: Extend `IBus.SendAsync<T>` XML doc with fan-out paragraph**

Replace the current `<summary>` at [IBus.cs:36-39](../../../src/ServiceConnect.Interfaces/Bus/IBus.cs#L36-L39):

```csharp
/// <summary>
/// Sends a message to a specific endpoint or to the configured queue mapping.
/// </summary>
/// <remarks>
/// When the message type maps to multiple queues (queue-mapping fan-out), every endpoint is
/// attempted; per-endpoint failures are collected and surface as an
/// <see cref="AggregateException"/>. Cancellation via <paramref name="cancellationToken"/>
/// propagates as <see cref="OperationCanceledException"/> directly.
/// </remarks>
Task SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message;
```

- [ ] **Step 2: Tighten `IBus.SendToManyAsync<T>` XML doc to specify `AggregateException`**

Replace at [IBus.cs:41-47](../../../src/ServiceConnect.Interfaces/Bus/IBus.cs#L41-L47):

```csharp
/// <summary>
/// Sends a message to each of the specified endpoints. Each delivery is dispatched as a
/// separate <see cref="SendAsync{T}"/>-equivalent call; failures on one endpoint do not
/// abort the others — per-endpoint failures are collected and surface as an
/// <see cref="AggregateException"/> at the end of the loop. Cancellation via
/// <paramref name="cancellationToken"/> propagates as
/// <see cref="OperationCanceledException"/> directly. The <c>options.EndPoint</c> field is
/// ignored when this method is called — the explicit <paramref name="endPoints"/> parameter
/// wins.
/// </summary>
Task SendToManyAsync<T>(T message, IReadOnlyList<string> endPoints, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message;
```

- [ ] **Step 3: Update `IProducer.SendAsync(Type, ...)` XML doc with the same paragraph**

Locate the declaration:

```bash
grep -n "Task SendAsync(Type" src/ServiceConnect.Interfaces/IProducer.cs
```

Replace its `<summary>` with the same fan-out paragraph as `IBus.SendAsync<T>`.

- [ ] **Step 4: Build solution, verify XML doc compiles cleanly**

```bash
dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj --no-restore
dotnet build src/ServiceConnect/ServiceConnect.csproj --no-restore
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj --no-restore
```

Expected: clean build, no `CS1574` (unresolved cref) warnings.

- [ ] **Step 5: Run the full test suite once**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-restore
```

Expected: all green.

- [ ] **Step 6: Commit Item 1**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs \
        src/ServiceConnect/Bus.cs \
        src/ServiceConnect.Interfaces/Bus/IBus.cs \
        src/ServiceConnect.Interfaces/IProducer.cs \
        src/ServiceConnect.UnitTests/

git commit -m "$(cat <<'EOF'
feat(transport): fan-out send continues past per-endpoint failure

Producer.SendAsync(Type) and Bus.SendToManyAsync<T> previously aborted the
per-endpoint foreach on the first failure, contradicting the IBus.SendToManyAsync
docstring promise that "failures on one endpoint do not abort the others" and
silently dropping deliveries to later endpoints when the queue mapping resolved
to multiple queues.

Both layers now wrap each iteration in try/catch and collect non-cancellation
exceptions; the loop completes and partial/total failures surface as
AggregateException with one InnerException per failed endpoint. Cancellation
propagates as OperationCanceledException directly — never wrapped, never
aggregated.

XML docs on IBus.SendAsync<T>, IBus.SendToManyAsync<T>, and IProducer.SendAsync(Type)
specify the new exception contract.
EOF
)"
```

---

## Phase C — Item 3: RabbitMqConsumerHost three-way collaborator split (Commit 3)

Three collaborators extracted from [RabbitMqConsumerHost.cs](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs); the host shrinks to ~300 lines of orchestration. **No behavioural change** — every observable AMQP frame, metric tag set, and shutdown semantic must be identical. Tests migrate with the logic.

> **Critical constraint for the implementer:** the existing `EventAsync` ([RabbitMqConsumerHost.cs:265-449](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L265-L449)) interlocks admission, header validation, dispatch, ack/nack, and the in-flight metric across nested `try/catch/finally` blocks coordinated via `_callbackAdmissionGate`, `_messagesBeingProcessed`, `_shutdownStarted`, `_shutdownTimedOut`, and `GetShutdownPublishToken()`. Read the full method and `DisposeAsync` together before extracting; preserve the exact lock acquisition order and the gauge-balance invariant (every `+1` on `messaging.serviceconnect.in_flight` is paired with one `-1` carrying identical tags). The collaborator surfaces below are the *target shape* — adapt the exact method bodies as needed to honour these interlocks.

### Task C.1: Extract `RabbitMqAdmissionGate` skeleton with TDD

**Files:**
- Create: [src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqAdmissionGate.cs](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqAdmissionGate.cs)
- Create: [src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqAdmissionGateTests.cs](../../../src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqAdmissionGateTests.cs)

- [ ] **Step 1: Write the failing tests for the gate's public surface**

```csharp
public sealed class RabbitMqAdmissionGateTests
{
    [Fact]
    public void TryAdmit_BeforeShutdown_ReturnsTrueAndIncrementsInFlight()
    {
        var gate = new RabbitMqAdmissionGate("test.consumer.queue");
        Assert.True(gate.TryAdmit());
        // assert in-flight metric emitted with delta=+1 and tags
        //   { messaging.system=rabbitmq, messaging.destination.name=test.consumer.queue }
    }

    [Fact]
    public void Release_PairedWithAdmit_DecrementsInFlight()
    {
        // admit then release; assert -1 emitted with identical tags
    }

    [Fact]
    public async Task DrainAsync_Completes_When_AllAdmittedDeliveriesReleased()
    {
        // admit two; assert drain task is incomplete; release both; assert drain completes
    }

    [Fact]
    public async Task TryAdmit_AfterShutdownBegins_ReturnsFalse()
    {
        var gate = new RabbitMqAdmissionGate("test.consumer.queue");
        gate.BeginShutdown();
        Assert.False(gate.TryAdmit());
    }
}
```

- [ ] **Step 2: Run tests, confirm compile failure (file doesn't exist)**

- [ ] **Step 3: Create `RabbitMqAdmissionGate` with the target surface**

```csharp
namespace ServiceConnect.Client.RabbitMQ;

internal sealed class RabbitMqAdmissionGate
{
    private readonly object _lock = new();
    private readonly string _consumerQueueName;
    private int _inFlight;
    private bool _shutdownStarted;
    private TaskCompletionSource? _drainTcs;

    public RabbitMqAdmissionGate(string consumerQueueName)
    {
        _consumerQueueName = consumerQueueName ?? throw new ArgumentNullException(nameof(consumerQueueName));
    }

    public TagList CurrentInFlightTags => new()
    {
        { "messaging.system", "rabbitmq" },
        { "messaging.destination.name", _consumerQueueName },
    };

    public bool TryAdmit()
    {
        TagList tags;
        lock (_lock)
        {
            if (_shutdownStarted) return false;
            Interlocked.Increment(ref _inFlight);
            tags = CurrentInFlightTags;
        }
        ServiceConnectMeter.AddInFlight(1, tags);
        return true;
    }

    public void Release()
    {
        TagList tags;
        TaskCompletionSource? drainTcs;
        lock (_lock)
        {
            tags = CurrentInFlightTags;
            int remaining = Interlocked.Decrement(ref _inFlight);
            drainTcs = (_shutdownStarted && remaining == 0) ? _drainTcs : null;
        }
        ServiceConnectMeter.AddInFlight(-1, tags);
        drainTcs?.TrySetResult();
    }

    public void BeginShutdown()
    {
        lock (_lock) { _shutdownStarted = true; }
    }

    public Task DrainAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_inFlight == 0) return Task.CompletedTask;
            _drainTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drainTcs.Task.WaitAsync(ct);
        }
    }
}
```

> **Implementation note:** the host's existing shutdown-coordination semantics (`_shutdownTimedOut`, the grace window, `_callbackAdmissionGate` lock object) need to be preserved end-to-end. The above is the gate's *narrow* contract; the host still drives shutdown timing from `DisposeAsync`. If the existing `DisposeAsync` reads/writes `_shutdownStarted` directly, route those reads/writes through `BeginShutdown` / a `IsShuttingDown` query on the gate.

- [ ] **Step 4: Run gate tests, confirm pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~RabbitMqAdmissionGateTests" \
    --no-restore
```

### Task C.2: Wire `RabbitMqAdmissionGate` into the host

**Files:**
- Modify: [src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs)
- Modify: [src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs:176-183](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs#L176-L183)

- [ ] **Step 1: Inject the gate into `RabbitMqConsumerHost` ctor**

Update the host's primary constructor to take `RabbitMqAdmissionGate admissionGate` as a new parameter (after `MessageRetryHandler`). Replace direct uses of `_messagesBeingProcessed`, `_callbackAdmissionGate` (lock object), `_shutdownStarted`, the in-flight counter increments/decrements, and the `BuildInFlightTags` private method with calls into the gate.

In `EventAsync`:

```csharp
private async Task EventAsync(object _, BasicDeliverEventArgs args, CancellationToken cancellationToken)
{
    var model = _model;
    var publishChannel = _publishChannel;
    bool processed = false;

    if (!_admissionGate.TryAdmit())
    {
        return;
    }

    try
    {
        // ... existing header validation + dispatch logic (unchanged for now; refactored in C.4 / C.7)
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error processing message");
    }
    finally
    {
        try
        {
            // ... existing ack/nack block — unchanged
        }
        finally
        {
            _admissionGate.Release();
        }
    }
}
```

Delete the host's `BuildInFlightTags` method. Delete the `_messagesBeingProcessed` int field, `_callbackAdmissionGate` object field (or rename to `_admissionGate` / route through the gate), and the `_shutdownStarted` field if it's no longer read directly from the host (else expose `IsShuttingDown` on the gate).

- [ ] **Step 2: Update `Consumer.cs:173-183` to instantiate the gate and pass it**

```csharp
var retryHandler = new MessageRetryHandler(
    _transportConfiguration.MaxRetries,
    _queueConfiguration.ErrorQueueName,
    _queueConfiguration.QueueName,
    _logger);
var auditPublisher = new MessageAuditPublisher(_queueConfiguration);
var admissionGate = new RabbitMqAdmissionGate(_queueConfiguration.QueueName);
RabbitMqConsumerHost client = new(
    _connection,
    _transportConfiguration,
    _queueConfiguration,
    _busConfiguration,
    retryHandler,
    auditPublisher,
    admissionGate,
    _logger);
```

- [ ] **Step 3: Update `DisposeAsync` to use the gate's `BeginShutdown` + `DrainAsync`**

Wherever `DisposeAsync` currently sets `_shutdownStarted = true` under `_callbackAdmissionGate`, call `_admissionGate.BeginShutdown()` instead. Wherever it waits for `_messagesBeingProcessed == 0` (likely a spin-wait or a `TaskCompletionSource`), call `await _admissionGate.DrainAsync(ct).ConfigureAwait(false)`.

- [ ] **Step 4: Run the full test suite**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-restore
```

Expected: all tests pass. The `RabbitMqConsumerHostInflightCounterTests` continue to assert the same observable metric tags through the host's external surface.

### Task C.3: Migrate `RabbitMqConsumerHostInflightCounterTests` → `RabbitMqAdmissionGateTests`

**Files:**
- Delete: [src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostInflightCounterTests.cs](../../../src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostInflightCounterTests.cs)
- Modify: [src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqAdmissionGateTests.cs](../../../src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqAdmissionGateTests.cs)

- [ ] **Step 1: Read the existing inflight tests and classify each**

For each `[Fact]` in `RabbitMqConsumerHostInflightCounterTests`, classify as:

- **Pure-gate** (admission-counter increment/decrement, shutdown rejection, tag content): move to `RabbitMqAdmissionGateTests` and rewrite to use `RabbitMqAdmissionGate` directly instead of constructing a host.
- **Wiring** (host's `EventAsync` admits, processes, releases — end-to-end): leave in the host's test file (`RabbitMqConsumerHostTests`) as integration coverage.

- [ ] **Step 2: Migrate pure-gate tests**

Each migrated test keeps its assertions verbatim; only `Arrange` (new `RabbitMqAdmissionGate("queue")` instead of `new RabbitMqConsumerHost(...)`) and `Act` (`gate.TryAdmit()` instead of raising a fake delivery on the host) change.

- [ ] **Step 3: Delete `RabbitMqConsumerHostInflightCounterTests.cs` if every test has been migrated or merged**

If a few tests are wiring-level, copy them into `RabbitMqConsumerHostTests.cs` first under a `// inflight-wiring` region, then delete the file.

- [ ] **Step 4: Run tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-restore
```

### Task C.4: Extract `RabbitMqHeaderValidator` skeleton with TDD

**Files:**
- Create: [src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqHeaderValidator.cs](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqHeaderValidator.cs)
- Create: [src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqHeaderValidatorTests.cs](../../../src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqHeaderValidatorTests.cs)

- [ ] **Step 1: Write failing tests covering the four validation rules in the existing host**

The host today rejects four conditions before dispatch (read [RabbitMqConsumerHost.cs:300-358](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L300-L358) for the canonical list):

1. `BasicProperties.Headers == null` or no non-null `TypeName`/`FullTypeName`.
2. `args.Body.Length > _maxInboundMessageSize`.
3. `inboundHeaders.Count > DefaultMaxHeaderCount`.
4. Oversized individual header (currently `TryRejectOversizedHeaderAsync`).

Each rejection routes through `_retryHandler.HandleTerminalFailureAsync` (rules 1-3) or `BasicReject` + audit publish + drop metric (rule 4). Write one `[Fact]` per rule asserting:

- The validator returns `Rejected(reason)`.
- The terminal-failure / audit-with-drop side-effect is invoked exactly once.
- The accepted path returns `Accepted` with no side effects.

- [ ] **Step 2: Run tests, confirm compile failure**

- [ ] **Step 3: Create `RabbitMqHeaderValidator` with the target surface**

```csharp
internal readonly record struct HeaderValidationResult(bool Accepted, string? RejectReason)
{
    public static HeaderValidationResult Accept() => new(true, null);
    public static HeaderValidationResult Reject(string reason) => new(false, reason);
}

internal sealed class RabbitMqHeaderValidator
{
    private readonly MessageRetryHandler _retryHandler;
    private readonly MessageAuditPublisher _auditPublisher;
    private readonly int _maxInboundMessageSize;
    private readonly int _maxHeaderCount;
    private readonly int _maxHeaderValueBytes;
    private readonly string _consumerQueueName;
    private readonly Func<CancellationToken> _shutdownPublishTokenFactory;
    private readonly ILogger _logger;

    public RabbitMqHeaderValidator(
        MessageRetryHandler retryHandler,
        MessageAuditPublisher auditPublisher,
        int maxInboundMessageSize,
        int maxHeaderCount,
        int maxHeaderValueBytes,
        string consumerQueueName,
        Func<CancellationToken> shutdownPublishTokenFactory,
        ILogger logger)
    {
        // null-checks omitted for brevity
        _retryHandler = retryHandler;
        _auditPublisher = auditPublisher;
        _maxInboundMessageSize = maxInboundMessageSize;
        _maxHeaderCount = maxHeaderCount;
        _maxHeaderValueBytes = maxHeaderValueBytes;
        _consumerQueueName = consumerQueueName;
        _shutdownPublishTokenFactory = shutdownPublishTokenFactory;
        _logger = logger;
    }

    public async Task<HeaderValidationResult> ValidateAsync(
        BasicDeliverEventArgs args,
        IChannel publishChannel,
        Func<BasicDeliverEventArgs, Dictionary<string, object>> copyInboundHeaders,
        CancellationToken ct)
    {
        // Rule 1: missing type-name header → _retryHandler.HandleTerminalFailureAsync, return Reject
        // Rule 2: oversized body → _retryHandler.HandleTerminalFailureAsync, return Reject
        // Rule 3: too many headers → _retryHandler.HandleTerminalFailureAsync, return Reject
        // Rule 4: oversized individual header → _retryHandler.HandleTerminalFailureAsync, return Reject
        //
        // Source locations:
        //   Rules 1-3 live inline in EventAsync at lines ~307-348 (three sequential if blocks).
        //   Rule 4 is the body of TryRejectOversizedHeaderAsync at lines 482-521.
        //   All four route through MessageRetryHandler.HandleTerminalFailureAsync today; the
        //   "audit publish + drop metric" framing in notes.md was anticipatory — the audit
        //   publish lives inside InboundMessageProcessor, not the host. Do NOT add a separate
        //   audit-publish call here; just preserve the existing terminal-failure routing.
    }
}
```

The `Func<CancellationToken> shutdownPublishTokenFactory` lets the validator obtain the host's shutdown-publish token without the validator needing to know about the host's shutdown internals. The host injects `() => GetShutdownPublishToken()`.

- [ ] **Step 4: Move the validation logic from host to validator**

Cut from `RabbitMqConsumerHost`:
- The three `if`-block rejections inside the `try` of `EventAsync` at lines ~307-348 (missing type name, oversized body, too many headers).
- The `TryRejectOversizedHeaderAsync` private method (lines 482-521) and its call site at line ~352.
- The `HasNonNullValue` local function nested inside `EventAsync` (lines ~304-305).

Paste into `RabbitMqHeaderValidator.ValidateAsync` adjusted to use the injected dependencies. The `copyInboundHeaders` parameter is a delegate that returns a copy of the inbound headers in the same shape the host uses today (the host keeps owning the `CopyInboundHeaders` private method since dispatch also needs it).

- [ ] **Step 5: Wire validator into the host's `EventAsync`**

```csharp
var validation = await _validator.ValidateAsync(args, publishChannel!, CopyInboundHeaders, cancellationToken).ConfigureAwait(false);
if (!validation.Accepted)
{
    processed = true;  // rejection paths consume the delivery; ack-without-redeliver
    return;
}
```

- [ ] **Step 6: Run tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-restore
```

### Task C.5: Migrate `RabbitMqConsumerHostHeaderSizeTests` → `RabbitMqHeaderValidatorTests`

**Files:**
- Delete: [src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostHeaderSizeTests.cs](../../../src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostHeaderSizeTests.cs)
- Modify: [src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqHeaderValidatorTests.cs](../../../src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqHeaderValidatorTests.cs)

- [ ] **Step 1: Classify each `[Fact]` in `RabbitMqConsumerHostHeaderSizeTests` as pure-validator or wiring**

Most should be pure-validator. Wiring tests (host correctly *invokes* the validator) stay in `RabbitMqConsumerHostTests`.

- [ ] **Step 2: Migrate pure-validator tests verbatim** (same assertions, new `Arrange` / `Act` against `RabbitMqHeaderValidator`).

- [ ] **Step 3: Delete the old file once all migrations are complete**

- [ ] **Step 4: Run the full unit-test suite**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-restore
```

### Task C.6: Extract `RabbitMqDispatchPipeline` skeleton with TDD

**Files:**
- Create: [src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqDispatchPipeline.cs](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqDispatchPipeline.cs)
- Create: [src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqDispatchPipelineTests.cs](../../../src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqDispatchPipelineTests.cs)

- [ ] **Step 1: Write failing tests for the dispatch + ack/nack matrix**

Cover:

- Successful dispatch → ack (`BasicAckAsync` called once with the right delivery tag, no requeue).
- Handler exception → nack-with-requeue OR retry-handler invocation (mirror existing host behaviour at [RabbitMqConsumerHost.cs:367-405](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L367-L405)).
- Channel closed concurrently during dispatch → graceful log, no exception escapes.
- Shutdown grace window expired → leave unacked (existing `_shutdownTimedOut` semantics).
- Consume-duration metric and consumed-message counter emit with correct tags + outcome (`success` / `error` / `retry`).

- [ ] **Step 2: Run tests, confirm compile failure**

- [ ] **Step 3: Create `RabbitMqDispatchPipeline` with the target surface**

```csharp
internal sealed class RabbitMqDispatchPipeline
{
    private readonly string _consumerQueueName;
    private readonly Func<bool> _shutdownTimedOutQuery;
    private readonly ILogger _logger;
    // ... ctor with these injected

    public async Task<bool> DispatchAsync(
        IInboundMessageProcessor processor,
        IChannel? model,
        IChannel publishChannel,
        BasicDeliverEventArgs args,
        CancellationToken cancellationToken)
    {
        // Wraps processor.ProcessAsync with the consume-duration metric.
        // Returns 'processed' boolean used by the host's ack/nack decision.
        // Move from RabbitMqConsumerHost.ProcessWithMetricsAsync.
    }

    public async Task AckOrNackAsync(
        IChannel? model,
        BasicDeliverEventArgs args,
        bool processed,
        bool callbackAdmitted,
        CancellationToken cancellationToken)
    {
        // The full ack/nack finally block from EventAsync (lines ~376-447), minus
        // the in-flight Decrement (which lives on the gate via Release()).
    }
}
```

- [ ] **Step 4: Move `ProcessWithMetricsAsync` and the ack/nack finally body from host to dispatch**

`ProcessWithMetricsAsync` already extracted from `EventAsync`; move the whole method into the dispatch class. The ack/nack `finally` block at [RabbitMqConsumerHost.cs:376-447](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L376-L447) becomes `AckOrNackAsync`. Preserve the per-exception-type log levels exactly (`AlreadyClosedException`, `ObjectDisposedException`, generic `Exception` → `LogAckOrNackFailure`).

- [ ] **Step 5: Wire dispatch into host's `EventAsync`**

```csharp
private async Task EventAsync(object _, BasicDeliverEventArgs args, CancellationToken cancellationToken)
{
    var model = _model;
    var publishChannel = _publishChannel;
    bool processed = false;
    bool callbackAdmitted = false;

    if (!_admissionGate.TryAdmit()) return;
    callbackAdmitted = true;

    try
    {
        var validation = await _validator.ValidateAsync(args, publishChannel!, CopyInboundHeaders, cancellationToken).ConfigureAwait(false);
        if (!validation.Accepted)
        {
            processed = true;
            return;
        }

        var processor = _messageProcessor;
        if (processor == null)
        {
            _logger.LogWarning("Message processor not initialised — message {DeliveryTag} will be nacked for redelivery", args.DeliveryTag);
            return;
        }

        processed = await _dispatch.DispatchAsync(processor, model, publishChannel!, args, cancellationToken).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error processing message");
    }
    finally
    {
        try
        {
            await _dispatch.AckOrNackAsync(model, args, processed, callbackAdmitted, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (callbackAdmitted) _admissionGate.Release();
        }
    }
}
```

- [ ] **Step 6: Run tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-restore
```

### Task C.7: Migrate `RabbitMqConsumerHostAckNackTests` → `RabbitMqDispatchPipelineTests`

**Files:**
- Delete: [src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostAckNackTests.cs](../../../src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostAckNackTests.cs)
- Modify: [src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqDispatchPipelineTests.cs](../../../src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqDispatchPipelineTests.cs)

- [ ] **Step 1: Migrate each `[Fact]` from `RabbitMqConsumerHostAckNackTests`**

Same migration rule as Tasks C.3 and C.5 — assertions verbatim, `Arrange` / `Act` retargeted to the dispatch class.

- [ ] **Step 2: Delete the old file**

- [ ] **Step 3: Run tests**

### Task C.8: Final cleanup, integration-test parity check, commit Item 3

**Files:**
- Modify: [src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs) — final shape audit
- Modify: [src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostTests.cs](../../../src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostTests.cs) — drop logic-level tests that moved out
- Modify: `src/ServiceConnect.UnitTests/RabbitMqConsumerHostTests.cs` (top-level, if exists per the grep listing) — same

- [ ] **Step 1: Confirm host file size**

```bash
wc -l src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs
```

Expected: ~300 lines (target). If significantly larger, audit for logic that should have moved out.

- [ ] **Step 2: Confirm no MA0051 warning on the host**

```bash
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj --no-restore /p:TreatWarningsAsErrors=false 2>&1 | grep MA0051 || echo "no MA0051 warnings"
```

Expected: `no MA0051 warnings`.

- [ ] **Step 3: Drop migrated tests from the big consolidated `RabbitMqConsumerHostTests.cs`**

Read `RabbitMqConsumerHostTests.cs` and identify any tests that became redundant after C.3 / C.5 / C.7 migrations. Delete them. Keep wiring/integration tests:

- Consumer-cancel scenarios.
- Recovery scenarios.
- Full ack-after-handler scenarios.
- `PrepareAsync` / `BeginConsumingAsync` / `ConsumeMessageTypeAsync` lifecycle scenarios.

- [ ] **Step 4: Run the unit-test project**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-restore
```

Expected: all green.

- [ ] **Step 5: Run the integration-test project (Testcontainers RabbitMQ)**

```bash
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --no-restore
```

Expected: all green. This is the canary for AMQP frame-sequence regressions.

- [ ] **Step 6: Manual metric tag-set diff**

Capture metric records from a representative integration scenario before and after the refactor (e.g., publish 100 messages, fail 10, confirm ack/nack outcomes). Diff the tag sets emitted on:

- `messaging.publish.duration`
- `messaging.process.duration`
- `messaging.client.published.messages`
- `messaging.client.consumed.messages`
- `messaging.serviceconnect.in_flight`

Expected: only the **retry-attempts** counter shows tag changes (carries Item 2's new scheme); all other counters/histograms have identical tag sets pre and post.

- [ ] **Step 7: Commit Item 3**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqAdmissionGate.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqHeaderValidator.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqDispatchPipeline.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/

git commit -m "$(cat <<'EOF'
refactor(transport): split RabbitMqConsumerHost into three collaborators

The host's 949-line EventAsync method braided together six concerns
(admission, in-flight bookkeeping, header validation, dispatch, ack/nack,
shutdown coordination) coordinated via four shared mutable fields and a
shared lock. Extract three internal-sealed collaborators so each concern
owns its state and contract:

- RabbitMqAdmissionGate: in-flight counter + shutdown gate + drain.
- RabbitMqHeaderValidator: pre-dispatch header rules + reject-with-audit.
- RabbitMqDispatchPipeline: processor invocation + ack/nack + duration metric.

The host shrinks to ~300 lines orchestrating the three collaborators.
Behaviour-preserving: identical AMQP frame sequence, identical metric tags
on every counter/histogram, identical shutdown semantics. Logic-level
tests migrate to per-collaborator test classes; the host's test file
shrinks to wiring/integration coverage.
EOF
)"
```

---

## Verification gate (whole PR)

- [ ] **Run unit tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-restore
```

- [ ] **Run RabbitMQ integration tests**

```bash
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --no-restore
```

- [ ] **Build the whole solution to confirm no analyzer regressions**

```bash
dotnet build ServiceConnect.sln --no-restore /p:TreatWarningsAsErrors=true
```

(If the project doesn't have `TreatWarningsAsErrors`, drop that flag and read the warnings output for any new entries vs. baseline.)

- [ ] **Confirm three commits, ordered Item 2 → Item 1 → Item 3**

```bash
git log --oneline master..HEAD
```

- [ ] **Update `notes.md` to remove the three resolved follow-ups**

Open `notes.md` and delete the three bullet items under "Follow-ups surfaced during Group B (metrics rollout)" that this PR resolves. Stage and amend into commit 3 (Item 3) or land as a fourth doc-cleanup commit — implementer's call.
