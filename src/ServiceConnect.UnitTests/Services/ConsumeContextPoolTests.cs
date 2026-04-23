using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class ConsumeContextPoolTests
{
    [Fact]
    public void EnsureActive_RejectsAccessAfterReleaseAndReuse()
    {
        // Audit claim: after Release → Rent → Initialize, a stale IConsumeContext reference
        // from the prior rent still passes EnsureActive() because _activeToken lives on the
        // pooled instance, which gets its token bumped back to match the new _rentToken.
        //
        // Expected behaviour: the stale reference's guard check must throw
        // (ObjectDisposedException or InvalidOperationException) so the caller can never
        // read headers/bus belonging to the NEXT message.

        var pool = new ConsumeContextPool();
        var bus = new Mock<IBus>().Object;
        var queueConfig = new Mock<IQueueConfiguration>().Object;
        var busConfig = new Mock<IBusConfiguration>().Object;

        // Rent a context, initialize it with message A's data, capture the reference.
        var headersA = new Dictionary<string, object> { ["msg"] = "A" };
        var contextA = pool.Rent(bus, headersA, queueConfig, busConfig, null, CancellationToken.None);

        // Caller finishes with A; pool reclaims.
        contextA.Release();

        // A new message arrives. Pool hands out the same underlying instance.
        var headersB = new Dictionary<string, object> { ["msg"] = "B" };
        var contextB = pool.Rent(bus, headersB, queueConfig, busConfig, null, CancellationToken.None);

        // Keep contextB alive so the compiler doesn't optimize away the second Rent.
        _ = contextB;

        // The stale reference contextA must NOT be allowed to read headers — otherwise it
        // leaks message B's headers to a caller that thinks it's still looking at A.
        Assert.Throws<InvalidOperationException>(() => _ = contextA.Headers);
    }

    [Fact]
    public void Rent_InitializesAndAllowsAccessBeforeRelease()
    {
        // Verify that a freshly rented context exposes its initialisation data without
        // throwing — i.e. EnsureActive does not fire for the original holder.

        var pool = new ConsumeContextPool();
        var bus = new Mock<IBus>().Object;
        var queueConfig = new Mock<IQueueConfiguration>().Object;
        var busConfig = new Mock<IBusConfiguration>().Object;

        var headers = new Dictionary<string, object>
        {
            ["key"] = "value",
            [HeaderKeys.MessageId] = "msg-1"
        };
        var context = pool.Rent(bus, headers, queueConfig, busConfig, null, CancellationToken.None);

        // All EnsureActive-gated properties must be readable before Release.
        var ex = Record.Exception(() =>
        {
            _ = context.Headers;
            _ = context.Bus;
            _ = context.CancellationToken;
            _ = context.MessageId;
            _ = context.CorrelationId;
        });
        Assert.Null(ex);

        // Headers should reflect the initialised data.
        Assert.True(context.Headers.ContainsKey("key"));
        Assert.Equal("value", context.Headers["key"]);

        // After release the context must be invalidated.
        context.Release();
        Assert.Throws<InvalidOperationException>(() => _ = context.Headers);
    }
}
