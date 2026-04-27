using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Concurrency exercises for <see cref="RequestReplyManager"/>. The correlation
/// dictionary, per-request <c>RequestState</c>, and timeout/reply race are all
/// shared mutable state on the request-reply hot path; bugs only show up when
/// many requests and replies overlap in flight.
/// </summary>
public class RequestReplyManagerConcurrencyTests
{
    [Fact]
    public async Task ParallelSendRequest_EachReceivesItsOwnReply()
    {
        // N concurrent requests must each be correlated back to exactly the reply
        // delivered for that request — no cross-correlation, no missed completion.
        const int requestCount = 128;

        var serializer = new Mock<IMessageSerializer>();
        var pipeline = new Mock<ISendMessagePipeline>();

        // Map captured request id -> caller's username so the per-request reply we
        // dispatch later carries the right correlation back to the caller. Driving
        // correlation through this map keeps the deserializer mock dependency-free.
        var usernameByRequestId = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        // Deserialize is keyed off the bytes we hand into ProcessReply — encode the
        // request id directly as a UTF-8 string so the mock can recover it.
        RequestReplyManager? manager = null;

        serializer.Setup(s => s.Serialize(It.IsAny<FakeMessage1>()))
            .Returns([0]);
        serializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(new InvocationFunc(invocation =>
            {
                var data = (ReadOnlyMemory<byte>)invocation.Arguments[0];
                var requestId = System.Text.Encoding.UTF8.GetString(data.Span);
                var username = usernameByRequestId.TryGetValue(requestId, out var u) ? u : "unknown";
                return new FakeMessage1(Guid.NewGuid()) { Username = $"reply-for-{username}" };
            }));

        pipeline.Setup(p => p.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, ct) =>
            {
                var requestId = ctx.Headers["RequestMessageId"];
                var bytes = System.Text.Encoding.UTF8.GetBytes(requestId);
                var msg = (FakeMessage1)ctx.Message;
                usernameByRequestId[requestId] = msg.Username;
                // Schedule reply on a background thread to drive the real reply/await race.
                _ = Task.Run(() => manager!.ProcessReply(requestId, bytes, typeof(FakeMessage1)));
                _ = ct;
            })
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(serializer.Object, pipeline.Object);

