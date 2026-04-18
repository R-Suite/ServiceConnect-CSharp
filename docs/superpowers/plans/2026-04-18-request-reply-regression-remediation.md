# Request-Reply Regression Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the request-reply regressions so `SendRequestMultiAsync` honors `RequestOptions.EndPoint`, `PublishRequestAsync` returns to true publish semantics with callback-per-reply behavior, and endpoint-targeting options are rejected for publish requests.

**Architecture:** Three sequential tasks. First, extend `IRequestReplyManager` with a narrow publish-request method and update `Bus.PublishRequestAsync` to delegate to it instead of buffering through `SendRequestMultiAsync`. Second, fix `SendRequestMultiAsync` target selection so a single `EndPoint` is honored. Third, add unit and end-to-end coverage for callback-per-reply publish behavior and explicit option validation.

**Tech Stack:** C# / .NET, xUnit, Moq, RabbitMQ E2E tests, GitNexus CLI.

**Spec:** [docs/superpowers/specs/2026-04-18-branch-regression-remediation-design.md](../specs/2026-04-18-branch-regression-remediation-design.md)

---

## File Inventory

**Production:**
- `src/ServiceConnect.Interfaces/IRequestReplyManager.cs` — add one publish-oriented method
- `src/ServiceConnect/Services/RequestReplyManager.cs` — implement publish-request tracking and fix single-endpoint routing in multi-request mode
- `src/ServiceConnect/Bus.cs` — validate publish-request options and delegate to the new manager method

**Unit tests:**
- `src/ServiceConnect.UnitTests/RequestReplyManagerTests.cs` — add single-endpoint multi-request and callback-per-reply publish coverage
- `src/ServiceConnect.UnitTests/BusTests.cs` — add publish-request delegation and argument validation coverage
- `src/ServiceConnect.UnitTests/InterfaceCleanupTests.cs` — keep the interface surface guard updated

**E2E tests:**
- `src/ServiceConnect.EndToEndTests/PublishRequestAsyncTests.cs` — rewrite to true publish fan-out semantics
- `src/ServiceConnect.EndToEndTests/RequestReplyE2ETests.cs` — keep as the direct send-request regression anchor

---

## Task 1: Extend `IRequestReplyManager` And Restore True Publish Semantics

**Files:**
- Modify: `src/ServiceConnect.Interfaces/IRequestReplyManager.cs`
- Modify: `src/ServiceConnect/Services/RequestReplyManager.cs`
- Modify: `src/ServiceConnect.UnitTests/InterfaceCleanupTests.cs`
- Modify: `src/ServiceConnect.UnitTests/RequestReplyManagerTests.cs`

- [ ] **Step 1: Add the publish-oriented method to `IRequestReplyManager`**

In `src/ServiceConnect.Interfaces/IRequestReplyManager.cs`, insert this method between `SendRequestMultiAsync` and `ProcessReply`:

```csharp
    Task PublishRequestAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        RequestOptions options,
        Action<TReply> onReply,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message;
```

- [ ] **Step 2: Update the interface cleanup test to include the new method**

In `src/ServiceConnect.UnitTests/InterfaceCleanupTests.cs`, replace the existing `IRequestReplyManager_MethodsDoNotExposeSendDelegate` body:

```csharp
        var sendRequest = typeof(IRequestReplyManager).GetMethod(nameof(IRequestReplyManager.SendRequestAsync))!;
        var sendRequestMulti = typeof(IRequestReplyManager).GetMethod(nameof(IRequestReplyManager.SendRequestMultiAsync))!;

        Assert.DoesNotContain(sendRequest.GetParameters(), parameter => parameter.ParameterType.Name.Contains("Func"));
        Assert.DoesNotContain(sendRequestMulti.GetParameters(), parameter => parameter.ParameterType.Name.Contains("Func"));
```

with:

```csharp
        var sendRequest = typeof(IRequestReplyManager).GetMethod(nameof(IRequestReplyManager.SendRequestAsync))!;
        var sendRequestMulti = typeof(IRequestReplyManager).GetMethod(nameof(IRequestReplyManager.SendRequestMultiAsync))!;
        var publishRequest = typeof(IRequestReplyManager).GetMethod(nameof(IRequestReplyManager.PublishRequestAsync))!;

        Assert.DoesNotContain(sendRequest.GetParameters(), parameter => parameter.ParameterType.Name.Contains("Func"));
        Assert.DoesNotContain(sendRequestMulti.GetParameters(), parameter => parameter.ParameterType.Name.Contains("Func"));
        Assert.DoesNotContain(publishRequest.GetParameters(), parameter => parameter.ParameterType.Name.Contains("Func"));
```

