using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class MessageBusReadStreamCasRaceTests
{
    [Fact]
    public void SetLastPacketNumber_PacketAlreadyExceedsValue_ThrowsImmediately()
    {
        // Sequential setup of the race: a packet for slot 5 lands while
        // _lastPacketNumber == -1 (no upper bound yet enforced by Write). The
        // subsequent SetLastPacketNumber(3) must reject — the stream's
        // declared total is below an already-stored slot.
        var stream = new MessageBusReadStream("test-seq");
        stream.Write([0xAA], packetNumber: 5);

        var ex = Assert.Throws<InvalidOperationException>(() => stream.SetLastPacketNumber(3));
        Assert.Contains("5", ex.Message);
    }

    [Fact]
    public async Task SetLastPacketNumber_ConcurrentWrite_AllOutOfRangeDetected()
    {
        // True concurrency. Each trial races a single Write(N+1) against
        // SetLastPacketNumber(N). One of three outcomes is acceptable:
        //   1. Write throws (rejected by the post-CAS upper bound in Write).
        //   2. SetLastPacketNumber throws (caught by pre- or post-CAS validation).
        //   3. Both throw.
        // The bug is: both succeed silently, leaving packet N+1 in _packets
        // with LastPacketNumber = N — a stream-state violation.
        const int trials = 200;
        var bothSucceededSilently = 0;
        for (var i = 0; i < trials; i++)
        {
            var stream = new MessageBusReadStream($"seq-{i}");
            const long N = 3;

            Exception? writeEx = null;
            Exception? setEx = null;
            var tWrite = Task.Run(() =>
            {
                try { stream.Write([0xBB], packetNumber: N + 1); }
                catch (Exception ex) { writeEx = ex; }
            });
            var tSet = Task.Run(() =>
            {
                try { stream.SetLastPacketNumber(N); }
                catch (Exception ex) { setEx = ex; }
            });
            await Task.WhenAll(tWrite, tSet);

            if (writeEx is null && setEx is null)
            {
                bothSucceededSilently++;
            }
        }

        Assert.Equal(0, bothSucceededSilently);
    }
}
