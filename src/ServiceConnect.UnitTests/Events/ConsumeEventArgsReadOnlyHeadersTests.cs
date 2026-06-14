using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Events;

public class ConsumeEventArgsReadOnlyHeadersTests
{
    [Fact]
    public void Headers_PropertyType_IsReadOnlyDictionary()
    {
        var prop = typeof(ConsumeEventArgs).GetProperty(nameof(ConsumeEventArgs.Headers));
        Assert.NotNull(prop);
        Assert.Equal(typeof(IReadOnlyDictionary<string, object>), prop!.PropertyType);
    }

    [Fact]
    public void Headers_ConstructsAndReadsBack()
    {
        var args = new ConsumeEventArgs
        {
            Headers = new Dictionary<string, object> { ["k"] = "v" }
        };
        Assert.Equal("v", args.Headers["k"]);
    }
}
