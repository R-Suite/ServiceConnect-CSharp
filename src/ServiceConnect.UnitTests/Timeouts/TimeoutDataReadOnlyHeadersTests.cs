using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Timeouts;

public class TimeoutDataReadOnlyHeadersTests
{
    [Fact]
    public void Headers_PropertyType_IsReadOnlyDictionary()
    {
        var prop = typeof(TimeoutData).GetProperty(nameof(TimeoutData.Headers));
        Assert.NotNull(prop);
        Assert.Equal(typeof(IReadOnlyDictionary<string, object>), prop!.PropertyType);
    }

    [Fact]
    public void Headers_ConstructsAndReadsBack()
    {
        var data = new TimeoutData
        {
            Headers = new Dictionary<string, object> { ["k"] = "v" }
        };
        Assert.Equal("v", data.Headers["k"]);
    }
}