The new method uses `Action<TReply>`, so keep the test focused on forbidding `Func`-style send delegates rather than forbidding all delegates.

- [ ] **Step 3: Add the failing request-reply manager tests**

In `src/ServiceConnect.UnitTests/RequestReplyManagerTests.cs`, add these tests:

```csharp
        [Fact]
        public async Task SendRequestMultiAsync_SendsToEndPoint_WhenSingleEndPointSpecified()
        {
            var options = new RequestOptions { Timeout = 5000, EndPoint = "single-endpoint" };
            var headers = new Dictionary<string, string>();
            var messageBytes = new byte[] { 1, 2, 3 };

            _mockSendPipeline.Setup(p => p.ExecuteSendMessagePipelineAsync(
                    typeof(FakeMessage1),
                    messageBytes,
                    It.IsAny<Dictionary<string, string>>(),
                    "single-endpoint",
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);
            await manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(messageBytes, headers, options);

            _mockSendPipeline.Verify(p => p.ExecuteSendMessagePipelineAsync(
                typeof(FakeMessage1),
                messageBytes,
                It.IsAny<Dictionary<string, string>>(),
                "single-endpoint",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task PublishRequestAsync_UsesPublishPipeline_AndInvokesCallbackBeforeCompletion()
        {
            var reply = new FakeMessage1(Guid.NewGuid()) { Username = "published reply" };
            _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1))).Returns(reply);

            RequestReplyManager? manager = null;
            string? capturedMessageId = null;
            int callbackCount = 0;
            var callbackRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            _mockSendPipeline.Setup(p => p.ExecutePublishMessagePipelineAsync(
                    typeof(FakeMessage1),
                    It.IsAny<byte[]>(),
                    It.IsAny<Dictionary<string, string>>(),
                    null,
                    It.IsAny<CancellationToken>()))
                .Callback<Type, byte[], Dictionary<string, string>?, string?, CancellationToken>((_, bytes, hdrs, _, _) =>
                {
                    capturedMessageId = hdrs![HeaderKeys.RequestMessageId];
                    Task.Run(async () =>
                    {
                        await Task.Delay(10);
                        manager!.ProcessReply(capturedMessageId!, bytes, typeof(FakeMessage1));
                    });
                })
                .Returns(Task.CompletedTask);

            manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

            var task = manager.PublishRequestAsync<FakeMessage1, FakeMessage1>(
                new byte[] { 1, 2, 3 },
                new Dictionary<string, string>(),
                new RequestOptions { Timeout = 200, ExpectedReplyCount = 1 },
                replyMessage =>
                {
                    Assert.Equal("published reply", replyMessage.Username);
                    callbackCount++;
                    callbackRan.TrySetResult();
                });

            await callbackRan.Task;
            Assert.Equal(1, callbackCount);
            await task;
        }
```

- [ ] **Step 4: Run the request-reply manager tests and confirm they fail first**

Run:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RequestReplyManagerTests|FullyQualifiedName~InterfaceCleanupTests"
```

Expected before production changes:
- compile/test failure because `PublishRequestAsync` does not exist on `IRequestReplyManager` and `RequestReplyManager`
- `SendRequestMultiAsync_SendsToEndPoint_WhenSingleEndPointSpecified` fails because the current code sends to `null` when `EndPoints` is absent

- [ ] **Step 5: Implement the new manager method and fix single-endpoint routing**

In `src/ServiceConnect/Services/RequestReplyManager.cs`:

1. In `SendRequestMultiAsync`, replace the outbound target selection block:

```csharp
            if (options.EndPoints != null)
            {
                foreach (string endPoint in options.EndPoints)
                    await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(TRequest), messageBytes, headers, endPoint, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(TRequest), messageBytes, headers, null, cancellationToken).ConfigureAwait(false);
            }
