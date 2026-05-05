using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class StreamProcessorAdmissionCapTests
{
    private static StreamProcessor BuildProcessor(TimeProvider? timeProvider = null)
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var accessor = new ConsumeScopeAccessor();
        accessor.Push(provider);
        return new StreamProcessor(
            accessor,
            NullLogger<StreamProcessor>.Instance,
            new MessageTypeRegistry(),
            new StreamHandlerRegistry([], NullLogger<StreamHandlerRegistry>.Instance),
            Mock.Of<IMessageSerializer>(),
            timeProvider ?? TimeProvider.System);
    }

    [Fact]
    public async Task ConcurrentInserts_ExceedingCap_LeaveExactlyCapEntries()
    {
        // Saturate the admission cap with concurrent packets for distinct sequenceIds.
        // _activeStreams.Count must settle at MaxActiveStreams (1000) — no leaked rejections.
        // The Interlocked counter is the admission gate; GetOrAdd is only called after a
        // successful counter bump, so no rejected entry ever appears in the dictionary.
        var processor = BuildProcessor();

        const int admissions = 1500;  // > MaxActiveStreams (1000)
        var tasks = new List<Task>(admissions);
        for (var i = 0; i < admissions; i++)
        {
            var seqId = Guid.NewGuid().ToString();
            var headers = new Dictionary<string, object>
            {
                [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
                [HeaderKeys.SequenceId] = seqId,
                [HeaderKeys.PacketNumber] = "0",
            };
            var envelope = new Envelope { Headers = headers, Body = new byte[] { 0xAA } };
            tasks.Add(processor.ProcessAsync(
                messageBytes: new byte[] { 0xAA },
                messageType: typeof(byte[]),
                message: null,
                headers: headers,
                envelope: envelope,
                cancellationToken: CancellationToken.None));
        }
        await Task.WhenAll(tasks);

        // Each successful admission is the first packet of its stream; nothing completes,
        // nothing evicts. The dictionary must contain exactly MaxActiveStreams (1000)
        // entries — no rejected entries leak through.
        Assert.Equal(1000, processor.ActiveStreamCount);
    }

    [Fact]
    public async Task ConcurrentInserts_AtCap_NeverExceedCap()
    {
        // Verify the cap is a hard upper bound even under heavy concurrency — the
        // Interlocked gate must ensure the dictionary never exceeds MaxActiveStreams.
        var processor = BuildProcessor();

        const int admissions = 2000;  // 2× MaxActiveStreams
        var tasks = new List<Task>(admissions);
        for (var i = 0; i < admissions; i++)
        {
            var seqId = Guid.NewGuid().ToString();
            var headers = new Dictionary<string, object>
            {
                [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
                [HeaderKeys.SequenceId] = seqId,
                [HeaderKeys.PacketNumber] = "0",
            };
            var envelope = new Envelope { Headers = headers, Body = new byte[] { 0xBB } };
            tasks.Add(processor.ProcessAsync(
                messageBytes: new byte[] { 0xBB },
                messageType: typeof(byte[]),
                message: null,
                headers: headers,
                envelope: envelope,
                cancellationToken: CancellationToken.None));
        }
        await Task.WhenAll(tasks);

        Assert.True(processor.ActiveStreamCount <= 1000,
            $"Expected at most 1000 active streams, but found {processor.ActiveStreamCount}");
    }
}
