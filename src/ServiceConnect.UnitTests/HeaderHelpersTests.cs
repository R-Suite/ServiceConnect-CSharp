using ServiceConnect.Client.RabbitMQ;
using Xunit;

namespace ServiceConnect.UnitTests;

public class HeaderHelpersTests
{
    [Fact]
    public void SetHeader_WritesValue_WhenNonNull()
    {
        var headers = new Dictionary<string, object>();

        HeaderHelpers.SetHeader(headers, "X", "value");

        Assert.Equal("value", headers["X"]);
    }

    [Fact]
    public void SetHeader_OverwritesExistingKey_WhenNonNull()
    {
        var headers = new Dictionary<string, object> { ["X"] = "old" };

        HeaderHelpers.SetHeader(headers, "X", "new");

        Assert.Equal("new", headers["X"]);
    }

    [Fact]
    public void SetHeader_RemovesExistingKey_WhenNull()
    {
        var headers = new Dictionary<string, object> { ["X"] = "value" };

        HeaderHelpers.SetHeader<string?>(headers, "X", null);

        Assert.False(headers.ContainsKey("X"));
    }

    [Fact]
    public void SetHeader_WithNullOnMissingKey_NoOp()
    {
        var headers = new Dictionary<string, object>();

        HeaderHelpers.SetHeader<string?>(headers, "X", null);

        Assert.False(headers.ContainsKey("X"));
    }

    [Fact]
    public void ToNullableHeaders_PreservesAllEntriesAsNullableValues()
    {
        var source = new Dictionary<string, object>
        {
            ["A"] = "alpha",
            ["B"] = 42,
        };

        var result = HeaderHelpers.ToNullableHeaders(source);

        Assert.Equal(2, result.Count);
        Assert.Equal("alpha", result["A"]);
        Assert.Equal(42, result["B"]);
    }

    [Fact]
    public void GetErrorMessage_WalksInnerExceptionChain()
    {
        var inner = new InvalidOperationException("inner-msg");
        var middle = new ApplicationException("middle-msg", inner);
        var outer = new Exception("outer-msg", middle);

        var text = HeaderHelpers.GetErrorMessage(outer);

        Assert.Contains("outer-msg", text);
        Assert.Contains("middle-msg", text);
        Assert.Contains("inner-msg", text);
    }
}