```

with:

```csharp
            if (options.EndPoints != null)
            {
                foreach (string endPoint in options.EndPoints)
                    await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(TRequest), messageBytes, headers, endPoint, cancellationToken).ConfigureAwait(false);
            }
            else if (!string.IsNullOrEmpty(options.EndPoint))
            {
                await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(TRequest), messageBytes, headers, options.EndPoint, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(TRequest), messageBytes, headers, null, cancellationToken).ConfigureAwait(false);
            }
```

2. Add this method below `SendRequestMultiAsync`:

```csharp
    public async Task PublishRequestAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        RequestOptions options,
        Action<TReply> onReply,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message
    {
        cancellationToken.ThrowIfCancellationRequested();

        var messageId = Guid.NewGuid();
        int expectedCount = options.ExpectedReplyCount ?? -1;
        int receivedCount = 0;
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pendingRequests[messageId] = new RequestState(tcs, expectedCount, typeof(TReply), reply =>
        {
            onReply((TReply)reply);
            if (expectedCount > 0 && Interlocked.Increment(ref receivedCount) >= expectedCount)
                tcs.TrySetResult(null!);
        });

        headers[HeaderKeys.RequestMessageId] = messageId.ToString();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(options.Timeout);

        await using var reg = linkedCts.Token.Register(() =>
        {
            _pendingRequests.TryRemove(messageId, out _);
            if (cancellationToken.IsCancellationRequested)
                tcs.TrySetCanceled(cancellationToken);
            else
                tcs.TrySetResult(null!);
        });

        try
        {
            await _sendPipeline.ExecutePublishMessagePipelineAsync(typeof(TRequest), messageBytes, headers, null, cancellationToken).ConfigureAwait(false);
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingRequests.TryRemove(messageId, out _);
        }
    }
```

This is the minimal shape because it reuses the existing pending-request dictionary and `ProcessReply` path instead of inventing a second reply-routing mechanism.

- [ ] **Step 6: Re-run the request-reply manager/interface tests and confirm they pass**

Run:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RequestReplyManagerTests|FullyQualifiedName~InterfaceCleanupTests"
```

Expected: all request-reply manager tests and the interface cleanup test pass.

## Task 2: Make `Bus.PublishRequestAsync` Validate Options And Delegate To The New Manager Method

**Files:**
- Modify: `src/ServiceConnect/Bus.cs`
- Modify: `src/ServiceConnect.UnitTests/BusTests.cs`

- [ ] **Step 1: Add the failing bus tests**

In `src/ServiceConnect.UnitTests/BusTests.cs`, add these tests after the `SendAsync` coverage:

```csharp
        [Fact]
        public async Task PublishRequestAsync_DelegatesToRequestReplyManagerPublishMethod()
        {
            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
            _mockRequestReplyManager.Setup(r => r.PublishRequestAsync<FakeMessage1, FakeMessage1>(
                    It.IsAny<byte[]>(),
                    It.IsAny<Dictionary<string, string>>(),
                    It.IsAny<RequestOptions>(),
                    It.IsAny<Action<FakeMessage1>>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            await _bus.PublishRequestAsync<FakeMessage1, FakeMessage1>(message, _ => { });

            _mockRequestReplyManager.Verify(r => r.PublishRequestAsync<FakeMessage1, FakeMessage1>(
                It.IsAny<byte[]>(),
                It.IsAny<Dictionary<string, string>>(),
                It.IsAny<RequestOptions>(),
                It.IsAny<Action<FakeMessage1>>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task PublishRequestAsync_WithEndPoint_ThrowsArgumentException()
        {
            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };

            await Assert.ThrowsAsync<ArgumentException>(() =>
                _bus.PublishRequestAsync<FakeMessage1, FakeMessage1>(
                    message,
                    _ => { },
                    new RequestOptions { EndPoint = "target-queue" }));
        }

        [Fact]
        public async Task PublishRequestAsync_WithEndPoints_ThrowsArgumentException()
        {
            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };

            await Assert.ThrowsAsync<ArgumentException>(() =>
                _bus.PublishRequestAsync<FakeMessage1, FakeMessage1>(
                    message,
                    _ => { },
                    new RequestOptions { EndPoints = new List<string> { "q1", "q2" } }));
        }
```

- [ ] **Step 2: Run the bus tests and confirm they fail first**

