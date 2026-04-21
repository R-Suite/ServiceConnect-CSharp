using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.UnitTests;

public class RequestOptionsTests
{
    [Fact]
    public void Default_IsFreshInstance_MutationsDoNotLeakAcrossCallers()
    {
        // L2 regression: Default used to be a shared mutable singleton, so one caller
        // poisoning it (e.g., setting a custom Timeout) bled into every other caller
        // that relied on the default.
        var first = RequestOptions.Default;
        first.Timeout = 123;
        first.EndPoint = "leaked";

        var second = RequestOptions.Default;

        Assert.Equal(RequestOptions.DefaultTimeoutMs, second.Timeout);
        Assert.Null(second.EndPoint);
        Assert.NotSame(first, second);
    }
}