        var tasks = Enumerable.Range(0, requestCount).Select(i => Task.Run(async () =>
        {
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            var request = new FakeMessage1(Guid.NewGuid()) { Username = $"caller-{i}" };
            var reply = await manager.SendRequestAsync<FakeMessage1, FakeMessage1>(
                request, headers, new RequestOptions { Timeout = 10_000 });
            return (i, reply.Username);
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(requestCount, results.Length);
        foreach (var (i, replyUsername) in results)
        {
            Assert.Equal($"reply-for-caller-{i}", replyUsername);
        }
    }

    [Fact]
    public async Task ConcurrentRepliesToSameRequest_OnlyFirstSatisfiesSingleAwait()
    {
        // ProcessReply may run from many channel threads at once. For a single-reply
        // request, the second reply MUST be ignored (returns false from TryProcessReply).
        var serializer = new Mock<IMessageSerializer>();
        var pipeline = new Mock<ISendMessagePipeline>();

        var requestId = (string?)null;
        RequestReplyManager? manager = null;

        serializer.Setup(s => s.Serialize(It.IsAny<FakeMessage1>())).Returns([1]);
        serializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(new FakeMessage1(Guid.NewGuid()) { Username = "winner" });

        pipeline.Setup(p => p.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) => requestId = ctx.Headers["RequestMessageId"])
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(serializer.Object, pipeline.Object);

        var requestTask = manager.SendRequestAsync<FakeMessage1, FakeMessage1>(
            new FakeMessage1(Guid.NewGuid()) { Username = "x" },
            new Dictionary<string, string>(StringComparer.Ordinal),
            new RequestOptions { Timeout = 10_000 });

        // Spin until the manager has registered the request and recorded the id.
        var spinDeadline = DateTime.UtcNow.AddSeconds(5);
        while (requestId is null && DateTime.UtcNow < spinDeadline)
        {
            await Task.Yield();
        }
        Assert.NotNull(requestId);

        const int replyAttempts = 32;
        var accepted = 0;
        var workers = Enumerable.Range(0, replyAttempts).Select(_ => Task.Run(() =>
        {
            if (manager.TryProcessReply(requestId!, new byte[] { 1 }, typeof(FakeMessage1)))
            {
                Interlocked.Increment(ref accepted);
            }
        })).ToArray();

        await Task.WhenAll(workers);
        var resolved = await requestTask;

        Assert.Equal("winner", resolved.Username);
        Assert.Equal(1, accepted);
    }

    [Fact]
    public async Task ConcurrentReplyAndTimeout_RequestCompletesExactlyOnce()
    {
        // The timeout callback and a late reply may both fire essentially simultaneously.
        // The TaskCompletionSource must complete exactly once; whichever path wins,
        // no exception escapes from the loser's TrySet call.
        const int iterations = 50;

        for (var iter = 0; iter < iterations; iter++)
        {
            var serializer = new Mock<IMessageSerializer>();
            var pipeline = new Mock<ISendMessagePipeline>();
            string? requestId = null;
            RequestReplyManager? manager = null;

            serializer.Setup(s => s.Serialize(It.IsAny<FakeMessage1>())).Returns([1]);
            serializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
                .Returns(new FakeMessage1(Guid.NewGuid()) { Username = "late-reply" });

            pipeline.Setup(p => p.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
                .Callback<SendContext, CancellationToken>((ctx, _) => requestId = ctx.Headers["RequestMessageId"])
                .Returns(Task.CompletedTask);

            manager = new RequestReplyManager(serializer.Object, pipeline.Object);

            var requestTask = manager.SendRequestAsync<FakeMessage1, FakeMessage1>(
                new FakeMessage1(Guid.NewGuid()) { Username = "x" },
                new Dictionary<string, string>(StringComparer.Ordinal),
                new RequestOptions { Timeout = 25 });

            // Wait until headers are populated, then race a reply against the timer.
            var spinDeadline = DateTime.UtcNow.AddSeconds(2);
            while (requestId is null && DateTime.UtcNow < spinDeadline)
            {
                await Task.Yield();
            }
            Assert.NotNull(requestId);

            // Fire the reply on a background thread at roughly the same instant the
            // timeout would fire — this is the actual race we want to exercise.
            var replyTask = Task.Run(() => manager.TryProcessReply(requestId!, new byte[] { 1 }, typeof(FakeMessage1)));

            // Either path is acceptable: a successful reply OR a RequestTimeoutException.
            // What is NOT acceptable: an unexpected exception or hang.
            try
            {
                var reply = await requestTask;
                Assert.Equal("late-reply", reply.Username);
            }
            catch (RequestTimeoutException)
            {
                // Timeout won the race — also valid.
            }

            await replyTask;
        }
    }

    [Fact]
    public async Task ParallelPublishRequest_AllRepliesAccountedFor()
    {
        // PublishRequestAsync with ExpectedReplyCount must fire onReply exactly
        // expected-count times when that many replies arrive concurrently, and
        // complete the awaiting task afterwards.
        const int callerCount = 8;
        const int repliesPerCaller = 16;

        var serializer = new Mock<IMessageSerializer>();
        var pipeline = new Mock<ISendMessagePipeline>();
        RequestReplyManager? manager = null;

        var idsByCaller = new ConcurrentDictionary<int, string>();
        var nextCallerSeed = 0;

        serializer.Setup(s => s.Serialize(It.IsAny<FakeMessage1>()))
            .Returns([0]);
        serializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(new FakeMessage1(Guid.NewGuid()) { Username = "ok" });

        pipeline.Setup(p => p.ExecutePublishMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                var caller = (FakeMessage1)ctx.Message;
                var seed = int.Parse(caller.Username, System.Globalization.CultureInfo.InvariantCulture);
                idsByCaller[seed] = ctx.Headers["RequestMessageId"];
            })
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(serializer.Object, pipeline.Object);

        var callerTasks = Enumerable.Range(0, callerCount).Select(_ => Task.Run(async () =>
        {
            var seed = Interlocked.Increment(ref nextCallerSeed);
            var received = 0;
            var publishTask = manager.PublishRequestAsync<FakeMessage1, FakeMessage1>(
                new FakeMessage1(Guid.NewGuid()) { Username = seed.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                new Dictionary<string, string>(StringComparer.Ordinal),
                new RequestOptions { Timeout = 30_000, ExpectedReplyCount = repliesPerCaller },
                _ => Interlocked.Increment(ref received));

            // Wait until the manager has stored the request id, then spray replies
            // back from many threads at once.
            var spinDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!idsByCaller.ContainsKey(seed) && DateTime.UtcNow < spinDeadline)
            {
                await Task.Yield();
            }
            Assert.True(idsByCaller.TryGetValue(seed, out var requestId));

            var replyTasks = Enumerable.Range(0, repliesPerCaller).Select(_ => Task.Run(() =>
                manager.TryProcessReply(requestId!, new byte[] { 1 }, typeof(FakeMessage1)))).ToArray();
            await Task.WhenAll(replyTasks);

            await publishTask;
            return received;
        })).ToArray();

        var results = await Task.WhenAll(callerTasks);

        Assert.All(results, r => Assert.Equal(repliesPerCaller, r));
    }
}