Run:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~BusTests"
```

Expected before production changes:
- the delegation test fails because `Bus.PublishRequestAsync` still calls `SendRequestMultiAsync`
- the validation tests fail because endpoint-targeting options are currently accepted

- [ ] **Step 3: Update `Bus.PublishRequestAsync` to validate and delegate correctly**

In `src/ServiceConnect/Bus.cs`, replace the current `PublishRequestAsync` method:

```csharp
    public async Task PublishRequestAsync<TRequest, TReply>(TRequest message, Action<TReply> onReply, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where TRequest : Message where TReply : Message
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var replies = await SendRequestMultiAsync<TRequest, TReply>(message, options, cancellationToken).ConfigureAwait(false);

        foreach (var reply in replies)
        {
            onReply(reply);
        }
    }
```

with:

```csharp
    public async Task PublishRequestAsync<TRequest, TReply>(TRequest message, Action<TReply> onReply, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where TRequest : Message where TReply : Message
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var requestOptions = options ?? RequestOptions.Default;
        if (!string.IsNullOrEmpty(requestOptions.EndPoint) || (requestOptions.EndPoints?.Count > 0))
            throw new ArgumentException("PublishRequestAsync does not support EndPoint or EndPoints. Use SendRequestAsync or SendRequestMultiAsync instead.", nameof(options));

        var messageBytes = _serializer.Serialize(message);
        Dictionary<string, string> headers;

        if (_hasOutgoingFilters)
        {
            var envelope = CreateEnvelope(typeof(TRequest), messageBytes, requestOptions.Headers);
            if (await _filterPipeline.ExecuteOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Outgoing filters blocked the request message.");
            headers = ExtractHeaders(envelope);
        }
        else
        {
            headers = BuildHeadersDirect(typeof(TRequest), requestOptions.Headers);
        }

        await _requestReplyManager.PublishRequestAsync<TRequest, TReply>(
            messageBytes,
            headers,
            requestOptions,
            onReply,
            cancellationToken).ConfigureAwait(false);
    }
```

- [ ] **Step 4: Re-run the bus tests and confirm they pass**

Run:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~BusTests"
```

Expected: all `BusTests` pass.

## Task 3: Rewrite Publish-Request E2E Coverage To True Publish Fan-Out

**Files:**
- Modify: `src/ServiceConnect.EndToEndTests/PublishRequestAsyncTests.cs`
- Re-run: `src/ServiceConnect.EndToEndTests/RequestReplyE2ETests.cs`

- [ ] **Step 1: Rewrite the publish-request E2E test to true publish fan-out**

Replace the single test in `src/ServiceConnect.EndToEndTests/PublishRequestAsyncTests.cs` with the exact file body below:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(RequestReplyCollection))]
public class PublishRequestAsyncTests
{
    private readonly MessagingFixture _fixture;

