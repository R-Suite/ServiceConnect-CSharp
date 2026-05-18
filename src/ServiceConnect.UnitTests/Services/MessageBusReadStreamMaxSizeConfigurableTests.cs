using ServiceConnect.Configuration;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

/// <summary>
/// Verifies that the per-stream byte cap previously hard-coded as
/// <c>MessageBusReadStream.MaxTotalStreamSize = 100 * 1024 * 1024</c> is now sourced
/// from <see cref="ServiceConnect.Interfaces.Configuration.IBusConfiguration.MaxStreamSizeBytes"/>
/// (default 100 MB) and threaded into each <see cref="MessageBusReadStream"/> at construction.
/// The cap check is <c>newTotal &gt; _maxTotalStreamSize</c>, so a configured value of
/// <c>1024</c> bytes admits exactly the cap and rejects the first byte that would exceed it.
/// </summary>
public class MessageBusReadStreamMaxSizeConfigurableTests
{
    [Fact]
    public void BusConfiguration_MaxStreamSizeBytes_DefaultsTo100MB()
    {
        var config = new BusConfiguration();

        Assert.Equal(100L * 1024 * 1024, config.MaxStreamSizeBytes);
    }

    [Fact]
    public void MessageBusReadStream_DefaultCtor_PreservesHistoricalCap()
    {
        // The defaulted ctor parameter exists so existing direct-construction callers
        // (legacy unit tests) keep their historical 100 MB ceiling without modification.
        // A 1 KiB packet still well below the default cap must commit cleanly.
        var stream = new MessageBusReadStream("seq-default");

        var ex = Record.Exception(() => stream.Write(new byte[1024], 0));

        Assert.Null(ex);
    }

    [Fact]
    public void MessageBusReadStream_CustomCap_AdmitsExactlyTheCapAndRejectsTheNextByte()
    {
        // Configured cap of 1024 bytes: a single 1024-byte packet exactly hits the cap
        // and is admitted (newTotal > cap is false at equality). A subsequent 1-byte
        // packet pushes newTotal to 1025 and must throw InvalidOperationException.
        var stream = new MessageBusReadStream("seq-custom", maxTotalStreamSize: 1024);

        stream.Write(new byte[1024], 0);

        var ex = Assert.Throws<InvalidOperationException>(() => stream.Write(new byte[1], 1));
        Assert.Contains("1,024 bytes", ex.Message);
    }

    [Fact]
    public void MessageBusReadStream_CustomCap_RollsBackReservedBytesOnRejection()
    {
        // A rejected Write must roll the cumulative byte count back so that a subsequent
        // smaller packet for the same sequence still has the unused headroom available.
        // Without rollback the inflated total would poison every future Write on this
        // sequence, even ones that would individually fit under the cap.
        var stream = new MessageBusReadStream("seq-rollback", maxTotalStreamSize: 1024);

        stream.Write(new byte[512], 0);
        Assert.Throws<InvalidOperationException>(() => stream.Write(new byte[1024], 1));

        // After rollback the reserved-byte count is back at 512, so a 512-byte top-up fits.
        var ex = Record.Exception(() => stream.Write(new byte[512], 2));
        Assert.Null(ex);
    }
}
