using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.UnitTests.Configuration;

public class RequestOptionsTests
{
    [Fact]
    public void Default_ReturnsInstanceWithDefaultTimeout()
    {
        // RequestOptions is a readonly record struct, so each access to Default returns
        // an independent copy by value. Mutations on one copy cannot affect another.
        var first = RequestOptions.Default;
        var second = RequestOptions.Default;

        Assert.Equal(RequestOptions.DefaultTimeoutMs, first.Timeout);
        Assert.Equal(RequestOptions.DefaultTimeoutMs, second.Timeout);
        Assert.Null(second.EndPoint);
    }
}
