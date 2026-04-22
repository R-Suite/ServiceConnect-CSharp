using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.UnitTests;

public class RequestOptionsTests
{
    [Fact]
    public void Default_IsFreshInstance_MutationsDoNotLeakAcrossCallers()
    {
        // Each access to Default must return a fresh instance so one caller
        // mutating the returned options (e.g. setting Timeout) cannot leak into
        // another caller that relies on the out-of-the-box defaults.
        var first = RequestOptions.Default;
        first.Timeout = 123;
        first.EndPoint = "leaked";

        var second = RequestOptions.Default;

        Assert.Equal(RequestOptions.DefaultTimeoutMs, second.Timeout);
        Assert.Null(second.EndPoint);
        Assert.NotSame(first, second);
    }
}