    public PublishRequestAsyncTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task PublishRequestAsync_TwoResponders_InvokesTwoCallbacks_WhenExpectedReplyCountIsTwo()
    {
        var responderQueue1 = _fixture.GetUniqueQueueName("pubreq-responder-1");
        var responderQueue2 = _fixture.GetUniqueQueueName("pubreq-responder-2");
        var requesterQueue = _fixture.GetUniqueQueueName("pubreq-requester");

        var replies = new ConcurrentBag<TestResponse>();

        var responder1Services = new ServiceCollection();
        responder1Services.AddLogging();
        responder1Services.AddSingleton<IList<HandlerReference>>(
        [
            new HandlerReference
            {
                HandlerType = typeof(PubReqReplyHandler),
                MessageType = typeof(TestRequest)
            }
        ]);
        responder1Services.AddTransient<IMessageHandler<TestRequest>, PubReqReplyHandler>();
        responder1Services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 3);
                t.SetClientSetting("RetrySeconds", 1);
            });
            builder.ConfigureQueues(q => q.QueueName = responderQueue1);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var responder2Services = new ServiceCollection();
        responder2Services.AddLogging();
        responder2Services.AddSingleton<IList<HandlerReference>>(
        [
            new HandlerReference
            {
                HandlerType = typeof(PubReqReplyHandler),
                MessageType = typeof(TestRequest)
            }
        ]);
        responder2Services.AddTransient<IMessageHandler<TestRequest>, PubReqReplyHandler>();
        responder2Services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 3);
                t.SetClientSetting("RetrySeconds", 1);
            });
            builder.ConfigureQueues(q => q.QueueName = responderQueue2);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var requesterServices = new ServiceCollection();
        requesterServices.AddLogging();
        requesterServices.AddSingleton<IList<HandlerReference>>(new List<HandlerReference>());
        requesterServices.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 3);
                t.SetClientSetting("RetrySeconds", 1);
            });
            builder.ConfigureQueues(q => q.QueueName = requesterQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var responder1Provider = responder1Services.BuildServiceProvider();
        var responder2Provider = responder2Services.BuildServiceProvider();
        var requesterProvider = requesterServices.BuildServiceProvider();

        var responder1Bus = responder1Provider.GetRequiredService<IBus>();
        var responder2Bus = responder2Provider.GetRequiredService<IBus>();
        var requesterBus = requesterProvider.GetRequiredService<IBus>();

        await responder1Bus.StartConsumingAsync();
        await responder2Bus.StartConsumingAsync();
        await requesterBus.StartConsumingAsync();

        try
        {
            var request = new TestRequest(Guid.NewGuid()) { Question = "publish-request-question" };

            await requesterBus.PublishRequestAsync<TestRequest, TestResponse>(
                request,
                reply => replies.Add(reply),
                new RequestOptions
                {
                    Timeout = 30000,
                    ExpectedReplyCount = 2
                });

            Assert.Equal(2, replies.Count);
            Assert.All(replies, reply => Assert.Equal("publish-request-question", reply.Answer));
        }
        finally
        {
            await responder1Bus.DisposeAsync();
            if (responder1Provider is IAsyncDisposable asyncResponder1Provider) await asyncResponder1Provider.DisposeAsync();
            await responder2Bus.DisposeAsync();
            if (responder2Provider is IAsyncDisposable asyncResponder2Provider) await asyncResponder2Provider.DisposeAsync();
            await requesterBus.DisposeAsync();
            if (requesterProvider is IAsyncDisposable asyncRequesterProvider) await asyncRequesterProvider.DisposeAsync();
        }
    }
}

file class PubReqReplyHandler : IMessageHandler<TestRequest>
{
    public IConsumeContext? Context { get; set; }

    public async Task HandleAsync(TestRequest message)
    {
        await Context!.ReplyAsync(new TestResponse(Guid.NewGuid())
        {
            Answer = message.Question
        });
    }
}
```

- [ ] **Step 2: Add the publish-request blocked-by-filter unit test**

In `src/ServiceConnect.UnitTests/BusTests.cs`, add this test below the publish-request validation tests:

```csharp
        [Fact]
        public async Task PublishRequestAsync_WhenFilterBlocksMessage_ThrowsInvalidOperationException()
        {
            _mockFilterPipeline.Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            var pipelineConfigWithFilter = new Mock<IPipelineConfiguration>();
            pipelineConfigWithFilter.Setup(x => x.OutgoingFilters).Returns(new List<Type> { typeof(object) });

            var busWithFilters = new Bus(
                _mockSerializer.Object,
                _mockFilterPipeline.Object,
                _mockSendPipeline.Object,
                _mockRequestReplyManager.Object,
                _mockLogger.Object,
                _mockQueueConfig.Object,
                _mockDispatcher.Object,
                _handlerReferences,
                pipelineConfigWithFilter.Object);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                busWithFilters.PublishRequestAsync<FakeMessage1, FakeMessage1>(
                    new FakeMessage1(Guid.NewGuid()),
                    _ => { }));
        }
```

- [ ] **Step 3: Run the request-reply unit suite and the publish-request E2E tests**

Run:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RequestReplyManagerTests|FullyQualifiedName~BusTests|FullyQualifiedName~InterfaceCleanupTests"
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "FullyQualifiedName~PublishRequestAsyncTests|FullyQualifiedName~RequestReplyE2ETests"
```

Expected:
- all request-reply unit tests pass
- publish-request E2E test proves true publish fan-out with two callbacks
- existing direct send-request E2E test still passes

- [ ] **Step 4: Run GitNexus impact verification before commit**

Run:

```bash
rtk gitnexus impact RequestReplyManager --include-tests
rtk gitnexus impact IRequestReplyManager --include-tests
```

Expected:
- changed scope is limited to request-reply manager, bus wrapper behavior, and request-reply-focused tests
